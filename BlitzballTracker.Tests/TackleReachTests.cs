using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Tackling is a forward's ability, and their reach is wider than their stride.
///
/// The sphere lays out as a grid: 1 and 2 across the top, D, C and Four through the
/// middle, A and B along the bottom, with 1 above A and 2 above B. A forward strikes
/// anywhere along their row or down their column, even where no path exists to walk.
/// </summary>
public class TackleReachTests
{
    private static BlitzGame NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return game;
    }

    private static PlayerState WithRole(BlitzGame game, PlayerRole role, string team) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    private static bool CanReach(BlitzGame game, Waymark from, Waymark to)
    {
        var forward = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);
        var target = WithRole(game, PlayerRole.Midfield, game.AwayTeam);

        forward.Position = from;
        target.Position = to;

        return game.CanTackle(forward, target);
    }

    [Theory]
    [InlineData(Waymark.A, Waymark.B)]      // along the bottom row
    [InlineData(Waymark.One, Waymark.Two)]  // along the top row
    [InlineData(Waymark.D, Waymark.C)]      // middle row, one step
    [InlineData(Waymark.C, Waymark.Four)]   // middle row, reaching the far keeper
    public void ForwardsStrikeAlongTheirRow(Waymark from, Waymark to)
        => Assert.True(CanReach(NewGame(), from, to));

    /// <summary>
    /// The middle row runs the length of the pitch, but reach carries one step along
    /// it. Nothing tackles from one goal all the way to the other.
    /// </summary>
    [Fact]
    public void ReachNeverSpansGoalToGoal()
        => Assert.False(CanReach(NewGame(), Waymark.D, Waymark.Four));

    [Theory]
    [InlineData(Waymark.A, Waymark.One)]
    [InlineData(Waymark.B, Waymark.Two)]
    public void ForwardsStrikeDownTheirColumn(Waymark from, Waymark to)
        => Assert.True(CanReach(NewGame(), from, to));

    /// <summary>
    /// Reach works like a queen, so the diagonals count too. On this layout every
    /// movement step runs at an angle, which makes the lanes the diagonals.
    /// </summary>
    [Theory]
    [InlineData(Waymark.A, Waymark.C)]
    [InlineData(Waymark.One, Waymark.C)]
    [InlineData(Waymark.D, Waymark.One)]
    [InlineData(Waymark.D, Waymark.A)]
    [InlineData(Waymark.C, Waymark.B)]
    [InlineData(Waymark.Two, Waymark.Four)]
    public void ForwardsStrikeAlongDiagonals(Waymark from, Waymark to)
        => Assert.True(CanReach(NewGame(), from, to));

    [Theory]
    [InlineData(Waymark.A, Waymark.Two)]    // no shared row, column or diagonal
    [InlineData(Waymark.One, Waymark.B)]
    [InlineData(Waymark.One, Waymark.Four)]
    [InlineData(Waymark.D, Waymark.B)]
    public void ForwardsCannotStrikeOffTheirLines(Waymark from, Waymark to)
        => Assert.False(CanReach(NewGame(), from, to));

    /// <summary>
    /// A tackle is a movement that stuns: the tackler ends up on their target's
    /// waymark. Somebody already stood beside you is blocked, not tackled.
    /// </summary>
    [Theory]
    [InlineData(Waymark.C)]
    [InlineData(Waymark.A)]
    [InlineData(Waymark.Four)]
    public void AForwardCannotTackleWithinTheirOwnZone(Waymark shared)
        => Assert.False(CanReach(NewGame(), shared, shared));

    [Fact]
    public void ASameZoneTackleIsReportedAsOutOfReach()
    {
        var game = NewGame();
        var parser = new ChatParser(game);

        var forward = game.Players.Values.First(p =>
            p.Team == "SIM RED" && p.Role == PlayerRole.LeftForward);

        var target = game.Players.Values.First(p =>
            p.Team == "SIM GOLD" && p.Role == PlayerRole.Midfield);

        game.TryPlace(forward, Waymark.C);
        game.TryPlace(target, Waymark.C);

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} lunges. [TACKLE -> {target.Name}]", DateTime.Now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("lane or their zone"));
    }

    /// <summary>
    /// The reach a forward has is not a path they can walk: the cross-lane pairs are
    /// deliberately absent from the movement connections.
    /// </summary>
    [Fact]
    public void ColumnPairsAreReachableButNotWalkable()
    {
        Assert.True(BlitzsphereLayout.SameColumn(Waymark.A, Waymark.One));

        var connected = BlitzsphereLayout.Lanes.Any(lane =>
            (lane.From == Waymark.A && lane.To == Waymark.One) ||
            (lane.From == Waymark.One && lane.To == Waymark.A));

        Assert.False(connected, "1 and A must not be a movement connection.");
    }

    [Fact]
    public void TacklingBelongsToForwardsAlone()
    {
        var game = NewGame();

        Assert.True(BlitzGame.CanTackle(WithRole(game, PlayerRole.LeftForward, game.HomeTeam)));
        Assert.True(BlitzGame.CanTackle(WithRole(game, PlayerRole.RightForward, game.HomeTeam)));

        Assert.False(BlitzGame.CanTackle(WithRole(game, PlayerRole.Midfield, game.HomeTeam)));
        Assert.False(BlitzGame.CanTackle(WithRole(game, PlayerRole.LeftDefender, game.HomeTeam)));
        Assert.False(BlitzGame.CanTackle(WithRole(game, PlayerRole.Goalkeeper, game.HomeTeam)));
    }

    /// <summary>
    /// The middle row carries both goals, which is exactly how a forward at Centre
    /// gets at a goalkeeper who can never leave their line.
    /// </summary>
    [Fact]
    public void AForwardAtCentreCanReachEitherKeeper()
    {
        var game = NewGame();
        var forward = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);
        forward.Position = Waymark.C;

        var keeper = WithRole(game, PlayerRole.Goalkeeper, game.AwayTeam);

        Assert.Equal(Waymark.Four, keeper.Position);
        Assert.True(game.CanTackle(forward, keeper));
    }

    [Fact]
    public void ANonForwardTackleIsAdvised()
    {
        var game = NewGame();
        var parser = new ChatParser(game);

        var mid = WithRole(game, PlayerRole.Midfield, game.HomeTeam);
        var target = WithRole(game, PlayerRole.Midfield, game.AwayTeam);

        parser.ProcessMessage(mid.Name, $"|| {mid.Name} lunges. [TACKLE -> {target.Name}]", DateTime.Now);

        Assert.Contains(game.PlayByPlay, line => line.Contains("is not a forward"));
    }

    [Fact]
    public void AnOutOfReachTackleIsAdvised()
    {
        var game = NewGame();
        var parser = new ChatParser(game);

        var forward = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);
        var target = WithRole(game, PlayerRole.Midfield, game.AwayTeam);

        forward.Position = Waymark.A;
        target.Position = Waymark.Two;   // neither row nor column

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} lunges. [TACKLE -> {target.Name}]", DateTime.Now);

        Assert.Contains(game.PlayByPlay, line => line.Contains("lane or their zone"));
    }

    /// <summary>
    /// The complete reach map, spelled out so it can be checked by eye rather than
    /// inferred from three separate rules. Reach is wide but not unlimited: it never
    /// crosses from one outer corner to the opposite one.
    ///
    /// Geometry only. Whether a particular player may tackle into a particular goal is
    /// a separate rule, covered by <see cref="AForwardCannotTackleIntoTheirOwnGoal"/> —
    /// keeping them apart is what lets this table stay the same for both sides.
    /// </summary>
    [Theory]
    // From D: one step along the middle row, and diagonally to the near pair.
    [InlineData(Waymark.D, "C,1,A")]
    // From 1: across the top, down its column, diagonally inward.
    [InlineData(Waymark.One, "2,A,D,C")]
    // From A: across the bottom, up its column, diagonally inward.
    [InlineData(Waymark.A, "B,1,D,C")]
    // Centre sees the whole board.
    [InlineData(Waymark.C, "D,4,1,A,2,B")]
    // From 2: mirror of 1.
    [InlineData(Waymark.Two, "1,B,C,4")]
    // From B: mirror of A.
    [InlineData(Waymark.B, "A,2,C,4")]
    // From 4: mirror of D.
    [InlineData(Waymark.Four, "C,2,B")]
    public void ReachMapIsExactlyThis(Waymark from, string expected)
    {
        var game = NewGame();

        var reachable = BlitzsphereLayout.All
            .Where(to => to != from && BlitzsphereLayout.SharesLine(from, to))
            .Select(BlitzsphereLayout.Label)
            .ToHashSet();

        var wanted = expected.Split(',').ToHashSet();

        Assert.True(reachable.SetEquals(wanted),
            $"From {BlitzsphereLayout.Label(from)}: expected [{string.Join(",", wanted.Order())}] " +
            $"but reach is [{string.Join(",", reachable.Order())}].");
    }

    /// <summary>
    /// A tackle is a movement that ends with the tackler standing in the waymark they
    /// declared, so the goal restrictions bind it exactly as they bind a move.
    ///
    /// A forward cannot declare a tackle into their own goal — geometry says they can
    /// reach it, and the rules say they may not stand there.
    /// </summary>
    [Fact]
    public void AForwardCannotTackleIntoTheirOwnGoal()
    {
        var game = NewGame();

        var forward = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);
        var enemy = WithRole(game, PlayerRole.Midfield, game.AwayTeam);

        var ownGoal = game.OwnGoal(forward);

        forward.Position = Waymark.C;
        enemy.Position = ownGoal;

        Assert.True(BlitzsphereLayout.SharesLine(Waymark.C, ownGoal),
            "Centre reaches both goals; the refusal must come from the role, not the geometry.");

        Assert.False(game.CanTackle(forward, enemy));
    }

    /// <summary>The goal they are attacking is the one they are meant to be tackling into.</summary>
    [Fact]
    public void AForwardCanStillTackleIntoTheEnemyGoal()
    {
        var game = NewGame();

        var forward = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);
        var enemy = WithRole(game, PlayerRole.Midfield, game.AwayTeam);

        forward.Position = Waymark.C;
        enemy.Position = game.AttackingGoal(forward);

        Assert.True(game.CanTackle(forward, enemy));
    }

    /// <summary>
    /// An illegal tackle must not take effect: no daze on the target, and Reposition
    /// must not carry the tackler into a goal they may not stand in.
    /// </summary>
    [Fact]
    public void ATackleIntoYourOwnGoalIsRefusedOutright()
    {
        var game = NewGame();
        var parser = new ChatParser(game);
        var now = DateTime.Now;

        var forward = WithRole(game, PlayerRole.LeftForward, game.HomeTeam);
        var enemy = WithRole(game, PlayerRole.Midfield, game.AwayTeam);

        var ownGoal = game.OwnGoal(forward);

        forward.Position = Waymark.C;
        enemy.Position = ownGoal;

        parser.ProcessMessage("Sim Referee","<< INNER PHASE (4/C/D) >> Start!", now);
        parser.ProcessMessage(forward.Name, $"|| {forward.Name} crashes in. [TACKLE -> {enemy.Name}]", now);
        parser.ProcessMessage("Sim Referee",$"Random! {forward.Name} rolls a 95 (out of 100).", now);
        parser.ProcessMessage("Sim Referee",$"Random! {enemy.Name} rolls a 5 (out of 100).", now);
        parser.ProcessMessage("Sim Referee","<< REPOSITION >>", now);

        Assert.Contains(game.PlayByPlay, l => l.Contains($"cannot enter {ownGoal}"));
        Assert.False(enemy.IsDazed);
        Assert.Equal(Waymark.C, forward.Position);
    }

    /// <summary>Generated matches must not produce tackles from roles that cannot tackle.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2024)]
    public void GeneratedMatchesOnlyTackleWithForwards(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var forwards = roster.Entries
            .Where(e => e.Role is PlayerRole.LeftForward or PlayerRole.RightForward)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var line in new MatchSimulator(roster, seed).Generate())
        {
            if (!line.Message.Contains("[TACKLE", StringComparison.OrdinalIgnoreCase)) continue;

            Assert.True(forwards.Contains(line.Sender),
                $"{line.Sender} declared a tackle but is not a forward.");
        }
    }
}
