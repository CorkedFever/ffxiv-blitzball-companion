using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Plugin;

namespace BlitzballTracker.UI.Views;

using BlitzballTracker.Core.GameState;

/// <summary>
/// Settings and session controls: the overlay, recording, the live feed, and
/// resetting the match.
/// </summary>
public sealed class SettingsView(
    BlitzGame state,
    Configuration config,
    IDalamudPluginInterface pluginInterface,
    GameRecorder recorder,
    LiveFeedClient liveFeed,
    string recordingsDirectory) : IShellView
{
    public string Title => "Settings";
    public string Icon => ((char)SeIconChar.BoxedPlus).ToString();

    private readonly BlitzGame _state = state;
    private readonly Configuration _config = config;
    private readonly IDalamudPluginInterface _pluginInterface = pluginInterface;
    private readonly GameRecorder _recorder = recorder;
    private readonly LiveFeedClient _liveFeed = liveFeed;
    private readonly string _recordingsDirectory = recordingsDirectory;

    private string _liveUrl = string.Empty;
    private string _status = string.Empty;

    public void Draw()
    {
        DrawRulesSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawOverlaySection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawRecordingSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawLiveFeedSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawMatchSection();

        if (_status.Length == 0) return;

        ImGui.Spacing();
        BlitzSkin.MutedWrapped(_status);
    }

    /// <summary>
    /// Rules that have changed between editions.
    ///
    /// Retired rules live here as switches rather than being deleted: the league's
    /// rules move, the published deck lags behind, and an old recording should be
    /// readable under the rules it was played by.
    /// </summary>
    private void DrawRulesSection()
    {
        BlitzSkin.SectionHeading("Rules edition");
        BlitzSkin.MutedWrapped(
            "Defaults follow how the game is played now. Switch one on to read an " +
            "older match back the way it was refereed.");

        ImGui.Spacing();

        var standby = _config.StandbyStatus;

        if (ImGui.Checkbox("Track STANDBY status", ref standby))
        {
            _config.StandbyStatus = standby;
            _state.Rules.StandbyStatus = standby;
            _pluginInterface.SavePluginConfig(_config);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Retired from the game, though the v3.2 guide still documents it.\n" +
                "Declaring nothing is a loss of action either way; this only decides\n" +
                "whether it is tracked as a named status.");
        }
    }

    private void DrawOverlaySection()
    {
        BlitzSkin.SectionHeading("In-world overlay");
        BlitzSkin.MutedWrapped(
            "Draws zone labels and player tags in the arena itself. Only appears where " +
            "a roster is loaded and both goals are placed, so it stays out of the way elsewhere.");

        ImGui.Spacing();

        var dirty = false;

        var overlay = _config.ShowWorldOverlay;
        if (ImGui.Checkbox("Show the overlay", ref overlay))
        {
            _config.ShowWorldOverlay = overlay;
            dirty = true;
        }

        ImGui.BeginDisabled(!overlay);

        var zones = _config.ShowZoneLabels;
        if (ImGui.Checkbox("Zone labels on waymarks", ref zones))
        {
            _config.ShowZoneLabels = zones;
            dirty = true;
        }

        var tags = _config.ShowPlayerTags;
        if (ImGui.Checkbox("Name tags over players", ref tags))
        {
            _config.ShowPlayerTags = tags;
            dirty = true;
        }

        var lanes = _config.ShowLaneLines;
        if (ImGui.Checkbox("Lane lines between zones", ref lanes))
        {
            _config.ShowLaneLines = lanes;
            dirty = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Links the zones so the sphere reads as a field.\n" +
                "A pulse runs down the lane when someone repositions.");
        }

        var height = _config.PlayerTagHeight;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Tag height", ref height, 0.5f, 4f, "%.1f"))
        {
            _config.PlayerTagHeight = height;
            dirty = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Blitzball is played underwater and characters drift, so\nname tags may need nudging up or down.");

        ImGui.EndDisabled();

        var radius = _config.MarkerRadius;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Marker radius", ref radius, 1f, 10f, "%.1f"))
        {
            _config.MarkerRadius = radius;
            dirty = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "How close counts as standing on a waymark, rather than near it.\n" +
                "Keep it tight: a venue is ringed with spectators, and a wide radius\n" +
                "reads the audience instead of the field.");
        }

        if (dirty)
            _pluginInterface.SavePluginConfig(_config);
    }

    private void DrawRecordingSection()
    {
        BlitzSkin.SectionHeading("Recording");
        BlitzSkin.MutedWrapped(
            "Writes match chat to a file you can replay later. Recordings also store " +
            "the roster, so an old match stays readable long after you have forgotten the lineup.");

        ImGui.Spacing();

        if (_recorder.IsRecording)
        {
            BlitzSkin.Pill($"● REC  {_recorder.LinesRecorded} lines", BlitzPalette.Danger);
            ImGui.Spacing();

            if (ImGui.Button("Stop recording", new Vector2(150, 0)))
            {
                var file = _recorder.CurrentFile;
                _recorder.Stop();
                _status = $"Saved to {file}";
            }
        }
        else
        {
            if (ImGui.Button("Start recording", new Vector2(150, 0)))
            {
                var path = _recorder.Start(_recordingsDirectory, _state.CurrentRoster);
                _status = $"Recording to {path}";
            }

            ImGui.SameLine();
            BlitzSkin.Muted(_state.HasRoster
                ? "The current roster will be written into the file."
                : "No roster loaded, so the recording will not describe itself.");
        }
    }

    private void DrawLiveFeedSection()
    {
        BlitzSkin.SectionHeading("Live feed");
        BlitzSkin.MutedWrapped("Pushes match chat to the companion web app as it happens.");

        ImGui.Spacing();

        if (_liveFeed.IsActive)
        {
            BlitzSkin.Pill($"● LIVE  {_liveFeed.MessagesSent} sent", BlitzPalette.Success);

            if (_liveFeed.Errors > 0)
            {
                ImGui.SameLine();
                BlitzSkin.Pill($"{_liveFeed.Errors} errors", BlitzPalette.Warning);
            }

            ImGui.Spacing();
            BlitzSkin.Muted(_liveFeed.BaseUrl);
            ImGui.Spacing();

            if (ImGui.Button("Stop feed", new Vector2(150, 0)))
            {
                _liveFeed.Stop();
                _status = "Live feed stopped.";
            }
        }
        else
        {
            ImGui.SetNextItemWidth(-160);
            ImGui.InputTextWithHint("##liveurl", "http://localhost:5000 (leave blank for default)", ref _liveUrl, 256);

            ImGui.SameLine();
            if (ImGui.Button("Start feed", new Vector2(150, 0)))
            {
                _liveFeed.Start(_liveUrl.Trim().Length == 0 ? null : _liveUrl.Trim());

                // Send the lineup straight away. Without it the far end recognises
                // nobody, and the failure is silent: phases and score tick over while
                // the field stays empty.
                _liveFeed.SendRoster(_state.CurrentRoster);

                _status = _state.HasRoster
                    ? $"Live feed started: {_liveFeed.BaseUrl}"
                    : $"Live feed started: {_liveFeed.BaseUrl} — no roster loaded yet, so the feed cannot track players.";
            }
        }
    }

    private void DrawMatchSection()
    {
        BlitzSkin.SectionHeading("Match");

        if (ImGui.Button("Reset match", new Vector2(150, 0)))
        {
            _state.Reset();
            _status = "Match reset. The roster was kept.";
        }

        ImGui.SameLine();
        BlitzSkin.Muted("Clears the score and play-by-play. Your roster stays loaded.");
    }
}
