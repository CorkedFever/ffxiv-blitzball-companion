using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Each role covers its own ground. Keepers never leave their goal, forwards never
/// drop into the goal they defend, and defenders never push into the goal they are
/// attacking.
/// </summary>
public class RoleZoneTests
{
    private static BlitzGame NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return game;
    }

    private static PlayerState Home(BlitzGame game, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(game.HomeTeam, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ForwardsStayOutOfTheGoalTheyDefend()
    {
        var game = NewGame();
        var forward = Home(game, PlayerRole.LeftForward);

        // Home defends D in Set 1 and attacks Four.
        Assert.False(game.CanOccupy(forward, Waymark.D));
        Assert.True(game.CanOccupy(forward, Waymark.Four));
        Assert.True(game.CanOccupy(forward, Waymark.C));
    }

    [Fact]
    public void DefendersStayOutOfTheGoalTheyAttack()
    {
        var game = NewGame();
        var defender = Home(game, PlayerRole.LeftDefender);

        Assert.False(game.CanOccupy(defender, Waymark.Four));
        Assert.True(game.CanOccupy(defender, Waymark.D));
        Assert.True(game.CanOccupy(defender, Waymark.C));
    }

    [Fact]
    public void MidfieldersCoverTheWholeField()
    {
        var game = NewGame();
        var mid = Home(game, PlayerRole.Midfield);

        foreach (var zone in BlitzsphereLayout.All)
            Assert.True(game.CanOccupy(mid, zone), $"Midfielder was refused {zone}.");
    }

    [Fact]
    public void RestrictionsFollowTheTeamsWhenTheySwapEnds()
    {
        var game = NewGame();
        var forward = Home(game, PlayerRole.LeftForward);

        Assert.False(game.CanOccupy(forward, Waymark.D));

        game.SwitchSides();

        // Now defending Four, so that is the goal they stay out of.
        Assert.True(game.CanOccupy(forward, Waymark.D));
        Assert.False(game.CanOccupy(forward, Waymark.Four));
    }

    [Fact]
    public void AParsedMoveIntoForbiddenGroundIsRefused()
    {
        var game = NewGame();
        var parser = new ChatParser(game);

        var forward = Home(game, PlayerRole.LeftForward);
        var before = forward.Position;

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} drops back. [MOVE to D]", DateTime.Now);

        Assert.Equal(before, forward.Position);
        Assert.Contains(game.PlayByPlay, line => line.Contains("cannot enter"));
    }

    /// <summary>
    /// Whole generated matches must never place anyone on ground their role does not
    /// cover, however the play unfolds.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    [InlineData(31337)]
    [InlineData(8675309)]
    public void NobodyEverStandsWhereTheirRoleCannotGo(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        foreach (var player in game.Players.Values)
        {
            Assert.True(game.CanOccupy(player, player.Position),
                $"{player.Name} ({player.Role}) ended on {player.Position}, which their role cannot occupy.");
        }
    }
}
