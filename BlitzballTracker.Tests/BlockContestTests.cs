using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A block does not resolve when it is declared. It stands against the blocked
/// player and fires later, if they try to pass or move: every blocker rolls against
/// them at once and they must out-roll the best of them.
///
/// Anyone may block. Winning blockers intercept a pass and hold a move in place.
/// </summary>
public class BlockContestTests
{
    private const string Ref = "Sim Referee";
    private const string Keeper = "Sim Scorekeeper";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    private static void Roll(ChatParser parser, string player, int value, DateTime at)
        => parser.ProcessMessage(Ref, $"Random! {player} rolls a {value} (out of 100).", at);

    private static PlayerState Named(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ABlockStandsRatherThanResolvingWhenDeclared()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var blocker = Named(game, "SIM GOLD", PlayerRole.Midfield);
        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);

        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} gets in the way. [BLOCK -> {carrier.Name}]", now);

        Assert.Contains(blocker.Name, game.BlockersOf(carrier.Name));
        Assert.True(carrier.IsBlocked);
    }

    /// <summary>
    /// Block battles: blocking a blocker negates what they were doing, which is how a
    /// side frees a held ball carrier. The counter-blocked player can no longer contest
    /// the carrier at all.
    /// </summary>
    [Fact]
    public void BlockingABlockerFreesWhoTheyWereHolding()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);
        var blocker = Named(game, "SIM GOLD", PlayerRole.LeftDefender);
        var rescuer = Named(game, "SIM RED", PlayerRole.LeftForward);

        blocker.Position = carrier.Position;
        rescuer.Position = blocker.Position;

        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} gets in the way. [BLOCK -> {carrier.Name}]", now);
        Assert.True(carrier.IsBlocked);
        Assert.Contains(blocker.Name, game.BlockersOf(carrier.Name));

        parser.ProcessMessage(rescuer.Name, $"|| {rescuer.Name} shoulders in. [BLOCK -> {blocker.Name}]", now);

        Assert.DoesNotContain(blocker.Name, game.BlockersOf(carrier.Name));
        Assert.False(carrier.IsBlocked, "Nobody is holding them any more.");
        Assert.True(blocker.IsBlocked, "The blocker is now the one being held.");
        Assert.Contains(game.PlayByPlay, l => l.Contains("blocks the blocker"));
    }

    /// <summary>Getting in somebody's way costs you your own freedom too (slide 44).</summary>
    [Fact]
    public void BlockingLeavesBothPlayersBlocked()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var target = Named(game, "SIM RED", PlayerRole.Midfield);
        var blocker = Named(game, "SIM GOLD", PlayerRole.LeftDefender);

        blocker.Position = target.Position;

        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} gets in the way. [BLOCK -> {target.Name}]", now);

        Assert.True(target.IsBlocked);
        Assert.True(blocker.IsBlocked);
    }

    /// <summary>Every outfield role can block; only goalkeepers have no BLOCK action.</summary>
    [Fact]
    public void EveryOutfieldRoleMayBlock()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);

        foreach (var role in new[] { PlayerRole.LeftDefender, PlayerRole.RightForward, PlayerRole.Midfield })
        {
            var blocker = Named(game, "SIM GOLD", role);
            parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} moves in. [BLOCK -> {carrier.Name}]", now);

            Assert.Contains(blocker.Name, game.BlockersOf(carrier.Name));
        }
    }

    [Fact]
    public void GoalkeepersHaveNoBlockAction()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var keeper = Named(game, "SIM GOLD", PlayerRole.Goalkeeper);
        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);

        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} moves in. [BLOCK -> {carrier.Name}]", now);

        Assert.DoesNotContain(keeper.Name, game.BlockersOf(carrier.Name));
        Assert.Contains(game.PlayByPlay, l => l.Contains("no BLOCK action"));
    }

    [Fact]
    public void GoalkeepersAreImmuneToBeingBlocked()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var blocker = Named(game, "SIM RED", PlayerRole.Midfield);
        var keeper = Named(game, "SIM GOLD", PlayerRole.Goalkeeper);

        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} moves in. [BLOCK -> {keeper.Name}]", now);

        Assert.Empty(game.BlockersOf(keeper.Name));
        Assert.False(keeper.IsBlocked);
    }

    /// <summary>At most three blockers hold one player; a fourth converts to Survey.</summary>
    [Fact]
    public void AFourthBlockerIsRefused()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);

        var blockers = game.Players.Values
            .Where(p => p.Team == "SIM GOLD" && p.Role != PlayerRole.Goalkeeper)
            .Take(4)
            .ToList();

        foreach (var blocker in blockers)
            parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} moves in. [BLOCK -> {carrier.Name}]", now);

        Assert.Equal(BlitzGame.MaxBlockersPerPlayer, game.BlockersOf(carrier.Name).Count);
        Assert.Contains(game.PlayByPlay, l => l.Contains("converts to SURVEY"));
    }

    [Fact]
    public void ABeatenPassIsIntercepted()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var blocker = Named(game, "SIM GOLD", PlayerRole.Midfield);
        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);
        var mate = Named(game, "SIM RED", PlayerRole.LeftForward);

        parser.ProcessMessage(Keeper, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} gets in the way. [BLOCK -> {carrier.Name}]", now);

        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} looks up. [PASS -> {mate.Name}]", now);

        Roll(parser, carrier.Name, 20, now);
        Roll(parser, blocker.Name, 80, now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("[INTERCEPT]") && l.Contains(blocker.Name));
    }

    [Fact]
    public void APassThatOutRollsEveryBlockerCarries()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var blocker = Named(game, "SIM GOLD", PlayerRole.Midfield);
        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);
        var mate = Named(game, "SIM RED", PlayerRole.LeftForward);

        parser.ProcessMessage(Keeper, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} gets in the way. [BLOCK -> {carrier.Name}]", now);
        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} looks up. [PASS -> {mate.Name}]", now);

        Roll(parser, carrier.Name, 90, now);
        Roll(parser, blocker.Name, 30, now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("breaks through the block"));
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("[INTERCEPT]"));
    }

    /// <summary>
    /// Several blocks stack, and the carrier has to beat the best of them, which is
    /// the same as having to beat all of them.
    /// </summary>
    [Fact]
    public void TheCarrierMustBeatTheStrongestBlocker()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var weak = Named(game, "SIM GOLD", PlayerRole.LeftDefender);
        var strong = Named(game, "SIM GOLD", PlayerRole.RightDefender);
        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);
        var mate = Named(game, "SIM RED", PlayerRole.LeftForward);

        parser.ProcessMessage(Keeper, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(weak.Name, $"|| {weak.Name} moves in. [BLOCK -> {carrier.Name}]", now);
        parser.ProcessMessage(strong.Name, $"|| {strong.Name} moves in. [BLOCK -> {carrier.Name}]", now);
        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} looks up. [PASS -> {mate.Name}]", now);

        Roll(parser, carrier.Name, 50, now);
        Roll(parser, weak.Name, 10, now);
        Roll(parser, strong.Name, 70, now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("[INTERCEPT]") && l.Contains(strong.Name));
    }

    [Fact]
    public void TheContestWaitsUntilEveryBlockerHasRolled()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var first = Named(game, "SIM GOLD", PlayerRole.LeftDefender);
        var second = Named(game, "SIM GOLD", PlayerRole.RightDefender);
        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);
        var mate = Named(game, "SIM RED", PlayerRole.LeftForward);

        parser.ProcessMessage(Keeper, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(first.Name, $"|| {first.Name} moves in. [BLOCK -> {carrier.Name}]", now);
        parser.ProcessMessage(second.Name, $"|| {second.Name} moves in. [BLOCK -> {carrier.Name}]", now);
        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} looks up. [PASS -> {mate.Name}]", now);

        Roll(parser, carrier.Name, 50, now);
        Roll(parser, first.Name, 10, now);

        // Deciding now would hand it to whoever rolled first.
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("breaks through"));
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("[INTERCEPT]"));

        Roll(parser, second.Name, 20, now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("breaks through"));
    }

    /// <summary>
    /// Blocks are declared during the ring's phase and fire during the carrier's
    /// turn, so they have to survive the boundary between the two.
    /// </summary>
    [Fact]
    public void BlocksSurviveIntoTheBallCarriersTurn()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var blocker = Named(game, "SIM GOLD", PlayerRole.Midfield);
        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);

        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now);
        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} moves in. [BLOCK -> {carrier.Name}]", now);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);

        Assert.Contains(blocker.Name, game.BlockersOf(carrier.Name));
    }

    [Fact]
    public void ANewActingPhaseSweepsOldBlocks()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var blocker = Named(game, "SIM GOLD", PlayerRole.Midfield);
        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);

        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} moves in. [BLOCK -> {carrier.Name}]", now);
        Assert.NotEmpty(game.BlockersOf(carrier.Name));

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);

        Assert.Empty(game.BlockersOf(carrier.Name));
        Assert.False(carrier.IsBlocked);
    }

    /// <summary>
    /// A blocked player does not move at all, and no roll-off decides it. The
    /// rulebook is explicit for the carrier: blocked, they may only shoot or pass.
    /// </summary>
    [Fact]
    public void ABlockedPlayerCannotMoveAtAll()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now);

        var blocker = Named(game, "SIM GOLD", PlayerRole.Midfield);
        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);
        var origin = carrier.Position;

        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} moves in. [BLOCK -> {carrier.Name}]", now);
        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} pushes on. [MOVE to A]", now);

        // No rolls: being blocked removes the option entirely.
        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.Equal(origin, carrier.Position);
        Assert.Contains(game.PlayByPlay, l => l.Contains("cannot move"));
    }
}
