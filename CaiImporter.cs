using System.Globalization;

namespace SplitwiseUploader;

/// <summary>
/// Importa l'export movimenti CAI (Crédit Agricole Italia): CSV con separatore ';' e intestazione
/// "Data Op.;Data Val.;Causale;Descrizione;Importo;Divisa".
/// Date gg/mm/aaaa, importi con apostrofo iniziale e virgola decimale (es. '-3,98 ; 1461 ; '-800,00).
/// Negativo = Uscita, positivo = Entrata. Fonte ExpenseSource.CAI.
/// </summary>
public static class CaiImporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Riconosce un file CAI dall'intestazione (o dal nome file).</summary>
    public static bool IsCai(string path)
    {
        try
        {
            if (Path.GetFileName(path).Contains("CAI", StringComparison.OrdinalIgnoreCase)) return true;
            var first = File.ReadLines(path).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
            var h = first.ToLowerInvariant();
            return h.Contains("data op") && h.Contains("causale") && h.Contains("descrizione") && h.Contains("divisa");
        }
        catch { return false; }
    }

    public static List<ExpenseRow> Import(string path)
    {
        var result = new List<ExpenseRow>();
        bool headerSeen = false;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(';');
            if (parts.Length < 6) continue;   // riga non valida

            // intestazione: salta la prima riga riconosciuta come header
            if (!headerSeen && line.ToLowerInvariant().Contains("data op") && line.ToLowerInvariant().Contains("importo"))
            { headerSeen = true; continue; }

            // struttura fissa: prime 3 colonne + ultime 2 fisse; la Descrizione è ciò che sta in mezzo
            // (così è robusto anche se la descrizione contenesse ';')
            var dataOp = parts[0].Trim();
            var importoStr = parts[^2].Trim();
            var causale = parts[2].Trim();
            var descr = string.Join(";", parts[3..^2]).Trim();

            if (!TryDate(dataOp, out var date)) continue;       // header/footer/righe spurie
            var amount = ParseAmount(importoStr);
            if (amount is null || amount.Value == 0) continue;

            var dir = amount.Value < 0 ? ExpenseDirection.Uscita : ExpenseDirection.Entrata;
            if (string.IsNullOrWhiteSpace(descr)) descr = causale;   // fallback

            result.Add(new ExpenseRow
            {
                Date = date,
                Amount = Math.Abs(amount.Value),
                Description = descr,
                Direction = dir,
                Source = ExpenseSource.CAI,
                Send = dir == ExpenseDirection.Uscita
            });
        }
        return result;
    }

    private static bool TryDate(string s, out DateTime date) =>
        DateTime.TryParseExact(s, "dd/MM/yyyy", Inv, DateTimeStyles.None, out date);

    private static decimal? ParseAmount(string s)
    {
        s = s.Replace("'", "").Replace("€", "").Replace(" ", "").Trim();   // toglie apostrofo Excel e simboli
        if (s.Length == 0) return null;
        if (s.Contains('.') && s.Contains(',')) s = s.Replace(".", "").Replace(",", ".");  // 1.234,56
        else if (s.Contains(',')) s = s.Replace(",", ".");                                 // 800,00
        return decimal.TryParse(s, NumberStyles.Any, Inv, out var r) ? r : null;
    }
}
