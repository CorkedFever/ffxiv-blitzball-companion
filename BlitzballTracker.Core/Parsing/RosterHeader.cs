using System.Text;

namespace BlitzballTracker.Core.Parsing;

using BlitzballTracker.Core.GameState;

/// <summary>
/// Stores the roster inside a recording, as comment lines above the chat.
///
/// A log without its lineup cannot be fully understood later: the roster is exactly
/// the thing chat never carries, and nobody remembers who played which role months
/// afterwards. Writing it into the file makes a recording self-describing.
///
/// Lines are prefixed with '#', which <see cref="LogReplay.ParseLine"/> ignores, so
/// existing playback is unaffected.
/// </summary>
public static class RosterHeader
{
    private const string Prefix = "#blitz ";

    /// <summary>
    /// Write the lineup, and who was recording it.
    ///
    /// <paramref name="localPlayer"/> matters more than it looks. The game writes your
    /// own dice as "You roll a 40" and everyone else's by name, so a recording made by
    /// somebody who is playing has a stack of rolls in it that name nobody. Live that is
    /// fine — the plugin knows who you are — but nothing in the file does, and by the
    /// time anyone replays it the rolls are unattributable for good. One line fixes it.
    /// </summary>
    public static string Write(Roster roster, string? localPlayer = null)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"{Prefix}version 1");
        builder.AppendLine($"{Prefix}home {roster.HomeTeam}");
        builder.AppendLine($"{Prefix}away {roster.AwayTeam}");

        if (!string.IsNullOrWhiteSpace(localPlayer))
            builder.AppendLine($"{Prefix}you {localPlayer}");

        foreach (var entry in roster.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;

            builder.AppendLine(
                $"{Prefix}player {entry.Name}|{entry.World ?? string.Empty}|" +
                $"{entry.Team}|{Roster.RoleAbbreviation(entry.Role)}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Recover a roster from a recording's header, or null when it has none.
    /// </summary>
    public static Roster? Read(IEnumerable<string> lines)
    {
        Roster? roster = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimStart();

            // The header sits at the top; stop as soon as real chat begins.
            if (!line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                break;
            }

            var body = line[Prefix.Length..].Trim();
            var space = body.IndexOf(' ');
            if (space <= 0) continue;

            var key = body[..space];
            var value = body[(space + 1)..].Trim();

            roster ??= new Roster();

            switch (key.ToLowerInvariant())
            {
                case "home":
                    roster.HomeTeam = value;
                    break;

                case "away":
                    roster.AwayTeam = value;
                    break;

                case "you":
                    roster.RecordedBy = value;
                    break;

                case "player":
                    var parts = value.Split('|');
                    if (parts.Length < 4) break;

                    roster.Entries.Add(new RosterEntry
                    {
                        Name = parts[0].Trim(),
                        World = parts[1].Trim().Length == 0 ? null : parts[1].Trim(),
                        Team = parts[2].Trim(),
                        Role = Roster.ParseRole(parts[3]),
                    });
                    break;
            }
        }

        return roster is { Entries.Count: > 0 } ? roster : null;
    }

    public static Roster? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Read(File.ReadLines(path, Encoding.UTF8)) : null;
        }
        catch
        {
            return null;
        }
    }
}
