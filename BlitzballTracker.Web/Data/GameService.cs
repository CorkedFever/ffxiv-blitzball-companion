using System.Text.RegularExpressions;
using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;

namespace BlitzballTracker.Web.Data;

/// <summary>
/// Singleton service that holds the current game state and provides parsing.
/// Supports instant parse and step-by-step replay.
/// </summary>
public partial class GameService
{
    public BlitzGame Game { get; private set; } = new();
    public ChatParser Parser { get; private set; }
    public int LinesProcessed { get; private set; }
    public int EventsRecognized { get; private set; }
    public string? FileName { get; private set; }

    // ── Stored file content (survives across tabs) ──
    public string? StoredContent { get; private set; }
    private string? _detectedLocalPlayer;

    // ── Replay state ──
    private ParsedLine[] _replayLines = [];
    private CancellationTokenSource? _replayCts;
    public int ReplayIndex { get; private set; }
    public int ReplayTotal => _replayLines.Length;
    public bool IsReplaying { get; private set; }
    public bool IsPaused { get; private set; }
    public bool HasReplayData => _replayLines.Length > 0;
    public bool HasFile => StoredContent != null;
    public int ReplayDelayMs { get; set; } = 400;
    public string? LastRawLine { get; private set; }

    public event Action? OnStateChanged;

    /// <summary>Notify all subscribers that game state has changed (for manual edits).</summary>
    public void NotifyStateChanged() => OnStateChanged?.Invoke();

    /// <summary>Process a single live message from the plugin and notify subscribers.</summary>
    public bool ProcessLiveMessage(string sender, string message, DateTime timestamp)
    {
        var recognized = Parser.ProcessMessage(sender, message, timestamp);
        if (recognized) EventsRecognized++;
        LinesProcessed++;
        OnStateChanged?.Invoke();
        return recognized;
    }

    public GameService()
    {
        Parser = new ChatParser(Game);
    }

    public void Reset()
    {
        StopReplay();
        Game = new BlitzGame();
        Parser = new ChatParser(Game);
        LinesProcessed = 0;
        EventsRecognized = 0;
        FileName = null;
        StoredContent = null;
        _detectedLocalPlayer = null;
        _replayLines = [];
        ReplayIndex = 0;
        LastRawLine = null;
        OnStateChanged?.Invoke();
    }

    /// <summary>Parse the entire log at once and store content for replay.</summary>
    public void ParseLogFile(string content, string fileName, string? localPlayerName = null, PlayerRole? localPlayerRole = null)
    {
        Reset();
        FileName = fileName;
        StoredContent = content;

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (localPlayerName == null)
            localPlayerName = DetectLocalPlayer(lines);

        _detectedLocalPlayer = localPlayerName;
        Parser.LocalPlayerName = localPlayerName;

        if (localPlayerName != null && localPlayerRole != null)
        {
            var p = new PlayerState { Name = localPlayerName, Role = localPlayerRole.Value };
            Game.Players[localPlayerName] = p;
        }

        foreach (var line in lines)
        {
            var match = RegexLogLine().Match(line);
            if (match.Success)
            {
                var channel = match.Groups[2].Value;
                if (!IsRelevantChannel(channel)) continue;

                var timeStr = match.Groups[1].Value;
                var sender = StripWorld(ExtractSender(match));
                var message = ExtractMessage(match);
                var timestamp = DateTime.TryParse(timeStr, out var ts) ? ts : DateTime.Now;

                if (Parser.ProcessMessage(sender, message, timestamp))
                    EventsRecognized++;
                LinesProcessed++;
            }
            else
            {
                var diceMatch = RegexDiceRollLine().Match(line);
                if (diceMatch.Success)
                {
                    var timestamp = DateTime.TryParse(diceMatch.Groups[1].Value, out var ts) ? ts : DateTime.Now;
                    if (Parser.ProcessMessage("System", diceMatch.Groups[2].Value, timestamp))
                        EventsRecognized++;
                    LinesProcessed++;
                }
            }
        }

        OnStateChanged?.Invoke();
    }

    // ── Roster discovery (pre-parse) ──

    private record DiscoveredRoster(
        string HomeTeam,
        string AwayTeam,
        List<(string Name, string Team, PlayerRole Role)> Players);

    /// <summary>Full-parse the log on a throwaway game to discover all players, teams, and roles.</summary>
    private DiscoveredRoster DiscoverRoster(string[] rawLines, string? localPlayerName)
    {
        var tempGame = new BlitzGame();
        var tempParser = new ChatParser(tempGame) { LocalPlayerName = localPlayerName };

        foreach (var line in rawLines)
        {
            var match = RegexLogLine().Match(line);
            if (match.Success)
            {
                var channel = match.Groups[2].Value;
                if (!IsRelevantChannel(channel)) continue;
                var sender = StripWorld(ExtractSender(match));
                var message = ExtractMessage(match);
                var timestamp = DateTime.TryParse(match.Groups[1].Value, out var ts) ? ts : DateTime.Now;
                tempParser.ProcessMessage(sender, message, timestamp);
            }
            else
            {
                var diceMatch = RegexDiceRollLine().Match(line);
                if (diceMatch.Success)
                {
                    var timestamp = DateTime.TryParse(diceMatch.Groups[1].Value, out var ts) ? ts : DateTime.Now;
                    tempParser.ProcessMessage("System", diceMatch.Groups[2].Value, timestamp);
                }
            }
        }

        var roster = tempGame.Players.Values
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .Select(p => (p.Name, p.Team, p.Role))
            .ToList();

        return new DiscoveredRoster(tempGame.HomeTeam, tempGame.AwayTeam, roster);
    }

