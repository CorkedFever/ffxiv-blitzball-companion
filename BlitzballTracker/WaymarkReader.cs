using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace BlitzballTracker;

using BlitzballTracker.Core.GameState;

/// <summary>
/// Reads the arena from the running game rather than inferring it from chat.
///
/// Some game state simply never reaches the chat log. A surveyor picks a lane by
/// swimming to it instead of declaring it, and two players sharing a waymark stand
/// on opposite sides of it. Both are plainly visible in the world, so read them.
/// </summary>
public sealed class WaymarkReader
{
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;

    /// <summary>Reusable buffers: this runs every frame, so it must not allocate.</summary>
    private readonly Dictionary<Waymark, Vector3> _markers = new();
    private readonly List<PlayerPosition> _players = [];

    private readonly Configuration _config;

    public WaymarkReader(IObjectTable objectTable, IPluginLog log, Configuration config)
    {
        _objectTable = objectTable;
        _log = log;
        _config = config;
    }

    /// <summary>
    /// How close counts as standing on a waymark. Tunable at the venue, because how
    /// far players float from a marker varies with the pool and with how tidily people
    /// line up.
    /// </summary>
    private float MarkerRadius => Math.Max(0.5f, _config.MarkerRadius);

    /// <summary>True when enough of the Blitzsphere is placed to work with.</summary>
    public bool ArenaReady { get; private set; }

    /// <summary>
    /// True when the arena in use was fabricated by the practice mode rather than
    /// read from real placed waymarks. Surfaced so the UI can say which it is.
    /// </summary>
    public bool UsingFabricatedArena { get; private set; }

    /// <summary>
    /// When set and enabled, markers and bodies come from the demo instead of the
    /// game, so the world-space features can be exercised without a full match.
    /// </summary>
    public DemoDirector? Demo { get; set; }

    private bool UsingDemo => Demo is { Enabled: true };

    /// <summary>
    /// Active field markers, keyed by the blitzball waymark they represent.
    /// Empty when the venue has not placed them.
    /// </summary>
    public unsafe IReadOnlyDictionary<Waymark, Vector3> ReadMarkers()
    {
        _markers.Clear();
        ArenaReady = false;
        UsingFabricatedArena = false;

        // Real waymarks always win. If you are stood in an actual venue, the
        // practice squad should appear on the real arena rather than a fake one
        // fabricated on top of it.
        var controller = MarkingController.Instance();
        if (controller != null)
        {
            for (var slot = 0; slot < 8; slot++)
            {
                var waymark = FieldGeometry.FromMarkerSlot(slot);
                if (waymark == Waymark.None) continue; // slot 3 ("3") is unused here

                var marker = controller->FieldMarkers[slot];
                if (!marker.Active) continue;

                _markers[waymark] = marker.Position;
            }

            // Both goals are needed to establish the axis that separates the two sides.
            ArenaReady = _markers.ContainsKey(Waymark.D) && _markers.ContainsKey(Waymark.Four);
        }

        if (ArenaReady || !UsingDemo) return _markers;

        // No usable arena placed, so fall back to the fabricated one.
        _markers.Clear();
        foreach (var (waymark, position) in Demo!.Markers)
            _markers[waymark] = position;

        ArenaReady = _markers.ContainsKey(Waymark.D) && _markers.ContainsKey(Waymark.Four);
        UsingFabricatedArena = ArenaReady;

        return _markers;
    }

    /// <summary>
    /// Every player character currently loaded, with position and facing.
    ///
    /// Only players within streaming range appear. Everyone in the arena is close
    /// enough; distant spectators may not load, which here is a convenience.
    /// </summary>
    public IReadOnlyList<PlayerPosition> ReadNearbyPlayers()
    {
        _players.Clear();

        if (UsingDemo)
        {
            _players.AddRange(Demo!.Bodies);
            return _players;
        }

        // PlayerObjects is the player-only slice of the table, so this avoids
        // walking every NPC and pet in the zone each poll.
        foreach (var obj in _objectTable.PlayerObjects)
        {
            if (obj is not IPlayerCharacter character) continue;

            var name = character.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name)) continue;

            _players.Add(new PlayerPosition(name, character.Position, character.Rotation));
        }

        return _players;
    }

    /// <summary>
    /// Move every rostered player to the waymark they are physically standing on.
    ///
    /// The game is authoritative over chat here: a declared [MOVE to X] that does not
    /// match where the player actually went is reported rather than obeyed.
    ///
    /// Returns how many rostered players were found standing on a waymark — not how
    /// many moved. A count of changes reads as zero when everyone is already where they
    /// should be, which is indistinguishable from reading nothing at all.
    /// </summary>
    public int SyncPositions(BlitzGame game, Action<string, Waymark, Waymark>? onMismatch = null)
    {
        if (!game.HasRoster) return 0;

        // Demo bodies are placed from the tracked positions, so syncing back from
        // them would just be a loop feeding itself.
        if (UsingDemo) return 0;

        var markers = ReadMarkers();
        if (!ArenaReady) return 0;

        var nearby = ReadNearbyPlayers();
        var placed = 0;

        foreach (var seen in nearby)
        {
            if (!game.Players.TryGetValue(PlayerNames.StripWorld(seen.Name), out var player))
                continue;

            // A surveyor swims out between two markers rather than standing on one,
            // and never says which lane they chose, so read it from where they are.
            player.SurveyedLane = player.IsSurveying
                ? FieldGeometry.NearestLane(seen.Position, markers)
                : null;

            var actual = FieldGeometry.NearestWaymark(seen.Position, markers, MarkerRadius);
            if (actual == Waymark.None) continue;

            placed++;

            if (player.Position == actual) continue;

            // Nothing to contradict when we had no idea where they were, which is the
            // state a match joined in progress starts from.
            if (player.Position != Waymark.None)
                onMismatch?.Invoke(player.Name, player.Position, actual);

            player.Position = actual;
        }

        return placed;
    }

    /// <summary>
    /// Build a roster from where everyone is standing at kickoff.
    ///
    /// The starting layout is deterministic, so waymark plus side-of-waymark gives
    /// each player's team and role. Intended as a starting point the user confirms,
    /// not as gospel: anyone loitering on a marker will be picked up too.
    /// </summary>
    public Roster? DetectFormation(string homeTeam, string awayTeam)
    {
        var markers = ReadMarkers();
        if (!ArenaReady)
        {
            _log.Warning("[BlitzTracker] Cannot detect formation: goals D and 4 are not both placed.");
            return null;
        }

        var readings = FieldGeometry.ReadFormation(ReadNearbyPlayers(), markers, MarkerRadius);
        if (readings.Count == 0) return null;

        return FieldGeometry.ToRoster(readings, homeTeam, awayTeam);
    }
}
