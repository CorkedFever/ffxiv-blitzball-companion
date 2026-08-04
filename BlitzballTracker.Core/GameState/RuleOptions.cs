namespace BlitzballTracker.Core.GameState;

/// <summary>
/// Rules that vary between editions of the game.
///
/// The league's rules move, and the published deck lags behind actual play. Where a
/// rule has been retired or changed, it belongs here as a switch rather than being
/// edited out of the code: a rule that is gone this season may well be back next one,
/// and a deleted one has to be rediscovered and rewritten from scratch.
///
/// Defaults describe how the game is played now, not what the deck says.
/// </summary>
public sealed class RuleOptions
{
    /// <summary>
    /// Whether failing to declare applies the STANDBY status.
    ///
    /// Retired as of the 2026 season. The v3.2 deck still documents it across three
    /// slides, and several actions are described as "becoming Standby" when they have
    /// no legal target. Letting a phase run out is still a loss of action either way;
    /// this only controls whether it is tracked as a named state.
    /// </summary>
    public bool StandbyStatus { get; set; }

    /// <summary>Every option back to how the game is currently played.</summary>
    public static RuleOptions Current() => new();

    /// <summary>
    /// Everything the v3.2 deck describes, including what has since been retired.
    /// Useful for reading old recordings back the way they were played.
    /// </summary>
    public static RuleOptions AsPublished() => new()
    {
        StandbyStatus = true,
    };

    public RuleOptions Clone() => new()
    {
        StandbyStatus = StandbyStatus,
    };
}
