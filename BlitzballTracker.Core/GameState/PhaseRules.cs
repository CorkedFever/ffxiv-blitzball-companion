namespace BlitzballTracker.Core.GameState;

/// <summary>
/// Which zones are live in each phase, and who may act.
///
/// The sphere splits into two rings. The outer ring is the four strike zones; the
/// inner ring is the centre and the two goals. A phase activates one ring, and
/// everyone standing in it acts at the same time rather than in turn.
/// </summary>
public static class PhaseRules
{
    /// <summary>The strike zones, flanking both goals.</summary>
    public static readonly Waymark[] OuterZones =
        [Waymark.A, Waymark.B, Waymark.One, Waymark.Two];

    /// <summary>Centre and the two goals.</summary>
    public static readonly Waymark[] InnerZones =
        [Waymark.C, Waymark.D, Waymark.Four];

    public static bool IsOuterZone(Waymark waymark) =>
        waymark is Waymark.A or Waymark.B or Waymark.One or Waymark.Two;

    public static bool IsInnerZone(Waymark waymark) =>
        waymark is Waymark.C or Waymark.D or Waymark.Four;

    /// <summary>
    /// The zones acting in this phase, or null when the phase has no field action:
    /// huddles, repositions and the referee-driven parts of the game.
    /// </summary>
    public static IReadOnlyList<Waymark>? ActiveZones(GamePhase phase) => phase switch
    {
        GamePhase.OuterHuddle or GamePhase.OuterPhase or GamePhase.BallCarrierOuter => OuterZones,
        GamePhase.InnerHuddle or GamePhase.InnerPhase or GamePhase.BallCarrierInner => InnerZones,

        // At the buzzer the ball is in a strike zone and its neighbours act.
        GamePhase.BuzzerPhase => OuterZones,

        _ => null,
    };

    /// <summary>Whether a zone's occupants act during this phase.</summary>
    public static bool ActsThisPhase(Waymark zone, GamePhase phase)
    {
        var active = ActiveZones(phase);
        if (active is null) return false;

        for (var i = 0; i < active.Count; i++)
        {
            if (active[i] == zone) return true;
        }

        return false;
    }

    /// <summary>True for the phases where the whole active ring acts together.</summary>
    public static bool IsSimultaneousActionPhase(GamePhase phase) =>
        phase is GamePhase.OuterPhase or GamePhase.InnerPhase or GamePhase.BuzzerPhase;

    /// <summary>True for the phases where only the ball carrier acts.</summary>
    public static bool IsBallCarrierPhase(GamePhase phase) =>
        phase is GamePhase.BallCarrierOuter or GamePhase.BallCarrierInner;
}
