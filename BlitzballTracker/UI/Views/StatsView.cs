using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;

namespace BlitzballTracker.UI.Views;

using BlitzballTracker.Core.GameState;

/// <summary>
/// Per-player numbers for the current match.
/// </summary>
public sealed class StatsView(BlitzGame state) : IShellView
{
    public string Title => "Stats";
    public string Icon => ((char)SeIconChar.ExperienceFilled).ToString();

    private readonly BlitzGame _state = state;

    /// <summary>Reused each frame so sorting does not allocate a list per draw.</summary>
    private readonly List<PlayerState> _ordered = new(16);

    private static readonly Comparison<PlayerState> ByTeamThenRole = static (a, b) =>
    {
        var team = string.Compare(a.Team, b.Team, StringComparison.OrdinalIgnoreCase);
        if (team != 0) return team;

        var role = a.Role.CompareTo(b.Role);
        return role != 0 ? role : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    };

    public void Draw()
    {
        if (!_state.HasRoster)
        {
            BlitzSkin.MutedWrapped("Load a roster to see per-player numbers.");
            return;
        }

        _ordered.Clear();
        _ordered.AddRange(_state.Players.Values);
        _ordered.Sort(ByTeamThenRole);

        if (!ImGui.BeginTable("stats", 10,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Doing", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableSetupColumn("Success", ImGuiTableColumnFlags.WidthFixed, 84f);
        ImGui.TableSetupColumn("Avg roll", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableSetupColumn("Goals", ImGuiTableColumnFlags.WidthFixed, 46f);
        ImGui.TableSetupColumn("Saves", ImGuiTableColumnFlags.WidthFixed, 46f);
        ImGui.TableHeadersRow();

        foreach (var player in _ordered)
            DrawRow(player);

        ImGui.EndTable();
    }

    private void DrawRow(PlayerState player)
    {
        var isHome = player.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase);
        var teamColor = BlitzPalette.TeamColor(isHome);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextColored(BlitzPalette.ToVector(teamColor), player.Name);

        ImGui.TableNextColumn();
        BlitzSkin.Muted(Roster.RoleAbbreviation(player.Role));

        ImGui.TableNextColumn();
        var status = BlitzIcons.StatusFor(player);
        if (status.Length > 0)
            ImGui.TextColored(BlitzPalette.ToVector(BlitzIcons.StatusColor(player)), status);
        else
            BlitzSkin.Muted("-");

        ImGui.TableNextColumn();
        var declared = _state.CurrentActionFor(player.Name);
        if (declared is not null && BlitzIcons.ActionLabel(declared.Action).Length > 0)
        {
            ImGui.TextColored(
                BlitzPalette.ToVector(BlitzIcons.OutcomeColor(declared.Outcome)),
                BlitzIcons.ActionLabel(declared.Action));
        }
        else
        {
            BlitzSkin.Muted("-");
        }

        ImGui.TableNextColumn();
        BlitzSkin.Muted(BlitzsphereLayout.Label(player.Position));

        ImGui.TableNextColumn();
        BlitzSkin.Muted(player.ActionsAttempted > 0
            ? $"{player.ActionsSucceeded}/{player.ActionsAttempted}"
            : "-");

        // A bar reads faster than a percentage when scanning a whole team.
        ImGui.TableNextColumn();
        if (player.ActionsAttempted > 0)
        {
            BlitzSkin.StatBar((float)player.SuccessRate, SuccessColor(player.SuccessRate), 52f);
            ImGui.SameLine();
            BlitzSkin.Muted($"{player.SuccessRate * 100f:0}%");
        }
        else
        {
            BlitzSkin.Muted("-");
        }

        ImGui.TableNextColumn();
        BlitzSkin.Muted(player.TotalRolls > 0 ? $"{player.RollAverage:0.0}" : "-");

        ImGui.TableNextColumn();
        if (player.Goals > 0)
            ImGui.TextColored(BlitzPalette.ToVector(BlitzPalette.Gold), player.Goals.ToString());
        else
            BlitzSkin.Muted("-");

        ImGui.TableNextColumn();
        if (player.Saves > 0)
            ImGui.TextColored(BlitzPalette.ToVector(BlitzPalette.Accent), player.Saves.ToString());
        else
            BlitzSkin.Muted("-");
    }

    private static uint SuccessColor(double rate) => rate switch
    {
        >= 0.6 => BlitzPalette.Success,
        >= 0.35 => BlitzPalette.Warning,
        _ => BlitzPalette.Danger,
    };
}
