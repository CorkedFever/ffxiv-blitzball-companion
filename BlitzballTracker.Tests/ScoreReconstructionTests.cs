using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Reading the score off the restarts, because nobody ever posts it.
///
/// Referees announce every restart but never the score. Slide 15 makes each restart a
/// statement about the score anyway: a goal that levels the game is followed by a
/// contested Blitzoff, and a goal that does not is followed by a Blitzon in which the
/// side that is <em>behind</em> receives the ball without rolling. So "Barracuda ball"
/// says Barracuda are losing — it is not a note about who conceded.
/// </summary>
public class ScoreReconstructionTests
{
    private const string Ref = "Match Referee";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    [Fact]
    public void TheOpeningWhistleIsNotAGoal()
    {
        var (game, parser) = NewGame();

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", DateTime.Now);

        Assert.Equal(0, game.GoalsSeen);
        Assert.False(game.ScoreIsDerived);
    }

    /// <summary>
    /// A Blitzon names the side that is behind, which says who scored: if the away side
    /// is receiving, the home side just went ahead.
    /// </summary>
    [Fact]
    public void ABlitzonNamesTheTrailingSideAndFixesTheScore()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);
        parser.ProcessMessage(Ref, "[Teams, please reset for Blitzon.  SIM GOLD ball.]", now.AddMinutes(2));
        parser.ProcessMessage(Ref, "<< BLITZON >>", now.AddMinutes(2));

        Assert.Equal(1, game.GoalsSeen);
        Assert.True(game.ScoreIsDerived);
        Assert.True(game.ScoreIsCertain);
        Assert.Equal(1, game.Score.Home);
        Assert.Equal(0, game.Score.Away);
    }

    /// <summary>
    /// A contested Blitzoff mid-match means the goal levelled the scores, which pins
    /// them exactly however muddled things were beforehand.
    /// </summary>
    [Fact]
    public void AContestedRestartMeansTheScoresAreLevel()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);
        parser.ProcessMessage(Ref, "[Teams, please reset for Blitzon.  SIM GOLD ball.]", now.AddMinutes(2));
        parser.ProcessMessage(Ref, "<< BLITZON >>", now.AddMinutes(2));
        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now.AddMinutes(4));

        Assert.Equal(2, game.GoalsSeen);
        Assert.True(game.ScoreIsCertain);
        Assert.Equal(1, game.Score.Home);
        Assert.Equal(1, game.Score.Away);
    }

    /// <summary>
    /// The honest limit. With one side two clear, the next goal leaves the other side
    /// behind whichever way it went, so the referee naming them says nothing new. The
    /// goal still counts; the split stops being certain and says so.
    /// </summary>
    [Fact]
    public void ATwoGoalGapMakesTheNextRestartAmbiguous()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);

        for (var goal = 1; goal <= 3; goal++)
        {
            parser.ProcessMessage(Ref, "[Teams, please reset for Blitzon.  SIM GOLD ball.]", now.AddMinutes(goal));
            parser.ProcessMessage(Ref, "<< BLITZON >>", now.AddMinutes(goal));
        }

        Assert.Equal(3, game.GoalsSeen);
        Assert.False(game.ScoreIsCertain);
    }

    /// <summary>A levelling restart re-pins a derivation that had come loose.</summary>
    [Fact]
    public void LevellingRecoversCertainty()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);

        for (var goal = 1; goal <= 3; goal++)
        {
            parser.ProcessMessage(Ref, "[Teams, please reset for Blitzon.  SIM GOLD ball.]", now.AddMinutes(goal));
            parser.ProcessMessage(Ref, "<< BLITZON >>", now.AddMinutes(goal));
        }

        Assert.False(game.ScoreIsCertain);

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now.AddMinutes(9));

        Assert.True(game.ScoreIsCertain);
        Assert.Equal(2, game.Score.Home);
        Assert.Equal(2, game.Score.Away);
    }

    /// <summary>
    /// A Blitzon nobody attributed still counts the goal, and says plainly that it
    /// cannot say which way it went.
    /// </summary>
    [Fact]
    public void AnUnattributedBlitzonCountsTheGoalButNotTheScorer()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);
        parser.ProcessMessage(Ref, "<< BLITZON >>", now.AddMinutes(2));

        Assert.Equal(1, game.GoalsSeen);
        Assert.False(game.ScoreIsCertain);
        Assert.Contains(game.PlayByPlay, l => l.Contains("not which way"));
    }

    /// <summary>The halftime bonus is ten per point of deficit, so it states the gap.</summary>
    [Fact]
    public void TheHalftimeBonusPinsTheScore()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);

        for (var goal = 1; goal <= 3; goal++)
        {
            parser.ProcessMessage(Ref, "[Teams, please reset for Blitzon.  SIM GOLD ball.]", now.AddMinutes(goal));
            parser.ProcessMessage(Ref, "<< BLITZON >>", now.AddMinutes(goal));
        }

        parser.ProcessMessage(Ref,
            "[My mistake, SIM GOLD had a +10 due to being down one point.]", now.AddMinutes(9));

        Assert.True(game.ScoreIsCertain);
        Assert.Equal(2, game.Score.Home);
        Assert.Equal(1, game.Score.Away);
    }

    /// <summary>A score somebody actually posted outranks anything derived.</summary>
    [Fact]
    public void APostedScoreWinsAndStopsTheDerivation()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);
        parser.ProcessMessage(Ref, "[Teams, please reset for Blitzon.  SIM GOLD ball.]", now.AddMinutes(2));
        parser.ProcessMessage(Ref, "<< BLITZON >>", now.AddMinutes(2));

        Assert.True(game.ScoreIsDerived);

        parser.ProcessMessage(Ref, "[[ SIM RED 3:2 SIM GOLD ]]", now.AddMinutes(3));

        Assert.False(game.ScoreIsDerived);
        Assert.Equal(3, game.Score.Home);
        Assert.Equal(2, game.Score.Away);
    }

    /// <summary>
    /// The variant comes from what the referee called, not from the score — the score is
    /// derived from these calls, so reading it back would be circular.
    /// </summary>
    [Fact]
    public void TheRestartKindComesFromTheCall()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< BLITZOFF >>", now);
        Assert.Equal(BlitzoffKind.Standard, game.BlitzoffVariant);

        parser.ProcessMessage(Ref, "<< BLITZON >>", now.AddMinutes(2));
        Assert.Equal(BlitzoffKind.Blitzon, game.BlitzoffVariant);
    }
}
