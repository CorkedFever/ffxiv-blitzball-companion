using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A dazed outfielder cannot hold onto a pass. The ball comes loose and everyone
/// standing in the zone rolls for it — flat rolls, made even by players who already
/// rolled this phase for something else.
/// </summary>
public class FumbleTests
{
    private const string Ref = "Sim Referee";
    private const string Scorer = "Sim Scorekeeper";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    private static void Roll(ChatParser parser, string player, int value, DateTime at)
        => parser.ProcessMessage(Ref, $"Random! {player} rolls a {value} (out of 100).", at);

    /// <summary>Two players alone in one zone, one of them dazed and about to be passed to.</summary>
    private static (BlitzGame Game, ChatParser Parser, PlayerState Dazed, PlayerState Rival) Loose()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var dazed = Player(game, "SIM GOLD", PlayerRole.LeftForward);
        var rival = Player(game, "SIM RED", PlayerRole.LeftDefender);
        var passer = Player(game, "SIM GOLD", PlayerRole.Midfield);

        // Clear the zone of everyone else so the contenders are known exactly.
        foreach (var p in game.Players.Values)
        {
            if (p == dazed || p == rival || p == passer || p.IsGoalkeeper) continue;
            p.Position = Waymark.None;
        }

        dazed.Position = Waymark.B;
        rival.Position = Waymark.B;
        passer.Position = Waymark.C;

        parser.ProcessMessage(Scorer, $"[BALL to {passer.Name}]", now);
        parser.ProcessMessage(Scorer, $"[[ DAZED - {dazed.Name} ]]", now);
        Assert.True(dazed.IsDazed);

        parser.ProcessMessage(Scorer, $"[[PASS COMPLETE to {dazed.Name} ]]", now);

        return (game, parser, dazed, rival);
    }

    [Fact]
    public void ADazedReceiverShakesTheBallLoose()
    {
        var (game, _, dazed, rival) = Loose();

        Assert.NotNull(game.Fumble);
        Assert.Contains(game.PlayByPlay, l => l.Contains("FUMBLE"));

        // Everyone in the zone contests it, including the player who dropped it.
        Assert.True(game.Fumble!.IsContender(dazed.Name));
        Assert.True(game.Fumble.IsContender(rival.Name));
    }

    [Fact]
    public void TheHighestRollInTheZoneComesUpWithIt()
    {
        var (game, parser, dazed, rival) = Loose();
        var now = DateTime.Now;

        Roll(parser, dazed.Name, 30, now);
        Roll(parser, rival.Name, 80, now);

        Assert.Null(game.Fumble);
        Assert.Equal(rival.Name, game.BallCarrier);
        Assert.Contains(game.PlayByPlay, l => l.Contains(rival.Name) && l.Contains("comes up with it"));
    }

    /// <summary>
    /// A dazed player can win their own fumble, and doing so does not set off a second
    /// one (slide 50).
    /// </summary>
    [Fact]
    public void TheDazedPlayerCanRecoverTheirOwnFumble()
    {
        var (game, parser, dazed, rival) = Loose();
        var now = DateTime.Now;

        Roll(parser, dazed.Name, 90, now);
        Roll(parser, rival.Name, 20, now);

        Assert.Equal(dazed.Name, game.BallCarrier);
        Assert.Null(game.Fumble);
        Assert.Equal(1, game.PlayByPlay.Count(l => l.Contains("FUMBLE")));
    }

    /// <summary>
    /// Fumble rolls are flat and separate. Taking them as phase rolls would overwrite a
    /// roll that is still deciding somebody's action.
    /// </summary>
    [Fact]
    public void AFumbleRollDoesNotDisturbThePhaseRoll()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var dazed = Player(game, "SIM GOLD", PlayerRole.LeftForward);
        var rival = Player(game, "SIM RED", PlayerRole.LeftDefender);
        var passer = Player(game, "SIM GOLD", PlayerRole.Midfield);

        foreach (var p in game.Players.Values)
        {
            if (p == dazed || p == rival || p == passer || p.IsGoalkeeper) continue;
            p.Position = Waymark.None;
        }

        dazed.Position = Waymark.B;
        rival.Position = Waymark.B;
        passer.Position = Waymark.C;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(rival.Name, $"|| {rival.Name} watches the lane. [SURVEY]", now);
        Roll(parser, rival.Name, 55, now);

        Assert.Equal(55, rival.PhaseRoll);

        parser.ProcessMessage(Scorer, $"[BALL to {passer.Name}]", now);
        parser.ProcessMessage(Scorer, $"[[ DAZED - {dazed.Name} ]]", now);
        parser.ProcessMessage(Scorer, $"[[PASS COMPLETE to {dazed.Name} ]]", now);

        // A second roll from a player who already rolled: it belongs to the loose ball,
        // and their phase roll must survive it untouched.
        Roll(parser, rival.Name, 12, now);

        Assert.Equal(55, rival.PhaseRoll);
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("rolled again"));
    }

    /// <summary>
    /// An open contest silently swallows every later roll from the players in it, so it
    /// is always closed at the phase boundary.
    /// </summary>
    [Fact]
    public void AnUnfinishedContestIsSettledWhenThePhaseEnds()
    {
        var (game, parser, _, rival) = Loose();
        var now = DateTime.Now;

        Roll(parser, rival.Name, 44, now);
        Assert.NotNull(game.Fumble);

        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", now.AddSeconds(30));

        Assert.Null(game.Fumble);
        Assert.Equal(rival.Name, game.BallCarrier);
        Assert.Contains(game.PlayByPlay, l => l.Contains("settled on the rolls given"));
    }

    /// <summary>Ties go to the defending player: the one the ball was meant for (slide 32).</summary>
    [Fact]
    public void ATieGoesToTheIntendedReceiver()
    {
        var (game, parser, dazed, rival) = Loose();
        var now = DateTime.Now;

        Roll(parser, rival.Name, 50, now);
        Roll(parser, dazed.Name, 50, now);

        Assert.Equal(dazed.Name, game.BallCarrier);
    }
}
