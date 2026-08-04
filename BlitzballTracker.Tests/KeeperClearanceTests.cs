using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A goalkeeper does not carry the ball. However they come by it — a save, a caught
/// fumble — they send it straight back out, and that pass resolves before play moves
/// on rather than waiting for a ball carrier turn.
/// </summary>
public class KeeperClearanceTests
{
    private const string Ref = "Sim Referee";
    private const string Scorer = "Sim Scorekeeper";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    private static PlayerState Keeper(BlitzGame game, string team) =>
        game.Players.Values.First(p =>
            p.Role == PlayerRole.Goalkeeper && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void AKeeperHoldingTheBallOwesAPass()
    {
        var (game, parser) = NewGame();
        var keeper = Keeper(game, "SIM GOLD");

        parser.ProcessMessage(Scorer, $"[BALL to {keeper.Name}]", DateTime.Now);

        Assert.True(game.KeeperMustClear);
        Assert.Contains(game.PlayByPlay, l => l.Contains(keeper.Name) && l.Contains("must pass it straight out"));
    }

    [Fact]
    public void TheObligationLiftsTheMomentTheBallLeaves()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var keeper = Keeper(game, "SIM GOLD");
        var mate = game.Players.Values.First(p =>
            p.Team.Equals(keeper.Team, StringComparison.OrdinalIgnoreCase) && !p.IsGoalkeeper);

        parser.ProcessMessage(Scorer, $"[BALL to {keeper.Name}]", now);
        parser.ProcessMessage(Scorer, $"[[PASS COMPLETE to {mate.Name} ]]", now);

        Assert.False(game.KeeperMustClear);
        Assert.Equal(mate.Name, game.BallCarrier);
    }

    /// <summary>
    /// The clearing pass does not wait for the keeper's ring to come round, so it must
    /// not draw the acting-out-of-turn advisory.
    /// </summary>
    [Fact]
    public void ClearingOutsideTheKeepersRingIsNotFlagged()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var keeper = Keeper(game, "SIM GOLD");
        var mate = game.Players.Values.First(p =>
            p.Team.Equals(keeper.Team, StringComparison.OrdinalIgnoreCase) && !p.IsGoalkeeper);

        // Keepers hold an inner zone, so an outer phase is not theirs to act in.
        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(Scorer, $"[BALL to {keeper.Name}]", now);
        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} sends it straight back out. [PASS -> {mate.Name}]", now);

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("ring is active"));
    }

    /// <summary>
    /// A keeper stands at their own goal, so every direction is forward. Their pass is
    /// exempt by rule regardless (slide 42), and measuring it only produced false
    /// tracking warnings.
    /// </summary>
    [Fact]
    public void AKeepersPassIsNeverReadAsBackward()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var keeper = Keeper(game, "SIM GOLD");
        var mate = game.Players.Values.First(p =>
            p.Team.Equals(keeper.Team, StringComparison.OrdinalIgnoreCase) && !p.IsGoalkeeper);

        parser.ProcessMessage(Scorer, $"[BALL to {keeper.Name}]", now);
        parser.ProcessMessage(Scorer, $"[[PASS COMPLETE to {mate.Name} ]]", now);

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("backward pass"));
    }

    [Fact]
    public void HoldingItIntoTheNextPhaseIsFlagged()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var keeper = Keeper(game, "SIM GOLD");

        parser.ProcessMessage(Scorer, $"[BALL to {keeper.Name}]", now);
        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now.AddSeconds(20));

        Assert.Contains(game.PlayByPlay,
            l => l.Contains('⚑') && l.Contains(keeper.Name) && l.Contains("clearing pass was owed"));
    }

    /// <summary>
    /// The clearing pass has to respect the keeper's reach.
    ///
    /// The generator used to throw to any team-mate ahead of them, which put the ball
    /// three zones downfield with somebody standing two zones away — a fumble, not a
    /// clearance. A keeper with genuinely nobody in reach would still produce this
    /// advisory legitimately, so if it ever fires, check the positions first.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    [InlineData(31337)]
    public void GeneratedKeepersThrowWithinTheirReach(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        var overreach = game.PlayByPlay
            .Where(l => l.Contains("goalkeeper may only throw") || l.Contains("cannot throw more than"))
            .ToList();

        Assert.True(overreach.Count == 0,
            $"Keeper threw beyond their reach:{Environment.NewLine}" +
            string.Join(Environment.NewLine, overreach.Take(5)));
    }

    /// <summary>A keeper should never reach a ball carrier turn still holding it.</summary>
    [Fact]
    public void AKeeperNeverReachesACarrierTurnWithTheBall()
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, 2024).Generate(), parser);

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("clearing pass was owed"));
    }
}
