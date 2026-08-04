using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Standing in the acting ring and declaring nothing is a loss of action, and
/// referees flag it.
///
/// With a whole ring acting at once it is easy to miss, so the tracker names anyone
/// who let the phase run out. Advisory only: whether it costs them anything is the
/// referees' call.
/// </summary>
public class MissedActionTests
{
    private const string Ref = "Sim Referee";

    private static (BlitzGame Game, ChatParser Parser) InOuterPhase(RuleOptions? rules = null)
    {
        var game = new BlitzGame();
        if (rules is not null) game.Rules = rules;
        game.ApplyRoster(MatchSimulator.StandardRoster());

        var parser = new ChatParser(game);
        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);

        return (game, parser);
    }

    private static bool Flagged(BlitzGame game, string name) =>
        game.PlayByPlay.Any(l => l.Contains("Loss of action") && l.Contains(name));

    [Fact]
    public void SomeoneInTheRingWhoDeclaresNothingIsNamed()
    {
        var (game, parser) = InOuterPhase();

        var idle = game.Players.Values.First(p =>
            PhaseRules.IsOuterZone(p.Position) && !p.HasBall);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);

        Assert.True(Flagged(game, idle.Name), $"{idle.Name} sat out and was not named.");
    }

    [Fact]
    public void DeclaringAnythingClearsYou()
    {
        var (game, parser) = InOuterPhase();
        var now = DateTime.Now;

        var actor = game.Players.Values.First(p => PhaseRules.IsOuterZone(p.Position));

        parser.ProcessMessage(actor.Name, $"|| {actor.Name} watches the lane. [SURVEY]", now);
        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.False(Flagged(game, actor.Name));
    }

    /// <summary>
    /// The inner ring is not acting during an outer phase, so its occupants are not
    /// sitting anything out.
    /// </summary>
    [Fact]
    public void PlayersOutsideTheActingRingAreNotNamed()
    {
        var (game, parser) = InOuterPhase();

        var keeper = game.Players.Values.First(p => p.Role == PlayerRole.Goalkeeper);
        Assert.True(PhaseRules.IsInnerZone(keeper.Position));

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);

        Assert.False(Flagged(game, keeper.Name));
    }

    /// <summary>The carrier sits the ring out by design and acts in their own turn.</summary>
    [Fact]
    public void TheBallCarrierIsNotNamed()
    {
        var (game, parser) = InOuterPhase();
        var now = DateTime.Now;

        var carrier = game.Players.Values.First(p => PhaseRules.IsOuterZone(p.Position));
        parser.ProcessMessage("Sim Scorekeeper", $"[BALL to {carrier.Name}]", now);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);

        Assert.False(Flagged(game, carrier.Name));
    }

    /// <summary>
    /// Standby was retired, so by default the loss of action is reported without it.
    /// The status is off, not gone: the deck still documents it, and the switch is what
    /// lets an older recording be read back the way it was refereed.
    /// </summary>
    [Fact]
    public void StandbyIsNotAppliedByDefault()
    {
        var (game, parser) = InOuterPhase();

        var idle = game.Players.Values.First(p =>
            PhaseRules.IsOuterZone(p.Position) && !p.HasBall);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);

        Assert.True(Flagged(game, idle.Name));
        Assert.False(idle.IsStandby);
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("STANDBY"));
    }

    [Fact]
    public void StandbyIsAppliedWhenTheOlderRulesAreSwitchedOn()
    {
        var (game, parser) = InOuterPhase(RuleOptions.AsPublished());

        var idle = game.Players.Values.First(p =>
            PhaseRules.IsOuterZone(p.Position) && !p.HasBall);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);

        Assert.True(idle.IsStandby);
        Assert.Contains(game.PlayByPlay, l => l.Contains("STANDBY") && l.Contains(idle.Name));
    }

    /// <summary>Standby lasts the phase you sat out, not the rest of the match.</summary>
    [Fact]
    public void StandbyLiftsWhenTheNextActingPhaseOpens()
    {
        var (game, parser) = InOuterPhase(RuleOptions.AsPublished());
        var now = DateTime.Now;

        var idle = game.Players.Values.First(p =>
            PhaseRules.IsOuterZone(p.Position) && !p.HasBall);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);
        Assert.True(idle.IsStandby);

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now.AddSeconds(30));
        Assert.False(idle.IsStandby);
    }

    /// <summary>
    /// The generator never lets a phase run out on purpose.
    ///
    /// Losing your action is a real thing that happens to real players, and the tracker
    /// reports it — but it is a mistake rather than a move. A generated match that
    /// plays it as a strategy teaches the wrong thing about the game, and it used to,
    /// because a player with nowhere forward to go had no fallback. Players move freely
    /// along the lanes, so there is always somewhere to go.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    [InlineData(31337)]
    public void GeneratedMatchesNeverSitAPhaseOut(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        var sat = game.PlayByPlay.Where(l => l.Contains("Loss of action")).ToList();

        Assert.True(sat.Count == 0,
            $"Generated match sat players out:{Environment.NewLine}" +
            string.Join(Environment.NewLine, sat.Take(5)));
    }

    /// <summary>
    /// A player with nothing available really can lose their action, so the advisory
    /// firing is not itself a fault. What must never happen is naming someone who was
    /// never obliged to act.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2024)]
    public void OnlyPlayersWhoOwedAnActionAreEverNamed(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        var named = game.PlayByPlay
            .Where(l => l.Contains("Loss of action"))
            .SelectMany(l => game.Players.Keys.Where(name => l.Contains(name)))
            .Distinct()
            .ToList();

        // Keepers hold an inner zone and are only ever obliged during an inner phase,
        // so they should not dominate this list, and nobody unknown may appear.
        foreach (var name in named)
            Assert.Contains(name, game.Players.Keys);
    }
}
