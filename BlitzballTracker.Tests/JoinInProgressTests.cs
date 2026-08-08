using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Picking up a match that started before the tracker did.
///
/// Chat announces what happens, never what is currently true, so nothing about the
/// field can be read back out of it. In the plugin the way in is the arena itself —
/// every player is physically standing on a waymark. What that cannot recover is
/// cleared rather than assumed, because a tracker quietly guessing is worse than one
/// saying it does not know.
/// </summary>
public class JoinInProgressTests
{
    private const string Ref = "Sim Referee";
    private const string Scorer = "Sim Scorekeeper";

    private static (BlitzGame Game, ChatParser Parser) MidMatch()
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, 2024).Generate(), parser);

        return (game, parser);
    }

    [Fact]
    public void JoiningStartsTracking()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());

        Assert.False(game.IsActive);

        game.JoinInProgress();

        Assert.True(game.IsActive);
        Assert.False(game.IsFinished);
    }

    /// <summary>
    /// The roster is the one thing a late joiner must already have, and it survives.
    /// </summary>
    [Fact]
    public void JoiningKeepsTheRoster()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());

        game.JoinInProgress();

        Assert.True(game.HasRoster);
        Assert.Equal(12, game.Players.Count);
    }

    /// <summary>
    /// Everything invisible to chat starts blank. Carrying over whatever happened to be
    /// in memory would look authoritative while being made up.
    /// </summary>
    [Fact]
    public void JoiningClearsWhatCannotBeKnown()
    {
        var (game, _) = MidMatch();

        var keeper = game.Players.Values.First(p => p.IsGoalkeeper);
        var outfielder = game.Players.Values.First(p => !p.IsGoalkeeper);

        keeper.GuardBonus = 30;
        outfielder.IsDazed = true;
        outfielder.IsBlocked = true;
        outfielder.PhaseRoll = 55;
        game.LastBackPassRound[outfielder.Team] = 3;

        game.JoinInProgress();

        Assert.Equal(0, keeper.GuardBonus);
        Assert.All(game.Players.Values, p =>
        {
            Assert.False(p.IsDazed);
            Assert.False(p.IsBlocked);
            Assert.False(p.IsDiving);
            Assert.False(p.IsSurveying);
            Assert.Null(p.PhaseRoll);
            Assert.Null(p.RalliedRoll);
        });

        Assert.Empty(game.Blocks);
        Assert.Empty(game.RushGates);
        Assert.Empty(game.LastBackPassRound);
        Assert.Null(game.Fumble);
        Assert.Empty(game.CurrentPhaseActions);
    }

    /// <summary>
    /// Positions are cleared, not kept.
    ///
    /// A match joined in progress has been running for a while, so the kickoff
    /// formation applied with the roster is simply wrong. Leaving it there is worse
    /// than admitting ignorance: the arena then contradicts every player at once and
    /// the log fills with faults that are really the first honest reading. The plugin
    /// fills them from the arena immediately afterwards.
    /// </summary>
    [Fact]
    public void JoiningForgetsWhereEveryoneWas()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());

        var player = game.Players.Values.First(p => !p.IsGoalkeeper);
        player.Position = Waymark.Two;

        game.JoinInProgress();

        Assert.All(game.Players.Values, p => Assert.Equal(Waymark.None, p.Position));
    }

    /// <summary>
    /// Score, phase, round and possession all correct themselves from the next referee
    /// call, so none of them need recovering on the way in.
    /// </summary>
    [Fact]
    public void TheNextRefereeCallCorrectsTheMatchState()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        game.JoinInProgress();

        var carrier = game.Players.Values.First(p => !p.IsGoalkeeper);

        parser.ProcessMessage(Ref, "<< ROUND 7 >>", now);
        parser.ProcessMessage(Scorer, "[[ SIM RED 2:1 SIM GOLD ]]", now);
        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now);
        parser.ProcessMessage(Scorer, $"[BALL to {carrier.Name}]", now);

        Assert.Equal(7, game.Round);
        Assert.Equal(2, game.Score.Home);
        Assert.Equal(1, game.Score.Away);
        Assert.Equal(GamePhase.InnerPhase, game.Phase);
        Assert.Equal(carrier.Name, game.BallCarrier);
    }

    /// <summary>A joined match follows the rest of the feed like any other.</summary>
    [Fact]
    public void PlayIsTrackedNormallyAfterJoining()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        game.JoinInProgress();

        var forward = game.Players.Values.First(p =>
            p.Role == PlayerRole.LeftForward && p.Team.Equals("SIM RED", StringComparison.OrdinalIgnoreCase));
        var victim = game.Players.Values.First(p =>
            p.Role == PlayerRole.Midfield && p.Team.Equals("SIM GOLD", StringComparison.OrdinalIgnoreCase));

        forward.Position = Waymark.B;
        victim.Position = Waymark.Two;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(forward.Name, $"|| {forward.Name} crashes in. [TACKLE -> {victim.Name}]", now);
        parser.ProcessMessage(Ref, $"Random! {forward.Name} rolls a 90 (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {victim.Name} rolls a 10 (out of 100).", now);

        Assert.True(victim.IsDazed);
    }
}
