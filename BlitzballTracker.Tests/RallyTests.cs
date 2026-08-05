using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// RALLY is the midfielder's specialty: they name a team-mate in their own zone and
/// lend them their roll, if it beats the one that team-mate made. It lasts the phase
/// and no longer (slide 56).
/// </summary>
public class RallyTests
{
    private const string Ref = "Sim Referee";

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    private static void Roll(ChatParser parser, string player, int value)
        => parser.ProcessMessage(Ref, $"Random! {player} rolls a {value} (out of 100).", DateTime.Now);

    /// <summary>A midfielder and a team-mate sharing a zone, mid-phase.</summary>
    private static (BlitzGame Game, ChatParser Parser, PlayerState Mid, PlayerState Mate) InZone()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);

        var mid = Player(game, "SIM RED", PlayerRole.Midfield);
        var mate = Player(game, "SIM RED", PlayerRole.LeftForward);

        // A and 1 are one zone across two markers — the deck's own example.
        mid.Position = Waymark.One;
        mate.Position = Waymark.A;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);

        return (game, parser, mid, mate);
    }

    [Fact]
    public void ABetterRollIsLentToTheNamedTeamMate()
    {
        var (game, parser, mid, mate) = InZone();

        parser.ProcessMessage(mid.Name, $"|| {mid.Name} urges them on. [RALLY -> {mate.Name}]", DateTime.Now);

        Roll(parser, mid.Name, 85);
        Roll(parser, mate.Name, 30);

        Assert.Equal(85, mate.RalliedRoll);
        Assert.Equal(30, mate.PhaseRoll);   // their own roll is not overwritten
        Assert.Contains(game.PlayByPlay, l => l.Contains("rallies") && l.Contains("in place of"));
    }

    [Fact]
    public void AWorseRollLendsNothing()
    {
        var (game, parser, mid, mate) = InZone();

        parser.ProcessMessage(mid.Name, $"|| {mid.Name} urges them on. [RALLY -> {mate.Name}]", DateTime.Now);

        Roll(parser, mid.Name, 20);
        Roll(parser, mate.Name, 70);

        Assert.Null(mate.RalliedRoll);
        Assert.Contains(game.PlayByPlay, l => l.Contains("nothing changes"));
    }

    /// <summary>The lent roll is the one that settles their contests.</summary>
    [Fact]
    public void TheLentRollDecidesTheirContests()
    {
        var (game, parser, mid, mate) = InZone();
        var now = DateTime.Now;

        var enemy = Player(game, "SIM GOLD", PlayerRole.Midfield);
        enemy.Position = Waymark.A;

        // The forward tackles, and would lose on their own roll.
        parser.ProcessMessage(mate.Name, $"|| {mate.Name} crashes in. [TACKLE -> {enemy.Name}]", now);
        parser.ProcessMessage(mid.Name, $"|| {mid.Name} urges them on. [RALLY -> {mate.Name}]", now);

        Roll(parser, mate.Name, 20);
        Roll(parser, enemy.Name, 60);
        Assert.False(enemy.IsDazed, "On their own roll the tackle fails.");

        // The rally lands and the tackle is re-decided on the lent roll.
        Roll(parser, mid.Name, 90);

        Assert.Equal(90, mate.RalliedRoll);
        Assert.True(enemy.IsDazed, "The lent roll should carry the tackle.");
    }

    /// <summary>It is only good for the phase it was rolled in.</summary>
    [Fact]
    public void TheLentRollExpiresWithThePhase()
    {
        var (game, parser, mid, mate) = InZone();
        var now = DateTime.Now;

        parser.ProcessMessage(mid.Name, $"|| {mid.Name} urges them on. [RALLY -> {mate.Name}]", now);
        Roll(parser, mid.Name, 85);
        Roll(parser, mate.Name, 30);
        Assert.Equal(85, mate.RalliedRoll);

        parser.ProcessMessage(Ref, "<< REPOSITION >>", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);

        Assert.Null(mate.RalliedRoll);
    }

    /// <summary>
    /// With nobody in reach there is nothing to lend to, so the action becomes a
    /// SURVEY rather than being lost (slide 56).
    /// </summary>
    [Fact]
    public void ARallyWithNobodyInReachBecomesASurvey()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);

        var mid = Player(game, "SIM RED", PlayerRole.Midfield);
        mid.Position = Waymark.C;

        // Clear the zone of team-mates.
        foreach (var p in game.Players.Values)
        {
            if (p == mid) continue;
            if (p.Team.Equals(mid.Team, StringComparison.OrdinalIgnoreCase))
                p.Position = Waymark.None;
        }

        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", DateTime.Now);
        parser.ProcessMessage(mid.Name, $"|| {mid.Name} calls out. [RALLY]", DateTime.Now);

        Assert.True(mid.IsSurveying);
        Assert.Contains(game.PlayByPlay, l => l.Contains("becomes a SURVEY"));
    }
}
