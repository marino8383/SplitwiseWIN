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
    /// riga giorno ("* 14"), riga mese ("GIU"), descrizione, categoria ("PAGAMENTO"),
    /// importo ("-21,99 €") ed eventuale tag ("Da contabilizzare").
    /// </summary>
    private static List<ExpenseRow> ParseBperBlocks(List<string> lines)
    {
        var result = new List<ExpenseRow>();
        var ym = Regex.Match(string.Join("\n", lines), @"\b(20\d{2})\b");
        int year = ym.Success ? int.Parse(ym.Groups[1].Value) : DateTime.Now.Year;

        bool IsBlockStart(int i) =>
            DayLineRx.IsMatch(lines[i]) && i + 1 < lines.Count && MonthMap.ContainsKey(lines[i + 1]);

        int i = 0;
        while (i < lines.Count)
        {
            if (!IsBlockStart(i)) { i++; continue; }

            int day = int.Parse(DayLineRx.Match(lines[i]).Groups[1].Value);
            int month = MonthMap[lines[i + 1]];

            decimal? amount = null;
            var descParts = new List<string>();
            int j = i + 2;
            for (; j < lines.Count && !IsBlockStart(j); j++)
            {
                var ln = lines[j];
                if (amount is null && Regex.IsMatch(ln, @"\d,\d{2}") && TryParseAmount(ln, out var a))
                    amount = a;
                else if (!IsCategoryOrTag(ln) && !MonthMap.ContainsKey(ln))
                    descParts.Add(ln);
            }

            if (amount != null)
            {
                DateTime? date = null;
                try { date = new DateTime(year, month, day); } catch { /* giorno non valido */ }
                var desc = Regex.Replace(string.Join(" ", descParts), @"\s{2,}", " ").Trim();
                if (IsNonExpenseDescription(desc)) continue;   // scarta emolumenti/entrate
                result.Add(new ExpenseRow
                {
                    Date = date,
                    Amount = Math.Abs(amount.Value),
                    Description = desc,
                    Source = ExpenseSource.BPER,
                    SourceLine = desc
                });
            }
            i = j;
        }
        return result;
    }

    private static readonly string[] NonExpenseKeywords =
    {
        "EMOLUMENTI", "STIPENDIO", "PENSIONE", "ACCREDITO", "GIROCONTO", "RIMBORSO",
        "STORNO", "CASHBACK", "ADDEBITO SU", "COMPETENZE", "INTERESSI", "BONIFICO A VOSTRO FAVORE"
    };

    /// <summary>Vero se la dicitura indica un'entrata/movimento interno (non una spesa da dividere).</summary>
    public static bool IsNonExpenseDescription(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        var u = desc.ToUpperInvariant();
        return NonExpenseKeywords.Any(k => u.Contains(k));
    }

    private static bool IsCategoryOrTag(string line)
    {
        var u = line.ToUpperInvariant().Trim();
        string[] cats = { "PAGAMENTO", "BONIFICO", "STIPENDIO", "ADDEBITO", "PRELIEVO",
                          "VERSAMENTO", "ACCREDITO", "COMMISSIONI", "RICARICA" };
        string[] tags = { "DA CONTABILIZZARE", "RATEIZZABILE" };
        return cats.Contains(u) || tags.Contains(u);
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
