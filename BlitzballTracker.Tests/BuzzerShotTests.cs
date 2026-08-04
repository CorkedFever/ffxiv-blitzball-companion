using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// The final shot of a set is not always the last thing that happens (slide 27).
///
/// A carrier who loses the ball at the buzzer hands the new holder a shot from wherever
/// they stand; a keeper who catches one answers with a goal-to-goal shot. Each can be
/// cut out in turn, but the second time the ball goes, the set is over.
/// </summary>
public class BuzzerShotTests
{
    private const string Ref = "Sim Referee";
    private const string Scorer = "Sim Scorekeeper";

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    private static void Roll(ChatParser parser, string player, int value)
        => parser.ProcessMessage(Ref, $"Random! {player} rolls a {value} (out of 100).", DateTime.Now);

    /// <summary>A carrier on B at the buzzer, with a Gold blocker standing on them.</summary>
    private static (BlitzGame Game, ChatParser Parser, PlayerState Carrier, PlayerState Blocker) AtTheBuzzer()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var carrier = Player(game, "SIM RED", PlayerRole.Midfield);
        var blocker = Player(game, "SIM GOLD", PlayerRole.LeftDefender);

        carrier.Position = Waymark.B;
        blocker.Position = Waymark.B;

        parser.ProcessMessage(Ref, "<< ROUND 10 >>", now);
        parser.ProcessMessage(Scorer, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(Ref, "<< BUZZER PHASE >>", now);
        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} gets in the way. [BLOCK -> {carrier.Name}]", now);

