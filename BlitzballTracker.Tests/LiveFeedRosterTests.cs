using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// The live feed sends the roster as the same header recordings use, and the far end
/// rebuilds it with <see cref="BlitzGame.ApplyRoster"/>.
///
/// The web app used to build PlayerState objects by hand from a bespoke DTO and never
/// set CurrentRoster — which is what ChatParser keys its name index off. The index was
/// therefore never built and every player name in the feed was discarded: phases and the
/// scoreboard worked, the field stayed empty, and it read as a rendering fault.
/// </summary>
public class LiveFeedRosterTests
{
    private const string Ref = "Sim Referee";

    [Fact]
    public void ARosterSurvivesTheRoundTrip()
    {
        var sent = MatchSimulator.StandardRoster();

        var received = RosterHeader.Read(RosterHeader.Write(sent).Split('\n'));

        Assert.NotNull(received);
        Assert.Equal(sent.HomeTeam, received!.HomeTeam);
        Assert.Equal(sent.AwayTeam, received.AwayTeam);
        Assert.Equal(sent.Entries.Count, received.Entries.Count);

        foreach (var entry in sent.Entries)
        {
            var match = received.Entries.Single(e => e.Name == entry.Name);
            Assert.Equal(entry.Team, match.Team);
            Assert.Equal(entry.Role, match.Role);
        }
    }

    /// <summary>
    /// The regression proper. Populating Players is not enough — without CurrentRoster
    /// the parser's index never builds, so it recognises nobody however full the
    /// dictionary looks.
    /// </summary>
    [Fact]
    public void PopulatingPlayersWithoutARosterRecognisesNobody()
    {
        var game = new BlitzGame();
        var parser = new ChatParser(game);

        // What the old endpoint did: build the players by hand.
        foreach (var entry in MatchSimulator.StandardRoster().Entries)
        {
            game.Players[entry.Name] = new PlayerState
            {
                Name = entry.Name,
                Team = entry.Team,
                Role = entry.Role,
            };
        }

        Assert.True(game.HasRoster, "Players are present, so this looks tracked.");

        var subject = game.Players.Values.First(p => !p.IsGoalkeeper);

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);
        parser.ProcessMessage(subject.Name, $"|| {subject.Name} moves up. [MOVE to C]", DateTime.Now);

        // ...and yet nothing is attributed to them.
        Assert.Empty(game.CurrentPhaseActions);
    }

    [Fact]
    public void ApplyingTheRosterMakesTheFeedTrackable()
    {
        var game = new BlitzGame();
        var parser = new ChatParser(game);

        var roster = RosterHeader.Read(RosterHeader.Write(MatchSimulator.StandardRoster()).Split('\n'));
        Assert.NotNull(roster);

        game.ApplyRoster(roster!);

        var subject = game.Players.Values.First(p => !p.IsGoalkeeper);

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);
        parser.ProcessMessage(subject.Name, $"|| {subject.Name} moves up. [MOVE to C]", DateTime.Now);

        Assert.Single(game.CurrentPhaseActions);
        Assert.Equal(subject.Name, game.CurrentPhaseActions[0].PlayerName);
    }

    /// <summary>
    /// A whole match through the feed's own path: header first, then the chat, exactly
    /// as the plugin sends it.
    /// </summary>
    [Fact]
    public void AMatchFedThroughTheWireTracksNormally()
    {
        var roster = RosterHeader.Read(RosterHeader.Write(MatchSimulator.StandardRoster()).Split('\n'))!;

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(MatchSimulator.StandardRoster(), 2024).Generate(), parser);

        Assert.Equal(12, game.Players.Count);
        Assert.True(game.IsFinished);
        Assert.Contains(game.Players.Values, p => p.Position != Waymark.None);
    }

    /// <summary>Resetting mid-broadcast must not throw the lineup away with the match.</summary>
    [Fact]
    public void ResettingKeepsTheRoster()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());

        game.Score = new Score(2, 1);
        game.Reset();

        Assert.True(game.HasRoster);
        Assert.Equal(12, game.Players.Count);
        Assert.Equal(0, game.Score.Home);
    }
}
