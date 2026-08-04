using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;

namespace BlitzballTracker.UI.Views;

using BlitzballTracker.Core.GameState;

/// <summary>
/// Testing tools, as controls rather than commands.
///
/// A blitzball match needs twelve people, so nearly everything here exists to let
/// one person exercise the plugin alone: a fabricated arena, a generated match, and
/// playback of a recorded one.
/// </summary>
public sealed class LabView(
    MatchDriver driver,
    BlitzGame state,
    WaymarkReader waymarks,
    Configuration config) : IShellView
{
    public string Title => "Lab";
    public string Icon => ((char)SeIconChar.Dice).ToString();

    private readonly MatchDriver _driver = driver;
    private readonly BlitzGame _state = state;
    private readonly WaymarkReader _waymarks = waymarks;
    private readonly Configuration _config = config;

    private int _seed = 42;

    /// <summary>Match seconds per real second. A real match runs about 50 minutes.</summary>
    private float _speed = 12f;
    private string _logPath = string.Empty;

    public void Draw()
    {
        BlitzSkin.SectionHeading("Simulated match");
        BlitzSkin.MutedWrapped(
            "Generates a full match and plays it out, so the whole plugin can be exercised " +
            "without eleven other people. The same seed always produces the same match, so " +
            "anything that looks wrong can be reproduced exactly.");
        BlitzSkin.MutedWrapped(
            "Simulated players have no bodies in the world, so stand-ins are placed " +
            "automatically. They appear on the real waymarks when a venue has them down.");

        ImGui.Spacing();

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Seed", ref _seed);

        ImGui.SameLine();
        if (ImGui.Button("Randomise"))
            _seed = Random.Shared.Next(1, 1_000_000);

        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Speed", ref _speed, 1f, 60f, "%.0fx real time"))
            _driver.Speed = _speed;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Playback follows the match clock, not a line rate.\n" +
                "A phase runs about a minute, so 1x is real time and\n" +
                "60x squeezes a whole match into under a minute.");
        }

        ImGui.SameLine();
        BlitzSkin.Muted(DescribePace());

        ImGui.Spacing();

        if (_driver.IsPlaying)
        {
            if (ImGui.Button(_driver.IsPaused ? "Resume" : "Pause", new Vector2(100, 0)))
                _driver.TogglePause();

            ImGui.SameLine();
            if (ImGui.Button("Stop", new Vector2(80, 0)))
                _driver.Stop();

            ImGui.SameLine();
            DrawProgress();
        }
        else
        {
            if (ImGui.Button("Simulate match", new Vector2(150, 0)))
                _driver.Simulate(_seed, _speed);

            ImGui.SameLine();
            BlitzSkin.Muted(_state.HasRoster
                ? "Uses your loaded roster."
                : "No roster loaded, so a stand-in squad is used.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawArenaSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawReplaySection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawDiagnostics();
    }

    /// <summary>
    /// Live state of everything the in-world display depends on.
    ///
    /// "Nothing is showing" has several possible causes that look identical from the
    /// outside, so list the conditions rather than leaving people to guess which one
    /// is failing.
    /// </summary>
    private void DrawDiagnostics()
    {
        if (!ImGui.CollapsingHeader("Diagnostics")) return;

        BlitzSkin.MutedWrapped("Every condition the in-world display depends on. All should be green.");
        ImGui.Spacing();

        // The overlay disables itself if it ever throws, which is easy to miss.
        Condition("Overlay enabled", _config.ShowWorldOverlay,
            "Turned off. It disables itself after an error: check /xllog, then re-enable below.");

        if (!_config.ShowWorldOverlay)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Re-enable"))
                _config.ShowWorldOverlay = true;
        }

        Condition("Player tags enabled", _config.ShowPlayerTags, "Turned off in Settings.");
        Condition("Lane lines enabled", _config.ShowLaneLines, "Turned off in Settings.");

        ImGui.Spacing();

        Condition("Roster loaded", _state.HasRoster, "Nothing can be tracked or drawn without one.");

        BlitzSkin.Muted($"    Home: '{_state.HomeTeam}'   Away: '{_state.AwayTeam}'");

        if (_state.CurrentRoster is { } roster)
        {
            BlitzSkin.Muted(
                $"    Roster rows: {roster.Entries.Count}, of which named: {roster.NamedCount}");
        }

        var placed = _state.Players.Values.Count(p => p.Position != Waymark.None);
        Condition($"Players placed on zones ({placed}/{_state.Players.Count})", placed > 0,
            "Everyone is unplaced, so there is nothing to draw on the field.");

        ImGui.Spacing();

        var markers = _waymarks.ReadMarkers();
        Condition($"Arena found ({markers.Count} waymarks)", _waymarks.ArenaReady,
            "Goals D and 4 must both be present. Place waymarks, or place stand-ins to fabricate an arena.");

        if (_waymarks.ArenaReady)
        {
            BlitzSkin.Muted(_waymarks.UsingFabricatedArena
                ? "    Using a fabricated practice arena."
                : "    Using the real waymarks placed here.");
        }

        ImGui.Spacing();

        Condition($"Stand-in bodies placed ({_driver.DemoBodyCount})", _driver.DemoBodyCount > 0,
            "Simulated players have no bodies, so nothing gets labelled. Place stand-ins above.");

        Condition("Playback running", _driver.IsPlaying,
            "Nothing is playing, so nobody will move. Start a simulated match above.");

        ImGui.Spacing();
        BlitzSkin.SectionHeading("Zones detected");

        if (markers.Count == 0)
        {
            BlitzSkin.Muted("None.");
            return;
        }

        foreach (var waymark in BlitzsphereLayout.All)
        {
            var found = markers.ContainsKey(waymark);
            ImGui.TextColored(
                BlitzPalette.ToVector(found ? BlitzPalette.Success : BlitzPalette.Danger),
                $"{BlitzsphereLayout.Label(waymark)} ");

            if (waymark != BlitzsphereLayout.All[^1])
                ImGui.SameLine();
        }
    }

    private static void Condition(string label, bool ok, string helpWhenFalse)
    {
        ImGui.TextColored(
            BlitzPalette.ToVector(ok ? BlitzPalette.Success : BlitzPalette.Danger),
            ok ? "OK  " : "X   ");

        ImGui.SameLine();
        ImGui.TextUnformatted(label);

        if (ok) return;

        ImGui.SameLine();
        BlitzSkin.Muted($"— {helpWhenFalse}");
    }

    private void DrawProgress()
    {
        var fraction = _driver.Progress;
        var color = _driver.IsPaused ? BlitzPalette.Warning : BlitzPalette.Accent;

        BlitzSkin.StatBar(fraction, color, 160f, 8f);
        ImGui.SameLine();
        BlitzSkin.Muted(_driver.IsPaused ? "paused" : $"{fraction * 100f:0}%");
    }

    /// <summary>
    /// Say how long this will take in plain terms, so the speed slider means
    /// something before you press play rather than after.
    /// </summary>
    private string DescribePace()
    {
        if (_driver.IsPlaying)
        {
            var remaining = _driver.EstimatedDuration;
            return remaining > TimeSpan.Zero
                ? $"~{remaining.TotalMinutes:0.#} min total"
                : string.Empty;
        }

        // A full match is 2 sets of 10 rounds, each round two phases of about a
        // minute plus huddles: roughly 50 minutes of match time.
        var minutes = 50.0 / Math.Max(1f, _speed);

        return minutes >= 1
            ? $"a match takes ~{minutes:0.#} min"
            : $"a match takes ~{minutes * 60:0} sec";
    }

    private void DrawArenaSection()
    {
        BlitzSkin.SectionHeading("Stand-in players");
        BlitzSkin.MutedWrapped(
            "Places twelve stand-in bodies on the field so the in-world overlay has " +
            "something to label. If no waymarks are placed nearby, a practice arena is " +
            "fabricated around you as well.");

        ImGui.Spacing();

        if (_driver.DemoEnabled)
        {
            if (ImGui.Button("Remove stand-ins", new Vector2(150, 0)))
                _driver.SetDemo(false);

            ImGui.SameLine();

            // Say which arena is actually in use: standing in a real venue with real
            // markers behaves differently from a fabricated one, and the difference
            // matters when something looks wrong.
            if (_waymarks.UsingFabricatedArena)
                BlitzSkin.Pill("Practice arena", BlitzPalette.Purple);
            else if (_waymarks.ArenaReady)
                BlitzSkin.Pill("On real waymarks", BlitzPalette.Success);
            else
                BlitzSkin.Pill("No arena found", BlitzPalette.Warning);
        }
        else
        {
            if (ImGui.Button("Place stand-ins", new Vector2(150, 0)))
            {
                if (!_driver.SetDemo(true))
                    ImGui.OpenPopup("no-local-player");
            }

            ImGui.SameLine();
            BlitzSkin.Muted(_waymarks.ArenaReady
                ? "Real waymarks detected: stand-ins will use them."
                : "No waymarks nearby, so an arena will be fabricated around you.");
        }

        if (ImGui.BeginPopup("no-local-player"))
        {
            ImGui.TextUnformatted("Cannot place the arena: no character loaded yet.");
            ImGui.EndPopup();
        }
    }

    private void DrawReplaySection()
    {
        BlitzSkin.SectionHeading("Replay a recording");
        BlitzSkin.MutedWrapped(
            "Plays a recorded log back through the parser. Useful for checking a real " +
            "match after the fact, or for investigating something that went wrong.");

        ImGui.Spacing();

        ImGui.SetNextItemWidth(-160);
        ImGui.InputTextWithHint("##logpath", "path to a recorded .txt or .log", ref _logPath, 512);

        ImGui.SameLine();
        var canPlay = _logPath.Trim().Length > 0 && !_driver.IsPlaying;

        ImGui.BeginDisabled(!canPlay);
        if (ImGui.Button("Play recording", new Vector2(150, 0)))
            _driver.ReplayFile(_logPath.Trim().Trim('"'), _speed);
        ImGui.EndDisabled();

        if (_driver.LastMessage.Length > 0)
        {
            ImGui.Spacing();
            BlitzSkin.MutedWrapped(_driver.LastMessage);
        }

        if (_driver.RecentRecordings.Count == 0) return;

        ImGui.Spacing();
        BlitzSkin.Muted("Your recordings:");

        foreach (var file in _driver.RecentRecordings)
        {
            ImGui.PushID(file);
            if (ImGui.SmallButton("Play"))
                _driver.ReplayFile(file, _speed);
            ImGui.PopID();

            ImGui.SameLine();
            BlitzSkin.Muted(Path.GetFileName(file));
        }
    }
}
