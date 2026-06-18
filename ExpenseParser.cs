using System.Globalization;
using System.Text.RegularExpressions;

namespace SplitwiseUploader;

// Modalità pensate per un gruppo da 2 persone. In tutti i casi paghi tu (movimento BPER/Satispay).
//  Equal      = spesa divisa a metà
//  AllToOther = l'intero importo è dovuto dall'altro
//  AllToMe    = l'intero importo è a tuo carico (spesa personale)
public enum SplitMode { Equal, AllToOther, AllToMe }

public class ExpenseRow
{
    public bool Send { get; set; } = true;
    public DateTime? Date { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public ExpenseDirection Direction { get; set; } = ExpenseDirection.Uscita;
    public SplitMode Mode { get; set; } = SplitMode.Equal;
    public ExpenseSource Source { get; set; } = ExpenseSource.MANUALE;

    // Quote personalizzate per Exact/Percent: userId -> valore (importo o percentuale).
    public Dictionary<long, decimal>? CustomShares { get; set; }

    public string SourceLine { get; set; } = "";
}

/// <summary>
/// Parser tollerante per movimenti BPER (CSV o testo) e Satispay (testo incollato).
/// L'euristica estrae data, importo e descrizione; tutto resta editabile nella griglia.
/// </summary>
public static class ExpenseParser
{
    // importi tipo 12,50 / 1.234,56 / -12,50 / 12.50
    private static readonly Regex AmountRx = new(
        @"-?\d{1,3}(?:[.\s]\d{3})*(?:,\d{2})|-?\d+[.,]\d{2}",
        RegexOptions.Compiled);

    private static readonly Regex DateRx = new(
        @"\b(\d{1,2})[\/\-\.](\d{1,2})[\/\-\.](\d{2,4})\b",
        RegexOptions.Compiled);

    // Abbreviazioni mese BPER (colonna data: es. "GIU")
    private static readonly Dictionary<string, int> MonthMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GEN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4, ["MAG"] = 5, ["GIU"] = 6,
        ["LUG"] = 7, ["AGO"] = 8, ["SET"] = 9, ["OTT"] = 10, ["NOV"] = 11, ["DIC"] = 12
    };

    private static readonly Regex DayLineRx = new(@"^\*?\s*(\d{1,2})$", RegexOptions.Compiled);

    // Riga che contiene SOLO un importo (eventuale segno, separatori, simbolo €): es. "-300,00 €", "+2.085,00 €"
    private static readonly Regex AmountOnlyRx = new(
        @"^[-+]?\s*\d{1,3}(?:[.\s]\d{3})*,\d{2}$", RegexOptions.Compiled);

    private static bool IsAmountLine(string ln, out decimal amount)
    {
        amount = 0;
        var t = ln.Replace("€", "").Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Trim();
        return AmountOnlyRx.IsMatch(t) && TryParseAmount(t, out amount);
    }

