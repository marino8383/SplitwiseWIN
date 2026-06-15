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

        Application.Run(new MainForm());
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
