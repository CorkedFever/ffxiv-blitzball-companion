using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Level rolls are settled by a reroll between the two players involved, called at the
/// end of the phase.
///
/// The reroll is private to the pair. It never touches anything else that roll decided,
/// and it does not replace the phase roll — which is still holding up every other
/// comparison it was part of.
/// </summary>
public class TieBreakTests
{
    private const string Ref = "Sim Referee";

    private static (BlitzGame Game, ChatParser Parser, PlayerState Forward, PlayerState Target) Tied()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var forward = game.Players.Values.First(p =>
            p.Role == PlayerRole.LeftForward && p.Team.Equals("SIM RED", StringComparison.OrdinalIgnoreCase));
        var target = game.Players.Values.First(p =>
            p.Role == PlayerRole.Midfield && p.Team.Equals("SIM GOLD", StringComparison.OrdinalIgnoreCase));

        forward.Position = Waymark.B;
        target.Position = Waymark.Two;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(forward.Name, $"|| {forward.Name} crashes in. [TACKLE -> {target.Name}]", now);
        parser.ProcessMessage(Ref, $"Random! {forward.Name} rolls a 50 (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {target.Name} rolls a 50 (out of 100).", now);

        return (game, parser, forward, target);
    }

    private static void Reroll(ChatParser parser, string player, int value)
        => parser.ProcessMessage(Ref, $"Random! {player} rolls a {value} (out of 100).", DateTime.Now);

    [Fact]
    public void ATieIsNotCalledUntilThePhaseEnds()
    {
        var (game, parser, _, _) = Tied();

        // Mid-phase the tie is only marked; the referee waits for the phase to finish.
        Assert.Empty(game.TieBreaks);
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("reroll to settle"));

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", DateTime.Now);

        Assert.Single(game.TieBreaks);
        Assert.Contains(game.PlayByPlay, l => l.Contains("Tied at 50") && l.Contains("reroll"));
    }

    [Fact]
    public void TheRerollSettlesTheAction()
    {
        var (game, parser, forward, target) = Tied();

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", DateTime.Now);

        Reroll(parser, forward.Name, 80);
        Reroll(parser, target.Name, 20);

        Assert.Empty(game.TieBreaks);
        Assert.True(target.IsDazed, "The tackle should have landed once the reroll was won.");
    }

    [Fact]
    public void LosingTheRerollStopsTheAction()
    {
        var (game, parser, forward, target) = Tied();

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", DateTime.Now);

        Reroll(parser, forward.Name, 10);
        Reroll(parser, target.Name, 90);

        Assert.Empty(game.TieBreaks);
        Assert.False(target.IsDazed);
    }

    /// <summary>
    /// The reroll settles only the pair it belongs to.
    ///
    /// The referee calls it after the phase has closed, by which point the phase rolls
    /// are already cleared — so what this guards is the other direction: the reroll must
    /// not be taken as a roll for the phase that has just begun, where it would bind to
    /// a fresh action or read as a re-roll of one.
    /// </summary>
    [Fact]
    public void ARerollIsNotTakenAsAPhaseRoll()
    {
        var (game, parser, forward, target) = Tied();

        Assert.Equal(50, forward.PhaseRoll);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", DateTime.Now);

        Reroll(parser, forward.Name, 3);
        Reroll(parser, target.Name, 99);

        Assert.Null(forward.PhaseRoll);
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("rolled again"));
    }

    /// <summary>Tie again and it goes again, up to three attempts.</summary>
    [Fact]
    public void TyingAgainCallsAnotherReroll()
    {
        var (game, parser, forward, target) = Tied();

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", DateTime.Now);

        Reroll(parser, forward.Name, 40);
        Reroll(parser, target.Name, 40);

        Assert.Single(game.TieBreaks);
        Assert.Equal(2, game.TieBreaks[0].Attempt);
        Assert.Contains(game.PlayByPlay, l => l.Contains("Tied again"));

        // The slate is wiped, so both owe a fresh roll.
        Assert.False(game.TieBreaks[0].HasRolled(forward.Name));
        Assert.False(game.TieBreaks[0].HasRolled(target.Name));
    }

    /// <summary>After the third reroll the tie goes to the defending player (slide 32).</summary>
    [Fact]
    public void ThreeTiesHandItToTheDefender()
    {
        var (game, parser, forward, target) = Tied();

        parser.ProcessMessage(Ref, "<< REPOSITION >>", DateTime.Now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", DateTime.Now);

        for (var attempt = 0; attempt < TieBreak.MaxRerolls; attempt++)
        {
            Reroll(parser, forward.Name, 40);
            Reroll(parser, target.Name, 40);
        }

        Assert.Empty(game.TieBreaks);
        Assert.False(target.IsDazed, "The defending player takes the tie, so the tackle fails.");
        Assert.Contains(game.PlayByPlay, l => l.Contains("Rerolls exhausted"));
    }

    /// <summary>
    /// A tie-break left open would swallow both players' rolls in the next phase, so it
    /// gets one phase and then falls to the defender.
    /// </summary>
    [Fact]
    public void AnUnrolledTieBreakDoesNotOutliveThePhase()
    {
        var (game, parser, forward, target) = Tied();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);
        Assert.Single(game.TieBreaks);

        // It is given a phase to be settled in, and closed at the boundary after that.
        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now.AddSeconds(30));
        Assert.Single(game.TieBreaks);

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now.AddSeconds(120));

        Assert.Empty(game.TieBreaks);
        Assert.Contains(game.PlayByPlay, l => l.Contains("No reroll came"));

        // And the next roll reaches the phase roll rather than being eaten.
        parser.ProcessMessage(Ref, $"Random! {forward.Name} rolls a 77 (out of 100).", now.AddSeconds(125));

        Assert.Equal(77, forward.PhaseRoll);
        Assert.False(target.IsDazed);
    }
}
