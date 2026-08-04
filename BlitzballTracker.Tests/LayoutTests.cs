using BlitzballTracker.Core.GameState;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// The layout is shared by the Blazor view and the in-game widget, so it has to stay
/// internally consistent or the two drift apart.
/// </summary>
public class BlitzsphereLayoutTests
{
    [Fact]
    public void EveryPlayableWaymarkHasANode()
    {
        // Waymark 3 is the one FFXIV marker blitzball does not use.
        var playable = Enum.GetValues<Waymark>().Where(w => w != Waymark.None);

        foreach (var waymark in playable)
            Assert.True(BlitzsphereLayout.Nodes.ContainsKey(waymark), $"{waymark} has no node.");
    }

    [Fact]
    public void EveryLaneConnectsTwoRealNodes()
    {
        foreach (var (from, to) in BlitzsphereLayout.Lanes)
        {
            Assert.True(BlitzsphereLayout.Nodes.ContainsKey(from), $"Lane starts at unknown node {from}.");
            Assert.True(BlitzsphereLayout.Nodes.ContainsKey(to), $"Lane ends at unknown node {to}.");
            Assert.NotEqual(from, to);
        }
    }

    [Fact]
    public void AllListMatchesTheNodeSet()
    {
        Assert.Equal(BlitzsphereLayout.Nodes.Count, BlitzsphereLayout.All.Count);
        Assert.Equal(BlitzsphereLayout.All.Count, BlitzsphereLayout.All.Distinct().Count());

        foreach (var waymark in BlitzsphereLayout.All)
            Assert.True(BlitzsphereLayout.Nodes.ContainsKey(waymark));
    }

    [Fact]
    public void NodesSitInsideTheDesignViewport()
    {
        foreach (var (waymark, point) in BlitzsphereLayout.Nodes)
        {
            Assert.InRange(point.X, 0f, BlitzsphereLayout.ViewWidth);
            Assert.InRange(point.Y, 0f, BlitzsphereLayout.ViewHeight);
        }
    }

    [Fact]
    public void GoalsSitAtOppositeEnds()
    {
        var d = BlitzsphereLayout.Nodes[Waymark.D];
        var four = BlitzsphereLayout.Nodes[Waymark.Four];

        // The whole team/role reading depends on D and Four being far apart along
        // one axis, since that axis separates the two sides.
        Assert.True(MathF.Abs(four.X - d.X) > BlitzsphereLayout.ViewWidth * 0.5f);

        Assert.True(BlitzsphereLayout.IsGoal(Waymark.D));
        Assert.True(BlitzsphereLayout.IsGoal(Waymark.Four));
        Assert.False(BlitzsphereLayout.IsGoal(Waymark.C));
    }

    /// <summary>
    /// The drawing must match how the waymarks actually sit in the arena: letter
    /// lane along the top, number lane along the bottom. Getting this inverted made
    /// the panel a mirror image of the field in front of you.
    /// </summary>
    [Fact]
    public void LetterLaneIsDrawnAboveTheNumberLane()
    {
        var a = BlitzsphereLayout.Nodes[Waymark.A];
        var b = BlitzsphereLayout.Nodes[Waymark.B];
        var one = BlitzsphereLayout.Nodes[Waymark.One];
        var two = BlitzsphereLayout.Nodes[Waymark.Two];

        Assert.True(a.Y < one.Y, "A should sit above 1.");
        Assert.True(b.Y < two.Y, "B should sit above 2.");

        Assert.Equal(a.Y, b.Y);
        Assert.Equal(one.Y, two.Y);
    }

    /// <summary>
    /// The cross-lane pairs stack vertically, which is what makes them a column for
    /// tackle reach even though nobody can walk between them.
    /// </summary>
    [Fact]
    public void CrossLanePairsShareAColumn()
    {
        Assert.Equal(BlitzsphereLayout.Nodes[Waymark.A].X, BlitzsphereLayout.Nodes[Waymark.One].X);
        Assert.Equal(BlitzsphereLayout.Nodes[Waymark.B].X, BlitzsphereLayout.Nodes[Waymark.Two].X);

        Assert.True(BlitzsphereLayout.SameColumn(Waymark.A, Waymark.One));
        Assert.True(BlitzsphereLayout.SameColumn(Waymark.B, Waymark.Two));
    }

    [Fact]
    public void StrikeZonesFlankTheirGoals()
    {
        var d = BlitzsphereLayout.Nodes[Waymark.D];
        var four = BlitzsphereLayout.Nodes[Waymark.Four];

        // A and 1 belong to D's end, B and 2 to Four's end. FieldGeometry relies on
        // this when deciding whether a strike zone is a player's own or the enemy's.
        foreach (var near in new[] { Waymark.A, Waymark.One })
        {
            var point = BlitzsphereLayout.Nodes[near];
            Assert.True(MathF.Abs(point.X - d.X) < MathF.Abs(point.X - four.X));
            Assert.True(FieldGeometry.IsAdjacentToD(near));
        }

        foreach (var far in new[] { Waymark.B, Waymark.Two })
        {
            var point = BlitzsphereLayout.Nodes[far];
            Assert.True(MathF.Abs(point.X - four.X) < MathF.Abs(point.X - d.X));
            Assert.False(FieldGeometry.IsAdjacentToD(far));
        }
    }
}
