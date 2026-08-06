using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Rush Gates belong to a side, not to the field.
///
/// The goalkeeper is the one role that cannot move, so a gate is their only way to
/// reach past their own goal. Each keeper may have one standing, and it lasts the
/// round it was placed in.
/// </summary>
public class RushGateTests
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
    public void AKeeperPlacesAGateAndItIsAttributedToTheirSide()
    {
        var (game, parser) = NewGame();
        var keeper = Keeper(game, "SIM RED");

        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} opens the way. [RUSH to C]", DateTime.Now);

        var gate = game.RushGateAt(Waymark.C);

        Assert.NotNull(gate);
        Assert.Equal(keeper.Name, gate!.PlacedBy);
        Assert.Equal("SIM RED", gate.Team);
        Assert.Equal(Waymark.C, gate.Position);
    }

    [Fact]
    public void BothKeepersCanHaveAGateStandingAtOnce()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var red = Keeper(game, "SIM RED");
        var gold = Keeper(game, "SIM GOLD");

        parser.ProcessMessage(red.Name, $"|| {red.Name} opens the way. [RUSH to C]", now);
        parser.ProcessMessage(gold.Name, $"|| {gold.Name} opens the way. [RUSH to A]", now);

        Assert.Equal(2, game.RushGates.Count);
        Assert.Equal("SIM RED", game.RushGateAt(Waymark.C)!.Team);
        Assert.Equal("SIM GOLD", game.RushGateAt(Waymark.A)!.Team);
    }

    [Fact]
    public void PlacingASecondGateMovesThatKeepersOwnAndLeavesTheOther()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var red = Keeper(game, "SIM RED");
        var gold = Keeper(game, "SIM GOLD");

        parser.ProcessMessage(gold.Name, $"|| {gold.Name} opens the way. [RUSH to A]", now);
        parser.ProcessMessage(red.Name, $"|| {red.Name} opens the way. [RUSH to C]", now);
        parser.ProcessMessage(red.Name, $"|| {red.Name} shifts it. [RUSH to B]", now);

        // Red's gate moved; Gold's is untouched.
        Assert.Equal(2, game.RushGates.Count);
        Assert.Null(game.RushGateAt(Waymark.C));
        Assert.Equal("SIM RED", game.RushGateAt(Waymark.B)!.Team);
        Assert.Equal("SIM GOLD", game.RushGateAt(Waymark.A)!.Team);
    }

    /// <summary>
    /// A gate lasts until the start of its placer's next turn (slide 65). That is the
    /// next inner phase, because a gate belongs to a goalkeeper and a keeper's turn
    /// comes round there.
    ///
    /// The bug this guards against: gates were only ever cleared by a full reset, so
    /// one placed in an early round was still standing at the end of the match.
    /// </summary>
    [Fact]
    public void AGateLastsUntilThePlacersNextTurn()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var keeper = Keeper(game, "SIM RED");

        parser.ProcessMessage("Referee", "<< INNER PHASE (4/C/D) >> Start!", now);
        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} opens the way. [RUSH to C]", now);
        Assert.Single(game.RushGates);

        // It survives everything up to their next turn, including a new round.
        parser.ProcessMessage("Referee", "<< REPOSITION >>", now);
        parser.ProcessMessage("Referee", "<< BALL CARRIER TURN >>", now);
        parser.ProcessMessage("Referee", "<< ROUND 4 >>", now);
        parser.ProcessMessage("Referee", "<< OUTER PHASE (A/B/1/2) >> Start!", now);

        Assert.Single(game.RushGates);

        // Their turn comes round again, and it is spent.
        parser.ProcessMessage("Referee", "<< INNER PHASE (4/C/D) >> Start!", now);

        Assert.Empty(game.RushGates);
        Assert.Null(game.RushGateAt(Waymark.C));
    }

    [Fact]
    public void AGateIsRecordedWithTheSetAndRoundItWasPlacedIn()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage("Referee", "<< ROUND 3 >>", now);

        var keeper = Keeper(game, "SIM GOLD");
        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} opens the way. [RUSH to 2]", now);

        var gate = game.RushGates["SIM GOLD"];
        Assert.Equal(3, gate.Round);
        Assert.Equal(1, gate.Set);
    }

    /// <summary>
    /// A gate does one thing: it lets a teammate who reaches it move on again. That
    /// is the whole effect, and it is what makes D to C or C to Four possible in a
    /// turn, since neither is a single step.
    /// </summary>
    [Fact]
    public void ReachingYourOwnGateGrantsAFollowUpMove()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var keeper = Keeper(game, "SIM RED");
        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} opens the way. [RUSH to A]", now);

        // A red defender starts on A's side of the field and steps onto the gate.
        var mover = game.Players.Values.First(p =>
            p.Team == "SIM RED" && p.Role == PlayerRole.LeftDefender);

        parser.ProcessMessage(mover.Name, $"|| {mover.Name} pushes up. [MOVE to A]", now);

        Assert.Equal(Waymark.A, mover.Position);
        Assert.True(mover.HasGateMove);
        Assert.Contains(game.PlayByPlay, line => line.Contains("may move again"));
    }

    [Fact]
    public void AnOpponentsGateGrantsNothing()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var keeper = Keeper(game, "SIM GOLD");
        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} opens the way. [RUSH to A]", now);

        var mover = game.Players.Values.First(p =>
            p.Team == "SIM RED" && p.Role == PlayerRole.LeftDefender);

        parser.ProcessMessage(mover.Name, $"|| {mover.Name} pushes up. [MOVE to A]", now);

        Assert.Equal(Waymark.A, mover.Position);
        Assert.False(mover.HasGateMove);
    }

    [Fact]
    public void TheFollowUpMoveDoesNotSurviveThePhase()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var keeper = Keeper(game, "SIM RED");
        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} opens the way. [RUSH to A]", now);

        var mover = game.Players.Values.First(p =>
            p.Team == "SIM RED" && p.Role == PlayerRole.LeftDefender);

        parser.ProcessMessage(mover.Name, $"|| {mover.Name} pushes up. [MOVE to A]", now);
        Assert.True(mover.HasGateMove);

        parser.ProcessMessage("Referee", "<< INNER PHASE (4/C/D) >> Start!", now);

        Assert.False(mover.HasGateMove);
    }

    [Fact]
    public void ResettingTheMatchClearsEveryGate()
    {
        var (game, parser) = NewGame();
        var keeper = Keeper(game, "SIM RED");

        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} opens the way. [RUSH to C]", DateTime.Now);
        Assert.Single(game.RushGates);

        game.Reset();

        Assert.Empty(game.RushGates);
    }
}
