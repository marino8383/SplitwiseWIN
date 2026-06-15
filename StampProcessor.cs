using System.Globalization;
using System.Text.RegularExpressions;
using Tesseract;

namespace SplitwiseUploader;

/// <summary>
/// Processa una cartella locale di screenshot (PNG/JPG) con Tesseract (OCR offline, ITA),
/// estraendo data/importo/dicitura e producendo ExpenseRow con fonte STAMP.
/// Richiede i tessdata in ./tessdata (vedi README).
/// </summary>
public static class StampProcessor
{
    private static readonly Regex AmountRx = new(
        @"(?:€|EUR)?\s*(\d{1,3}(?:[.\s]\d{3})*(?:,\d{2})|\d+[.,]\d{2})\s*(?:€|EUR)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DateRx = new(
        @"\b(\d{1,2})[\/\-\.](\d{1,2})[\/\-\.](\d{2,4})\b",
        RegexOptions.Compiled);

    public class StampResult
    {
        public ExpenseRow Row { get; set; } = new();
        public string File { get; set; } = "";
        public string RawText { get; set; } = "";
        public bool Confident { get; set; }
    }

    /// <summary>
    /// OCR di una singola immagine (es. incollata dagli appunti). Può restituire più righe se
    /// l'immagine è una LISTA di movimenti, una sola se è la schermata di dettaglio.
    /// </summary>
    public static List<StampResult> ProcessSingleFile(string file, string tessDataPath)
    {
        using var engine = NewEngine(tessDataPath);
        return OcrFile(engine, file);
    }

    private static TesseractEngine NewEngine(string tessDataPath)
    {
        // LstmOnly: compatibile con i file di tessdata_fast (solo LSTM). Con EngineMode.Default
        // servirebbero i dati legacy del repo 'tessdata' completo.
        var engine = new TesseractEngine(tessDataPath, "ita", EngineMode.LstmOnly);
        // Gli screenshot sono a ~96 dpi: senza questo Tesseract assume un dpi sbagliato e legge male
        // numeri e date. SparseText perché la schermata è fatta di etichette sparse, non un blocco unico.
        engine.SetVariable("user_defined_dpi", "300");
        engine.DefaultPageSegMode = PageSegMode.SparseText;
        return engine;
    }

    private static List<StampResult> OcrFile(TesseractEngine engine, string file)
    {
        try
        {
            using var img = Pix.LoadFromFile(file);
            using var page = engine.Process(img);
            var fullText = page.GetText() ?? "";

            // Lista movimenti: ricostruisco ogni riga dalle coordinate delle parole.
            var rows = ReconstructTransactionRows(page);
            if (rows.Count >= 2)
            {
                var year = GuessYear(fullText);
                int fallbackMonth = GuessListMonth(rows) ?? DateTime.Now.Month;
                var results = new List<StampResult>();
                foreach (var line in rows)
                {
                    var row = ExtractLine(line, year, fallbackMonth);
                    if (row != null)
                        results.Add(new StampResult { File = file, RawText = line, Confident = true, Row = row });
                }
                if (results.Count > 0) return results;
            }

            // Altrimenti (schermata di dettaglio) estrazione singola dal testo completo.
            return new List<StampResult> { Extract(fullText, file) };
        }
        catch (Exception ex)
        {
            return new List<StampResult>
            {
                new StampResult
                {
                    File = file,
                    RawText = "OCR fallito: " + ex.Message,
                    Confident = false,
                    Row = new ExpenseRow { Source = ExpenseSource.STAMP, Send = false,
                        Description = $"[OCR fallito] {Path.GetFileName(file)}" }
                }
            };
        }
    }

