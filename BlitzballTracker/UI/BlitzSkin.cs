using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace BlitzballTracker.UI;

using BlitzballTracker.Core.GameState;

/// <summary>
/// The shared painter for every Blitzball window.
///
/// Nothing draws raw ImGui widgets directly. Routing all of it through one small
/// vocabulary is what stops thirty screens from looking like thirty plugins, and it
/// is the entire difference between default ImGui and something that looks designed.
/// </summary>
public static class BlitzSkin
{
    /// <summary>One corner radius everywhere, so every rounded edge is from one family.</summary>
    public const float Radius = 6f;

    private static readonly Vector2 PillPadding = new(7, 2);

    public static bool BeginCard(string id, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BlitzPalette.ToVector(BlitzPalette.BgCard));
        ImGui.PushStyleColor(ImGuiCol.Border, BlitzPalette.ToVector(BlitzPalette.Border));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Radius);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(9, 7));

        return ImGui.BeginChild(id, size, true);
    }

    public static void EndCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    /// <summary>Dim, spaced-out label marking a group of related content.</summary>
    public static void SectionHeading(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, BlitzPalette.ToVector(BlitzPalette.InkDim));
        ImGui.TextUnformatted(text.ToUpperInvariant());
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// A small tinted capsule, mirroring the web app's .pill-ok / .pill-dazed /
    /// .pill-ball / .pill-role family.
    /// </summary>
    public static void Pill(string text, uint color)
    {
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(text);
        var box = textSize + (PillPadding * 2);

        draw.AddRectFilled(origin, origin + box, BlitzPalette.WithAlpha(color, 0.16f), Radius);
        draw.AddText(origin + PillPadding, color, text);

        ImGui.Dummy(box);
    }

    /// <summary>The current phase, tinted to match and outlined.</summary>
    public static void PhaseChip(GamePhase phase)
    {
        var color = BlitzPalette.PhaseColor(phase);
        var label = BlitzPalette.PhaseLabel(phase);

        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(label);
        var box = textSize + (PillPadding * 2);

        draw.AddRectFilled(origin, origin + box, BlitzPalette.WithAlpha(color, 0.14f), Radius);
        draw.AddRect(origin, origin + box, BlitzPalette.WithAlpha(color, 0.55f), Radius);
        draw.AddText(origin + PillPadding, color, label);

        ImGui.Dummy(box);
    }

    /// <summary>
    /// Team names flanking the score. Uses the full width so the score sits centred
    /// regardless of how long the team names are.
    /// </summary>
    public static void ScoreBanner(string homeTeam, string awayTeam, Score score, float scorePulse = 0f)
    {
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        var home = homeTeam.Length > 0 ? homeTeam : "HOME";
        var away = awayTeam.Length > 0 ? awayTeam : "AWAY";
        var scoreText = $"{score.Home} : {score.Away}";

        var homeSize = ImGui.CalcTextSize(home);
        var awaySize = ImGui.CalcTextSize(away);
        var scoreSize = ImGui.CalcTextSize(scoreText);

        var height = MathF.Max(scoreSize.Y, MathF.Max(homeSize.Y, awaySize.Y)) + 8f;

        draw.AddRectFilled(origin, origin + new Vector2(width, height), BlitzPalette.BgCard, Radius);

        var midY = origin.Y + ((height - homeSize.Y) * 0.5f);

        draw.AddText(new Vector2(origin.X + 10f, midY), BlitzPalette.TeamHome, home);
        draw.AddText(new Vector2(origin.X + width - awaySize.X - 10f, midY), BlitzPalette.TeamAway, away);

        // A brief flare when the score changes, so a goal is not a silent swap.
        var scoreColor = scorePulse > 0.01f
            ? BlitzPalette.Gold
            : BlitzPalette.Ink;

        draw.AddText(
            new Vector2(origin.X + ((width - scoreSize.X) * 0.5f), origin.Y + ((height - scoreSize.Y) * 0.5f)),
            scoreColor,
            scoreText);

        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>A thin filled track, for success rates and roll averages.</summary>
    public static void StatBar(float fraction, uint color, float width = 60f, float height = 6f)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);

        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);

        draw.AddRectFilled(origin, origin + size, BlitzPalette.WithAlpha(BlitzPalette.Ink, 0.08f), height * 0.5f);

        if (fraction > 0f)
        {
            draw.AddRectFilled(
                origin,
                origin + new Vector2(width * fraction, height),
                color,
                height * 0.5f);
        }

        ImGui.Dummy(size);
    }

    /// <summary>Body text in the muted ink tier.</summary>
    public static void Muted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, BlitzPalette.ToVector(BlitzPalette.InkDim));
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    public static void MutedWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, BlitzPalette.ToVector(BlitzPalette.InkDim));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
