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

    public event Action? OnStateChanged;

    public LiveService()
    {
        Parser = new ChatParser(Game);
    }

    public void NotifyStateChanged() => OnStateChanged?.Invoke();

    public bool ProcessMessage(string sender, string message, DateTime timestamp)
    {
        var recognized = Parser.ProcessMessage(sender, message, timestamp);
        if (recognized) EventsRecognized++;
        LinesProcessed++;
        OnStateChanged?.Invoke();
        return recognized;
    }

    public void Reset()
    {
        Game = new BlitzGame();
        Parser = new ChatParser(Game);
        LinesProcessed = 0;
        EventsRecognized = 0;
        OnStateChanged?.Invoke();
    }
}
