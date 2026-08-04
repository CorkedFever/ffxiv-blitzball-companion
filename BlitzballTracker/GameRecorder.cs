using Dalamud.Game.Text;

namespace BlitzballTracker;

/// <summary>
/// Records blitzball game chat to a log file in the format expected by the web replay parser.
/// Output format: [yyyy-MM-dd HH:mm:ss] [Channel] Sender: message
/// </summary>
public sealed class GameRecorder : IDisposable
{
    private StreamWriter? _writer;
    private string? _filePath;
    private bool _recording;

    public bool IsRecording => _recording;
    public string? CurrentFile => _filePath;
    public int LinesRecorded { get; private set; }

    /// <summary>
    /// Start recording to a new file in the specified directory.
    ///
    /// When a roster is supplied it is written into the file's header, so the
    /// recording describes itself. Without that, an old log cannot be fully replayed
    /// later, because the lineup is the one thing chat never carries.
    /// </summary>
    public string Start(string outputDir, Core.GameState.Roster? roster = null)
    {
        Stop(); // close any existing recording

        Directory.CreateDirectory(outputDir);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        _filePath = Path.Combine(outputDir, $"blitzball_{timestamp}.txt");
        _writer = new StreamWriter(_filePath, append: false, encoding: System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };

        if (roster is { Entries.Count: > 0 })
            _writer.Write(Core.Parsing.RosterHeader.Write(roster));

        _recording = true;
        LinesRecorded = 0;

        return _filePath;
    }

    /// <summary>Write a chat line in the standard log format.</summary>
    public void Write(XivChatType type, string sender, string message)
    {
        if (!_recording || _writer == null) return;

        var channel = GetChannelName(type);
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _writer.WriteLine($"[{ts}] [{channel}] {sender}: {message}");
        LinesRecorded++;
    }

    /// <summary>Stop recording and close the file.</summary>
    public void Stop()
    {
        if (_writer != null)
        {
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
        }
        _recording = false;
    }

    public void Dispose() => Stop();

    private static string GetChannelName(XivChatType type)
    {
        var code = (ushort)type & 0xFF;
        return code switch
        {
            0x1E => "Yell",
            0x4A => "Dice Roll",
            0x49 or 0xC9 => "Field Marker",
            0x0E => "Party",
            0x0A => "Say",
            _ => $"Chat_{code:X2}",
        };
    }
}
