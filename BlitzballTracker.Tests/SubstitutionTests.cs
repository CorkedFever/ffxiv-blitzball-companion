using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Teams swap players mid-match, commonly at halftime.
///
/// Re-applying an edited roster is not a substitution: it clears every player and
/// resets positions, which mid-match means losing every stat earned so far and sending
/// both sides back to their kickoff formation. This has to be surgical.
/// </summary>
public class SubstitutionTests
{
    private const string Ref = "Sim Referee";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TheSubstituteTakesTheRoleAndThePlace()
    {
        var (game, _) = NewGame();

        var outgoing = Player(game, "SIM RED", PlayerRole.LeftForward);
        outgoing.Position = Waymark.B;

        Assert.True(game.Substitute(outgoing.Name, "Fresh Legs"));

        var incoming = game.Players["Fresh Legs"];

        Assert.Equal(PlayerRole.LeftForward, incoming.Role);
        Assert.Equal("SIM RED", incoming.Team);
        Assert.Equal(Waymark.B, incoming.Position);
    }

    /// <summary>Goals and tackles belong to whoever made them.</summary>
    [Fact]
    public void StatsStayWithThePlayerWhoEarnedThem()
    {
        var (game, _) = NewGame();

        var outgoing = Player(game, "SIM RED", PlayerRole.LeftForward);
        outgoing.Goals = 2;
        outgoing.Tackles = 3;

        game.Substitute(outgoing.Name, "Fresh Legs");

        Assert.Equal(2, outgoing.Goals);
        Assert.Equal(3, outgoing.Tackles);
        Assert.Equal(0, game.Players["Fresh Legs"].Goals);

        // And the departing player is kept, so the match record still has them.
        Assert.True(game.Players.ContainsKey(outgoing.Name));
        Assert.True(outgoing.IsSubstituted);
        Assert.Equal(Waymark.None, outgoing.Position);
    }

    /// <summary>
    /// The regression that matters. ChatParser rebuilds its name index only when the
    /// roster reference changes, so a substitution that mutated the roster in place
    /// would leave the substitute unrecognised and every action of theirs discarded.
    /// </summary>
    [Fact]
    public void TheSubstituteIsRecognisedInChat()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var outgoing = Player(game, "SIM RED", PlayerRole.LeftForward);
        outgoing.Position = Waymark.B;

        // Get the index built against the original roster first.
        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now);
        parser.ProcessMessage(outgoing.Name, $"|| {outgoing.Name} moves up. [MOVE to C]", now);
        Assert.Single(game.CurrentPhaseActions);

        game.Substitute(outgoing.Name, "Fresh Legs");

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", now.AddSeconds(60));
        parser.ProcessMessage("Fresh Legs", "|| Fresh Legs moves up. [MOVE to C]", now.AddSeconds(60));

        Assert.Single(game.CurrentPhaseActions);
        Assert.Equal("Fresh Legs", game.CurrentPhaseActions[0].PlayerName);
    }

    /// <summary>Possession goes with the shirt, or the ball leaves the match.</summary>
    [Fact]
    public void SubbingTheCarrierHandsOverTheBall()
    {
        var (game, parser) = NewGame();

        var carrier = Player(game, "SIM RED", PlayerRole.Midfield);
        parser.ProcessMessage("Sim Scorekeeper", $"[BALL to {carrier.Name}]", DateTime.Now);
        Assert.Equal(carrier.Name, game.BallCarrier);

        game.Substitute(carrier.Name, "Fresh Legs");

        Assert.Equal("Fresh Legs", game.BallCarrier);
        Assert.True(game.Players["Fresh Legs"].HasBall);
        Assert.False(carrier.HasBall);
    }

    /// <summary>A player who has gone off does not come back on at the next goal.</summary>
    [Fact]
    public void ASubstitutedPlayerStaysOffAtAReset()
    {
        var (game, _) = NewGame();

        var outgoing = Player(game, "SIM RED", PlayerRole.LeftForward);
        game.Substitute(outgoing.Name, "Fresh Legs");

        game.ResetPositions();

        Assert.Equal(Waymark.None, outgoing.Position);
        Assert.NotEqual(Waymark.None, game.Players["Fresh Legs"].Position);
    }

    /// <summary>Blocks the departing player was holding leave with them.</summary>
    [Fact]
    public void BlocksHeldByTheDepartingPlayerAreReleased()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var blocker = Player(game, "SIM GOLD", PlayerRole.LeftDefender);
        var held = Player(game, "SIM RED", PlayerRole.Midfield);

        blocker.Position = held.Position;

        parser.ProcessMessage(blocker.Name, $"|| {blocker.Name} gets in the way. [BLOCK -> {held.Name}]", now);
        Assert.True(held.IsBlocked);

        game.Substitute(blocker.Name, "Fresh Legs");

        Assert.False(held.IsBlocked);
        Assert.DoesNotContain(blocker.Name, game.BlockersOf(held.Name));
    }

    [Fact]
    public void SubstitutingSomebodyAlreadyOnTheRosterIsRefused()
    {
        var (game, _) = NewGame();

        var outgoing = Player(game, "SIM RED", PlayerRole.LeftForward);
        var existing = Player(game, "SIM RED", PlayerRole.Midfield);

        Assert.False(game.Substitute(outgoing.Name, existing.Name));
        Assert.False(outgoing.IsSubstituted);
    }

    [Fact]
    public void SubstitutingAnUnknownPlayerIsRefused()
    {
        var (game, _) = NewGame();

        Assert.False(game.Substitute("Nobody At All", "Fresh Legs"));
        Assert.DoesNotContain("Fresh Legs", game.Players.Keys);
    }

    /// <summary>
    /// The tracked roster follows the field, so it can be saved, re-sent to the live
    /// feed, and written into a recording.
    /// </summary>
    [Fact]
    public void TheRosterFollowsTheSubstitution()
    {
        var (game, _) = NewGame();

        var outgoing = Player(game, "SIM RED", PlayerRole.LeftForward);
        game.Substitute(outgoing.Name, "Fresh Legs");

        var roster = game.CurrentRoster;

        Assert.NotNull(roster);
        Assert.Contains(roster!.Entries, e => e.Name == "Fresh Legs");
        Assert.DoesNotContain(roster.Entries, e => e.Name == outgoing.Name);
        Assert.Equal(12, roster.Entries.Count);
    }
}
