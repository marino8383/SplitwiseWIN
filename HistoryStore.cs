using Microsoft.Data.Sqlite;
using System.Globalization;

namespace SplitwiseUploader;

public enum ExpenseSource { MANUALE, CSV, STAMP, SATISPAY, BPER }

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

    public HistoryStore(string? dbPath = null)
    {
        dbPath ??= Path.Combine(AppContext.BaseDirectory, "history.db");
        _connStr = $"Data Source={dbPath}";
        Init();
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
    }

    /// <summary>Inserisce una nuova spesa come Pending. Ritorna l'Id locale.</summary>
    public long AddPending(DateTime? date, string description, decimal amount, ExpenseSource source,
        ExpenseDirection direction = ExpenseDirection.Uscita)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO expenses (DateTicks, Description, Amount, Source, Direction, Status, SplitwiseExpenseId, SentAtUtcTicks, CreatedAtUtcTicks)
            VALUES ($d, $desc, $amt, $src, $dir, 'Pending', 0, NULL, $created);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$d", (object?)date?.Ticks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", description);
        cmd.Parameters.AddWithValue("$amt", AmountKey(amount));
        cmd.Parameters.AddWithValue("$src", source.ToString());
        cmd.Parameters.AddWithValue("$dir", direction.ToString());
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
            iCreated = r.GetOrdinal("CreatedAtUtcTicks"), iDir = r.GetOrdinal("Direction");
        while (r.Read())
            list.Add(new ExpenseRecord
            {
                Id = r.GetInt64(iId),
                Date = r.IsDBNull(iD) ? null : new DateTime(r.GetInt64(iD)),
                Description = r.GetString(iDesc),
                Amount = decimal.Parse(r.GetString(iAmt), CultureInfo.InvariantCulture),
                Source = Enum.Parse<ExpenseSource>(r.GetString(iSrc)),
                Direction = r.IsDBNull(iDir) ? ExpenseDirection.Uscita : Enum.Parse<ExpenseDirection>(r.GetString(iDir)),
                Status = Enum.Parse<ExpenseStatus>(r.GetString(iStat)),
                SplitwiseExpenseId = r.GetInt64(iSid),
                SentAtUtc = r.IsDBNull(iSent) ? null : new DateTime(r.GetInt64(iSent)),
                CreatedAtUtc = new DateTime(r.GetInt64(iCreated))
            });
        return list;
    }

    private static string AmountKey(decimal a) =>
        a.ToString("0.00", CultureInfo.InvariantCulture);
}
