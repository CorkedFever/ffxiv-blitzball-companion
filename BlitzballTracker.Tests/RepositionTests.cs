using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A ring acts together, so it moves together. Moves declared during a phase are
/// held until Reposition and then resolve at once.
///
/// Applying them the moment they were announced meant later contests in the same
/// phase measured reach from somewhere the player had not gone yet.
/// </summary>
public class RepositionTests
{
    private const string Ref = "Sim Referee";

    private static (BlitzGame Game, ChatParser Parser) InOuterPhase()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());

        var parser = new ChatParser(game);
        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);

        return (game, parser);
    }

    private static PlayerState WithRole(BlitzGame game, PlayerRole role, string team) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void AMoveDeclaredInAPhaseDoesNotTakeEffectYet()
    {
        var (game, parser) = InOuterPhase();
        var mover = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);

        var before = mover.Position;
        parser.ProcessMessage(mover.Name, $"|| {mover.Name} pushes up. [MOVE to C]", DateTime.Now);

        Assert.Equal(before, mover.Position);
    }

    [Fact]
    public void RepositionResolvesTheMove()
    {
        var (game, parser) = InOuterPhase();
        var mover = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);
        var now = DateTime.Now;

        parser.ProcessMessage(mover.Name, $"|| {mover.Name} pushes up. [MOVE to C]", now);
        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.Equal(Waymark.C, mover.Position);
        Assert.Contains(game.PlayByPlay, line => line.Contains("Reposition:"));

        // Everyone moves at once, so the reposition line is the one place the log can
        // say who ended up where. It used to report only a count.
        Assert.Contains(game.PlayByPlay, line => line.Contains("Reposition:") && line.Contains('→'));
    }

    [Fact]
    public void EveryoneRepositionsTogether()
    {
        var (game, parser) = InOuterPhase();
        var now = DateTime.Now;

        var first = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);
        var second = WithRole(game, PlayerRole.RightForward, game.HomeTeam);

        parser.ProcessMessage(first.Name, $"|| {first.Name} pushes up. [MOVE to C]", now);
        parser.ProcessMessage(second.Name, $"|| {second.Name} pushes up. [MOVE to C]", now);

        Assert.NotEqual(Waymark.C, first.Position);
        Assert.NotEqual(Waymark.C, second.Position);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.Equal(Waymark.C, first.Position);
        Assert.Equal(Waymark.C, second.Position);
    }

    /// <summary>
    /// The reason deferring matters. A forward at A can reach B, 1, D and C, but not
    /// 2. Declaring a move to C must not extend their reach for the rest of the
    /// phase: they are still standing on A until Reposition.
    /// </summary>
    [Fact]
    public void ReachIsMeasuredFromWhereAPlayerStillStands()
    {
        var (game, parser) = InOuterPhase();
        var now = DateTime.Now;

        var forward = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);
        var target = WithRole(game, PlayerRole.Midfield, game.AwayTeam);

        forward.Position = Waymark.A;
        target.Position = Waymark.Two;

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} pushes up. [MOVE to C]", now);
        parser.ProcessMessage(forward.Name, $"|| {forward.Name} lunges. [TACKLE -> {target.Name}]", now);

        // From A, 2 is off their lines. Had the move landed early, C would have
        // reached it and this would pass silently.
        Assert.Contains(game.PlayByPlay, line => line.Contains("lane or their zone"));
    }

    /// <summary>
    /// The ball carrier's turn has no reposition after it, so their move lands when
    /// they make it.
    ///
    /// Forward, because a carrier may only move toward the enemy goal — the home side
    /// attacks Four in Set 1, so B is on and A would be a retreat.
    /// </summary>
    [Fact]
    public void TheBallCarrierMovesImmediatelyInTheirOwnTurn()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());

        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var carrier = WithRole(game, PlayerRole.Midfield, game.HomeTeam);
        parser.ProcessMessage("Sim Scorekeeper", $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);

        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} glides on. [MOVE to B]", now);

        Assert.Equal(Waymark.B, carrier.Position);
    }

    [Fact]
    public void ARefusedMoveIsStillRefusedAtReposition()
    {
        var (game, parser) = InOuterPhase();
        var now = DateTime.Now;

        // Forwards do not drop into the goal they defend.
        var forward = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);
        var before = forward.Position;

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} drops back. [MOVE to D]", now);
        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.Equal(before, forward.Position);
        Assert.Contains(game.PlayByPlay, line => line.Contains("cannot enter"));
    }
}
