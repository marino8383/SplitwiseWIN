using System.Globalization;

namespace SplitwiseUploader;

/// <summary>
/// Elaborazione della cartella "inbox" senza interfaccia (usabile da riga di comando / scheduler).
/// Importa i file (BPER .xls, Satispay .xlsx, testo/CSV, immagini OCR), scarta i duplicati (stessa
/// chiave data+importo+descrizione normalizzata, come l'import interattivo), sposta i file in
/// "processati" e scrive tutto su un logger. Non invia nulla a Splitwise.
/// </summary>
public static class InboxProcessor
{
    private static readonly HashSet<string> DataExts =
        new(new[] { ".xls", ".xlsx", ".csv", ".txt" }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ImageExts =
        new(new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" }, StringComparer.OrdinalIgnoreCase);

    public static (int files, int added, int dup, int skippedImg) Run(
        HistoryStore db, string folder, string tessData, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        { log($"Cartella inbox inesistente o non configurata: '{folder}'."); return (0, 0, 0, 0); }

        var processed = Path.Combine(folder, "processati");
        bool ocrOk = File.Exists(Path.Combine(tessData, "ita.traineddata"));

        // chiavi già presenti nel DB (qualsiasi stato): per scartare i duplicati esatti
        var keys = db.GetAll().Select(e => DupKey(e.Date, e.Amount, e.Description)).ToHashSet();

        int files = 0, added = 0, dup = 0, skippedImg = 0;
        foreach (var file in Directory.EnumerateFiles(folder)
                     .Where(f => { var x = Path.GetExtension(f).ToLowerInvariant(); return DataExts.Contains(x) || ImageExts.Contains(x); })
                     .OrderBy(f => f))
        {
            var name = Path.GetFileName(file);
            try
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                List<ExpenseRow> rows;
                if (ImageExts.Contains(ext))
                {
                    if (!ocrOk) { skippedImg++; log($"SALTATO (manca tessdata/ita.traineddata): {name}"); continue; }
                    rows = StampProcessor.ProcessSingleFile(file, tessData).Select(r => r.Row).ToList();
                }
                else
                {
                    rows = ext switch
                    {
                        ".xls" => BperXlsImporter.Import(file),
                        ".xlsx" => SatispayImporter.Import(file),
                        ".csv" when CaiImporter.IsCai(file) => CaiImporter.Import(file),   // export CAI
                        _ => ExpenseParser.ParseText(File.ReadAllText(file))
                    };
                }

                long batch = db.BeginImportLog($"[batch] {name}");
                int a = 0, s = 0;
                foreach (var r in rows)
                {
                    if (r.Amount <= 0 && string.IsNullOrWhiteSpace(r.Description)) continue;
                    var desc = string.IsNullOrWhiteSpace(r.Description) ? "Spesa" : r.Description.Trim();
                    var key = DupKey(r.Date, r.Amount, desc);
                    var dataStr = r.Date.HasValue ? r.Date.Value.ToString("dd/MM/yyyy") : "??/??/????";
                    if (!keys.Add(key))
                    {
                        s++;
                        log($"    SCARTATO (già presente): {dataStr}  {r.Amount:0.00}€  {desc}");
                        continue;
                    }
                    db.AddPending(r.Date, desc, r.Amount, r.Source, r.Direction, batch);
                    a++;
                    log($"    importato: {dataStr}  {r.Amount:0.00}€  {desc}");
                }
                db.UpdateImportLog(batch, a, s);
                added += a; dup += s; files++;
                log($"{name}: {rows.Count} righe → {a} importati, {s} già presenti (scartati).");

                Directory.CreateDirectory(processed);
                var dest = Path.Combine(processed, name);
                if (File.Exists(dest))
                    dest = Path.Combine(processed, $"{Path.GetFileNameWithoutExtension(file)}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                File.Move(file, dest);
            }
            catch (Exception ex) { log($"ERRORE su {name}: {ex.Message}"); }
        }

        log($"TOTALE: {files} file processati, {added} importati, {dup} già presenti" +
            (skippedImg > 0 ? $", {skippedImg} immagini saltate (manca tessdata)" : "") + ".");
        return (files, added, dup, skippedImg);
    }

    // Stessa chiave di deduplica dell'import interattivo (MainForm): data(giorno)+importo(2dec)+descrizione normalizzata.
    private static string DupKey(DateTime? date, decimal amount, string? desc) =>
        $"{date?.Date.Ticks ?? 0}|{amount.ToString("0.00", CultureInfo.InvariantCulture)}|{MainForm.NormDesc(desc)}";
}
