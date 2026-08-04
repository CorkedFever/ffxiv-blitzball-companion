using BlitzballTracker.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(o => o.DetailedErrors = true);
builder.Services.AddSingleton<GameService>();
builder.Services.AddSingleton<LiveService>();
builder.Logging.AddFilter("Microsoft.AspNetCore.Components", LogLevel.Trace);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}


app.UseStaticFiles();

app.UseRouting();

// ── Live feed API (plugin → web) — uses LiveService, independent from replay ──
app.MapPost("/api/live", (LiveChatMessage msg, LiveService svc) =>
{
    var sender = msg.Sender?.Trim() ?? "";
    var message = msg.Message?.Trim() ?? "";
    if (string.IsNullOrEmpty(message)) return Results.BadRequest("Empty message");

    var timestamp = msg.Timestamp != default ? msg.Timestamp : DateTime.Now;
    var recognized = svc.ProcessMessage(sender, message, timestamp);

    return Results.Ok(new { recognized, players = svc.Game.Players.Count });
});

app.MapPost("/api/live/reset", (LiveService svc) =>
{
    svc.Reset();
    return Results.Ok();
});

app.MapGet("/api/live/status", (LiveService svc) =>
{
    return Results.Ok(new
    {
        score = $"{svc.Game.Score.Home}:{svc.Game.Score.Away}",
        home = svc.Game.HomeTeam,
        away = svc.Game.AwayTeam,
        players = svc.Game.Players.Count,
        events = svc.EventsRecognized,
        phase = svc.Game.Phase.ToString(),
        homeGoal = svc.Game.HomeGoalTarget.ToString(),
        awayGoal = svc.Game.AwayGoalTarget.ToString(),
    });
});

app.MapPost("/api/live/roster", (RosterSetup roster, LiveService svc) =>
{
    // Set teams
    if (!string.IsNullOrEmpty(roster.HomeTeam)) svc.Game.HomeTeam = roster.HomeTeam;
    if (!string.IsNullOrEmpty(roster.AwayTeam)) svc.Game.AwayTeam = roster.AwayTeam;

    // Set goal assignments (determines all player positions)
    if (Enum.TryParse<BlitzballTracker.Core.GameState.Waymark>(roster.HomeGoal, true, out var hg))
        svc.Game.HomeGoalTarget = hg;
    if (Enum.TryParse<BlitzballTracker.Core.GameState.Waymark>(roster.AwayGoal, true, out var ag))
        svc.Game.AwayGoalTarget = ag;

    // Add/update players
    if (roster.Players != null)
    {
        foreach (var rp in roster.Players)
        {
            if (string.IsNullOrEmpty(rp.Name)) continue;

            if (!svc.Game.Players.TryGetValue(rp.Name, out var player))
            {
                player = new BlitzballTracker.Core.GameState.PlayerState { Name = rp.Name };
                svc.Game.Players[rp.Name] = player;
            }

            if (!string.IsNullOrEmpty(rp.Team)) player.Team = rp.Team;
            if (Enum.TryParse<BlitzballTracker.Core.GameState.PlayerRole>(rp.Role, true, out var role))
                player.Role = role;
        }
    }

    // Recalculate starting positions based on roles + goal assignments
    svc.Game.ResetPositions();
    svc.NotifyStateChanged();

    return Results.Ok(new { players = svc.Game.Players.Count, home = svc.Game.HomeTeam, away = svc.Game.AwayTeam });
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

/// <summary>DTO for live chat messages from the plugin.</summary>
public record LiveChatMessage(string? Sender, string? Message, DateTime Timestamp);

/// <summary>DTO for roster setup — teams, goals, and player assignments.</summary>
public record RosterSetup(
    string? HomeTeam,
    string? AwayTeam,
    string? HomeGoal,  // e.g. "Four" or "D"
    string? AwayGoal,
    RosterPlayer[]? Players);

public record RosterPlayer(string? Name, string? Team, string? Role);
