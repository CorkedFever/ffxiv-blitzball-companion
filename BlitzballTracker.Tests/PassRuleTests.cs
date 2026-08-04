using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Passing is spread across three slides that interact: 41 for distance, 42 for the
/// goalkeeper's shorter reach, 43 for the back pass. A pass has to be judged against
/// all three at once, which is why they are answered in one place.
/// </summary>
public class PassRuleTests
{
    private static BlitzGame NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        game.Round = 3;
        return game;
    }

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    /// <summary>Park everyone somewhere harmless so a test controls only what it cares about.</summary>
    private static void ClearField(BlitzGame game, params PlayerState[] keep)
    {
        foreach (var player in game.Players.Values)
        {
            if (keep.Contains(player) || player.IsGoalkeeper) continue;
            player.Position = Waymark.None;
        }
    }

    [Fact]
    public void OneToThreeZonesAheadCarriesAutomatically()
    {
        var game = NewGame();

        var passer = Player(game, "SIM GOLD", PlayerRole.Midfield);
        var receiver = Player(game, "SIM GOLD", PlayerRole.LeftForward);

        var verdict = game.AssessPass(passer, passer.Position, receiver);

        Assert.Equal(PassKind.Automatic, verdict.Kind);
    }

    /// <summary>The two lanes sit at the same rank, so crossing is not advancing — or retreating.</summary>
    [Fact]
    public void CrossingBetweenLanesIsNotABackPass()
    {
        var game = NewGame();

        var passer = Player(game, "SIM GOLD", PlayerRole.LeftDefender);
        var receiver = Player(game, "SIM GOLD", PlayerRole.RightDefender);

        passer.Position = Waymark.A;
        receiver.Position = Waymark.One;

        var verdict = game.AssessPass(passer, passer.Position, receiver);

        Assert.Equal(PassKind.Automatic, verdict.Kind);
        Assert.Equal(0, verdict.ZonesAhead);
    }

    [Fact]
    public void GoalToGoalIsContestedByTheKeeper()
    {
        var game = NewGame();

        var passer = Player(game, "SIM GOLD", PlayerRole.Midfield);
        var receiver = Player(game, "SIM GOLD", PlayerRole.LeftForward);

        passer.Position = game.OwnGoal(passer);
        receiver.Position = game.AttackingGoal(passer);

        var verdict = game.AssessPass(passer, passer.Position, receiver);

        Assert.Equal(PassKind.ContestedByKeeper, verdict.Kind);
        Assert.Equal(4, verdict.ZonesAhead);
    }

    [Fact]
    public void NobodyMayPassToAGoalkeeper()
    {
        var game = NewGame();

        var passer = Player(game, "SIM GOLD", PlayerRole.Midfield);
        var keeper = Player(game, "SIM GOLD", PlayerRole.Goalkeeper);

        var verdict = game.AssessPass(passer, passer.Position, keeper);

        Assert.Equal(PassKind.KeeperCannotReceive, verdict.Kind);
    }

    // --- The goalkeeper's reach (slide 42) ---

    [Fact]
    public void AKeeperMayThrowTwoZones()
    {
        var game = NewGame();

        var keeper = Player(game, "SIM GOLD", PlayerRole.Goalkeeper);
        var mate = Player(game, "SIM GOLD", PlayerRole.Midfield);

        mate.Position = Waymark.C;

        var verdict = game.AssessPass(keeper, keeper.Position, mate);

        Assert.Equal(PassKind.Automatic, verdict.Kind);
    }

    /// <summary>
    /// The long throw is permitted when nobody is closer — and it still comes loose.
    ///
    /// That is what reconciles slide 42 with itself: it allows three zones when there is
    /// no shorter option, then says a keeper passing more than two causes a fumble. Both
    /// hold. The exception is not a safe long throw, it is permission to put the ball
    /// into a contest rather than have no option at all.
    /// </summary>
    [Fact]
    public void AForcedLongThrowIsLegalButComesLoose()
    {
        var game = NewGame();

        var keeper = Player(game, "SIM GOLD", PlayerRole.Goalkeeper);
        var distant = Player(game, "SIM GOLD", PlayerRole.LeftForward);

        ClearField(game, distant);

        // Three zones out, with the rest of the squad off the field entirely.
        distant.Position = game.OwnGoal(keeper) == Waymark.D ? Waymark.Two : Waymark.One;

        Assert.Equal(3, Math.Abs(game.ZonesAhead(keeper, keeper.Position, distant.Position)));

        var forced = game.AssessPass(keeper, keeper.Position, distant);

        Assert.Equal(PassKind.ForcedLong, forced.Kind);
        Assert.True(forced.IsLegal, "The keeper had no shorter option, so this is allowed.");
        Assert.False(forced.Arrives, "It still comes loose in the receiving zone.");
    }

    [Fact]
    public void TheLongThrowIsNotTheirsToMakeWithSomebodyCloser()
    {
        var game = NewGame();

        var keeper = Player(game, "SIM GOLD", PlayerRole.Goalkeeper);
        var distant = Player(game, "SIM GOLD", PlayerRole.LeftForward);

        ClearField(game, distant);
        distant.Position = game.OwnGoal(keeper) == Waymark.D ? Waymark.Two : Waymark.One;

        var near = Player(game, "SIM GOLD", PlayerRole.Midfield);
        near.Position = Waymark.C;

        var verdict = game.AssessPass(keeper, keeper.Position, distant);

        Assert.Equal(PassKind.Overreach, verdict.Kind);
        Assert.False(verdict.IsLegal);
    }

    // --- Back pass (slide 43) ---

    [Fact]
    public void ABackPassNeedsTheFieldAheadToBeShut()
    {
        var game = NewGame();

        var passer = Player(game, "SIM GOLD", PlayerRole.LeftForward);
        var behind = Player(game, "SIM GOLD", PlayerRole.Midfield);
        var ahead = Player(game, "SIM GOLD", PlayerRole.RightForward);

        ClearField(game, passer, behind, ahead);

        passer.Position = Waymark.C;
        behind.Position = game.OwnGoal(passer) == Waymark.D ? Waymark.A : Waymark.B;
        ahead.Position = game.OwnGoal(passer) == Waymark.D ? Waymark.B : Waymark.A;

        // An open team-mate ahead means the ball has no business going backwards.
        Assert.Equal(PassKind.IllegalBackPass, game.AssessPass(passer, passer.Position, behind).Kind);

        // Block them and the option opens up.
        ahead.IsBlocked = true;
        Assert.Equal(PassKind.BackPass, game.AssessPass(passer, passer.Position, behind).Kind);
    }

    [Fact]
    public void ABackPassReachesOneZoneBehindAtMost()
    {
        var game = NewGame();

        var passer = Player(game, "SIM GOLD", PlayerRole.LeftForward);
        var deep = Player(game, "SIM GOLD", PlayerRole.LeftDefender);

        ClearField(game, passer, deep);

        passer.Position = game.OwnGoal(passer) == Waymark.D ? Waymark.B : Waymark.A;
        deep.Position = game.OwnGoal(passer) == Waymark.D ? Waymark.A : Waymark.B;

        var verdict = game.AssessPass(passer, passer.Position, deep);

        Assert.Equal(PassKind.IllegalBackPass, verdict.Kind);
        Assert.Contains("one zone behind", verdict.Reason);
    }

    [Fact]
    public void UsingABackPassLocksTheTeamOutNextRound()
    {
        var game = NewGame();

        var passer = Player(game, "SIM GOLD", PlayerRole.LeftForward);
        var behind = Player(game, "SIM GOLD", PlayerRole.Midfield);

        ClearField(game, passer, behind);

        passer.Position = Waymark.C;
        behind.Position = game.OwnGoal(passer) == Waymark.D ? Waymark.A : Waymark.B;

        Assert.Equal(PassKind.BackPass, game.AssessPass(passer, passer.Position, behind).Kind);

        game.RecordBackPass(passer);

        // Same round and the next are shut; the round after is clear again.
        Assert.Equal(PassKind.IllegalBackPass, game.AssessPass(passer, passer.Position, behind).Kind);

        game.Round += 1;
        Assert.Equal(PassKind.IllegalBackPass, game.AssessPass(passer, passer.Position, behind).Kind);

        game.Round += 1;
        Assert.Equal(PassKind.BackPass, game.AssessPass(passer, passer.Position, behind).Kind);
    }

    [Fact]
    public void NoBackPassFromInsideTheEnemyGoal()
    {
        var game = NewGame();

        var passer = Player(game, "SIM GOLD", PlayerRole.LeftForward);
        var behind = Player(game, "SIM GOLD", PlayerRole.Midfield);

        ClearField(game, passer, behind);

        passer.Position = game.AttackingGoal(passer);
        behind.Position = game.OwnGoal(passer) == Waymark.D ? Waymark.B : Waymark.A;

        var verdict = game.AssessPass(passer, passer.Position, behind);

        Assert.Equal(PassKind.IllegalBackPass, verdict.Kind);
        Assert.Contains("enemy goal", verdict.Reason);
    }

    /// <summary>Keepers have no Move, Survey or Block action (slide 62).</summary>
    [Fact]
    public void AGoalkeeperHasNoSurveyAction()
    {
        var game = NewGame();
        var parser = new ChatParser(game);

        var keeper = Player(game, "SIM GOLD", PlayerRole.Goalkeeper);

        parser.ProcessMessage(keeper.Name, $"|| {keeper.Name} watches the lane. [SURVEY]", DateTime.Now);

        Assert.False(keeper.IsSurveying);
        Assert.Contains(game.PlayByPlay, l => l.Contains("no SURVEY action"));
    }
}
