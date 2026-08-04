using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// There is no occupancy limit on a waymark — any number of players may share one, and
/// the tracker must never refuse or flag it. Piling a side onto one marker is poor play,
/// not an infraction.
///
/// The generator spreads out anyway, because choosing uniformly funnelled a whole side
/// onto one marker: from Centre there are only two ways forward. That is a *taste* in
/// the generator and lives nowhere near the rules engine.
/// </summary>
public class FormationTests
{
    private const string Ref = "Sim Referee";
    private const string Scorer = "Sim Scorekeeper";

    /// <summary>Stacking a whole side on one waymark is legal and draws nothing.</summary>
    [Fact]
    public void SharingAWaymarkIsNeverRefused()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var side = game.Players.Values
            .Where(p => p.Team.Equals("SIM RED", StringComparison.OrdinalIgnoreCase) && !p.IsGoalkeeper)
            .ToList();

        foreach (var player in side)
            player.Position = Waymark.A;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);

        foreach (var player in side)
            parser.ProcessMessage(player.Name, $"|| {player.Name} pushes up. [MOVE to C]", now);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.All(side, p => Assert.Equal(Waymark.C, p.Position));
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("cannot enter"));
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains('⚑') && l.Contains("waymark"));
    }

    /// <summary>
    /// The generator should look like a match rather than a scrum. Not a rule, so this
    /// is a quality bar rather than a correctness one — but five of six on one marker
    /// was what it did before, and that reads as a bug on the field view.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    public void GeneratedSidesDoNotAllPileOntoOneWaymark(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);
        var parser = new ChatParser(game);

        var peak = 0;

        foreach (var line in new MatchSimulator(roster, seed).Generate())
        {
            if (!LogReplay.IsRelevantChannel(line.Channel)) continue;
            parser.ProcessMessage(line.Sender, line.Message, line.Timestamp);

            var crowd = game.Players.Values
                .Where(p => p.Position != Waymark.None)
                .GroupBy(p => (p.Team, p.Position))
                .Max(g => g.Count());

            peak = Math.Max(peak, crowd);
        }

        Assert.True(peak <= 4, $"A side bunched {peak} deep on one waymark.");
    }
}
