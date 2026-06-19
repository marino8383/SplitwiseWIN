using System.Text.Json;

namespace SplitwiseUploader;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Helper: "SplitwiseUploader.exe groups" stampa i gruppi e i loro id, poi esce.
        // Utile per trovare il GroupId da mettere in appsettings.json.
        if (args.Length > 0 && args[0].Equals("groups", StringComparison.OrdinalIgnoreCase))
        {
            ListGroups().GetAwaiter().GetResult();
            return;
        }

        // Modalità BATCH senza interfaccia: "SplitwiseUploader.exe inbox" (o "batch").
        // Processa la cartella InboxFolder, scrive un log giornaliero in .\logs\yyyyMMdd.log ed esce.
        // Pensata per Windows Task Scheduler. Non invia nulla a Splitwise.
        if (args.Length > 0 && (args[0].Equals("inbox", StringComparison.OrdinalIgnoreCase)
                             || args[0].Equals("batch", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = RunInboxBatch();
            return;
        }

        Application.Run(new MainForm());
    }

    // Esecuzione headless dell'import inbox con log su file. Ritorna 0 se ok, 1 se errore di configurazione.
    private static int RunInboxBatch()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"{DateTime.Now:yyyyMMdd}.log");

        void Log(string msg)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { /* ignora errori di scrittura log */ }
            Console.WriteLine(line);
        }

        Log("=== Avvio batch inbox ===");
        try
        {
            var cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(cfgPath)) { Log("ERRORE: appsettings.json non trovato."); return 1; }
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(cfgPath))!;
            if (string.IsNullOrWhiteSpace(cfg.InboxFolder)) { Log("ERRORE: InboxFolder non configurato in appsettings.json."); return 1; }

            var db = new HistoryStore(cfg.DbPath);   // stesso DB configurato nell'app
            var tessData = Path.Combine(AppContext.BaseDirectory, "tessdata");
            var (files, added, dup, skippedImg, addedIds) = InboxProcessor.Run(db, cfg.InboxFolder, tessData, Log);
            Log($"=== Fine import: {files} file, {added} importati, {dup} scartati, {skippedImg} immagini saltate ===");

            // Regole Splitwise: invio automatico dei nuovi importati (se Splitwise configurato e ci sono regole)
            var rules = db.GetSplitwiseRules();
            if (addedIds.Count > 0 && rules.Count > 0 && cfg.GroupId != 0
                && !string.IsNullOrWhiteSpace(cfg.ConsumerKey) && !string.IsNullOrWhiteSpace(cfg.ConsumerSecret))
            {
                try
                {
                    Log("Regole Splitwise: verifica e invio automatico…");
                    var client = new SplitwiseClient(cfg.ConsumerKey, cfg.ConsumerSecret);
                    client.AuthenticateAsync().GetAwaiter().GetResult();
                    var me = client.GetCurrentUserAsync().GetAwaiter().GetResult();
                    var cands = db.GetAll().Where(e => addedIds.Contains(e.Id)
                                    && e.Direction == ExpenseDirection.Uscita
                                    && !ExpenseParser.IsOverlapDescription(e.Description)).ToList();
                    var (sent, flagged) = SplitwiseAuto.ProcessAsync(db, client, cfg.GroupId, me, cfg.CurrencyCode,
                        rules, cfg.AmountTolerance, cfg.NearbyDays, cands, Log).GetAwaiter().GetResult();
                    Log($"Regole Splitwise: {sent} inviate in automatico, {flagged} da verificare (Note).");
                }
                catch (Exception ex) { Log("Regole Splitwise (batch) non riuscite: " + ex.Message); }
            }

            Log("=== Fine batch ===");
            return 0;
        }
        catch (Exception ex) { Log("ERRORE batch: " + ex); return 1; }
    }

    private static async Task ListGroups()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var cfg = JsonSerializer.Deserialize<AppConfig>(json)!;
        var c = new SplitwiseClient(cfg.ConsumerKey, cfg.ConsumerSecret);
        await c.AuthenticateAsync();
        var groups = await c.GetGroupsAsync();
        var sb = new System.Text.StringBuilder("Gruppi disponibili:\n\n");
        foreach (var g in groups) sb.AppendLine($"  id = {g.Id}\t{g.Name}");
        MessageBox.Show(sb.ToString(), "Splitwise — gruppi");
    }
}
