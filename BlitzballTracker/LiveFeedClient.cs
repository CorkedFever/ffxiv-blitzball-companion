using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace BlitzballTracker;

/// <summary>
/// Sends live chat messages to the BlitzballTracker web app via HTTP.
/// </summary>
public sealed class LiveFeedClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IPluginLog _log;
    private bool _active;

    public bool IsActive => _active;
    public string BaseUrl { get; private set; }
    public int MessagesSent { get; private set; }
    public int Errors { get; private set; }

    public LiveFeedClient(IPluginLog log, string baseUrl = "http://localhost:5039")
    {
        _log = log;
        BaseUrl = baseUrl;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    public void Start(string? baseUrl = null)
    {
        if (baseUrl != null) BaseUrl = baseUrl;
        _active = true;
        MessagesSent = 0;
        Errors = 0;
    }

    public void Stop() => _active = false;

    /// <summary>Send a chat message to the web app. Fire-and-forget to avoid blocking the game thread.</summary>
    public void Send(XivChatType type, string sender, string message)
    {
        if (!_active) return;

        // Fire and forget — don't block the game thread
        _ = SendAsync(sender, message);
    }

    private async Task SendAsync(string sender, string message)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                sender,
                message,
                timestamp = DateTime.Now,
            });

            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{BaseUrl}/api/live", content);

            if (response.IsSuccessStatusCode)
                MessagesSent++;
            else
                Errors++;
        }
        catch (Exception ex)
        {
            Errors++;
            _log.Warning($"[LiveFeed] Send failed: {ex.Message}");
        }
    }

    /// <summary>Tell the web app to reset its game state (for new game).</summary>
    public async Task ResetRemoteAsync()
    {
        try
        {
            await _http.PostAsync($"{BaseUrl}/api/live/reset", null);
        }
        catch (Exception ex)
        {
            _log.Warning($"[LiveFeed] Reset failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _active = false;
        _http.Dispose();
    }
}
