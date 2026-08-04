using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;

namespace BlitzballTracker.Web.Data;

/// <summary>
/// Singleton service for live game state (plugin feed).
/// Completely independent from GameService (replay/file uploads).
/// </summary>
public class LiveService
{
    public BlitzGame Game { get; private set; } = new();
    public ChatParser Parser { get; private set; }
    public int LinesProcessed { get; private set; }
    public int EventsRecognized { get; private set; }

    /// <summary>Whether a roster has arrived from the plugin yet.</summary>
    public bool HasRoster => Game.HasRoster;

    public event Action? OnStateChanged;

    public LiveService()
    {
        Parser = new ChatParser(Game);
    }

    public void NotifyStateChanged() => OnStateChanged?.Invoke();

    /// <summary>
    /// Take the lineup the plugin is tracking with.
    ///
    /// Nothing can be followed without this. The parser only recognises names on the
    /// team sheet, so until one arrives every player name in the feed is discarded and
    /// the field stays empty while phases and the scoreboard tick over — which looks
    /// like a rendering fault and is not one.
    /// </summary>
    public void ApplyRoster(Roster roster)
    {
        Game.ApplyRoster(roster);
        OnStateChanged?.Invoke();
    }

    public bool ProcessMessage(string sender, string message, DateTime timestamp)
    {
        var recognized = Parser.ProcessMessage(sender, message, timestamp);
        if (recognized) EventsRecognized++;
        LinesProcessed++;
        OnStateChanged?.Invoke();
        return recognized;
    }

    /// <summary>
    /// Clear the match but keep the lineup.
    ///
    /// Building a fresh <see cref="BlitzGame"/> here used to throw the roster away with
    /// it, so a reset mid-broadcast left the overlay unable to recognise anybody until
    /// the plugin happened to send one again.
    /// </summary>
    public void Reset()
    {
        Game.Reset();
        LinesProcessed = 0;
        EventsRecognized = 0;
        OnStateChanged?.Invoke();
    }
}
