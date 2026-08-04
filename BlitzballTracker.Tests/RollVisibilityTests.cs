using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Blitzball is decided by dice, so the numbers are the record. A log that keeps only
/// outcomes cannot settle a disputed call, which is the one job it has once the match
/// is over.
/// </summary>
public class RollVisibilityTests
{
    private const string Ref = "O'looqa Honji";
    private const string Player = "Soren Kell";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(Fixtures.RealMatchRoster());
        return (game, new ChatParser(game));
    }

    private static void Roll(ChatParser parser, string player, int value, DateTime at)
        => parser.ProcessMessage(Ref, $"Random! {player} rolls a {value} (out of 100).", at);

    private static int RollLines(BlitzGame game)
        => game.PlayByPlay.Count(l => l.Contains(" rolls "));

    [Fact]
    public void TheNumberIsWrittenDown()
    {
        var (game, parser) = NewGame();

        Roll(parser, Player, 78, DateTime.Now);

        Assert.Contains(game.PlayByPlay, l => l.Contains(Player) && l.Contains("rolls 78"));
    }

    /// <summary>
    /// A total on its own cannot answer the question people actually argue about,
    /// which is whether the bonus was counted.
    /// </summary>
    [Fact]
    public void AModifierIsShownAsArithmeticRatherThanFoldedIn()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(Player, $"|| {Player} strikes for goal. [SHOOT] [FWD: +10]", now);
        Roll(parser, Player, 78, now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("78 +10 = 88"));
    }

    /// <summary>
    /// Shown for actions that are actually rolled for. SURVEY is not one — declaring it
    /// arms a lane guard, and the roll-off belongs to Reposition.
    /// </summary>
    [Fact]
    public void TheRollSaysWhatItWasFor()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(Player, $"|| {Player} strikes for goal. [SHOOT]", now);
        Roll(parser, Player, 51, now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("rolls 51") && l.Contains("SHOOT"));
    }

    /// <summary>
    /// The re-roll advisory already names both numbers, so logging the replacement
    /// separately would report one roll twice.
    /// </summary>
    [Fact]
    public void ARerollIsRecordedOnceNotTwice()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        Roll(parser, Player, 20, now);
        Roll(parser, Player, 90, now);

        Assert.Equal(1, RollLines(game));
        Assert.Contains(game.PlayByPlay, l => l.Contains("rolled again") && l.Contains("90"));
    }

    /// <summary>Spectators roll dice in the stands all match. Not our business.</summary>
    [Fact]
    public void SpectatorRollsStayOutOfTheRecord()
    {
        var (game, parser) = NewGame();

        Roll(parser, "Papani Pani", 99, DateTime.Now);

        Assert.Equal(0, RollLines(game));
    }

    /// <summary>
    /// A basic move is not opposed by anything: you declare the waymark and you go.
    /// The tracker used to attribute any stray roll to it, which made the record read
    /// as though moving were a dice roll.
    /// </summary>
    [Fact]
    public void AnUncontestedMoveIsNotRolledFor()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(Player, $"|| {Player} swims for position. [MOVE to C]", now);
        Roll(parser, Player, 51, now);

        var move = game.CurrentPhaseActions.Single(a => a.Action == ActionType.Move);

        Assert.Null(move.Roll);
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("for MOVE"));

        // The roll still happened and is still shown; it just is not claimed by the move.
        Assert.Equal(51, game.Players[Player].PhaseRoll);
        Assert.Contains(game.PlayByPlay, l => l.Contains("rolls 51"));
    }

    /// <summary>
    /// Being blocked is what turns a move into a contest, and a player only has one
    /// roll — so it must not be eaten by a move that never needed it.
    /// </summary>
    [Fact]
    public void ABlockedMoveIsRolledFor()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var mover = game.Players.Values.First(p =>
            p.Role == PlayerRole.Midfield && p.Team.Equals("SIM RED", StringComparison.OrdinalIgnoreCase));
        var blocker = game.Players.Values.First(p =>
            p.Role == PlayerRole.Midfield && p.Team.Equals("SIM GOLD", StringComparison.OrdinalIgnoreCase));

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} gets in the way. [BLOCK -> {mover.Name}]", now);
        Assert.True(mover.IsBlocked);

        parser.ProcessMessage(mover.Name, $"|| {mover.Name} pushes through. [MOVE to C]", now);
        parser.ProcessMessage(Ref, $"Random! {mover.Name} rolls a 66 (out of 100).", now);

        var move = game.CurrentPhaseActions.Single(a => a.Action == ActionType.Move);
        Assert.Equal(66, move.Roll);
    }

    [Fact]
    public void TheLocalPlayersRollIsRecordedToo()
    {
        var (game, parser) = NewGame();
        parser.LocalPlayerName = Player;

        parser.ProcessMessage(Player, "You roll a 64 (out of 100).", DateTime.Now);

        Assert.Equal(64, game.Players[Player].PhaseRoll);
        Assert.Contains(game.PlayByPlay, l => l.Contains("rolls 64"));
    }

    /// <summary>
    /// The local player's rolls used to go down a shortcut path that kept the first
    /// roll and ignored every correction — the same bug already fixed for everyone
    /// else. The local player is the one person certain to be at the match.
    /// </summary>
    [Fact]
    public void TheLocalPlayersRerollSupersedesJustLikeAnyoneElses()
    {
        var (game, parser) = NewGame();
        parser.LocalPlayerName = Player;
        var now = DateTime.Now;

        parser.ProcessMessage(Player, "You roll a 20 (out of 100).", now);
        parser.ProcessMessage(Player, "You roll a 90 (out of 100).", now);

        Assert.Equal(90, game.Players[Player].PhaseRoll);
        Assert.Contains(game.PlayByPlay, l => l.Contains("rolled again"));
    }
}
