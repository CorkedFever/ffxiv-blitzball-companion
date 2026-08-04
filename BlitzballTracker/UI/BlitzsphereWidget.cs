using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace BlitzballTracker.UI;

using BlitzballTracker.Core.GameState;

/// <summary>
/// The Blitzsphere field view, drawn with ImGui's draw list.
///
/// This is a port of the web app's SVG view, which maps over almost directly: lines
/// become AddLine, circles become AddCircleFilled, and the Gaussian blur glow
/// becomes a few concentric circles at falling alpha. The node layout is shared with
/// the Blazor view through <see cref="BlitzsphereLayout"/>.
///
/// Immediate mode redraws everything every frame, so the draw path allocates
/// nothing: buckets and hit-test targets are preallocated and refilled, never
/// rebuilt with LINQ.
/// </summary>
public sealed class BlitzsphereWidget(BlitzGame state)
{
    private const int WaymarkSlots = 8;

    private readonly BlitzGame _state = state;

    /// <summary>Players per waymark, refilled each frame.</summary>
    private readonly List<PlayerState>[] _buckets =
        Enumerable.Range(0, WaymarkSlots).Select(_ => new List<PlayerState>(6)).ToArray();

    private readonly List<(PlayerState Player, Vector2 Screen)> _hitTargets = new(16);
    private readonly Dictionary<string, Spring2> _springs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Comparison<PlayerState> ByBallThenName = static (a, b) =>
    {
        if (a.HasBall != b.HasBall) return a.HasBall ? -1 : 1;
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    };

    private PlayerState? _selected;

    /// <summary>Set when the user drags a player to a new zone, for the host to react to.</summary>
    public PlayerState? Selected => _selected;

    public void Draw()
    {
        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 40f) return;

        var scale = avail.X / BlitzsphereLayout.ViewWidth;
        var height = BlitzsphereLayout.ViewHeight * scale;

        var origin = ImGui.GetCursorScreenPos();

        // Reserve the region first so the surrounding layout flows correctly and we
        // get a single well-defined hit area. Drawing happens on top via the draw list.
        ImGui.InvisibleButton("##blitzsphere", new Vector2(avail.X, height));
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var mouse = ImGui.GetIO().MousePos;

        var draw = ImGui.GetWindowDrawList();
        var delta = ImGui.GetIO().DeltaTime;

        BucketPlayers();
        _hitTargets.Clear();

        DrawLanes(draw, origin, scale);
        DrawZones(draw, origin, scale);
        DrawPlayers(draw, origin, scale, delta);

