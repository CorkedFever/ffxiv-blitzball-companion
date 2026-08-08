using System.Numerics;
using BlitzballTracker.Core.GameState;
using Xunit;

namespace BlitzballTracker.Tests;

public class FieldGeometryTests
{
    /// <summary>
    /// A synthetic arena laid out like the Blitzsphere: goal D at one end, goal Four
    /// at the other, letter lane (A/B) on one side, number lane (1/2) on the other.
    /// World X runs from D to Four; Z is the lane axis. Y stays flat here, but the
    /// production code deliberately ignores it because players sit at varying depths.
    /// </summary>
    private static Dictionary<Waymark, Vector3> Arena() => new()
    {
        [Waymark.D] = new Vector3(60, 0, 170),
        [Waymark.One] = new Vector3(170, 0, 80),
        [Waymark.A] = new Vector3(170, 0, 260),
        [Waymark.C] = new Vector3(300, 0, 170),
        [Waymark.Two] = new Vector3(430, 0, 80),
        [Waymark.B] = new Vector3(430, 0, 260),
        [Waymark.Four] = new Vector3(540, 0, 170),
    };

    /// <summary>
    /// Standing on the D side of a marker: the side the D-defenders take.
    ///
    /// Well inside <see cref="FieldGeometry.OnMarkerRadius"/>, because being on a
    /// waymark now means being on it rather than somewhere near it.
    /// </summary>
    private static Vector3 TowardD(Vector3 marker) => marker with { X = marker.X - 1f };

    /// <summary>Standing on the Four side of a marker.</summary>
    private static Vector3 TowardFour(Vector3 marker) => marker with { X = marker.X + 1f };

    /// <summary>
    /// A match venue is ringed with spectators, and plenty of them stand within a few
    /// yards of a marker — including whoever is running the tracker.
    ///
    /// Proximity alone used to be enough to be called a player, so detection dragged
    /// the crowd in. The kickoff formation is fixed: each marker holds a known number
    /// of players, so only the closest that many count.
    /// </summary>
    [Fact]
    public void ReadFormation_IgnoresTheCrowdStandingNearAMarker()
    {
        var arena = Arena();
        var markerTwo = arena[Waymark.Two];

        var readings = FieldGeometry.ReadFormation(
        [
            // The two actually lined up, close in and facing each other across it.
            new PlayerPosition("Home Winger", TowardD(markerTwo), 0f),
            new PlayerPosition("Away Fullback", TowardFour(markerTwo), 0f),

            // Onlookers, further out but still inside the radius — so it is the
            // per-marker cap keeping them out, not the distance check.
            new PlayerPosition("Bystander", markerTwo with { X = markerTwo.X - 2.5f }, 0f),
            new PlayerPosition("Someone Watching", markerTwo with { X = markerTwo.X + 2.5f }, 0f),
            new PlayerPosition("Also Loitering", markerTwo with { Z = markerTwo.Z + 2.5f }, 0f),
        ], arena);

        Assert.Equal(2, readings.Count);
        Assert.Contains(readings, r => r.Name == "Home Winger");
        Assert.Contains(readings, r => r.Name == "Away Fullback");
        Assert.DoesNotContain(readings, r => r.Name == "Bystander");
        Assert.DoesNotContain(readings, r => r.Name == "Someone Watching");
        Assert.DoesNotContain(readings, r => r.Name == "Also Loitering");
    }

    /// <summary>A goal holds its keeper and nobody else, however many are milling about.</summary>
    [Fact]
    public void ReadFormation_TakesOneKeeperPerGoal()
    {
        var arena = Arena();
        var goal = arena[Waymark.D];

        var readings = FieldGeometry.ReadFormation(
        [
            new PlayerPosition("Keeper", goal with { X = goal.X + 0.5f }, 0f),
            new PlayerPosition("Hanger On", goal with { X = goal.X + 2f }, 0f),
            new PlayerPosition("Another", goal with { Z = goal.Z + 2.5f }, 0f),
        ], arena);

        var atGoal = readings.Where(r => r.Waymark == Waymark.D).ToList();

        Assert.Single(atGoal);
        Assert.Equal("Keeper", atGoal[0].Name);
        Assert.Equal(PlayerRole.Goalkeeper, atGoal[0].Role);
    }

    /// <summary>
    /// Two on the same side of a marker is not a formation — one of them is standing
    /// about. Only the nearer counts, and the far side stays empty.
    /// </summary>
    [Fact]
    public void ReadFormation_TakesOnlyTheNearestPerSide()
    {
        var arena = Arena();
        var markerA = arena[Waymark.A];

        var readings = FieldGeometry.ReadFormation(
        [
            new PlayerPosition("Nearer", markerA with { X = markerA.X - 1f }, 0f),
            new PlayerPosition("Further", markerA with { X = markerA.X - 2.5f }, 0f),
        ], arena);

        Assert.Single(readings);
        Assert.Equal("Nearer", readings[0].Name);
    }

    [Fact]
    public void NearestWaymark_PicksClosestMarker()
    {
        var arena = Arena();
        var nearC = new Vector3(302, 0, 171);

        Assert.Equal(Waymark.C, FieldGeometry.NearestWaymark(nearC, arena));
    }

