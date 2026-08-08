namespace BlitzballTracker.Core.GameState;

/// <summary>
/// Every FFXIV world, so a world can be told from a surname.
///
/// A crossworld name reaches the plugin as "Akii MalaguldCactuar" — no separator
/// at all. The game draws a world icon between the two, but that icon is a payload
/// rather than a character, and flattening a chat line to text drops it, welding the
/// world onto the surname. Nothing in the shape of the string says where the name
/// ends, so the only way to cut it is to recognise the world by name.
///
/// EBL is cross-world by nature: a single match here drew on eleven different worlds.
/// Getting this wrong does not lose one player, it loses everyone not on your own
/// world, which is nearly everyone.
/// </summary>
public static class Worlds
{
    /// <summary>
    /// Indexed by name for lookup, ordinal-cased because worlds are always written
    /// exactly as the game writes them.
    /// </summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        // North America — Aether, Primal, Crystal, Dynamis
        "Adamantoise", "Cactuar", "Faerie", "Gilgamesh",
        "Jenova", "Midgardsormr", "Sargatanas", "Siren",
        "Behemoth", "Excalibur", "Exodus", "Famfrit",
        "Hyperion", "Lamia", "Leviathan", "Ultros",
        "Balmung", "Brynhildr", "Coeurl", "Diabolos",
        "Goblin", "Malboro", "Mateus", "Zalera",
        "Halicarnassus", "Maduin", "Marilith", "Seraph",
        "Cuchulainn", "Golem", "Kraken", "Rafflesia",

        // Europe — Chaos, Light
        "Cerberus", "Louisoix", "Moogle", "Omega",
        "Phantom", "Ragnarok", "Sagittarius", "Spriggan",
        "Alpha", "Lich", "Odin", "Phoenix",
        "Raiden", "Shiva", "Twintania", "Zodiark",

        // Oceania — Materia
        "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan",

        // Japan — Elemental, Gaia, Mana, Meteor
        "Aegis", "Atomos", "Carbuncle", "Garuda",
        "Gungnir", "Kujata", "Tonberry", "Typhon",
        "Alexander", "Bahamut", "Durandal", "Fenrir",
        "Ifrit", "Ridill", "Tiamat", "Ultima",
        "Anima", "Asura", "Chocobo", "Hades",
        "Ixion", "Masamune", "Pandaemonium", "Titan",
        "Belias", "Mandragora", "Ramuh", "Shinryu",
        "Unicorn", "Valefor", "Yojimbo", "Zeromus",
    };

    public static bool IsWorld(string candidate) =>
        candidate.Length > 0 && Known.Contains(candidate);

    /// <summary>
    /// Split a fused "NameWorld" into its parts, or return the whole string as the
    /// name when no world is on the end.
    ///
    /// The guard is that whatever remains has to still look like a character name —
    /// two words. Several worlds are ordinary enough words to be a surname (Titan,
    /// Phoenix, Shiva, Golem), and without the guard a player really called
    /// "Rielle Phoenix" would be cut down to "Rielle" and then match nobody. Requiring
    /// a first and last name left over means only a genuine third component is taken.
    /// </summary>
    public static (string Name, string? World) Split(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return (string.Empty, null);

        var s = raw.Trim();

        // Walk back from the end to find where a capitalised trailing word starts.
        // Both spellings end up here: "Akii MalaguldCactuar" and "Akii Malaguld Cactuar".
        for (var i = s.Length - 1; i > 0; i--)
        {
            if (!char.IsUpper(s[i])) continue;

            var tail = s[i..];
            if (!Known.Contains(tail)) continue;

            var head = s[..i].TrimEnd();
            if (LooksLikeAFullName(head))
                return (head, tail);
        }

        return (s, null);
    }

    /// <summary>
    /// Two words or more. FFXIV forenames and surnames are both mandatory, so anything
    /// shorter is a fragment rather than a name — which is exactly what a bad cut
    /// produces.
    /// </summary>
    private static bool LooksLikeAFullName(string candidate) =>
        candidate.Length > 0 && candidate.Contains(' ');
}
