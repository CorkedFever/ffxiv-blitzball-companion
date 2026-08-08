using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// The match arrives on two channels, and not everyone can see both.
///
/// Referees post the structure — phases, rounds, the score — in the league's
/// cross-world linkshell. Players declare and roll in Yell. A spectator without the
/// linkshell sees the play but never the structure, and the tracker has to say so
/// rather than reporting Pre-Game, Round 0/10 for an hour.
/// </summary>
public class PhaseFeedTests
{
    private const string Ref = "Match Referee";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void APhaseCallMarksTheFeedAsPresent()
    {
        var (game, parser) = NewGame();

        Assert.False(game.HasPhaseFeed);

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);

        Assert.True(game.HasPhaseFeed);
    }

    [Fact]
    public void PlayWithoutPhaseCallsIsFlaggedOnce()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var actor = Player(game, "SIM RED", PlayerRole.LeftDefender);
        actor.Position = Waymark.A;

        parser.ProcessMessage(actor.Name, $"|| {actor.Name} swims. [MOVE to C]", now);
        parser.ProcessMessage(actor.Name, $"|| {actor.Name} swims. [MOVE to C]", now.AddSeconds(30));

        var notices = game.PlayByPlay.Count(l => l.Contains("no referee phase calls"));

        Assert.False(game.HasPhaseFeed);
        Assert.Equal(1, notices);
    }

    [Fact]
    public void NothingIsFlaggedWhenTheFeedIsThere()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var actor = Player(game, "SIM RED", PlayerRole.LeftDefender);
        actor.Position = Waymark.A;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(actor.Name, $"|| {actor.Name} swims. [MOVE to C]", now);

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("no referee phase calls"));
    }

    /// <summary>
    /// The point of saying so rather than going quiet: everything that arrives in Yell
    /// is still followed. Actions, rolls, contests and possession do not need the
    /// linkshell.
    /// </summary>
    [Fact]
    public void PlayIsStillTrackedWithoutAnyPhaseCalls()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var forward = Player(game, "SIM RED", PlayerRole.LeftForward);
        var victim = Player(game, "SIM GOLD", PlayerRole.Midfield);

        forward.Position = Waymark.B;
        victim.Position = Waymark.Two;

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} crashes in. [TACKLE -> {victim.Name}]", now);
        parser.ProcessMessage(Ref, $"Random! {forward.Name} rolls a 90 (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {victim.Name} rolls a 10 (out of 100).", now);

        Assert.False(game.HasPhaseFeed);
        Assert.True(victim.IsDazed, "A contest in Yell resolves without any phase call.");
    }

    /// <summary>
    /// A recording keeps the referee's channel, or it cannot be replayed into a full
    /// match later — the phases and rounds all come through there.
    /// </summary>
    [Theory]
    [InlineData("CrossLinkShell")]
    [InlineData("CWLS")]
    [InlineData("Yell")]
    [InlineData("Dice Roll")]
    public void ARecordedRefereeChannelReplays(string channel)
    {
        Assert.True(LogReplay.IsRelevantChannel(channel));
    }

    [Theory]
    [InlineData("Party")]
    [InlineData("Linkshell")]
    [InlineData("Say")]
    public void PrivateChannelsAreLeftOutOfReplay(string channel)
    {
        Assert.False(LogReplay.IsRelevantChannel(channel));
    }

    [Fact]
    public void ResettingForgetsThatTheFeedWasSeen()
    {
        var (game, parser) = NewGame();

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);
        Assert.True(game.HasPhaseFeed);

        game.Reset();

        Assert.False(game.HasPhaseFeed);
    }
}
