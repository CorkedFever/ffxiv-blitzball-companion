using System.Text.RegularExpressions;
using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;

namespace BlitzballTracker.App;

/// <summary>
/// Standalone Blitzball Tracker — parses game logs (txt/csv) and displays game state.
/// Can run live (tailing a file) or post-game (full file parse).
/// </summary>
public static partial class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Blitzball Tracker — Standalone Mode");
            Console.WriteLine("Usage:");
            Console.WriteLine("  BlitzballTracker.App <file.txt>          Parse a game log file");
            Console.WriteLine("  BlitzballTracker.App --live <file.txt>   Tail a log file in real-time");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  BlitzballTracker.App game_daigoros_vs_auspices.txt");
            return;
        }

        var liveMode = args.Contains("--live", StringComparer.OrdinalIgnoreCase);
        var playerName = GetArgValue(args, "--player");
        var playerRole = GetArgValue(args, "--role");
        var filePath = args.Last();

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            return;
        }

        var game = new BlitzGame();
        var parser = new ChatParser(game);

        if (liveMode)
        {
            await RunLiveMode(filePath, game, parser);
        }
        else
        {
            RunParseMode(filePath, game, parser, playerName, playerRole);
        }
    }

    /// <summary>
    /// Parse an entire game log file and display final state + stats.
    /// </summary>
    private static void RunParseMode(string filePath, BlitzGame game, ChatParser parser, string? playerName = null, string? playerRole = null)
    {
        var lines = File.ReadAllLines(filePath);
        var parsed = 0;

        // Use --player arg or auto-detect from "You roll" lines
        parser.LocalPlayerName = playerName ?? DetectLocalPlayer(lines);
        if (parser.LocalPlayerName != null)
            Console.WriteLine($"Local player: {parser.LocalPlayerName}");

        // Apply --role if provided
        if (parser.LocalPlayerName != null && playerRole != null)
        {
            var p = game.Players.GetValueOrDefault(parser.LocalPlayerName) ?? new PlayerState { Name = parser.LocalPlayerName };
            p.Role = ParseRole(playerRole);
            game.Players[parser.LocalPlayerName] = p;
        }

        foreach (var line in lines)
        {
            var match = RegexLogLine().Match(line);
            if (!match.Success) continue;

            var timeStr = match.Groups[1].Value;
            var channel = match.Groups[2].Value;
            var sender = StripWorld(match.Groups[3].Value);
            var message = match.Groups[4].Value;

            // Only parse Yell, Dice Roll, and Field Marker channels
            if (!IsRelevantChannel(channel)) continue;

            var timestamp = DateTime.TryParse(timeStr, out var ts) ? ts : DateTime.Now;
            if (parser.ProcessMessage(sender, message, timestamp))
                parsed++;
        }

        Console.WriteLine($"Parsed {lines.Length} lines, {parsed} game events recognized.\n");
        PrintGameState(game);
        Console.WriteLine();
        PrintStats(game);
        Console.WriteLine();
        PrintPlayByPlay(game);
    }

    /// <summary>
    /// Detect the local player by finding who declares actions near "You roll" lines.
    /// The local player is the one who posts action declarations AND has "You roll" entries.
    /// </summary>
    private static string? DetectLocalPlayer(string[] lines)
    {
        // Collect all senders who declare actions (contain [ACTION] brackets)
        var actionSenders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var match = RegexLogLine().Match(line);
            if (!match.Success) continue;
            var channel = match.Groups[2].Value;
            if (!channel.Equals("Yell", StringComparison.OrdinalIgnoreCase)) continue;
            var message = match.Groups[4].Value;
            // Check if this is an action declaration (contains [ACTION...])
            if (message.Contains("[TACKLE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[BLOCK", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[MOVE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[DIVE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[PASS", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[SHOOT", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[GUARD", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[TAUNT", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[RALLY", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[SHOVE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[SURVEY", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[RUSH", StringComparison.OrdinalIgnoreCase))
            {
                var sender = StripWorld(match.Groups[3].Value);
                actionSenders.Add(sender);
            }
        }

        // Now find who sent the most recent Yell (from action senders) before a "You roll"
        string? lastActionSender = null;
        foreach (var line in lines)
        {
            var match = RegexLogLine().Match(line);
            if (!match.Success) continue;
            var channel = match.Groups[2].Value;
            var sender = StripWorld(match.Groups[3].Value);
            var message = match.Groups[4].Value;

            if (channel.Equals("Yell", StringComparison.OrdinalIgnoreCase) && actionSenders.Contains(sender))
                lastActionSender = sender;

            if (channel.Equals("Dice Roll", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("You roll", StringComparison.OrdinalIgnoreCase) &&
                lastActionSender != null)
            {
                return lastActionSender;
            }
        }
        return null;
    }

    /// <summary>
    /// Tail a log file in real-time, printing updates as they arrive.
    /// </summary>
    private static async Task RunLiveMode(string filePath, BlitzGame game, ChatParser parser)
    {
        Console.WriteLine($"[LIVE] Watching: {filePath}");
        Console.WriteLine("[LIVE] Press Ctrl+C to stop.\n");

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        // Start at end of file for live mode
        fs.Seek(0, SeekOrigin.End);

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null)
            {
                await Task.Delay(100, cts.Token);
                continue;
            }

            var match = RegexLogLine().Match(line);
            if (!match.Success) continue;

            var channel = match.Groups[2].Value;
            if (!IsRelevantChannel(channel)) continue;

            var sender = StripWorld(match.Groups[3].Value);
            var message = match.Groups[4].Value;
            var timestamp = DateTime.TryParse(match.Groups[1].Value, out var ts) ? ts : DateTime.Now;

            if (parser.ProcessMessage(sender, message, timestamp))
            {
                // Print latest play-by-play entry
                if (game.PlayByPlay.Count > 0)
                {
                    var latest = game.PlayByPlay[^1];
                    var phaseLabel = game.Phase.ToString();
                    Console.WriteLine($"  [{phaseLabel}] {latest}");
                }
            }
        }

        Console.WriteLine("\n--- Final State ---");
        PrintGameState(game);
        PrintStats(game);
    }

    private static bool IsRelevantChannel(string channel)
    {
        return channel is "Yell" or "Dice Roll" or "Field Marker";
    }

    private static void PrintGameState(BlitzGame game)
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine($"  {game.HomeTeam,12} {game.Score.Home} : {game.Score.Away} {game.AwayTeam,-12}");
        Console.WriteLine($"  Set {game.Set} — Round {game.Round}/10 — {game.Phase}");
        Console.WriteLine($"  Ball: {game.BallCarrier ?? "contested"} ({game.BallTeam ?? "?"})");
        Console.WriteLine("═══════════════════════════════════════════");
    }

    private static void PrintStats(BlitzGame game)
    {
        if (game.Players.Count == 0) return;

        Console.WriteLine("\n┌─────────────────────────────┬──────┬──────┬─────────┬────────┬────────┬────────┐");
        Console.WriteLine("│ Player                      │ Team │ Role │ Actions │ Succ % │ AvgRol │ Status │");
        Console.WriteLine("├─────────────────────────────┼──────┼──────┼─────────┼────────┼────────┼────────┤");

        foreach (var p in game.Players.Values.OrderBy(p => p.Team).ThenBy(p => p.Name))
        {
            var succPct = p.ActionsAttempted > 0 ? $"{p.SuccessRate:P0}" : "  -  ";
            var avg = p.TotalRolls > 0 ? $"{p.RollAverage,5:F1}" : "  -  ";
            var status = p.IsDazed ? "DAZED" : p.HasBall ? "BALL " : " OK  ";
            var acts = p.ActionsAttempted > 0 ? $"{p.ActionsSucceeded}/{p.ActionsAttempted}" : "  -  ";
            var role = RoleLabel(p.Role);

            Console.WriteLine($"│ {p.Name,-27} │ {p.Team,-4} │ {role,-4} │ {acts,-7} │ {succPct,-6} │ {avg,-6} │ {status} │");
        }

        Console.WriteLine("└─────────────────────────────┴──────┴──────┴─────────┴────────┴────────┴────────┘");
    }

    private static string RoleLabel(PlayerRole role) => role switch
    {
        PlayerRole.Midfield => "M",
        PlayerRole.LeftForward => "LF",
        PlayerRole.RightForward => "RF",
        PlayerRole.LeftDefender => "LD",
        PlayerRole.RightDefender => "RD",
        PlayerRole.Goalkeeper => "GK",
        _ => "  ",
    };

    private static void PrintPlayByPlay(BlitzGame game, int lastN = 20)
    {
        if (game.PlayByPlay.Count == 0) return;

        Console.WriteLine($"\nPlay-by-Play (last {Math.Min(lastN, game.PlayByPlay.Count)}):");
        Console.WriteLine("───────────────────────────────────────────");
        var start = Math.Max(0, game.PlayByPlay.Count - lastN);
        for (var i = start; i < game.PlayByPlay.Count; i++)
        {
            Console.WriteLine($"  {game.PlayByPlay[i]}");
        }
    }

    // Matches: [2026-06-01 22:05:42] [Yell] O'looqa Honji (Balmung): message here
    // Also handles no-sender system messages: [time] [channel] message
    [GeneratedRegex(@"^\[([^\]]+)\]\s+\[([^\]]+)\]\s+(?:([^:]+?):\s+)?(.+)$")]
    private static partial Regex RegexLogLine();

    // Strips world name from sender: "O'looqa Honji (Balmung)" -> "O'looqa Honji"
    [GeneratedRegex(@"\s+\([^)]+\)$")]
    private static partial Regex RegexWorldSuffix();

    private static string StripWorld(string sender)
    {
        return RegexWorldSuffix().Replace(sender, "").Trim();
    }

    private static string? GetArgValue(string[] args, string key)
    {
        var idx = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    private static PlayerRole ParseRole(string s) => s.ToUpperInvariant() switch
    {
        "M" or "MID" or "MIDFIELD" => PlayerRole.Midfield,
        "LF" => PlayerRole.LeftForward,
        "RF" => PlayerRole.RightForward,
        "LD" => PlayerRole.LeftDefender,
        "RD" => PlayerRole.RightDefender,
        "GK" => PlayerRole.Goalkeeper,
        _ => PlayerRole.None,
    };
}
