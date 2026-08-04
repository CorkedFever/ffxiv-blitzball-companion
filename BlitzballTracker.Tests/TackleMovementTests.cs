using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A tackle is a movement that stuns, and it resolves like one.
///
/// Slide 59: "If you Tackle a target that is moving (or tackling), they still get to
/// move. You both end in the Waymark that you declared in your Action." So the
/// tackler goes where they aimed, not chasing wherever the target ended up.
/// </summary>
public class TackleMovementTests
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

    private static PlayerState Find(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p => p.Team == team && p.Role == role);

    [Fact]
    public void TheTacklerDoesNotMoveUntilReposition()
    {
        var (game, parser) = InOuterPhase();
        var now = DateTime.Now;

        var forward = Find(game, "SIM RED", PlayerRole.LeftForward);
        var target = Find(game, "SIM GOLD", PlayerRole.Midfield);

        // Four, not D: a red forward may not enter the goal they defend.
        game.TryPlace(forward, Waymark.C);
        game.TryPlace(target, Waymark.Four);

        var startedAt = forward.Position;

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} crashes in. [TACKLE -> {target.Name}]", now);
        parser.ProcessMessage(Ref, $"Random! {forward.Name} rolls a 90 (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {target.Name} rolls a 10 (out of 100).", now);

        // Rolls are in and the tackle has landed, but nobody has moved yet.
        Assert.True(target.IsDazed);
        Assert.Equal(startedAt, forward.Position);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.Equal(Waymark.Four, forward.Position);
    }

    /// <summary>
    /// The rule that was wrong: the tackler used to be moved to wherever the target
    /// was standing at the moment the rolls resolved.
    /// </summary>
    [Fact]
    public void TheTacklerEndsWhereTheyAimedEvenIfTheTargetMovesAway()
    {
        var (game, parser) = InOuterPhase();
        var now = DateTime.Now;

        var forward = Find(game, "SIM RED", PlayerRole.LeftForward);
        var target = Find(game, "SIM GOLD", PlayerRole.Midfield);

        game.TryPlace(forward, Waymark.C);
        game.TryPlace(target, Waymark.B);

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} crashes in. [TACKLE -> {target.Name}]", now);

        // The target was already leaving for somewhere else this phase. Waymarks are
        // called by their marker label, so "4" rather than "Four".
        parser.ProcessMessage(target.Name, $"|| {target.Name} slips away. [MOVE to 4]", now);

        parser.ProcessMessage(Ref, $"Random! {forward.Name} rolls a 90 (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {target.Name} rolls a 10 (out of 100).", now);
        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        // Both end where they each declared: the tackler at B, the target at Four.
        Assert.Equal(Waymark.B, forward.Position);
        Assert.Equal(Waymark.Four, target.Position);
    }

    [Fact]
    public void ATackledPlayerStillGetsTheirOwnMove()
    {
        var (game, parser) = InOuterPhase();
        var now = DateTime.Now;

        var forward = Find(game, "SIM RED", PlayerRole.LeftForward);
        var target = Find(game, "SIM GOLD", PlayerRole.Midfield);

        game.TryPlace(forward, Waymark.C);
        game.TryPlace(target, Waymark.C);
        game.TryPlace(forward, Waymark.One);

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} crashes in. [TACKLE -> {target.Name}]", now);
        parser.ProcessMessage(target.Name, $"|| {target.Name} pushes on. [MOVE to B]", now);

        parser.ProcessMessage(Ref, $"Random! {forward.Name} rolls a 95 (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {target.Name} rolls a 5 (out of 100).", now);
        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        // Dazed, and still went where they were going.
        Assert.True(target.IsDazed);
        Assert.Equal(Waymark.B, target.Position);
    }

    [Fact]
    public void AFailedTackleMovesNobody()
    {
        var (game, parser) = InOuterPhase();
        var now = DateTime.Now;

        var forward = Find(game, "SIM RED", PlayerRole.LeftForward);
        var target = Find(game, "SIM GOLD", PlayerRole.Midfield);

        game.TryPlace(forward, Waymark.C);
        game.TryPlace(target, Waymark.D);

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} crashes in. [TACKLE -> {target.Name}]", now);
        parser.ProcessMessage(Ref, $"Random! {forward.Name} rolls a 10 (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {target.Name} rolls a 90 (out of 100).", now);
        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.False(target.IsDazed);
        Assert.Equal(Waymark.C, forward.Position);
    }
}
