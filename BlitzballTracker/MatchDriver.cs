using Dalamud.Plugin.Services;

namespace BlitzballTracker;

using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;

/// <summary>
/// Owns playback: simulated matches, recorded replays, and the practice arena.
///
/// This lives apart from the plugin entry point so the UI can drive it directly.
/// Previously all of it hung off slash-command handlers, which meant the only way
/// to use any of it was to type a command.
/// </summary>
public sealed class MatchDriver(
    BlitzGame state,
    ChatParser parser,
    DemoDirector demo,
    IPluginLog log,
    string recordingsDirectory)
{
    private readonly BlitzGame _state = state;
    private readonly ChatParser _parser = parser;
    private readonly DemoDirector _demo = demo;
    private readonly IPluginLog _log = log;
    private readonly string _recordingsDirectory = recordingsDirectory;

    private LogLine[]? _lines;
    private int _index;

    /// <summary>
    /// How many seconds of match time pass per real second.
    ///
    /// Playback follows the log's own timestamps rather than a fixed line rate. A
    /// real phase runs about a minute, and the generator writes timestamps to match,
    /// so pacing by lines made a whole phase flash past in about a second no matter
    /// what the setting said.
    /// </summary>
    private double _speed = 12;

    private DateTime _startedAt;
    private DateTime _logStart;
    private DateTime? _pausedAt;

    private readonly List<string> _recentRecordings = [];

    public bool IsPlaying => _lines is not null;

    public bool DemoEnabled => _demo.Enabled;

    /// <summary>How many stand-in bodies are currently placed, for diagnostics.</summary>
    public int DemoBodyCount => _demo.Bodies.Count;

    /// <summary>How far through the current playback, 0 to 1.</summary>
    public float Progress => _lines is null || _lines.Length == 0
        ? 0f
        : Math.Clamp((float)_index / _lines.Length, 0f, 1f);

    /// <summary>Most recent outcome, for the UI to show without needing chat.</summary>
    public string LastMessage { get; private set; } = string.Empty;

    public IReadOnlyList<string> RecentRecordings => _recentRecordings;

    /// <summary>Generate a match and play it out. The same seed always replays identically.</summary>
    public void Simulate(int seed, double linesPerSecond)
    {
        // Only reuse the loaded roster if it actually names people. The editor pads
        // squads with blank rows, so entry count alone is not evidence of a lineup.
        var reuseLoaded = _state.CurrentRoster is { NamedCount: >= 2 };

        var roster = reuseLoaded
            ? _state.CurrentRoster!
            : MatchSimulator.StandardRoster();

        try
        {
            var lines = new MatchSimulator(roster, seed).Generate().ToArray();

            // The tracker must be tracking the same people the match is about. When
            // we fell back to a generated squad, apply it unconditionally: skipping
            // this left every simulated player unrecognised, so the field rendered
            // empty while phases and scores carried on parsing normally.
            if (!reuseLoaded || !_state.HasRoster)
                _state.ApplyRoster(roster);

            Begin(lines, linesPerSecond);

            // Simulated players do not physically exist, so without stand-in bodies
            // there is nothing in the world for the overlay to tag and the arena
            // looks empty. Asking to watch a match should just show it.
            var placed = _demo.Enabled || SetDemo(true);

            LastMessage = placed
                ? $"Simulating {roster.HomeTeam} vs {roster.AwayTeam} (seed {seed})."
                : $"Simulating {roster.HomeTeam} vs {roster.AwayTeam} (seed {seed}). " +
                  "Stand-in bodies could not be placed, so only the Match screen will update.";
        }
        catch (Exception ex)
        {
            Stop();
            LastMessage = $"Simulation failed: {ex.Message}";
            _log.Error($"Simulation failed: {ex}");
        }
    }

    /// <summary>
    /// Play a recorded log back through the parser.
    ///
    /// <paramref name="useEmbeddedRoster"/> exists because a header can be wrong. The
    /// roster is written when recording starts, so a match that began before the lineup
    /// was updated carries the previous game's — and a stale sheet is worse than none,
    /// since the parser only recognises names on it and will therefore recognise nobody.
    /// Turning this off keeps whatever roster is already loaded.
    /// </summary>
    public void ReplayFile(string path, double linesPerSecond, bool useEmbeddedRoster = true)
    {
        if (!File.Exists(path))
        {
            LastMessage = $"No such file: {path}";
            return;
        }

        // A recording made by this plugin carries its own roster, so it can be
        // replayed years later without anyone remembering the lineup.
        var embedded = useEmbeddedRoster ? RosterHeader.ReadFile(path) : null;
        if (embedded is not null)
        {
            _state.ApplyRoster(embedded);

            // A recording made by somebody who was playing has their own dice in it as
            // "You roll a 40". Only the header says who "You" was, so without this every
            // roll they made goes unattributed — and it is their own team's player that
            // ends up looking like they never rolled.
            if (embedded.RecordedBy is { Length: > 0 } recorder)
                _parser.LocalPlayerName = recorder;
        }
        else if (!_state.HasRoster)
        {
            LastMessage =
                "This recording has no roster in it, and none is loaded. " +
                "Set one up first or every name will be ignored.";
            return;
        }

        try
        {
            var lines = LogReplay.ReadFile(path).ToArray();
            if (lines.Length == 0)
            {
                LastMessage = "No recognisable lines in that file.";
                return;
            }

            Begin(lines, linesPerSecond);
            LastMessage = $"Replaying {Path.GetFileName(path)} ({lines.Length} lines).";
        }
        catch (Exception ex)
        {
            Stop();
            LastMessage = $"Replay failed: {ex.Message}";
            _log.Error($"Replay failed for {path}: {ex}");
        }
    }

    /// <summary>Match seconds per real second. 1 plays a match out in real time.</summary>
    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.25, 240);
    }

    public bool IsPaused => _pausedAt is not null;

    /// <summary>How long the loaded match will take at the current speed.</summary>
    public TimeSpan EstimatedDuration
    {
        get
        {
            if (_lines is null || _lines.Length == 0) return TimeSpan.Zero;

            var span = _lines[^1].Timestamp - _logStart;
            return span <= TimeSpan.Zero ? TimeSpan.Zero : span / _speed;
        }
    }

    /// <summary>Stop the clock without losing your place, so a phase can be studied.</summary>
    public void TogglePause()
    {
        if (_lines is null) return;

        if (_pausedAt is { } since)
        {
            // Shift the start forward by however long we sat still, so the log time
            // already played stays where it was.
            _startedAt += DateTime.Now - since;
            _pausedAt = null;
        }
        else
        {
            _pausedAt = DateTime.Now;
        }
    }

    private void Begin(LogLine[] lines, double speed)
    {
        _state.Reset();
        _parser.ClearUnmatchedNames();

        _lines = lines;
        _index = 0;
        Speed = speed;

        _startedAt = DateTime.Now;
        _logStart = lines[0].Timestamp;
        _pausedAt = null;
    }

    public void Stop()
    {
        _lines = null;
        _index = 0;
    }

    /// <summary>Place or remove the practice arena. False when there is no character to anchor to.</summary>
    public bool SetDemo(bool enabled)
    {
        if (!enabled)
        {
            _demo.Disable();
            LastMessage = "Practice arena removed.";
            return true;
        }

        if (!_state.HasRoster)
            _state.ApplyRoster(DemoDirector.DemoRoster());

        if (!_demo.Enable())
        {
            LastMessage = "Cannot place the arena: no character loaded yet.";
            return false;
        }

        // Nothing is going to announce a blitzoff in an empty room, and both the
        // overlay and the match view gate on an active game.
        _state.IsActive = true;
        LastMessage = "Practice arena placed. Look around you.";
        return true;
    }

    /// <summary>Advance playback. Called once per frame.</summary>
    public void Step()
    {
        if (_lines is null || _pausedAt is not null) return;

        // Follow the log's clock, scaled. Gaps between phases are preserved in
        // proportion, so a huddle stays short and a phase stays long.
        var elapsed = DateTime.Now - _startedAt;
        var upTo = _logStart + TimeSpan.FromTicks((long)(elapsed.Ticks * _speed));

        // Cap the per-frame budget so a stall does not dump the rest of the match
        // into a single frame.
        for (var budget = 0; budget < 64 && _index < _lines.Length; budget++)
        {
            if (_lines[_index].Timestamp > upTo) break;

            var line = _lines[_index++];

            if (!LogReplay.IsRelevantChannel(line.Channel)) continue;

            try
            {
                _parser.ProcessMessage(line.Sender, line.Message, line.Timestamp);
            }
            catch (Exception ex)
            {
                _log.Error($"[BlitzTracker] Playback parse error: {ex.Message}");
            }
        }

        if (_index < _lines.Length) return;

        LastMessage =
            $"Finished. {_state.HomeTeam} {_state.Score.Home}:{_state.Score.Away} {_state.AwayTeam}." +
            (_parser.UnmatchedNames.Count > 0
                ? $" {_parser.UnmatchedNames.Count} name(s) acted but were not on the roster."
                : string.Empty);

        _lines = null;
    }

    /// <summary>Refresh the list of recordings on disk, for the replay picker.</summary>
    public void RefreshRecordings()
    {
        _recentRecordings.Clear();

        try
        {
            if (!Directory.Exists(_recordingsDirectory)) return;

            var files = Directory.EnumerateFiles(_recordingsDirectory, "*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(8);

            _recentRecordings.AddRange(files);
        }
        catch (Exception ex)
        {
            _log.Warning($"[BlitzTracker] Could not list recordings: {ex.Message}");
        }
    }
}
