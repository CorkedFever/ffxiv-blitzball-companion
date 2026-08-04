using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using Xunit;

namespace BlitzballTracker.Tests;

public class PlayerNameTests
{
    [Theory]
    [InlineData("Beki Dotharl [Mateus]", "Beki Dotharl")]
    [InlineData("O'looqa Honji (Balmung)", "O'looqa Honji")]
    [InlineData("  Tuasun Chaontis  ", "Tuasun Chaontis")]
    public void StripWorld_RemovesWorldSuffix(string raw, string expected)
        => Assert.Equal(expected, PlayerNames.StripWorld(raw));

    [Fact]
    public void Normalize_TreatsLogVariationsAsTheSamePlayer()
    {
        // The same character as they appear across different lines of a real export.
        var forms = new[]
        {
            "Tuasun Chaontis",
            "Tuasun Chaontis ",
            "Tuasun Chaontis (Malboro)",
            "Tuasun Chaontis [Malboro]",
        };

        var keys = forms.Select(PlayerNames.Normalize).Distinct().ToList();
        Assert.Single(keys);
    }

    [Fact]
    public void Normalize_KeepsApostrophesAndHyphens()
    {
        // These are load-bearing in FFXIV names and must not be stripped.
        Assert.Equal("k'yriss arashito", PlayerNames.Normalize("K'yriss Arashito"));
        Assert.Equal("qasim abd-al-daiya", PlayerNames.Normalize("Qasim Abd-al-daiya"));
    }
}

public class RosterIndexTests
{
    private static RosterIndex Index() => new(
    [
        "Mhinco Pokhmhakwaahni",
        "Sataya Saoraigne",
        "Soren Kell",
        "Manami Tsukino",
    ]);

    [Fact]
    public void Resolve_MatchesRegardlessOfWorldSuffix()
        => Assert.Equal("Soren Kell", Index().Resolve("Soren Kell (Mateus)"));

    [Fact]
    public void Resolve_MatchesUniqueFirstName()
        => Assert.Equal("Manami Tsukino", Index().Resolve("Manami"));

    [Fact]
    public void Resolve_RejectsNamesOffTheRoster()
    {
        Assert.Null(Index().Resolve("Papani Pani"));
        Assert.Null(Index().Resolve("Ffon Aveross"));
    }

    /// <summary>
    /// Referees abbreviate when calling corrections: "REROLL Mhin/Sata".
    /// A closed roster makes prefix matching safe.
    /// </summary>
    [Fact]
    public void ResolveShorthand_ExpandsRefereeAbbreviations()
    {
        var index = Index();
        Assert.Equal("Mhinco Pokhmhakwaahni", index.ResolveShorthand("Mhin"));
        Assert.Equal("Sataya Saoraigne", index.ResolveShorthand("Sata"));
    }

    [Fact]
    public void ResolveShorthand_RefusesAmbiguousPrefixes()
    {
        var index = new RosterIndex(["Soren Kell", "Soleil Mas"]);

        // "So" is both too short and ambiguous: guessing would silently mis-credit.
        Assert.Null(index.ResolveShorthand("So"));
    }
}

public class RosterParsingTests
{
    [Fact]
    public void ParseFromText_ReadsAPastedTeamSheet()
    {
        var roster = Roster.ParseFromText("""
            DAIGOROS
            Mhinco Pokhmhakwaahni [Mateus] - M
            Soren Kell - LF
            GK: Mirita Ebenae

            AUSPICES
            Manami Tsukino / LD
            J'dextera Sol - GK
            """);

        Assert.Equal("DAIGOROS", roster.HomeTeam);
        Assert.Equal("AUSPICES", roster.AwayTeam);
        Assert.Equal(5, roster.Entries.Count);

        var mhinco = roster.Entries[0];
        Assert.Equal("Mhinco Pokhmhakwaahni", mhinco.Name);
        Assert.Equal("Mateus", mhinco.World);
        Assert.Equal(PlayerRole.Midfield, mhinco.Role);
        Assert.Equal("DAIGOROS", mhinco.Team);

        Assert.Equal(PlayerRole.Goalkeeper, roster.Entries[2].Role);
        Assert.Equal("Mirita Ebenae", roster.Entries[2].Name);
        Assert.Equal("AUSPICES", roster.Entries[3].Team);
    }

    [Fact]
    public void Validate_WarnsAboutShortSquadsRatherThanFailing()
    {
        // Matches really do run short-handed; the Chocobowl log has a live disconnect.
        var roster = Roster.ParseFromText("""
            DAIGOROS
            Soren Kell - LF
            AUSPICES
            J'dextera Sol - GK
            """);

        var problems = roster.Validate();
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("expected 6"));
    }
}

