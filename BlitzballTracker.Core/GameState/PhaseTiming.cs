namespace BlitzballTracker.Core.GameState;

/// <summary>
/// How long each phase runs for.
///
/// Huddles are a short planning window; action phases run about a minute before the
/// referee calls time. Phases often end early once everyone has acted, so these are
/// ceilings rather than fixed lengths.
/// </summary>
public static class PhaseTiming
{
    /// <summary>Planning window before an action phase.</summary>
    public static readonly TimeSpan Huddle = TimeSpan.FromSeconds(15);

    /// <summary>Maximum length of an action phase.</summary>
    public static readonly TimeSpan Action = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The expected length of a phase, or null when it has no clock: the referee
    /// drives the rest at their own pace.
    /// </summary>
    public static TimeSpan? For(GamePhase phase) => phase switch
    {
        GamePhase.OuterHuddle or GamePhase.InnerHuddle => Huddle,

        GamePhase.OuterPhase or GamePhase.InnerPhase => Action,
        GamePhase.BallCarrierOuter or GamePhase.BallCarrierInner => Action,
        GamePhase.BuzzerPhase => Action,

        _ => null,
    };

    /// <summary>
    /// How long a roll may arrive after its phase closed and still be attributed to
    /// the action it was for.
    ///
    /// Deliberately far shorter than a phase. Set anywhere near <see cref="Action"/>
    /// and a straggler could attach itself to an action from a phase that ended a
    /// full minute earlier, which is worse than dropping it.
    /// </summary>
    public static readonly TimeSpan LateRollGrace = TimeSpan.FromSeconds(20);
}
