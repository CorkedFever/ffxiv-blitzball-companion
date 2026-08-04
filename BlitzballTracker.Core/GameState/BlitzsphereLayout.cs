using System.Numerics;

namespace BlitzballTracker.Core.GameState;

/// <summary>
/// The Blitzsphere's on-screen layout, in a fixed 600x340 design space.
///
/// This lives in Core so the Blazor field view and the in-game ImGui widget draw
/// from one definition. Goal D sits on the left and goal 4 on the right, with the
/// letter lane (A, B) along the top and the number lane (1, 2) along the bottom,
/// matching how the waymarks are actually placed in the arena.
///
/// Note this is display space, unrelated to the world coordinates in
/// <see cref="FieldGeometry"/>: consumers scale it to whatever room they have.
/// </summary>
public static class BlitzsphereLayout
{
    public const float ViewWidth = 600f;
    public const float ViewHeight = 340f;

    public static readonly IReadOnlyDictionary<Waymark, Vector2> Nodes =
        new Dictionary<Waymark, Vector2>
        {
            [Waymark.D] = new(60, 170),

            // Letter lane runs along the top, number lane along the bottom, as the
            // markers are laid out in the arena. This is presentation only: the
            // connections, rows and columns below are the same either way.
            [Waymark.A] = new(170, 80),
            [Waymark.One] = new(170, 260),

            [Waymark.C] = new(300, 170),

            [Waymark.B] = new(430, 80),
            [Waymark.Two] = new(430, 260),

            [Waymark.Four] = new(540, 170),
        };

    /// <summary>
    /// Where a player can move to in one step.
    ///
    /// Two zig-zag paths from goal to goal, and nothing else. The cross-lane pairs
    /// (1 with A, 2 with B) are deliberately absent: they sit alongside each other
    /// and a forward can tackle across them, but nobody can simply walk between them.
    /// </summary>
    public static readonly IReadOnlyList<(Waymark From, Waymark To)> Lanes =
    [
        // Number lane: D - 1 - C - 2 - 4
        (Waymark.D, Waymark.One),
        (Waymark.One, Waymark.C),
        (Waymark.C, Waymark.Two),
        (Waymark.Two, Waymark.Four),

        // Letter lane: D - A - C - B - 4
        (Waymark.D, Waymark.A),
        (Waymark.A, Waymark.C),
        (Waymark.C, Waymark.B),
        (Waymark.B, Waymark.Four),
    ];

    /// <summary>Every waymark in draw order, goal to goal.</summary>
    public static readonly IReadOnlyList<Waymark> All =
    [
        Waymark.D, Waymark.One, Waymark.A, Waymark.C, Waymark.Two, Waymark.B, Waymark.Four,
    ];

    /// <summary>
    /// The three rows the sphere lays out in, running goal to goal.
    ///
    /// These are not the movement connections: you travel the zig-zag in
    /// <see cref="Lanes"/>. Rows are lines of sight along the field, and a forward
    /// reaches down their own row to tackle. The middle row holding both goals is
    /// what lets a forward at Center get at a goalkeeper.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<Waymark>> Rows =
    [
        [Waymark.One, Waymark.Two],             // number lane, along the top
        [Waymark.D, Waymark.C, Waymark.Four],   // goal to goal, through the middle
        [Waymark.A, Waymark.B],                 // letter lane, along the bottom
    ];

    /// <summary>
    /// The paired waymarks that make up a strike zone.
    ///
    /// A "zone" in the rulebook is a column across the field: the goals and Centre
    /// stand alone, while each strike zone is two markers stacked here. You cannot
    /// walk between the two, but a forward can reach across to tackle.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<Waymark>> Columns =
    [
        [Waymark.One, Waymark.A],
        [Waymark.Two, Waymark.B],
    ];

    /// <summary>
    /// Whether two zones are neighbours along a row.
    ///
    /// Neighbours, not merely members: the middle row runs D, C, Four, and reach
    /// carries one step along it. D and Four share that row but sit at opposite ends
    /// of the pitch, and nothing reaches the whole way across.
    /// </summary>
    public static bool SameRow(Waymark a, Waymark b) => AdjacentInAnyGroup(Rows, a, b);

    /// <summary>Whether two zones are neighbours down a column.</summary>
    public static bool SameColumn(Waymark a, Waymark b) => AdjacentInAnyGroup(Columns, a, b);

    /// <summary>Whether two zones are joined by a single movement step.</summary>
    public static bool AreConnected(Waymark a, Waymark b)
    {
        foreach (var (from, to) in Lanes)
        {
            if ((from == a && to == b) || (from == b && to == a)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether two zones lie on a common line: along a row, down a column, or
    /// diagonally.
    ///
    /// This is a forward's tackling reach, and it works like a queen rather than a
    /// rook. The diagonals are precisely the movement connections, since every lane
    /// step on this layout runs at an angle, so reach is the three straight lines
    /// taken together.
    ///
    /// It is wider than movement in both directions: a forward can strike down a
    /// column they could never walk, and reach a goalkeeper along the middle row.
    /// </summary>
    public static bool SharesLine(Waymark a, Waymark b)
    {
        if (a == Waymark.None || b == Waymark.None) return false;

        // Never the same waymark. A tackle is a movement that stuns: the tackler ends
        // up standing on their target's marker, so there has to be somewhere to
        // travel to. Contesting somebody already beside you is a block, not a tackle.
        //
        // Note this is waymark, not zone. A and 1 are one zone but two markers, so a
        // tackle across them is a real move and is allowed.
        if (a == b) return false;

        return SameRow(a, b) || SameColumn(a, b) || AreConnected(a, b);
    }

    /// <summary>
    /// Whether two zones sit next to each other in one of the given lines.
    ///
    /// Adjacency rather than shared membership, so a three-zone line reaches a step
    /// at a time instead of end to end.
    /// </summary>
    private static bool AdjacentInAnyGroup(
        IReadOnlyList<IReadOnlyList<Waymark>> groups, Waymark a, Waymark b)
    {
        if (a == Waymark.None || b == Waymark.None || a == b) return false;

        foreach (var group in groups)
        {
            for (var i = 0; i < group.Count - 1; i++)
            {
                var first = group[i];
                var second = group[i + 1];

                if ((first == a && second == b) || (first == b && second == a)) return true;
            }
        }

        return false;
    }

    public static string Label(Waymark waymark) => waymark switch
    {
        Waymark.D => "D",
        Waymark.One => "1",
        Waymark.A => "A",
        Waymark.C => "C",
        Waymark.Two => "2",
        Waymark.B => "B",
        Waymark.Four => "4",
        _ => string.Empty,
    };

    public static string ZoneName(Waymark waymark) => waymark switch
    {
        Waymark.D or Waymark.Four => "GOAL",
        Waymark.One or Waymark.A or Waymark.Two or Waymark.B => "STRIKE",
        Waymark.C => "CENTER",
        _ => string.Empty,
    };

    public static bool IsGoal(Waymark waymark) => waymark is Waymark.D or Waymark.Four;
}
