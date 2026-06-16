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

        // leggo tutto il primo foglio come matrice di celle
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var r = new object?[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++) r[i] = reader.GetValue(i);
            rows.Add(r);
        }

        // trova l'intestazione (tollerante): "Data operazione" + (Importo | Entrate/Uscite)
        int hRow = -1, hDate = -1, hDesc = -1, hImp = -1, hEnt = -1, hUsc = -1;
        for (int ri = 0; ri < rows.Count && hRow < 0; ri++)
        {
            int d = -1, ds = -1, imp = -1, ent = -1, usc = -1;
            for (int ci = 0; ci < rows[ri].Length; ci++)
            {
                var t = (Convert.ToString(rows[ri][ci], Inv) ?? "").Trim().ToLowerInvariant();
                if (t.Contains("data") && t.Contains("operazione")) d = ci;
                else if (t.Contains("descrizione")) ds = ci;
                else if (t.Contains("importo") && !t.Contains("valuta") && !t.Contains("estera")) imp = ci;
                else if (t.Contains("entrate")) ent = ci;
                else if (t.Contains("uscite")) usc = ci;
            }
            if (d >= 0 && (imp >= 0 || usc >= 0 || ent >= 0))
            { hRow = ri; hDate = d; hDesc = ds; hImp = imp; hEnt = ent; hUsc = usc; }
        }

        var result = new List<ExpenseRow>();
        if (hRow >= 0)
        {
            for (int ri = hRow + 1; ri < rows.Count; ri++)
            {
                if (!TryItalianDate(Cell(rows[ri], hDate), out var date)) continue;  // header/footer/totale

                decimal? amount = null;
                var dir = ExpenseDirection.Uscita;
                var usc = AsNumber(Cell(rows[ri], hUsc));
                var ent = AsNumber(Cell(rows[ri], hEnt));
                var imp = AsNumber(Cell(rows[ri], hImp));
                if (usc is not null && usc.Value != 0) { amount = Math.Abs(usc.Value); dir = ExpenseDirection.Uscita; }
                else if (ent is not null && ent.Value != 0) { amount = Math.Abs(ent.Value); dir = ExpenseDirection.Entrata; }
                else if (imp is not null && imp.Value != 0) { amount = Math.Abs(imp.Value); dir = imp.Value < 0 ? ExpenseDirection.Uscita : ExpenseDirection.Entrata; }
                if (amount is null) continue;

                var desc = (Convert.ToString(Cell(rows[ri], hDesc), Inv) ?? "").Trim();
                result.Add(new ExpenseRow
                {
                    Date = date,
                    Amount = amount.Value,
                    Description = desc,
                    Direction = dir,
                    Source = ExpenseSource.BPER,
                    Send = dir == ExpenseDirection.Uscita
                });
            }
            return result;
        }

        // fallback (intestazione non riconosciuta): solo uscite, euristica per posizione
        foreach (var row in rows)
        {
            DateTime? date = null; decimal? amount = null; var texts = new List<string>();
            foreach (var cell in row)
            {
                if (date is null && TryItalianDate(cell, out var d)) { date = d; continue; }
                var num = AsNumber(cell);
                if (amount is null && num is < 0m) { amount = num; continue; }
                var s = (Convert.ToString(cell, Inv) ?? "").Trim();
                if (s.Length > 0) texts.Add(s);
            }
            if (date is null || amount is null) continue;
            var desc = texts.Where(t => t.Any(char.IsLetter) && !IsStatus(t)).OrderByDescending(t => t.Length).FirstOrDefault() ?? "";
            result.Add(new ExpenseRow
            {
                Date = date, Amount = Math.Abs(amount.Value), Description = desc,
                Direction = ExpenseDirection.Uscita, Source = ExpenseSource.BPER, Send = true
            });
        }
        return result;
    }

    private static object? Cell(object?[] row, int i) => i >= 0 && i < row.Length ? row[i] : null;

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
