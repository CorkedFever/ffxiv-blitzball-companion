using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A drawn match goes to a shootout, and a drawn shootout to the captains' duel.
///
/// Both were stubs: the shootout fired five shots against a bare threshold with no
/// keeper on the other end and no ordering, and sudden death was an enum value with
/// nothing behind it.
/// </summary>
public class OvertimeTests
{
    private const string Ref = "Sim Referee";
    private const string Scorer = "Sim Scorekeeper";

    private static (BlitzGame Game, ChatParser Parser) InShootout()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());

        var parser = new ChatParser(game);
        parser.ProcessMessage(Ref, "<< SHOOTOUT >>", DateTime.Now);
        parser.ProcessMessage(Ref, "[[ FIRST -- SIM GOLD ]]", DateTime.Now);

        return (game, parser);
    }

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    private static void Attempt(BlitzGame game, ChatParser parser, string team, PlayerRole role, int shot, int save)
    {
        var shooter = Player(game, team, role);
        var keeper = game.Players.Values.First(p =>
            p.Role == PlayerRole.Goalkeeper && !p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

        var now = DateTime.Now;

        parser.ProcessMessage(shooter.Name, $"|| {shooter.Name} steps up to Centre. [SHOOT]", now);
        parser.ProcessMessage(Ref, $"Random! {shooter.Name} rolls a {shot} (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {keeper.Name} rolls a {save} (out of 100).", now);
    }

    [Fact]
    public void TheCaptainsRollOffDecidesWhoShootsFirst()
    {
        var (game, _) = InShootout();

        Assert.Equal("SIM GOLD", game.ShootoutFirstTeam);
        Assert.Equal(("SIM GOLD", PlayerRole.Midfield), game.NextShooter());
    }

    /// <summary>
    /// No modifiers apply, so a shot that would carry in open play on a forward's +10
    /// is just a losing number here.
    /// </summary>
    [Fact]
    public void ShootoutAttemptsAreFlat()
    {
        var (game, parser) = InShootout();

        var forward = Player(game, "SIM GOLD", PlayerRole.LeftForward);
        forward.GuardBonus = 0;

        // 45 against 50: with a forward's +10 this would be a goal. Flat, it is a save.
        Attempt(game, parser, "SIM GOLD", PlayerRole.Midfield, 45, 50);

        Assert.Equal(0, game.ShootoutScore.Home + game.ShootoutScore.Away);
        Assert.Single(game.ShootoutAttempts);
        Assert.False(game.ShootoutAttempts[0].Scored);
    }

    [Fact]
    public void TheKeepersGuardBonusDoesNotApply()
    {
        var (game, parser) = InShootout();

        var keeper = Player(game, "SIM RED", PlayerRole.Goalkeeper);
        keeper.GuardBonus = 50;

        // 60 vs 20 is a goal on the bare numbers; with +50 the keeper would smother it.
        Attempt(game, parser, "SIM GOLD", PlayerRole.Midfield, 60, 20);

        Assert.True(game.ShootoutAttempts[0].Scored);
    }

    /// <summary>Midfielder first, then out along the line (slide 28).</summary>
    [Fact]
    public void SteppingUpOutOfTurnIsFlagged()
    {
        var (game, parser) = InShootout();

        Attempt(game, parser, "SIM GOLD", PlayerRole.RightDefender, 70, 20);

        Assert.Contains(game.PlayByPlay, l => l.Contains("out of turn"));
    }

    [Fact]
    public void SidesAlternateDownTheirOwnLines()
    {
        var (game, parser) = InShootout();

        Attempt(game, parser, "SIM GOLD", PlayerRole.Midfield, 70, 20);
        Assert.Equal(("SIM RED", PlayerRole.Midfield), game.NextShooter());

        Attempt(game, parser, "SIM RED", PlayerRole.Midfield, 70, 20);
        Assert.Equal(("SIM GOLD", PlayerRole.LeftForward), game.NextShooter());

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("out of turn"));
    }

    /// <summary>
    /// The winner takes one point, which is what breaks the tie. The shootout tally is
    /// not added to the match score.
    /// </summary>
    [Fact]
    public void TheWinnerTakesASinglePoint()
    {
        var (game, parser) = InShootout();
        game.Score = new Score(2, 2);

        foreach (var role in BlitzGame.ShootoutOrder)
        {
            Attempt(game, parser, "SIM GOLD", role, 90, 10);   // scores
            Attempt(game, parser, "SIM RED", role, 10, 90);    // saved
        }

        // SIM GOLD are the away side in the standard roster.
        Assert.True(game.ShootoutComplete);
        Assert.Equal(5, game.ShootoutScore.Away);
        Assert.Equal(0, game.ShootoutScore.Home);

        // One point, not five.
        Assert.Equal(3, game.Score.Away);
        Assert.Equal(2, game.Score.Home);
        Assert.Contains(game.PlayByPlay, l => l.Contains("win the shootout"));
    }

    [Fact]
    public void ADrawnShootoutCallsForSuddenDeath()
    {
        var (game, parser) = InShootout();

        foreach (var role in BlitzGame.ShootoutOrder)
        {
            Attempt(game, parser, "SIM GOLD", role, 90, 10);
            Attempt(game, parser, "SIM RED", role, 90, 10);
        }

        Assert.Null(game.ShootoutWinner());
        Assert.Contains(game.PlayByPlay, l => l.Contains("SUDDEN DEATH"));
    }

    // --- Sudden death (slide 29) ---

    [Fact]
    public void SuddenDeathEmptiesTheSphere()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);

        parser.ProcessMessage(Ref, "<< SUDDEN DEATH >>", DateTime.Now);

        Assert.Equal(GamePhase.SuddenDeath, game.Phase);
        Assert.NotNull(game.SuddenDeath);
        Assert.All(game.Players.Values, p => Assert.Equal(Waymark.None, p.Position));
    }

    [Fact]
    public void AnUnblockedShotWinsItOutright()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        game.Score = new Score(3, 3);

        var captain = Player(game, "SIM RED", PlayerRole.Midfield);

        parser.ProcessMessage(Ref, "<< SUDDEN DEATH >>", now);
        parser.ProcessMessage(Scorer, $"[BALL to {captain.Name}]", now);
        parser.ProcessMessage(captain.Name, $"|| {captain.Name} takes it on. [SHOOT]", now);

        Assert.True(game.IsFinished);
        Assert.Equal(GamePhase.PostGame, game.Phase);
        Assert.Equal(4, game.Score.Home);   // SIM RED are home
        Assert.Contains(game.PlayByPlay, l => l.Contains("win it in sudden death"));
    }

    /// <summary>A blocked captain has to fight the shot away rather than winning on the spot.</summary>
    [Fact]
    public void ABlockedShotDoesNotWinOnItsOwn()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var holder = Player(game, "SIM RED", PlayerRole.Midfield);
        var challenger = Player(game, "SIM GOLD", PlayerRole.Midfield);

        parser.ProcessMessage(Ref, "<< SUDDEN DEATH >>", now);
        parser.ProcessMessage(Scorer, $"[BALL to {holder.Name}]", now);
        parser.ProcessMessage(challenger.Name, $"|| {challenger.Name} lunges. [BLOCK -> {holder.Name}]", now);

        Assert.True(game.SuddenDeath!.HolderBlocked);

        parser.ProcessMessage(holder.Name, $"|| {holder.Name} forces it. [SHOOT]", now);

        Assert.False(game.IsFinished, "A blocked shot still has to beat the block.");
    }

    // --- End to end ---

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    [InlineData(31337)]
    public void GeneratedOvertimeLeavesNobodyLevel(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        // Whatever route it took, a finished match has a winner: regulation, shootout,
        // or the duel. A draw means overtime failed to settle it.
        Assert.True(game.IsFinished);
        Assert.NotEqual(game.Score.Home, game.Score.Away);
    }
}