        if (clicked)
            HandleClick(mouse, origin, scale);
    }

    private void BucketPlayers()
    {
        foreach (var bucket in _buckets)
            bucket.Clear();

        foreach (var player in _state.Players.Values)
        {
            var slot = (int)player.Position;
            if (slot <= 0 || slot >= WaymarkSlots) continue; // Waymark.None and out of range
            _buckets[slot].Add(player);
        }

        // Ball carrier on top, then alphabetical. Sorted in place: no allocation.
        foreach (var bucket in _buckets)
        {
            if (bucket.Count > 1)
                bucket.Sort(ByBallThenName);
        }
    }

    private static Vector2 ToScreen(Vector2 origin, float scale, Vector2 design)
        => origin + (design * scale);

    private void DrawLanes(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        foreach (var (from, to) in BlitzsphereLayout.Lanes)
        {
            var a = ToScreen(origin, scale, BlitzsphereLayout.Nodes[from]);
            var b = ToScreen(origin, scale, BlitzsphereLayout.Nodes[to]);

            // A surveyor guards a lane rather than a node, so light up the lane they
            // are watching in their side's colour.
            var surveyor = _state.SurveyorOf(from, to);

            if (surveyor is null)
            {
                draw.AddLine(a, b, BlitzPalette.LaneLine, MathF.Max(1f, 1.5f * scale));
                continue;
            }

            var guardColor = BlitzPalette.TeamColor(
                surveyor.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase));

            draw.AddLine(a, b, BlitzPalette.WithAlpha(guardColor, 0.25f), MathF.Max(3f, 5f * scale));
            draw.AddLine(a, b, BlitzPalette.WithAlpha(guardColor, 0.85f), MathF.Max(1.5f, 2f * scale));
        }
    }

    private void DrawZones(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        var ballWaymark = BallWaymark();

        foreach (var waymark in BlitzsphereLayout.All)
        {
            var center = ToScreen(origin, scale, BlitzsphereLayout.Nodes[waymark]);
            var radius = 32f * scale;

            var hasBall = ballWaymark == waymark;
            var isGoal = BlitzsphereLayout.IsGoal(waymark);
            // A gate belongs to a side, so it carries that side's colour: knowing
            // whose it is matters more than knowing one exists.
            var gate = _state.RushGateAt(waymark);

            if (gate is not null)
            {
                var gateColor = BlitzPalette.TeamColor(
                    gate.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase));

                GlowRing(draw, center, 38f * scale, gateColor, MathF.Max(1.5f, 2f * scale));
            }

            draw.AddCircleFilled(center, radius, hasBall ? BlitzPalette.ZoneBallFill : BlitzPalette.ZoneFill, 40);

            var stroke = hasBall
                ? BlitzPalette.ZoneBallStroke
                : isGoal ? BlitzPalette.ZoneGoalStroke : BlitzPalette.ZoneStroke;

            draw.AddCircle(center, radius, stroke, 40, MathF.Max(1f, (hasBall ? 2.5f : 2f) * scale));

            // Zone marked as a drop target while a player is selected.
            if (_selected is not null)
                draw.AddCircle(center, radius + (4f * scale), BlitzPalette.WithAlpha(BlitzPalette.Ink, 0.35f), 40, 1f);

            // The real waymark glyph, so a zone reads as the marker it represents.
            CenteredText(draw, center + new Vector2(0f, -22f * scale),
                BlitzIcons.WaymarkGlyph(waymark), BlitzPalette.ZoneLabel);

            CenteredText(draw, center + new Vector2(0f, -10f * scale),
                BlitzsphereLayout.ZoneName(waymark), BlitzPalette.ZoneSubLabel);
        }
    }

    private void DrawPlayers(ImDrawListPtr draw, Vector2 origin, float scale, float delta)
    {
        for (var slot = 1; slot < WaymarkSlots; slot++)
        {
            var bucket = _buckets[slot];
            if (bucket.Count == 0) continue;

            var waymark = (Waymark)slot;
            var center = ToScreen(origin, scale, BlitzsphereLayout.Nodes[waymark]);

            var spacing = (bucket.Count <= 3 ? 13f : 11f) * scale;
            var top = center.Y + (8f * scale) - ((bucket.Count - 1) * spacing * 0.5f);

            for (var i = 0; i < bucket.Count; i++)
            {
                var player = bucket[i];
                var target = new Vector2(center.X - (20f * scale), top + (i * spacing));

                // Springs are the reason a reposition slides along the lane instead
                // of teleporting, which is most of what sells the whole view.
                ref var spring = ref CollectionsMarshal.GetValueRefOrAddDefault(_springs, player.Name, out var existed);
                if (!existed) spring.Snap(target);
                spring.Update(target, delta);

                var pos = spring.Value;
                var isHome = player.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase);
                var teamColor = BlitzPalette.TeamColor(isHome);

                if (player.HasBall)
                    GlowDot(draw, pos, 4f * scale, BlitzPalette.Ball);

                // Team colour is identity and must never change. Recolouring it for
                // status made a dazed gold player look red and a red ball carrier
                // look gold, which reads as switching sides. Status is carried by
                // rings, glows and symbols instead.
                var dotRadius = MathF.Max(2f, 4f * scale);
                draw.AddCircleFilled(pos, dotRadius, teamColor, 12);

                if (player.IsDazed)
                    draw.AddCircle(pos, dotRadius + (2.5f * scale), BlitzPalette.Danger, 12, MathF.Max(1f, 1.5f * scale));

                if (ReferenceEquals(player, _selected))
                    draw.AddCircle(pos, MathF.Max(4f, 6.5f * scale), BlitzPalette.Ink, 12, 1.5f);

                // Dazed dims the name rather than repainting it, so the side stays readable.
                var textColor = player.IsDazed
                    ? BlitzPalette.WithAlpha(teamColor, 0.55f)
                    : BlitzPalette.WithAlpha(teamColor, 0.92f);

                var namePos = pos + new Vector2(7f * scale, -7f * scale);
                var label = LabelFor(player);
                draw.AddText(namePos, textColor, label);

                // Status symbols trail the name. Drawn separately rather than
                // concatenated so both halves stay cached: the name never changes,
                // the status changes constantly.
                var cursor = ImGui.CalcTextSize(label).X + (3f * scale);
                var gap = 3f * scale;

                var status = BlitzIcons.StatusFor(player);
                if (status.Length > 0)
                {
                    draw.AddText(namePos + new Vector2(cursor, 0f), BlitzIcons.StatusColor(player), status);
                    cursor += ImGui.CalcTextSize(status).X + gap;
                }

                // What they have declared this phase, tinted by how it turned out:
                // pale while pending, green on success, red on failure.
                var declared = _state.CurrentActionFor(player.Name);
                if (declared is not null)
                {
                    var actionText = BlitzIcons.ActionLabel(
                        declared.Action,
                        declared.TargetWaymark ?? Waymark.None);

                    if (actionText.Length > 0)
                    {
                        draw.AddText(
                            namePos + new Vector2(cursor, 0f),
                            BlitzIcons.OutcomeColor(declared.Outcome),
                            actionText);

                        cursor += ImGui.CalcTextSize(actionText).X + gap;
                    }
                }

                // The roll itself. This is a dice game, so the number is the most
                // interesting thing on the field: a symbol saying somebody rolled,
                // without saying what, is the one thing worth not showing.
                if (player.PhaseRoll is { } roll)
                {
                    var modifier = declared?.Modifier ?? 0;

                    draw.AddText(
                        namePos + new Vector2(cursor, 0f),
                        BlitzIcons.RollColor(roll + modifier),
                        BlitzIcons.RollText(roll, modifier));
                }

                _hitTargets.Add((player, pos));
            }
        }
    }

    /// <summary>
    /// Cheap stand-in for the SVG feGaussianBlur: a few translucent circles stacked
    /// outward. Reads as a glow at a fraction of the cost.
    /// </summary>
    private static void GlowDot(ImDrawListPtr draw, Vector2 center, float radius, uint color)
    {
        for (var layer = 3; layer >= 1; layer--)
        {
            var alpha = 0.13f * layer;
            draw.AddCircleFilled(center, radius + (layer * 2.2f), BlitzPalette.WithAlpha(color, alpha), 16);
        }
    }

    private static void GlowRing(ImDrawListPtr draw, Vector2 center, float radius, uint color, float thickness)
    {
        for (var layer = 3; layer >= 1; layer--)
        {
            var alpha = 0.11f * layer;
            draw.AddCircle(center, radius + (layer * 2f), BlitzPalette.WithAlpha(color, alpha), 40, thickness);
        }

        draw.AddCircle(center, radius, color, 40, thickness);
    }

    private static void CenteredText(ImDrawListPtr draw, Vector2 center, string text, uint color)
    {
        if (text.Length == 0) return;

        var size = ImGui.CalcTextSize(text);
        draw.AddText(center - (size * 0.5f), color, text);
    }

    /// <summary>
    /// Display label, cached because building it every frame for every player would
    /// allocate a string per player per frame.
    /// </summary>
    private string LabelFor(PlayerState player)
    {
        if (_labels.TryGetValue(player.Name, out var cached))
            return cached;

        var space = player.Name.IndexOf(' ');
        var shortName = space > 0 ? player.Name[..space] : player.Name;

        var badge = Roster.RoleAbbreviation(player.Role);
        var label = badge == "-" ? shortName : $"{shortName} {badge}";

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
    /// Select a player, then click a zone to move them. Mirrors the web app's
    /// interaction, including clicking the same player again to cancel.
    /// </summary>
    private void HandleClick(Vector2 mouse, Vector2 origin, float scale)
    {
        // Players win over zones: their dots sit inside a zone's radius.
        var grabRadius = MathF.Max(7f, 9f * scale);
        var grabSq = grabRadius * grabRadius;

        foreach (var (player, screen) in _hitTargets)
        {
            if (Vector2.DistanceSquared(mouse, screen) > grabSq) continue;

            _selected = ReferenceEquals(player, _selected) ? null : player;
            return;
        }

        if (_selected is null) return;

        var zoneRadius = 32f * scale;
        var zoneSq = zoneRadius * zoneRadius;

        foreach (var waymark in BlitzsphereLayout.All)
        {
            var center = ToScreen(origin, scale, BlitzsphereLayout.Nodes[waymark]);
            if (Vector2.DistanceSquared(mouse, center) > zoneSq) continue;

            _selected.Position = waymark;
            _selected = null;
            return;
        }
    }

    /// <summary>Drop cached labels and springs, e.g. after a roster change.</summary>
    public void Invalidate()
    {
        _springs.Clear();
        _labels.Clear();
        _selected = null;
    }
}
