namespace BlitzballTracker.Core.GameState;

/// <summary>
/// One player on a team sheet.
/// </summary>
public sealed class RosterEntry
{
    public string Name { get; set; } = string.Empty;
    public string? World { get; set; }
    public string Team { get; set; } = string.Empty;
    public PlayerRole Role { get; set; } = PlayerRole.None;

    public RosterEntry Clone() => new()
    {
        Name = Name,
        World = World,
        Team = Team,
        Role = Role,
    };
}

/// <summary>
/// The two team sheets for a match.
///
/// This exists because the roster cannot be recovered from chat. Logs carry no
/// structured lineup announcement, and roles only surface informally and partially
/// ("[GK+20 (Dazed)]", "Midfielder Beki Dotharl [Mateus]"). Without a roster the
/// parser has to guess who is playing, and at a real event the crowd shouting
/// "BLITZOFF!" gets mistaken for players.
/// </summary>
public sealed class Roster
{
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;

    // Settable rather than init-only so the plugin can round-trip saved rosters
    // through its JSON configuration.
    public List<RosterEntry> Entries { get; set; } = [];

    /// <summary>
    /// Who was at the keyboard when this was recorded, when it came from a recording.
    ///
    /// The game writes your own dice as "You roll a 40" and everybody else's by name,
    /// so a recording made by someone who is playing carries rolls that name nobody.
    /// Carrying the recorder's name alongside the lineup is what lets a replay put
    /// those rolls back on the right player.
    /// </summary>
    public string? RecordedBy { get; set; }

    /// <summary>
    /// Entries that actually name somebody.
    ///
    /// The editor pads both squads to six rows so the grid stays stable while typing,
    /// so a roster can hold twelve entries and still describe nobody.
    /// </summary>
    public int NamedCount => Entries.Count(e => !string.IsNullOrWhiteSpace(e.Name));

    public bool IsEmpty => NamedCount == 0;

    public Roster Clone() => new()
    {
        HomeTeam = HomeTeam,
        AwayTeam = AwayTeam,
        Entries = Entries.Select(e => e.Clone()).ToList(),
    };

    /// <summary>
    /// Human-readable problems with this roster.
    ///
    /// These are warnings, never hard errors. Real matches run short-handed:
    /// the Chocobowl log contains an actual mid-match disconnect
    /// ("We have an injury on the field... someone is DCing").
    /// </summary>
    public List<string> Validate()
    {
        var problems = new List<string>();

        // Not cosmetic: a player with no team has no goal to defend, so ResetPositions
        // cannot place them and they never reach the field at all.
        if (string.IsNullOrWhiteSpace(HomeTeam) || string.IsNullOrWhiteSpace(AwayTeam))
            problems.Add("Both team names must be set, or players cannot be placed on the field.");

        if (!string.IsNullOrWhiteSpace(HomeTeam) &&
            HomeTeam.Equals(AwayTeam, StringComparison.OrdinalIgnoreCase))
            problems.Add("Both teams have the same name.");

        var named = Entries.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();

        var duplicates = named
            .GroupBy(e => PlayerNames.Normalize(e.Name))
            .Where(g => g.Count() > 1)
            .Select(g => g.First().Name);
        foreach (var dup in duplicates)
            problems.Add($"'{dup}' appears more than once.");

        foreach (var team in new[] { HomeTeam, AwayTeam })
        {
            if (string.IsNullOrWhiteSpace(team)) continue;

            var squad = named
                .Where(e => e.Team.Equals(team, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (squad.Count == 0)
            {
                problems.Add($"{team} has no players.");
                continue;
            }

            if (squad.Count != 6)
                problems.Add($"{team} has {squad.Count} players (expected 6).");

            var keepers = squad.Count(e => e.Role == PlayerRole.Goalkeeper);
            if (keepers != 1)
                problems.Add($"{team} has {keepers} goalkeepers (expected 1).");

            var roleless = squad.Count(e => e.Role == PlayerRole.None);
            if (roleless > 0)
                problems.Add($"{team} has {roleless} player(s) with no role set.");
        }

        return problems;
    }

    /// <summary>
    /// Parse a pasted team sheet. Lineups are normally posted before a match,
    /// so pasting beats typing twelve names by hand.
    ///
    /// Recognised shapes (separator may be -, –, /, |, comma, or tab):
    ///   Daigoros                        &lt;- a bare line starts a new team
    ///   Beki Dotharl [Mateus] - M
    ///   GK: Jongleur Djrn
    ///   Soleil Mas / LF
    /// </summary>
    public static Roster ParseFromText(string text)
    {
        var roster = new Roster();
        if (string.IsNullOrWhiteSpace(text)) return roster;

        string? currentTeam = null;
        var teamsSeen = new List<string>();

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;

            var (namePart, rolePart) = SplitNameAndRole(line);

            var role = ParseRole(rolePart);

            // A line with no parseable role and no separator is a team header.
            if (role == PlayerRole.None && rolePart is null)
            {
                currentTeam = namePart.Trim();
                if (currentTeam.Length > 0 && !teamsSeen.Contains(currentTeam, StringComparer.OrdinalIgnoreCase))
                    teamsSeen.Add(currentTeam);
                continue;
            }

            var name = PlayerNames.StripWorld(namePart);
            if (name.Length == 0) continue;

            roster.Entries.Add(new RosterEntry
            {
                Name = name,
                World = PlayerNames.ExtractWorld(namePart),
                Team = currentTeam ?? string.Empty,
                Role = role,
            });
        }

        roster.HomeTeam = teamsSeen.ElementAtOrDefault(0) ?? string.Empty;
        roster.AwayTeam = teamsSeen.ElementAtOrDefault(1) ?? string.Empty;
        return roster;
    }

    /// <summary>
    /// Split "Beki Dotharl - M" or "GK: Jongleur Djrn" into name and role text.
    /// Returns a null role when the line carries no separator at all.
    /// </summary>
    private static (string Name, string? Role) SplitNameAndRole(string line)
    {
        // Leading "ROLE: Name" form.
        var colon = line.IndexOf(':');
        if (colon > 0)
        {
            var head = line[..colon].Trim();
            var tail = line[(colon + 1)..].Trim();
            if (ParseRole(head) != PlayerRole.None)
                return (tail, head);
            return (head, tail);
        }

        foreach (var sep in new[] { " - ", " – ", " — ", " / ", " | ", "\t", ", " })
        {
            var idx = line.LastIndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
                return (line[..idx].Trim(), line[(idx + sep.Length)..].Trim());
        }

        return (line, null);
    }

    public static PlayerRole ParseRole(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return PlayerRole.None;

        var t = s.Trim().Trim('(', ')', '[', ']', '.').ToUpperInvariant();

        return t switch
        {
            "GK" or "G" or "GOALIE" or "GOALKEEPER" or "KEEPER" => PlayerRole.Goalkeeper,
            "M" or "MID" or "MF" or "MIDFIELD" or "MIDFIELDER" => PlayerRole.Midfield,
            "LF" or "LEFT FORWARD" or "LEFTFORWARD" => PlayerRole.LeftForward,
            "RF" or "RIGHT FORWARD" or "RIGHTFORWARD" => PlayerRole.RightForward,
            "LD" or "LEFT DEFENDER" or "LEFTDEFENDER" or "LEFT DEFENSE" => PlayerRole.LeftDefender,
            "RD" or "RIGHT DEFENDER" or "RIGHTDEFENDER" or "RIGHT DEFENSE" => PlayerRole.RightDefender,
            _ => PlayerRole.None,
        };
    }

    public static string RoleAbbreviation(PlayerRole role) => role switch
    {
        PlayerRole.Goalkeeper => "GK",
        PlayerRole.Midfield => "M",
        PlayerRole.LeftForward => "LF",
        PlayerRole.RightForward => "RF",
        PlayerRole.LeftDefender => "LD",
        PlayerRole.RightDefender => "RD",
        _ => "-",
    };
}

/// <summary>
/// Fast, forgiving name lookup over a roster.
///
/// Resolution order: exact normalized match, then unique first-name match.
/// The first-name fallback only fires when exactly one roster player owns that
/// first name, so it can never silently pick the wrong player.
/// </summary>
public sealed class RosterIndex
{
    private readonly Dictionary<string, string> _byFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byFirstName = new(StringComparer.Ordinal);