public class SpectatorExclusionTests
{
    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(Fixtures.RealMatchRoster());
        return (game, new ChatParser(game));
    }

    /// <summary>
    /// The bug that motivated all of this. Real event logs open with the crowd
    /// shouting along; game_chocobowl.txt has roughly eighteen different people
    /// doing it. None of them are players.
    /// </summary>
    [Fact]
    public void CrowdShoutingBlitzoff_DoesNotCreatePlayers()
    {
        var (game, parser) = NewGame();
        var before = game.Players.Count;

        foreach (var name in new[]
                 {
                     "Tuasun Chaontis", "K'yriss Arashito", "Helios Silberfluegel",
                     "Rinalys Dawnstar", "Nisshoku Hakumei", "Mauh Awendah",
                 })
        {
            parser.ProcessMessage(name, "\"BLITZOFF!!!\"", DateTime.Now);
        }

        Assert.Equal(before, game.Players.Count);
        Assert.DoesNotContain("Tuasun Chaontis", game.Players.Keys);
    }

    [Fact]
    public void SpectatorDiceRolls_DoNotCreatePlayersOrSkewStats()
    {
        var (game, parser) = NewGame();

        parser.ProcessMessage("Papani Pani", "Random! Papani Pani rolls a 98 (out of 100).", DateTime.Now);

        Assert.DoesNotContain("Papani Pani", game.Players.Keys);
        Assert.All(game.Players.Values, p => Assert.Equal(0, p.TotalRolls));
    }

    [Fact]
    public void CommentaryNamingPlayers_DoesNotMakeTheCommentatorAPlayer()
    {
        var (game, parser) = NewGame();

        parser.ProcessMessage(
            "Lakaera Riverthorn",
            "\"And it's Mhinco of the Daigoros team with the ball! What a strong start folks!\"",
            DateTime.Now);

        Assert.DoesNotContain("Lakaera Riverthorn", game.Players.Keys);
        Assert.Equal(12, game.Players.Count);
    }
}

/// <summary>
/// Coverage against a committed sample of a real recorded match.
///
/// Generated matches carry the behavioural load; these exist for the things a
/// generator will not reproduce faithfully, namely how untidy real human chat is.
/// </summary>
public class RealMatchSampleTests
{
    private static (BlitzGame Game, ChatParser Parser) ReplayDaigoros()
    {
        var game = new BlitzGame();
        game.ApplyRoster(Fixtures.RealMatchRoster());

        var parser = new ChatParser(game);
        LogReplay.ReplayFile(Fixtures.RealMatchSample, parser);

        return (game, parser);
    }

    [RealMatchFact]
    public void Replay_TracksExactlyTheRosteredTwelve()
    {
        var (game, _) = ReplayDaigoros();
        Assert.Equal(12, game.Players.Count);
    }

    [RealMatchFact]
    public void Replay_NeverAdoptsRefereesOrCommentators()
    {
        var (game, _) = ReplayDaigoros();

        foreach (var official in Fixtures.RealMatchNonPlayers)
            Assert.DoesNotContain(official, game.Players.Keys);
    }

    /// <summary>
    /// The visible symptom of the old parser: everyone piling onto Center because
    /// roleless phantoms fell through ResetPositions' default case.
    /// </summary>
    [RealMatchFact]
    public void Replay_DoesNotCollapseEveryoneOntoOneWaymark()
    {
        var (game, _) = ReplayDaigoros();

        var occupied = game.Players.Values.Select(p => p.Position).Distinct().ToList();
        Assert.True(occupied.Count > 1,
            $"All players ended on a single waymark: {occupied.FirstOrDefault()}");
    }

    [RealMatchFact]
    public void Replay_LeavesNoRosteredPlayerUnplaced()
    {
        var (game, _) = ReplayDaigoros();

        var unplaced = game.Players.Values
            .Where(p => p.Position == Waymark.None)
            .Select(p => p.Name)
            .ToList();

        Assert.True(unplaced.Count == 0,
            $"Rostered players left at Waymark.None: {string.Join(", ", unplaced)}");
    }

    [RealMatchFact]
    public void Replay_NeverReportsARosteredPlayerAsUnmatched()
    {
        var (game, parser) = ReplayDaigoros();

        var misfiled = parser.UnmatchedNames.Keys
            .Where(n => game.Players.ContainsKey(n))
            .ToList();

        Assert.True(misfiled.Count == 0,
            $"Roster members treated as strangers: {string.Join(", ", misfiled)}");
    }

    [RealMatchFact]
    public void Replay_RecognisesTheMatchAndScoresIt()
    {
        var (game, _) = ReplayDaigoros();

        Assert.True(game.IsActive, "Blitzoff was never detected.");
        Assert.Equal("DAIGOROS", game.HomeTeam);
        Assert.Equal("AUSPICES", game.AwayTeam);
        Assert.True(game.Score.Home + game.Score.Away > 0, "No goals were recorded.");
    }
}
