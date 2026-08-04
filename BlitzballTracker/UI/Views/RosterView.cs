using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Plugin;

namespace BlitzballTracker.UI.Views;

using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;

/// <summary>
/// Enter the two team sheets before kickoff.
///
/// The roster cannot be recovered from chat: logs carry no structured lineup, and
/// roles only surface informally and partially. Without it the parser cannot tell a
/// player from a spectator. Everything here optimises for the minute before a match,
/// because that is when it gets used.
/// </summary>
public sealed class RosterView : IShellView
{
    private const int SquadSize = 6;

    private static readonly PlayerRole[] RoleOrder =
    [
        PlayerRole.Goalkeeper,
        PlayerRole.Midfield,
        PlayerRole.LeftForward,
        PlayerRole.RightForward,
        PlayerRole.LeftDefender,
        PlayerRole.RightDefender,
    ];

    private static readonly string[] RoleLabels = ["GK", "M", "LF", "RF", "LD", "RD"];

    private readonly BlitzGame _state;
    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly WaymarkReader _waymarks;
    private readonly ChatParser _parser;

    private Roster _draft;
    private string _pasteBuffer = string.Empty;
    private string _presetName = string.Empty;
    private string _status = string.Empty;
    private bool _showPaste;

    public string Title => "Roster";
    public string Icon => ((char)SeIconChar.BoxedQuestionMark).ToString();

    /// <summary>Surface unmatched names on the nav rail: a missing player should be loud.</summary>
    public string? Badge => _parser.UnmatchedNames.Count > 0
        ? _parser.UnmatchedNames.Count.ToString()
        : null;

    private readonly LiveFeedClient _liveFeed;

    public RosterView(
        BlitzGame state,
        Configuration config,
        IDalamudPluginInterface pluginInterface,
        WaymarkReader waymarks,
        ChatParser parser,
        LiveFeedClient liveFeed)
    {
        _state = state;
        _config = config;
        _pluginInterface = pluginInterface;
        _waymarks = waymarks;
        _parser = parser;
        _liveFeed = liveFeed;

        _draft = (_config.LastRoster ?? new Roster()).Clone();
        EnsureSlots();
    }

    /// <summary>Pad both squads to six rows so the grid is stable while editing.</summary>
    private void EnsureSlots()
    {
        foreach (var isHome in new[] { true, false })
        {
            var teamName = isHome ? _draft.HomeTeam : _draft.AwayTeam;
            var count = _draft.Entries.Count(e => IsOn(e, teamName));

            for (var i = count; i < SquadSize; i++)
            {
                _draft.Entries.Add(new RosterEntry
                {
                    Team = teamName,
                    Role = RoleOrder[Math.Min(i, RoleOrder.Length - 1)],
                });
            }
        }
    }

    private static bool IsOn(RosterEntry entry, string team) =>
        entry.Team.Equals(team, StringComparison.OrdinalIgnoreCase);

    public void Draw()
    {
        // Show what is actually being tracked. Otherwise a roster applied elsewhere
        // (a simulated match, a recording that carried its own) leaves this screen
        // looking blank, as though nothing were loaded at all.
        if (_draft.NamedCount == 0 && _state.CurrentRoster is { NamedCount: > 0 } live)
        {
            _draft = live.Clone();
            EnsureSlots();
        }

        DrawTeamNames();
        ImGui.Spacing();

        DrawSquads();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawActions();
        DrawPasteImport();
        DrawPresets();
        DrawValidation();
        DrawUnmatchedNames();

        if (_status.Length == 0) return;

        ImGui.Spacing();
        BlitzSkin.MutedWrapped(_status);
    }

