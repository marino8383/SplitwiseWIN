using System.Globalization;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace SplitwiseUploader;

/// <summary>
/// Importa l'export "Lista Movimenti Carta" di BPER (.xls binario, ma legge anche .xlsx).
/// Approccio indipendente dall'intestazione: una riga è un movimento se ha una DATA italiana
/// ("09 giugno 2026") e un IMPORTO negativo (uscita). Robusto a piccole differenze di layout.
/// </summary>
public static class BperXlsImporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static BperXlsImporter()
    {
        // ExcelDataReader, per i .xls binari, usa code page legacy (es. 1252): vanno registrate.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    private static readonly Dictionary<string, int> ItMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gennaio"] = 1, ["febbraio"] = 2, ["marzo"] = 3, ["aprile"] = 4, ["maggio"] = 5, ["giugno"] = 6,
        ["luglio"] = 7, ["agosto"] = 8, ["settembre"] = 9, ["ottobre"] = 10, ["novembre"] = 11, ["dicembre"] = 12
    };

    public static List<ExpenseRow> Import(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);  // auto-rileva .xls / .xlsx

        var result = new List<ExpenseRow>();
        do
        {
            while (reader.Read())
            {
                int n = reader.FieldCount;
                DateTime? date = null;
                decimal? amount = null;
                var texts = new List<string>();

                for (int i = 0; i < n; i++)
                {
                    var cell = reader.GetValue(i);

                    if (date is null && TryItalianDate(cell, out var d)) { date = d; continue; }

                    var num = AsNumber(cell);
                    if (amount is null && num is < 0m) { amount = num; continue; }

                    var s = (Convert.ToString(cell, Inv) ?? "").Trim();
                    if (s.Length > 0) texts.Add(s);
                }

                if (date is null || amount is null) continue;   // non è una riga movimento

                // descrizione = testo più lungo con lettere, escludendo gli stati ("Contabilizzato"…)
                var desc = texts
                    .Where(t => t.Any(char.IsLetter) && !IsStatus(t))
                    .OrderByDescending(t => t.Length)
                    .FirstOrDefault() ?? "";

                // escludi emolumenti, addebito riepilogativo della carta e altre voci non-spesa
                if (ExpenseParser.IsNonExpenseDescription(desc)) continue;

                result.Add(new ExpenseRow
                {
                    Date = date,
                    Amount = Math.Abs(amount.Value),
                    Description = desc,
                    Source = ExpenseSource.BPER,
                    Send = true
                });
            }
        } while (reader.NextResult());

        return result;
    }

    private static bool IsStatus(string t)
    {
        var u = t.Trim().ToLowerInvariant();
        return u.Contains("contabilizz") || u is "autorizzato" or "storno" or "annullato" or "in corso" or "rifiutato";
    }

    private static decimal? AsNumber(object? v)
    {
        if (v is double d) return (decimal)d;
        if (v is decimal m) return m;
        if (v is int i) return i;
        if (v is float f) return (decimal)f;
        var s = (Convert.ToString(v, Inv) ?? "").Replace("€", "").Trim();
        if (s.Length == 0 || !Regex.IsMatch(s, @"^-?[\d.,\s]+$")) return null;  // solo se sembra un numero
        if (s.Contains(',') && s.Contains('.')) s = s.Replace(".", "").Replace(",", ".");
        else if (s.Contains(',')) s = s.Replace(",", ".");
        s = s.Replace(" ", "");
        return decimal.TryParse(s, NumberStyles.Any, Inv, out var r) ? r : null;
    }

    private static bool TryItalianDate(object? v, out DateTime date)
    {
        date = default;
        if (v is DateTime dt) { date = dt.Date; return true; }
        var s = (Convert.ToString(v, Inv) ?? "").Trim();
        if (s.Length == 0) return false;

        if (DateTime.TryParse(s, new CultureInfo("it-IT"), DateTimeStyles.None, out var d1))
        { date = d1.Date; return true; }

        var mm = Regex.Match(s, @"(\d{1,2})\s+([A-Za-zàèéìòù]+)\s+(\d{4})");
        if (mm.Success && ItMonths.TryGetValue(mm.Groups[2].Value, out var mo)
            && int.TryParse(mm.Groups[1].Value, out var day) && int.TryParse(mm.Groups[3].Value, out var yr))
        {
            try { date = new DateTime(yr, mo, day); return true; } catch { return false; }
        }
        return false;
    }
}
