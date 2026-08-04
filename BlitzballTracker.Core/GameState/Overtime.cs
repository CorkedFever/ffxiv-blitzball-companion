namespace BlitzballTracker.Core.GameState;

/// <summary>
/// One attempt in a shootout, recorded so the sequence can be read back.
/// </summary>
public readonly record struct ShootoutAttempt(string Shooter, string Team, int Roll, int KeeperRoll, bool Scored);

/// <summary>
/// The captains' duel that follows a drawn shootout.
///
/// The sphere empties, both captains meet at Centre, and the referee calls a final
/// blitzoff. Whoever loses it gets one chance to block; a blocked captain must win
/// another roll to get their shot away, and failing that the ball is intercepted and
/// the roles swap. It repeats until a shot goes in unblocked (slide 29).
/// </summary>
public sealed class SuddenDeath
{
    /// <summary>The captain holding the ball, who shoots next.</summary>
    public string? Holder { get; set; }

    /// <summary>The other captain, who owes a block attempt.</summary>
    public string? Challenger { get; set; }

    /// <summary>Whether the holder is currently blocked and must fight the shot away.</summary>
    public bool HolderBlocked { get; set; }

    /// <summary>How many times possession has changed hands. Only for the commentary.</summary>
    public int Exchanges { get; set; }

    /// <summary>Swap the roles after an interception.</summary>
    public void Turnover()
    {
        (Holder, Challenger) = (Challenger, Holder);
        HolderBlocked = false;
        Exchanges++;
    }
}

public partial class BlitzGame
{
    /// <summary>Five attempts a side (slide 28).</summary>
    public const int ShootoutAttemptsPerSide = 5;

    /// <summary>
    /// The order shooters step up in: midfielder first, then out along the line
    /// (slide 28). Goalkeepers do not take one — they are facing them.
    /// </summary>
    public static readonly PlayerRole[] ShootoutOrder =
    [
        PlayerRole.Midfield,
        PlayerRole.LeftForward,
        PlayerRole.RightForward,
        PlayerRole.LeftDefender,
        PlayerRole.RightDefender,
    ];

    /// <summary>Goals in the shootout, kept apart from the match score until it is settled.</summary>
    public Score ShootoutScore { get; set; }

    /// <summary>Attempts taken so far, in order.</summary>
    public List<ShootoutAttempt> ShootoutAttempts { get; } = [];

    /// <summary>Which side won the roll-off and shoots first. Empty until known.</summary>
    public string ShootoutFirstTeam { get; set; } = string.Empty;

    /// <summary>The captains' duel, once a drawn shootout brings one about.</summary>
    public SuddenDeath? SuddenDeath { get; set; }

    /// <summary>Whether both sides have taken all five.</summary>
    public bool ShootoutComplete => ShootoutAttempts.Count >= ShootoutAttemptsPerSide * 2;

    /// <summary>
    /// Whose turn it is to step up, by team and role, or null once it is over.
    ///
    /// Sides alternate from whoever won the roll-off, and each works down its own line
    /// independently — so this is the round number within a side, not a global count.
    /// </summary>
    public (string Team, PlayerRole Role)? NextShooter()
    {
        if (ShootoutComplete) return null;
        if (string.IsNullOrEmpty(ShootoutFirstTeam)) return null;

        var second = ShootoutFirstTeam.Equals(HomeTeam, StringComparison.OrdinalIgnoreCase)
            ? AwayTeam
            : HomeTeam;

        var taken = ShootoutAttempts.Count;
        var team = taken % 2 == 0 ? ShootoutFirstTeam : second;

        return (team, ShootoutOrder[taken / 2]);
    }

    /// <summary>
    /// Record an attempt. Flat rolls, no modifiers of any kind — that is the whole
    /// point of a shootout (slide 28).
    /// </summary>
    public bool RecordShootoutAttempt(PlayerState shooter, int roll, int keeperRoll)
    {
        var scored = roll > keeperRoll;
        var isHome = shooter.Team.Equals(HomeTeam, StringComparison.OrdinalIgnoreCase);

        ShootoutAttempts.Add(new ShootoutAttempt(shooter.Name, shooter.Team, roll, keeperRoll, scored));

        if (scored)
        {
            ShootoutScore = isHome
                ? ShootoutScore with { Home = ShootoutScore.Home + 1 }
                : ShootoutScore with { Away = ShootoutScore.Away + 1 };
        }

        return scored;
    }

    /// <summary>
    /// The side that won the shootout, or null if it too was drawn.
    ///
    /// The winner takes a single point, which is what breaks the tie — the shootout
    /// tally itself is not added to the match score.
    /// </summary>
    public string? ShootoutWinner()
    {
        if (ShootoutScore.Home == ShootoutScore.Away) return null;
        return ShootoutScore.Home > ShootoutScore.Away ? HomeTeam : AwayTeam;
    }

    public void ClearOvertime()
    {
        ShootoutScore = default;
        ShootoutAttempts.Clear();
        ShootoutFirstTeam = string.Empty;
        SuddenDeath = null;
    }
}
