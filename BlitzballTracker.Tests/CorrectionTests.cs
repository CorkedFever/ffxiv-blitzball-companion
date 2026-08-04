using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Referees correct mistakes constantly during a real match: flags, grace, and
/// re-rolls. The parser has to follow those calls rather than keep its own
/// bookkeeping, because the referees are the authority.
/// </summary>
public class CorrectionTests
{
    private const string Ref = "O'looqa Honji";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(Fixtures.RealMatchRoster());
        return (game, new ChatParser(game));
    }

    private static void Roll(ChatParser parser, string player, int value, DateTime at)
        => parser.ProcessMessage(Ref, $"Random! {player} rolls a {value} (out of 100).", at);

    [Fact]
    public void SecondRollSupersedesTheFirst()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        Roll(parser, "Soren Kell", 20, now);
        Roll(parser, "Soren Kell", 90, now);

        // Referees accept re-rolls at their discretion, so the newer roll stands.
        // The old parser kept the first and silently discarded the correction.
        Assert.Equal(90, game.Players["Soren Kell"].PhaseRoll);
    }

    [Fact]
    public void SecondRollIsSurfacedAsAnAdvisory()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        Roll(parser, "Soren Kell", 20, now);
        Roll(parser, "Soren Kell", 90, now);

        Assert.Contains(game.PlayByPlay, line => line.Contains("rolled again") && line.Contains("20"));
    }

    [Fact]
    public void GraceVoidsThePlayersRoll()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        Roll(parser, "Soren Kell", 77, now);
        Assert.Equal(77, game.Players["Soren Kell"].PhaseRoll);

        parser.ProcessMessage("Ffon Aveross", "[[ GRACE GIVEN -- Soren Kell ]]", now);

        Assert.Null(game.Players["Soren Kell"].PhaseRoll);
    }

    [Theory]
    [InlineData("[[ GRACE GIVEN -- Soren Kell ]]")]
    [InlineData("[[ GRACE GIVEN - Soren Kell ]]")]
    [InlineData("[ Grace Given Soren Kell ]")]
    [InlineData("Soren Kell [Mateus] [ERROR - GRACE]")]
    public void GraceIsRecognisedInEveryFormRefereesUse(string message)
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        Roll(parser, "Soren Kell", 77, now);
        parser.ProcessMessage("Ffon Aveross", message, now);

        Assert.Null(game.Players["Soren Kell"].PhaseRoll);
    }

    /// <summary>
    /// Referees abbreviate names when calling re-rolls. A closed roster makes the
    /// prefix expansion safe.
    /// </summary>
    [Theory]
    [InlineData("REROLL Mhin/Sata")]
    [InlineData("[[REROLL MHINCO vs SATAYA]]")]
    [InlineData("[[Reroll Mhinco and Sataya]]")]
    [InlineData("[[MHIN vs SATA REROLL]]")]
    public void RerollVoidsBothNamedPlayers(string message)
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        Roll(parser, "Mhinco Pokhmhakwaahni", 50, now);
        Roll(parser, "Sataya Saoraigne", 50, now);

        parser.ProcessMessage(Ref, message, now);

        Assert.Null(game.Players["Mhinco Pokhmhakwaahni"].PhaseRoll);
        Assert.Null(game.Players["Sataya Saoraigne"].PhaseRoll);
    }

    [Fact]
    public void RerollDoesNotTouchUninvolvedPlayers()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        Roll(parser, "Mhinco Pokhmhakwaahni", 50, now);
        Roll(parser, "Soren Kell", 60, now);

        parser.ProcessMessage(Ref, "REROLL Mhin/Sata", now);

        Assert.Equal(60, game.Players["Soren Kell"].PhaseRoll);
    }

    [Fact]
    public void FlagIsRecordedWithoutChangingState()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        Roll(parser, "Soren Kell", 42, now);
        parser.ProcessMessage("Ziandria Dorthal", "[[FLAG]]", now);

        // A bare flag names nobody, so nothing is voided. The referee decides next.
        Assert.Equal(42, game.Players["Soren Kell"].PhaseRoll);
        Assert.Contains(game.PlayByPlay, line => line.Contains("FLAG"));
    }

    /// <summary>
    /// A re-roll after an outcome has already been applied must reverse it, or the
    /// tackle would be counted twice and the daze would stick.
    /// </summary>
    [Fact]
    public void RerollReversesAnAlreadyAppliedOutcome()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(
            "Manami Tsukino",
            "|| Manami lunges. [TACKLE -> Soren Kell]",
            now);

        Roll(parser, "Manami Tsukino", 90, now);
        Roll(parser, "Soren Kell", 10, now);

        var manami = game.Players["Manami Tsukino"];
        var soren = game.Players["Soren Kell"];

        Assert.Equal(1, manami.Tackles);
        Assert.True(soren.IsDazed);

        parser.ProcessMessage(Ref, "REROLL Manami/Soren", now);

        Assert.Equal(0, manami.Tackles);
        Assert.False(soren.IsDazed);
        Assert.DoesNotContain("Soren Kell", game.DazeTracker.Keys);
    }
}

/// <summary>
/// Players must post their action before rolling, except when they are reacting to
/// something aimed at them. The old check had no exception for defenders, so it
/// flagged exactly the people following the rules.
/// </summary>
public class RollOrderAdvisoryTests
{
    private const string Ref = "O'looqa Honji";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(Fixtures.RealMatchRoster());
        return (game, new ChatParser(game));
    }

    private static bool WasAdvised(BlitzGame game, string player)
        => game.PlayByPlay.Any(l => l.Contains("rolled before posting") && l.Contains(player));

    [Fact]
    public void RollingBeforePostingWithNoReasonIsAdvised()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "Random! Soren Kell rolls a 55 (out of 100).", now);
        parser.ProcessMessage("Soren Kell", "|| Soren winds up. [SHOOT]", now);

        Assert.True(WasAdvised(game, "Soren Kell"));
    }

    [Fact]
    public void DefendingAgainstATackleIsNotAdvised()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        // Manami declares against Soren, so Soren's roll is a defence, not a breach.
        parser.ProcessMessage("Manami Tsukino", "|| Manami lunges. [TACKLE -> Soren Kell]", now);
        parser.ProcessMessage(Ref, "Random! Soren Kell rolls a 55 (out of 100).", now);
        parser.ProcessMessage("Soren Kell", "|| Soren shoves back. [BLOCK -> Manami Tsukino]", now);

        Assert.False(WasAdvised(game, "Soren Kell"));
    }

    [Fact]
    public void BallCarrierReactingIsNotAdvised()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage("Ziandria Dorthal", "[BALL to Soren Kell]", now);
        parser.ProcessMessage(Ref, "Random! Soren Kell rolls a 55 (out of 100).", now);
        parser.ProcessMessage("Soren Kell", "|| Soren surges forward. [SHOOT]", now);

        Assert.False(WasAdvised(game, "Soren Kell"));
    }

    [Fact]
    public void GoalkeeperContestingAShotIsNotAdvised()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage("Soren Kell", "|| Soren strikes. [SHOOT]", now);
        parser.ProcessMessage(Ref, "Random! J'dextera Sol rolls a 70 (out of 100).", now);
        parser.ProcessMessage("J'dextera Sol", "|| Dextera braces. [GUARD]", now);

        Assert.False(WasAdvised(game, "J'dextera Sol"));
    }
}
