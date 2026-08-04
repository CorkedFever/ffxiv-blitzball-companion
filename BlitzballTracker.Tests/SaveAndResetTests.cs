using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// What happens to the ball when a shot is saved or scored.
/// </summary>
public class SaveAndResetTests
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

    /// <summary>
    /// Catching the ball means having it. Without this the shooter stayed the carrier
    /// after being saved and kept taking carrier turns.
    /// </summary>
    [Fact]
    public void ASavedShotGivesTheKeeperTheBall()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var shooter = Named(game, "SIM RED", PlayerRole.Midfield);
        var keeper = Named(game, "SIM GOLD", PlayerRole.Goalkeeper);

        parser.ProcessMessage(Keeper, $"[BALL to {shooter.Name}]", now);
        parser.ProcessMessage(shooter.Name, $"|| {shooter.Name} winds up. [SHOOT]", now);

        Roll(parser, shooter.Name, 25, now);
        Roll(parser, keeper.Name, 98, now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("[SAVE]"));
        Assert.Equal(keeper.Name, game.BallCarrier);
        Assert.True(keeper.HasBall);
        Assert.False(shooter.HasBall);
    }

    [Fact]
    public void AScoredGoalResetsEveryoneToTheirStartingMark()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var shooter = Named(game, "SIM RED", PlayerRole.LeftForward);
        var keeper = Named(game, "SIM GOLD", PlayerRole.Goalkeeper);

        // Drag someone off their mark so the reset is observable.
        var strayer = Named(game, "SIM RED", PlayerRole.Midfield);
        strayer.Position = Waymark.B;

        parser.ProcessMessage(Keeper, $"[BALL to {shooter.Name}]", now);
        parser.ProcessMessage(shooter.Name, $"|| {shooter.Name} winds up. [SHOOT]", now);

        Roll(parser, shooter.Name, 99, now);
        Roll(parser, keeper.Name, 2, now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("[GOAL]"));
        Assert.Equal(BlitzGame.StartingPosition(strayer.Role, game.OwnGoal(strayer)), strayer.Position);
    }

    /// <summary>
    /// The compulsory shot binds the carrier only when the inner turn is actually
    /// theirs. One still out in the outer ring takes the outer carrier turn instead,
    /// so demanding a shot there contradicts the ring they are standing in.
    /// </summary>
    [Fact]
    public void TheCompulsoryShotDoesNotBindACarrierOutInTheOuterRing()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var carrier = Named(game, "SIM RED", PlayerRole.LeftForward);
        carrier.Position = Waymark.One;

        parser.ProcessMessage(Ref, "<< ROUND 10 >>", now);
        parser.ProcessMessage(Keeper, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);

        Assert.False(game.BallCarrierMustShoot);
    }

    [Fact]
    public void TheCompulsoryShotBindsACarrierInTheInnerRing()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var carrier = Named(game, "SIM RED", PlayerRole.Midfield);
        carrier.Position = Waymark.C;

        parser.ProcessMessage(Ref, "<< ROUND 10 >>", now);
        parser.ProcessMessage(Keeper, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);

        Assert.True(game.BallCarrierMustShoot);
    }
}
