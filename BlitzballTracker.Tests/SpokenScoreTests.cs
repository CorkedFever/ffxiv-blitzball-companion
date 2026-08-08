using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// The score as scorekeepers actually call it.
///
/// It is announced in plain Yell — "Vidraal 2 - 1 Barracudas." — rather than in the
/// bracketed form the tracker originally looked for, which is why it appeared for a
/// while that the score was never posted at all. It is, in a shape loose enough that
/// the only safe way to read it is to insist both names are teams we know.
/// </summary>
public class SpokenScoreTests
{
    private const string Keeper = "Scorekeeper";

    private static (BlitzGame Game, ChatParser Parser) NewGame(
        string home = "SIM RED", string away = "SIM GOLD",
        string? homeCity = null, string? awayCity = null)
    {
        var roster = MatchSimulator.StandardRoster();
        roster.HomeTeam = home;
        roster.AwayTeam = away;
        roster.HomeAlias = homeCity;
        roster.AwayAlias = awayCity;

        foreach (var entry in roster.Entries)
            entry.Team = entry.Team == "SIM RED" ? home : away;

        var game = new BlitzGame();
        game.ApplyRoster(roster);
        return (game, new ChatParser(game));
    }

    [Theory]
    [InlineData("\"SIM RED 2 - 1 SIM GOLD.\"", 2, 1)]
    [InlineData("\"Halftiiiiiiime! SIM RED 1 - 0 SIM GOLD.\"", 1, 0)]
    [InlineData("SIM RED 3 – 2 SIM GOLD", 3, 2)]
    public void ASpokenScoreIsRead(string message, int home, int away)
    {
        var (game, parser) = NewGame();

        parser.ProcessMessage(Keeper, message, DateTime.Now);

        Assert.Equal(home, game.Score.Home);
        Assert.Equal(away, game.Score.Away);
        Assert.False(game.ScoreIsDerived);
    }

    /// <summary>Scorekeepers write whichever side they please first.</summary>
    [Fact]
    public void TheSidesAreOrientedToTheRoster()
    {
        var (game, parser) = NewGame();

        parser.ProcessMessage(Keeper, "\"SIM GOLD 4 - 2 SIM RED.\"", DateTime.Now);

        Assert.Equal(2, game.Score.Home);
        Assert.Equal(4, game.Score.Away);
    }

    [Fact]
    public void AFinalScoreLineIsRead()
    {
        var (game, parser) = NewGame();

        parser.ProcessMessage(Keeper, "[Final score: SIM GOLD - 2, SIM RED - 1]", DateTime.Now);

        Assert.Equal(1, game.Score.Home);
        Assert.Equal(2, game.Score.Away);
    }

    /// <summary>
    /// The guard that makes the loose pattern safe. A bare "N - M" between two words is
    /// ordinary chat, and believing it would rewrite the score off a passing remark.
    /// </summary>
    [Theory]
    [InlineData("\"That was a 3 - 1 sort of half, lads.\"")]
    [InlineData("\"Rangers 2 - 0 Rovers, over on the other pitch.\"")]
    [InlineData("\"I rolled 40 - 20 under what I needed.\"")]
    public void ScoresBetweenTeamsWeDoNotKnowAreIgnored(string message)
    {
        var (game, parser) = NewGame();

        parser.ProcessMessage(Keeper, message, DateTime.Now);

        Assert.Equal(0, game.Score.Home);
        Assert.Equal(0, game.Score.Away);
    }

    /// <summary>
    /// Referees call a side by its city as readily as by its name, and mean the same
    /// team. Possession at a Blitzon is read from that call, so the city has to resolve.
    /// </summary>
    [Fact]
    public void ATeamIsRecognisedByItsCity()
    {
        var (game, parser) = NewGame("Barracudas", "Vidraal", homeCity: "Limsa");
        var now = DateTime.Now;

        parser.ProcessMessage(Keeper, "<< BLITZOFF >>", now);
        parser.ProcessMessage(Keeper, "[Teams, please reset for Blitzon.  Limsa ball.]", now.AddMinutes(2));
        parser.ProcessMessage(Keeper, "<< BLITZON >>", now.AddMinutes(2));

        // Limsa is the Barracudas, who are home — so the home side is the one behind.
        Assert.Equal(1, game.GoalsSeen);
        Assert.True(game.ScoreIsCertain);
        Assert.Equal(0, game.Score.Home);
        Assert.Equal(1, game.Score.Away);
    }

    [Fact]
    public void ACityStillWorksAfterARoundTripThroughARecording()
    {
        var roster = MatchSimulator.StandardRoster();
        roster.HomeTeam = "Barracudas";
        roster.AwayTeam = "Vidraal";
        roster.HomeAlias = "Limsa";

        var recovered = RosterHeader.Read(
            RosterHeader.Write(roster).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.NotNull(recovered);
        Assert.Equal("Limsa", recovered!.HomeAlias);
        Assert.Null(recovered.AwayAlias);
    }
}
