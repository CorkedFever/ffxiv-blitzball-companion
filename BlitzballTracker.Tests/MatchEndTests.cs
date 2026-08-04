using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A match has an ending. Round 10 finishing with the ball in a strike zone gets one
/// last exchange — the buzzer phase — and every match closes on a final whistle.
///
/// Neither existed: generated matches stopped mid-sentence, and nothing ever set
/// <see cref="GamePhase.PostGame"/>, so a finished game sat in whatever phase it
/// happened to end on.
/// </summary>
public class MatchEndTests
{
    private const string Ref = "Sim Referee";

    private static (BlitzGame Game, ChatParser Parser) Replay(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        return (game, parser);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    [InlineData(31337)]
    public void EveryGeneratedMatchEndsOnAFinalWhistle(int seed)
    {
        var (game, _) = Replay(seed);

        Assert.True(game.IsFinished);
        Assert.Equal(GamePhase.PostGame, game.Phase);
        Assert.Contains(game.PlayByPlay, l => l.Contains("FULL TIME"));
    }

    /// <summary>
    /// The buzzer phase only happens when the ball is in a strike zone at the end of
    /// the round, so it is not guaranteed for any one seed — but across a spread of
    /// them it must happen, or the generator is not producing it at all.
    /// </summary>
    [Fact]
    public void GeneratedMatchesReachTheBuzzerPhase()
    {
        var seeds = new[] { 1, 42, 99, 2024, 31337, 8675309 };

        var reached = seeds.Count(seed =>
            Replay(seed).Game.PlayByPlay.Any(l => l.Contains("BUZZER PHASE")));

        Assert.True(reached > 0,
            "No generated match reached a buzzer phase across six seeds.");
    }

    [Fact]
    public void TheFinalWhistleNamesTheWinner()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());

        var parser = new ChatParser(game);
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< STANDBY FOR BLITZOFF >>", now);
        parser.ProcessMessage("Sim Scorekeeper", "[[ SIM GOLD 3:1 SIM RED ]]", now);
        parser.ProcessMessage(Ref, "<< GAME OVER >>", now);

        Assert.True(game.IsFinished);
        Assert.Contains(game.PlayByPlay, l => l.Contains("FULL TIME") && l.Contains("SIM GOLD"));
    }

    /// <summary>
    /// A finished match is still worth looking at, so the whistle must not wipe the
    /// board — only the roster editor and an explicit reset do that.
    /// </summary>
    [Fact]
    public void TheWhistleLeavesTheMatchReadable()
    {
        var (game, _) = Replay(2024);

        Assert.NotEmpty(game.PlayByPlay);
        Assert.Equal(12, game.Players.Count);
        Assert.Contains(game.Players.Values, p => p.Position != Waymark.None);
    }

    [Fact]
    public void ResettingClearsTheFinishedFlag()
    {
        var (game, _) = Replay(2024);
        Assert.True(game.IsFinished);

        game.Reset();

        Assert.False(game.IsFinished);
        Assert.Equal(GamePhase.PreGame, game.Phase);
    }
}
