using System.Numerics;

namespace BlitzballTracker.Core.GameState;

/// <summary>A player's physical position in the arena.</summary>
public readonly record struct PlayerPosition(string Name, Vector3 Position, float Rotation);

/// <summary>What the formation reader concluded about one player.</summary>
public readonly record struct FormationReading(
    string Name,
    Waymark Waymark,
    bool DefendsD,
    PlayerRole Role);

/// <summary>
/// Geometry over the Blitzsphere's real in-game waymarks.
///
/// The chat log cannot carry everything: a surveyor picks a lane by swimming to it
/// rather than declaring it, and two players sharing a waymark stand on opposite
/// sides of it. All of that is visible in the world, so the plugin reads it directly
/// instead of guessing. This class holds the pure math so it can be tested without
/// a running game.
/// </summary>
public static class FieldGeometry
{
    /// <summary>
    /// FFXIV field marker slot indices: A B C D 1 2 3 4.
    /// Blitzball uses seven of the eight; marker 3 is unused.
    /// </summary>
    public static int? MarkerSlot(Waymark waymark) => waymark switch
    {
        Waymark.A => 0,
        Waymark.B => 1,
        Waymark.C => 2,
        Waymark.D => 3,
        Waymark.One => 4,
        Waymark.Two => 5,
        Waymark.Four => 7,
        _ => null,
    };

    public static Waymark FromMarkerSlot(int slot) => slot switch
    {
        0 => Waymark.A,
        1 => Waymark.B,
        2 => Waymark.C,
        3 => Waymark.D,
        4 => Waymark.One,
        5 => Waymark.Two,
        7 => Waymark.Four,
        _ => Waymark.None,
    };

    /// <summary>The four strike zones, which hold two opposing players at kickoff.</summary>
    public static bool IsStrikeZone(Waymark w) =>
        w is Waymark.A or Waymark.B or Waymark.One or Waymark.Two;

    /// <summary>Letter lane (A/B) maps to Left roles, number lane (1/2) to Right roles.</summary>
    public static bool IsLetterLane(Waymark w) => w is Waymark.A or Waymark.B;

    /// <summary>Strike zones adjacent to goal D.</summary>
    public static bool IsAdjacentToD(Waymark w) => w is Waymark.A or Waymark.One;

    /// <summary>
    /// Horizontal (XZ) distance. Blitzball is played underwater, so two players can
    /// share a waymark at very different depths; including Y would mis-assign them.
    /// </summary>
    public static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// The waymark a player is standing on, or None when they are further than
    /// <paramref name="maxDistance"/> from every marker (spectators, benched players).
    /// </summary>
    public static Waymark NearestWaymark(
        Vector3 position,
        IReadOnlyDictionary<Waymark, Vector3> markers,
        float maxDistance = 15f)
    {
        var best = Waymark.None;
        var bestDistance = float.MaxValue;

        foreach (var (waymark, markerPos) in markers)
        {
            var distance = HorizontalDistance(position, markerPos);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = waymark;
            }
        }

