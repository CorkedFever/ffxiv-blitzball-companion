using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BlitzballTracker.Windows;

using BlitzballTracker.UI;
using BlitzballTracker.UI.Views;

/// <summary>
/// The single window everything lives in.
///
/// Rather than a scatter of windows toggled by typed commands, this is one shell
/// with a navigation rail: pick a screen, it slides in. The selection indicator and
/// the transition are spring-driven, which is most of what separates an overlay
/// that feels considered from one that feels like a debug panel.
/// </summary>
public sealed class ShellWindow : Window
{
    private const float NavWidth = 148f;
    private const float ItemHeight = 34f;

    private readonly IShellView[] _views;

    private int _current;
    private int _direction = 1;

    private Spring _indicator;
    private Spring _transition;

    public ShellWindow(IShellView[] views)
        : base("Blitzball Companion", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _views = views;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680, 440),
            MaximumSize = new Vector2(1400, 1100),
        };

        _indicator.Snap(0f);
        _transition.Snap(0f);
    }

    /// <summary>Jump to a named screen, for shortcuts that still exist.</summary>
    public void Navigate(string title)
    {
        var index = Array.FindIndex(_views, v => v.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) Select(index);

        IsOpen = true;
    }

    private void Select(int index)
    {
        if (index == _current) return;

        _direction = index > _current ? 1 : -1;
        _current = index;

        // Restart the slide. It runs back to zero, so the incoming screen settles.
        _transition.Snap(1f);
    }

    public override void Draw()
    {
        var delta = ImGui.GetIO().DeltaTime;

        DrawNavigation(delta);

        ImGui.SameLine();

        DrawContent(delta);
    }

    private void DrawNavigation(float delta)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BlitzPalette.ToVector(BlitzPalette.BgDark));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, BlitzSkin.Radius);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 8));

        if (ImGui.BeginChild("nav", new Vector2(NavWidth, 0), true))
        {
            var draw = ImGui.GetWindowDrawList();
            var origin = ImGui.GetCursorScreenPos();

            // The highlight slides to the selected item rather than jumping.
            _indicator.Update(_current * ItemHeight, delta);

            var indicatorTop = new Vector2(origin.X - 2f, origin.Y + _indicator.Value + 4f);
            draw.AddRectFilled(
                indicatorTop,
                indicatorTop + new Vector2(3f, ItemHeight - 8f),
                BlitzPalette.Accent,
                2f);

            for (var i = 0; i < _views.Length; i++)
                DrawNavItem(draw, origin, i);
        }

        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private void DrawNavItem(ImDrawListPtr draw, Vector2 origin, int index)
    {
        var view = _views[index];
        var selected = index == _current;

        var top = origin + new Vector2(0f, index * ItemHeight);
        var size = new Vector2(NavWidth - 20f, ItemHeight - 4f);

        ImGui.SetCursorScreenPos(top);
        ImGui.PushID(index);

        if (ImGui.InvisibleButton("##nav", size))
            Select(index);

        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        if (hovered && !selected)
            draw.AddRectFilled(top, top + size, BlitzPalette.BgCardHover, BlitzSkin.Radius);
        else if (selected)
            draw.AddRectFilled(top, top + size, BlitzPalette.BgCard, BlitzSkin.Radius);

        var textColor = selected ? BlitzPalette.Ink : BlitzPalette.InkDim;
        var textY = top.Y + ((size.Y - ImGui.GetTextLineHeight()) * 0.5f);

        draw.AddText(new Vector2(top.X + 8f, textY), textColor, view.Icon);
        draw.AddText(new Vector2(top.X + 28f, textY), textColor, view.Title);

        var badge = view.Badge;
        if (string.IsNullOrEmpty(badge)) return;

        var badgeSize = ImGui.CalcTextSize(badge);
        var badgePos = new Vector2(top.X + size.X - badgeSize.X - 10f, textY);

        draw.AddRectFilled(
            badgePos - new Vector2(5f, 2f),
            badgePos + badgeSize + new Vector2(5f, 2f),
            BlitzPalette.WithAlpha(BlitzPalette.Warning, 0.2f),
            BlitzSkin.Radius);

        draw.AddText(badgePos, BlitzPalette.Warning, badge);
    }

    private void DrawContent(float delta)
    {
        _transition.Update(0f, delta, 18f);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, BlitzPalette.ToVector(BlitzPalette.BgDark));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, BlitzSkin.Radius);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));

        if (ImGui.BeginChild("content", new Vector2(0, 0), true))
        {
            var view = _views[_current];

            ImGui.PushStyleColor(ImGuiCol.Text, BlitzPalette.ToVector(BlitzPalette.Ink));
            ImGui.TextUnformatted($"{view.Icon}  {view.Title}");
            ImGui.PopStyleColor();

            ImGui.Separator();
            ImGui.Spacing();

            // Slide the incoming screen in from the side it came from, and fade it
            // up, so switching reads as movement rather than a hard cut.
            var slide = _transition.Value * 26f * _direction;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + slide);

            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(1f - _transition.Value, 0.05f, 1f));

            try
            {
                view.Draw();
            }
            catch (Exception)
            {
                // A view that throws must not take the whole window down with it.
                ImGui.TextColored(BlitzPalette.ToVector(BlitzPalette.Danger),
                    "This screen hit an error. See /xllog for details.");
                throw;
            }
            finally
            {
                ImGui.PopStyleVar();
            }
        }

        ImGui.EndChild();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }
}
