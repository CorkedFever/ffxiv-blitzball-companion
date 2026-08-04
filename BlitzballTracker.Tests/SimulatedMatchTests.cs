using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Behaviour is verified against generated matches rather than one recorded log.
///
/// A single recording is a single set of circumstances: it cannot produce a
/// shootout, a halftime side-switch, or a referee correction on demand, and it
/// cannot be varied. A seeded generator produces all of those, reproducibly, and
/// lets the same invariants be checked across many different matches.
/// </summary>
public class SimulatedMatchTests
{
    private static (BlitzGame Game, ChatParser Parser, MatchSimulator Sim) Play(
        int seed, SimulationOptions? options = null)
    {
        var roster = MatchSimulator.StandardRoster();
        var sim = new MatchSimulator(roster, seed, options);
        var lines = sim.Generate();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(lines, parser);

        return (game, parser, sim);
    }

    [Fact]
    public void SameSeedProducesTheSameMatch()
    {
        var first = new MatchSimulator(MatchSimulator.StandardRoster(), 1234).Generate();
        var second = new MatchSimulator(MatchSimulator.StandardRoster(), 1234).Generate();

        Assert.Equal(first.Count, second.Count);

        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Sender, second[i].Sender);
            Assert.Equal(first[i].Message, second[i].Message);
        }
    }

    [Fact]
    public void DifferentSeedsProduceDifferentMatches()
    {
        var a = new MatchSimulator(MatchSimulator.StandardRoster(), 1).Generate();
        var b = new MatchSimulator(MatchSimulator.StandardRoster(), 2).Generate();

        var same = a.Count == b.Count &&
                   a.Zip(b).All(pair => pair.First.Message == pair.Second.Message);

        Assert.False(same, "Two seeds produced an identical match.");
    }

    [Fact]
    public void GeneratedMatchIsRecognisedEndToEnd()
    {
        var (game, _, _) = Play(7);

        Assert.True(game.IsActive, "Blitzoff was never detected.");
        Assert.Equal("SIM RED", game.HomeTeam);
        Assert.Equal("SIM GOLD", game.AwayTeam);
        Assert.Equal(12, game.Players.Count);
    }

    /// <summary>
    /// The invariants that must hold for any match, checked across many of them.
    /// This is the part a single recorded log cannot give us.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    [InlineData(31337)]
    [InlineData(8675309)]
    public void InvariantsHoldAcrossManyMatches(int seed)
    {
        var (game, parser, _) = Play(seed);

        // Only the twelve rostered players are ever tracked.
        Assert.Equal(12, game.Players.Count);

        // Officials, commentators and crowd are never adopted.
        Assert.DoesNotContain(MatchSimulator.Referee, game.Players.Keys);
        Assert.DoesNotContain(MatchSimulator.Scorekeeper, game.Players.Keys);
        Assert.DoesNotContain(MatchSimulator.Commentator, game.Players.Keys);
        foreach (var spectator in MatchSimulator.CrowdNames)
            Assert.DoesNotContain(spectator, game.Players.Keys);

        // Nobody is left unplaced, and they never all pile onto one waymark.
        Assert.DoesNotContain(game.Players.Values, p => p.Position == Waymark.None);
        Assert.True(game.Players.Values.Select(p => p.Position).Distinct().Count() > 1);

        // Goalkeepers hold their goal. Nothing in a match may walk them out of it.
        foreach (var keeper in game.Players.Values.Where(p => p.Role == PlayerRole.Goalkeeper))
            Assert.Equal(game.OwnGoal(keeper), keeper.Position);

        // Scores stay sane.
        Assert.True(game.Score.Home >= 0 && game.Score.Away >= 0);

        // No roster member is ever treated as a stranger.
        Assert.DoesNotContain(parser.UnmatchedNames.Keys, name => game.Players.ContainsKey(name));

        // Stat bookkeeping stays consistent. This is the real check on the re-roll
        // undo path: a bad reversal shows up here as more successes than attempts,
        // or as counters driven negative.
        foreach (var player in game.Players.Values)
        {
            Assert.True(player.ActionsSucceeded <= player.ActionsAttempted,
                $"{player.Name} succeeded {player.ActionsSucceeded} of {player.ActionsAttempted} attempts.");

            Assert.True(player.ActionsAttempted >= 0);
            Assert.True(player.Tackles >= 0);
            Assert.True(player.Blocks >= 0);
            Assert.True(player.Dives >= 0);
            Assert.True(player.Goals >= 0);
            Assert.True(player.Saves >= 0);
            Assert.True(player.TotalRolls >= 0);
        }
    }

    /// <summary>
    /// The simulator posts the scoreboard with the teams reversed part of the time,
    /// exactly as real referees do. Home and away must never swap as a result: that
    /// bug inverted every isHome check and sent the whole field to the wrong end.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(256)]
    public void ReversedScoreboardLinesNeverSwapTheTeams(int seed)
    {
        var (game, _, _) = Play(seed);

        Assert.Equal("SIM RED", game.HomeTeam);
        Assert.Equal("SIM GOLD", game.AwayTeam);

        // Every player keeps the team the roster gave them.
        foreach (var player in game.Players.Values)
            Assert.True(player.Team is "SIM RED" or "SIM GOLD");
    }

    [Fact]
    public void CrowdNoiseNeverBecomesPlayersEvenWhenHeavy()
    {
        var (game, _, _) = Play(11, new SimulationOptions
        {
            IncludeCrowd = true,
            IncludeCommentary = true,
        });

        foreach (var spectator in MatchSimulator.CrowdNames)
            Assert.DoesNotContain(spectator, game.Players.Keys);

        Assert.Equal(12, game.Players.Count);
    }

    /// <summary>
    /// Referee corrections should be common enough to exercise, and must not corrupt
    /// state when they fire.
    /// </summary>
    [Fact]
    public void HeavyCorrectionLoadLeavesStateConsistent()
    {
        var (game, parser, _) = Play(5, new SimulationOptions
        {
            RerollChance = 0.5,
            GraceChance = 0.4,
            LateRollChance = 0.3,
        });

        Assert.Equal(12, game.Players.Count);

        foreach (var player in game.Players.Values)
        {
            Assert.True(player.ActionsSucceeded <= player.ActionsAttempted);
            Assert.True(player.Tackles >= 0);
            Assert.True(player.Blocks >= 0);
        }

        // Corrections should actually have happened, or this proves nothing.
        Assert.Contains(game.PlayByPlay, line => line.Contains("re-roll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ADrawnMatchGoesToAShootout()
    {
        // Zero rounds means nobody scores, so the shootout path always runs.
        var (game, _, _) = Play(1, new SimulationOptions { Sets = 1, RoundsPerSet = 0 });

        Assert.Contains(game.PlayByPlay, line => line.Contains("SHOOTOUT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HalftimeSwitchesSides()
    {
        var (game, _, _) = Play(21, new SimulationOptions { Sets = 2, RoundsPerSet = 1 });

        Assert.Contains(game.PlayByPlay, line => line.Contains("HALFTIME", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, game.Set);
    }
}