    private void DrawTeamNames()
    {
        var home = _draft.HomeTeam;
        var away = _draft.AwayTeam;

        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("##home", "Home team (defends D)", ref home, 64))
        {
            RenameTeam(_draft.HomeTeam, home);
            _draft.HomeTeam = home;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("##away", "Away team (defends 4)", ref away, 64))
        {
            RenameTeam(_draft.AwayTeam, away);
            _draft.AwayTeam = away;
        }
    }

    private void RenameTeam(string oldName, string newName)
    {
        foreach (var entry in _draft.Entries)
        {
            if (IsOn(entry, oldName))
                entry.Team = newName;
        }
    }

    private void DrawSquads()
    {
        if (!ImGui.BeginTable("squads", 2, ImGuiTableFlags.BordersInnerV))
            return;

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawSquad(_draft.HomeTeam, "home");

        ImGui.TableNextColumn();
        DrawSquad(_draft.AwayTeam, "away");

        ImGui.EndTable();
    }

    private void DrawSquad(string team, string idPrefix)
    {
        ImGui.TextColored(
            BlitzPalette.ToVector(idPrefix == "home" ? BlitzPalette.TeamHome : BlitzPalette.TeamAway),
            team.Length > 0 ? team : "(unnamed team)");

        ImGui.Spacing();

        var slot = 0;
        foreach (var entry in _draft.Entries)
        {
            if (!IsOn(entry, team)) continue;

            ImGui.PushID($"{idPrefix}-{slot}");

            var role = Math.Max(0, Array.IndexOf(RoleOrder, entry.Role));
            ImGui.SetNextItemWidth(58);
            if (ImGui.Combo("##role", ref role, RoleLabels, RoleLabels.Length))
                entry.Role = RoleOrder[role];

            ImGui.SameLine();
            var name = entry.Name;
            ImGui.SetNextItemWidth(190);
            if (ImGui.InputTextWithHint("##name", "character name", ref name, 64))
            {
                entry.Name = PlayerNames.StripWorld(name);
                entry.World = PlayerNames.ExtractWorld(name) ?? entry.World;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Nearby"))
                ImGui.OpenPopup("nearby");

            DrawNearbyPicker(entry);

            ImGui.PopID();
            slot++;
        }
    }

    /// <summary>
    /// Pick a name from the characters actually standing around you. At a match the
    /// players are right there, so this beats typing twelve names correctly.
    /// </summary>
    private void DrawNearbyPicker(RosterEntry entry)
    {
        if (!ImGui.BeginPopup("nearby")) return;

        var taken = _draft.Entries
            .Where(e => e != entry && e.Name.Length > 0)
            .Select(e => PlayerNames.Normalize(e.Name))
            .ToHashSet(StringComparer.Ordinal);

        var any = false;
        foreach (var candidate in _waymarks.ReadNearbyPlayers())
        {
            var clean = PlayerNames.StripWorld(candidate.Name);
            if (taken.Contains(PlayerNames.Normalize(clean))) continue;

            any = true;
            if (ImGui.Selectable(clean))
            {
                entry.Name = clean;
                ImGui.CloseCurrentPopup();
            }
        }

        if (!any)
            ImGui.TextDisabled("No unassigned players nearby.");

        ImGui.EndPopup();
    }

    private void DrawActions()
    {
        if (ImGui.Button("Apply roster", new Vector2(130, 0)))
            Apply();

        ImGui.SameLine();
        if (ImGui.Button("Detect from formation", new Vector2(170, 0)))
            DetectFormation();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Read team and role from where everyone is standing right now.\n" +
                "Only meaningful at kickoff, before anyone has moved.");
        }

        ImGui.SameLine();
        if (ImGui.Button(_showPaste ? "Hide paste" : "Paste sheet", new Vector2(110, 0)))
            _showPaste = !_showPaste;

        ImGui.SameLine();
        if (ImGui.Button("Clear", new Vector2(80, 0)))
        {
            _draft = new Roster { HomeTeam = _draft.HomeTeam, AwayTeam = _draft.AwayTeam };
            EnsureSlots();
            _status = "Draft cleared. The active roster is unchanged until you apply.";
        }
    }

    private void DrawPasteImport()
    {
        if (!_showPaste) return;

        ImGui.Spacing();
        BlitzSkin.Muted("One player per line: \"Name - GK\", \"GK: Name\", or \"Name / M\".");
        BlitzSkin.Muted("A line with no role starts a new team.");

        ImGui.InputTextMultiline("##paste", ref _pasteBuffer, 4096, new Vector2(-1, 110));

        if (!ImGui.Button("Import", new Vector2(130, 0))) return;

        var parsed = Roster.ParseFromText(_pasteBuffer);
        if (parsed.Entries.Count == 0)
        {
            _status = "Nothing recognised in that text.";
            return;
        }

        _draft = parsed;
        EnsureSlots();
        _status = $"Imported {parsed.Entries.Count} players. Review, then apply.";
    }

    private void DrawPresets()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        BlitzSkin.SectionHeading("Presets");
        BlitzSkin.Muted("League sides recur, so enter a team once and reuse it.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(180);
        ImGui.InputTextWithHint("##preset", "preset name", ref _presetName, 64);

        ImGui.SameLine();
        if (ImGui.Button("Save preset") && _presetName.Trim().Length > 0)
        {
            _config.SavedRosters[_presetName.Trim()] = _draft.Clone();
            _pluginInterface.SavePluginConfig(_config);
            _status = $"Saved preset '{_presetName.Trim()}'.";
        }

        if (_config.SavedRosters.Count == 0) return;

        ImGui.SameLine();
        if (!ImGui.BeginCombo("##presets", "Load preset")) return;

        string? remove = null;

        foreach (var (name, roster) in _config.SavedRosters)
        {
            if (ImGui.Selectable(name))
            {
                _draft = roster.Clone();
                EnsureSlots();
                _presetName = name;
                _status = $"Loaded preset '{name}'. Review, then apply.";
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                remove = name;
        }

        ImGui.EndCombo();

        if (remove is null) return;

        _config.SavedRosters.Remove(remove);
        _pluginInterface.SavePluginConfig(_config);
        _status = $"Deleted preset '{remove}'.";
    }

    /// <summary>
    /// Problems are warnings, never blockers. Matches genuinely run short-handed:
    /// one recorded game contains a mid-match disconnect.
    /// </summary>
    private void DrawValidation()
    {
        var problems = _draft.Validate();
        if (problems.Count == 0) return;

        ImGui.Spacing();
        ImGui.TextColored(BlitzPalette.ToVector(BlitzPalette.Warning), "Check before applying:");

        foreach (var problem in problems)
            ImGui.BulletText(problem);
    }

    /// <summary>
    /// Names that acted like players but are not on the sheet. Expected to hold
    /// referees and commentators. A real player appearing here means their actions
    /// are being dropped, which should be loud rather than silent.
    /// </summary>
    private void DrawUnmatchedNames()
    {
        if (_parser.UnmatchedNames.Count == 0) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!ImGui.CollapsingHeader($"Seen but not on the roster ({_parser.UnmatchedNames.Count})"))
            return;

        BlitzSkin.Muted("Referees and commentators belong here. Players do not.");

        foreach (var (name, hits) in _parser.UnmatchedNames.OrderByDescending(kv => kv.Value))
        {
            ImGui.PushID(name);
            if (ImGui.SmallButton("Add"))
            {
                var slot = _draft.Entries.FirstOrDefault(e => e.Name.Length == 0);
                if (slot is not null)
                {
                    slot.Name = name;
                    _status = $"Added {name} to the draft. Set their team and role, then apply.";
                }
                else
                {
                    _status = "No empty slot free. Clear one first.";
                }
            }
            ImGui.PopID();

            ImGui.SameLine();
            BlitzSkin.Muted($"{name}  ({hits})");
        }
    }

    private void Apply()
    {
        var applied = _draft.Clone();
        applied.Entries.RemoveAll(e => string.IsNullOrWhiteSpace(e.Name));

        if (applied.Entries.Count == 0)
        {
            _status = "Nothing to apply: no names entered.";
            return;
        }

        _state.ApplyRoster(applied);
        _parser.ClearUnmatchedNames();

        // Keep the broadcast in step. A substitution mid-match is exactly when the
        // overlay would otherwise start dropping a player's actions on the floor.
        _liveFeed.SendRoster(applied);

        _config.LastRoster = applied;
        _pluginInterface.SavePluginConfig(_config);

        _status = $"Tracking {applied.Entries.Count} players. {_state.HomeTeam} vs {_state.AwayTeam}.";
    }

    private void DetectFormation()
    {
        var home = _draft.HomeTeam.Length > 0 ? _draft.HomeTeam : "HOME";
        var away = _draft.AwayTeam.Length > 0 ? _draft.AwayTeam : "AWAY";

        var detected = _waymarks.DetectFormation(home, away);

        if (detected is null)
        {
            _status = _waymarks.ArenaReady
                ? "No players found standing on waymarks."
                : "Waymarks D and 4 must both be placed before the formation can be read.";
            return;
        }

        _draft = detected;
        _draft.HomeTeam = home;
        _draft.AwayTeam = away;
        EnsureSlots();

        _status = $"Read {detected.Entries.Count} players from the field. Check names and roles, then apply.";
    }
}
