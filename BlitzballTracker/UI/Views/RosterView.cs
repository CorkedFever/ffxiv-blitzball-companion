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

    /// <summary>
    /// The tracked roster this editor last pulled from, so it pulls again only when
    /// that is genuinely replaced rather than whenever the draft happens to be empty.
    /// </summary>
    private Roster? _syncedFrom;

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

    /// <summary>
    /// Normalise the draft to exactly twelve rows, six a side, home first.
    ///
    /// Which side a row belongs to is its <em>position in the list</em>, never its team
    /// name. Keying on the name meant that with both teams unnamed — which is how the
    /// screen starts — every row matched both columns, so the two squads rendered the
    /// same six objects and a name typed into one appeared in the other. Two teams
    /// given the same name broke it the same way.
    ///
    /// Blitzball is six a side, so anything past twelve is a paste gone wrong and is
    /// dropped. Better to lose a row visibly than to apply a player no column shows.
    /// </summary>
    private void EnsureSlots()
    {
        var home = new List<RosterEntry>(SquadSize);
        var away = new List<RosterEntry>(SquadSize);

        foreach (var entry in _draft.Entries)
        {
            // A row already tagged with a side goes to it; anything untagged fills in
            // order, which is what a blank draft and a pasted sheet both need.
            if (_draft.HomeTeam.Length > 0 && IsOn(entry, _draft.HomeTeam) && home.Count < SquadSize)
                home.Add(entry);
            else if (_draft.AwayTeam.Length > 0 && IsOn(entry, _draft.AwayTeam) && away.Count < SquadSize)
                away.Add(entry);
            else if (home.Count < SquadSize)
                home.Add(entry);
            else if (away.Count < SquadSize)
                away.Add(entry);
        }

        PadSquad(home, _draft.HomeTeam);
        PadSquad(away, _draft.AwayTeam);

        _draft.Entries.Clear();
        _draft.Entries.AddRange(home);
        _draft.Entries.AddRange(away);
    }

    private static void PadSquad(List<RosterEntry> squad, string team)
    {
        for (var i = squad.Count; i < SquadSize; i++)
            squad.Add(new RosterEntry { Team = team, Role = RoleOrder[i] });

        // The tag follows the column, so renaming a team moves nobody between sides.
        foreach (var entry in squad)
            entry.Team = team;
    }

    private static bool IsOn(RosterEntry entry, string team) =>
        entry.Team.Equals(team, StringComparison.OrdinalIgnoreCase);

    public void Draw()
    {
        // Show what is actually being tracked. Otherwise a roster applied elsewhere
        // (a simulated match, a recording that carried its own, a substitution) leaves
        // this screen looking blank, as though nothing were loaded at all.
        //
        // Keyed on the roster *instance* changing, not on the draft being empty. The
        // latter meant Clear undid itself: emptying the draft made it empty, which was
        // the condition to refill it from the live roster on the very next frame.
        if (!ReferenceEquals(_syncedFrom, _state.CurrentRoster) &&
            _state.CurrentRoster is { NamedCount: > 0 } live)
        {
            _syncedFrom = _state.CurrentRoster;
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
        DrawSubstitution();
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
            _draft.HomeTeam = home;
            RetagSquad(isHome: true);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("##away", "Away team (defends 4)", ref away, 64))
        {
            _draft.AwayTeam = away;
            RetagSquad(isHome: false);
        }
    }

    /// <summary>
    /// Mark the draft as the user's, so the tracked roster is not pulled back over it.
    ///
    /// Everything that replaces the draft deliberately — clearing, pasting, loading a
    /// preset, detecting a formation — says so here. Otherwise a roster arriving from
    /// elsewhere mid-edit would quietly overwrite the work.
    /// </summary>
    private void ClaimDraft() => _syncedFrom = _state.CurrentRoster;

    /// <summary>
    /// Point a column's rows at their team name.
    ///
    /// By position, so renaming a side never moves anybody across — which is what
    /// happened when both sides were unnamed and every row matched every name.
    /// </summary>
    private void RetagSquad(bool isHome)
    {
        var team = isHome ? _draft.HomeTeam : _draft.AwayTeam;
        var start = isHome ? 0 : SquadSize;

        for (var i = start; i < start + SquadSize && i < _draft.Entries.Count; i++)
            _draft.Entries[i].Team = team;
    }

    private void DrawSquads()
    {
        if (!ImGui.BeginTable("squads", 2, ImGuiTableFlags.BordersInnerV))
            return;

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawSquad(isHome: true);

        ImGui.TableNextColumn();
        DrawSquad(isHome: false);

        ImGui.EndTable();
    }

    private void DrawSquad(bool isHome)
    {
        var team = isHome ? _draft.HomeTeam : _draft.AwayTeam;
        var idPrefix = isHome ? "home" : "away";

        ImGui.TextColored(
            BlitzPalette.ToVector(isHome ? BlitzPalette.TeamHome : BlitzPalette.TeamAway),
            team.Length > 0 ? team : "(unnamed team)");

        ImGui.Spacing();

        // By position, not by name: the two columns must never be able to show the
        // same row.
        var start = isHome ? 0 : SquadSize;

        for (var slot = 0; slot < SquadSize; slot++)
        {
            var index = start + slot;
            if (index >= _draft.Entries.Count) break;

            var entry = _draft.Entries[index];

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
                "Read team and role from where everyone is standing right now,\n" +
                "and start tracking them immediately.\n" +
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

            // Stay cleared. Without this the tracked roster would be pulled straight
            // back in, because an empty draft used to be the signal to refill it.
            _syncedFrom = _state.CurrentRoster;

            _status = "Draft cleared. The active roster is unchanged until you apply.";
        }
    }

    private int _subOutIndex;
    private string _subInName = string.Empty;

    /// <summary>
    /// Swap one player for another without disturbing the match.
    ///
    /// Kept apart from the roster editor above on purpose. Applying an edited roster
    /// rebuilds every player from scratch, which mid-match means losing every stat
    /// earned so far and sending both sides back to their kickoff formation. A
    /// substitution has to be surgical, and teams do make them — commonly at halftime.
    /// </summary>
    private void DrawSubstitution()
    {
        if (!_state.HasRoster || !_state.IsActive) return;

        ImGui.Spacing();
        BlitzSkin.SectionHeading("Substitution");

        BlitzSkin.MutedWrapped(
            "Swaps a player mid-match without resetting anything. The substitute takes " +
            "over the role and the place on the field; the stats stay with whoever earned them.");

        ImGui.Spacing();

        var onField = _state.Players.Values
            .Where(p => !p.IsSubstituted)
            .OrderBy(p => p.Team, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (onField.Count == 0) return;

        _subOutIndex = Math.Clamp(_subOutIndex, 0, onField.Count - 1);

        var labels = onField
            .Select(p => $"{p.Name} ({Roster.RoleAbbreviation(p.Role)}, {p.Team})")
            .ToArray();

        ImGui.SetNextItemWidth(260);
        ImGui.Combo("##suboff", ref _subOutIndex, labels, labels.Length);

        ImGui.SameLine();
        BlitzSkin.Muted("comes off for");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.InputTextWithHint("##subon", "name coming on", ref _subInName, 64);

        ImGui.SameLine();
        if (ImGui.Button("Substitute", new Vector2(110, 0)))
            ApplySubstitution(onField[_subOutIndex]);
    }

    private void ApplySubstitution(PlayerState outgoing)
    {
        var incoming = _subInName.Trim();

        if (incoming.Length == 0)
        {
            _status = "Enter the name of the player coming on.";
            return;
        }

        if (!_state.Substitute(outgoing.Name, incoming))
        {
            _status = $"Could not substitute: {incoming} may already be on the roster.";
            return;
        }

        _parser.ClearUnmatchedNames();

        // The tracked roster is now the authority, so the editor and the saved copy
        // both follow it rather than drifting from what is actually on the field.
        if (_state.CurrentRoster is { } updated)
        {
            _syncedFrom = updated;
            _draft = updated.Clone();
            EnsureSlots();

            _config.LastRoster = updated;
            _pluginInterface.SavePluginConfig(_config);

            _liveFeed.SendRoster(updated);
        }

        _state.PlayByPlay.Add(
            $"[{DateTime.Now:HH:mm:ss}] Substitution: {incoming} comes on for {outgoing.Name} " +
            $"at {Roster.RoleAbbreviation(outgoing.Role)}.");

        _status = $"{incoming} on for {outgoing.Name}.";
        _subInName = string.Empty;
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
        ClaimDraft();

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
                ClaimDraft();

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

        // What is on screen is what was just applied, so there is nothing to pull back.
        _syncedFrom = _state.CurrentRoster;

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
        ClaimDraft();

        // Applied outright rather than left sitting in the draft. This is pressed in the
        // minute before kickoff with everyone already standing in formation, and a read
        // that needs a second click to take effect is a read that gets forgotten — which
        // costs the whole match, because an unapplied roster means nobody is recognised.
        // The draft stays editable, so a correction is still a change away.
        Apply();

        // Apply reports its own failures, and those are the more useful message.
        if (_state.HasRoster)
        {
            _status =
                $"Read {detected.Entries.Count} players off the field and applied them. " +
                "Check the roles below if anyone was standing oddly.";
        }
    }
}
