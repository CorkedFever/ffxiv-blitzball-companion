using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace BlitzballTracker.UI;

using BlitzballTracker.Core.GameState;

/// <summary>
/// Draws game state into the arena itself, projected from world coordinates.
///
/// The waymarks are real placed markers and the players are really standing on them,
/// so the tracker's view of the field can be drawn where the field actually is
/// rather than only in a panel off to the side. Doubles as a broadcast overlay.
///
/// Everything goes to the foreground draw list, which renders over the game without
/// needing a window to host it.
/// </summary>
public sealed class WorldOverlay(
    BlitzGame state,
    WaymarkReader waymarks,
    IGameGui gameGui,
    Configuration config)
{
    private readonly BlitzGame _state = state;
    private readonly WaymarkReader _waymarks = waymarks;
    private readonly IGameGui _gameGui = gameGui;
    private readonly Configuration _config = config;

    public void Draw()
    {
        if (!_config.ShowWorldOverlay) return;

        // Only meaningful at a blitzball venue: a loaded roster plus both goals down.
        if (!_state.HasRoster) return;

        var markers = _waymarks.ReadMarkers();
        if (!_waymarks.ArenaReady) return;

        // Watch every tracked player, not just the ones currently on screen, or a
        // move that happens behind you would never register.
        foreach (var player in _state.Players.Values)
            TrackMovement(player, player.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase));

        var draw = ImGui.GetForegroundDrawList();

        // Lanes first, so everything else sits on top of them.
        if (_config.ShowLaneLines)
            DrawLanes(draw, markers);

        if (_config.ShowZoneLabels)
            DrawZones(draw, markers);

        if (_config.ShowPlayerTags)
            DrawPlayers(draw);
    }

    /// <summary>How far above the markers the lane lines float, in game units.</summary>
    private const float LaneHeight = 1.0f;

    /// <summary>How long a movement pulse takes to travel its lane.</summary>
    private static readonly TimeSpan PulseDuration = TimeSpan.FromSeconds(1.8);

    private readonly Dictionary<string, Waymark> _lastSeenAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MovementPulse> _pulses = [];

    private readonly record struct MovementPulse(Waymark From, Waymark To, DateTime At, bool IsHome);

    /// <summary>
    /// Draw the lanes connecting adjacent zones, so the sphere reads as a field
    /// rather than seven unrelated markers, and animate anyone moving along them.
    /// </summary>
    private void DrawLanes(ImDrawListPtr draw, IReadOnlyDictionary<Waymark, Vector3> markers)
    {
        var ballWaymark = BallWaymark();

        foreach (var (from, to) in BlitzsphereLayout.Lanes)
        {
            if (!TryProjectLane(markers, from, to, out var a, out var b)) continue;

            // A guarded lane outranks everything: someone is watching that route.
            var surveyor = _state.SurveyorOf(from, to);

            if (surveyor is not null)
            {
                var guardColor = BlitzPalette.TeamColor(
                    surveyor.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase));

                draw.AddLine(a, b, BlitzPalette.WithAlpha(guardColor, 0.25f), 6f);
                draw.AddLine(a, b, BlitzPalette.WithAlpha(guardColor, 0.9f), 2.5f);
                continue;
            }

            // The lane the ball is sitting on gets picked out from the rest.
            var touchesBall = from == ballWaymark || to == ballWaymark;

            var color = touchesBall
                ? BlitzPalette.WithAlpha(BlitzPalette.Ball, 0.45f)
                : BlitzPalette.WithAlpha(BlitzPalette.Accent, 0.22f);

            draw.AddLine(a, b, color, touchesBall ? 2.5f : 1.5f);
        }

        DrawMovementPulses(draw, markers);
    }

    private void DrawMovementPulses(ImDrawListPtr draw, IReadOnlyDictionary<Waymark, Vector3> markers)
    {
        var now = DateTime.Now;
        _pulses.RemoveAll(p => now - p.At > PulseDuration);

        foreach (var pulse in _pulses)
        {
            if (!TryProjectLane(markers, pulse.From, pulse.To, out var a, out var b)) continue;

            var progress = (float)((now - pulse.At).TotalSeconds / PulseDuration.TotalSeconds);
            progress = Math.Clamp(progress, 0f, 1f);

            var fade = 1f - progress;
            var color = BlitzPalette.TeamColor(pulse.IsHome);

            // Brighten the whole lane briefly, so the route taken is obvious even if
            // you look up part way through.
            draw.AddLine(a, b, BlitzPalette.WithAlpha(color, 0.5f * fade), 3f);

            // A head travelling the lane, with a short trail behind it.
            var head = Vector2.Lerp(a, b, progress);
            var tail = Vector2.Lerp(a, b, Math.Max(0f, progress - 0.12f));

            draw.AddLine(tail, head, BlitzPalette.WithAlpha(color, 0.9f * fade), 4f);

            for (var layer = 3; layer >= 1; layer--)
            {
                draw.AddCircleFilled(head, 3f + (layer * 2f),
                    BlitzPalette.WithAlpha(color, 0.12f * layer * fade), 12);
            }

            draw.AddCircleFilled(head, 3.5f, BlitzPalette.WithAlpha(color, fade), 12);
        }
    }

    /// <summary>
    /// Project both ends of a lane to screen.
    ///
    /// Uses the overload that reports whether a point is in front of the camera
    /// separately from whether it is inside the viewport, so a lane running to a
    /// marker just off the edge of the screen still gets drawn instead of vanishing.
    /// </summary>
    private bool TryProjectLane(
        IReadOnlyDictionary<Waymark, Vector3> markers,
        Waymark from,
        Waymark to,
        out Vector2 a,
        out Vector2 b)
    {
        a = default;
        b = default;

        if (!markers.TryGetValue(from, out var worldA) || !markers.TryGetValue(to, out var worldB))
            return false;

        worldA.Y += LaneHeight;
        worldB.Y += LaneHeight;

        var frontA = _gameGui.WorldToScreen(worldA, out a, out _);
        var frontB = _gameGui.WorldToScreen(worldB, out b, out _);

        // Both ends must be in front of the camera; a line to a point behind it
        // projects to nonsense.
        return frontA && frontB;
    }

    /// <summary>
    /// Note that a player changed zone, so the lane they took can be animated.
    /// </summary>
    private void TrackMovement(PlayerState player, bool isHome)
    {
        var current = player.Position;

        if (!_lastSeenAt.TryGetValue(player.Name, out var previous))
        {
            _lastSeenAt[player.Name] = current;
            return;
        }

        if (previous == current) return;
        _lastSeenAt[player.Name] = current;

        if (previous == Waymark.None || current == Waymark.None) return;

        _pulses.Add(new MovementPulse(previous, current, DateTime.Now, isHome));
    }

    private void DrawZones(ImDrawListPtr draw, IReadOnlyDictionary<Waymark, Vector3> markers)
    {
        var ballWaymark = BallWaymark();

        foreach (var (waymark, worldPos) in markers)
        {
            // Lift the label clear of the marker decal on the floor.
            var anchor = worldPos with { Y = worldPos.Y + 0.6f };
            if (!_gameGui.WorldToScreen(anchor, out var screen)) continue;

            var isGoal = BlitzsphereLayout.IsGoal(waymark);
            var hasBall = ballWaymark == waymark;
            var gate = _state.RushGateAt(waymark);

            var color = hasBall ? BlitzPalette.Ball
                : gate is not null ? BlitzPalette.RushGate
                : isGoal ? BlitzPalette.ZoneGoalStroke
                : BlitzPalette.Accent;

            draw.AddCircle(screen, 14f, BlitzPalette.WithAlpha(color, 0.55f), 24, 2f);

            // The outer ring carries the placing side's colour, so you can tell at a
            // glance whose gate is sitting on the zone.
            if (gate is not null)
            {
                var gateColor = BlitzPalette.TeamColor(
                    gate.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase));

                draw.AddCircle(screen, 20f, BlitzPalette.WithAlpha(gateColor, 0.45f), 24, 3f);
            }

            // The genuine waymark glyph, so the label matches the marker underfoot.
            OutlinedCentered(draw, screen - new Vector2(0f, 32f), BlitzIcons.WaymarkGlyph(waymark), color);

            OutlinedCentered(draw, screen - new Vector2(0f, 17f),
                BlitzsphereLayout.ZoneName(waymark), BlitzPalette.WithAlpha(BlitzPalette.InkDim, 0.9f));

            if (gate is not null)
                OutlinedCentered(draw, screen + new Vector2(0f, 18f), BlitzIcons.RushGate, BlitzPalette.RushGate);
        }
    }

    private void DrawPlayers(ImDrawListPtr draw)
    {
        foreach (var seen in _waymarks.ReadNearbyPlayers())
        {
            // Only people on the team sheet: a venue is full of spectators.
            if (!_state.Players.TryGetValue(PlayerNames.StripWorld(seen.Name), out var player))
                continue;

            var head = seen.Position with { Y = seen.Position.Y + _config.PlayerTagHeight };
            if (!_gameGui.WorldToScreen(head, out var screen)) continue;

            var isHome = player.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase);
            var teamColor = BlitzPalette.TeamColor(isHome);

            // Names always carry the team colour: nobody changes sides mid-match, so
            // nothing about their state may repaint them as the opposition. Dazed
            // dims instead, and the ball carrier is marked by the halo and chevron.
            var color = player.IsDazed
                ? BlitzPalette.WithAlpha(teamColor, 0.6f)
                : teamColor;

            if (player.HasBall)
            {
                // A soft halo plus a chevron, so the carrier is findable at a glance
                // in a crowded sphere.
                for (var layer = 3; layer >= 1; layer--)
                {
                    draw.AddCircleFilled(
                        screen, 6f + (layer * 3f),
                        BlitzPalette.WithAlpha(BlitzPalette.Ball, 0.10f * layer), 16);
                }

                draw.AddTriangleFilled(
                    screen + new Vector2(-7f, -14f),
                    screen + new Vector2(7f, -14f),
                    screen + new Vector2(0f, -4f),
                    BlitzPalette.Ball);
            }

            OutlinedCentered(draw, screen, LabelFor(player), color);

            // Symbols read faster than words over a busy scene, and stack without
            // needing more vertical room.
            // Symbols carry the state colour; the name above keeps the team's.
            // Each piece that draws claims a line, so the stack closes up rather than
            // leaving a gap where a player happens to have no status or no action.
            var line = 15f;

            var status = BlitzIcons.StatusFor(player);
            if (status.Length > 0)
            {
                OutlinedCentered(draw, screen + new Vector2(0f, line), status, BlitzIcons.StatusColor(player));
                line += 14f;
            }

            if (DrawDeclaredAction(draw, player, screen + new Vector2(0f, line)))
                line += 14f;

            DrawRoll(draw, player, screen + new Vector2(0f, line));
        }
    }

    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Name plus role badge, cached because this runs every frame for every player
    /// and the composed string never changes.
    /// </summary>
    private string LabelFor(PlayerState player)
    {
        if (_labels.TryGetValue(player.Name, out var cached))
            return cached;

        var badge = Roster.RoleAbbreviation(player.Role);
        var label = badge == "-" ? player.Name : $"{player.Name}  {badge}";

        _labels[player.Name] = label;
        return label;
    }

    private Waymark BallWaymark()
    {
        if (_state.BallCarrier is null) return Waymark.None;
        return _state.Players.TryGetValue(_state.BallCarrier, out var carrier)
            ? carrier.Position
            : Waymark.None;
    }

    /// <summary>
    /// A player's roll for this phase, over their head in the arena itself.
    ///
    /// The whole point of standing in the sphere is watching the rolls land, so this
    /// puts the number where people are already looking instead of in a window behind
    /// the game.
    /// </summary>
    private void DrawRoll(ImDrawListPtr draw, PlayerState player, Vector2 anchor)
    {
        if (player.PhaseRoll is not { } roll) return;

        var modifier = _state.CurrentActionFor(player.Name)?.Modifier ?? 0;

        OutlinedCentered(
            draw,
            anchor,
            BlitzIcons.RollText(roll, modifier),
            BlitzIcons.RollColor(roll + modifier));
    }

    /// <summary>
    /// The action a player has declared, and who it is aimed at. Returns whether
    /// anything was drawn, so the caller knows if the line was used.
    ///
    /// Drawn as separate pieces rather than one assembled string, because this runs
    /// every frame for every player on the field.
    /// </summary>
    private bool DrawDeclaredAction(ImDrawListPtr draw, PlayerState player, Vector2 anchor)
    {
        var declared = _state.CurrentActionFor(player.Name);
        if (declared is null) return false;

        var label = BlitzIcons.ActionLabel(declared.Action);
        if (label.Length == 0) return false;

        var color = BlitzIcons.OutcomeColor(declared.Outcome);

        // A move names a waymark rather than a player, and that destination is the
        // whole content of the declaration.
        var target = declared.TargetName;
        if (string.IsNullOrEmpty(target) && declared.TargetWaymark is { } destination)
            target = BlitzIcons.WaymarkGlyph(destination);

        if (string.IsNullOrEmpty(target))
        {
            OutlinedCentered(draw, anchor, label, color);
            return true;
        }

        const string arrow = " > ";

        var labelSize = ImGui.CalcTextSize(label);
        var arrowSize = ImGui.CalcTextSize(arrow);
        var targetSize = ImGui.CalcTextSize(target);

        // Centre the run as a whole, then lay the three parts out along it.
        var x = anchor.X - ((labelSize.X + arrowSize.X + targetSize.X) * 0.5f);
        var y = anchor.Y - (labelSize.Y * 0.5f);

        Outlined(draw, new Vector2(x, y), label, color);
        x += labelSize.X;

        Outlined(draw, new Vector2(x, y), arrow, BlitzPalette.WithAlpha(BlitzPalette.InkDim, 0.9f));
        x += arrowSize.X;

        Outlined(draw, new Vector2(x, y), target, color);
        return true;
    }

    /// <summary>
    /// Centred text with a dark outline. World overlays sit on top of arbitrary
    /// scenery, so unoutlined text becomes unreadable against bright water.
    /// </summary>
    private static void OutlinedCentered(ImDrawListPtr draw, Vector2 center, string text, uint color)
    {
        if (text.Length == 0) return;

        Outlined(draw, center - (ImGui.CalcTextSize(text) * 0.5f), text, color);
    }

    private static void Outlined(ImDrawListPtr draw, Vector2 topLeft, string text, uint color)
    {
        if (text.Length == 0) return;

        var shadow = BlitzPalette.Rgb(0x000000, 0.85f);

        draw.AddText(topLeft + new Vector2(1f, 0f), shadow, text);
        draw.AddText(topLeft + new Vector2(-1f, 0f), shadow, text);
        draw.AddText(topLeft + new Vector2(0f, 1f), shadow, text);
        draw.AddText(topLeft + new Vector2(0f, -1f), shadow, text);

        draw.AddText(topLeft, color, text);
    }
}
