using System.Text;
using System.Text.RegularExpressions;

namespace BlitzballTracker.Core.Parsing;

using BlitzballTracker.Core.GameState;

/// <summary>One parsed line of an exported chat log.</summary>
public sealed record LogLine(DateTime Timestamp, string? Channel, string Sender, string Message);

/// <summary>
/// Reads exported FFXIV chat logs and feeds them through <see cref="ChatParser"/>.
///
/// This lives in Core rather than the console app so that the plugin's replay
/// command and the regression tests share one implementation.
///
/// Real exports come in more than one shape, so several line formats are supported.
/// </summary>
public static partial class LogReplay
{
    /// <summary>[timestamp] [Channel] Sender (World): message</summary>
    [GeneratedRegex(@"^\[([^\]]+)\]\s+\[([^\]]+)\]\s+(?:([^:]+?):\s+)?(.+)$")]
    private static partial Regex RegexStandard();

    /// <summary>[timestamp] [Channel]&lt;Sender&gt; message  (linkshell / CWLS form)</summary>
    [GeneratedRegex(@"^\[([^\]]+)\]\s*\[([^\]]+)\]\s*<([^>]+)>\s*(.+)$")]
    private static partial Regex RegexLinkshell();

    /// <summary>[timestamp] Sender: message  (no channel column)</summary>
    [GeneratedRegex(@"^\[?([0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?|[0-9]{4}-[0-9]{2}-[0-9]{2}[^\]]*)\]?\s*([^:\[<][^:]{0,60}?):\s+(.+)$")]
    private static partial Regex RegexPlain();

    /// <summary>Channels that can carry game events.</summary>
    public static bool IsRelevantChannel(string? channel)
    {
        // An unknown channel means the export had no channel column. Those logs are
        // single-channel exports, so there is nothing to filter on.
        if (string.IsNullOrEmpty(channel)) return true;

        // CrossLinkShell carries the referee's phase calls, so a recording replays into
        // a full match only if it is kept.
        return channel is "Yell" or "Dice Roll" or "Field Marker" or "Shout"
                       or "CrossLinkShell" or "CWLS";
    }

    /// <summary>
    /// Parse a single raw log line, or null when it matches no known format.
    /// </summary>
    public static LogLine? ParseLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var line = raw.TrimEnd();

        // Linkshell form must be tried first: its channel bracket is followed
        // immediately by '<', which the standard pattern would mis-split.
        var m = RegexLinkshell().Match(line);
        if (m.Success)
            return Build(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);

        m = RegexStandard().Match(line);
        if (m.Success)
            return Build(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);

        m = RegexPlain().Match(line);
        if (m.Success)
            return Build(m.Groups[1].Value, null, m.Groups[2].Value, m.Groups[3].Value);

        return null;

        static LogLine Build(string time, string? channel, string sender, string message) => new(
            DateTime.TryParse(time, out var ts) ? ts : DateTime.Now,
            channel,
            PlayerNames.StripWorld(sender),
            message.Trim());
    }

    public static IEnumerable<LogLine> ReadLines(IEnumerable<string> rawLines)
    {
        foreach (var raw in rawLines)
        {
            var parsed = ParseLine(raw);
            if (parsed is not null)
                yield return parsed;
        }
    }

    /// <summary>
    /// Read a log file as UTF-8. Exports contain non-ASCII decoration
    /// (☆, ―, smart quotes) that is mangled under the system codepage.
    /// </summary>
    public static IEnumerable<LogLine> ReadFile(string path) =>
        ReadLines(File.ReadAllLines(path, Encoding.UTF8));

    /// <summary>
    /// Feed every relevant line through the parser. Returns how many were
    /// recognised as game events.
    /// </summary>
    public static int Replay(IEnumerable<LogLine> lines, ChatParser parser)
    {
        var recognized = 0;

        foreach (var line in lines)
        {
            if (!IsRelevantChannel(line.Channel)) continue;

            if (parser.ProcessMessage(line.Sender, line.Message, line.Timestamp))
                recognized++;
        }

        return recognized;
    }

    public static int ReplayFile(string path, ChatParser parser) =>
        Replay(ReadFile(path), parser);

    /// <summary>
    /// Guess which character recorded the log, by finding the action declarer
    /// immediately preceding a "You roll" line. Returns null when undetermined.
    /// </summary>
    public static string? DetectLocalPlayer(IEnumerable<LogLine> lines)
    {
        var materialized = lines as IReadOnlyList<LogLine> ?? lines.ToList();

        var actionSenders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in materialized)
        {
            if (LooksLikeActionDeclaration(line.Message))
                actionSenders.Add(line.Sender);
        }

        string? lastActionSender = null;
        foreach (var line in materialized)
        {
            if (actionSenders.Contains(line.Sender))
                lastActionSender = line.Sender;

            if (line.Message.Contains("You roll", StringComparison.OrdinalIgnoreCase) &&
                lastActionSender is not null)
            {
                return lastActionSender;
            }
        }

        return null;
    }

    private static readonly string[] ActionTags =
    [
        "[TACKLE", "[BLOCK", "[MOVE", "[DIVE", "[PASS", "[SHOOT",
        "[GUARD", "[TAUNT", "[RALLY", "[SHOVE", "[SURVEY", "[RUSH",
    ];

    public static bool LooksLikeActionDeclaration(string message)
    {
        foreach (var tag in ActionTags)
        {
            if (message.Contains(tag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
