using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace BlitzballTracker;

using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;

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

    /// <summary>Whether the far end has the lineup we are tracking with.</summary>
    public bool RosterSent { get; private set; }

    public void Start(string? baseUrl = null)
    {
        if (baseUrl != null) BaseUrl = baseUrl;
        _active = true;
        MessagesSent = 0;
        Errors = 0;
        RosterSent = false;
    }

    public void Stop() => _active = false;

    /// <summary>
    /// Send the lineup, without which the far end can follow nothing.
    ///
    /// The web app re-parses the feed through its own parser, and that parser only
    /// recognises names on the team sheet. Until this arrives it discards every player
    /// name in the feed: phases and the scoreboard tick over while the field stays
    /// empty and possession never resolves, which looks like a rendering fault rather
    /// than a missing roster.
    ///
    /// Sent as the same header format recordings use, so both ends share one parser.
    /// </summary>
    public void SendRoster(Roster? roster)
    {
        if (!_active) return;
        if (roster is not { NamedCount: > 0 }) return;

        _ = SendRosterAsync(roster);
    }

    private async Task SendRosterAsync(Roster roster)
    {
        try
        {
            var content = new StringContent(RosterHeader.Write(roster), Encoding.UTF8, "text/plain");
            var response = await _http.PostAsync($"{BaseUrl}/api/live/roster", content);

            if (response.IsSuccessStatusCode)
            {
                RosterSent = true;
                _log.Info($"[LiveFeed] Sent roster: {roster.HomeTeam} vs {roster.AwayTeam}.");
            }
            else
            {
                Errors++;
                _log.Warning($"[LiveFeed] Roster rejected: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Errors++;
            _log.Warning($"[LiveFeed] Roster send failed: {ex.Message}");
        }
    }

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
