using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// On the last round of a set the Inner Ball Carrier phase leaves the carrier no
/// choice: they must shoot. The Buzzer phase ends the same way.
/// </summary>
public class MustShootTests
{
    private const string Ref = "Sim Referee";
    private const string Keeper = "Sim Scorekeeper";

    private static (BlitzGame Game, ChatParser Parser, PlayerState Carrier) AtCarrierTurn(
        int round, string phaseMessage)
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());

        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var carrier = game.Players.Values.First(p =>
            p.Role == PlayerRole.Midfield && p.Team == "SIM RED");

        parser.ProcessMessage(Ref, $"<< ROUND {round} >>", now);
        parser.ProcessMessage(Keeper, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now);
        parser.ProcessMessage(Ref, phaseMessage, now);

        return (game, parser, carrier);
    }

    [Fact]
    public void TheCarrierMustShootOnTheLastRoundsInnerTurn()
    {
        var (game, _, _) = AtCarrierTurn(10, "<< BALL CARRIER TURN >>");
        Assert.True(game.BallCarrierMustShoot);
    }

    [Fact]
    public void EarlierRoundsLeaveTheCarrierFree()
    {
        var (game, _, _) = AtCarrierTurn(7, "<< BALL CARRIER TURN >>");
        Assert.False(game.BallCarrierMustShoot);
    }

    [Fact]
    public void TheBuzzerAlsoDemandsAShot()
    {
        var game = new BlitzGame { Phase = GamePhase.BuzzerPhase };
        Assert.True(game.BallCarrierMustShoot);
    }

    [Fact]
    public void ADeclaredPassOnTheLastRoundIsRefused()
    {
        var (game, parser, carrier) = AtCarrierTurn(10, "<< BALL CARRIER TURN >>");

        var mate = game.Players.Values.First(p =>
            p.Team == "SIM RED" && p.Role == PlayerRole.LeftForward);

        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} looks up. [PASS -> {mate.Name}]", DateTime.Now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("must SHOOT here"));
    }

    /// <summary>A refused action must not be credited, or the stats reward it.</summary>
    [Fact]
    public void ARefusedActionIsNotCounted()
    {
        var (game, parser, carrier) = AtCarrierTurn(10, "<< BALL CARRIER TURN >>");

        var succeededBefore = carrier.ActionsSucceeded;
        var moveFrom = carrier.Position;

        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} swims on. [MOVE to A]", DateTime.Now);

        Assert.Equal(succeededBefore, carrier.ActionsSucceeded);
        Assert.Equal(moveFrom, carrier.Position);
    }

    [Fact]
    public void ShootingIsStillAccepted()
    {
        var (game, parser, carrier) = AtCarrierTurn(10, "<< BALL CARRIER TURN >>");

        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} winds up. [SHOOT]", DateTime.Now);

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("must SHOOT here"));
    }

    /// <summary>Only the carrier is bound by this; everyone else acts as normal.</summary>
    [Fact]
    public void OtherPlayersAreUnaffected()
    {
        var (game, parser, carrier) = AtCarrierTurn(10, "<< BALL CARRIER TURN >>");

        var other = game.Players.Values.First(p =>
            p.Team == "SIM GOLD" && p.Role == PlayerRole.LeftDefender);

        parser.ProcessMessage(other.Name, $"|| {other.Name} watches the lane. [SURVEY]", DateTime.Now);

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("must SHOOT here"));
        Assert.True(other.IsSurveying);
    }
}