    /// <summary>Canonical roster names, in roster order.</summary>
    public IReadOnlyList<string> Names { get; }

    public RosterIndex(IEnumerable<string> canonicalNames)
    {
        var names = new List<string>();
        var firstNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var name in canonicalNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            var key = PlayerNames.Normalize(name);
            if (key.Length == 0 || _byFullName.ContainsKey(key)) continue;

            _byFullName[key] = name;
            names.Add(name);

            var first = PlayerNames.FirstName(name);
            if (first.Length > 0)
                firstNameCounts[first] = firstNameCounts.GetValueOrDefault(first) + 1;
        }

        // Only index first names that are unambiguous across the whole roster.
        foreach (var name in names)
        {
            var first = PlayerNames.FirstName(name);
            if (first.Length > 0 && firstNameCounts[first] == 1)
                _byFirstName[first] = name;
        }

        Names = names;
    }

    /// <summary>
    /// Resolve a raw name from chat to a canonical roster name, or null if not on the roster.
    /// </summary>
    public string? Resolve(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;

        var key = PlayerNames.Normalize(rawName);
        if (key.Length == 0) return null;

        if (_byFullName.TryGetValue(key, out var exact))
            return exact;

        // Only try the first-name fallback for single-token inputs, so that a
        // full name belonging to someone off-roster never collapses onto a
        // roster player who happens to share a first name.
        if (!key.Contains(' ') && _byFirstName.TryGetValue(key, out var byFirst))
            return byFirst;

        return null;
    }

    public bool Contains(string? rawName) => Resolve(rawName) is not null;

    /// <summary>
    /// Resolve referee shorthand to a canonical roster name.
    ///
    /// Refs abbreviate heavily when calling corrections: "REROLL Mhin/Sata",
    /// "[[REROLL BORO vs KURAI]]". A closed twelve-player roster makes prefix
    /// matching safe, because uniqueness can be checked exactly. Falls back to
    /// <see cref="Resolve"/> first, and returns null when a prefix is ambiguous.
    /// </summary>
    public string? ResolveShorthand(string? rawName)
    {
        var exact = Resolve(rawName);
        if (exact is not null) return exact;

        if (string.IsNullOrWhiteSpace(rawName)) return null;

        var key = PlayerNames.Normalize(rawName);
        if (key.Length < 3) return null; // too short to disambiguate safely

        string? match = null;

        foreach (var name in Names)
        {
            var full = PlayerNames.Normalize(name);
            var first = PlayerNames.FirstName(name);

            if (!full.StartsWith(key, StringComparison.Ordinal) &&
                !first.StartsWith(key, StringComparison.Ordinal))
                continue;

            if (match is not null && !match.Equals(name, StringComparison.Ordinal))
                return null; // ambiguous prefix: refuse rather than guess

            match = name;
        }

        return match;
    }
}
