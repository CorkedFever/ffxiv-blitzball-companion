using System.Text;

namespace BlitzballTracker.Core.GameState;

/// <summary>
/// Name normalization shared by the roster and the chat parser.
///
/// Real logs are messy: names arrive with trailing spaces before the colon
/// ("Tuasun Chaontis :"), with world suffixes ("Beki Dotharl [Mateus]"),
/// with non-breaking spaces, and occasionally wrapped in smart quotes.
/// Everything that compares names must go through here.
/// </summary>
public static class PlayerNames
{
    /// <summary>
    /// Remove a trailing world suffix: "Beki Dotharl [Mateus]" becomes "Beki Dotharl".
    /// Also handles the parenthesised form some refs use.
    /// </summary>
    public static string StripWorld(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var s = raw.Trim();

        // A crossworld name arrives as "Name<glyph>World", the glyph being one of the
        // game's own icons. Those live in the private use area, so cutting at the first
        // one strips the world without needing to know which icon was used — and there
        // are several. This is the lucky case; the line below handles the usual one.
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is < '' or > '') continue;

            s = s[..i].TrimEnd();
            break;
        }

        // More often the glyph is already gone: it is a payload rather than a character,
        // and flattening a chat line to text drops it. What arrives is "Akii
        // MalaguldCactuar", with nothing at all marking where the name ends, so the world
        // has to be recognised by name instead. See Worlds.Split.
        s = Worlds.Split(s).Name;

        var bracket = s.LastIndexOf('[');
        if (bracket > 0 && s.EndsWith(']'))
            s = s[..bracket];

        var paren = s.LastIndexOf('(');
        if (paren > 0 && s.EndsWith(')'))
            s = s[..paren];

        return s.Trim();
    }

    /// <summary>
    /// Extract the world from "Beki Dotharl [Mateus]", or null when absent.
    /// </summary>
    public static string? ExtractWorld(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        var s = raw.Trim();
        var bracket = s.LastIndexOf('[');
        if (bracket >= 0 && s.EndsWith(']') && bracket + 1 < s.Length - 1)
        {
            var world = s[(bracket + 1)..^1].Trim();
            return world.Length > 0 ? world : null;
        }

        // The common case in live chat: no brackets, the world simply welded on the end.
        return Worlds.Split(s).World;
    }

    /// <summary>
    /// Canonical comparison key: world stripped, decorations removed,
    /// internal whitespace collapsed, lowercased.
    /// </summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var s = StripWorld(raw);

        var sb = new StringBuilder(s.Length);
        var lastWasSpace = false;

        foreach (var ch in s)
        {
            // Normalize every space-like character to a plain space.
            if (char.IsWhiteSpace(ch) || ch == ' ' || ch == ' ' || ch == ' ')
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            // Drop quotes and stray punctuation that logs pick up at the edges.
            // Apostrophes and hyphens are kept: they are load-bearing in FFXIV names
            // (K'yriss Arashito, Deslandes Ebon'sky, Abd-al-daiya).
            if (ch is '"' or '“' or '”' or '‘' or '’' or '“' or '”'
                   or ':' or ',' or '!' or '?' or '.' or '*')
                continue;

            sb.Append(char.ToLowerInvariant(ch));
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// First name only, normalized. Used for the unique-first-name fallback match.
    /// </summary>
    public static string FirstName(string raw)
    {
        var normalized = Normalize(raw);
        var space = normalized.IndexOf(' ');
        return space > 0 ? normalized[..space] : normalized;
    }
}