    private static readonly Dictionary<string, int> MonthMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GEN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4, ["MAG"] = 5, ["GIU"] = 6,
        ["LUG"] = 7, ["AGO"] = 8, ["SET"] = 9, ["OTT"] = 10, ["NOV"] = 11, ["DIC"] = 12
    };

    private static readonly Regex MoneyRx = new(
        @"[-+]?\s*(?:€|EUR)?\s*\d{1,3}(?:[.\s]\d{3})*,\d{2}\s*(?:€|EUR)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DayMonthRx = new(
        @"\b(\d{1,2})\s*(GEN|FEB|MAR|APR|MAG|GIU|LUG|AGO|SET|OTT|NOV|DIC)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly record struct Word(string Text, int X, int YCenter);

    /// <summary>
    /// Ricostruisce le righe di una lista movimenti raggruppando le parole per posizione verticale,
    /// ancorandosi agli importi (uno per movimento). Restituisce vuoto se non sembra una lista.
    /// </summary>
    private static List<string> ReconstructTransactionRows(Page page)
    {
        var words = new List<Word>();
        using (var iter = page.GetIterator())
        {
            iter.Begin();
            do
            {
                var t = iter.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var r))
                    words.Add(new Word(t.Trim(), r.X1, (r.Y1 + r.Y2) / 2));
            } while (iter.Next(PageIteratorLevel.Word));
        }

        // parole-importo (es. "21,99", "2.085,00", anche col punto "7.40"): una per movimento
        var amounts = words.Where(w => Regex.IsMatch(w.Text, @"\d[.,]\d{2}\b"))
            .OrderBy(w => w.YCenter).ToList();
        if (amounts.Count < 2) return new List<string>();

        // passo verticale tipico tra importi consecutivi (= altezza di una riga)
        var gaps = new List<int>();
        for (int i = 1; i < amounts.Count; i++) gaps.Add(amounts[i].YCenter - amounts[i - 1].YCenter);
        gaps.Sort();
        int medianGap = Math.Max(1, gaps[gaps.Count / 2]);
        int maxDist = (int)(medianGap * 0.75);   // oltre questa distanza la parola è di un'altra riga

        // ogni parola va all'importo verticalmente PIÙ VICINO: così catturo anche la data,
        // che in alcune maschere (es. BPER web) sta in alto nella riga, non centrata sull'importo.
        var buckets = new List<List<Word>>();
        for (int i = 0; i < amounts.Count; i++) buckets.Add(new List<Word>());
        foreach (var w in words)
        {
            int best = 0, bestDist = int.MaxValue;
            for (int i = 0; i < amounts.Count; i++)
            {
                int d = Math.Abs(amounts[i].YCenter - w.YCenter);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            if (bestDist <= maxDist) buckets[best].Add(w);
        }

        return buckets
            .Select(b => string.Join(" ", b.OrderBy(w => w.X).Select(w => w.Text)))
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static int GuessYear(string text)
    {
        var m = Regex.Match(text, @"\b(20\d{2})\b");
        return m.Success ? int.Parse(m.Groups[1].Value) : DateTime.Now.Year;
    }

    // Mese "dominante" letto chiaramente nell'immagine (es. "MAG" su almeno una riga); null se nessuno.
    private static int? GuessListMonth(IEnumerable<string> rows)
    {
        var counts = new Dictionary<int, int>();
        foreach (var line in rows)
        {
            var m = Regex.Match(line, @"\b(GEN|FEB|MAR|APR|MAG|GIU|LUG|AGO|SET|OTT|NOV|DIC)\b",
                RegexOptions.IgnoreCase);
            if (m.Success && MonthMap.TryGetValue(m.Groups[1].Value, out var mo))
                counts[mo] = counts.GetValueOrDefault(mo) + 1;
        }
        return counts.Count == 0 ? null : counts.OrderByDescending(kv => kv.Value).First().Key;
    }

    // Data di una riga: prima il mese "pulito" (giorno+mese o mese+giorno), poi una data gg/mm/aaaa,
    // infine — se il mese è illeggibile — il giorno a inizio riga con il mese dominante dell'immagine
    // (o quello corrente) e l'anno dedotto.
    private static DateTime? GuessRowDate(string line, int year, int fallbackMonth)
    {
        var m1 = Regex.Match(line, @"\b(\d{1,2})\s*(GEN|FEB|MAR|APR|MAG|GIU|LUG|AGO|SET|OTT|NOV|DIC)\b",
            RegexOptions.IgnoreCase);
        if (m1.Success && MonthMap.TryGetValue(m1.Groups[2].Value, out var mo1))
            return MakeDate(year, mo1, int.Parse(m1.Groups[1].Value));

        var m2 = Regex.Match(line, @"\b(GEN|FEB|MAR|APR|MAG|GIU|LUG|AGO|SET|OTT|NOV|DIC)\s*(\d{1,2})\b",
            RegexOptions.IgnoreCase);
        if (m2.Success && MonthMap.TryGetValue(m2.Groups[1].Value, out var mo2))
            return MakeDate(year, mo2, int.Parse(m2.Groups[2].Value));

        var dx = DateRx.Match(line);
        if (dx.Success && TryParseDate(dx.Value, out var d3)) return d3;

        // fallback: giorno a inizio riga (mese storpiato ignorato) -> mese dominante/corrente + anno dedotto
        var m4 = Regex.Match(line, @"^\D{0,8}(\d{1,2})\b");
        if (m4.Success && int.TryParse(m4.Groups[1].Value, out var dd) && dd >= 1 && dd <= 31)
            return MakeDate(year, fallbackMonth, dd);

        return null;
    }

    private static DateTime? MakeDate(int year, int month, int day)
    {
        try { return new DateTime(year, month, day); } catch { return null; }
    }

    // Estrae una spesa da UNA riga della lista movimenti (data "15 GIU", descrizione, importo €).
    private static ExpenseRow? ExtractLine(string line, int year, int fallbackMonth)
    {
        // importo: preferisco quello col simbolo €; se manca, prendo l'ultimo (di solito a destra = totale)
        Match? euro = null, last = null;
        foreach (Match m in AmountRx.Matches(line))
        {
            if (!TryParseAmount(m.Groups[1].Value, out _)) continue;
            last = m;
            if (m.Value.Contains('€') || m.Value.Contains("EUR", StringComparison.OrdinalIgnoreCase))
                euro = m;
        }
        var chosen = euro ?? last;
        if (chosen is null || !TryParseAmount(chosen.Groups[1].Value, out var amount)) return null;
        // '+' subito prima dell'importo => entrata (es. stipendio): non è una spesa da dividere
        bool income = Regex.IsMatch(line[..chosen.Index], @"\+\s*$");

        var date = GuessRowDate(line, year, fallbackMonth);

        // descrizione: la riga ripulita da data, importo, tag, categoria e codici bancari
        var desc = DayMonthRx.Replace(line, "");
        // togli il giorno/mese (anche storpiato dall'OCR, es. "lu 12") a inizio riga
        desc = Regex.Replace(desc, @"^\D{0,8}\d{1,2}\b\s*", "");
        desc = MoneyRx.Replace(desc, "");
        desc = Regex.Replace(desc, @"\b(Rateizzabile|Da contabilizzare)\b", "", RegexOptions.IgnoreCase);
        desc = Regex.Replace(desc, @"\b(PAGAMENTO|BONIFICO|STIPENDIO|EMOLUMENTI)\b", "", RegexOptions.IgnoreCase);
        var cut = Regex.Match(desc, @"\s+(N[:.]|ID:|XID|CRO|TRN|DEB:|RID)\b", RegexOptions.IgnoreCase);
        if (cut.Success) desc = desc[..cut.Index];
        desc = Regex.Replace(desc, @"\s{2,}", " ").Trim().Trim('-', ',', ';', ':', '.').Trim();
        if (desc.Length == 0) desc = "Spesa";
        if (desc.Length > 80) desc = desc[..80];

        return new ExpenseRow
        {
            Date = date,
            Amount = amount,
            Description = desc,
            Source = ExpenseSource.STAMP,
            Send = !income   // le entrate (+) le lascio deselezionate
        };
    }

    private static StampResult Extract(string text, string file)
    {
        // Importo: preferisco quelli col simbolo € (così ignoro valori come "7,19 kg" di CO2);
        // tra i candidati prendo il più grande (di solito il totale).
        decimal? amountAny = null, amountEuro = null;
        foreach (Match m in AmountRx.Matches(text))
        {
            if (!TryParseAmount(m.Groups[1].Value, out var a)) continue;
            amountAny = amountAny is null ? a : Math.Max(amountAny.Value, a);
            if (m.Value.Contains('€') || m.Value.Contains("EUR", StringComparison.OrdinalIgnoreCase))
                amountEuro = amountEuro is null ? a : Math.Max(amountEuro.Value, a);
        }
        decimal? amount = amountEuro ?? amountAny;

        // Data: preferisco quella vicino a "contabile" (data dell'operazione); altrimenti la prima trovata.
        DateTime? date = null;
        var contIdx = text.IndexOf("contabile", StringComparison.OrdinalIgnoreCase);
        if (contIdx >= 0)
        {
            var dmc = DateRx.Match(text[contIdx..]);
            if (dmc.Success) TryParseDate(dmc.Value, out date);
        }
        if (date is null)
        {
            var dm = DateRx.Match(text);
            if (dm.Success) TryParseDate(dm.Value, out date);
        }

        var desc = ExtractDescription(text) ?? "Spesa";

        var confident = amount is not null;
        return new StampResult
        {
            File = file,
            RawText = text,
            Confident = confident,
            Row = new ExpenseRow
            {
                Date = date,
                Amount = amount ?? 0m,
                Description = desc,
                Source = ExpenseSource.STAMP,
                Send = confident   // se non ho trovato importo, lascio deselezionato
            }
        };
    }

    // Etichette dell'app bancaria da NON usare come descrizione.
    private static readonly string[] LabelLines =
    {
        "data contabile", "data valuta", "tipo di movimento", "tipologia operazione",
        "descrizione completa", "descrizione", "co2", "consumata", "saldo", "causale"
    };

    /// <summary>
    /// Sceglie la riga con più testo (la descrizione vera, non l'importo/la data/un'etichetta),
    /// la ripulisce dal prefisso giorno/mese e taglia i codici bancari finali (N:, ID:, XID, DEB:…).
    /// </summary>
    private static string? ExtractDescription(string text)
    {
        var desc = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Count(char.IsLetter) >= 4)                       // abbastanza testo
            .Where(l => !LabelLines.Any(s => l.ToLowerInvariant().StartsWith(s)))
            .Where(l => !(l.Contains('€') && l.Count(char.IsLetter) < 8))  // scarta le righe-importo
            .OrderByDescending(l => l.Count(char.IsLetter))                // la riga più "ricca" di testo
            .FirstOrDefault();
        if (desc is null) return null;

        // togli il prefisso giorno/mese (es. "15 GIU", "16.")
        desc = Regex.Replace(desc, @"^\s*\d{1,2}[\.\)]?\s*", "");
        desc = Regex.Replace(desc,
            @"^(GEN|FEB|MAR|APR|MAG|GIU|LUG|AGO|SET|OTT|NOV|DIC)\b\.?\s*", "", RegexOptions.IgnoreCase);
        // taglia codici/identificativi bancari finali
        var cut = Regex.Match(desc, @"\s+(N[:.]|ID:|XID|CRO|TRN|DEB:|RID)\b", RegexOptions.IgnoreCase);
        if (cut.Success) desc = desc[..cut.Index];

        desc = desc.Trim().Trim('-', ',', ';', ':', '.').Trim();
        if (desc.Length == 0) return null;
        return desc.Length > 80 ? desc[..80] : desc;
    }

    private static bool TryParseDate(string s, out DateTime? date)
    {
        date = null;
        var formats = new[] { "d/M/yyyy", "d/M/yy", "dd/MM/yyyy", "dd/MM/yy",
                              "d-M-yyyy", "dd-MM-yyyy", "d.M.yyyy", "dd.MM.yyyy" };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d)) { date = d; return true; }
        return false;
    }

    private static bool TryParseAmount(string t, out decimal amount)
    {
        if (t.Contains(',') && t.Contains('.')) t = t.Replace(".", "").Replace(",", ".");
        else if (t.Contains(',')) t = t.Replace(",", ".");
        t = t.Replace(" ", "");
        return decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
    }
}
