using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SplitwiseUploader;

/// <summary>
/// Client minimale per le API Splitwise v3.0.
/// Autenticazione via OAuth2 client_credentials (servono solo consumer key/secret).
/// </summary>
public class SplitwiseClient
{
    private const string Base = "https://secure.splitwise.com";
    private readonly HttpClient _http = new();
    private readonly string _consumerKey;
    private readonly string _consumerSecret;
    private string? _accessToken;

    public SplitwiseClient(string consumerKey, string consumerSecret)
    {
        _consumerKey = consumerKey;
        _consumerSecret = consumerSecret;
    }

    /// <summary>Ottiene (e memorizza) un access token via client_credentials.</summary>
    public async Task AuthenticateAsync()
    {
        var resp = await _http.PostAsync($"{Base}/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _consumerKey,
                ["client_secret"] = _consumerSecret,
                ["grant_type"] = "client_credentials"
            }));

        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Autenticazione fallita ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new Exception("access_token assente nella risposta.");

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    /// <summary>Id dell'utente corrente (il proprietario della API key). Serve per sapere chi sei "tu".</summary>
    public async Task<long> GetCurrentUserAsync()
    {
        EnsureAuth();
        var resp = await _http.GetAsync($"{Base}/api/v3.0/get_current_user");
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"get_current_user fallita ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("user").GetProperty("id").GetInt64();
    }

    /// <summary>Restituisce i gruppi dell'utente come (id, nome). Utile per trovare il GroupId.</summary>
    public async Task<List<(long Id, string Name)>> GetGroupsAsync()
    {
        EnsureAuth();
        var resp = await _http.GetAsync($"{Base}/api/v3.0/get_groups");
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"get_groups fallita ({(int)resp.StatusCode}): {body}");

        var result = new List<(long, string)>();
        using var doc = JsonDocument.Parse(body);
        foreach (var g in doc.RootElement.GetProperty("groups").EnumerateArray())
            result.Add((g.GetProperty("id").GetInt64(), g.GetProperty("name").GetString() ?? ""));
        return result;
    }

    /// <summary>
    /// Spese del gruppo in un dato giorno (esclude le cancellate e i pagamenti/rimborsi).
    /// Serve per verificare i duplicati direttamente su Splitwise, non solo nello storico locale.
    /// </summary>
    public async Task<List<(long Id, DateTime Date, decimal Cost, string Description)>>
        GetExpensesOnDayAsync(long groupId, DateTime day)
    {
        EnsureAuth();
        // Finestra ampia (±1 giorno) per evitare problemi di fuso orario; filtro poi sulla data locale.
        var after = day.Date.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var before = day.Date.AddDays(2).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var url = $"{Base}/api/v3.0/get_expenses?group_id={groupId}" +
                  $"&dated_after={after}&dated_before={before}&limit=0";

        var resp = await _http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"get_expenses fallita ({(int)resp.StatusCode}): {body}");

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var result = new List<(long, DateTime, decimal, string)>();
        using var doc = JsonDocument.Parse(body);
        foreach (var e in doc.RootElement.GetProperty("expenses").EnumerateArray())
        {
            // salta le spese cancellate
            if (e.TryGetProperty("deleted_at", out var del) && del.ValueKind != JsonValueKind.Null)
                continue;
            // salta i pagamenti (saldi), non sono spese vere
            if (e.TryGetProperty("payment", out var pay) && pay.ValueKind == JsonValueKind.True)
                continue;

            if (!e.TryGetProperty("date", out var dt) || dt.ValueKind == JsonValueKind.Null) continue;
            if (!DateTimeOffset.TryParse(dt.GetString(), out var dto)) continue;
            var localDate = dto.ToLocalTime().Date;
            if (localDate != day.Date) continue;

            var costStr = e.GetProperty("cost").GetString() ?? "0";
            decimal.TryParse(costStr, System.Globalization.NumberStyles.Any, inv, out var cost);
            var desc = e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            result.Add((e.GetProperty("id").GetInt64(), localDate, cost, desc));
        }
        return result;
    }

    /// <summary>
    /// Tutte le spese del gruppo da una certa data in poi (esclude cancellate e pagamenti/saldi).
    /// Usato dalla ricerca testuale.
    /// </summary>
    public async Task<List<(long Id, DateTime Date, decimal Cost, string Description, long PayerId)>>
        GetExpensesSinceAsync(long groupId, DateTime since)
    {
        EnsureAuth();
        var after = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var url = $"{Base}/api/v3.0/get_expenses?group_id={groupId}&dated_after={after}&limit=0";

        var resp = await _http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"get_expenses fallita ({(int)resp.StatusCode}): {body}");

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var result = new List<(long, DateTime, decimal, string, long)>();
        using var doc = JsonDocument.Parse(body);
        foreach (var e in doc.RootElement.GetProperty("expenses").EnumerateArray())
        {
            if (e.TryGetProperty("deleted_at", out var del) && del.ValueKind != JsonValueKind.Null) continue;
            if (e.TryGetProperty("payment", out var pay) && pay.ValueKind == JsonValueKind.True) continue;
            if (!e.TryGetProperty("date", out var dt) || dt.ValueKind == JsonValueKind.Null) continue;
            if (!DateTimeOffset.TryParse(dt.GetString(), out var dto)) continue;

            var costStr = e.GetProperty("cost").GetString() ?? "0";
            decimal.TryParse(costStr, System.Globalization.NumberStyles.Any, inv, out var cost);
            var desc = e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

            // pagante = utente con paid_share > 0 più alto
            long payerId = 0; decimal bestPaid = 0;
            if (e.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in users.EnumerateArray())
                {
                    decimal paid = 0;
                    if (u.TryGetProperty("paid_share", out var ps))
                        decimal.TryParse(ps.GetString(), System.Globalization.NumberStyles.Any, inv, out paid);
                    if (paid > bestPaid)
                    {
                        bestPaid = paid;
                        if (u.TryGetProperty("user_id", out var uid)) payerId = uid.GetInt64();
                        else if (u.TryGetProperty("user", out var uo) && uo.TryGetProperty("id", out var idp)) payerId = idp.GetInt64();
                    }
                }
            }

            result.Add((e.GetProperty("id").GetInt64(), dto.ToLocalTime().Date, cost, desc, payerId));
        }
        return result;
    }

    /// <summary>Membri di un gruppo come (id, nome). Serve per le divisioni Exact/Percent.</summary>
    public async Task<List<(long Id, string Name)>> GetGroupMembersAsync(long groupId)
    {
        EnsureAuth();
        var resp = await _http.GetAsync($"{Base}/api/v3.0/get_group/{groupId}");
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"get_group fallita ({(int)resp.StatusCode}): {body}");

        var result = new List<(long, string)>();
        using var doc = JsonDocument.Parse(body);
        foreach (var m in doc.RootElement.GetProperty("group").GetProperty("members").EnumerateArray())
        {
            var id = m.GetProperty("id").GetInt64();
            var fn = m.TryGetProperty("first_name", out var f) ? f.GetString() : "";
            var ln = m.TryGetProperty("last_name", out var l) ? l.GetString() : "";
            result.Add((id, $"{fn} {ln}".Trim()));
        }
        return result;
    }

    /// <summary>
    /// Crea una spesa divisa equamente nel gruppo.
    /// Ritorna l'id della spesa creata.
    /// </summary>
    public Task<long> CreateEqualExpenseAsync(long groupId, decimal cost, string description,
        string currency, DateTime? date = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["cost"] = cost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["description"] = description,
            ["group_id"] = groupId,
            ["currency_code"] = currency,
            ["split_equally"] = true
        };
        if (date.HasValue) payload["date"] = date.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        return PostExpenseAsync(payload);
    }

    /// <summary>
    /// Crea una spesa con quote esatte. shares = lista di (userId, paidShare, owedShare).
    /// La somma degli owedShare (e dei paidShare) deve essere uguale a cost.
    /// </summary>
    public Task<long> CreateSharedExpenseAsync(long groupId, decimal cost, string description,
        string currency, IEnumerable<(long UserId, decimal Paid, decimal Owed)> shares, DateTime? date = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var payload = new Dictionary<string, object?>
        {
            ["cost"] = cost.ToString("0.00", inv),
            ["description"] = description,
            ["group_id"] = groupId,
            ["currency_code"] = currency
        };
        if (date.HasValue) payload["date"] = date.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

        int i = 0;
        foreach (var s in shares)
        {
            payload[$"users__{i}__user_id"] = s.UserId;
            payload[$"users__{i}__paid_share"] = s.Paid.ToString("0.00", inv);
            payload[$"users__{i}__owed_share"] = s.Owed.ToString("0.00", inv);
            i++;
        }
        return PostExpenseAsync(payload);
    }

    private async Task<long> PostExpenseAsync(Dictionary<string, object?> payload)
    {
        EnsureAuth();
        var resp = await _http.PostAsJsonAsync($"{Base}/api/v3.0/create_expense", payload);
        var body = await resp.Content.ReadAsStringAsync();

        // ATTENZIONE: 200 OK non garantisce successo. Va controllato l'array "errors".
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Object &&
            errors.EnumerateObject().Any())
        {
            throw new Exception($"Splitwise ha rifiutato la spesa: {errors.GetRawText()}");
        }

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"create_expense HTTP {(int)resp.StatusCode}: {body}");

        var expenses = root.GetProperty("expenses");
        return expenses.GetArrayLength() > 0
            ? expenses[0].GetProperty("id").GetInt64()
            : 0;
    }

    /// <summary>Elimina una spesa su Splitwise per id. Lancia eccezione se Splitwise riporta errori o "success" false.</summary>
    public async Task DeleteExpenseAsync(long expenseId)
    {
        EnsureAuth();
        var resp = await _http.PostAsync($"{Base}/api/v3.0/delete_expense/{expenseId}", null);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Object && errors.EnumerateObject().Any())
            throw new Exception($"Splitwise non ha eliminato la spesa: {errors.GetRawText()}");

        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new Exception($"Splitwise: eliminazione non riuscita. {body}");

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"delete_expense HTTP {(int)resp.StatusCode}: {body}");
    }

    private void EnsureAuth()
    {
        if (_accessToken is null)
            throw new InvalidOperationException("Chiama AuthenticateAsync() prima.");
    }
}
