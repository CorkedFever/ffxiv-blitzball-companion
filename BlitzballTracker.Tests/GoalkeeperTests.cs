using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Goalkeepers hold their goal and never leave it.
///
/// This was a comment on the role enum rather than a rule the code enforced, so a
/// mistyped move, or a tackle resolving in the keeper's favour (a successful tackle
/// moves the tackler to their target's zone), was enough to walk a keeper out to
/// Center carrying the ball.
/// </summary>
public class GoalkeeperTests
{
    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    private static PlayerState Keeper(BlitzGame game, string team) =>
        game.Players.Values.First(p =>
            p.Role == PlayerRole.Goalkeeper &&
            p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void KeepersStartOnOpposingGoals()
    {
        var (game, _) = NewGame();

        var red = Keeper(game, "SIM RED");
        var gold = Keeper(game, "SIM GOLD");

        Assert.Equal(Waymark.D, red.Position);
        Assert.Equal(Waymark.Four, gold.Position);
        Assert.NotEqual(red.Position, gold.Position);
    }

    [Fact]
    public void TryPlaceRefusesToMoveAKeeperOffGoal()
    {
        var (game, _) = NewGame();
        var keeper = Keeper(game, "SIM RED");

        Assert.False(game.TryPlace(keeper, Waymark.C));
        Assert.Equal(Waymark.D, keeper.Position);

        // Placing them back on their own goal is fine.
        Assert.True(game.TryPlace(keeper, Waymark.D));
    }

    [Fact]
    public void TryPlaceAllowsOutfieldPlayersToMoveFreely()
    {
        var (game, _) = NewGame();
        var mid = game.Players.Values.First(p => p.Role == PlayerRole.Midfield);

        Assert.True(game.TryPlace(mid, Waymark.B));
        Assert.Equal(Waymark.B, mid.Position);
    }

    [Fact]
    public void AParsedMoveCannotTakeAKeeperOffGoal()
    {
        var (game, parser) = NewGame();
        var keeper = Keeper(game, "SIM RED");

        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} swims out. [MOVE to C]", DateTime.Now);

        Assert.Equal(Waymark.D, keeper.Position);
        Assert.Contains(game.PlayByPlay, line => line.Contains("cannot enter"));
    }

    [Fact]
    public void KeepersDefendTheOtherGoalAfterHalftime()
    {
        var (game, _) = NewGame();

        var red = Keeper(game, "SIM RED");
        Assert.Equal(Waymark.D, game.OwnGoal(red));

        game.SwitchSides();
        game.ResetPositions();

        Assert.Equal(Waymark.Four, game.OwnGoal(red));
        Assert.Equal(Waymark.Four, red.Position);
    }

    /// <summary>
    /// The property that matters, checked across whole generated matches rather than
    /// one contrived message.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    [InlineData(31337)]
    [InlineData(8675309)]
    public void KeepersNeverLeaveGoalAcrossAWholeMatch(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        foreach (var keeper in game.Players.Values.Where(p => p.Role == PlayerRole.Goalkeeper))
        {
            Assert.Equal(game.OwnGoal(keeper), keeper.Position);
        }
    }

    /// <summary>
    /// The simulator must not generate illegal play either. Passing to the first
    /// teammate sent nearly every pass to the keeper, because keepers sit at index
    /// zero of a squad.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(256)]
    public void GeneratedMatchesDoNotSendKeepersIntoFieldPlay(int seed)
    {
        var roster = MatchSimulator.StandardRoster();
        var keeperNames = roster.Entries
            .Where(e => e.Role == PlayerRole.Goalkeeper)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lines = new MatchSimulator(roster, seed).Generate();

        foreach (var line in lines)
        {
            // Keepers never declare field actions or receive passes.
            foreach (var keeper in keeperNames)
            {
                Assert.DoesNotContain($"[PASS -> {keeper}]", line.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain($"PASS COMPLETE to {keeper}", line.Message, StringComparison.OrdinalIgnoreCase);
            }

            if (!keeperNames.Contains(line.Sender)) continue;

            Assert.DoesNotContain("[TACKLE", line.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[MOVE", line.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