        return bestDistance <= maxDistance ? best : Waymark.None;
    }

    /// <summary>
    /// The lane a position sits closest to, or null when it is not near one.
    ///
    /// A surveyor guards a lane by swimming out between two markers rather than
    /// standing on either, and never says which lane they picked. Reading it back
    /// from where they are is the only way to know.
    /// </summary>
    public static (Waymark From, Waymark To)? NearestLane(
        Vector3 position,
        IReadOnlyDictionary<Waymark, Vector3> markers,
        float maxDistance = 12f)
    {
        (Waymark From, Waymark To)? best = null;
        var bestDistance = float.MaxValue;

        foreach (var (from, to) in BlitzsphereLayout.Lanes)
        {
            if (!markers.TryGetValue(from, out var start)) continue;
            if (!markers.TryGetValue(to, out var end)) continue;

            var distance = DistanceToSegment(position, start, end);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = (from, to);
        }

        return bestDistance <= maxDistance ? best : null;
    }

    /// <summary>
    /// Horizontal distance from a point to a line segment. Depth is ignored for the
    /// same reason as everywhere else here: the players are swimming.
    /// </summary>
    private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        var px = point.X - start.X;
        var pz = point.Z - start.Z;

        var bx = end.X - start.X;
        var bz = end.Z - start.Z;

        var lengthSquared = (bx * bx) + (bz * bz);
        if (lengthSquared < 0.0001f) return MathF.Sqrt((px * px) + (pz * pz));

        // Project onto the segment and clamp, so the ends do not extend past the markers.
        var t = Math.Clamp(((px * bx) + (pz * bz)) / lengthSquared, 0f, 1f);

        var dx = px - (t * bx);
        var dz = pz - (t * bz);

        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    /// Which half of a waymark a player stands on, as a signed projection onto the
    /// axis running from goal D to goal Four.
    ///
    /// Negative means the D side, positive the Four side. At kickoff each player
    /// stands on the side toward their own goal and faces the enemy goal, so this
    /// sign identifies which goal they defend. It is the reason two players sharing
    /// a strike zone can be told apart at all.
    /// </summary>
    public static float GoalAxisOffset(
        Vector3 position,
        Vector3 markerPosition,
        Vector3 goalD,
        Vector3 goalFour)
    {
        var axis = new Vector2(goalFour.X - goalD.X, goalFour.Z - goalD.Z);
        if (axis.LengthSquared() < 0.0001f) return 0f;
        axis = Vector2.Normalize(axis);

        var offset = new Vector2(position.X - markerPosition.X, position.Z - markerPosition.Z);
        return Vector2.Dot(offset, axis);
    }

    /// <summary>
    /// Read team and role for every player from the kickoff formation.
    ///
    /// The starting layout is deterministic (see <see cref="BlitzGame.ResetPositions"/>):
    /// goalkeeper on their own goal, midfielder at C, defenders in their own strike
    /// zone, forwards in the enemy strike zone, split across the letter and number
    /// lanes. So waymark plus side-of-waymark determines role uniquely.
    ///
    /// Returns a reading per player found on a waymark; players elsewhere are skipped.
    /// </summary>
    public static List<FormationReading> ReadFormation(
        IReadOnlyList<PlayerPosition> players,
        IReadOnlyDictionary<Waymark, Vector3> markers,
        float maxDistance = 15f)
    {
        var readings = new List<FormationReading>();

        if (!markers.TryGetValue(Waymark.D, out var goalD) ||
            !markers.TryGetValue(Waymark.Four, out var goalFour))
        {
            return readings; // Without both goals there is no axis to measure against.
        }

        // Everyone standing near a marker, with how near and which side of it they are
        // on. Proximity alone is not enough to call somebody a player: a match venue is
        // ringed with spectators, and plenty of them are within a few yards of a marker.
        var candidates = new List<(string Name, Waymark Waymark, float Distance, float Offset)>();

        foreach (var player in players)
        {
            var waymark = NearestWaymark(player.Position, markers, maxDistance);
            if (waymark == Waymark.None) continue;

            var markerPos = markers[waymark];

            candidates.Add((
                player.Name,
                waymark,
                HorizontalDistance(player.Position, markerPos),
                GoalAxisOffset(player.Position, markerPos, goalD, goalFour)));
        }

        // The kickoff formation is fixed, so each marker holds a known number of
        // players and no more. Taking only the closest that many is what keeps the
        // crowd out — a bystander has to be nearer the marker than the player actually
        // lined up on it to displace them.
        foreach (var waymark in markers.Keys)
        {
            var here = new List<(string Name, Waymark Waymark, float Distance, float Offset)>();

            foreach (var candidate in candidates)
            {
                if (candidate.Waymark == waymark) here.Add(candidate);
            }

            if (here.Count == 0) continue;

            here.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

            // A goal holds its keeper and nobody else.
            if (waymark is Waymark.D or Waymark.Four)
            {
                readings.Add(new FormationReading(
                    here[0].Name, waymark, DefendsD: waymark == Waymark.D, PlayerRole.Goalkeeper));
                continue;
            }

            // Every other marker holds exactly two at kickoff, one from each side,
            // facing each other across it. So take the nearest on each side and stop.
            foreach (var defendsD in new[] { true, false })
            {
                foreach (var candidate in here)
                {
                    if (candidate.Offset < 0f != defendsD) continue;

                    var role = waymark == Waymark.C
                        // Centre holds one midfielder from each side.
                        ? PlayerRole.Midfield

                        // A strike zone holds the defender of the adjacent goal and the
                        // forward attacking it. Lane decides Left versus Right.
                        : StrikeRole(waymark, defendsD);

                    readings.Add(new FormationReading(candidate.Name, waymark, defendsD, role));
                    break;
                }
            }
        }

        return readings;
    }

    /// <summary>
    /// A player in a strike zone is a defender when the zone is next to the goal they
    /// defend, and a forward otherwise.
    ///
    /// Left and right are relative to each team's facing, since the two sides face
    /// opposite ways down the pool. That is why a right forward lines up against the
    /// opposing left defender rather than their right defender: on any single lane,
    /// one team's Right role and the other team's Left role occupy the same zone.
    ///
    /// Note this disagrees with <see cref="BlitzGame.ResetPositions"/>, which puts
    /// LeftDefender on the letter lane for both teams.
    /// </summary>
    private static PlayerRole StrikeRole(Waymark waymark, bool defendsD)
    {
        var ownZone = IsAdjacentToD(waymark) == defendsD;

        // Letter lane is the left flank for the side defending D, and the right
        // flank for the side defending Four.
        var isLeft = IsLetterLane(waymark) == defendsD;

        return (ownZone, isLeft) switch
        {
            (true, true) => PlayerRole.LeftDefender,
            (true, false) => PlayerRole.RightDefender,
            (false, true) => PlayerRole.LeftForward,
            (false, false) => PlayerRole.RightForward,
        };
    }

    /// <summary>
    /// Turn a formation reading into a roster. The team defending D is Home, matching
    /// <see cref="BlitzGame"/> where Home attacks Four in Set 1.
    /// </summary>
    public static Roster ToRoster(
        IReadOnlyList<FormationReading> readings,
        string homeTeam,
        string awayTeam)
    {
        var roster = new Roster { HomeTeam = homeTeam, AwayTeam = awayTeam };

        foreach (var reading in readings)
        {
            roster.Entries.Add(new RosterEntry
            {
                Name = reading.Name,
                Team = reading.DefendsD ? homeTeam : awayTeam,
                Role = reading.Role,
            });
        }

        return roster;
    }
}
