using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// DIVE is the defender's interception. It arms a state, and when a ball is passed into
/// the zone they are covering they roll against the passer for it.
///
/// It used to arm the state, work out who was eligible, print that they could contest —
/// and then never award anyone the ball. Slide 33 also orders the ways a ball can be
/// stopped: a block is closer than a dive and settles it first, even if the diver rolled
/// higher.
/// </summary>
public class DiveTests
{
    private const string Ref = "Sim Referee";
    private const string Scorer = "Sim Scorekeeper";

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A Gold pass from C out to B, with a Red defender diving on the destination.
    /// </summary>
    private static (BlitzGame Game, ChatParser Parser, PlayerState Passer, PlayerState Receiver, PlayerState Diver)
        PassIntoADive()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var passer = Player(game, "SIM GOLD", PlayerRole.Midfield);
        var receiver = Player(game, "SIM GOLD", PlayerRole.LeftForward);
        var diver = Player(game, "SIM RED", PlayerRole.LeftDefender);

        passer.Position = Waymark.C;
        receiver.Position = Waymark.B;
        diver.Position = Waymark.B;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(Scorer, $"[BALL to {passer.Name}]", now);
        parser.ProcessMessage(diver.Name, $"|| {diver.Name} reads the lane. [DIVE]", now);

        Assert.True(diver.IsDiving);

        return (game, parser, passer, receiver, diver);
    }

    private static void Roll(ChatParser parser, string player, int value)
        => parser.ProcessMessage(Ref, $"Random! {player} rolls a {value} (out of 100).", DateTime.Now);

    [Fact]
    public void APassIntoADiveIsContested()
    {
        var (game, parser, passer, receiver, diver) = PassIntoADive();

        parser.ProcessMessage(passer.Name, $"|| {passer.Name} looks up. [PASS -> {receiver.Name}]", DateTime.Now);

        var pass = game.CurrentPhaseActions.Single(a => a.Action == ActionType.Pass);

        Assert.Contains(diver.Name, pass.DivedBy!);
        Assert.Equal(ActionOutcome.Pending, pass.Outcome);
        Assert.True(game.CallsForRoll(pass), "A dived-on pass is a contest and needs rolls.");
    }

    [Fact]
    public void ADiverWhoOutrollsThePasserTakesTheBall()
    {
        var (game, parser, passer, receiver, diver) = PassIntoADive();

        parser.ProcessMessage(passer.Name, $"|| {passer.Name} looks up. [PASS -> {receiver.Name}]", DateTime.Now);

        Roll(parser, passer.Name, 30);
        Roll(parser, diver.Name, 85);

        Assert.Equal(diver.Name, game.BallCarrier);
        Assert.Contains(game.PlayByPlay, l => l.Contains("[INTERCEPT]") && l.Contains(diver.Name));
    }

    [Fact]
    public void ThePassCarriesWhenTheDiverIsOutrolled()
    {
        var (game, parser, passer, receiver, diver) = PassIntoADive();

        parser.ProcessMessage(passer.Name, $"|| {passer.Name} looks up. [PASS -> {receiver.Name}]", DateTime.Now);

        Roll(parser, passer.Name, 90);
        Roll(parser, diver.Name, 12);

        var pass = game.CurrentPhaseActions.Single(a => a.Action == ActionType.Pass);

        Assert.Equal(ActionOutcome.Success, pass.Outcome);
        Assert.Contains(game.PlayByPlay, l => l.Contains("breaks through the dive"));
        Assert.NotEqual(diver.Name, game.BallCarrier);
    }

    /// <summary>
    /// The diver rolls against the passer — the player who put the ball in the air —
    /// not the team-mate it was aimed at.
    /// </summary>
    [Fact]
    public void TheDiverRollsAgainstThePasserNotTheReceiver()
    {
        var (game, parser, passer, receiver, diver) = PassIntoADive();

        parser.ProcessMessage(passer.Name, $"|| {passer.Name} looks up. [PASS -> {receiver.Name}]", DateTime.Now);

        // The receiver rolls high and it changes nothing: they are not in this contest.
        Roll(parser, receiver.Name, 99);
        Roll(parser, passer.Name, 20);
        Roll(parser, diver.Name, 60);

        Assert.Equal(diver.Name, game.BallCarrier);
    }

    /// <summary>
    /// Slide 33: the closest successful interception beats the rest, even when it did
    /// not roll the highest. A block is closer than a dive.
    /// </summary>
    [Fact]
    public void ABlockBeatsADiveEvenOnALowerRoll()
    {
        var (game, parser, passer, receiver, diver) = PassIntoADive();
        var now = DateTime.Now;

        var blocker = Player(game, "SIM RED", PlayerRole.Midfield);
        blocker.Position = passer.Position;

        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} gets in the way. [BLOCK -> {passer.Name}]", now);
        parser.ProcessMessage(passer.Name, $"|| {passer.Name} looks up. [PASS -> {receiver.Name}]", now);

        Roll(parser, passer.Name, 30);
        Roll(parser, diver.Name, 99);
        Roll(parser, blocker.Name, 40);

        // The blocker rolled far lower than the diver and still takes it: they are
        // closer to the ball.
        Assert.Equal(blocker.Name, game.BallCarrier);
        Assert.Contains(game.PlayByPlay, l => l.Contains("[INTERCEPT]") && l.Contains(blocker.Name));
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("dives across"));
    }

    /// <summary>
    /// A dive only covers balls arriving into its zone, so a pass leaving that zone
    /// cannot be dived on (slide 61).
    /// </summary>
    [Fact]
    public void ADiveDoesNotCoverAPassLeavingItsOwnZone()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var passer = Player(game, "SIM GOLD", PlayerRole.LeftForward);
        var receiver = Player(game, "SIM GOLD", PlayerRole.Midfield);
        var diver = Player(game, "SIM RED", PlayerRole.LeftDefender);

        passer.Position = Waymark.B;
        diver.Position = Waymark.B;
        receiver.Position = Waymark.Four;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(Scorer, $"[BALL to {passer.Name}]", now);
        parser.ProcessMessage(diver.Name, $"|| {diver.Name} reads the lane. [DIVE]", now);
        parser.ProcessMessage(passer.Name, $"|| {passer.Name} looks up. [PASS -> {receiver.Name}]", now);

        var pass = game.CurrentPhaseActions.Single(a => a.Action == ActionType.Pass);

        Assert.True(pass.DivedBy is null or { Count: 0 });
    }
}
