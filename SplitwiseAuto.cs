using System.Text.RegularExpressions;

namespace SplitwiseUploader;

/// <summary>
/// Invio automatico a Splitwise in base alle "Regole Splitwise" (frasi/parole nella descrizione).
/// Per ogni movimento candidato: se "Confronta Splitwise" accenderebbe qualcosa (stessa data+importo,
/// importo+descrizione simile, o importo entro tolleranza a data vicina, tra le spese pagate da ME),
/// NON invia e segnala nelle Note "verificare se caricare in splitwise"; altrimenti invia (Parti uguali).
/// Logica condivisa tra UI e batch headless.
/// </summary>
public static class SplitwiseAuto
{
    public const string VerifyNote = "verificare se caricare in splitwise";

    /// <summary>Vero se la descrizione contiene (no maiuscole/accenti) almeno una delle frasi-regola.</summary>
    public static bool MatchesAnyRule(string? desc, IEnumerable<string> rules)
    {
        var nd = MainForm.NormDesc(desc);
        if (nd.Length == 0) return false;
        foreach (var r in rules)
        {
            var nr = MainForm.NormDesc(r);
            if (nr.Length > 0 && nd.Contains(nr)) return true;
        }
        return false;
    }

    /// <summary>Vero se il "Confronta Splitwise" accenderebbe la riga (verde/azzurro/giallo) tra le spese pagate da me.</summary>
    public static bool LikelyOnSplitwise(
        ExpenseRecord e,
        List<(long Id, DateTime Date, decimal Cost, string Description, long PayerId)> mine,
        decimal amtTol, int nearby)
    {
        if (!e.Date.HasValue) return false;
        var day = e.Date.Value.Date;
        var swDesc = string.IsNullOrWhiteSpace(e.Note) ? (e.Description ?? "") : e.Note.Trim();

        foreach (var x in mine)
        {
            if (Math.Abs(x.Cost - e.Amount) < 0.005m)          // importo esatto
            {
                if (x.Date == day) return true;                 // verde
                if (DescriptionsOverlap(swDesc, x.Description)) return true; // azzurro
            }
        }
        foreach (var x in mine)
            if (Math.Abs(x.Cost - e.Amount) <= amtTol && Math.Abs((x.Date - day).TotalDays) <= nearby)
                return true;                                    // giallo
        return false;
    }

    /// <summary>
    /// Processa i candidati (già filtrati: uscite non-overlap con data) applicando le regole.
    /// Invia quelli "puliti", segnala nelle Note quelli che forse sono già su Splitwise. Ritorna (inviati, segnalati).
    /// </summary>
    public static async Task<(int sent, int flagged)> ProcessAsync(
        HistoryStore db, SplitwiseClient client, long groupId, long me, string currency,
        List<string> rules, decimal amtTol, int nearby, List<ExpenseRecord> candidates, Action<string>? log = null)
    {
        var matching = candidates.Where(e => e.Date.HasValue && MatchesAnyRule(e.Description, rules)).ToList();
        if (matching.Count == 0) return (0, 0);

        var since = matching.Min(e => e.Date!.Value).AddDays(-nearby - 1);
        var existing = await client.GetExpensesSinceAsync(groupId, since);
        var mine = existing.Where(x => x.PayerId == me).ToList();

        int sent = 0, flagged = 0;
        foreach (var e in matching)
        {
            if (LikelyOnSplitwise(e, mine, amtTol, nearby))
            {
                db.SetNote(e.Id, VerifyNote);
                flagged++;
                log?.Invoke($"  DA VERIFICARE (forse già su Splitwise): {e.Date:dd/MM/yyyy} {e.Amount:0.00}€ {e.Description}");
            }
            else
            {
                try
                {
                    var desc = string.IsNullOrWhiteSpace(e.Note) ? e.Description : e.Note.Trim();
                    var id = await client.CreateEqualExpenseAsync(groupId, e.Amount, desc, currency, e.Date);
                    db.MarkSent(e.Id, id);
                    sent++;
                    log?.Invoke($"  INVIATO a Splitwise: {e.Date:dd/MM/yyyy} {e.Amount:0.00}€ {e.Description}");
                }
                catch (Exception ex) { log?.Invoke($"  invio fallito: {e.Description}: {ex.Message}"); }
            }
        }
        return (sent, flagged);
    }

    // Stessa euristica di MainForm: condividono un token distintivo (codice/numero lungo).
    private static bool DescriptionsOverlap(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var tb = b.ToLowerInvariant();
        var tokens = Regex.Split(a.ToLowerInvariant(), @"[^a-z0-9àèéìòù]+")
            .Where(t => t.Length >= 5 && t.Any(char.IsDigit));
        return tokens.Any(t => tb.Contains(t));
    }
}
