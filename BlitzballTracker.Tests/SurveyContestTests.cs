using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Declaring SURVEY is not rolled for. It arms a guard over a lane and does nothing at
/// all until Reposition, when somebody tries to come through it — that is when the
/// roll-off happens.
///
/// The tracker used to roll at declaration, which meant the number was spent a phase
/// before the thing it was supposed to decide.
/// </summary>
public class SurveyContestTests
{
    private const string Ref = "Sim Referee";

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    private static void Roll(ChatParser parser, string player, int value)
        => parser.ProcessMessage(Ref, $"Random! {player} rolls a {value} (out of 100).", DateTime.Now);

    /// <summary>
    /// A Gold defender watching A–C from the far end, with a Red player on A declaring
    /// a move to C.
    ///
    /// The surveyor stands at C rather than A on purpose: a survey cannot catch
    /// somebody leaving the waymark it is surveying from (slide 48).
    /// </summary>
    private static (BlitzGame Game, ChatParser Parser, PlayerState Mover, PlayerState Surveyor) IntoTheLane()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var mover = Player(game, "SIM RED", PlayerRole.LeftForward);
        var surveyor = Player(game, "SIM GOLD", PlayerRole.LeftDefender);

        mover.Position = Waymark.A;
        surveyor.Position = Waymark.C;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(surveyor.Name,
            $"|| {surveyor.Name} watches the lane. [SURVEY][A <-> C]", now);
        parser.ProcessMessage(mover.Name, $"|| {mover.Name} swims for position. [MOVE to C]", now);

        return (game, parser, mover, surveyor);
    }

    [Fact]
    public void DeclaringASurveyIsNotRolledFor()
    {
        var (game, _, _, surveyor) = IntoTheLane();

        var survey = game.CurrentPhaseActions.Single(a => a.Action == ActionType.Survey);

        Assert.True(surveyor.IsSurveying);
        Assert.False(game.CallsForRoll(survey));
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("for SURVEY"));
    }

    [Fact]
    public void TheRollOffHappensAtReposition()
    {
        var (game, parser, _, _) = IntoTheLane();

        Assert.Empty(game.SurveyContests);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);

        Assert.Single(game.SurveyContests);
        Assert.Contains(game.PlayByPlay, l => l.Contains("catches") && l.Contains("coming through"));
    }

    [Fact]
    public void BeatingTheSurveyLetsTheMoveThrough()
    {
        var (game, parser, mover, surveyor) = IntoTheLane();

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);

        Roll(parser, mover.Name, 80);
        Roll(parser, surveyor.Name, 20);

        Assert.Equal(Waymark.C, mover.Position);
        Assert.Empty(game.SurveyContests);
        Assert.Contains(game.PlayByPlay, l => l.Contains("beats the survey"));
    }

    [Fact]
    public void LosingTheRollOffStopsTheMoveDead()
    {
        var (game, parser, mover, surveyor) = IntoTheLane();

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);

        Roll(parser, mover.Name, 20);
        Roll(parser, surveyor.Name, 80);

        Assert.Equal(Waymark.A, mover.Position);
        Assert.Contains(game.PlayByPlay, l => l.Contains("holds the lane"));
    }

    /// <summary>The surveyor is defending the lane, so a tie keeps it shut.</summary>
    [Fact]
    public void ATieHoldsTheLane()
    {
        var (game, parser, mover, surveyor) = IntoTheLane();

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);

        Roll(parser, mover.Name, 50);
        Roll(parser, surveyor.Name, 50);

        Assert.Equal(Waymark.A, mover.Position);
        Assert.Contains(game.PlayByPlay, l => l.Contains("holds the lane"));
    }

    /// <summary>A survey guards against the other side, not your own team-mates.</summary>
    [Fact]
    public void ASurveyDoesNotCatchATeamMate()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var mover = Player(game, "SIM RED", PlayerRole.LeftForward);
        var surveyor = Player(game, "SIM RED", PlayerRole.LeftDefender);

        mover.Position = Waymark.A;
        surveyor.Position = Waymark.A;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(surveyor.Name, $"|| {surveyor.Name} watches. [SURVEY][A <-> C]", now);
        parser.ProcessMessage(mover.Name, $"|| {mover.Name} pushes up. [MOVE to C]", now);
        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.Empty(game.SurveyContests);
        Assert.Equal(Waymark.C, mover.Position);
    }

    /// <summary>
    /// An open roll-off would swallow both players' rolls in the next phase, so it is
    /// closed at the boundary — with the lane holding, since the surveyor defends it.
    /// </summary>
    [Fact]
    public void AnUnrolledContestClosesAtTheNextPhase()
    {
        var (game, parser, mover, _) = IntoTheLane();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);
        Assert.Single(game.SurveyContests);

        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now.AddSeconds(20));

        Assert.Empty(game.SurveyContests);
        Assert.Equal(Waymark.A, mover.Position);
        Assert.Contains(game.PlayByPlay, l => l.Contains("never rolled off"));
    }

    /// <summary>
    /// A survey watches the lane ahead. Somebody already standing alongside the
    /// surveyor and setting off elsewhere was never in it (slide 48).
    /// </summary>
    [Fact]
    public void ASurveyCannotCatchSomeoneLeavingItsOwnWaymark()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var mover = Player(game, "SIM RED", PlayerRole.LeftForward);
        var surveyor = Player(game, "SIM GOLD", PlayerRole.LeftDefender);

        // The surveyor is standing at the waymark the mover is leaving from.
        mover.Position = Waymark.A;
        surveyor.Position = Waymark.A;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(surveyor.Name, $"|| {surveyor.Name} watches. [SURVEY][A <-> C]", now);
        parser.ProcessMessage(mover.Name, $"|| {mover.Name} swims for position. [MOVE to C]", now);
        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.Empty(game.SurveyContests);
        Assert.Equal(Waymark.C, mover.Position);
    }

    /// <summary>
    /// A tackle is a movement, so a survey catches one coming down its lane — and
    /// beating it cancels the tackle outright rather than merely halting the travel,
    /// so the daze comes off too (slide 59).
    /// </summary>
    [Fact]
    public void ASurveyCanCancelATackleAndItsDaze()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var forward = Player(game, "SIM RED", PlayerRole.LeftForward);
        var victim = Player(game, "SIM GOLD", PlayerRole.Midfield);
        var surveyor = Player(game, "SIM GOLD", PlayerRole.LeftDefender);

        forward.Position = Waymark.A;
        victim.Position = Waymark.C;
        surveyor.Position = Waymark.C;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(surveyor.Name, $"|| {surveyor.Name} watches. [SURVEY][A <-> C]", now);
        parser.ProcessMessage(forward.Name, $"|| {forward.Name} crashes in. [TACKLE -> {victim.Name}]", now);

        parser.ProcessMessage(Ref, $"Random! {forward.Name} rolls a 90 (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {victim.Name} rolls a 10 (out of 100).", now);

        Assert.True(victim.IsDazed, "The tackle landed before Reposition.");

        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);
        Assert.Single(game.SurveyContests);

        // The surveyor wins the roll-off, so the tackle never happened.
        Roll(parser, forward.Name, 20);
        Roll(parser, surveyor.Name, 80);

        Assert.Equal(Waymark.A, forward.Position);
        Assert.False(victim.IsDazed, "A cancelled tackle takes its daze with it.");
        Assert.Contains(game.PlayByPlay, l => l.Contains("reads the tackle and cancels it"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2024)]
    public void GeneratedMatchesDoNotRollForDeclaringSurvey(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("for SURVEY"));
    }
}
