using Dalamud.Configuration;

namespace BlitzballTracker;

using BlitzballTracker.Core.GameState;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Named team sheets, so a recurring league side is entered once and reused.
    /// Key is a user-chosen preset name.
    /// </summary>
    public Dictionary<string, Roster> SavedRosters { get; set; } = new();

    /// <summary>
    /// The roster in use, restored on load so a crash or reload mid-match does not
    /// mean re-entering twelve names.
    /// </summary>
    public Roster? LastRoster { get; set; }

    /// <summary>
    /// Track STANDBY as a named status when a player declares nothing.
    ///
    /// Retired from the game, so off by default. Kept as a switch rather than removed
    /// because league rules move, and reading an old recording back the way it was
    /// played needs the rules of the day.
    /// </summary>
    public bool StandbyStatus { get; set; }

    /// <summary>Draw game state into the arena itself, over the real waymarks.</summary>
    public bool ShowWorldOverlay { get; set; } = true;

    public bool ShowZoneLabels { get; set; } = true;

    public bool ShowPlayerTags { get; set; } = true;

    /// <summary>
    /// Draw the lanes connecting adjacent zones, and animate a pulse along the lane
    /// a player moves down.
    /// </summary>
    public bool ShowLaneLines { get; set; } = true;

    /// <summary>
    /// How far above a player's origin their name tag floats, in game units.
    /// Adjustable because blitzball is played underwater and characters drift.
    /// </summary>
    public float PlayerTagHeight { get; set; } = 2.2f;

    /// <summary>
    /// How close a character has to be to count as standing on a waymark.
    ///
    /// Tight on purpose: a venue is ringed with spectators, and a generous radius
    /// reads the audience rather than the field. Adjustable because how far players
    /// float from a marker varies with the venue and how tidily people line up.
    /// </summary>
    public float MarkerRadius { get; set; } = FieldGeometry.OnMarkerRadius;

    /// <summary>
    /// The local player's character name (used to resolve "You roll a..." messages).
    /// If empty, auto-detected from Dalamud.
    /// </summary>
    public string LocalPlayerName { get; set; } = string.Empty;

    /// <summary>
    /// Which team roster assignments (for coloring). Key = player name, value = team name.
    /// </summary>
    public Dictionary<string, string> TeamRosters { get; set; } = new();

    /// <summary>
    /// Whether to show the grid window by default during a game.
    /// </summary>
    public bool ShowGridWindow { get; set; } = true;

    /// <summary>
    /// Whether to show the stats window by default during a game.
    /// </summary>
    public bool ShowStatsWindow { get; set; } = true;
}