        return (game, parser, carrier, blocker);
    }

    [Fact]
    public void TheBuzzerIsTheEndOfASet()
    {
        var (game, _, _, _) = AtTheBuzzer();

        Assert.True(game.IsFinalExchange);
    }

    /// <summary>Round 10's inner carrier turn counts too, not only the buzzer phase.</summary>
    [Fact]
    public void SoIsTheFinalInnerCarrierTurn()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< ROUND 10 >>", now);
        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);

        Assert.True(game.IsFinalExchange);
    }

    [Fact]
    public void AShotIsNotAFinalExchangeInOpenPlay()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< ROUND 3 >>", now);
        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);

        Assert.False(game.IsFinalExchange);
    }

    /// <summary>
    /// A blocked carrier whose shot is cut out hands the shot to whoever took it.
    /// </summary>
    [Fact]
    public void ABlockedShotAtTheBuzzerBecomesTheirShot()
    {
        var (game, parser, carrier, blocker) = AtTheBuzzer();

        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} winds up. [SHOOT]", DateTime.Now);

        Roll(parser, carrier.Name, 20);
        Roll(parser, blocker.Name, 80);

        Assert.Equal(blocker.Name, game.BallCarrier);
        Assert.NotNull(game.BuzzerShot);
        Assert.Equal(blocker.Name, game.BuzzerShot!.Shooter);
        Assert.Equal(carrier.Name, game.BuzzerShot.Interceptor);
        Assert.Contains(game.PlayByPlay, l => l.Contains("BUZZER SHOT"));
    }

    /// <summary>A keeper who catches at the buzzer answers with a goal-to-goal shot.</summary>
    [Fact]
    public void AKeeperCatchingAtTheBuzzerShootsBack()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var shooter = Player(game, "SIM RED", PlayerRole.Midfield);
        var keeper = Player(game, "SIM GOLD", PlayerRole.Goalkeeper);

        shooter.Position = Waymark.B;

        parser.ProcessMessage(Ref, "<< ROUND 10 >>", now);
        parser.ProcessMessage(Scorer, $"[BALL to {shooter.Name}]", now);
        parser.ProcessMessage(Ref, "<< BUZZER PHASE >>", now);
        parser.ProcessMessage(shooter.Name, $"|| {shooter.Name} winds up. [SHOOT]", now);

        Roll(parser, shooter.Name, 10);
        Roll(parser, keeper.Name, 90);

        Assert.Equal(keeper.Name, game.BallCarrier);
        Assert.NotNull(game.BuzzerShot);
        Assert.True(game.BuzzerShot!.IsKeeperReply);
        Assert.Contains(game.PlayByPlay, l => l.Contains("goal-to-goal shot"));
    }

    /// <summary>
    /// The chain is capped: the second time the ball changes hands the set is over,
    /// however dramatic it is getting.
    /// </summary>
    [Fact]
    public void TheChainStopsAfterTheSecondTurnover()
    {
        var (game, parser, carrier, blocker) = AtTheBuzzer();
        var now = DateTime.Now;

        // First turnover: the block takes the shot away.
        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} winds up. [SHOOT]", now);
        Roll(parser, carrier.Name, 20);
        Roll(parser, blocker.Name, 80);

        Assert.Equal(1, game.BuzzerShot!.Link);
        Assert.False(game.BuzzerShot.IsLast);

        // Second: the original carrier blocks the buzzer shot and takes it back.
        parser.ProcessMessage(Ref, "<< BUZZER PHASE >>", now);
        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} lunges. [BLOCK -> {blocker.Name}]", now);
        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} fires. [SHOOT]", now);

        Roll(parser, blocker.Name, 15);
        Roll(parser, carrier.Name, 85);

        Assert.Equal(2, game.BuzzerShot!.Link);
        Assert.True(game.BuzzerShot.IsLast);
    }

    /// <summary>Outside the final exchange a lost ball is just a lost ball.</summary>
    [Fact]
    public void NoBuzzerShotInOpenPlay()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var carrier = Player(game, "SIM RED", PlayerRole.Midfield);
        var blocker = Player(game, "SIM GOLD", PlayerRole.LeftDefender);

        carrier.Position = Waymark.B;
        blocker.Position = Waymark.B;

        parser.ProcessMessage(Ref, "<< ROUND 4 >>", now);
        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(Scorer, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} gets in the way. [BLOCK -> {carrier.Name}]", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);
        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} winds up. [SHOOT]", now);

        Roll(parser, carrier.Name, 20);
        Roll(parser, blocker.Name, 80);

        Assert.Null(game.BuzzerShot);
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("BUZZER SHOT"));
    }

    // --- The goalkeeper tier (slide 33) ---

    /// <summary>
    /// A block is closer to the ball than the keeper, so a shot it stops never reaches
    /// the net at all — no save, no goal.
    /// </summary>
    [Fact]
    public void ABlockStopsAShotBeforeTheKeeperSeesIt()
    {
        var (game, parser, carrier, blocker) = AtTheBuzzer();
        var keeper = Player(game, "SIM GOLD", PlayerRole.Goalkeeper);

        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} winds up. [SHOOT]", DateTime.Now);

        Roll(parser, carrier.Name, 20);
        Roll(parser, blocker.Name, 80);
        Roll(parser, keeper.Name, 99);

        Assert.Contains(game.PlayByPlay, l => l.Contains("never reaches the net"));
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("[SAVE]"));
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("[GOAL]"));
        Assert.Equal(0, keeper.Saves);
    }

    /// <summary>
    /// The generator must not be flagged by its own parser.
    ///
    /// It was: a keeper who caught at the buzzer owes a goal-to-goal shot, and the
    /// generator sent the ordinary clearing pass instead — which the parser then
    /// correctly refused, and the pass went on to open a fumble nobody rolled for.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    [InlineData(31337)]
    public void GeneratedMatchesDeclareNothingInvalid(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        var refused = game.PlayByPlay
            .Where(l => l.Contains("is not a valid action") || l.Contains("not open to the carrier"))
            .ToList();

        Assert.True(refused.Count == 0,
            $"Generated match declared actions its own parser refuses:{Environment.NewLine}" +
            string.Join(Environment.NewLine, refused.Take(5)));
    }

    /// <summary>A shot nobody cut out on the way still meets the keeper as before.</summary>
    [Fact]
    public void AnUncontestedShotStillReachesTheKeeper()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var shooter = Player(game, "SIM RED", PlayerRole.Midfield);
        var keeper = Player(game, "SIM GOLD", PlayerRole.Goalkeeper);

        shooter.Position = Waymark.B;

        parser.ProcessMessage(Ref, "<< ROUND 4 >>", now);
        parser.ProcessMessage(Scorer, $"[BALL to {shooter.Name}]", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);
        parser.ProcessMessage(shooter.Name, $"|| {shooter.Name} winds up. [SHOOT]", now);

        Roll(parser, shooter.Name, 95);
        Roll(parser, keeper.Name, 5);

        Assert.Contains(game.PlayByPlay, l => l.Contains("[GOAL]"));
    }
}
