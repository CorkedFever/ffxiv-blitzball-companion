namespace BlitzballTracker.Core.GameState;

/// <summary>
/// A surveyor catching somebody moving down the lane they are watching.
///
/// Declaring SURVEY is not rolled for. It arms a guard over a lane, and the roll only
/// happens at Reposition, when an opponent actually tries to come through — which is
/// why this is a deferred contest rather than something settled when it was called.
///
/// Lose it and the move simply does not happen; the mover stays where they were.
/// </summary>
public sealed class SurveyContest
{
    public required string Mover { get; init; }

    public required string Surveyor { get; init; }

    public required Waymark From { get; init; }

    public required Waymark To { get; init; }

    public required DateTime OpenedAt { get; init; }

    public Dictionary<string, int> Rolls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Involves(string name) =>
        Mover.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        Surveyor.Equals(name, StringComparison.OrdinalIgnoreCase);

    public bool HasRolled(string name) => Rolls.ContainsKey(name);

    public bool Complete => Rolls.Count >= 2;

    public IEnumerable<string> Outstanding =>
        new[] { Mover, Surveyor }.Where(name => !Rolls.ContainsKey(name));

    /// <summary>
    /// Whether the mover gets through. Null while rolls are outstanding.
    ///
    /// The surveyor is the one defending the lane, so a tie stops the move — the same
    /// way a tie goes to the defender everywhere else.
    /// </summary>
    public bool? MoverWins()
    {
        if (!Complete) return null;
        return Rolls[Mover] > Rolls[Surveyor];
    }
}

public partial class BlitzGame
{
    /// <summary>Movements caught by a survey and waiting on their roll-off.</summary>
    public List<SurveyContest> SurveyContests { get; } = [];

    public SurveyContest? SurveyContestFor(string name) =>
        SurveyContests.FirstOrDefault(c => c.Involves(name));

    /// <summary>
    /// The opposing surveyor watching this movement, if there is one.
    ///
    /// A player never surveys their own side's movement — the point of the action is
    /// stopping the other team coming through.
    /// </summary>
    public PlayerState? SurveyorAgainst(PlayerState mover, Waymark from, Waymark to)
    {
        var guard = SurveyorOf(from, to);
        if (guard is null) return null;
        if (guard.Team.Equals(mover.Team, StringComparison.OrdinalIgnoreCase)) return null;

        return guard;
    }

    public void ClearSurveyContests() => SurveyContests.Clear();
}