    public static List<ExpenseRow> ParseText(string text)
    {
        var rows = new List<ExpenseRow>();
        if (string.IsNullOrWhiteSpace(text)) return rows;

        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        // Lista movimenti BPER (web) copiata come testo: blocchi multi-riga
        // (giorno / mese / descrizione / categoria / importo / tag).
        if (lines.Count(l => MonthMap.ContainsKey(l)) >= 2)
        {
            var bper = ParseBperBlocks(lines);
            if (bper.Count > 0) return bper;
        }

        foreach (var line in lines)
        {
            // CSV con separatori ; o , (BPER export)
            if (line.Contains(';') || (line.Count(c => c == ',') >= 2 && AmountRx.IsMatch(line)))
            {
                var r = ParseCsvLine(line);
                if (r != null) { rows.Add(r); continue; }
            }

            var row = ParseFreeLine(line);
            if (row != null) rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Parser per la lista movimenti BPER web copiata come testo. Ogni movimento è un blocco:
    /// riga giorno ("15"), riga mese ("GIU"), descrizione, categoria, importo ("-20,00 €" / "+2.085,00 €")
    /// ed eventuale tag. La lista è in ordine di data DECRESCENTE e NON riporta l'anno: lo deduco
    /// (quando il mese "risale" scendendo, siamo nell'anno precedente). Direzione dal segno dell'importo.
    /// </summary>
    private static List<ExpenseRow> ParseBperBlocks(List<string> lines)
    {
        var result = new List<ExpenseRow>();

        bool IsBlockStart(int i) =>
            DayLineRx.IsMatch(lines[i]) && i + 1 < lines.Count && MonthMap.ContainsKey(lines[i + 1]);

        int year = DateTime.Now.Year;
        int prevMonth = 0;
        bool first = true;

        int i = 0;
        while (i < lines.Count)
        {
            if (!IsBlockStart(i)) { i++; continue; }

            int day = int.Parse(DayLineRx.Match(lines[i]).Groups[1].Value);
            int month = MonthMap[lines[i + 1]];

            // anno dedotto dalla sequenza decrescente
            if (first) { if (month > DateTime.Now.Month) year--; first = false; }
            else if (month > prevMonth) year--;   // il mese è "risalito" scendendo => anno precedente
            prevMonth = month;

            decimal? amount = null;
            bool income = false;
            string? bestDesc = null;
            int j = i + 2;
            for (; j < lines.Count && !IsBlockStart(j); j++)
            {
                var ln = lines[j];
                // importo SOLO dalla riga che è un importo puro (es. "-300,00 €"), non dalla descrizione
                if (amount is null && IsAmountLine(ln, out var a))
                {
                    amount = a;
                    income = a >= 0;   // le uscite hanno il segno '-', le entrate il '+'
                }
                else if (!MonthMap.ContainsKey(ln) && ln.Any(char.IsLetter))
                {
                    // descrizione = la riga più lunga del blocco (la categoria/tag sono corte)
                    if (bestDesc is null || ln.Length > bestDesc.Length) bestDesc = ln;
                }
            }

            if (amount != null)
            {
                DateTime? date = null;
                try { date = new DateTime(year, month, day); } catch { /* giorno non valido */ }
                var desc = Regex.Replace(bestDesc ?? "", @"\s{2,}", " ").Trim();
                result.Add(new ExpenseRow
                {
                    Date = date,
                    Amount = Math.Abs(amount.Value),
                    Description = desc,
                    Direction = income ? ExpenseDirection.Entrata : ExpenseDirection.Uscita,
                    Source = ExpenseSource.BPER,
                    SourceLine = desc
                });
            }
            i = j;
        }
        return result;
    }

    // Voci che SI SOVRAPPONGONO ad altri import (vanno nascoste di default e non inviate):
    //  - "CARTA DI CREDITO": riepilogo mensile carta sul conto (le singole sono nell'export carta)
    //  - "RICARICA DELL'APP": ricarica Satispay sul conto (le spese sono nell'export Satispay)
    // Diciture di sovrapposizione/aggregato da NON contare:
    //  - "CARTA DI CREDITO": riepilogo carta sul conto;
    //  - "RICARICA DELL'APP": ricarica Satispay (già nei movimenti Satispay);
    //  - "ADDEBITO SU VS C/C": riga di saldo/pagamento carta sull'export CARTA (totale già coperto dalle singole spese);
    //  - "VERSO IL TUO CONTO BANCARIO": giroconto Satispay→banca lato Satispay (uscita -200);
    //  - "ACCREDITO DALL'APP SATISPAY": stesso giroconto lato CONTO BPER (entrata +200). I due si compensano.
    private static readonly string[] OverlapKeywords =
        { "CARTA DI CREDITO", "RICARICA DELL'APP", "ADDEBITO SU VS C/C",
          "VERSO IL TUO CONTO BANCARIO", "ACCREDITO DALL'APP SATISPAY" };

    /// <summary>Vero se la dicitura è una sovrapposizione con un altro import (carta di credito / ricarica Satispay).</summary>
    public static bool IsOverlapDescription(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        var u = desc.ToUpperInvariant();
        return OverlapKeywords.Any(k => u.Contains(k));
    }

    private static ExpenseRow? ParseCsvLine(string line)
    {
        var sep = line.Contains(';') ? ';' : ',';
        var parts = line.Split(sep).Select(p => p.Trim().Trim('"')).ToArray();

        // salta header
        if (parts.Any(p => p.ToLowerInvariant() is "data" or "importo" or "descrizione" or "amount" or "date"))
            return null;

        DateTime? date = null;
        decimal? amount = null;
        var descParts = new List<string>();

        foreach (var p in parts)
        {
            if (date is null && TryParseDate(p, out var d)) { date = d; continue; }
            if (amount is null && TryParseAmount(p, out var a)) { amount = a; continue; }
            if (!string.IsNullOrWhiteSpace(p)) descParts.Add(p);
        }

        if (amount is null) return null;
        return new ExpenseRow
        {
            Date = date,
            Amount = Math.Abs(amount.Value),
            Description = string.Join(" ", descParts).Trim(),
            Source = ExpenseSource.CSV,
            SourceLine = line
        };
    }

    private static ExpenseRow? ParseFreeLine(string line)
    {
        var amountMatches = AmountRx.Matches(line);
        if (amountMatches.Count == 0) return null;

        // ultimo importo della riga = quello della transazione (euristica comune)
        var amtToken = amountMatches[^1].Value;
        if (!TryParseAmount(amtToken, out var amount)) return null;

        DateTime? date = null;
        var dm = DateRx.Match(line);
        if (dm.Success && TryParseDate(dm.Value, out var parsedDate)) date = parsedDate;

        var desc = line;
        desc = desc.Replace(amtToken, "");
        if (dm.Success) desc = desc.Replace(dm.Value, "");
        desc = Regex.Replace(desc, @"\s{2,}", " ").Trim(' ', '-', '\t', ';', ',');

        return new ExpenseRow
        {
            Date = date,
            Amount = Math.Abs(amount),
            Description = desc,
            Source = ExpenseSource.CSV,
            SourceLine = line
        };
    }

    private static bool TryParseDate(string s, out DateTime date)
    {
        date = default;
        var m = DateRx.Match(s);
        if (!m.Success) return false;
        var formats = new[] { "d/M/yyyy", "d/M/yy", "dd/MM/yyyy", "dd/MM/yy",
                              "d-M-yyyy", "dd-MM-yyyy", "d.M.yyyy", "dd.MM.yyyy" };
        return DateTime.TryParseExact(m.Value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);
    }

    private static bool TryParseAmount(string s, out decimal amount)
    {
        amount = 0;
        var m = AmountRx.Match(s);
        if (!m.Success) return false;
        var t = m.Value;

        // formato italiano: . migliaia, , decimali
        if (t.Contains(',') && t.Contains('.'))
            t = t.Replace(".", "").Replace(",", ".");
        else if (t.Contains(','))
            t = t.Replace(",", ".");

        t = t.Replace(" ", "");
        return decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
    }
}
