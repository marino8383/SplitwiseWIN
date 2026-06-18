using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace SplitwiseUploader;

/// <summary>
/// Importa l'export "Esporta report" di Satispay (.xlsx, foglio "Transactions").
/// Tiene solo le SPESE reali (uscite verso Negozi/Persone) e scarta ricariche,
/// giroconti a Risparmi/Investimenti, bonifici banca e incassi.
/// Legge l'xlsx senza dipendenze esterne (zip + XML).
/// </summary>
public static class SatispayImporter
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static List<ExpenseRow> Import(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        // shared strings (le celle di testo vi fanno riferimento per indice)
        var sst = new List<string>();
        var sstEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sstEntry != null)
        {
            using var s = sstEntry.Open();
            var doc = XDocument.Load(s);
            foreach (var si in doc.Root!.Elements(S + "si"))
                sst.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));
        }

        // cerca il foglio con l'intestazione giusta (di norma "Transactions" = sheet1)
        foreach (var entry in zip.Entries
                     .Where(e => e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml"))
                     .OrderBy(e => e.FullName))
        {
            var rows = ReadSheet(entry, sst);
            if (rows.Count == 0) continue;

            var header = rows[0];
            string? Col(params string[] names) =>
                header.FirstOrDefault(kv => names.Any(n =>
                    kv.Value.Trim().Equals(n, StringComparison.OrdinalIgnoreCase))).Key;

            var cData = Col("Data");
            var cNome = Col("Nome");
            var cImporto = Col("Importo");
            var cStato = Col("Stato");
            if (cData is null || cImporto is null) continue; // non è il foglio giusto

            var result = new List<ExpenseRow>();
            foreach (var cells in rows.Skip(1))
            {
                var impStr = Get(cells, cImporto);
                if (!decimal.TryParse(impStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var imp))
                    continue;

                var stato = Get(cells, cStato);
                // importa tutto ciò che è approvato; entrata/uscita dal segno (niente più esclusioni di tipo)
                if (cStato != null && stato.Length > 0 && !stato.Contains("Approvato", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (imp == 0) continue;

                var nome = Get(cells, cNome).Trim();
                var dir = imp < 0 ? ExpenseDirection.Uscita : ExpenseDirection.Entrata;

                result.Add(new ExpenseRow
                {
                    Date = ParseDate(Get(cells, cData)),
                    Amount = Math.Abs(imp),
                    Description = nome,
                    Direction = dir,
                    Source = ExpenseSource.SATISPAY,
                    Send = dir == ExpenseDirection.Uscita
                });
            }
            return result;
        }
        return new List<ExpenseRow>();
    }

    // Le date Excel sono numeri seriali (giorni dal 1899-12-30); a volte testo.
    private static DateTime? ParseDate(string s)
    {
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var oa))
        {
            try { return DateTime.FromOADate(oa).Date; } catch { return null; }
        }
        return DateTime.TryParse(s, out var d) ? d.Date : null;
    }

    /// <summary>Legge un foglio come lista di righe; ogni riga è una mappa colonna→valore (testo risolto).</summary>
    private static List<Dictionary<string, string>> ReadSheet(ZipArchiveEntry entry, List<string> sst)
    {
        var rows = new List<Dictionary<string, string>>();
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var data = doc.Root?.Element(S + "sheetData");
        if (data is null) return rows;

        foreach (var row in data.Elements(S + "row"))
        {
            var map = new Dictionary<string, string>();
            foreach (var c in row.Elements(S + "c"))
            {
                var reference = (string?)c.Attribute("r") ?? "";
                var col = new string(reference.TakeWhile(char.IsLetter).ToArray());
                map[col] = CellValue(c, sst);
            }
            rows.Add(map);
        }
        return rows;
    }

    private static string CellValue(XElement c, List<string> sst)
    {
        var t = (string?)c.Attribute("t");
        if (t == "s")
        {
            var v = c.Element(S + "v")?.Value;
            return int.TryParse(v, out var i) && i >= 0 && i < sst.Count ? sst[i] : "";
        }
        if (t == "inlineStr")
            return string.Concat(c.Element(S + "is")?.Descendants(S + "t").Select(x => x.Value) ?? Array.Empty<string>());
        return c.Element(S + "v")?.Value ?? "";  // numerico / data seriale / "str" di formula
    }

    private static string Get(Dictionary<string, string> cells, string? col) =>
        col != null && cells.TryGetValue(col, out var v) ? v : "";
}
