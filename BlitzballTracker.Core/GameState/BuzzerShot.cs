namespace BlitzballTracker.Core.GameState;

/// <summary>
/// An extra shot owed at the very end of a set (slide 27).
///
/// The final shot is not always the last thing that happens. A carrier who is blocked
/// and loses the ball hands the *new* carrier a shot from wherever they stand, with the
/// player who just lost it rolling to intercept. A keeper who catches one takes an
/// immediate goal-to-goal shot of their own. Each of those can be cut out in turn — but
/// only so far: the second interception or catch ends the set.
/// </summary>
public sealed class BuzzerShot
{
    /// <summary>
    /// How many times the ball can change hands before the set is over regardless.
    ///
    /// Slide 27 caps it: "if the ball is intercepted and/or caught the second time in
    /// any of these scenarios, the Set Ends".
    /// </summary>
    public const int MaxLinks = 2;

    /// <summary>Who may take the shot.</summary>
    public required string Shooter { get; init; }

    /// <summary>Who rolls to intercept it — normally whoever just lost the ball.</summary>
    public required string Interceptor { get; init; }

    /// <summary>Which link of the chain this is, counting from one.</summary>
    public required int Link { get; init; }

    /// <summary>Whether this is the keeper's goal-to-goal reply to a catch.</summary>
    public bool IsKeeperReply { get; init; }

    public required DateTime OpenedAt { get; init; }

    /// <summary>Whether the chain has run as far as the rules allow.</summary>
    public bool IsLast => Link >= MaxLinks;
}

public partial class BlitzGame
{
    /// <summary>The buzzer shot currently owed, if any.</summary>
    public BuzzerShot? BuzzerShot { get; private set; }

    /// <summary>
    /// Whether play is in the exchange a set ends on.
    ///
    /// The final inner carrier turn and the buzzer phase are the two moments where a
    /// lost ball turns into another shot rather than simply ending the round.
    /// </summary>
    public bool IsFinalExchange =>
        Phase == GamePhase.BuzzerPhase ||
        (Phase == GamePhase.BallCarrierInner && Round >= FinalRound);

    /// <summary>
    /// Hand a buzzer shot to whoever just took the ball. Returns null when the chain has
    /// run out, or when this is not the end of a set at all.
    /// </summary>
    public BuzzerShot? OpenBuzzerShot(string shooter, string interceptor, DateTime at, bool keeperReply = false)
    {
        if (!IsFinalExchange) return null;
        if (BuzzerShot is { IsLast: true }) return null;

        var link = (BuzzerShot?.Link ?? 0) + 1;

        BuzzerShot = new BuzzerShot
        {
            Shooter = shooter,
            Interceptor = interceptor,
            Link = link,
            IsKeeperReply = keeperReply,
            OpenedAt = at,
        };

        return BuzzerShot;
    }

    public void ClearBuzzerShot() => BuzzerShot = null;
}