    /// <summary>Seed the current game with a pre-discovered roster so players appear at starting positions.</summary>
    private void SeedRoster(DiscoveredRoster roster)
    {
        Game.HomeTeam = roster.HomeTeam;
        Game.AwayTeam = roster.AwayTeam;

        foreach (var (name, team, role) in roster.Players)
        {
            var p = new PlayerState { Name = name, Team = team, Role = role };
            Game.Players[name] = p;
        }

        // Place everyone at starting positions
        Game.ResetPositions();
    }

    // ── Replay engine ──

    private record ParsedLine(string Raw, string TimeStr, string Channel, string Sender, string Message);

    /// <summary>Load a log file for replay without processing any lines yet.</summary>
    public void LoadForReplay(string content, string fileName, string? localPlayerName = null, PlayerRole? localPlayerRole = null)
    {
        Reset();
        FileName = fileName;
        StoredContent = content;

        var rawLines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (localPlayerName == null)
            localPlayerName = DetectLocalPlayer(rawLines);

        _detectedLocalPlayer = localPlayerName;
        Parser.LocalPlayerName = localPlayerName;

        // Pre-parse the entire log to discover all players, teams, and roles
        var roster = DiscoverRoster(rawLines, localPlayerName);
        SeedRoster(roster);

        // Pre-parse and filter to relevant lines only
        var parsed = new List<ParsedLine>();
        foreach (var line in rawLines)
        {
            var match = RegexLogLine().Match(line);
            if (match.Success)
            {
                var channel = match.Groups[2].Value;
                if (!IsRelevantChannel(channel)) continue;
                parsed.Add(new ParsedLine(
                    line,
                    match.Groups[1].Value,
                    channel,
                    StripWorld(ExtractSender(match)),
                    ExtractMessage(match)));
            }
            else
            {
                var diceMatch = RegexDiceRollLine().Match(line);
                if (diceMatch.Success)
                {
                    parsed.Add(new ParsedLine(
                        line,
                        diceMatch.Groups[1].Value,
                        "Dice Roll",
                        "System",
                        diceMatch.Groups[2].Value));
                }
            }
        }
        _replayLines = parsed.ToArray();
        ReplayIndex = 0;

        OnStateChanged?.Invoke();
    }

    /// <summary>Process the next line in the replay.</summary>
    public bool StepForward()
    {
        if (ReplayIndex >= _replayLines.Length) return false;

        var pl = _replayLines[ReplayIndex];
        LastRawLine = pl.Raw;
        var timestamp = DateTime.TryParse(pl.TimeStr, out var ts) ? ts : DateTime.Now;

        if (Parser.ProcessMessage(pl.Sender, pl.Message, timestamp))
            EventsRecognized++;
        LinesProcessed++;
        ReplayIndex++;

        OnStateChanged?.Invoke();
        return ReplayIndex < _replayLines.Length;
    }

