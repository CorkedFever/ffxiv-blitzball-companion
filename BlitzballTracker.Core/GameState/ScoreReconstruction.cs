namespace BlitzballTracker.Core.GameState;

/// <summary>
/// What a restart tells us about the score.
///
/// The referees never post the score. They do announce every restart, and slide 15
/// makes each one a statement about the score: a goal that levels the game is followed
/// by a contested Blitzoff, and a goal that does not is followed by a Blitzon where the
/// side that is <em>behind</em> simply receives the ball. So "Barracuda ball" is not a
/// note about who conceded — it says Barracuda are losing.
/// </summary>
public enum RestartReading
{
    /// <summary>A restart happened but nothing about the score can be read from it.</summary>
    Unknown,

    /// <summary>A contested Blitzoff: the goal levelled the scores.</summary>
    Level,

    /// <summary>A Blitzon where the home side received: they are behind.</summary>
    HomeBehind,

    /// <summary>A Blitzon where the away side received: they are behind.</summary>
    AwayBehind,
}

public partial class BlitzGame
{
    /// <summary>
    /// Goals seen, counted from restarts. Exact even when the split between the teams
    /// is not, because every restart follows exactly one goal.
    /// </summary>
    public int GoalsSeen { get; private set; }

    /// <summary>
    /// Whether the score was worked out from restarts rather than read from a posted
    /// score line. Worth showing, because a derived score is an inference and the UI
    /// should not present it with the same confidence as one somebody typed.
    /// </summary>
    public bool ScoreIsDerived { get; private set; }

    /// <summary>
    /// Whether the derivation is still pinned to one answer.
    ///
    /// It comes unpinned when a two-goal gap opens: with the home side two ahead, the
    /// next goal leaves the away side behind either way, so "away behind" no longer
    /// says who scored. The total stays right and the next levelling restart re-pins it.
    /// </summary>
    public bool ScoreIsCertain { get; private set; } = true;

    /// <summary>
    /// Whether the score came from somebody stating it rather than from inference.
    ///
    /// When it did, the restarts stop being evidence and become a check: a Blitzon
    /// called at a level score is a miscall worth flagging, not a reason to rewrite a
    /// score we were told.
    /// </summary>
    public bool ScoreWasPosted { get; private set; }

    /// <summary>
    /// Record the goal implied by a restart, and narrow the score as far as the
    /// referee's call allows.
    /// </summary>
    public void RegisterGoalFromRestart(RestartReading reading)
    {
        GoalsSeen++;

        // Nothing to infer when the score is already known.
        if (ScoreWasPosted) return;

        ScoreIsDerived = true;

        // Levelling is absolute: with this many goals scored and the game tied, each
        // side has exactly half. That holds no matter how muddled things were before,
        // which is what makes it the anchor the derivation recovers on.
        if (reading == RestartReading.Level)
        {
            SetDerivedScore(new Score(GoalsSeen / 2, GoalsSeen / 2));
            ScoreIsCertain = GoalsSeen % 2 == 0;
            return;
        }

        if (reading == RestartReading.Unknown || !ScoreIsCertain)
        {
            ScoreIsCertain = false;
            return;
        }

        // Exactly one side scored, so there are only two possible new scores. Keep
        // whichever agrees with the side the referee just handed the ball to.
        var scoredHome = new Score(Score.Home + 1, Score.Away);
        var scoredAway = new Score(Score.Home, Score.Away + 1);

        var homeFits = Fits(scoredHome, reading);
        var awayFits = Fits(scoredAway, reading);

        if (homeFits && !awayFits) SetDerivedScore(scoredHome);
        else if (awayFits && !homeFits) SetDerivedScore(scoredAway);
        else ScoreIsCertain = false;
    }

    private static bool Fits(Score score, RestartReading reading) => reading switch
    {
        RestartReading.HomeBehind => score.Home < score.Away,
        RestartReading.AwayBehind => score.Away < score.Home,
        _ => false,
    };

    /// <summary>
    /// Take a score somebody actually posted, which outranks anything derived.
    /// </summary>
    public void AdoptPostedScore(Score posted)
    {
        Score = posted;   // the public setter marks it as known
        GoalsSeen = posted.Home + posted.Away;
    }

    /// <summary>
    /// Pin the score from a halftime restart bonus, which is +10 per point of deficit
    /// (slide 15). The bonus states the gap outright, so it re-pins a derivation that
    /// had come loose.
    /// </summary>
    public void AdoptHalftimeDeficit(int deficit, bool homeIsBehind)
    {
        if (deficit <= 0 || GoalsSeen < deficit) return;

        // total = leader + trailer, gap = leader - trailer.
        var trailer = (GoalsSeen - deficit) / 2;
        if ((GoalsSeen - deficit) % 2 != 0) return;

        SetDerivedScore(homeIsBehind
            ? new Score(trailer, trailer + deficit)
            : new Score(trailer + deficit, trailer));

        ScoreIsCertain = true;
    }

    /// <summary>
    /// Write a score the tracker worked out, without marking it as one somebody stated.
    /// </summary>
    private void SetDerivedScore(Score derived)
    {
        _score = derived;
        ScoreIsDerived = true;
    }

    /// <summary>Another name each side goes by, usually the city they represent.</summary>
    public string? HomeAlias { get; set; }
    public string? AwayAlias { get; set; }

    public bool MatchesHome(string spoken) => NamesTheSameTeam(spoken, HomeTeam, HomeAlias);
    public bool MatchesAway(string spoken) => NamesTheSameTeam(spoken, AwayTeam, AwayAlias);

    /// <summary>
    /// Whether a name a referee used refers to this side.
    ///
    /// Names are shortened in passing — "Barracuda ball" for the Barracudas — so either
    /// containing the other counts. The alias covers the other habit, of calling a team
    /// by its city, which is a different word entirely and cannot be matched by shape.
    /// </summary>
    private static bool NamesTheSameTeam(string spoken, string rostered, string? alias)
    {
        spoken = spoken.Trim();
        if (spoken.Length == 0) return false;

        if (rostered.Length > 0 &&
            (spoken.Contains(rostered, StringComparison.OrdinalIgnoreCase) ||
             rostered.Contains(spoken, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return alias is { Length: > 0 } &&
               (spoken.Contains(alias, StringComparison.OrdinalIgnoreCase) ||
                alias.Contains(spoken, StringComparison.OrdinalIgnoreCase));
    }

    private void ResetScoreDerivation()
    {
        GoalsSeen = 0;
        ScoreIsDerived = false;
        ScoreIsCertain = true;
        ScoreWasPosted = false;
    }
}
