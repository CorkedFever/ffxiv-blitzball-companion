using System.Numerics;
using Dalamud.Plugin.Services;

namespace BlitzballTracker;

using BlitzballTracker.Core.GameState;

/// <summary>
/// Fabricates an arena and a full squad so the world-space features can be tested
/// without eleven other people.
///
/// The overlay, formation detection and position sync all read real waymarks and
/// real player bodies, none of which exist when you are stood in your house alone.
/// This stands in for both: seven markers laid out around you, and twelve bodies
/// placed from whatever the tracker currently believes about each player. Drive it
/// with a paced replay and a recorded match acts itself out in front of you.
/// </summary>
public sealed class DemoDirector(IObjectTable objectTable, BlitzGame state)
{
    /// <summary>
    /// Design units to world units. The Blitzsphere is 600 wide, so this puts the
    /// far goals about 36 units apart, close to a real arena.
    /// </summary>
    private const float WorldScale = 0.06f;

    /// <summary>How far off a marker a body stands, toward the goal they defend.</summary>
    private const float SideOffset = 1.6f;

    private readonly IObjectTable _objectTable = objectTable;
    private readonly BlitzGame _state = state;

    private readonly Dictionary<Waymark, Vector3> _markers = new();
    private readonly List<PlayerPosition> _bodies = [];

    public bool Enabled { get; private set; }

    /// <summary>Where the arena was anchored when the demo was switched on.</summary>
    public Vector3 Anchor { get; private set; }

    public IReadOnlyDictionary<Waymark, Vector3> Markers => _markers;

    public IReadOnlyList<PlayerPosition> Bodies => _bodies;

    /// <summary>
    /// A stand-in squad, used when no real roster is loaded so the demo is one
    /// command rather than a form to fill in first. Shares the simulator's roster so
    /// the two testing paths line up.
    /// </summary>
    public static Roster DemoRoster() => Core.Simulation.MatchSimulator.StandardRoster();

    /// <summary>
    /// Stand up the practice arena.
    ///
    /// The fabricated markers built here are only a fallback: if real waymarks are
    /// placed, <see cref="WaymarkReader"/> uses those instead and the stand-in
    /// bodies are positioned on the genuine arena. That way this can be switched on
    /// while standing in a real venue to see how the overlay behaves there.
    ///
    /// Returns false when there is no local player to anchor to.
    /// </summary>
    public bool Enable()
    {
        var local = _objectTable.LocalPlayer;
        if (local is null) return false;

        // Push the arena out in front so you are stood at the edge looking in,
        // rather than inside the middle of it.
        Anchor = local.Position;

        _markers.Clear();
        foreach (var (waymark, design) in BlitzsphereLayout.Nodes)
        {
            // Design space has +X toward goal Four and +Y down the lanes. Map that
            // onto world X and Z, keeping the arena flat at the anchor's height.
            var offsetX = (design.X - (BlitzsphereLayout.ViewWidth * 0.5f)) * WorldScale;
            var offsetZ = (design.Y - (BlitzsphereLayout.ViewHeight * 0.5f)) * WorldScale;

            _markers[waymark] = new Vector3(Anchor.X + offsetX, Anchor.Y, Anchor.Z + offsetZ);
        }

        Enabled = true;
        Refresh(_markers);
        return true;
    }

    public void Disable()
    {
        Enabled = false;
        _markers.Clear();
        _bodies.Clear();
    }

    /// <summary>
    /// Rebuild the stand-in bodies from the tracker's current view of the game, so a
    /// simulated or replayed match moves them around the arena.
    /// </summary>
    /// <param name="markers">
    /// The arena actually in use. Real waymarks when they are placed, otherwise the
    /// fabricated ones from <see cref="Enable"/>.
    /// </param>
    public void Refresh(IReadOnlyDictionary<Waymark, Vector3> markers)
    {
        _bodies.Clear();
        if (!Enabled) return;

        if (!markers.TryGetValue(Waymark.D, out var goalD) ||
            !markers.TryGetValue(Waymark.Four, out var goalFour))
            return;

        var axis = Vector3.Normalize(new Vector3(goalFour.X - goalD.X, 0f, goalFour.Z - goalD.Z));

        // Spread players sharing a waymark so they do not stack into one body.
        var perWaymark = new Dictionary<Waymark, int>();

        foreach (var player in _state.Players.Values)
        {
            var waymark = player.Position;
            if (waymark == Waymark.None) continue;
            if (!markers.TryGetValue(waymark, out var marker)) continue;

            var defendsD = DefendsD(player);

            // Stand on the side of the marker toward your own goal, as at kickoff.
            var side = defendsD ? -SideOffset : SideOffset;

            var index = perWaymark.GetValueOrDefault(waymark);
            perWaymark[waymark] = index + 1;

            // Fan players out perpendicular to the goal axis so several on one
            // marker stay individually visible.
            var perpendicular = new Vector3(-axis.Z, 0f, axis.X);
            var spread = ((index % 3) - 1) * 0.9f;

            var position = marker + (axis * side) + (perpendicular * spread);

            // Face your own goal's opposite: forward is the direction you attack.
            var facing = MathF.Atan2(axis.X, axis.Z) + (defendsD ? 0f : MathF.PI);

            _bodies.Add(new PlayerPosition(player.Name, position, facing));
        }
    }

    private bool DefendsD(PlayerState player)
    {
        var isHome = player.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase);

        // Home defends D in Set 1 and Four in Set 2, matching BlitzGame.
        return _state.Set == 1 ? isHome : !isHome;
    }
}
