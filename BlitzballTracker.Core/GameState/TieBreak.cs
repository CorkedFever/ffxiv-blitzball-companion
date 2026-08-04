namespace BlitzballTracker.Core.GameState;

/// <summary>
/// Two players who rolled the same number, rerolling to settle the action between them.
///
/// Three things make this its own path rather than another phase roll:
///
/// It is <em>private to the pair</em>. The reroll settles only their contest — it is
/// never compared against anyone else's roll, and it does not revisit comparisons that
/// were already decided. Somebody who lost to the original roll stays beaten by it.
///
/// It <em>does not replace the phase roll</em> (slide 33), which is still deciding
/// every other comparison that roll was part of.
///
/// And the referee calls it <em>at the end of the phase</em> (slide 32), not the moment
/// the tie appears, so the rest of the phase plays out first.
/// </summary>
public sealed class TieBreak
{
    /// <summary>Up to three rerolls; after the third the defender takes it (slide 32).</summary>
    public const int MaxRerolls = 3;

    public required ActionEvent Action { get; init; }

    /// <summary>The player who declared the action.</summary>
    public required string Challenger { get; init; }

    /// <summary>
    /// The target of the action, who takes it if the rerolls run out. In a fumble this
    /// is the intended receiver or the side that lost the ball.
    /// </summary>
    public required string Defender { get; init; }

    public required int TiedAt { get; init; }

    public required DateTime OpenedAt { get; init; }

    /// <summary>Which reroll this is, counting from one.</summary>
    public int Attempt { get; private set; } = 1;

    /// <summary>
    /// How many phase boundaries this has survived.
    ///
    /// The reroll is called at the end of a phase and expected before the next one
    /// starts. One that outlives that would sit there swallowing the next phase's rolls
    /// from both players, so it is given a phase and then closed.
    /// </summary>
    public int BoundariesSurvived { get; set; }

    public Dictionary<string, int> Rolls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Involves(string name) =>
        Challenger.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        Defender.Equals(name, StringComparison.OrdinalIgnoreCase);

    public bool HasRolled(string name) => Rolls.ContainsKey(name);

    public bool Complete => Rolls.Count >= 2;

    public IEnumerable<string> Outstanding =>
        new[] { Challenger, Defender }.Where(name => !Rolls.ContainsKey(name));

    /// <summary>Whether the rerolls are spent and the defender takes it by default.</summary>
    public bool Exhausted => Attempt >= MaxRerolls;

    /// <summary>
    /// Settle this attempt. Returns the winner, or null when they tied again and there
    /// is another reroll to come — in which case the slate is wiped for it.
    /// </summary>
    public string? Settle()
    {
        if (!Complete) return null;

        var challengerRoll = Rolls[Challenger];
        var defenderRoll = Rolls[Defender];

        if (challengerRoll > defenderRoll) return Challenger;
        if (defenderRoll > challengerRoll) return Defender;

        // Tied again. The defender takes it once the rerolls are spent.
        if (Exhausted) return Defender;

        Attempt++;
        Rolls.Clear();
        return null;
    }
}

public partial class BlitzGame
{
    /// <summary>
    /// Ties waiting to be rerolled. A phase can end with several, and each is settled
    /// independently by the two players in it.
    /// </summary>
    public List<TieBreak> TieBreaks { get; } = [];

    public TieBreak? TieBreakFor(string name) =>
        TieBreaks.FirstOrDefault(t => t.Involves(name));

    public void ClearTieBreaks() => TieBreaks.Clear();
}
