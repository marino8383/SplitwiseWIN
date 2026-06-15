namespace SplitwiseUploader;

/// <summary>
/// Processa una cartella locale (tipicamente Google Drive sincronizzato) di file CSV
/// esportati da BPER. Usa lo stesso parser del testo/CSV. Dopo l'elaborazione sposta
/// i file in una sottocartella "processati" per non rileggerli.
/// </summary>
public static class CsvFolderProcessor
{
    private static readonly string[] Extensions = { ".csv", ".txt" };
    public const string ArchiveFolderName = "processati";

    public class CsvFolderResult
    {
        public List<ExpenseRow> Rows { get; } = new();
        public List<string> ProcessedFiles { get; } = new();
        public List<string> ArchivedFiles { get; } = new();
        public List<string> Errors { get; } = new();
    }

    public static CsvFolderResult ProcessFolder(string folder, bool archive = true)
    {
        var result = new CsvFolderResult();
        if (!Directory.Exists(folder)) return result;

        var archiveDir = Path.Combine(folder, ArchiveFolderName);

        var files = Directory.EnumerateFiles(folder)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file);
                var rows = ExpenseParser.ParseText(text);
                foreach (var r in rows) r.Source = ExpenseSource.CSV;
                result.Rows.AddRange(rows);
                result.ProcessedFiles.Add(file);

                if (archive)
                {
                    Directory.CreateDirectory(archiveDir);
                    var dest = UniqueDestination(archiveDir, Path.GetFileName(file));
                    File.Move(file, dest);
                    result.ArchivedFiles.Add(dest);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Evita collisioni se un file con lo stesso nome è già stato archiviato.</summary>
    private static string UniqueDestination(string dir, string fileName)
    {
        var dest = Path.Combine(dir, fileName);
        if (!File.Exists(dest)) return dest;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(dir, $"{name}_{stamp}{ext}");
    }
}
