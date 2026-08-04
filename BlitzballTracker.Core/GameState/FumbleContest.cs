namespace BlitzballTracker.Core.GameState;

/// <summary>Why the ball came loose.</summary>
public enum FumbleCause
{
    /// <summary>A dazed midfielder, forward or defender was passed to (slide 50).</summary>
    DazedReceiver,

    /// <summary>A goalkeeper threw further than their reach (slide 42).</summary>
    KeeperOverreach,
}

/// <summary>
/// A loose ball, contested by everyone standing in the zone it landed in.
///
/// This deliberately sits outside the ordinary roll machinery. Fumble rolls are flat
/// <c>/random 100</c> and have to be made even by players who already rolled this
/// phase, so they cannot go through <see cref="PlayerState.PhaseRoll"/> — doing that
/// would overwrite a roll that is still deciding somebody's action.
/// </summary>
public sealed class FumbleContest
{
    public required Waymark Zone { get; init; }

    public required FumbleCause Cause { get; init; }

    /// <summary>Who the ball was meant for. Defends the tie (slide 32).</summary>
    public required string IntendedReceiver { get; init; }

    /// <summary>The side that lost the ball. Defends the tie when the receiver is not in it.</summary>
    public required string FumblingTeam { get; init; }

    public required DateTime StartedAt { get; init; }

    /// <summary>Everyone entitled to roll, by name.</summary>
    public List<string> Contenders { get; } = [];

    public Dictionary<string, int> Rolls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsContender(string name) => Contenders.Contains(name, StringComparer.OrdinalIgnoreCase);

    public bool HasRolled(string name) => Rolls.ContainsKey(name);

    /// <summary>Whether every contender has thrown in.</summary>
    public bool Complete => Rolls.Count >= Contenders.Count;

    public IEnumerable<string> Outstanding =>
        Contenders.Where(name => !Rolls.ContainsKey(name));
}

public partial class BlitzGame
{
    /// <summary>The loose ball currently being contested, if any.</summary>
    public FumbleContest? Fumble { get; private set; }

    /// <summary>
    /// Whether a pass landing on this player shakes the ball loose.
    ///
    /// Only outfield players fumble, and only on receiving. A goalkeeper cannot be
    /// passed to in the first place, and a dazed player carrying the ball keeps it.
    /// </summary>
    public static bool FumblesOnReceipt(PlayerState receiver) =>
        receiver.IsDazed && receiver.Role is not PlayerRole.Goalkeeper and not PlayerRole.None;

    /// <summary>
    /// Open a contest for a loose ball. Returns null when nobody is standing in the
    /// zone to contest it.
    ///
    /// Zone rather than waymark: A and 1 are one zone across two markers, so a ball
    /// loose on A is contested by anyone on 1 as well.
    /// </summary>
    public FumbleContest? OpenFumble(Waymark zone, FumbleCause cause, string intendedReceiver, DateTime at)
    {
        var rank = ZoneRank(zone);
        if (rank < 0) return null;

        var receiverTeam = Players.TryGetValue(intendedReceiver, out var target) ? target.Team : string.Empty;

        var contest = new FumbleContest
        {
            Zone = zone,
            Cause = cause,
            IntendedReceiver = intendedReceiver,
            FumblingTeam = receiverTeam,
            StartedAt = at,
        };

        foreach (var player in Players.Values)
        {
            if (ZoneRank(player.Position) != rank) continue;
            contest.Contenders.Add(player.Name);
        }

        if (contest.Contenders.Count == 0) return null;

        Fumble = contest;
        return contest;
    }

    /// <summary>Record a contender's fumble roll. Returns false if it was not theirs to make.</summary>
    public bool RecordFumbleRoll(string name, int roll)
    {
        if (Fumble is not { } contest) return false;
        if (!contest.IsContender(name)) return false;

        contest.Rolls[name] = roll;
        return true;
    }

    /// <summary>
    /// Settle the contest and hand the ball to the winner, or null if rolls are still
    /// outstanding.
    ///
    /// Ties fall to the defending player — the intended receiver, or failing that
    /// somebody from the side that lost the ball (slide 32). Referees may call up to
    /// three rerolls before applying that default; this reports where it lands if they
    /// do not.
    /// </summary>
    public string? ResolveFumble()
    {
        if (Fumble is not { } contest || !contest.Complete) return null;

        return SettleFumble(contest);
    }

    /// <summary>
    /// Settle a contest that never got all its rolls, using whoever did roll.
    ///
    /// Needed because a phase can close with rolls still owed: somebody forgets, or a
    /// recording is missing a line. Leaving the contest open would mean it quietly ate
    /// every later roll from those players, so it is closed at the phase boundary
    /// either way. Returns null when nobody rolled at all, leaving possession alone.
    /// </summary>
    public string? AbandonFumble()
    {
        if (Fumble is not { } contest) return null;

        if (contest.Rolls.Count == 0)
        {
            Fumble = null;
            return null;
        }

        return SettleFumble(contest);
    }

    private string? SettleFumble(FumbleContest contest)
    {
        var best = -1;
        var tied = new List<string>();

        foreach (var (name, roll) in contest.Rolls)
        {
            if (roll > best)
            {
                best = roll;
                tied.Clear();
                tied.Add(name);
            }
            else if (roll == best)
            {
                tied.Add(name);
            }
        }

        var winner = tied.Count == 1 ? tied[0] : BreakFumbleTie(contest, tied);

        Fumble = null;
        return winner;
    }

    /// <summary>
    /// The defending player takes it: first the intended receiver, then anyone from
    /// the side that lost the ball (slide 32).
    /// </summary>
    private string BreakFumbleTie(FumbleContest contest, List<string> tied)
    {
        foreach (var name in tied)
        {
            if (name.Equals(contest.IntendedReceiver, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        if (!string.IsNullOrEmpty(contest.FumblingTeam))
        {
            foreach (var name in tied)
            {
                if (Players.TryGetValue(name, out var player) &&
                    player.Team.Equals(contest.FumblingTeam, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }

        return tied[0];
    }

    /// <summary>Abandon an unresolved contest, e.g. when the match is reset.</summary>
    public void ClearFumble() => Fumble = null;
}
