namespace BlitzballTracker.Core.GameState;

/// <summary>How a pass is settled, once the distance and the two players are known.</summary>
public enum PassKind
{
    /// <summary>1–3 zones ahead, or level. Carries automatically (slide 41).</summary>
    Automatic,

    /// <summary>
    /// Goal to goal. An opposed roll against the goalkeeper, who takes the ball if
    /// they win it — even though they were never the intended target (slide 41).
    /// </summary>
    ContestedByKeeper,

    /// <summary>Backwards, and legally so: nothing was on ahead (slide 43).</summary>
    BackPass,

    /// <summary>Backwards when it should not have been.</summary>
    IllegalBackPass,

    /// <summary>
    /// A goalkeeper throwing further than their reach. Everyone in the receiving zone
    /// rolls for it (slide 42).
    /// </summary>
    Overreach,

    /// <summary>
    /// A goalkeeper's three-zone throw, made because nobody was closer. Permitted, and
    /// it still comes loose in the receiving zone (slide 42).
    /// </summary>
    ForcedLong,

    /// <summary>Aimed at a goalkeeper, who cannot receive passes at all (slide 62).</summary>
    KeeperCannotReceive,
}

/// <summary>A pass, judged. <see cref="Reason"/> is empty for the legal outcomes.</summary>
public readonly record struct PassAssessment(PassKind Kind, int ZonesAhead, string Reason)
{
    /// <summary>Whether the pass was one the passer was allowed to make.</summary>
    public bool IsLegal =>
        Kind is PassKind.Automatic or PassKind.ContestedByKeeper or PassKind.BackPass or PassKind.ForcedLong;

    /// <summary>Whether the ball arrives cleanly, rather than loose for the zone to contest.</summary>
    public bool Arrives => Kind is PassKind.Automatic or PassKind.ContestedByKeeper or PassKind.BackPass;
}

public partial class BlitzGame
{
    /// <summary>
    /// Judge a pass against the distance rules.
    ///
    /// The deck spreads this across three slides — 41 for distance, 42 for the
    /// goalkeeper's shorter reach, 43 for the back pass — and they interact, so they
    /// are answered together rather than checked one at a time at the call site.
    ///
    /// Judgement only. Nothing here moves the ball or records anything: a referee is
    /// still the authority on whether a pass stood.
    /// </summary>
    public PassAssessment AssessPass(PlayerState passer, Waymark from, PlayerState receiver)
    {
        // Keepers catch fumbles, but nobody may pass to them.
        if (receiver.IsGoalkeeper)
        {
            return new PassAssessment(PassKind.KeeperCannotReceive, 0,
                $"{receiver.Name} is a goalkeeper and cannot receive a pass");
        }

        var ahead = ZonesAhead(passer, from, receiver.Position);

        if (passer.IsGoalkeeper) return AssessKeeperPass(passer, ahead);

        return ahead switch
        {
            >= 1 and <= 3 => new PassAssessment(PassKind.Automatic, ahead, string.Empty),
            4 => new PassAssessment(PassKind.ContestedByKeeper, ahead, string.Empty),

            // Level counts as forward: the two lanes sit at the same rank, so crossing
            // from 1 to A is not a retreat.
            0 => new PassAssessment(PassKind.Automatic, 0, string.Empty),

            _ => AssessBackPass(passer, ahead),
        };
    }

    /// <summary>
    /// The goalkeeper's reach: 0–2 zones, stretching to 3 only when there is nobody
    /// closer. Blocked team-mates still count as somebody (slide 42).
    /// </summary>
    private PassAssessment AssessKeeperPass(PlayerState keeper, int ahead)
    {
        // A keeper's pass never counts as a back pass, even into their own net, so the
        // direction does not matter — only how far it travels.
        var distance = Math.Abs(ahead);

        if (distance <= 2) return new PassAssessment(PassKind.Automatic, ahead, string.Empty);

        // Three zones is permitted when nobody is closer — and it still comes loose.
        // That is what reconciles slide 42 with itself: it allows the long throw and
        // then says a keeper passing more than two zones causes a fumble, which are
        // both true at once. The exception is not a way to throw further safely, it is
        // permission to put the ball into a contest rather than have none at all.
        if (distance == 3 && !HasAllyWithin(keeper, 2))
        {
            return new PassAssessment(PassKind.ForcedLong, ahead,
                "no ally within two zones, so the long throw comes loose");
        }

        var why = distance == 3
            ? "a goalkeeper may only throw three zones when nobody is closer"
            : "a goalkeeper cannot throw more than three zones";

        return new PassAssessment(PassKind.Overreach, ahead, why);
    }

    /// <summary>
    /// A back pass is a real option, not a mistake — but only when the field ahead is
    /// genuinely shut (slide 43).
    /// </summary>
    private PassAssessment AssessBackPass(PlayerState passer, int ahead)
    {
        string? refusal = null;

        if (ahead < -1)
            refusal = "a back pass reaches one zone behind at most";
        else if (passer.Position == AttackingGoal(passer))
            refusal = "a back pass cannot be made from inside the enemy goal";
        else if (HasUnblockedAllyAhead(passer))
            refusal = "a back pass needs the field ahead to be shut";
        else if (!BackPassAvailable(passer))
            refusal = $"{passer.Team} already used their back pass";

        return refusal is null
            ? new PassAssessment(PassKind.BackPass, ahead, string.Empty)
            : new PassAssessment(PassKind.IllegalBackPass, ahead, refusal);
    }

    /// <summary>Spend the team's back pass for the round.</summary>
    public void RecordBackPass(PlayerState passer)
    {
        if (!string.IsNullOrEmpty(passer.Team))
            LastBackPassRound[passer.Team] = Round;
    }
}
