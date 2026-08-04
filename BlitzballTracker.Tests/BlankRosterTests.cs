using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// The roster editor pads both squads to six rows so the grid stays stable while
/// typing, which means a roster can hold twelve entries and name nobody.
///
/// Treating that as a loaded roster produced a nasty failure: phases and scores kept
/// parsing normally, because neither needs players, while everything player-related
/// silently did nothing. The field rendered with every zone empty and the match
/// looked like it was running fine.
/// </summary>
public class BlankRosterTests
{
    private static Roster PaddedButNameless()
    {
        var roster = new Roster { HomeTeam = "HOME", AwayTeam = "AWAY" };

        for (var i = 0; i < 12; i++)
            roster.Entries.Add(new RosterEntry { Team = i < 6 ? "HOME" : "AWAY", Role = PlayerRole.Midfield });

        return roster;
    }

    [Fact]
    public void ARosterOfBlankRowsIsNotALoadedRoster()
    {
        var roster = PaddedButNameless();

        Assert.Equal(12, roster.Entries.Count);
        Assert.Equal(0, roster.NamedCount);
        Assert.True(roster.IsEmpty);
    }

    [Fact]
    public void ApplyingABlankRosterLeavesNothingTracked()
    {
        var game = new BlitzGame();
        game.ApplyRoster(PaddedButNameless());

        Assert.Empty(game.Players);

        // The critical part: this must not claim to be ready, or every player-facing
        // path fails quietly while the match appears to run.
        Assert.False(game.HasRoster);
    }

    [Fact]
    public void PartiallyFilledRostersCountOnlyTheNamedPlayers()
    {
        var roster = PaddedButNameless();
        roster.Entries[0].Name = "Someone Real";
        roster.Entries[7].Name = "Another Real";

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        Assert.Equal(2, roster.NamedCount);
        Assert.Equal(2, game.Players.Count);
        Assert.True(game.HasRoster);
    }

    /// <summary>
    /// With no players tracked, a scoreboard line must not be allowed to define the
    /// teams: the simulator posts them in either order, so whichever line arrived
    /// first would decide which side was home.
    /// </summary>
    [Fact]
    public void ABlankRosterDoesNotLetScoreLinesDecideTheTeams()
    {
        var real = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(real);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(real, 1234).Generate(), parser);

        // Home stays whatever the roster said, whichever way round the referee
        // happened to type the scoreboard.
        Assert.Equal("SIM RED", game.HomeTeam);
        Assert.Equal("SIM GOLD", game.AwayTeam);
    }

    /// <summary>
    /// The visible symptom from the bug report: a match that plays out with every
    /// zone empty. With a real roster, players must actually be placed on the field.
    /// </summary>
    [Fact]
    public void APlayedMatchPutsPlayersOnTheField()
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, 77).Generate(), parser);

        Assert.Equal(12, game.Players.Count);
        Assert.DoesNotContain(game.Players.Values, p => p.Position == Waymark.None);

        // And somebody must have ended up holding the ball at some point.
        Assert.Contains(game.PlayByPlay, line => line.Contains("possession", StringComparison.OrdinalIgnoreCase));
    }
}
