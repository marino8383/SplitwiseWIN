using Microsoft.Data.Sqlite;
using System.Globalization;

namespace SplitwiseUploader;

public enum ExpenseSource { MANUALE, CSV, STAMP, SATISPAY, BPER, BPERCARD, CAI }

// Direzione del movimento: spesa (Uscita) o incasso (Entrata). Solo le Uscite si inviano a Splitwise.
public enum ExpenseDirection { Uscita, Entrata }

// Ciclo di vita di una spesa nel DB.
public enum ExpenseStatus
{
    Pending,               // importata, non ancora inviata
    Inviata,               // inviata con successo a Splitwise
    Archiviata,            // messa da parte senza inviare (recuperabile)
    EliminataLogicamente   // era inviata, poi eliminata logicamente (non tocca Splitwise)
}

public class ExpenseRecord
{
    public long Id { get; set; }
    public DateTime? Date { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public ExpenseSource Source { get; set; }
    public ExpenseDirection Direction { get; set; } = ExpenseDirection.Uscita;
    public string ManualTags { get; set; } = "";   // tag assegnati a mano (CSV), oltre a quelli da regole
    public long ImportBatch { get; set; }            // id del registro di import (import_log) da cui proviene
    public bool DupIgnore { get; set; }              // true = escludi dal controllo duplicati locale
    public string Note { get; set; } = "";           // nota libera modificabile (non usata per il match)
    public bool ExcludeTotals { get; set; }          // true = non conteggiato in totali/statistiche (es. padre suddiviso)
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Pending;

    public long SplitwiseExpenseId { get; set; }      // 0 finché non inviata
    public DateTime? SentAtUtc { get; set; }           // valorizzata all'invio riuscito
    public DateTime CreatedAtUtc { get; set; }

    // helper di visualizzazione
    public bool IsSent => Status == ExpenseStatus.Inviata;
    public string SentDisplay => SentAtUtc.HasValue
        ? SentAtUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
        : "—";
}

/// <summary>
/// Storico locale su SQLite: tiene TUTTE le spese (pending, inviate, archiviate, eliminate).
/// Deduplica su DATA + IMPORTO. Gestione archivio e data invio.
/// </summary>
public class HistoryStore
{
    private readonly string _connStr;

    /// <summary>Percorso del file DB attualmente in uso (visibile nelle Opzioni).</summary>
    public string DbPath { get; }

    public HistoryStore(string? dbPath = null)
    {
        DbPath = string.IsNullOrWhiteSpace(dbPath) ? Path.Combine(AppContext.BaseDirectory, "history.db") : dbPath!;
        _connStr = $"Data Source={DbPath}";
        Init();
    }