    [Fact]
    public void NearestWaymark_IgnoresVerticalDistance()
    {
        var arena = Arena();

        // Blitzball is underwater: a player floating well above C is still at C.
        var highAboveC = new Vector3(300, 40, 170);

        Assert.Equal(Waymark.C, FieldGeometry.NearestWaymark(highAboveC, arena));
    }

    [Fact]
    public void NearestWaymark_ReturnsNoneForDistantSpectators()
    {
        var arena = Arena();
        var inTheStands = new Vector3(300, 0, 900);

        Assert.Equal(Waymark.None, FieldGeometry.NearestWaymark(inTheStands, arena));
    }

    /// <summary>
    /// The property that pins the whole convention: a right forward lines up against
    /// the opposing left defender, both standing on the same static marker, because
    /// left and right mirror with each team's facing.
    /// </summary>
    [Fact]
    public void ReadFormation_RightForwardFacesOpposingLeftDefender()
    {
        var arena = Arena();
        var markerTwo = arena[Waymark.Two];

        var readings = FieldGeometry.ReadFormation(
        [
            // Defends D, so pressing the far strike zone as a forward.
            new PlayerPosition("Home Winger", TowardD(markerTwo), 0f),
            // Defends Four, so holding their own strike zone.
            new PlayerPosition("Away Fullback", TowardFour(markerTwo), 0f),
        ], arena);

        var forward = readings.Single(r => r.Name == "Home Winger");
        var defender = readings.Single(r => r.Name == "Away Fullback");

        Assert.Equal(PlayerRole.RightForward, forward.Role);
        Assert.Equal(PlayerRole.LeftDefender, defender.Role);

        // Same static marker, opposite sides, opposite teams.
        Assert.Equal(Waymark.Two, forward.Waymark);
        Assert.Equal(Waymark.Two, defender.Waymark);
        Assert.True(forward.DefendsD);
        Assert.False(defender.DefendsD);
    }

    [Fact]
    public void ReadFormation_ReadsGoalkeepersFromTheirGoals()
    {
        var arena = Arena();

        var readings = FieldGeometry.ReadFormation(
        [
            new PlayerPosition("Keeper D", arena[Waymark.D], 0f),
            new PlayerPosition("Keeper Four", arena[Waymark.Four], 0f),
        ], arena);

        Assert.All(readings, r => Assert.Equal(PlayerRole.Goalkeeper, r.Role));
        Assert.True(readings.Single(r => r.Name == "Keeper D").DefendsD);
        Assert.False(readings.Single(r => r.Name == "Keeper Four").DefendsD);
    }

    [Fact]
    public void ReadFormation_SplitsMidfieldersAtCentreBySide()
    {
        var arena = Arena();
        var centre = arena[Waymark.C];

        var readings = FieldGeometry.ReadFormation(
        [
            new PlayerPosition("Home Mid", TowardD(centre), 0f),
            new PlayerPosition("Away Mid", TowardFour(centre), 0f),
        ], arena);

        Assert.All(readings, r => Assert.Equal(PlayerRole.Midfield, r.Role));
        Assert.True(readings.Single(r => r.Name == "Home Mid").DefendsD);
        Assert.False(readings.Single(r => r.Name == "Away Mid").DefendsD);
    }

    /// <summary>
    /// A full twelve-player kickoff should reproduce exactly the layout
    /// <see cref="BlitzGame.StartingPosition"/> would place, for both sides.
    /// </summary>
    [Fact]
    public void ReadFormation_RoundTripsThroughStartingPositions()
    {
        var arena = Arena();
        var players = new List<PlayerPosition>();

        var roles = new[]
        {
            PlayerRole.Goalkeeper, PlayerRole.Midfield,
            PlayerRole.LeftDefender, PlayerRole.RightDefender,
            PlayerRole.LeftForward, PlayerRole.RightForward,
        };

        foreach (var role in roles)
        {
            var homeMark = BlitzGame.StartingPosition(role, Waymark.D);
            var awayMark = BlitzGame.StartingPosition(role, Waymark.Four);

            players.Add(new PlayerPosition($"home-{role}",
                role == PlayerRole.Goalkeeper ? arena[homeMark] : TowardD(arena[homeMark]), 0f));
            players.Add(new PlayerPosition($"away-{role}",
                role == PlayerRole.Goalkeeper ? arena[awayMark] : TowardFour(arena[awayMark]), 0f));
        }

        var readings = FieldGeometry.ReadFormation(players, arena);

        Assert.Equal(12, readings.Count);

        foreach (var role in roles)
        {
            var home = readings.Single(r => r.Name == $"home-{role}");
            var away = readings.Single(r => r.Name == $"away-{role}");

            Assert.Equal(role, home.Role);
            Assert.Equal(role, away.Role);
            Assert.True(home.DefendsD);
            Assert.False(away.DefendsD);
        }
    }

    [Fact]
    public void ToRoster_AssignsDefendersOfDToHomeTeam()
    {
        var arena = Arena();
        var readings = FieldGeometry.ReadFormation(
        [
            new PlayerPosition("Keeper D", arena[Waymark.D], 0f),
            new PlayerPosition("Keeper Four", arena[Waymark.Four], 0f),
        ], arena);

        var roster = FieldGeometry.ToRoster(readings, "DAIGOROS", "AUSPICES");

        Assert.Equal("DAIGOROS", roster.Entries.Single(e => e.Name == "Keeper D").Team);
        Assert.Equal("AUSPICES", roster.Entries.Single(e => e.Name == "Keeper Four").Team);
    }
}
