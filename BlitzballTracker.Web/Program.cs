using BlitzballTracker.Core.Parsing;
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

// The lineup the plugin is tracking with, in the same header format recordings use.
//
// Sharing that format rather than inventing a DTO means one parser and one set of
// rules on both sides. The previous version built PlayerState objects by hand and
// never set CurrentRoster — which is what ChatParser keys its name index off, so the
// index was never built and every player name in the feed was discarded. Phases and
// the scoreboard worked, the field stayed empty, and it looked like a rendering fault.
app.MapPost("/api/live/roster", async (HttpRequest request, LiveService svc) =>
{
    using var reader = new StreamReader(request.Body);
    var header = await reader.ReadToEndAsync();

    var roster = RosterHeader.Read(header.Split('\n'));

    if (roster is null)
        return Results.BadRequest("No roster found in the payload.");

    svc.ApplyRoster(roster);

    return Results.Ok(new
    {
        players = svc.Game.Players.Count,
        home = svc.Game.HomeTeam,
        away = svc.Game.AwayTeam,
    });
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

/// <summary>DTO for live chat messages from the plugin.</summary>
public record LiveChatMessage(string? Sender, string? Message, DateTime Timestamp);
