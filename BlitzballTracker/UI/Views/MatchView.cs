using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;

namespace BlitzballTracker.UI.Views;

using BlitzballTracker.Core.GameState;

/// <summary>
/// The live match: scoreboard, phase, the Blitzsphere field view, and play-by-play.
/// </summary>
public sealed class MatchView(
    BlitzGame state,
    GameRecorder recorder,
    LiveFeedClient liveFeed,
    MatchDriver driver) : IShellView
{
    public string Title => "Match";
    public string Icon => ((char)SeIconChar.Circle).ToString();

    private readonly BlitzGame _state = state;
    private readonly GameRecorder _recorder = recorder;
    private readonly LiveFeedClient _liveFeed = liveFeed;
    private readonly MatchDriver _driver = driver;

    private readonly BlitzsphereWidget _sphere = new(state);

    private Score _lastScore;
    private Spring _scorePulse;
    private int _lastPlayerCount;

    /// <summary>Refilled and sorted in place each frame, never rebuilt.</summary>
    private readonly List<(PlayerState Player, int Effective, int Roll, int Modifier)> _rollers = new(12);

    private readonly Dictionary<string, string> _shortNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Highest effective roll first, then by name so ties do not jitter.</summary>
    private static readonly Comparison<(PlayerState Player, int Effective, int Roll, int Modifier)> ByEffectiveRoll =
        static (a, b) => a.Effective != b.Effective
            ? b.Effective.CompareTo(a.Effective)
            : string.Compare(a.Player.Name, b.Player.Name, StringComparison.OrdinalIgnoreCase);

    public void Draw()
    {
        // A roster swap invalidates cached labels and in-flight spring motion.
        if (_state.Players.Count != _lastPlayerCount)
        {
            _sphere.Invalidate();
            _shortNames.Clear();
            _lastPlayerCount = _state.Players.Count;
        }

        DrawStatusPills();

        // Tracking without a team sheet used to adopt spectators as players and pile
        // them all onto Center, so say so rather than showing confident nonsense.
        if (!_state.HasRoster)
        {
            DrawEmptyState(
                "No roster loaded",
                "Without a team sheet the tracker cannot tell players from spectators. " +
                "Set one up on the Roster screen, or place a practice arena from the Lab.");
            return;
        }

        // A finished match still shows: the final score and where everyone ended up are
        // the most interesting the board gets all game.
        if (!_state.IsActive && !_state.IsFinished)
        {
            DrawEmptyState(
                "Waiting for kickoff",
                "Tracking begins when << STANDBY FOR BLITZOFF >> appears in Yell chat. " +
                "To try it now, run a simulated match from the Lab.");
            return;
        }

        DrawScoreboard();
        ImGui.Spacing();

        DrawPhaseRow();
        ImGui.Spacing();

        DrawRollStrip();

        _sphere.Draw();
        ImGui.Spacing();

        DrawPlayByPlay();
    }

    private static void DrawEmptyState(string headline, string detail)
    {
        ImGui.Spacing();
        ImGui.TextColored(BlitzPalette.ToVector(BlitzPalette.Warning), headline);
        ImGui.Spacing();
        BlitzSkin.MutedWrapped(detail);
    }

    private void DrawStatusPills()
    {
        var drew = false;

        if (_recorder.IsRecording)
        {
            BlitzSkin.Pill($"● REC {_recorder.LinesRecorded}", BlitzPalette.Danger);
            drew = true;
        }

        if (_liveFeed.IsActive)
        {
            if (drew) ImGui.SameLine();
            BlitzSkin.Pill($"● LIVE {_liveFeed.MessagesSent}", BlitzPalette.Success);
            drew = true;
        }

        if (_driver.IsPlaying)
        {
            if (drew) ImGui.SameLine();
            BlitzSkin.Pill($"▶ PLAYBACK {_driver.Progress * 100f:0}%", BlitzPalette.Accent);
            drew = true;
        }

        if (_driver.DemoEnabled)
        {
            if (drew) ImGui.SameLine();
            BlitzSkin.Pill("PRACTICE ARENA", BlitzPalette.Purple);
            drew = true;
        }

        if (drew)
            ImGui.Spacing();
    }

    private void DrawScoreboard()
    {
        // Flare briefly when the score moves, so a goal is not a silent swap.
        if (!_state.Score.Equals(_lastScore))
        {
            _scorePulse.Snap(1f);
            _lastScore = _state.Score;
        }

        _scorePulse.Update(0f, ImGui.GetIO().DeltaTime, 5f);

        BlitzSkin.ScoreBanner(_state.HomeTeam, _state.AwayTeam, _state.Score, _scorePulse.Value);
    }

    private void DrawPhaseRow()
    {
        BlitzSkin.PhaseChip(_state.Phase);

        // Nothing below is live any more, so the row stops pretending it is: no clock
        // counting down, no carrier waiting to act.
        if (_state.IsFinished)
        {
            ImGui.SameLine();
            BlitzSkin.Pill("FULL TIME", BlitzPalette.Gold);

            ImGui.SameLine();
            BlitzSkin.Muted($"Set {_state.Set}  ·  {_state.Round} rounds played");
            return;
        }

        // Overtime is its own scoreboard: the shootout tally is not match goals, and
        // showing only the match score during one hides the thing being decided.
        if (_state.Phase == GamePhase.Shootout)
        {
            ImGui.SameLine();
            BlitzSkin.Pill(
                $"SHOOTOUT {_state.ShootoutScore.Home}–{_state.ShootoutScore.Away}  " +
                $"({_state.ShootoutAttempts.Count}/{BlitzGame.ShootoutAttemptsPerSide * 2})",
                BlitzPalette.Gold);

            if (_state.NextShooter() is { } next)
            {
                ImGui.SameLine();
                BlitzSkin.Muted($"up next: {next.Team} {Roster.RoleAbbreviation(next.Role)}");
            }

            return;
        }

        if (_state.SuddenDeath is { } duel)
        {
            ImGui.SameLine();
            BlitzSkin.Pill("SUDDEN DEATH", BlitzPalette.Danger);

            ImGui.SameLine();
            BlitzSkin.Muted(duel.HolderBlocked
                ? $"{duel.Holder} is blocked and must force the shot"
                : $"{duel.Holder} shoots — unopposed wins it");

            return;
        }

        ImGui.SameLine();
        DrawPhaseClock();

        ImGui.SameLine();
        BlitzSkin.Muted($"Set {_state.Set}  ·  Round {_state.Round}/10");

        if (_state.RoundsRemaining > 0)
        {
            ImGui.SameLine();
            BlitzSkin.Pill($"{_state.RoundsRemaining} to buzzer", BlitzPalette.Warning);
        }

        ImGui.SameLine();
        if (_state.BallCarrier is not null)
            BlitzSkin.Pill(_state.BallCarrier, BlitzPalette.Ball);
        else
            BlitzSkin.Pill("contested", BlitzPalette.InkDim);

        // Worth shouting about: on the last round's inner turn, and at the buzzer,
        // the carrier has no other legal action.
        if (_state.BallCarrierMustShoot && _state.BallCarrier is not null)
        {
            ImGui.SameLine();
            BlitzSkin.Pill("MUST SHOOT", BlitzPalette.Danger);
        }

        // A keeper holds play up until they send it back out, so everyone is waiting
        // on them and ought to be able to see that.
        if (_state.KeeperMustClear)
        {
            ImGui.SameLine();
            BlitzSkin.Pill("MUST PASS OUT", BlitzPalette.Warning);
        }

        // A loose ball is the one moment where several people owe a roll at once and
        // nobody can see whose it is waiting on.
        if (_state.Fumble is { } loose)
        {
            ImGui.SameLine();
            BlitzSkin.Pill($"FUMBLE {loose.Zone} — {loose.Rolls.Count}/{loose.Contenders.Count}", BlitzPalette.Danger);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Still to roll:\n{string.Join("\n", loose.Outstanding)}");
        }

        // The set has not ended yet — somebody still has a shot coming, which is easy
        // to miss when everyone assumes the buzzer was the end of it.
        if (_state.BuzzerShot is { } chain)
        {
            ImGui.SameLine();
            BlitzSkin.Pill($"BUZZER SHOT {chain.Link}/{BuzzerShot.MaxLinks}", BlitzPalette.Gold);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"{chain.Shooter} shoots{(chain.IsKeeperReply ? " goal to goal" : " from where they stand")}.\n" +
                    $"{chain.Interceptor} rolls to intercept." +
                    (chain.IsLast ? "\nLast one — the set ends either way." : string.Empty));
            }
        }

        // Rerolls are owed after the phase has closed, which is exactly when they are
        // easiest to forget.
        if (_state.TieBreaks.Count > 0)
        {
            ImGui.SameLine();
            BlitzSkin.Pill($"REROLL ×{_state.TieBreaks.Count}", BlitzPalette.Purple);

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                foreach (var tie in _state.TieBreaks)
                {
                    ImGui.TextUnformatted(
                        $"{tie.Challenger} vs {tie.Defender} — tied at {tie.TiedAt}, attempt {tie.Attempt}");
                }
                ImGui.EndTooltip();
            }
        }

        ImGui.SameLine();
        DrawLegend();
    }

    /// <summary>
    /// Countdown for phases that have one: roughly fifteen seconds to huddle, a
    /// minute to act. Phases often end early once everyone has acted, so this is a
    /// ceiling rather than a promise.
    /// </summary>
    private void DrawPhaseClock()
    {
        var duration = PhaseTiming.For(_state.Phase);
        if (duration is null)
        {
            BlitzSkin.Pill("—", BlitzPalette.InkDim);
            return;
        }

        var remaining = _state.PhaseRemaining ?? TimeSpan.Zero;
        var fraction = (float)(remaining.TotalSeconds / duration.Value.TotalSeconds);

        // Colour by urgency rather than making people read the number.
        var color = fraction switch
        {
            <= 0f => BlitzPalette.Danger,
            < 0.25f => BlitzPalette.Warning,
            _ => BlitzPalette.Success,
        };

        BlitzSkin.StatBar(fraction, color, 54f, 8f);

        ImGui.SameLine();
        ImGui.TextColored(
            BlitzPalette.ToVector(color),
            remaining > TimeSpan.Zero ? $"{remaining.TotalSeconds:0}s" : "time");
    }

    /// <summary>
    /// Everyone who has rolled this phase, highest first.
    ///
    /// The field view shows each roll beside its player, which answers "what did they
    /// get" but not "who is winning" — for that the numbers have to sit next to each
    /// other. Ordering them is the whole feature: a contest is decided by the top of
    /// this list.
    /// </summary>
    private void DrawRollStrip()
    {
        _rollers.Clear();

        foreach (var player in _state.Players.Values)
        {
            if (player.PhaseRoll is not { } roll) continue;

            var modifier = _state.CurrentActionFor(player.Name)?.Modifier ?? 0;
            _rollers.Add((player, roll + modifier, roll, modifier));
        }

        if (_rollers.Count == 0) return;

        _rollers.Sort(ByEffectiveRoll);

        BlitzSkin.SectionHeading("Rolls this phase");

        for (var i = 0; i < _rollers.Count; i++)
        {
            var (player, effective, roll, modifier) = _rollers[i];

            if (i > 0) ImGui.SameLine(0f, 12f);

            var isHome = player.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase);

            ImGui.TextColored(
                BlitzPalette.ToVector(BlitzPalette.TeamColor(isHome)),
                ShortName(player.Name));

            ImGui.SameLine(0f, 3f);

            ImGui.TextColored(
                BlitzPalette.ToVector(BlitzIcons.RollColor(effective)),
                BlitzIcons.RollText(roll, modifier));
        }

        ImGui.Spacing();
    }

    /// <summary>
    /// Cached because this runs every frame for every player who has rolled, and the
    /// trimmed name never changes.
    /// </summary>
    private string ShortName(string name)
    {
        if (_shortNames.TryGetValue(name, out var cached))
            return cached;

        var space = name.IndexOf(' ');
        var shortName = space > 0 ? name[..space] : name;

        _shortNames[name] = shortName;
        return shortName;
    }

    /// <summary>
    /// The status symbols are compact but not self-explanatory, so keep the key one
    /// hover away rather than making people guess.
    /// </summary>
    private static void DrawLegend()
    {
        BlitzSkin.Muted("(?)");

        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted("Status symbols");
        ImGui.Separator();

        foreach (var (icon, meaning, color) in BlitzIcons.Legend)
        {
            ImGui.TextColored(BlitzPalette.ToVector(color), icon);
            ImGui.SameLine();
            ImGui.TextUnformatted(meaning);
        }

        ImGui.EndTooltip();
    }

    private void DrawPlayByPlay()
    {
        BlitzSkin.SectionHeading("Play-by-play");

        if (!BlitzSkin.BeginCard("play-by-play", new Vector2(0, 0)))
        {
            BlitzSkin.EndCard();
            return;
        }

        var start = Math.Max(0, _state.PlayByPlay.Count - 40);
        for (var i = start; i < _state.PlayByPlay.Count; i++)
        {
            var line = _state.PlayByPlay[i];

            // Advisories and referee flags carry a marker; tint them so they stand out
            // from ordinary commentary without needing to be read closely.
            var color = line.Contains('⚑') ? BlitzPalette.Warning
                : line.Contains("[GOAL]") ? BlitzPalette.Gold
                : line.Contains("[SAVE]") ? BlitzPalette.Accent
                // Rolls are frequent and are the record rather than the story, so they
                // sit a tier back and let outcomes carry the eye.
                : line.Contains(" rolls ") ? BlitzPalette.InkDim
                : BlitzPalette.Ink;

            ImGui.PushStyleColor(ImGuiCol.Text, BlitzPalette.ToVector(color));
            ImGui.TextWrapped(line);
            ImGui.PopStyleColor();
        }

        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
            ImGui.SetScrollHereY(1.0f);

        BlitzSkin.EndCard();
    }
}
