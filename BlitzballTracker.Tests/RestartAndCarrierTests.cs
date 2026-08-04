using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// The ball does not go back into play the same way every time (slide 15), and carrying
/// it narrows movement sharply (slide 52). Both were previously treated as the ordinary
/// case: every goal drew a plain roll-off, and the carrier moved under everyone else's
/// rules.
/// </summary>
public class RestartAndCarrierTests
{
    private const string Ref = "Sim Referee";
    private const string Scorer = "Sim Scorekeeper";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    // --- Blitzoff variants (slide 15) ---

    [Fact]
    public void LevelScoresGetAPlainRollOff()
    {
        var (game, parser) = NewGame();
        game.Score = new Score(2, 2);

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", DateTime.Now);

        Assert.Equal(BlitzoffKind.Standard, game.BlitzoffVariant);
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("BLITZON"));
    }

    [Fact]
    public void AGoalThatDoesNotLevelTheScoresIsABlitzon()
    {
        var (game, parser) = NewGame();
        game.Score = new Score(3, 1);

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", DateTime.Now);

        Assert.Equal(BlitzoffKind.Blitzon, game.BlitzoffVariant);
        Assert.Equal("SIM GOLD", game.TrailingTeam);   // away side, and behind
        Assert.Contains(game.PlayByPlay, l => l.Contains("BLITZON") && l.Contains("no roll-off"));
    }

    /// <summary>There is no roll-off to get wrong, so the ball going elsewhere is a miscall.</summary>
    [Fact]
    public void ABlitzonGivingTheBallToTheLeadersIsFlagged()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        game.Score = new Score(3, 1);
        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);

        var leader = Player(game, "SIM RED", PlayerRole.Midfield);
        parser.ProcessMessage(Scorer, $"[BALL to {leader.Name}]", now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("Blitzon gives the ball to"));
    }

    [Fact]
    public void ABlitzonToTheTrailingSideIsNotFlagged()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        game.Score = new Score(3, 1);
        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);

        var trailing = Player(game, "SIM GOLD", PlayerRole.Midfield);
        parser.ProcessMessage(Scorer, $"[BALL to {trailing.Name}]", now);

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("Blitzon gives the ball to"));
    }

    /// <summary>Set 2 opens with the trailing side weighted by ten per point (slide 15).</summary>
    [Fact]
    public void TheHalftimeRestartWeightsTheTrailingSide()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        game.Score = new Score(1, 3);

        parser.ProcessMessage(Ref, "HALFTIME", now);
        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);

        Assert.Equal(BlitzoffKind.HalftimeRestart, game.BlitzoffVariant);
        Assert.Equal(2, game.PointDeficit);

        var chasing = Player(game, "SIM RED", PlayerRole.Midfield);
        var leading = Player(game, "SIM GOLD", PlayerRole.Midfield);

        Assert.Equal(20, game.BlitzoffBonus(chasing));
        Assert.Equal(0, game.BlitzoffBonus(leading));
        Assert.Contains(game.PlayByPlay, l => l.Contains("+20") && l.Contains("deficit"));
    }

    /// <summary>A restart from level scores carries no bonus for anybody.</summary>
    [Fact]
    public void ALevelHalftimeRestartWeightsNobody()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        game.Score = new Score(2, 2);

        parser.ProcessMessage(Ref, "HALFTIME", now);
        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);

        Assert.All(game.Players.Values, p => Assert.Equal(0, game.BlitzoffBonus(p)));
    }

    // --- Ball carrier movement (slide 52) ---

    private static (BlitzGame Game, ChatParser Parser, PlayerState Carrier) WithBall()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var carrier = Player(game, "SIM RED", PlayerRole.Midfield);
        carrier.Position = Waymark.C;

        parser.ProcessMessage(Scorer, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);

        return (game, parser, carrier);
    }

    [Fact]
    public void TheCarrierMayAdvance()
    {
        var (_, parser, carrier) = WithBall();

        // Home attacks Four in Set 1, so B is forward of C.
        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} drives on. [MOVE to B]", DateTime.Now);

        Assert.Equal(Waymark.B, carrier.Position);
    }

    [Fact]
    public void TheCarrierMayNotRetreat()
    {
        var (game, parser, carrier) = WithBall();

        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} drops back. [MOVE to A]", DateTime.Now);

        Assert.Equal(Waymark.C, carrier.Position);
        Assert.Contains(game.PlayByPlay, l => l.Contains("must move toward the enemy goal"));
    }

    /// <summary>
    /// The two lanes of a zone sit at the same rank, so crossing between them advances
    /// nothing — and the carrier may not move within a zone.
    /// </summary>
    [Fact]
    public void TheCarrierMayNotCrossWithinAZone()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var carrier = Player(game, "SIM RED", PlayerRole.LeftForward);
        carrier.Position = Waymark.A;

        parser.ProcessMessage(Scorer, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);
        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} slides across. [MOVE to 1]", now);

        Assert.Equal(Waymark.A, carrier.Position);
        Assert.Contains(game.PlayByPlay, l => l.Contains("cannot carry the ball to"));
    }

    /// <summary>
    /// Where a role restriction leaves the carrier nowhere to go, the ball has to move
    /// instead of the player.
    /// </summary>
    [Fact]
    public void ARoleRestrictionSendsTheCarrierToShootOrPass()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        // A defender cannot enter the goal they are attacking.
        var carrier = Player(game, "SIM RED", PlayerRole.LeftDefender);
        carrier.Position = Waymark.B;

        parser.ProcessMessage(Scorer, $"[BALL to {carrier.Name}]", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);
        parser.ProcessMessage(carrier.Name, $"|| {carrier.Name} pushes on. [MOVE to 4]", now);

        Assert.Equal(Waymark.B, carrier.Position);
        Assert.Contains(game.PlayByPlay,
            l => l.Contains("cannot enter") && l.Contains("SHOOT or PASS"));
    }

    /// <summary>Carrying the ball closes everything but MOVE, PASS and SHOOT.</summary>
    [Theory]
    [InlineData("TACKLE")]
    [InlineData("BLOCK")]
    [InlineData("SURVEY")]
    [InlineData("DIVE")]
    public void TheCarrierLosesEveryOtherAction(string action)
    {
        var (game, parser, carrier) = WithBall();

        var target = Player(game, "SIM GOLD", PlayerRole.Midfield);

        parser.ProcessMessage(carrier.Name,
            $"|| {carrier.Name} tries it. [{action} -> {target.Name}]", DateTime.Now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("is not open to the carrier"));
        Assert.False(target.IsDazed);
        Assert.False(carrier.IsSurveying);
        Assert.False(carrier.IsDiving);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2024)]
    public void GeneratedCarriersNeverMoveIllegally(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("cannot carry the ball to"));
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("is not open to the carrier"));
    }
}