    /// <summary>Azzera TUTTE le tabelle dati (movimenti, registri, regole, tag, meta). Schema invariato.</summary>
    public void ClearAllData()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var t in new[] { "expenses", "import_log", "tag_rules", "tags", "meta" })
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {t};";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS expenses (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DateTicks INTEGER NULL,
                Description TEXT NOT NULL,
                Amount TEXT NOT NULL,
                Source TEXT NOT NULL,
                Status TEXT NOT NULL,
                SplitwiseExpenseId INTEGER NOT NULL DEFAULT 0,
                SentAtUtcTicks INTEGER NULL,
                CreatedAtUtcTicks INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_dedup ON expenses(DateTicks, Amount);
            CREATE INDEX IF NOT EXISTS ix_status ON expenses(Status);
            CREATE TABLE IF NOT EXISTS tag_rules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Keyword TEXT NOT NULL,
                Tag TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tags (
                Name TEXT PRIMARY KEY
            );
            CREATE TABLE IF NOT EXISTS import_log (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WhenUtcTicks INTEGER NOT NULL,
                Origin TEXT NOT NULL,
                Imported INTEGER NOT NULL,
                Skipped INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meta (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS splitwise_rules (
                Phrase TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // migrazione: colonna Direction (Uscita/Entrata) per i DB già esistenti
        try
        {
            using var mig = c.CreateCommand();
            mig.CommandText = "ALTER TABLE expenses ADD COLUMN Direction TEXT NOT NULL DEFAULT 'Uscita';";
            mig.ExecuteNonQuery();
        }
        catch { /* colonna già presente */ }

        // migrazione: colonna ImportBatch (id del registro di import) per i DB già esistenti
        try
        {
            using var mig = c.CreateCommand();
            mig.CommandText = "ALTER TABLE expenses ADD COLUMN ImportBatch INTEGER NOT NULL DEFAULT 0;";
            mig.ExecuteNonQuery();
        }
        catch { /* colonna già presente */ }

        // migrazione: colonna ManualTags (tag assegnati a mano alla singola riga)
        try
        {
            using var mig = c.CreateCommand();
            mig.CommandText = "ALTER TABLE expenses ADD COLUMN ManualTags TEXT NOT NULL DEFAULT '';";
            mig.ExecuteNonQuery();
        }
        catch { /* colonna già presente */ }

        // migrazione: colonna DupIgnore (1 = ignora questa riga nel controllo duplicati locale)
        try
        {
            using var mig = c.CreateCommand();
            mig.CommandText = "ALTER TABLE expenses ADD COLUMN DupIgnore INTEGER NOT NULL DEFAULT 0;";
            mig.ExecuteNonQuery();
        }
        catch { /* colonna già presente */ }

        // migrazione: colonna Note (testo libero modificabile, NON usata per il match duplicati)
        try
        {
            using var mig = c.CreateCommand();
            mig.CommandText = "ALTER TABLE expenses ADD COLUMN Note TEXT NOT NULL DEFAULT '';";
            mig.ExecuteNonQuery();
        }
        catch { /* colonna già presente */ }

        // migrazione: colonna ExcludeTotals (1 = non contare nei totali/statistiche; es. padre di una suddivisione)
        try
        {
            using var mig = c.CreateCommand();
            mig.CommandText = "ALTER TABLE expenses ADD COLUMN ExcludeTotals INTEGER NOT NULL DEFAULT 0;";
            mig.ExecuteNonQuery();
        }
        catch { /* colonna già presente */ }
    }

    /// <summary>Marca/smarca un movimento come escluso dai totali e dalle statistiche.</summary>
    public void SetExcludeTotals(long id, bool exclude)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE expenses SET ExcludeTotals=$v WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$v", exclude ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Riclassifica come BPERCARD i movimenti BPER importati da un registro il cui nome file contiene "carta".
    /// Serve a recuperare i movimenti carta inseriti prima della distinzione di fonte. Ritorna quante righe.
    /// </summary>
    public int ReclassifyCardSourceByOrigin()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE expenses SET Source='BPERCARD'
            WHERE Source='BPER'
              AND ImportBatch IN (SELECT Id FROM import_log WHERE lower(Origin) LIKE '%carta%');";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Imposta la nota libera di un movimento.</summary>
    public void SetNote(long id, string note)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE expenses SET Note=$n WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$n", note ?? "");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Marca (o smarca) delle righe come "ignora nel controllo duplicati locale".</summary>
    public void SetDupIgnore(IEnumerable<long> ids, bool ignore)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in list)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE expenses SET DupIgnore=$v WHERE Id=$id;";
            cmd.Parameters.AddWithValue("$v", ignore ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Inserisce una nuova spesa come Pending. Ritorna l'Id locale.</summary>
    public long AddPending(DateTime? date, string description, decimal amount, ExpenseSource source,
        ExpenseDirection direction = ExpenseDirection.Uscita, long importBatch = 0)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO expenses (DateTicks, Description, Amount, Source, Direction, ImportBatch, Status, SplitwiseExpenseId, SentAtUtcTicks, CreatedAtUtcTicks)
            VALUES ($d, $desc, $amt, $src, $dir, $batch, 'Pending', 0, NULL, $created);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$d", (object?)date?.Ticks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", description);
        cmd.Parameters.AddWithValue("$amt", AmountKey(amount));
        cmd.Parameters.AddWithValue("$src", source.ToString());
        cmd.Parameters.AddWithValue("$dir", direction.ToString());
        cmd.Parameters.AddWithValue("$batch", importBatch);
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.Ticks);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Marca una spesa come inviata, registrando id Splitwise e data invio.</summary>
    public void MarkSent(long id, long splitwiseExpenseId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE expenses
            SET Status='Inviata', SplitwiseExpenseId=$sid, SentAtUtcTicks=$sent
            WHERE Id=$id;
            """;
        cmd.Parameters.AddWithValue("$sid", splitwiseExpenseId);
        cmd.Parameters.AddWithValue("$sent", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Riporta a Pending e azzera il collegamento a Splitwise (dopo aver eliminato la spesa su Splitwise).</summary>
    public void UnmarkSent(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE expenses SET Status='Pending', SplitwiseExpenseId=0, SentAtUtcTicks=NULL WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetStatus(long id, ExpenseStatus status)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE expenses SET Status=$s WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$s", status.ToString());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetStatusMany(IEnumerable<long> ids, ExpenseStatus status)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE expenses SET Status=$s WHERE Id=$id;";
        var pS = cmd.Parameters.Add("$s", SqliteType.Text);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in ids)
        {
            pS.Value = status.ToString();
            pId.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Elimina FISICAMENTE le spese dal DB locale (irreversibile, non tocca Splitwise).</summary>
    public void DeleteMany(IEnumerable<long> ids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM expenses WHERE Id=$id;";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in ids)
        {
            pId.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void UpdateEditable(long id, DateTime? date, string description, decimal amount,
        ExpenseSource source)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE expenses SET DateTicks=$d, Description=$desc, Amount=$amt, Source=$src
            WHERE Id=$id;
            """;
        cmd.Parameters.AddWithValue("$d", (object?)date?.Ticks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", description);
        cmd.Parameters.AddWithValue("$amt", AmountKey(amount));
        cmd.Parameters.AddWithValue("$src", source.ToString());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Cerca duplicati già INVIATI con stessa DATA (giorno) e IMPORTO. La dicitura è ignorata.
    /// </summary>
    public List<ExpenseRecord> FindSentDuplicates(DateTime? date, decimal amount)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM expenses
            WHERE Status='Inviata'
              AND Amount=$amt
              AND ((DateTicks IS NULL AND $d IS NULL)
                   OR (DateTicks IS NOT NULL AND $d IS NOT NULL
                       AND DateTicks/864000000000 = $d/864000000000));
            """;
        cmd.Parameters.AddWithValue("$amt", AmountKey(amount));
        cmd.Parameters.AddWithValue("$d", (object?)date?.Date.Ticks ?? DBNull.Value);
        return ReadAll(cmd);
    }

    public List<ExpenseRecord> GetByStatus(params ExpenseStatus[] statuses)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var names = statuses.Select((s, i) => $"$s{i}").ToArray();
        cmd.CommandText = $"""
            SELECT * FROM expenses
            WHERE Status IN ({string.Join(",", names)})
            ORDER BY (DateTicks IS NULL), DateTicks DESC, Id DESC;
            """;
        for (int i = 0; i < statuses.Length; i++)
            cmd.Parameters.AddWithValue($"$s{i}", statuses[i].ToString());
        return ReadAll(cmd);
    }

    public List<ExpenseRecord> GetAll()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM expenses ORDER BY (DateTicks IS NULL), DateTicks DESC, Id DESC;";
        return ReadAll(cmd);
    }

    private static List<ExpenseRecord> ReadAll(SqliteCommand cmd)
    {
        var list = new List<ExpenseRecord>();
        using var r = cmd.ExecuteReader();
        int iId = r.GetOrdinal("Id"), iD = r.GetOrdinal("DateTicks"),
            iDesc = r.GetOrdinal("Description"), iAmt = r.GetOrdinal("Amount"),
            iSrc = r.GetOrdinal("Source"), iStat = r.GetOrdinal("Status"),
            iSid = r.GetOrdinal("SplitwiseExpenseId"), iSent = r.GetOrdinal("SentAtUtcTicks"),
            iCreated = r.GetOrdinal("CreatedAtUtcTicks"), iDir = r.GetOrdinal("Direction"),
            iMan = r.GetOrdinal("ManualTags"), iBatch = r.GetOrdinal("ImportBatch"),
            iDupIg = r.GetOrdinal("DupIgnore"), iNote = r.GetOrdinal("Note"),
            iExcl = r.GetOrdinal("ExcludeTotals");
        while (r.Read())
            list.Add(new ExpenseRecord
            {
                Id = r.GetInt64(iId),
                Date = r.IsDBNull(iD) ? null : new DateTime(r.GetInt64(iD)),
                Description = r.GetString(iDesc),
                Amount = decimal.Parse(r.GetString(iAmt), CultureInfo.InvariantCulture),
                Source = Enum.Parse<ExpenseSource>(r.GetString(iSrc)),
                Direction = r.IsDBNull(iDir) ? ExpenseDirection.Uscita : Enum.Parse<ExpenseDirection>(r.GetString(iDir)),
                ManualTags = r.IsDBNull(iMan) ? "" : r.GetString(iMan),
                ImportBatch = r.IsDBNull(iBatch) ? 0 : r.GetInt64(iBatch),
                DupIgnore = !r.IsDBNull(iDupIg) && r.GetInt64(iDupIg) != 0,
                Note = r.IsDBNull(iNote) ? "" : r.GetString(iNote),
                ExcludeTotals = !r.IsDBNull(iExcl) && r.GetInt64(iExcl) != 0,
                Status = Enum.Parse<ExpenseStatus>(r.GetString(iStat)),
                SplitwiseExpenseId = r.GetInt64(iSid),
                SentAtUtc = r.IsDBNull(iSent) ? null : new DateTime(r.GetInt64(iSent)),
                CreatedAtUtc = new DateTime(r.GetInt64(iCreated))
            });
        return list;
    }

    /// <summary>Imposta i tag manuali (CSV) di un singolo movimento.</summary>
    public void SetManualTags(long id, string tagsCsv)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE expenses SET ManualTags=$t WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$t", tagsCsv ?? "");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---------- REGOLE SPLITWISE (frasi/parole che attivano l'invio automatico) ----------
    public List<string> GetSplitwiseRules()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Phrase FROM splitwise_rules ORDER BY Phrase;";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) { var p = r.GetString(0); if (!string.IsNullOrWhiteSpace(p)) list.Add(p); }
        return list;
    }

    public void SaveSplitwiseRules(IEnumerable<string> phrases)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM splitwise_rules;"; del.ExecuteNonQuery(); }
        foreach (var p in phrases.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO splitwise_rules(Phrase) VALUES($p);";
            ins.Parameters.AddWithValue("$p", p);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---------- META (flag di migrazione e simili) ----------
    public string? GetMeta(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Value FROM meta WHERE Key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(Key,Value) VALUES($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=$v;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value ?? "");
        cmd.ExecuteNonQuery();
    }

    // ---------- REGISTRO IMPORT ----------
    /// <summary>Crea una voce di registro (contatori a 0) e ne ritorna l'Id da usare come batch.</summary>
    public long BeginImportLog(string origin)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO import_log (WhenUtcTicks, Origin, Imported, Skipped) VALUES ($w, $o, 0, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$w", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("$o", origin ?? "");
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void UpdateImportLog(long id, int imported, int skipped)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE import_log SET Imported=$i, Skipped=$s WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$i", imported);
        cmd.Parameters.AddWithValue("$s", skipped);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<(long Id, DateTime When, string Origin, int Imported, int Skipped)> GetImportLog()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, WhenUtcTicks, Origin, Imported, Skipped FROM import_log ORDER BY WhenUtcTicks DESC;";
        var list = new List<(long, DateTime, string, int, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), new DateTime(r.GetInt64(1)).ToLocalTime(), r.GetString(2), r.GetInt32(3), r.GetInt32(4)));
        return list;
    }

    /// <summary>Elimina una voce di registro E tutti i movimenti importati con quel batch. Ritorna le righe cancellate.</summary>
    public int DeleteImportBatch(long logId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        int deleted;
        using (var d1 = c.CreateCommand())
        {
            d1.Transaction = tx;
            d1.CommandText = "DELETE FROM expenses WHERE ImportBatch=$id;";
            d1.Parameters.AddWithValue("$id", logId);
            deleted = d1.ExecuteNonQuery();
        }
        using (var d2 = c.CreateCommand())
        {
            d2.Transaction = tx;
            d2.CommandText = "DELETE FROM import_log WHERE Id=$id;";
            d2.Parameters.AddWithValue("$id", logId);
            d2.ExecuteNonQuery();
        }
        tx.Commit();
        return deleted;
    }

    public void ClearImportLog()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM import_log;";
        cmd.ExecuteNonQuery();
    }

    // ---------- TAG CODIFICATI ----------
    public List<string> GetTags()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Name FROM tags ORDER BY Name;";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void AddTag(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO tags (Name) VALUES ($n);";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }

    public void DeleteTag(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM tags WHERE Name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }

    // ---------- REGOLE TAG (parola -> tag) ----------
    /// <summary>Aggiunge una singola regola parola→tag (e registra il tag se nuovo).</summary>
    public void AddTagRule(string keyword, string tag)
    {
        keyword = (keyword ?? "").Trim();
        tag = (tag ?? "").Trim();
        if (keyword.Length == 0 || tag.Length == 0) return;
        AddTag(tag);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO tag_rules (Keyword, Tag) VALUES ($k, $t);";
        cmd.Parameters.AddWithValue("$k", keyword);
        cmd.Parameters.AddWithValue("$t", tag);
        cmd.ExecuteNonQuery();
    }

    public List<(string Keyword, string Tag)> GetTagRules()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Keyword, Tag FROM tag_rules ORDER BY Tag, Keyword;";
        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Sostituisce TUTTE le regole con quelle date (svuota e reinserisce).</summary>
    public void SaveTagRules(IEnumerable<(string Keyword, string Tag)> rules)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM tag_rules;"; del.ExecuteNonQuery(); }
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO tag_rules (Keyword, Tag) VALUES ($k, $t);";
        var pK = cmd.Parameters.Add("$k", SqliteType.Text);
        var pT = cmd.Parameters.Add("$t", SqliteType.Text);
        foreach (var (k, t) in rules)
        {
            if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(t)) continue;
            pK.Value = k.Trim(); pT.Value = t.Trim();
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static string AmountKey(decimal a) =>
        a.ToString("0.00", CultureInfo.InvariantCulture);
}
