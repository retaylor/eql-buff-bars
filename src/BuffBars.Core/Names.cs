namespace BuffBars.Core;

public static class Names
{
    /// <summary>
    /// Canonical key for an actor name: leading article stripped (the client is inconsistent
    /// about "a"/"A"), lowercased. "A Teir`Dal ranger" and "a Teir`Dal ranger" collide.
    /// </summary>
    public static string Key(string display)
    {
        var s = display.Trim();
        foreach (var art in (ReadOnlySpan<string>)["a ", "an ", "the ", "A ", "An ", "The "])
        {
            if (s.StartsWith(art, StringComparison.Ordinal)) { s = s[art.Length..]; break; }
        }
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Heuristic: player names are a single capitalized word with no spaces or articles.
    /// NPCs have articles, spaces, or "pet" suffixes.
    /// </summary>
    public static bool IsLikelyPlayer(string display)
    {
        var s = display.Trim();
        if (s.Length == 0 || s.Contains(' ')) return false;
        return char.IsUpper(s[0]);
    }

    public static string StripArticleForDisplay(string display)
    {
        var s = display.Trim();
        foreach (var art in (ReadOnlySpan<string>)["a ", "an ", "the ", "A ", "An ", "The "])
        {
            if (s.StartsWith(art, StringComparison.Ordinal)) return s[art.Length..];
        }
        return s;
    }
}
