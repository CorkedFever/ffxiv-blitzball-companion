using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// The ball never travels backwards. It may cross between the two lanes at the same
/// rank, but it does not retreat toward the passer's own goal.
///
/// Because that never happens in play, the parser treats a backward pass as evidence
/// that it has someone in the wrong zone rather than as a player infraction.
/// </summary>
public class PassDirectionTests
{
    private static BlitzGame NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return game;
    }

    private static PlayerState HomePlayer(BlitzGame game) =>
        game.Players.Values.First(p =>
            p.Team.Equals(game.HomeTeam, StringComparison.OrdinalIgnoreCase) &&
            p.Role != PlayerRole.Goalkeeper);

    private static PlayerState AwayPlayer(BlitzGame game) =>
        game.Players.Values.First(p =>
            p.Team.Equals(game.AwayTeam, StringComparison.OrdinalIgnoreCase) &&
            p.Role != PlayerRole.Goalkeeper);

    [Fact]
    public void ZonesAreRankedFromOneGoalToTheOther()
    {
        Assert.Equal(0, BlitzGame.ZoneRank(Waymark.D));
        Assert.Equal(2, BlitzGame.ZoneRank(Waymark.C));
        Assert.Equal(4, BlitzGame.ZoneRank(Waymark.Four));

        // Parallel lanes: the two strike zones beside a goal are level with each other.
        Assert.Equal(BlitzGame.ZoneRank(Waymark.One), BlitzGame.ZoneRank(Waymark.A));
        Assert.Equal(BlitzGame.ZoneRank(Waymark.Two), BlitzGame.ZoneRank(Waymark.B));
    }

    [Fact]
    public void CrossingBetweenLanesIsNotBackward()
    {
        var game = NewGame();
        var player = HomePlayer(game);

        // 1 to A is a sideways ball, not a retreat.
        Assert.False(game.IsBackwardPass(player, Waymark.One, Waymark.A));
        Assert.False(game.IsBackwardPass(player, Waymark.B, Waymark.Two));
    }

    [Fact]
    public void TheTwoSidesAdvanceInOppositeDirections()
    {
        var game = NewGame();

        var home = HomePlayer(game);   // defends D, attacks Four
        var away = AwayPlayer(game);   // defends Four, attacks D

        Assert.Equal(Waymark.Four, game.AttackingGoal(home));
        Assert.Equal(Waymark.D, game.AttackingGoal(away));

        // C to B advances for home and retreats for away.
        Assert.False(game.IsBackwardPass(home, Waymark.C, Waymark.B));
        Assert.True(game.IsBackwardPass(away, Waymark.C, Waymark.B));

        // And the reverse holds going the other way.
        Assert.True(game.IsBackwardPass(home, Waymark.C, Waymark.A));
        Assert.False(game.IsBackwardPass(away, Waymark.C, Waymark.A));
    }

    [Fact]
    public void DirectionFlipsWhenTeamsSwapEndsAtHalftime()
    {
        var game = NewGame();
        var home = HomePlayer(game);

        Assert.Equal(Waymark.Four, game.AttackingGoal(home));

        game.SwitchSides();

        // Same player, other end, so forward is now the other way.
        Assert.Equal(Waymark.D, game.AttackingGoal(home));
        Assert.True(game.IsBackwardPass(home, Waymark.C, Waymark.B));
    }

    /// <summary>
    /// The generator must not produce passes that cannot happen, or every simulated
    /// match would be littered with tracking warnings.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    [InlineData(31337)]
    [InlineData(8675309)]
    public void GeneratedMatchesContainNoBackwardPasses(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        var complaints = game.PlayByPlay
            .Where(line => line.Contains("backward pass", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(complaints.Count == 0,
            $"Generated match produced backward passes:{Environment.NewLine}" +
            string.Join(Environment.NewLine, complaints.Take(5)));
    }
}
