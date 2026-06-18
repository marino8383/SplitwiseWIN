namespace SplitwiseUploader;

/// <summary>
/// Calcola i tag di un movimento dalle regole "parola -> tag" (match per sottostringa, multi-tag).
/// I tag non sono memorizzati: si ricavano al volo dalla descrizione, così cambiando le regole
/// le statistiche si aggiornano subito.
/// </summary>
public static class TagEngine
{
    public const string Untagged = "(senza tag)";

    public static List<string> TagsFor(string? description, IReadOnlyList<(string Keyword, string Tag)> rules)
    {
        var d = (description ?? "").ToUpperInvariant();
        var tags = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Keyword) && d.Contains(r.Keyword.ToUpperInvariant()))
            .Select(r => r.Tag.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tags.Count == 0) tags.Add(Untagged);
        return tags;
    }
}