    /// <summary>Start auto-playing the replay.</summary>
    public async Task StartReplay()
    {
        if (_replayLines.Length == 0) return;
        if (IsReplaying && !IsPaused) return;

        IsReplaying = true;
        IsPaused = false;
        _replayCts = new CancellationTokenSource();
        var token = _replayCts.Token;

        OnStateChanged?.Invoke();

        try
        {
            while (ReplayIndex < _replayLines.Length && !token.IsCancellationRequested)
            {
                StepForward();
                await Task.Delay(ReplayDelayMs, token);
            }
        }
        catch (TaskCanceledException) { }

        if (!IsPaused)
        {
            IsReplaying = false;
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>Pause auto-play.</summary>
    public void PauseReplay()
    {
        IsPaused = true;
        _replayCts?.Cancel();
        OnStateChanged?.Invoke();
    }

    /// <summary>Stop and reset replay to the beginning.</summary>
    public void StopReplay()
    {
        _replayCts?.Cancel();
        _replayCts?.Dispose();
        _replayCts = null;
        IsReplaying = false;
        IsPaused = false;
    }

    /// <summary>Skip to end — process all remaining lines instantly.</summary>
    public void SkipToEnd()
    {
        StopReplay();
        while (ReplayIndex < _replayLines.Length)
        {
            var pl = _replayLines[ReplayIndex];
            LastRawLine = pl.Raw;
            var timestamp = DateTime.TryParse(pl.TimeStr, out var ts) ? ts : DateTime.Now;
            if (Parser.ProcessMessage(pl.Sender, pl.Message, timestamp))
                EventsRecognized++;
            LinesProcessed++;
            ReplayIndex++;
        }
        OnStateChanged?.Invoke();
    }

    /// <summary>Reset game state and prepare replay from stored content.</summary>
    public void RestartReplay()
    {
        if (StoredContent == null || FileName == null) return;

        var savedContent = StoredContent;
        var savedFileName = FileName;

        StopReplay();
        Game = new BlitzGame();
        Parser = new ChatParser(Game);
        LinesProcessed = 0;
        EventsRecognized = 0;
        LastRawLine = null;

        // Restore stored data
        StoredContent = savedContent;
        FileName = savedFileName;
        Parser.LocalPlayerName = _detectedLocalPlayer;

        // Pre-parse the entire log to discover roster, then seed
        var rawLines = savedContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var roster = DiscoverRoster(rawLines, _detectedLocalPlayer);
        SeedRoster(roster);

        // Re-parse lines for replay
        var parsed = new List<ParsedLine>();
        foreach (var line in rawLines)
        {
            var match = RegexLogLine().Match(line);
            if (!match.Success) continue;
            var channel = match.Groups[2].Value;
            if (!IsRelevantChannel(channel)) continue;
            parsed.Add(new ParsedLine(
                line,
                match.Groups[1].Value,
                channel,
                StripWorld(ExtractSender(match)),
                ExtractMessage(match)));
        }
        _replayLines = parsed.ToArray();
        ReplayIndex = 0;

        OnStateChanged?.Invoke();
    }

    private string? DetectLocalPlayer(string[] lines)
    {
        var actionSenders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var match = RegexLogLine().Match(line);
            if (!match.Success) continue;
            var channel = match.Groups[2].Value;
            // In old format, only Yell has actions; in new format, no channel = public
            if (!channel.Equals("Yell", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(channel)) continue;
            var message = ExtractMessage(match);
            if (message.Contains("[TACKLE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[BLOCK", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[MOVE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[DIVE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[SHOOT", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[GUARD", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[TAUNT", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[RALLY", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[SHOVE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[SURVEY", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[RUSH", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[PASS", StringComparison.OrdinalIgnoreCase))
            {
                actionSenders.Add(StripWorld(ExtractSender(match)));
            }
        }

        string? lastActionSender = null;
        foreach (var line in lines)
        {
            var match = RegexLogLine().Match(line);
            if (match.Success)
            {
                var channel = match.Groups[2].Value;
                var sender = StripWorld(ExtractSender(match));

                if (channel.Equals("Yell", StringComparison.OrdinalIgnoreCase) && actionSenders.Contains(sender))
                    lastActionSender = sender;
            }
            else
            {
                var diceMatch = RegexDiceRollLine().Match(line);
                if (diceMatch.Success &&
                    diceMatch.Groups[2].Value.Contains("You roll", StringComparison.OrdinalIgnoreCase) &&
                    lastActionSender != null)
                    return lastActionSender;
            }
        }
        return null;
    }

    private static bool IsRelevantChannel(string channel) =>
        string.IsNullOrEmpty(channel) || // no channel = public (Say/Yell/Emote)
        channel.Equals("Yell", StringComparison.OrdinalIgnoreCase) ||
        channel.Equals("Say", StringComparison.OrdinalIgnoreCase) ||
        channel.Equals("Dice Roll", StringComparison.OrdinalIgnoreCase) ||
        channel.StartsWith("CWLS", StringComparison.OrdinalIgnoreCase) ||
        channel.StartsWith("Field Marker", StringComparison.OrdinalIgnoreCase);

    // Handles both formats:
    //   [2026-06-01 22:05:42] [Yell] Sender (World): message
    //   [18:30] Sender: message
    //   [18:18] [CWLS6]<Sender> message
    [GeneratedRegex(@"^\[([^\]]+)\]\]?\s+(?:\[([^\]]+)\]\s*)?(?:<([^>]+)>\s*|([^:]+?)\s*:\s+)(.+)$")]
    private static partial Regex RegexLogLine();

    // Dice Roll system messages have no sender:message format:
    //   [2026-06-01 22:07:04] [Dice Roll] Random! Player rolls a 98 (out of 100).
    [GeneratedRegex(@"^\[([^\]]+)\]\s+\[Dice Roll\]\s+(.+)$")]
    private static partial Regex RegexDiceRollLine();

    /// <summary>Extract sender from a log line match (group 3 = angle-bracket, group 4 = colon format).</summary>
    private static string ExtractSender(Match match) =>
        !string.IsNullOrEmpty(match.Groups[3].Value) ? match.Groups[3].Value : match.Groups[4].Value;

    /// <summary>Extract message from a log line match (group 5).</summary>
    private static string ExtractMessage(Match match) => match.Groups[5].Value;

    [GeneratedRegex(@"\s+\([^)]+\)$")]
    private static partial Regex RegexWorldSuffix();

    private static string StripWorld(string sender) =>
        RegexWorldSuffix().Replace(sender, "").Trim();
}
