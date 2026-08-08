using System.Text.RegularExpressions;

namespace BlitzballTracker.Core.Parsing;

using BlitzballTracker.Core.GameState;

/// <summary>
/// Parses Yell-channel chat messages into game events that update BlitzGame state.
/// </summary>
public partial class ChatParser
{
    private readonly BlitzGame _state;

    /// <summary>
    /// The name of the local player (the person who recorded the log).
    /// "You roll a X" lines will be attributed to this player.
    /// </summary>
    public string? LocalPlayerName { get; set; }

    private RosterIndex? _index;
    private Roster? _indexedRoster;

    private readonly Dictionary<string, int> _unmatchedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names that looked like they were taking game actions but are not on the roster,
    /// with a hit count. Expected to contain commentators and crowd. A real player
    /// showing up here means the roster is missing someone, and their actions are
    /// being dropped: the UI surfaces this rather than failing silently.
    /// </summary>
    public IReadOnlyDictionary<string, int> UnmatchedNames => _unmatchedNames;

    public ChatParser(BlitzGame state)
    {
        _state = state;
    }

    /// <summary>
    /// Rebuild the name index when the roster has been swapped out.
    /// </summary>
    private RosterIndex? Index
    {
        get
        {
            if (!_state.HasRoster) return null;

            if (!ReferenceEquals(_indexedRoster, _state.CurrentRoster))
            {
                _indexedRoster = _state.CurrentRoster;
                _index = new RosterIndex(_state.Players.Keys);
            }

            return _index;
        }
    }

    /// <summary>
    /// Resolve a name from chat to a rostered player, or null.
    ///
    /// This is the allowlist that keeps spectators out of the game state. Logs from
    /// real events are full of crowd participation (the Chocobowl log opens with
    /// eighteen different people shouting "BLITZOFF!") and commentators naming
    /// players in prose. Anyone not on the team sheet is not a player.
    /// </summary>
    private PlayerState? ResolvePlayer(string? rawName)
    {
        var canonical = Index?.Resolve(rawName);
        if (canonical is null)
        {
            RecordUnmatched(rawName);
            return null;
        }

        return _state.Players.GetValueOrDefault(canonical);
    }

    /// <summary>
    /// Speculative lookup that does not pollute <see cref="UnmatchedNames"/>.
    /// Used where the text being tested is only a guess at a name, such as a
    /// bracketed token that might equally be a waymark.
    /// </summary>
    private PlayerState? ResolveQuiet(string? rawName)
    {
        var canonical = Index?.Resolve(rawName);
        return canonical is null ? null : _state.Players.GetValueOrDefault(canonical);
    }

    private void RecordUnmatched(string? rawName)
    {
        // With no roster loaded there is nothing to be unmatched against, and every
        // name in the log would pile up here.
        if (!_state.HasRoster) return;
        if (string.IsNullOrWhiteSpace(rawName)) return;

        var display = PlayerNames.StripWorld(rawName).Trim();
        if (display.Length == 0) return;

        _unmatchedNames[display] = _unmatchedNames.GetValueOrDefault(display) + 1;
    }

    public void ClearUnmatchedNames() => _unmatchedNames.Clear();

    /// <summary>
    /// Process a single chat message. Returns true if the message was a recognized game event.
    /// </summary>
    public bool ProcessMessage(string sender, string message, DateTime timestamp)
    {
        // Normalize whitespace
        message = message.Trim();

        // --- Phase / structure messages (from ref) ---
        if (TryParsePhaseMessage(message, timestamp))
        {
            _state.HasPhaseFeed = true;
            return true;
        }

        // --- Referee corrections: flags, grace, re-rolls ---
        if (TryParseCorrection(sender, message, timestamp))
            return true;

        // --- Score messages ---
        if (TryParseScore(message))
            return true;

        // Scorekeepers call the score in plain speech far more often than they post it
        // in brackets, so this is the form that actually turns up.
        if (TrySpokenScore(message))
            return true;

        // --- Ball state messages (from scorekeeper) ---
        if (TryParseBallState(sender, message, timestamp))
            return true;

        // --- Dice rolls ---
        if (TryParseDiceRoll(sender, message, timestamp))
            return true;

        // --- Player action declarations ---
        if (TryParseAction(sender, message, timestamp))
        {
            ReportMissingPhaseFeed(timestamp);
            return true;
        }

        // --- Status effects (DAZED, GRACE) ---
        if (TryParseStatus(message, timestamp))
            return true;

        // --- Officials calling an outcome outright ---
        if (TryParseOfficialCall(message, timestamp))
            return true;

        return false;
    }

    #region Phase Parsing

    private bool TryParsePhaseMessage(string message, DateTime timestamp)
    {
        // << STANDBY FOR BLITZOFF >>
        if (message.Contains("STANDBY FOR BLITZOFF", StringComparison.OrdinalIgnoreCase))
        {
            _state.IsActive = true;
            _state.Phase = GamePhase.PreGame;
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Game starting!");
            return true;
        }

        // "[Teams, please reset for Blitzon.  Barracuda ball.]" — this lands before the
        // << BLITZON >> itself, so it is held until the restart arrives.
        var blitzonBall = RegexBlitzonBall().Match(message);
        if (blitzonBall.Success)
        {
            _blitzonReceivingTeam = blitzonBall.Groups[1].Value.Trim();
            return true;
        }

        // "[... had a +10 due to being down one point.]" — the halftime bonus is ten per
        // point of deficit (slide 15), so saying it out loud states the gap exactly.
        var deficit = RegexDeficitBonus().Match(message);
        if (deficit.Success && TryReadDeficit(deficit, timestamp))
            return true;

        // << BLITZOFF >> and << BLITZON >>
        if (RegexBlitzoff().IsMatch(message))
        {
            var restartingSet = _state.Phase == GamePhase.Halftime;
            var saysBlitzon = message.Contains("BLITZON", StringComparison.OrdinalIgnoreCase);

            // Every restart bar the opening whistle and the second-set kickoff follows a
            // goal, and which restart it is says what the score now looks like: a
            // contested Blitzoff means the goal levelled it, a Blitzon means it did not
            // and the side receiving is the side behind (slide 15). The referees never
            // post a score, so this is the only reading of it anyone gets.
            if (_state.Phase != GamePhase.PreGame && !restartingSet)
                _state.RegisterGoalFromRestart(ReadRestart(saysBlitzon, timestamp));

            _state.Phase = GamePhase.Blitzoff;
            if (_state.Round == 0)
                _state.Round = 1;
            _state.ResetPositions();

            AnnounceBlitzoffVariant(restartingSet, saysBlitzon, timestamp);
            _blitzonReceivingTeam = null;
            return true;
        }

        // << ROUND N >>
        var roundMatch = RegexRound().Match(message);
        if (roundMatch.Success)
        {
            _state.Round = int.Parse(roundMatch.Groups[1].Value);

            // Rush Gates are not swept here. They last until the start of their
            // placer's next turn (slide 65), which is the next inner phase — close to
            // a round, but not the same when a goal resets play mid-round.

            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Set {_state.Set}, Round {_state.Round}");
            return true;
        }

        // N ROUNDS TO BUZZER
        var buzzerMatch = RegexBuzzer().Match(message);
        if (buzzerMatch.Success)
        {
            _state.RoundsRemaining = int.Parse(buzzerMatch.Groups[1].Value);
            return true;
        }

        // << OUTER HUDDLE PHASE >>
        if (message.Contains("OUTER HUDDLE PHASE", StringComparison.OrdinalIgnoreCase))
        {
            _state.Phase = GamePhase.OuterHuddle;
            return true;
        }

        // << INNER HUDDLE PHASE >>
        if (message.Contains("INNER HUDDLE PHASE", StringComparison.OrdinalIgnoreCase))
        {
            _state.Phase = GamePhase.InnerHuddle;
            return true;
        }

        // << OUTER PHASE (A/B/1/2) >> Start!
        if (message.Contains("OUTER PHASE", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("Start", StringComparison.OrdinalIgnoreCase))
        {
            ReportUnclearedKeeper(timestamp);

            _state.Phase = GamePhase.OuterPhase;
            ClearPhaseState(timestamp);

            // A fresh acting phase, so last round's blocks are gone.
            _state.ClearBlocks();
            _state.ClearDives();
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Outer Phase begins");
            return true;
        }

        // << INNER PHASE (4/C/D) >> Start!
        if (message.Contains("INNER PHASE", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("Start", StringComparison.OrdinalIgnoreCase))
        {
            ReportUnclearedKeeper(timestamp);

            _state.Phase = GamePhase.InnerPhase;

            // The keeper's turn comes round here, so any gate they laid on the last one
            // has run out (slide 65).
            _state.InnerPhaseCount++;
            _state.ExpireRushGates();

            ClearPhaseState(timestamp);
            _state.ClearBlocks();
            _state.ClearDives();
            // GUARD bonus expires at start of next Inner Phase
            foreach (var p in _state.Players.Values.Where(p => p.IsGoalkeeper))
            {
                p.GuardBonus = 0;
                p.IsGuarding = false;
            }
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Inner Phase begins");
            return true;
        }

        // << REPOSITION >> — infer Outer vs Inner based on what phase just ended
        if (message.Contains("REPOSITION", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("<<", StringComparison.Ordinal) || RegexReposition().IsMatch(message)))
        {
            // Note which phase is closing before switching: eligibility has to be
            // judged against the phase that just ran, not against Reposition.
            var closingPhase = _state.Phase;

            _state.Phase = closingPhase switch
            {
                GamePhase.OuterPhase => GamePhase.OuterReposition,
                GamePhase.InnerPhase => GamePhase.InnerReposition,
                _ => GamePhase.OuterReposition, // default fallback
            };

            // Everyone who declared a move goes now, together.
            ApplyPendingMoves(timestamp);
            ReportMissedActions(closingPhase, timestamp);
            return true;
        }

        // << BALL CARRIER TURN >> — infer Outer vs Inner based on previous phase
        if (message.Contains("BALL CARRIER TURN", StringComparison.OrdinalIgnoreCase))
        {
            // A keeper never takes a carrier turn: they should have cleared it before
            // play got this far.
            ReportUnclearedKeeper(timestamp);

            _state.Phase = _state.Phase switch
            {
                GamePhase.OuterReposition or GamePhase.OuterPhase => GamePhase.BallCarrierOuter,
                GamePhase.InnerReposition or GamePhase.InnerPhase => GamePhase.BallCarrierInner,
                _ => GamePhase.BallCarrierInner, // default fallback
            };

            // Increment BC turn counter and clear expired DAZEs
            _state.BallCarrierTurnCount++;
            ClearExpiredDazes();

            // Clear BLOCKED status at start of new BC turn
            // Blocks deliberately survive here: they were declared during the ring's
            // phase and this is the turn they fire in.
            // Dive states deliberately survive here, like blocks: a defender arms one
            // during the ring's phase and the carrier's turn is when the ball moves.
            foreach (var p in _state.Players.Values)
            {
                p.IsSurveying = false;
                p.SurveyedLane = null;
            }

            ClearPhaseState(timestamp);
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Ball Carrier Turn — {_state.BallCarrier ?? "?"}");
            return true;
        }

        // << PHASE SHIFT >> — ref ends current phase early (all players acted or time)
        if (message.Contains("PHASE SHIFT", StringComparison.OrdinalIgnoreCase))
        {
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Phase Shift (early end)");
            return true;
        }

        // << BUZZER PHASE >>
        if (message.Contains("BUZZER PHASE", StringComparison.OrdinalIgnoreCase))
        {
            _state.Phase = GamePhase.BuzzerPhase;
            ClearPhaseState(timestamp);
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] BUZZER PHASE! Ball carrier must shoot!");
            return true;
        }

        // << GAME OVER >> — the final whistle. Nothing set PostGame before this, so a
        // finished match sat in whatever phase it happened to end on and the display
        // never admitted the game had stopped.
        if (message.Contains("GAME OVER", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("FULL TIME", StringComparison.OrdinalIgnoreCase))
        {
            ClearPhaseState(timestamp);

            _state.Phase = GamePhase.PostGame;
            _state.IsFinished = true;
            _state.RoundsRemaining = 0;
            _state.ClearBlocks();
            _state.ClearDives();

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] FULL TIME — {_state.HomeTeam} {_state.Score.Home}" +
                $":{_state.Score.Away} {_state.AwayTeam}. {Verdict()}");

            return true;
        }

        // HALFTIME
        if (message.Contains("HALFTIME", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("HALF TIME", StringComparison.OrdinalIgnoreCase))
        {
            _state.Phase = GamePhase.Halftime;
            _state.SwitchSides();
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] HALFTIME — teams switching sides");
            return true;
        }

        // SHOOTOUT
        if (message.Contains("SHOOTOUT", StringComparison.OrdinalIgnoreCase))
        {
            ClearPhaseState(timestamp);

            _state.Phase = GamePhase.Shootout;
            _state.ClearBlocks();
            _state.ClearDives();
            _state.ShootoutScore = default;
            _state.ShootoutAttempts.Clear();

            // Home takes the letter lane and away the numbers, and the captains roll
            // off for who goes first. Until that is announced, assume home leads —
            // corrected the moment a [[ FIRST: team ]] line arrives.
            if (_state.ShootoutFirstTeam.Length == 0)
                _state.ShootoutFirstTeam = _state.HomeTeam;

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] OVERTIME — SHOOTOUT! Five each, flat rolls, no modifiers.");
            return true;
        }

        // [[ FIRST -- Team ]] — who won the captains' roll-off.
        var firstMatch = RegexShootoutFirst().Match(message);
        if (firstMatch.Success && _state.Phase == GamePhase.Shootout)
        {
            var named = firstMatch.Groups[1].Value.Trim();

            _state.ShootoutFirstTeam =
                named.Contains(_state.AwayTeam, StringComparison.OrdinalIgnoreCase) && _state.AwayTeam.Length > 0
                    ? _state.AwayTeam
                    : _state.HomeTeam;

            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] {_state.ShootoutFirstTeam} shoot first.");
            return true;
        }

        // SUDDEN DEATH
        if (message.Contains("SUDDEN DEATH", StringComparison.OrdinalIgnoreCase))
        {
            ClearPhaseState(timestamp);

            _state.Phase = GamePhase.SuddenDeath;
            _state.ClearBlocks();
            _state.ClearDives();
            _state.SuddenDeath = new SuddenDeath();

            // The sphere empties, keepers included, and both captains meet at Centre.
            foreach (var player in _state.Players.Values)
                player.Position = Waymark.None;

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] SUDDEN DEATH — the sphere clears and the captains meet at Centre.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// How long a roll can arrive after its phase closed and still be attributed.
    ///
    /// This was a full minute, which is roughly how long a phase itself lasts: a
    /// straggler could therefore bind to an action from a phase that had already
    /// finished entirely. It now sits well inside a single phase.
    /// </summary>
    private static readonly TimeSpan LateRollWindow = PhaseTiming.LateRollGrace;

    /// <summary>Actions left unresolved when a phase closed, held briefly for late rolls.</summary>
    private readonly List<ActionEvent> _recentlyClosed = [];

    /// <summary>
    /// Put a player on a zone, reporting a refusal and picking up a Rush Gate relay
    /// if their own side has one waiting there.
    ///
    /// Returns whether they actually moved, so callers do not report a move the rules
    /// just refused.
    /// </summary>
    private bool MovePlayer(PlayerState player, Waymark destination, DateTime timestamp)
    {
        if (!_state.TryPlace(player, destination))
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ {player.Name} " +
                $"({Roster.RoleAbbreviation(player.Role)}) cannot enter {destination}; MOVE ignored.");
            return false;
        }

        // A gate is their own side's relay: reaching it buys another move, which is
        // how the ball crosses gaps no single lane spans.
        var gate = _state.RushGateAt(destination);

        if (gate is not null && gate.Team.Equals(player.Team, StringComparison.OrdinalIgnoreCase))
        {
            player.HasGateMove = true;

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] {player.Name} reaches the Rush Gate at " +
                $"{destination} and may move again.");
        }

        return true;
    }

    /// <summary>
    /// Resolve every move declared during the phase, all at once.
    ///
    /// This is what Reposition is for: the ring acts together, so the ring moves
    /// together, and nothing that happened during the phase was measured against a
    /// position anyone had already left.
    /// </summary>
    private void ApplyPendingMoves(DateTime timestamp)
    {
        // Named rather than counted. Everyone moves at once, so "4 players moved" is
        // the one moment in the match where the log knows exactly what happened and
        // says the least about it.
        List<string>? moves = null;

        foreach (var action in _state.CurrentPhaseActions)
        {
            // A won tackle relocates the tackler, and it happens here with everyone
            // else's movement rather than the moment the rolls came in.
            var isTackleMove = action.Action == ActionType.Tackle
                               && action.Outcome == ActionOutcome.Success;

            if (action.Action != ActionType.Move && !isTackleMove) continue;
            if (action.Outcome == ActionOutcome.Fail) continue;

            // A contested move only lands if its roll-off has been won.
            if (action.ContestedBy is { Count: > 0 } && action.Outcome != ActionOutcome.Success)
                continue;
            if (action.TargetWaymark is not { } destination || destination == Waymark.None) continue;

            var player = _state.Players.GetValueOrDefault(action.PlayerName);
            if (player is null) continue;
            if (player.Position == destination) continue;

            var origin = player.Position;

            // Somebody watching this lane gets their roll now, not when they declared
            // it. The move waits on the roll-off rather than landing first.
            //
            // Tackles are caught too: a tackle is a movement, and one down a surveyed
            // lane can be cancelled by the survey at Reposition (slide 59).
            if (_state.SurveyorAgainst(player, origin, destination) is { } guard)
            {
                OpenSurveyContest(player, guard, origin, destination, timestamp,
                    isTackleMove, isTackleMove ? action : null);
                continue;
            }

            // Only report what actually happened. Listing the move regardless meant a
            // refusal and the move it refused were both printed, one line apart.
            if (MovePlayer(player, destination, timestamp))
                (moves ??= []).Add($"{player.Name} {origin}→{destination}");
        }

        if (moves is { Count: > 0 })
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Reposition: {string.Join(", ", moves)}.");
    }

    /// <summary>
    /// Name anyone who was in the acting ring and declared nothing.
    ///
    /// Standing in an active zone and letting the phase run out is a loss of action,
    /// and referees flag it. Easy to miss in a busy match with a whole ring acting at
    /// once, so surface it at the moment the phase closes.
    ///
    /// Advisory only: the referees decide whether it actually cost anyone anything.
    /// </summary>
    private void ReportMissedActions(GamePhase closingPhase, DateTime timestamp)
    {
        if (!PhaseRules.IsSimultaneousActionPhase(closingPhase)) return;

        var zones = PhaseRules.ActiveZones(closingPhase);
        if (zones is null) return;

        List<string>? missed = null;

        foreach (var player in _state.Players.Values)
        {
            // The carrier sits the ring out and acts in their own turn.
            if (_state.BallCarrier is not null &&
                _state.BallCarrier.Equals(player.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            var inRing = false;
            for (var i = 0; i < zones.Count; i++)
            {
                if (zones[i] != player.Position) continue;
                inRing = true;
                break;
            }

            if (!inRing) continue;
            if (_state.CurrentActionFor(player.Name) is not null) continue;

            (missed ??= []).Add(player.Name);
        }

        if (missed is null) return;

        missed.Sort(StringComparer.OrdinalIgnoreCase);

        // Losing the action is the same either way. Whether it is tracked as a named
        // STANDBY status depends on which edition of the rules is in play: the status
        // was retired, though the v3.2 deck still documents it.
        if (_state.Rules.StandbyStatus)
        {
            foreach (var name in missed)
            {
                if (_state.Players.TryGetValue(name, out var idle))
                    idle.IsStandby = true;
            }
        }

        var label = _state.Rules.StandbyStatus ? "STANDBY" : "Loss of action";

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] ⚑ {label} — nothing declared: {string.Join(", ", missed)}.");
    }

    private void ClearPhaseState(DateTime now)
    {
        CloseOutstandingFumble(now);
        CloseOutstandingSurveys(now);
        ExpireStaleTieBreaks(now);
        OpenTieBreaks(now);

        _recentlyClosed.RemoveAll(a => now - a.Timestamp > LateRollWindow);

        foreach (var action in _state.CurrentPhaseActions)
        {
            if (action.Outcome == ActionOutcome.Pending && action.Roll is null)
                _recentlyClosed.Add(action);
        }

        _state.CurrentPhaseActions.Clear();

        foreach (var player in _state.Players.Values)
        {
            player.PhaseRoll = null;
            player.RalliedRoll = null;
            player.HasGateMove = false;
            player.IsStandby = false;
        }
    }

    /// <summary>
    /// DAZE lasts "until the end of the Ball Carrier's next turn."
    /// When BallCarrierTurnCount > the turn it was applied, it expires.
    /// </summary>
    private void ClearExpiredDazes()
    {
        var expired = _state.DazeTracker
            .Where(kv => _state.BallCarrierTurnCount > kv.Value + 1)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var name in expired)
        {
            _state.DazeTracker.Remove(name);
            if (_state.Players.TryGetValue(name, out var p))
                p.IsDazed = false;
        }
    }

    #endregion

    #region Score

    /// <summary>
    /// Read a score the scorekeeper called in plain speech.
    ///
    /// "Vidraal 2 - 1 Barracudas." is how it is actually announced — no brackets, a
    /// hyphen rather than a colon, and often trailing a shout ("Halftiiiiime! ..."). A
    /// bare "N - M" between two words is far too common in ordinary chat to trust on
    /// shape alone, so both names are checked against the roster before it is believed.
    /// </summary>
    private bool TrySpokenScore(string message)
    {
        var final = RegexFinalScore().Match(message);
        if (final.Success &&
            TryOrientScore(final.Groups[1].Value, int.Parse(final.Groups[2].Value),
                           final.Groups[3].Value, int.Parse(final.Groups[4].Value)))
        {
            return true;
        }

        foreach (Match m in RegexScoreSpoken().Matches(message))
        {
            if (TryOrientScore(m.Groups[1].Value, int.Parse(m.Groups[2].Value),
                               m.Groups[4].Value, int.Parse(m.Groups[3].Value)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Accept a spoken score only when both sides name teams we know, and put them the
    /// right way round. Scorekeepers write whichever side they please first.
    /// </summary>
    private bool TryOrientScore(string firstTeam, int firstScore, string secondTeam, int secondScore)
    {
        if (!_state.HasRoster || _state.HomeTeam.Length == 0) return false;

        var firstIsHome = _state.MatchesHome(firstTeam);
        var firstIsAway = _state.MatchesAway(firstTeam);
        var secondIsHome = _state.MatchesHome(secondTeam);
        var secondIsAway = _state.MatchesAway(secondTeam);

        if (firstIsHome && secondIsAway)
            _state.AdoptPostedScore(new Score(firstScore, secondScore));
        else if (firstIsAway && secondIsHome)
            _state.AdoptPostedScore(new Score(secondScore, firstScore));
        else
            return false;

        return true;
    }

    private bool TryParseScore(string message)
    {
        // [[ DAIGOROS 0:0 AUSPICES ]]
        var scoreMatch = RegexScore().Match(message);
        if (!scoreMatch.Success) return false;

        var firstTeam = scoreMatch.Groups[1].Value.Trim();
        var firstScore = int.Parse(scoreMatch.Groups[2].Value);
        var secondScore = int.Parse(scoreMatch.Groups[3].Value);
        var secondTeam = scoreMatch.Groups[4].Value.Trim();

        // Referees post the scoreboard with the teams in either order, e.g. both
        // "[[ DAIGOROS 0:0 AUSPICES ]]" and "[[ AUSPICES 0:1 DAIGOROS ]]" appear in
        // the same match. Taking the first name as home would swap the two teams
        // mid-game, which inverts every isHome check and sends the whole field to the
        // wrong end on the next ResetPositions. So orient the line to the known teams
        // instead of letting it redefine them.
        if (_state.HasRoster && !string.IsNullOrEmpty(_state.HomeTeam))
        {
            if (firstTeam.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase))
            {
                // A posted score outranks anything derived from restarts, and turns the
                // derivation off — there is no need to infer what somebody has stated.
                _state.AdoptPostedScore(new Score(firstScore, secondScore));
            }
            else if (firstTeam.Equals(_state.AwayTeam, StringComparison.OrdinalIgnoreCase))
            {
                _state.AdoptPostedScore(new Score(secondScore, firstScore));
            }
            else
            {
                // Neither name is recognised: record the numbers but leave the
                // roster's team identities alone.
                _state.Score = new Score(firstScore, secondScore);
            }
        }
        else
        {
            // No roster to anchor against, so bootstrap identities from the line.
            _state.HomeTeam = firstTeam;
            _state.AwayTeam = secondTeam;
            _state.Score = new Score(firstScore, secondScore);
        }

        _state.PlayByPlay.Add(
            $"Score: {_state.HomeTeam} {_state.Score.Home}:{_state.Score.Away} {_state.AwayTeam}");
        return true;
    }

    #endregion

    #region Ball State

    private bool TryParseBallState(string sender, string message, DateTime timestamp)
    {
        // [[ TEAM BALL GET ]] or [[ TEAM, BALL GET ]] or [[ TEAM -- BALL GET ]]
        var ballGetMatch = RegexBallGet().Match(message);
        if (ballGetMatch.Success)
        {
            var team = ballGetMatch.Groups[1].Value.Trim().TrimEnd(',', '-', ' ');
            _state.BallTeam = team;
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] {team} has possession!");
            return true;
        }

        // [BALL to PlayerName]
        var ballToMatch = RegexBallTo().Match(message);
        if (ballToMatch.Success)
        {
            SetBallCarrier(ballToMatch.Groups[1].Value.Trim().TrimEnd(']'), timestamp);
            return true;
        }

        // [[PASS COMPLETE to Player ]]
        var passComplete = RegexPassComplete().Match(message);
        if (passComplete.Success)
        {
            // Remember who is releasing the ball, before possession moves.
            var passer = _state.BallCarrier is null
                ? null
                : _state.Players.GetValueOrDefault(_state.BallCarrier);
            var origin = passer?.Position ?? Waymark.None;

            var playerName = passComplete.Groups[1].Value.Trim();
            if (SetBallCarrier(playerName, timestamp))
            {
                _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Pass complete to {_state.BallCarrier}");

                var receiver = _state.Players.GetValueOrDefault(_state.BallCarrier!);

                if (passer is not null && receiver is not null)
                {
                    var verdict = ReportPassLegality(passer, origin, receiver, timestamp);

                    // A dazed outfielder cannot hold onto it, and a keeper who threw
                    // too far never got it there cleanly. Either way the ball is loose
                    // in the receiving zone and everyone standing there gets a go.
                    if (BlitzGame.FumblesOnReceipt(receiver))
                        OpenFumble(receiver.Position, FumbleCause.DazedReceiver, receiver, timestamp);
                    else if (!verdict.Arrives)
                        OpenFumble(receiver.Position, FumbleCause.KeeperOverreach, receiver, timestamp);
                }

                // A defender lying in wait gets a roll at anything entering their
                // zone, whoever it was aimed at.
                if (passer is not null && receiver is not null)
                {
                    var divers = _state.DiversCovering(origin, receiver.Position, passer.Team);

                    foreach (var diver in divers)
                    {
                        _state.PlayByPlay.Add(
                            $"[{timestamp:HH:mm:ss}] {diver.Name} is diving and can contest the ball " +
                            $"arriving at {receiver.Position}.");
                    }
                }
            }

            return true;
        }

        // [[ CAUGHT ]] or [[CAUGHT by Player ]]
        var caughtMatch = RegexCaught().Match(message);
        if (caughtMatch.Success)
        {
            var catcher = caughtMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(catcher))
            {
                var player = ResolvePlayer(catcher);
                if (player != null)
                {
                    SetBallCarrier(player.Name, timestamp);
                    player.Saves++;
                    _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] CAUGHT by {player.Name}!");
                }
            }
            else
            {
                _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Shot CAUGHT!");
            }
            return true;
        }

        // [[FUMBLE ...]] — a referee calling one the tracker did not work out for
        // itself. Open the contest around whoever currently holds the ball, so the
        // rolls that follow have somewhere to go.
        if (message.Contains("FUMBLE", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.Fumble is null &&
                _state.BallCarrier is { } holder &&
                _state.Players.TryGetValue(holder, out var dropped))
            {
                OpenFumble(dropped.Position, FumbleCause.DazedReceiver, dropped, timestamp);
            }
            else
            {
                _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] FUMBLE! Recovery roll needed!");
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Move the ball to a player. Returns false when the name is not on the roster,
    /// leaving possession untouched rather than inventing a carrier.
    /// </summary>
    private bool SetBallCarrier(string rawName, DateTime timestamp)
    {
        var player = ResolvePlayer(rawName);
        if (player is null) return false;

        // Clear previous carrier
        if (_state.BallCarrier != null && _state.Players.TryGetValue(_state.BallCarrier, out var prev))
            prev.HasBall = false;

        _state.BallCarrier = player.Name;
        player.HasBall = true;

        // Possession follows the carrier's roster team, which is more reliable than
        // the most recent [[ TEAM BALL GET ]] announcement.
        if (!string.IsNullOrEmpty(player.Team))
            _state.BallTeam = player.Team;

        // In the duel, taking the ball makes you the shooter and the other captain the
        // one who owes a block. That is the whole turn structure of sudden death.
        if (_state.Phase == GamePhase.SuddenDeath && _state.SuddenDeath is { } duel)
        {
            if (duel.Holder is not null && !duel.Holder.Equals(player.Name, StringComparison.OrdinalIgnoreCase))
                duel.Turnover();

            duel.Holder ??= player.Name;
            duel.Challenger ??= _state.Players.Values
                .FirstOrDefault(p => !p.Team.Equals(player.Team, StringComparison.OrdinalIgnoreCase))?.Name;

            duel.HolderBlocked = false;
        }

        ReportBlitzonRecipient(player, timestamp);

        // A keeper does not carry the ball, they send it straight back out, and that
        // pass happens before play moves on. Said out loud because the keeper is
        // holding everyone up until they do it.
        //
        // Except at the buzzer, where they owe a goal-to-goal shot instead. Saying both
        // told them to pass and then flagged them for passing.
        if (player.IsGoalkeeper && !_state.BallCarrierMustShoot)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] {player.Name} has it in the goal and must pass it straight out.");
        }

        return true;
    }

    #endregion

    #region Dice Rolls

    private bool TryParseDiceRoll(string sender, string message, DateTime timestamp)
    {
        // "Random! PlayerName rolls a 98 (out of 100)."
        var rollMatch = RegexDiceRoll().Match(message);
        if (rollMatch.Success)
        {
            var playerName = rollMatch.Groups[1].Value.Trim();
            var roll = int.Parse(rollMatch.Groups[2].Value);

            // Spectators roll dice too. Recognised as a roll, but not our business.
            var player = ResolvePlayer(playerName);
            if (player is null) return true;

            player.TotalRolls++;
            player.RollSum += roll;

            // A loose ball takes precedence: everyone in the zone owes a roll for it,
            // including players who already rolled this phase for something else.
            if (TryTakeFumbleRoll(player, roll, timestamp)) return true;

            // A survey roll-off at Reposition, which sits after the phase the move was
            // declared in and so is not that phase's roll.
            if (TryTakeSurveyRoll(player, roll, timestamp)) return true;

            // Then a reroll they owe from a tie. Like a fumble roll it settles one
            // contest and leaves their phase roll standing.
            if (TryTakeTieBreakRoll(player, roll, timestamp)) return true;

            var superseded = RecordPhaseRoll(player, roll, timestamp);

            var pendingAction = FindActionAwaitingRoll(player, timestamp);
            if (pendingAction != null)
                pendingAction.Roll = roll;

            // Logged before resolving, so the commentary reads in the order it
            // happened: the number, then what it decided.
            if (!superseded)
                LogRoll(player, roll, pendingAction, timestamp);

            // Now try to resolve any opposed actions involving this player
            ResolveOpposedActions(player.Name);

            return true;
        }

        // "You roll a N" — this is the local player
        var youRollMatch = RegexYouRoll().Match(message);
        if (youRollMatch.Success)
        {
            // The local player is only relevant if they are actually playing.
            var player = ResolveQuiet(LocalPlayerName);
            if (player is not null)
            {
                var roll = int.Parse(youRollMatch.Groups[1].Value);
                player.TotalRolls++;
                player.RollSum += roll;

                if (TryTakeFumbleRoll(player, roll, timestamp)) return true;
                if (TryTakeSurveyRoll(player, roll, timestamp)) return true;
                if (TryTakeTieBreakRoll(player, roll, timestamp)) return true;

                // Routed through the same two helpers as everybody else. This path
                // used to keep the first roll and bind only within the phase, so the
                // local player alone lost their re-rolls and their late rolls — and
                // the local player is the one person guaranteed to be at the match.
                var superseded = RecordPhaseRoll(player, roll, timestamp);

                var pendingAction = FindActionAwaitingRoll(player, timestamp);
                if (pendingAction != null)
                    pendingAction.Roll = roll;

                if (!superseded)
                    LogRoll(player, roll, pendingAction, timestamp);

                ResolveOpposedActions(player.Name);
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// After a roll comes in, check if any opposed actions between this player and another
    /// can now be resolved (both sides have rolled).
    /// </summary>
    private void ResolveOpposedActions(string playerName)
    {
        foreach (var action in _state.CurrentPhaseActions.Where(a => a.Outcome == ActionOutcome.Pending))
        {
            // Find actions where this player is the actor OR the target
            var isActor = action.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase);
            var isTarget = action.TargetName != null &&
                           action.TargetName.Equals(playerName, StringComparison.OrdinalIgnoreCase);

            var contests = (action.ContestedBy?.Contains(playerName, StringComparer.OrdinalIgnoreCase) ?? false)
                           || (action.DivedBy?.Contains(playerName, StringComparer.OrdinalIgnoreCase) ?? false);

            // SHOOT is special: opposed vs the GK, who isn't in TargetName — and a shot
            // may also be blocked or dived on, which is settled inside ResolveShoot so
            // the three tiers stay in one place. It must not be diverted to the generic
            // block contest, which knows nothing about the net behind it.
            if (action.Action == ActionType.Shoot)
            {
                var shooter = _state.Players.GetValueOrDefault(action.PlayerName);
                var shooterTeam = shooter?.Team ?? "";

                var isOpposingKeeper =
                    _state.Players.TryGetValue(playerName, out var rollingPlayer) &&
                    rollingPlayer.Role == PlayerRole.Goalkeeper &&
                    !rollingPlayer.Team.Equals(shooterTeam, StringComparison.OrdinalIgnoreCase);

                if (isActor || isOpposingKeeper || contests)
                    ResolveShoot(action);

                continue;
            }

            // Blocks and dives standing against this player fire together against their
            // pass or move, rather than being decided when they were declared.
            if (action.ContestedBy is { Count: > 0 } || action.DivedBy is { Count: > 0 })
            {
                ResolveBlockContest(action);
                continue;
            }

            // Rally is not opposed by anyone — it measures the midfielder against the
            // team-mate they are lending to.
            if (action.Action == ActionType.Rally)
            {
                ResolveRally(action);
                continue;
            }

            if (!isActor && !isTarget) continue;

            if (action.TargetName == null) continue;

            // Check if this is an opposed action type
            if (!IsOpposedAction(action.Action)) continue;

            // Get both players' phase rolls
            var actorRoll = GetPlayerPhaseRoll(action.PlayerName);
            var targetRoll = GetPlayerPhaseRoll(action.TargetName);

            if (actorRoll == null || targetRoll == null) continue;

            // Daze takes the modifier with it, the same way it strips a shooter's class
            // bonus and a keeper's GUARD. Applied here too so a dazed player does not
            // keep a bonus they have lost.
            var actorDazed = _state.Players.TryGetValue(action.PlayerName, out var actingPlayer)
                             && actingPlayer.IsDazed;

            var effectiveActorRoll = actorRoll.Value + (actorDazed ? 0 : action.Modifier ?? 0);
            var effectiveTargetRoll = targetRoll.Value;

            // Apply GK guard bonus if target is a goalkeeper being taunted/tackled
            // (GK doesn't get bonus on defense vs tackle/taunt - that's only for shots)

            if (effectiveActorRoll > effectiveTargetRoll)
            {
                action.Outcome = ActionOutcome.Success;
                var applied = action.Applied = new AppliedEffects();

                if (_state.Players.TryGetValue(action.PlayerName, out var actor))
                {
                    actor.ActionsSucceeded++;
                    applied.ActorSucceeded = true;
                }

                // Apply effects on success
                ApplyActionSuccess(action);
            }
            else if (effectiveActorRoll < effectiveTargetRoll)
            {
                action.Outcome = ActionOutcome.Fail;
                action.TiedAt = null;
            }
            else
            {
                // Level. The referee calls the reroll at the end of the phase, not now,
                // so this only marks it — the rest of the phase still has to play out.
                action.TiedAt = effectiveActorRoll;
            }
        }
    }

    /// <summary>
    /// Settle a pass or move against every block standing on the player.
    ///
    /// All the blockers roll and the best of them is what the carrier has to beat,
    /// which is the same thing as having to beat all of them. Each side rolls once
    /// for the phase, so a blocker's existing roll is reused rather than taken again.
    /// </summary>
    private void ResolveBlockContest(ActionEvent action)
    {
        var carrierRoll = GetPlayerPhaseRoll(action.PlayerName);
        if (carrierRoll is null) return;

        // Blocks first. The closest successful interception beats everything else, even
        // if something further out rolled higher (slide 33), so a block that gets there
        // ends the ball's flight before a diver's number is ever looked at.
        if (!TryBestOf(action.ContestedBy, out var bestBlocker, out var blockRoll)) return;

        if (bestBlocker is not null && blockRoll >= carrierRoll)
        {
            action.Applied = new AppliedEffects();
            action.Outcome = ActionOutcome.Fail;

            // A beaten pass is cut out; a beaten move simply does not happen. The move
            // case is not fully confirmed and may need revisiting.
            _state.PlayByPlay.Add(action.Action == ActionType.Pass
                ? $"[INTERCEPT] {bestBlocker} cuts out {action.PlayerName}'s pass ({blockRoll} vs {carrierRoll})."
                : $"{bestBlocker} holds {action.PlayerName} in place ({blockRoll} vs {carrierRoll}).");

            if (action.Action == ActionType.Pass)
            {
                SetBallCarrier(bestBlocker, action.Timestamp);
                OpenBuzzerShot(bestBlocker, action.PlayerName, action.Timestamp);
            }

            return;
        }

        // Then dives, one tier further out. A diver rolls against the passer — the
        // player who put the ball in the air — not the team-mate it was aimed at.
        if (!TryBestOf(action.DivedBy, out var bestDiver, out var diveRoll)) return;

        if (bestDiver is not null && diveRoll >= carrierRoll)
        {
            action.Outcome = ActionOutcome.Fail;

            _state.PlayByPlay.Add(
                $"[INTERCEPT] {bestDiver} dives across and takes {action.PlayerName}'s pass " +
                $"({diveRoll} vs {carrierRoll}).");

            SetBallCarrier(bestDiver, action.Timestamp);

            // A dive that comes off at the end of a set can be turned into a buzzer
            // shot rather than simply ending it (slide 27).
            OpenBuzzerShot(bestDiver, action.PlayerName, action.Timestamp);
            return;
        }

        var applied = action.Applied = new AppliedEffects();

        action.Outcome = ActionOutcome.Success;

        if (_state.Players.TryGetValue(action.PlayerName, out var carrier))
        {
            carrier.ActionsSucceeded++;
            applied.ActorSucceeded = true;
        }

        var beatWhat = bestBlocker is not null ? "the block" : "the dive";
        var beatRoll = bestBlocker is not null ? blockRoll : diveRoll;

        _state.PlayByPlay.Add(
            $"{action.PlayerName} breaks through {beatWhat} ({carrierRoll} vs {beatRoll}).");
    }

    /// <summary>
    /// The highest roll among a set of contesters, or false if any of them has not
    /// rolled yet.
    ///
    /// Waiting for all of them matters: a partial result would settle the contest on
    /// whoever happened to type first.
    /// </summary>
    private bool TryBestOf(List<string>? names, out string? best, out int bestRoll)
    {
        best = null;
        bestRoll = 0;

        if (names is not { Count: > 0 }) return true;

        foreach (var name in names)
        {
            var roll = GetPlayerPhaseRoll(name);
            if (roll is null) return false;

            if (best is null || roll > bestRoll)
            {
                bestRoll = roll.Value;
                best = name;
            }
        }

        return true;
    }

    private bool _warnedAboutPhaseFeed;

    /// <summary>
    /// Say once when play is arriving but the phase calls are not.
    ///
    /// Referees post the structure — phases, rounds, the score — in the league's
    /// cross-world linkshell, while players declare and roll in Yell. A spectator sees
    /// the second and not the first, so the match reads as stuck before kickoff while
    /// actions pour in. That is not a fault, and it should not look like one.
    /// </summary>
    private void ReportMissingPhaseFeed(DateTime timestamp)
    {
        if (_state.HasPhaseFeed || _warnedAboutPhaseFeed) return;

        _warnedAboutPhaseFeed = true;

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] ⚑ Following play, but no referee phase calls have arrived. " +
            "Those are posted in the league linkshell, so without it the phase, round and " +
            "score stay unknown. Actions, rolls and possession are still being tracked.");
    }

    /// <summary>
    /// Work out which of the three restarts this is, and say so.
    ///
    /// The ball does not go back into play the same way every time (slide 15). Level
    /// scores mean a straight roll-off; a goal that did *not* level them hands the ball
    /// straight to the side behind with no roll at all; and the second set opens with a
    /// roll-off weighted toward whoever is chasing.
    ///
    /// The referees announce who actually ends up with it, so this reports what should
    /// happen rather than deciding it.
    /// </summary>
    private void AnnounceBlitzoffVariant(bool restartingSet, bool saysBlitzon, DateTime timestamp)
    {
        // When the score is known, it decides the variant and the call is checked
        // against it. When it is not — the usual case, since referees never post a
        // score — the call is all there is, and reading the variant back off a score
        // that was itself derived from these calls would be circular.
        _state.BlitzoffVariant =
            restartingSet ? BlitzoffKind.HalftimeRestart
            : _state.ScoreWasPosted
                ? (_state.Score.Home == _state.Score.Away ? BlitzoffKind.Standard : BlitzoffKind.Blitzon)
                : (saysBlitzon ? BlitzoffKind.Blitzon : BlitzoffKind.Standard);

        if (_state.ScoreWasPosted && !restartingSet &&
            saysBlitzon != (_state.BlitzoffVariant == BlitzoffKind.Blitzon))
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ Referee called a " +
                $"{(saysBlitzon ? "BLITZON" : "BLITZOFF")} at {_state.Score.Home}:{_state.Score.Away}, " +
                "which is the other restart by slide 15.");
        }

        switch (_state.BlitzoffVariant)
        {
            case BlitzoffKind.Blitzon:
                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] BLITZON — {_state.TrailingTeam} take the ball; no roll-off.");
                break;

            case BlitzoffKind.HalftimeRestart when _state.PointDeficit > 0:
                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] BLITZOFF! {_state.TrailingTeam} roll with " +
                    $"+{_state.PointDeficit * 10} for the {_state.PointDeficit}-point deficit.");
                break;

            default:
                _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] BLITZOFF!");
                break;
        }
    }

    /// <summary>The team named in a "reset for Blitzon — X ball" call, if one was seen.</summary>
    private string? _blitzonReceivingTeam;

    /// <summary>
    /// Work out what a restart says about the score.
    ///
    /// A contested Blitzoff means the goal levelled the game. A Blitzon means it did
    /// not, and the side handed the ball is the side behind — which is only readable
    /// when the referee named them, so an unnamed Blitzon narrows nothing.
    /// </summary>
    private RestartReading ReadRestart(bool saysBlitzon, DateTime timestamp)
    {
        if (!saysBlitzon) return RestartReading.Level;

        if (_blitzonReceivingTeam is not { Length: > 0 } team)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ BLITZON with no team named — a goal was scored, " +
                "but not which way.");
            return RestartReading.Unknown;
        }

        if (_state.MatchesHome(team)) return RestartReading.HomeBehind;
        if (_state.MatchesAway(team)) return RestartReading.AwayBehind;

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] ⚑ BLITZON gives the ball to \"{team}\", which matches " +
            $"neither {_state.HomeTeam} nor {_state.AwayTeam}.");
        return RestartReading.Unknown;
    }

    /// <summary>
    /// Read the halftime deficit out of the bonus the referee announces for it.
    /// </summary>
    private bool TryReadDeficit(Match match, DateTime timestamp)
    {
        var team = match.Groups[1].Value.Trim();
        var deficit = ReadSmallNumber(match.Groups[2].Value);

        if (deficit <= 0) return false;

        bool homeBehind;
        if (_state.MatchesHome(team)) homeBehind = true;
        else if (_state.MatchesAway(team)) homeBehind = false;
        else return false;

        _state.AdoptHalftimeDeficit(deficit, homeBehind);

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] {team} are {deficit} down — score read as " +
            $"{_state.Score.Home}:{_state.Score.Away}.");
        return true;
    }

    private static int ReadSmallNumber(string word) => word.ToLowerInvariant() switch
    {
        "one" => 1,
        "two" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        _ => int.TryParse(word, out var n) ? n : 0,
    };

    /// <summary>
    /// Note when a Blitzon hands the ball to the wrong side.
    ///
    /// There is no roll-off to get wrong here: the side that is behind receives it,
    /// full stop. Anything else means somebody has missed the call.
    /// </summary>
    private void ReportBlitzonRecipient(PlayerState receiver, DateTime timestamp)
    {
        if (_state.Phase != GamePhase.Blitzoff) return;
        if (_state.BlitzoffVariant != BlitzoffKind.Blitzon) return;
        if (_state.TrailingTeam.Length == 0) return;

        if (receiver.Team.Equals(_state.TrailingTeam, StringComparison.OrdinalIgnoreCase)) return;

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] ⚑ Blitzon gives the ball to {_state.TrailingTeam}, " +
            $"but it went to {receiver.Name}.");
    }

    /// <summary>
    /// The captains' duel. Returns whether the message was consumed here.
    ///
    /// Play alternates in a fixed shape: the holder shoots, the challenger may block,
    /// a blocked holder must win a second roll to get the shot away, and failing that
    /// the ball turns over and the roles swap. An unblocked shot that is not
    /// intercepted wins the game on the spot (slide 29).
    ///
    /// Everything here reads the game's own possession, so a referee announcing
    /// something different overrides it rather than fighting it.
    /// </summary>
    private bool TryResolveSuddenDeath(ActionEvent action, DateTime timestamp)
    {
        if (_state.Phase != GamePhase.SuddenDeath) return false;
        if (_state.SuddenDeath is not { } duel) return false;

        var actor = _state.Players.GetValueOrDefault(action.PlayerName);
        if (actor is null) return false;

        switch (action.Action)
        {
            case ActionType.Block:
                duel.HolderBlocked = true;

                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] {actor.Name} gets a hand in — " +
                    $"{duel.Holder} must fight the shot away.");
                return true;

            case ActionType.Shoot:
                // Unblocked, and there is nothing left to stop it.
                if (!duel.HolderBlocked)
                {
                    WinBySuddenDeath(actor, timestamp);
                    return true;
                }

                // Blocked: they still have to beat the block to get it off, and the
                // roll that decides it is the ordinary opposed one. Left to resolve
                // through the normal path so the numbers are compared once, not twice.
                return false;

            default:
                return false;
        }
    }

    /// <summary>End it. A sudden death shot that goes in wins the match outright.</summary>
    private void WinBySuddenDeath(PlayerState scorer, DateTime timestamp)
    {
        var isHome = scorer.Team.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase);

        _state.Score = isHome
            ? _state.Score with { Home = _state.Score.Home + 1 }
            : _state.Score with { Away = _state.Score.Away + 1 };

        scorer.Goals++;
        _state.SuddenDeath = null;
        _state.IsFinished = true;
        _state.Phase = GamePhase.PostGame;

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] [GOAL] {scorer.Name} scores unopposed — " +
            $"{scorer.Team} win it in sudden death.");
    }

    /// <summary>
    /// One shootout attempt: flat roll against a flat keeper roll.
    ///
    /// The tally is kept apart from the match score. Only the winner's single point
    /// joins it at the end, because that point is what breaks the tie — the shootout
    /// goals themselves are not match goals.
    /// </summary>
    private void ResolveShootoutAttempt(
        ActionEvent action, PlayerState? shooter, int shooterRoll, PlayerState keeper, int keeperRoll)
    {
        if (shooter is null) return;

        ReportShootoutOrder(shooter, action.Timestamp);

        var scored = _state.RecordShootoutAttempt(shooter, shooterRoll, keeperRoll);

        action.Outcome = scored ? ActionOutcome.Goal : ActionOutcome.Caught;
        action.Applied = new AppliedEffects();

        if (scored) shooter.Goals++;
        else keeper.Saves++;

        var taken = _state.ShootoutAttempts.Count;

        _state.PlayByPlay.Add(
            $"[{action.Timestamp:HH:mm:ss}] {(scored ? "[GOAL]" : "[SAVE]")} " +
            $"{shooter.Name} {shooterRoll} vs {keeper.Name} {keeperRoll} — " +
            $"shootout {_state.HomeTeam} {_state.ShootoutScore.Home}:{_state.ShootoutScore.Away} " +
            $"{_state.AwayTeam} ({taken}/{BlitzGame.ShootoutAttemptsPerSide * 2}).");

        if (_state.ShootoutComplete)
            ConcludeShootout(action.Timestamp);
    }

    /// <summary>
    /// Note when somebody steps up out of turn.
    ///
    /// The line is fixed — midfielder, then out along it — and sides alternate from
    /// whoever won the roll-off. Advisory, like everything else: a referee decides
    /// whether it mattered.
    /// </summary>
    private void ReportShootoutOrder(PlayerState shooter, DateTime timestamp)
    {
        if (_state.NextShooter() is not { } expected) return;

        var rightTeam = shooter.Team.Equals(expected.Team, StringComparison.OrdinalIgnoreCase);
        if (rightTeam && shooter.Role == expected.Role) return;

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] ⚑ {shooter.Name} stepped up out of turn; " +
            $"expected {expected.Team}'s {Roster.RoleAbbreviation(expected.Role)}.");
    }

    /// <summary>
    /// Award the point that breaks the tie, or send it to sudden death.
    /// </summary>
    private void ConcludeShootout(DateTime timestamp)
    {
        var winner = _state.ShootoutWinner();

        if (winner is null)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] Shootout drawn at {_state.ShootoutScore.Home} — SUDDEN DEATH.");
            return;
        }

        var isHome = winner.Equals(_state.HomeTeam, StringComparison.OrdinalIgnoreCase);

        _state.Score = isHome
            ? _state.Score with { Home = _state.Score.Home + 1 }
            : _state.Score with { Away = _state.Score.Away + 1 };

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] {winner} win the shootout " +
            $"{Math.Max(_state.ShootoutScore.Home, _state.ShootoutScore.Away)}-" +
            $"{Math.Min(_state.ShootoutScore.Home, _state.ShootoutScore.Away)} and take the point.");
    }

    /// <summary>
    /// Lend the midfielder's roll to the team-mate they named.
    ///
    /// The rally beats their team-mate's own roll or it does nothing: "if your roll is
    /// higher than your team member's, they now use your roll in place of their own"
    /// (slide 56). It lasts the phase and no longer.
    ///
    /// Compared on the raw rolls both made. Reading them back through the rallied value
    /// would let a rally measure itself against its own result.
    /// </summary>
    private void ResolveRally(ActionEvent action)
    {
        if (action.TargetName is not { } targetName) return;

        var midfielder = _state.Players.GetValueOrDefault(action.PlayerName);
        var mate = _state.Players.GetValueOrDefault(targetName);

        if (midfielder?.PhaseRoll is not { } rallyRoll) return;
        if (mate?.PhaseRoll is not { } ownRoll) return;

        action.Applied = new AppliedEffects();

        if (rallyRoll <= ownRoll)
        {
            action.Outcome = ActionOutcome.Fail;

            _state.PlayByPlay.Add(
                $"{midfielder.Name} rallies {mate.Name}, but {rallyRoll} does not beat " +
                $"their own {ownRoll} — nothing changes.");
            return;
        }

        mate.RalliedRoll = rallyRoll;

        // Settled before the sweep below, and deliberately so: that sweep reopens
        // everything involving this player, and the rally is one of those. Left pending
        // it would be picked up and resolved again, forever.
        action.Outcome = ActionOutcome.Success;
        midfielder.ActionsSucceeded++;
        action.Applied.ActorSucceeded = true;

        _state.PlayByPlay.Add(
            $"{midfielder.Name} rallies {mate.Name} — they take {rallyRoll} in place of " +
            $"their own {ownRoll} for the phase.");

        // Contests this player was in may already have been settled on the roll they
        // are no longer using, so they are undone and re-decided — the same machinery
        // a referee's re-roll goes through, for the same reason.
        ReopenActionsInvolving(mate, clearActorRoll: false);
        action.Outcome = ActionOutcome.Success;

        ResolveOpposedActions(mate.Name);
    }

    /// <summary>
    /// Whether anyone is in reach of a rally at all.
    ///
    /// The midfielder names who they are rallying — it is never inferred — so this only
    /// answers whether a legal target exists, which is what decides the conversion to
    /// SURVEY. Reach is the midfielder's own zone. Zone rather than waymark, so a
    /// midfielder on 1 can rally a forward on A, which is the deck's own example.
    /// </summary>
    private bool HasRallyTarget(PlayerState player)
    {
        var zone = BlitzGame.ZoneRank(player.Position);
        if (zone < 0) return false;

        foreach (var mate in _state.Players.Values)
        {
            if (ReferenceEquals(mate, player)) continue;
            if (!mate.Team.Equals(player.Team, StringComparison.OrdinalIgnoreCase)) continue;
            if (BlitzGame.ZoneRank(mate.Position) != zone) continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Turn a declared action into the one the rules fall back to.
    ///
    /// Several actions convert rather than being lost when they have no legal target:
    /// a rally with nobody to lend to becomes a survey, a tackle with nobody in reach
    /// becomes a move (slides 56, 59). Reported, because the player asked for one thing
    /// and got another.
    /// </summary>
    private void ConvertAction(ActionEvent evt, PlayerState player, ActionType into, string why, DateTime timestamp)
    {
        var from = evt.Action;
        evt.Action = into;

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] {player.Name} had {why}, so their " +
            $"{from.ToString().ToUpperInvariant()} becomes a {into.ToString().ToUpperInvariant()}.");

        switch (into)
        {
            case ActionType.Survey:
                evt.Outcome = ActionOutcome.Success;
                player.ActionsSucceeded++;
                player.IsSurveying = true;
                break;

            case ActionType.Move:
                // A move with nowhere named is not a move at all; leave it pending
                // rather than inventing a destination.
                if (evt.TargetWaymark is { } to && to != Waymark.None)
                {
                    evt.Outcome = ActionOutcome.Success;
                    player.ActionsSucceeded++;
                }
                break;
        }
    }

    /// <summary>
    /// Hold a movement up while the surveyor watching that lane rolls against it.
    ///
    /// This is the moment a survey is actually worth anything: it was declared a phase
    /// ago and does nothing until somebody tries to come through.
    /// </summary>
    private void OpenSurveyContest(
        PlayerState mover, PlayerState surveyor, Waymark from, Waymark to, DateTime timestamp,
        bool isTackle = false, ActionEvent? tackle = null)
    {
        _state.SurveyContests.Add(new SurveyContest
        {
            Mover = mover.Name,
            Surveyor = surveyor.Name,
            From = from,
            To = to,
            OpenedAt = timestamp,
            IsTackle = isTackle,
            Tackle = tackle,
        });

        var what = isTackle ? "tackling through" : "coming through";

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] {surveyor.Name} is watching {from}–{to} and catches " +
            $"{mover.Name} {what}; they roll it off.");
    }

    /// <summary>
    /// Take a roll as part of a survey roll-off rather than as a phase roll.
    ///
    /// Reposition sits after the phase the move was declared in, so this roll is not the
    /// declaring roll and must not overwrite it.
    /// </summary>
    private bool TryTakeSurveyRoll(PlayerState player, int roll, DateTime timestamp)
    {
        var contest = _state.SurveyContestFor(player.Name);
        if (contest is null) return false;
        if (contest.HasRolled(player.Name)) return false;

        contest.Rolls[player.Name] = roll;

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] {player.Name} rolls {roll} for the lane.");

        if (!contest.Complete)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] Waiting on {string.Join(", ", contest.Outstanding)}.");
            return true;
        }

        SettleSurveyContest(contest, timestamp);
        return true;
    }

    /// <summary>Let the movement through, or stop it dead.</summary>
    private void SettleSurveyContest(SurveyContest contest, DateTime timestamp)
    {
        _state.SurveyContests.Remove(contest);

        if (!_state.Players.TryGetValue(contest.Mover, out var mover)) return;

        // A tie stops the move: the surveyor is the one defending the lane.
        if (contest.MoverWins() is not true)
        {
            // A survey beaten tackle is cancelled, not merely halted, so whatever it
            // already did comes off with it — the daze most of all (slide 59).
            if (contest is { IsTackle: true, Tackle: { } tackle })
            {
                UnapplyAction(tackle);
                tackle.Outcome = ActionOutcome.Fail;

                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] {contest.Surveyor} reads the tackle and cancels it — " +
                    $"{contest.Mover} stays at {contest.From} and nobody is dazed.");
                return;
            }

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] {contest.Surveyor} holds the lane — " +
                $"{contest.Mover} does not get through to {contest.To}.");
            return;
        }

        if (MovePlayer(mover, contest.To, timestamp))
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] {contest.Mover} beats the survey and reaches {contest.To}.");
        }
    }

    /// <summary>
    /// Close survey roll-offs whose rolls never arrived.
    ///
    /// One left open would swallow both players' rolls in the next phase. The mover is
    /// held up rather than let through, since the surveyor defends the lane.
    /// </summary>
    private void CloseOutstandingSurveys(DateTime timestamp)
    {
        foreach (var contest in _state.SurveyContests.ToList())
        {
            _state.SurveyContests.Remove(contest);

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ {contest.Surveyor} and {contest.Mover} never rolled off; " +
                $"the lane holds and {contest.Mover} stays at {contest.From}.");
        }
    }

    /// <summary>
    /// Announce a buzzer shot when a lost ball at the end of a set becomes another one.
    ///
    /// Silent outside the final exchange, and silent once the chain has run its two
    /// links — at which point the set is over rather than continuing.
    /// </summary>
    private void OpenBuzzerShot(string taker, string loser, DateTime timestamp, bool keeperReply = false)
    {
        if (!_state.IsFinalExchange) return;

        if (_state.OpenBuzzerShot(taker, loser, timestamp, keeperReply) is not { } chain)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] Second time the ball has gone — that is the set.");

            _state.ClearBuzzerShot();
            return;
        }

        _state.PlayByPlay.Add(keeperReply
            ? $"[{timestamp:HH:mm:ss}] {taker} caught it at the buzzer and takes an immediate " +
              $"goal-to-goal shot; {loser} rolls to intercept."
            : $"[{timestamp:HH:mm:ss}] BUZZER SHOT — {taker} may shoot from where they stand; " +
              $"{loser} rolls to intercept.");
    }

    /// <summary>
    /// Blocks and dives on a shot, resolved before it ever reaches the keeper.
    ///
    /// Returns true when one of them took the ball and the shot is over. The
    /// <paramref name="settled"/> flag separates "nobody stopped it" from "somebody in
    /// the chain has not rolled yet" — the caller must not hand a half-resolved shot to
    /// the goalkeeper.
    /// </summary>
    private bool TryInterceptBeforeTheNet(ActionEvent action, int shooterRoll, out bool settled)
    {
        settled = false;

        if (!TryBestOf(action.ContestedBy, out var bestBlocker, out var blockRoll)) return false;
        if (!TryBestOf(action.DivedBy, out var bestDiver, out var diveRoll)) return false;

        settled = true;

        // Closest first: a block that gets there ends it before a diver's number is
        // looked at, however high that number was.
        var (taker, takerRoll, how) =
            bestBlocker is not null && blockRoll >= shooterRoll
                ? (bestBlocker, blockRoll, "blocks it down")
            : bestDiver is not null && diveRoll >= shooterRoll
                ? (bestDiver, diveRoll, "dives across it")
                : (null, 0, string.Empty);

        if (taker is null) return false;

        action.Outcome = ActionOutcome.Fail;
        action.Applied = new AppliedEffects();

        _state.PlayByPlay.Add(
            $"[INTERCEPT] {taker} {how} — {action.PlayerName}'s shot never reaches the net " +
            $"({takerRoll} vs {shooterRoll}).");

        SetBallCarrier(taker, action.Timestamp);
        OpenBuzzerShot(taker, action.PlayerName, action.Timestamp);

        return true;
    }

    private void ResolveShoot(ActionEvent action)
    {
        var shooterRoll = GetPlayerPhaseRoll(action.PlayerName);
        if (shooterRoll == null) return;

        // Find the opposing GK
        var shooterPlayer = _state.Players.GetValueOrDefault(action.PlayerName);
        var shooterTeam = shooterPlayer?.Team;
        var gk = _state.Players.Values.FirstOrDefault(p =>
            p.Role == PlayerRole.Goalkeeper &&
            !p.Team.Equals(shooterTeam ?? "", StringComparison.OrdinalIgnoreCase));

        if (gk == null) return; // Can't resolve without knowing GK

        // Block and Dive are both closer to the ball than the keeper, so they settle it
        // first (slide 33). Only a shot nobody cut out on the way reaches the net.
        if (TryInterceptBeforeTheNet(action, shooterRoll.Value, out var resolved))
            return;

        if (!resolved) return;   // somebody in the chain still owes a roll

        var gkRoll = gk.PhaseRoll;
        // GK can contest shots even if they already acted — they always roll vs shots
        // If GK hasn't rolled yet, we can't resolve
        if (gkRoll == null) return;

        // A shootout is settled on the bare numbers. No distance bonus, no GUARD, no
        // class modifier — everyone steps up to the same spot and takes the same shot,
        // which is the entire point of it (slide 28).
        if (_state.Phase == GamePhase.Shootout)
        {
            ResolveShootoutAttempt(action, shooterPlayer, shooterRoll.Value, gk, gkRoll.Value);
            return;
        }

        // Calculate GK bonus (distance-based + guard bonus)
        var shooterPos = shooterPlayer?.Position ?? Waymark.C;
        var gkDistanceBonus = gk.GetGoalkeeperBonus(shooterPos);
        var totalGkBonus = gkDistanceBonus + gk.GuardBonus;
        // Clamp 0-50
        totalGkBonus = Math.Clamp(totalGkBonus, 0, 50);

        // Blitzoff/Blitzon from center gets +30 instead of +20
        if (shooterPos == Waymark.C && (_state.Phase == GamePhase.Blitzoff))
            totalGkBonus = Math.Clamp(gkDistanceBonus + 10 + gk.GuardBonus, 0, 50);

        // Apply BC class bonus for shooter
        var shooterBonus = action.Modifier ?? 0;
        if (shooterPlayer?.Role == PlayerRole.LeftForward || shooterPlayer?.Role == PlayerRole.RightForward)
            shooterBonus += 10; // Improved Shoot
        else if (shooterPlayer?.Role == PlayerRole.Midfield)
            shooterBonus += 5;  // Improved Carrier

        // Strip BC modifier if dazed
        if (shooterPlayer?.IsDazed == true)
            shooterBonus = 0;

        var effectiveShooterRoll = shooterRoll.Value + shooterBonus;
        var effectiveGkRoll = gkRoll.Value + totalGkBonus;

        var applied = action.Applied = new AppliedEffects();

        if (effectiveShooterRoll > effectiveGkRoll)
        {
            action.Outcome = ActionOutcome.Goal;
            if (shooterPlayer != null)
            {
                shooterPlayer.Goals++;
                shooterPlayer.ActionsSucceeded++;
                applied.ActorGoals++;
                applied.ActorSucceeded = true;
            }
            _state.PlayByPlay.Add($"[GOAL] {action.PlayerName} scores! (Roll {effectiveShooterRoll} vs GK {effectiveGkRoll})");
            _state.ResetPositions();
        }
        else
        {
            action.Outcome = ActionOutcome.Caught;
            gk.Saves++;
            applied.GoalkeeperName = gk.Name;
            applied.GoalkeeperSaves = 1;

            // Catching it means having it. Without this the shooter stayed the ball
            // carrier after being saved, and kept taking carrier turns.
            SetBallCarrier(gk.Name, action.Timestamp);

            _state.PlayByPlay.Add($"[SAVE] {gk.Name} catches! (GK {effectiveGkRoll} vs Shooter {effectiveShooterRoll})");

            // A keeper who catches at the buzzer does not simply hold it: they answer
            // with a goal-to-goal shot of their own (slide 27).
            OpenBuzzerShot(gk.Name, action.PlayerName, action.Timestamp, keeperReply: true);
        }
    }

    /// <summary>
    /// Daze a player and strip what the daze takes with it.
    ///
    /// Daze is not only a status — it removes the stat bonuses the player was carrying.
    /// A keeper loses their whole GUARD bonus, and a carrier loses their class modifier
    /// (slides 64, 66). Both were previously left in place, so a dazed keeper kept
    /// defending the net at full strength.
    ///
    /// Recorded into <paramref name="applied"/> so a referee's re-roll can put it back.
    /// </summary>
    private void ApplyDaze(PlayerState target, AppliedEffects applied)
    {
        target.IsDazed = true;
        applied.TargetDazed = true;
        _state.DazeTracker[target.Name] = _state.BallCarrierTurnCount;

        if (!target.IsGoalkeeper) return;

        // Ten, not the lot. Slide 59 is explicit — "their catch bonus is lowered by 10"
        // — and slide 66's "the GUARD is removed" means the one activation they just
        // made, each of which is worth ten. A keeper who has guarded twice keeps the
        // first one.
        var removed = Math.Min(10, target.GuardBonus);

        applied.TargetGuardBonusRemoved = removed;
        target.GuardBonus -= removed;
        target.IsGuarding = false;
    }

    /// <summary>
    /// Apply game effects when an opposed action succeeds.
    /// </summary>
    private void ApplyActionSuccess(ActionEvent action)
    {
        // Every mutation below is recorded so a referee re-roll can reverse it.
        var applied = action.Applied ??= new AppliedEffects();

        if (action.TargetName == null) return;
        var target = _state.Players.GetValueOrDefault(action.TargetName);
        var actor = _state.Players.GetValueOrDefault(action.PlayerName);

        switch (action.Action)
        {
            case ActionType.Tackle:
                // Target becomes DAZED
                if (target != null)
                {
                    // The tackler's move is not applied here. It happens at Reposition
                    // with everyone else's, and it goes to the waymark that was
                    // declared rather than wherever the target has since ended up:
                    // "you both end in the Waymark that you declared in your Action".
                    ApplyDaze(target, applied);
                }
                if (actor != null)
                {
                    actor.Tackles++;
                    applied.ActorTackles++;
                }
                break;

            case ActionType.Taunt:
                if (target != null) ApplyDaze(target, applied);
                break;

            case ActionType.Shove:
                // Target gets moved (positional - we just track success)
                break;

            // Dive is deliberately absent. It arms a state rather than resolving on
            // declaration, and it used to hand the diver possession outright even
            // with no ball anywhere near them.
        }
    }

    /// <summary>
    /// Record a roll against the current phase. Returns whether it replaced an earlier
    /// one, which the caller uses to avoid logging the same roll twice.
    ///
    /// The rules allow one roll per phase, but referees accept re-rolls at their own
    /// discretion, so a later roll supersedes an earlier one. This previously used
    /// <c>PhaseRoll ??= roll</c>, which kept the first roll and silently discarded
    /// every correction.
    /// </summary>
    private bool RecordPhaseRoll(PlayerState player, int roll, DateTime timestamp)
    {
        var superseded = false;

        if (player.PhaseRoll is { } previous)
        {
            if (previous == roll) return true;

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ Advisory: {player.Name} rolled again ({previous} → {roll}).");

            // Whatever the superseded roll decided has to be undone before the new
            // one is resolved, or its effects would be counted twice.
            ReopenActionsInvolving(player, clearActorRoll: false);
            superseded = true;
        }

        player.PhaseRoll = roll;
        return superseded;
    }

    /// <summary>
    /// Judge a completed pass against the distance rules and say what it was.
    ///
    /// Advisory throughout. The referees have already let the pass stand by announcing
    /// it, so this reports what the rules make of it rather than trying to reverse it.
    /// </summary>
    private PassAssessment ReportPassLegality(PlayerState passer, Waymark origin, PlayerState receiver, DateTime timestamp)
    {
        var verdict = _state.AssessPass(passer, origin, receiver);

        switch (verdict.Kind)
        {
            case PassKind.BackPass:
                // Spending it is what locks the team out next round, so it has to be
                // recorded even though nothing here enforces the pass itself.
                _state.RecordBackPass(passer);

                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] Back pass — {passer.Team} cannot use another until round {_state.Round + 2}.");
                break;

            case PassKind.ContestedByKeeper:
                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] Goal to goal: the keeper contests this, and takes the ball if they win it.");
                break;

            case PassKind.ForcedLong:
                // Legal, and still loose. Not flagged: the keeper had no shorter option.
                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] {passer.Name} has to go long — " +
                    $"{verdict.Reason}, so it is a fumble in {receiver.Position}.");
                break;

            case PassKind.Overreach:
                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] ⚑ {passer.Name} threw {Math.Abs(verdict.ZonesAhead)} zones — " +
                    $"{verdict.Reason}. That is a fumble in {receiver.Position}.");
                break;

            case PassKind.KeeperCannotReceive:
            case PassKind.IllegalBackPass:
                _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] ⚑ {verdict.Reason}.");
                break;
        }

        return verdict;
    }

    /// <summary>
    /// Shake the ball loose and call for rolls from everyone in the zone.
    ///
    /// Possession stays with the intended receiver until the contest settles, so the
    /// field always shows the ball somewhere rather than nowhere.
    /// </summary>
    private void OpenFumble(Waymark zone, FumbleCause cause, PlayerState receiver, DateTime timestamp)
    {
        var contest = _state.OpenFumble(zone, cause, receiver.Name, timestamp);
        if (contest is null) return;

        var why = cause == FumbleCause.DazedReceiver
            ? $"{receiver.Name} is dazed and cannot hold it"
            : "the throw was too long";

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] ⚑ FUMBLE in {zone} — {why}. " +
            $"Everyone there rolls: {string.Join(", ", contest.Contenders)}.");
    }

    /// <summary>
    /// Call for a reroll on every action that came out level this phase.
    ///
    /// Deliberately at the phase boundary rather than the moment the tie appears: the
    /// referee waits for the phase to finish, and everything else in it stands
    /// regardless of how the tie eventually falls (slide 32).
    /// </summary>
    private void OpenTieBreaks(DateTime timestamp)
    {
        foreach (var action in _state.CurrentPhaseActions)
        {
            if (action.Outcome != ActionOutcome.Pending) continue;
            if (action.TiedAt is not { } tiedAt) continue;
            if (action.TargetName is not { } defender) continue;

            // Already waiting on one from an earlier boundary.
            if (_state.TieBreaks.Any(t => ReferenceEquals(t.Action, action))) continue;

            _state.TieBreaks.Add(new TieBreak
            {
                Action = action,
                Challenger = action.PlayerName,
                Defender = defender,
                TiedAt = tiedAt,
                OpenedAt = timestamp,
            });

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ Tied at {tiedAt} — {action.PlayerName} and {defender} " +
                $"reroll to settle the {action.Action.ToString().ToUpperInvariant()}.");
        }
    }

    /// <summary>
    /// Close tie-breaks whose reroll never arrived.
    ///
    /// One left open would swallow both players' rolls in the next phase, so it gets a
    /// phase to be settled and then falls to the defending player.
    /// </summary>
    private void ExpireStaleTieBreaks(DateTime timestamp)
    {
        foreach (var contest in _state.TieBreaks.ToList())
        {
            contest.BoundariesSurvived++;
            if (contest.BoundariesSurvived < 2) continue;

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ No reroll came for {contest.Challenger} vs " +
                $"{contest.Defender}; the tie falls to {contest.Defender}.");

            SettleTiedAction(contest, contest.Defender, timestamp, announce: false);
        }
    }

    /// <summary>
    /// Take a roll as part of a tie-break rather than as a phase roll.
    ///
    /// The reroll settles only the pair it belongs to. It is never compared against
    /// anyone else's roll, and it does not revisit comparisons the original roll
    /// already decided — somebody who lost to that roll stays beaten by it. That is
    /// also why it must not overwrite <see cref="PlayerState.PhaseRoll"/>: that roll is
    /// still holding up every other comparison it was part of.
    /// </summary>
    private bool TryTakeTieBreakRoll(PlayerState player, int roll, DateTime timestamp)
    {
        var contest = _state.TieBreakFor(player.Name);
        if (contest is null) return false;
        if (contest.HasRolled(player.Name)) return false;

        contest.Rolls[player.Name] = roll;

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] {player.Name} rerolls {roll} to break the tie.");

        if (!contest.Complete)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] Waiting on {string.Join(", ", contest.Outstanding)}.");
            return true;
        }

        var attempt = contest.Attempt;
        var winner = contest.Settle();

        if (winner is null)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ Tied again on reroll {attempt}. Going again.");
            return true;
        }

        SettleTiedAction(contest, winner, timestamp);
        return true;
    }

    /// <summary>Apply the tie-break's verdict to the action it was settling.</summary>
    private void SettleTiedAction(TieBreak contest, string winner, DateTime timestamp, bool announce = true)
    {
        var action = contest.Action;
        var challengerWon = winner.Equals(contest.Challenger, StringComparison.OrdinalIgnoreCase);

        action.TiedAt = null;
        _state.TieBreaks.Remove(contest);

        if (challengerWon)
        {
            action.Outcome = ActionOutcome.Success;
            action.Applied = new AppliedEffects();

            if (_state.Players.TryGetValue(action.PlayerName, out var actor))
            {
                actor.ActionsSucceeded++;
                action.Applied.ActorSucceeded = true;
            }

            ApplyActionSuccess(action);
        }
        else
        {
            action.Outcome = ActionOutcome.Fail;
        }

        if (!announce) return;

        // Settle() leaves both rolls in place when it hands the tie to the defender on
        // the last attempt, which is how that case is told apart from a clean win.
        var byDefault = contest.Rolls.Count == 2
                        && contest.Rolls[contest.Challenger] == contest.Rolls[contest.Defender];

        _state.PlayByPlay.Add(byDefault
            ? $"[{timestamp:HH:mm:ss}] Rerolls exhausted — {winner} takes it as the defending player."
            : $"[{timestamp:HH:mm:ss}] {winner} wins the reroll; " +
              $"{action.PlayerName}'s {action.Action.ToString().ToUpperInvariant()} " +
              $"{(challengerWon ? "stands" : "is stopped")}.");
    }

    /// <summary>
    /// Settle a contest that is still open when the phase ends.
    ///
    /// A fumble is supposed to resolve on the spot, but rolls do go missing — someone
    /// forgets, or a recording drops a line. An open contest silently eats every later
    /// roll from the players in it, so it is always closed at the boundary: with a
    /// winner if anybody rolled, and abandoned if nobody did.
    /// </summary>
    private void CloseOutstandingFumble(DateTime timestamp)
    {
        if (_state.Fumble is not { } contest) return;

        var outstanding = string.Join(", ", contest.Outstanding);
        var winner = _state.AbandonFumble();

        if (winner is null)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ The fumble in {contest.Zone} went unrolled; possession unchanged.");
            return;
        }

        SetBallCarrier(winner, timestamp);

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] ⚑ Fumble in {contest.Zone} settled on the rolls given — " +
            $"{winner} takes it. Never rolled: {outstanding}.");
    }

    /// <summary>
    /// Take a roll as part of a loose-ball contest rather than as a phase roll.
    ///
    /// Fumble rolls are flat and are made even by players who already rolled this
    /// phase, so they must not touch <see cref="PlayerState.PhaseRoll"/> — that roll is
    /// very often still deciding somebody's action.
    /// </summary>
    private bool TryTakeFumbleRoll(PlayerState player, int roll, DateTime timestamp)
    {
        if (_state.Fumble is not { } contest) return false;
        if (!contest.IsContender(player.Name)) return false;
        if (contest.HasRolled(player.Name)) return false;

        _state.RecordFumbleRoll(player.Name, roll);
        _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] {player.Name} rolls {roll} for the loose ball.");

        if (!contest.Complete)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] Still to roll: {string.Join(", ", contest.Outstanding)}.");
            return true;
        }

        var winner = _state.ResolveFumble();
        if (winner is null) return true;

        SetBallCarrier(winner, timestamp);
        _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] {winner} comes up with it!");

        return true;
    }

    /// <summary>
    /// Who won, in the words a commentator would use.
    ///
    /// A draw is reported plainly rather than as an error: it is the normal state of
    /// affairs going into a shootout.
    /// </summary>
    private string Verdict()
    {
        var (home, away) = (_state.Score.Home, _state.Score.Away);

        if (home == away) return "All square.";

        var winner = home > away ? _state.HomeTeam : _state.AwayTeam;
        return $"{(winner.Length > 0 ? winner : "The leaders")} take it.";
    }

    /// <summary>
    /// Flag play moving on while a goalkeeper is still holding the ball.
    ///
    /// The clearing pass resolves before anything else, so a keeper who still has it
    /// when the next phase is called has held play up — and every phase they keep it
    /// for is one their team spends a player down.
    /// </summary>
    private void ReportUnclearedKeeper(DateTime timestamp)
    {
        if (!_state.KeeperMustClear) return;

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] ⚑ {_state.BallCarrier} still has the ball in goal; " +
            "the clearing pass was owed before this.");
    }

    /// <summary>
    /// Put the roll itself into the commentary.
    ///
    /// A dice game whose log records only outcomes has thrown away the argument. When
    /// a call is disputed what people need to see is the number, and whether the
    /// modifier was counted — so the modifier is shown as arithmetic rather than
    /// folded into a total nobody can check.
    ///
    /// Skipped for re-rolls: the advisory line already names both numbers.
    /// </summary>
    private void LogRoll(PlayerState player, int roll, ActionEvent? action, DateTime timestamp)
    {
        var modifier = action?.Modifier ?? 0;

        var value = modifier switch
        {
            0 => roll.ToString(),
            > 0 => $"{roll} +{modifier} = {roll + modifier}",
            _ => $"{roll} {modifier} = {roll + modifier}",
        };

        var purpose = action is not null && action.Action != ActionType.None
            ? $" for {action.Action.ToString().ToUpperInvariant()}"
            : string.Empty;

        _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] {player.Name} rolls {value}{purpose}.");
    }

    /// <summary>
    /// Find the action this roll belongs to: normally one declared this phase, but
    /// failing that one whose phase closed moments ago.
    /// </summary>
    private ActionEvent? FindActionAwaitingRoll(PlayerState player, DateTime timestamp)
    {
        for (var i = _state.CurrentPhaseActions.Count - 1; i >= 0; i--)
        {
            var action = _state.CurrentPhaseActions[i];
            if (action.Roll is not null) continue;
            if (!action.PlayerName.Equals(player.Name, StringComparison.OrdinalIgnoreCase)) continue;

            // An uncontested move is not awaiting anything. Binding a roll to it would
            // both misreport what the roll was for and eat a roll the player may have
            // needed for a real contest.
            if (!_state.CallsForRoll(action)) continue;

            return action;
        }

        // Rolls routinely land a beat after the referee calls the phase. The action
        // used to be discarded at the boundary, so the straggler resolved nothing
        // while still counting toward the player's roll average.
        for (var i = _recentlyClosed.Count - 1; i >= 0; i--)
        {
            var action = _recentlyClosed[i];
            if (action.Roll is not null) continue;
            if (!action.PlayerName.Equals(player.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (timestamp - action.Timestamp > LateRollWindow) continue;
            if (!_state.CallsForRoll(action)) continue;

            // Attribution is corrected here, but the opposing roll for that phase is
            // already gone, so the outcome stays pending for a human to judge.
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] Late roll: {player.Name}'s {action.Action} from the previous phase.");
            return action;
        }

        return null;
    }

    /// <summary>
    /// The roll a player is standing on, which is not always the one they made.
    ///
    /// A rallied player uses their midfielder's roll in place of their own for the
    /// phase, so everything that compares rolls has to come through here.
    /// </summary>
    private int? GetPlayerPhaseRoll(string playerName)
    {
        if (!_state.Players.TryGetValue(playerName, out var p)) return null;

        return p.RalliedRoll ?? p.PhaseRoll;
    }

    /// <summary>
    /// Whether this player had a legitimate reason to roll without declaring an
    /// action of their own: they are defending against one.
    /// </summary>
    private bool HasDefensiveReasonToRoll(PlayerState player)
    {
        // The ball carrier reacts to whatever is aimed at them.
        if (_state.BallCarrier is not null &&
            _state.BallCarrier.Equals(player.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var action in _state.CurrentPhaseActions)
        {
            var isActor = action.PlayerName.Equals(player.Name, StringComparison.OrdinalIgnoreCase);
            if (isActor) continue;

            // Named as the target of something opposed.
            if (IsOpposedAction(action.Action) &&
                action.TargetName?.Equals(player.Name, StringComparison.OrdinalIgnoreCase) == true)
                return true;

            // A goalkeeper contests every shot without being named as its target.
            if (action.Action == ActionType.Shoot && player.Role == PlayerRole.Goalkeeper)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Actions decided by rolling against their target there and then.
    ///
    /// Block is deliberately absent: it is established on declaration and lies in
    /// wait, firing only if the blocked player later tries to pass or move.
    /// </summary>
    private static bool IsOpposedAction(ActionType action) => action is
        ActionType.Tackle or ActionType.Shove or ActionType.Taunt;

    #endregion

    #region Action Declarations

    private bool TryParseAction(string sender, string message, DateTime timestamp)
    {
        // Primary format: [ACTION → Target] or [ACTION -> Target] or [ACTION]
        var actionMatch = RegexActionDeclaration().Match(message);
        if (!actionMatch.Success)
        {
            // Secondary format: [ACTION] [TargetName] (RALLY uses this)
            actionMatch = RegexActionWithSeparateTarget().Match(message);
            if (!actionMatch.Success)
            {
                // Tertiary format: [ACTION]s to [Target] or [ACTION] to Target
                actionMatch = RegexActionLooseTarget().Match(message);
                if (!actionMatch.Success)
                {
                    // Quaternary format: unbracketed "attempts to TACKLE PlayerName!"
                    actionMatch = RegexActionUnbracketed().Match(message);
                    if (!actionMatch.Success)
                        return false;
                }
            }
        }

        var actionStr = actionMatch.Groups[1].Value.Trim();
        var targetStr = actionMatch.Groups.Count > 2 ? actionMatch.Groups[2].Value.Trim() : "";

        var actionType = ParseActionType(actionStr);
        if (actionType == ActionType.None)
            return false;

        // Only rostered players declare actions. This is the main defence against
        // commentary and crowd chatter being scored as gameplay: previously any
        // sender whose message matched an action pattern was created as a player.
        var player = ResolvePlayer(sender);
        if (player is null)
            return false;

        var evt = new ActionEvent
        {
            Timestamp = timestamp,
            PlayerName = player.Name,
            Action = actionType,
            Outcome = ActionOutcome.Pending,
        };

        // Parse target — could be a player name or a waymark
        var cleanTarget = targetStr.TrimEnd(']', '!', ' ');
        if (cleanTarget.StartsWith('+') || cleanTarget.StartsWith('-'))
        {
            // This is a modifier, not a target (e.g., [SHOOT +10])
            if (int.TryParse(cleanTarget, out var inlineMod))
                evt.Modifier = inlineMod;
        }
        else
        {
            var waymark = ParseWaymark(cleanTarget);
            if (waymark != Waymark.None)
            {
                evt.TargetWaymark = waymark;
            }
            else if (!string.IsNullOrWhiteSpace(cleanTarget))
            {
                // Quiet: a bracketed token is only a guess at a name, and may well
                // be neither a waymark nor a player.
                evt.TargetName = ResolveQuiet(cleanTarget)?.Name;
            }
        }

        // If no target found in brackets, try to find a known player in the message text
        // (e.g., "points at Mhinco Pokhmhakwaahni and attempts to [BLOCK]")
        if (string.IsNullOrEmpty(evt.TargetName) && evt.TargetWaymark == null
            && actionType is not ActionType.Move and not ActionType.Guard
                          and not ActionType.Rush and not ActionType.Survey)
        {
            var proseTarget = FindTargetPlayerInText(player.Name, message);
            if (proseTarget != null)
                evt.TargetName = proseTarget;
        }

        // Check for class modifier like [FWD: +10] or [MID: +5]
        var modMatch = RegexClassModifier().Match(message);
        if (modMatch.Success)
        {
            evt.Modifier = int.Parse(modMatch.Groups[1].Value);
        }

        player.ActionsAttempted++;

        // Blocks standing against this player fire now, if they are passing or
        // moving. Every blocker rolls against them at once, and they must out-roll
        // the best of them, so the action cannot be credited yet.
        var isCarrier = _state.BallCarrier is not null &&
                        _state.BallCarrier.Equals(player.Name, StringComparison.OrdinalIgnoreCase);

        // Reasons the declared action does not happen at all, rather than happening and
        // failing. Nothing about it is credited or applied.
        var actionRefused = isCarrier
                            && _state.BallCarrierMustShoot
                            && actionType != ActionType.Shoot;

        if (actionRefused)
        {
            evt.Outcome = ActionOutcome.Fail;

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ {player.Name} must SHOOT here; " +
                $"{actionType.ToString().ToUpperInvariant()} is not a valid action.");
        }
        else if (isCarrier && !BlitzGame.CarrierMayDeclare(actionType))
        {
            // Carrying the ball closes everything but MOVE, PASS and SHOOT — no
            // tackling, no blocking, no specialty actions while you have it (slide 52).
            evt.Outcome = ActionOutcome.Fail;
            actionRefused = true;

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ {player.Name} has the ball; " +
                $"{actionType.ToString().ToUpperInvariant()} is not open to the carrier.");
        }

        List<string>? contestedBy = null;
        var moveBlocked = false;

        if (actionType is ActionType.Pass or ActionType.Move or ActionType.Shoot)
        {
            var blockers = _state.BlockersOf(player.Name);

            if (blockers.Count > 0 && actionType == ActionType.Move)
            {
                moveBlocked = true;
                // A blocked player does not move at all. For the ball carrier the
                // rulebook is explicit: blocked, they may only shoot or pass.
                evt.Outcome = ActionOutcome.Fail;

                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] ⚑ {player.Name} is blocked and cannot move; " +
                    "only SHOOT or PASS is available.");
            }
            else if (blockers.Count > 0)
            {
                contestedBy = [.. blockers];
                evt.ContestedBy = contestedBy;

                var what = actionType == ActionType.Shoot ? "shot" : "pass";

                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] {blockers.Count} block(s) contest {player.Name}'s {what}.");
            }

            // A defender lying in wait gets a roll at anything arriving in their zone,
            // whether it was aimed at a team-mate or at the net. Attached at declaration
            // so the ball is known to be contested before anyone rolls — otherwise the
            // passer's roll would look unnecessary.
            //
            // A shot travels to the goal, so that is the destination it is measured to.
            var arrivesAt = actionType == ActionType.Shoot
                ? _state.AttackingGoal(player)
                : evt.TargetName is { } aimedAt && _state.Players.TryGetValue(aimedAt, out var intended)
                    ? intended.Position
                    : Waymark.None;

            if (arrivesAt != Waymark.None)
            {
                var divers = _state.DiversCovering(player.Position, arrivesAt, player.Team);

                if (divers.Count > 0)
                {
                    evt.DivedBy = divers.ConvertAll(d => d.Name);

                    _state.PlayByPlay.Add(
                        $"[{timestamp:HH:mm:ss}] {string.Join(", ", evt.DivedBy)} " +
                        $"{(divers.Count == 1 ? "is" : "are")} diving on {arrivesAt}.");
                }
            }
        }

        // Auto-resolve non-opposed actions immediately. Skipped entirely when the
        // carrier was obliged to shoot: the declared action does not happen at all,
        // so nothing about it should be credited or applied.
        switch (actionRefused ? ActionType.None : actionType)
        {
            case ActionType.Block:
                // A block is established on declaration and then waits. It does not
                // roll against anyone now: it fires if the blocked player passes or
                // moves later in the phase.
                if (evt.TargetName is null) break;

                if (!BlitzGame.CanBlock(player))
                {
                    evt.Outcome = ActionOutcome.Fail;
                    _state.PlayByPlay.Add(
                        $"[{timestamp:HH:mm:ss}] ⚑ {player.Name} is a goalkeeper and has no BLOCK action.");
                    break;
                }

                if (_state.AddBlock(evt.TargetName, player.Name))
                {
                    evt.Outcome = ActionOutcome.Success;
                    player.ActionsSucceeded++;
                    player.Blocks++;

                    if (_state.Players.TryGetValue(evt.TargetName, out var blocked))
                        blocked.IsBlocked = true;

                    // Blocking a blocker negates what they were doing — a held team-mate
                    // is freed. Block battles are how a side gets its carrier moving
                    // again, and the counter-blocked player can no longer contest them.
                    var freed = _state.CancelBlocksBy(evt.TargetName);

                    if (freed.Count > 0)
                    {
                        _state.PlayByPlay.Add(
                            $"[{timestamp:HH:mm:ss}] {player.Name} blocks the blocker — " +
                            $"{evt.TargetName} can no longer hold {string.Join(", ", freed)}.");
                    }

                    // Both players end up in the blocked state (slide 44): getting in
                    // somebody's way costs you your own freedom to move.
                    player.IsBlocked = true;
                }
                else
                {
                    // Goalkeepers are immune, and a player already held by three
                    // blockers cannot take a fourth: it converts to a Survey.
                    evt.Outcome = ActionOutcome.Fail;

                    var immune = _state.Players.TryGetValue(evt.TargetName, out var refused)
                                 && refused.Role == PlayerRole.Goalkeeper;

                    _state.PlayByPlay.Add(immune
                        ? $"[{timestamp:HH:mm:ss}] ⚑ {evt.TargetName} is a goalkeeper and cannot be blocked."
                        : $"[{timestamp:HH:mm:ss}] ⚑ {evt.TargetName} already has " +
                          $"{BlitzGame.MaxBlockersPerPlayer} blockers; {player.Name}'s block converts to SURVEY.");
                }
                break;

            case ActionType.Move:
                // If no waymark from the action regex, scan message for a waymark target
                if (evt.TargetWaymark == null || evt.TargetWaymark == Waymark.None)
                {
                    var wmMatch = RegexWaymarkInMessage().Match(message);
                    if (wmMatch.Success)
                        evt.TargetWaymark = ParseWaymark(wmMatch.Groups[1].Value);
                }

                // The carrier's movement is far narrower than anyone else's: it has to
                // go toward the enemy goal, so retreating and crossing within a zone are
                // both out. Where a restriction leaves them nowhere to go, the ball has
                // to move instead of the player (slide 52).
                if (isCarrier && evt.TargetWaymark is { } wanted && wanted != Waymark.None
                    && !_state.CarrierMayMoveTo(player, wanted))
                {
                    evt.Outcome = ActionOutcome.Fail;

                    var why = _state.CanOccupy(player, wanted)
                        ? "the carrier must move toward the enemy goal"
                        : $"a {Roster.RoleAbbreviation(player.Role)} cannot enter {wanted}";

                    _state.PlayByPlay.Add(
                        $"[{timestamp:HH:mm:ss}] ⚑ {player.Name} cannot carry the ball to {wanted}; " +
                        $"{why}. SHOOT or PASS instead.");
                    break;
                }

                if (contestedBy is null && !moveBlocked)
                {
                    evt.Outcome = ActionOutcome.Success;
                    player.ActionsSucceeded++;
                }
                // Update position if waymark target specified
                // Moves declared during a ring phase do not take effect when they are
                // announced: the whole ring repositions together once the phase ends.
                // Applying them early meant a later contest in the same phase measured
                // reach from somewhere the player had not gone yet.
                //
                // The ball carrier's own turn has no reposition after it, so theirs
                // takes effect straight away.
                // A contested move waits on the roll-off before it can land at all.
                if (evt.TargetWaymark is { } destination && destination != Waymark.None
                    && contestedBy is null && !moveBlocked
                    && !PhaseRules.IsSimultaneousActionPhase(_state.Phase))
                {
                    MovePlayer(player, destination, timestamp);
                }
                break;

            case ActionType.Guard:
                evt.Outcome = ActionOutcome.Success;
                player.ActionsSucceeded++;
                // Increase GK goalie bonus by 10 (clamped 0-50)
                if (player.IsGoalkeeper && !player.IsDazed)
                {
                    player.GuardBonus = Math.Min(50, player.GuardBonus + 10);
                    player.IsGuarding = true;
                }
                break;

            case ActionType.Rush:
                evt.Outcome = ActionOutcome.Success;
                player.ActionsSucceeded++;
                // Place a Rush Gate, recording whose it is. A keeper cannot leave
                // their goal, so this is how they reach the rest of the field.
                if (evt.TargetWaymark is { } gateAt && gateAt != Waymark.None)
                {
                    _state.PlaceRushGate(new RushGate(
                        player.Name, player.Team, gateAt, _state.Set, _state.Round, timestamp));

                    _state.PlayByPlay.Add(
                        $"[{timestamp:HH:mm:ss}] {player.Name} places a Rush Gate at {gateAt}.");
                }
                break;

            case ActionType.Dive:
                // Diving is the defender's speciality. It arms a state that lasts
                // until their next turn: if an enemy sends the ball into their zone,
                // they get a roll at it even though it was never meant for them.
                if (!BlitzGame.CanDive(player))
                {
                    evt.Outcome = ActionOutcome.Fail;

                    _state.PlayByPlay.Add(
                        $"[{timestamp:HH:mm:ss}] ⚑ {player.Name} " +
                        $"({Roster.RoleAbbreviation(player.Role)}) has no DIVE action; it belongs to defenders.");
                    break;
                }

                evt.Outcome = ActionOutcome.Success;
                player.ActionsSucceeded++;
                player.Dives++;
                player.IsDiving = true;
                break;

            case ActionType.Survey:
                // Keepers have no Move, Survey or Block action at all (slide 62).
                // Move is covered by their never leaving goal and Block is refused
                // below; Survey was the one that slipped through.
                if (player.IsGoalkeeper)
                {
                    _state.PlayByPlay.Add(
                        $"[{timestamp:HH:mm:ss}] ⚑ {player.Name} is a goalkeeper and has no SURVEY action.");
                    break;
                }

                evt.Outcome = ActionOutcome.Success;
                player.ActionsSucceeded++;
                player.IsSurveying = true;

                // The standard macros name the lane: "[SURVEY][A ←→ B]". When it is
                // declared, take it from the post rather than inferring it from where
                // the player happens to be swimming.
                var laneMatch = RegexSurveyLane().Match(message);
                if (laneMatch.Success)
                {
                    var laneFrom = ParseWaymark(laneMatch.Groups[1].Value);
                    var laneTo = ParseWaymark(laneMatch.Groups[2].Value);

                    if (laneFrom != Waymark.None && laneTo != Waymark.None && laneFrom != laneTo)
                        player.SurveyedLane = (laneFrom, laneTo);
                }
                break;

            case ActionType.Rally:
                // Rally lends the midfielder's roll to a team-mate they name in their
                // own zone. With nobody in reach there is nothing to lend it to, and
                // the action becomes a SURVEY rather than being lost (slide 56).
                if (!HasRallyTarget(player))
                {
                    ConvertAction(evt, player, ActionType.Survey,
                        "no team-mate in their zone to rally", timestamp);
                    break;
                }

                // Otherwise it stays pending until both rolls are in.
                break;

            case ActionType.Pass:
                // Passes carry unless something contests them: a block standing against
                // the passer, or a defender diving on where the ball is headed.
                if (contestedBy is null && evt.DivedBy is not { Count: > 0 })
                {
                    evt.Outcome = ActionOutcome.Success;
                    player.ActionsSucceeded++;
                }
                break;

            // Opposed actions stay Pending until rolls resolve them:
            // Tackle, Block, Shove, Taunt, Shoot
        }

        _state.CurrentPhaseActions.Add(evt);
        _state.GameLog.Add(evt);

        // The captains' duel has its own shape, and an unblocked shot ends the match
        // there and then rather than going through the goalkeeper.
        if (TryResolveSuddenDeath(evt, timestamp))
            return true;

        // Teams and roles come from the roster, so there is nothing to infer here.

        // A tackle nobody was named for has nothing to resolve against, and the deck
        // converts it to a MOVE rather than losing it (slide 59). Only when a waymark
        // was called too — a tackle with neither a target nor a destination is just an
        // unparsed post, and inventing a move from it would be worse than leaving it.
        if (actionType == ActionType.Tackle && evt.TargetName is null
            && evt.TargetWaymark is { } fallbackTo && fallbackTo != Waymark.None)
        {
            ConvertAction(evt, player, ActionType.Move, "nobody in reach to tackle", timestamp);
        }

        // Tackling belongs to forwards, and their reach runs along their row rather
        // than being confined to their own zone.
        if (actionType == ActionType.Tackle && evt.TargetName is not null)
        {
            var tackled = _state.Players.GetValueOrDefault(evt.TargetName);

            // Lock in where the target stands right now. A tackle carries the tackler
            // to the waymark they called, and if the target moves away in the same
            // phase the tackler still ends up where they aimed.
            if (tackled is not null && tackled.Position != Waymark.None)
                evt.TargetWaymark = tackled.Position;

            if (tackled is not null && !_state.CanTackle(player, tackled))
            {
                var reason = !BlitzGame.CanTackle(player)
                    ? $"a {Roster.RoleAbbreviation(player.Role)} is not a forward"
                    : !_state.CanOccupy(player, tackled.Position)
                    // The tackler ends up standing there, so this is the move rule
                    // biting, not the reach rule.
                    ? $"a {Roster.RoleAbbreviation(player.Role)} cannot enter {tackled.Position}"

                    // A referee says "lane" and "zone". The code's rows are the
                    // rulebook's lanes and its columns are the rulebook's zones, so
                    // the internal names are not the ones to say out loud.
                    : $"{tackled.Position} is not in their lane or their zone";

                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] ⚑ Advisory: {player.Name} tackled " +
                    $"{tackled.Name}, but {reason}.");

                // Reach and role stay advisory — the tracker may simply have someone in
                // the wrong zone, and referees decide. A goal the tackler may not stand
                // in is different: the tackle ends with them standing there, so it is
                // not a declaration they can make at all. Failing it here stops it
                // dazing the target and stops Reposition carrying them in.
                if (!_state.CanOccupy(player, tackled.Position))
                    evt.Outcome = ActionOutcome.Fail;
            }
        }

        // A phase activates one ring of the sphere. Someone declaring from outside it
        // is either acting out of turn, or standing somewhere the tracker does not
        // think they are: worth surfacing either way, but never worth blocking.
        if (player.Position != Waymark.None
            && (PhaseRules.IsSimultaneousActionPhase(_state.Phase) || PhaseRules.IsBallCarrierPhase(_state.Phase))
            && !_state.CanActThisPhase(player))
        {
            var standing = PhaseRules.IsOuterZone(player.Position) ? "outer" : "inner";
            var active = PhaseRules.ActiveZones(_state.Phase) is { Count: > 0 } zones
                ? (PhaseRules.IsOuterZone(zones[0]) ? "outer" : "inner")
                : "no";

            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ Advisory: {player.Name} acted from {player.Position} " +
                $"({standing} ring) while the {active} ring is active.");
        }

        // The rule is post the action, then roll. But several people legitimately roll
        // without posting anything: the named target of an opposed action, the
        // goalkeeper contesting a shot, and the ball carrier reacting to something
        // aimed at them. Flagging those meant the players following the rules drew
        // the false alarms, so only flag when no defensive reason exists.
        //
        // This stays advisory. Referees are the authority on whether it mattered.
        if (player.PhaseRoll != null
            && evt.Outcome == ActionOutcome.Pending
            && !HasDefensiveReasonToRoll(player))
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ Advisory: {player.Name} rolled before posting.");
        }

        return true;
    }

    private static ActionType ParseActionType(string s)
    {
        var upper = s.ToUpperInvariant().Trim();
        return upper switch
        {
            "TACKLE" => ActionType.Tackle,
            "BLOCK" => ActionType.Block,
            "MOVE" => ActionType.Move,
            "DIVE" => ActionType.Dive,
            "PASS" => ActionType.Pass,
            "SHOOT" or "SHOOT!" => ActionType.Shoot,
            "GUARD" => ActionType.Guard,
            "TAUNT" => ActionType.Taunt,
            "RALLY" => ActionType.Rally,
            "SHOVE" => ActionType.Shove,
            "SURVEY" => ActionType.Survey,
            "RUSH" => ActionType.Rush,
            _ => ActionType.None,
        };
    }

    private static Waymark ParseWaymark(string s)
    {
        return s.Trim().ToUpperInvariant() switch
        {
            "D" => Waymark.D,
            "1" => Waymark.One,
            "A" => Waymark.A,
            "C" => Waymark.C,
            "2" => Waymark.Two,
            "B" => Waymark.B,
            "4" => Waymark.Four,
            _ => Waymark.None,
        };
    }

    #endregion

    #region Status Effects

    /// <summary>
    /// Outcomes the referees state outright, rather than ones worked out from rolls.
    ///
    /// These are worth more than anything inferred. A player narrates what they are
    /// attempting; an official says what happened. Where the two disagree the official
    /// is right, and taking their word removes a whole class of drift — every place the
    /// tracker would otherwise have to guess which roll opposed which, and compound the
    /// error for the rest of the match when it guessed wrong.
    /// </summary>
    private bool TryParseOfficialCall(string message, DateTime timestamp)
    {
        // "<< INTERCEPTION BY Shizuka Hirano! >>" — possession has moved, whatever the
        // rolls in Yell looked like.
        var intercept = RegexInterception().Match(message);
        if (intercept.Success)
        {
            var thief = ResolvePlayer(intercept.Groups[1].Value.Trim());
            if (thief is not null)
            {
                SetBallCarrier(thief.Name, timestamp);

                // An interception is what a successful BLOCK or DIVE buys you — both
                // "intercept balls" per slides 55 and 60 — so it counts as one rather
                // than needing a statistic of its own.
                thief.Blocks++;

                _state.PlayByPlay.Add(
                    $"[{timestamp:HH:mm:ss}] INTERCEPTED by {thief.Name}!");
            }
            return true;
        }

        // "[[SURVEY - Ffon Aveross ]] PULLED" / "[[SHOVE - Manami Tsukino ]] SHOVED"
        var pull = RegexOfficialPull().Match(message);
        if (pull.Success)
        {
            var player = ResolvePlayer(pull.Groups[2].Value.Trim());
            if (player is not null)
            {
                var kind = pull.Groups[1].Value.ToUpperInvariant();

                // Whatever they were part-way through does not happen. A survey that
                // pulls somebody stops the movement outright, so leaving the declared
                // move pending would walk them to a waymark they never reached.
                CancelPendingMovement(player, timestamp);

                _state.PlayByPlay.Add(kind == "SURVEY"
                    ? $"[{timestamp:HH:mm:ss}] {player.Name} is PULLED by a survey — the move does not happen."
                    : $"[{timestamp:HH:mm:ss}] {player.Name} is SHOVED out of position.");
            }
            return true;
        }

        // "[Tie rolloff: Akii & Venn]" — the referee naming a tie the tracker may not
        // have spotted, usually because one of the two rolls never parsed.
        var tie = RegexTieRolloff().Match(message);
        if (tie.Success)
        {
            OpenAnnouncedTieBreak(
                tie.Groups[1].Value.Trim(), tie.Groups[2].Value.Trim(), timestamp);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Drop a declared-but-unresolved move, because an official says it did not happen.
    /// </summary>
    private void CancelPendingMovement(PlayerState player, DateTime timestamp)
    {
        foreach (var action in _state.CurrentPhaseActions)
        {
            if (action.Outcome != ActionOutcome.Pending) continue;
            if (!action.PlayerName.Equals(player.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (action.Action is not (ActionType.Move or ActionType.Tackle)) continue;

            UnapplyAction(action);
            action.Outcome = ActionOutcome.Fail;
        }

        // A survey contest already under way is settled by the call instead of by rolls.
        foreach (var contest in _state.SurveyContests.ToList())
        {
            if (!contest.Mover.Equals(player.Name, StringComparison.OrdinalIgnoreCase)) continue;
            _state.SurveyContests.Remove(contest);
        }
    }

    /// <summary>
    /// Open a tie-break the referee called, attaching it to the contest it belongs to.
    ///
    /// Officials write first names only ("Tie rolloff: Akii &amp; Venn"), and a tie-break
    /// has to hang off the action it settles. When no pending contest involves both
    /// players there is nothing to attach it to, so it is reported rather than invented —
    /// a tie-break against the wrong action would resolve the wrong contest.
    /// </summary>
    private void OpenAnnouncedTieBreak(string firstName, string secondName, DateTime timestamp)
    {
        var one = ResolvePlayer(firstName);
        var two = ResolvePlayer(secondName);

        if (one is null || two is null)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ Referee called a tie roll-off between {firstName} " +
                $"and {secondName}, who are not both on the roster.");
            return;
        }

        var contest = _state.CurrentPhaseActions.FirstOrDefault(a =>
            a.Outcome == ActionOutcome.Pending &&
            a.TargetName is not null &&
            ((a.PlayerName.Equals(one.Name, StringComparison.OrdinalIgnoreCase) &&
              a.TargetName.Equals(two.Name, StringComparison.OrdinalIgnoreCase)) ||
             (a.PlayerName.Equals(two.Name, StringComparison.OrdinalIgnoreCase) &&
              a.TargetName.Equals(one.Name, StringComparison.OrdinalIgnoreCase))));

        if (contest is null)
        {
            _state.PlayByPlay.Add(
                $"[{timestamp:HH:mm:ss}] ⚑ Referee called a tie roll-off between {one.Name} " +
                $"and {two.Name}, but no open contest between them was seen. Both should reroll.");
            return;
        }

        if (_state.TieBreaks.Any(t => ReferenceEquals(t.Action, contest))) return;

        _state.TieBreaks.Add(new TieBreak
        {
            Action = contest,
            Challenger = contest.PlayerName,
            Defender = contest.TargetName!,
            TiedAt = contest.TiedAt ?? one.PhaseRoll ?? 0,
            OpenedAt = timestamp,
        });

        _state.PlayByPlay.Add(
            $"[{timestamp:HH:mm:ss}] Referee calls a tie roll-off: {one.Name} and {two.Name} reroll.");
    }

    private bool TryParseStatus(string message, DateTime timestamp)
    {
        // [[ DAZED - PlayerName ]]
        var dazedMatch = RegexDazed().Match(message);
        if (dazedMatch.Success)
        {
            var player = ResolvePlayer(dazedMatch.Groups[1].Value.Trim());
            if (player is not null)
            {
                // Through the same path as a daze the tracker worked out for itself, so
                // an announced one also strips the keeper's GUARD. Registers with the
                // tracker too — without that an announced daze never expired.
                ApplyDaze(player, new AppliedEffects());

                _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] {player.Name} is DAZED!");
            }
            return true;
        }

        return false;
    }

    #endregion

    #region Referee Corrections

    /// <summary>
    /// Handle the referee vocabulary for fixing mistakes.
    ///
    /// The rule is post your action, then roll. Rolling first draws a flag, and the
    /// referee may give grace, in which case the player re-rolls. From a real log:
    /// "Boro's roll came before the post, as seen by the referee team. Boro will
    /// need to re-roll for their action, as grace was given."
    /// </summary>
    private bool TryParseCorrection(string sender, string message, DateTime timestamp)
    {
        // A bare flag names nobody. It is advisory: the referee decides what follows.
        if (RegexFlag().IsMatch(message))
        {
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] ⚑ FLAG raised by {sender}.");
            return true;
        }

        // "Boro Tumet [Midgardsormr] [ERROR - GRACE]"
        var errorGrace = RegexErrorGrace().Match(message);
        if (errorGrace.Success)
        {
            ApplyGrace(errorGrace.Groups[1].Value, timestamp);
            return true;
        }

        // "[[ GRACE GIVEN -- J'dextera Sol ]]"
        var grace = RegexGrace().Match(message);
        if (grace.Success)
        {
            ApplyGrace(grace.Groups[1].Value, timestamp);
            return true;
        }

        // "REROLL Mhin/Sata", "[[REROLL BORO vs KURAI]]", "[[KHAS vs KURAI REROLL]]"
        if (RegexRerollKeyword().IsMatch(message))
        {
            var payload = RegexRerollKeyword().Replace(message, " ");
            var voided = 0;

            foreach (var token in RegexNameSeparator().Split(payload))
            {
                var name = token.Trim(' ', '[', ']', '.', '!', '"', ':');
                if (name.Length == 0) continue;

                // Referees abbreviate heavily here, so allow prefix matching.
                var canonical = Index?.ResolveShorthand(name);
                if (canonical is null) continue;

                var player = _state.Players.GetValueOrDefault(canonical);
                if (player is null) continue;

                VoidRoll(player, "Re-roll called", timestamp);
                voided++;
            }

            if (voided == 0)
                _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Re-roll called, but no roster name matched.");

            return true;
        }

        return false;
    }

    private void ApplyGrace(string rawName, DateTime timestamp)
    {
        var player = ResolveQuiet(rawName) ??
                     (Index?.ResolveShorthand(rawName) is { } canonical
                         ? _state.Players.GetValueOrDefault(canonical)
                         : null);

        if (player is null)
        {
            _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] Grace given, but '{rawName.Trim()}' is not on the roster.");
            return;
        }

        VoidRoll(player, "Grace given", timestamp);
    }

    /// <summary>
    /// Discard a player's roll for this phase and reopen anything it decided, so the
    /// replacement roll is the one that counts.
    /// </summary>
    private void VoidRoll(PlayerState player, string reason, DateTime timestamp)
    {
        player.PhaseRoll = null;
        ReopenActionsInvolving(player, clearActorRoll: true);

        _state.PlayByPlay.Add($"[{timestamp:HH:mm:ss}] {reason}: {player.Name} re-rolls.");
    }

    /// <summary>
    /// Undo any already-applied outcome that depended on this player's roll and set
    /// those actions back to pending, ready to resolve again.
    /// </summary>
    private void ReopenActionsInvolving(PlayerState player, bool clearActorRoll)
    {
        foreach (var action in _state.CurrentPhaseActions)
        {
            var isActor = action.PlayerName.Equals(player.Name, StringComparison.OrdinalIgnoreCase);
            var isTarget = action.TargetName?.Equals(player.Name, StringComparison.OrdinalIgnoreCase) == true;

            // A goalkeeper contests every shot without ever being named as its target.
            var contestsShot = action.Action == ActionType.Shoot
                               && player.Role == PlayerRole.Goalkeeper
                               && !isActor;

            if (!isActor && !isTarget && !contestsShot) continue;

            if (action.Outcome != ActionOutcome.Pending)
                UnapplyAction(action);

            if (isActor && clearActorRoll)
                action.Roll = null;
        }
    }

    /// <summary>
    /// Reverse the state changes a resolved action made, so a corrected roll does not
    /// double-count stats or leave a stale daze behind.
    /// </summary>
    private void UnapplyAction(ActionEvent action)
    {
        var applied = action.Applied;
        action.Outcome = ActionOutcome.Pending;
        action.Applied = null;

        if (applied is null) return;

        var actor = _state.Players.GetValueOrDefault(action.PlayerName);
        if (actor is not null)
        {
            if (applied.ActorSucceeded)
                actor.ActionsSucceeded = Math.Max(0, actor.ActionsSucceeded - 1);

            actor.Tackles = Math.Max(0, actor.Tackles - applied.ActorTackles);
            actor.Blocks = Math.Max(0, actor.Blocks - applied.ActorBlocks);
            actor.Dives = Math.Max(0, actor.Dives - applied.ActorDives);
            actor.Goals = Math.Max(0, actor.Goals - applied.ActorGoals);

            if (applied.ActorPreviousPosition is { } previous)
                actor.Position = previous;

            if (applied.ActorBlockedSet)
                actor.IsBlocked = false;
        }

        var target = action.TargetName is null
            ? null
            : _state.Players.GetValueOrDefault(action.TargetName);

        if (target is not null)
        {
            if (applied.TargetDazed)
            {
                target.IsDazed = false;
                _state.DazeTracker.Remove(target.Name);
            }

            if (applied.TargetBlockedSet)
                target.IsBlocked = false;

            if (applied.TargetGuardBonusRemoved > 0)
                target.GuardBonus = Math.Min(50, target.GuardBonus + applied.TargetGuardBonusRemoved);
        }

        if (applied.GoalkeeperName is not null &&
            _state.Players.TryGetValue(applied.GoalkeeperName, out var keeper))
        {
            keeper.Saves = Math.Max(0, keeper.Saves - applied.GoalkeeperSaves);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// When [ACTION] has no explicit target, look for a rostered player named in the
    /// message text (e.g. "points at Mhinco Pokhmhakwaahni and attempts to [BLOCK]").
    ///
    /// Searches roster names only, and requires word boundaries so a short name cannot
    /// match inside a longer one.
    /// </summary>
    private string? FindTargetPlayerInText(string senderCanonical, string message)
    {
        var index = Index;
        if (index is null) return null;

        string? best = null;
        var bestIdx = int.MaxValue;

        foreach (var name in index.Names)
        {
            if (name.Equals(senderCanonical, StringComparison.OrdinalIgnoreCase)) continue;

            var idx = IndexOfWord(message, name);
            if (idx >= 0 && idx < bestIdx)
            {
                best = name;
                bestIdx = idx;
            }
        }

        return best;
    }

    /// <summary>
    /// Case-insensitive search requiring non-letter boundaries on both sides, so a
    /// short name cannot match inside a longer one.
    /// </summary>
    private static int IndexOfWord(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle) || needle.Length > haystack.Length) return -1;

        var start = 0;
        while (start <= haystack.Length - needle.Length)
        {
            var idx = haystack.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            var beforeOk = idx == 0 || !char.IsLetter(haystack[idx - 1]);
            var after = idx + needle.Length;
            var afterOk = after >= haystack.Length || !char.IsLetter(haystack[after]);

            if (beforeOk && afterOk) return idx;

            start = idx + 1;
        }

        return -1;
    }

    #endregion

    #region Regex (source-generated)

    // Both restarts. Referees write "<< BLITZOFF >>"; a Blitzon is called inline and in
    // passing, "[BLITZON.]", so square brackets and trailing punctuation count too.
    //
    // Brackets of some kind are required, and that is the whole point: the crowd shouts
    // "BLITZOFF!!!" at kickoff, and matching bare text restarts the match on every cheer.
    [GeneratedRegex(@"<<\s*BLITZ(?:OFF|ON)\s*>>|\[\s*BLITZ(?:OFF|ON)\s*[.!]?\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex RegexBlitzoff();

    // "[Teams, please reset for Blitzon.  Barracuda ball.]" — names the side receiving,
    // which by slide 15 is the side behind on score.
    [GeneratedRegex(@"reset\s+for\s+Blitzon\s*[.,]?\s*(.+?)\s+ball", RegexOptions.IgnoreCase)]
    private static partial Regex RegexBlitzonBall();

    // "[My mistake, Barracuda had a +10 due to being down one point.]" — the halftime
    // bonus is ten per point of deficit, so announcing it states the gap.
    [GeneratedRegex(@"([\w'\- ]+?)\s+had\s+a\s+\+\d+\s+due\s+to\s+being\s+down\s+(\w+)\s+point", RegexOptions.IgnoreCase)]
    private static partial Regex RegexDeficitBonus();

    [GeneratedRegex(@"<<\s*ROUND\s+(\d+)\s*>>", RegexOptions.IgnoreCase)]
    private static partial Regex RegexRound();

    [GeneratedRegex(@"(\d+)\s+ROUNDS?\s+TO\s+BUZZER", RegexOptions.IgnoreCase)]
    private static partial Regex RegexBuzzer();

    // "[[ FIRST -- SIM RED ]]" / "FIRST: SIM RED" — the captains' roll-off result.
    [GeneratedRegex(@"FIRST\s*(?:--|[-:])\s*([^\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexShootoutFirst();

    [GeneratedRegex(@"REPOSITION", RegexOptions.IgnoreCase)]
    private static partial Regex RegexReposition();

    [GeneratedRegex(@"\[\[\s*(\w[\w\s]*?)\s+(\d+):(\d+)\s+(\w[\w\s]*?)\s*\]\]")]
    private static partial Regex RegexScore();

    // How scorekeepers actually call it, in plain Yell: "Vidraal 2 - 1 Barracudas."
    // Also catches "Halftiiiiime! Vidraal 1 - 0 Barracudas." because the score is read
    // out of the tail rather than anchored to the start.
    //
    // Deliberately loose, and safe only because both names are checked against the
    // roster before it is believed — a bare "N - M" between two words is far too common
    // in ordinary chat to trust on shape alone.
    [GeneratedRegex(@"([A-Za-z][\w']*(?:\s+[A-Za-z][\w']*)?)\s+(\d+)\s*[-–—:]\s*(\d+)\s+([A-Za-z][\w']*(?:\s+[A-Za-z][\w']*)?)")]
    private static partial Regex RegexScoreSpoken();

    // "[Final score: Vidraal - 2, Barracuda - 1]"
    [GeneratedRegex(@"Final\s+score\s*:\s*([\w'\s]+?)\s*[-–]\s*(\d+)\s*,\s*([\w'\s]+?)\s*[-–]\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexFinalScore();

    [GeneratedRegex(@"\[\[\s*([\w\s']+?)\s*[-,]?\s*BALL\s*GET\s*!?\s*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex RegexBallGet();

    [GeneratedRegex(@"\[BALL\s+to\s+([\w\s']+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex RegexBallTo();

    // "<< PASS COMPLETE TO Kauan Jaguaribara >>" is the spelling officials actually use;
    // requiring a square bracket meant none of them were read.
    [GeneratedRegex(@"PASS\s+COMPLETE\s+to\s+([\w\s'\-]+?)\s*[!]?\s*(?:\]|>>)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexPassComplete();

    // "<< INTERCEPTION BY Shizuka Hirano! >>"
    [GeneratedRegex(@"INTERCEPT(?:ION|ED)?\s+by\s+([\w\s'\-]+?)\s*[!]?\s*(?:\]|>>)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexInterception();

    // "[[SHOVE - Manami Tsukino ]] SHOVED" and "[[SURVEY - Ffon Aveross ]] PULLED".
    //
    // Read the same way as "[[ DAZED - Name ]]": the brackets name the player the call is
    // about and the trailing word says what happened to them. That is the only reading
    // consistent across all three, but it is a reading — if referees turn out to name the
    // surveyor rather than the player pulled, this is the line to change.
    [GeneratedRegex(@"\[\[\s*(SHOVE|SURVEY)\s*[-–]\s*([\w\s'\-]+?)\s*\]\]\s*(SHOVED|PULLED)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexOfficialPull();

    // "[Tie rolloff: Akii & Venn]", "[Tie reroll: Vesper - Verre]", "[Tie rolloff - Kesac - Verre]".
    // Officials use first names here, which ResolvePlayer already handles.
    //
    // Both brackets are optional because referees type these at speed mid-match and drop
    // one often enough to matter — "Tie reroll - Kauan - Tua]" is from a real game. A
    // missed roll-off leaves a contest unresolved for the rest of the phase, so it is
    // worth being generous about the punctuation.
    [GeneratedRegex(@"\[?\s*Tie\s+(?:roll\s*off|re\s*roll)\s*[-:]?\s*([\w'\-]+)\s*(?:&|-|vs\.?|and)\s*([\w'\-]+)\s*\]?", RegexOptions.IgnoreCase)]
    private static partial Regex RegexTieRolloff();

    // Referees write this as "<< CAUGHT BY Akii Malaguld! >>" as often as with square
    // brackets, so the closer has to be either — matching only "]" missed every
    // catch the officials actually called.
    [GeneratedRegex(@"CAUGHT(?:\s+by\s+([\w\s'\-]+?))?\s*[!]?\s*(?:\]|>>)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexCaught();

    // The game puts a dice glyph between "a" and the number — "rolls a  7" — and it is
    // a private-use character, not whitespace. Requiring digits straight after "a" meant
    // no roll in a real match parsed at all, so nothing ever resolved.
    // The name is captured loosely and normalised afterwards, because a crossworld
    // roller arrives as "Name<glyph>World" and a character class that stops at the
    // glyph captures the world instead of the player.
    // The /random line arrives with no sender at all — "[Dice Roll] : Random! Name rolls
    // a 40" — so the leading punctuation is part of the text the name is cut from. With
    // a permissive capture the lazy match starts before "Random!" and swallows it, and
    // every roll is then credited to a player called ": Random! Name". Excluding colons
    // and exclamation marks is enough: neither can appear in a character name.
    [GeneratedRegex(@"(?:Random!\s*)?([^:!]+?)\s+rolls\s+a\s+\D{0,4}?(\d+)\s+\(out\s+of\s+100\)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexDiceRoll();

    [GeneratedRegex(@"You\s+roll\s+a\s+\D{0,4}?(\d+)\s+\(out\s+of\s+100\)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexYouRoll();

    [GeneratedRegex(@"\[(TACKLE|BLOCK|MOVE|DIVE|PASS|SHOOT!?|GUARD|TAUNT|RALLY|SHOVE|SURVEY|RUSH)\s*(?:→|-{1,5}>|>|\bto\b)?\s*([\w\s'+\-]*?)\]", RegexOptions.IgnoreCase)]
    private static partial Regex RegexActionDeclaration();

    // Secondary pattern: [ACTION] [TargetName] — used by RALLY
    // "[TACKLE] Name!" and "[RALLY] on: Name!" — the second is how rally names its
    // target in practice, and the colon stopped the capture dead.
    [GeneratedRegex(@"\[(TACKLE|BLOCK|MOVES?|DIVE|PASS|SHOOT!?|GUARD|TAUNT|RALLY|SHOVE|SURVEY|RUSH)\]\s*(?:on\s*:?\s*)?\[?([\w\s'\-]+?)\]?!", RegexOptions.IgnoreCase)]
    private static partial Regex RegexActionWithSeparateTarget();

    // Tertiary pattern: [ACTION]s to [Target] or [ACTION] to Target.
    [GeneratedRegex(@"\[(TACKLE|BLOCK|MOVES?|DIVE|PASS|SHOOT!?|GUARD|TAUNT|RALLY|SHOVE|SURVEY|RUSH)\]s?\s+(?:the\s+[Bb]all\s+)?(?:to|toward|towards)\s+\[?([\w\s'\-]+?)\]?\.?", RegexOptions.IgnoreCase)]
    private static partial Regex RegexActionLooseTarget();

    // Quaternary pattern: unbracketed "attempts to TACKLE PlayerName!" (no [ ] brackets)
    // Target is limited to capitalized words (FFXIV name format: "First Last")
    // Hyphens are load-bearing in FFXIV surnames — Abd-al-daiya, Djt-marouc,
    // Iron-breaker — and leaving them out of the class does not fail to match, it
    // matches a prefix. "Qasim Abd-al-daiya" silently becomes "Qasim Abd", which
    // resolves to nobody, and the action is lost rather than reported.
    [GeneratedRegex(@"attempts?\s+to\s+(TACKLE|BLOCK|MOVES?|DIVE|PASS|SHOOT|GUARD|TAUNT|RALLY|SHOVE|SURVEY|RUSH)\s+([A-Z][\w'\-]+(?:\s+[A-Z][\w'\-]+)*)", RegexOptions.None)]
    private static partial Regex RegexActionUnbracketed();

    // Fallback waymark finder: matches waymark letter/number after "to", arrows, or in brackets
    // Used when MOVE action regex didn't capture a target (e.g., "[MOVE]s to [1]")
    [GeneratedRegex(@"(?:\bto\b|→|-{1,5}>|>)\s*\[?([1-4AaBbCcDd])\]?", RegexOptions.IgnoreCase)]
    private static partial Regex RegexWaymarkInMessage();

    // Class modifier like [FWD: +10] or [MID: +5] or [DEF: +10]
    [GeneratedRegex(@"\[(?:FWD|MID|DEF|MIDFIELD|FORWARD|DEFENDER):\s*\+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexClassModifier();

    [GeneratedRegex(@"[+](\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexModifier();

    // The separator is a hyphen and so are half the surnames — "[[ DAZED - Qasim
    // Abd-al-daiya ]]". The leading "[-–]" eats the separator, so the capture can safely
    // keep hyphens; without them it stops mid-surname and dazes a player who does not
    // exist, leaving the real one standing.
    [GeneratedRegex(@"DAZED\s*[-–]\s*([\w\s'\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexDazed();

    // Referees write "GRACE GIVEN -- Name", "GRACE GIVEN - Name" and "Grace Given Name",
    // so the separator has to tolerate repeated or absent dashes.
    [GeneratedRegex(@"GRACE\s+GIVEN\s*[-–—]*\s*([\w\s'\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexGrace();

    // "[Flag]" and "[[FLAG]]" — a referee marking something wrong, with no name.
    [GeneratedRegex(@"\[+\s*FLAG\s*\]+", RegexOptions.IgnoreCase)]
    private static partial Regex RegexFlag();

    // "Boro Tumet [Midgardsormr] [ERROR - GRACE]" — the name comes first here.
    [GeneratedRegex(@"^(.*?)\s*\[\s*ERROR\s*[-–—]?\s*GRACE\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex RegexErrorGrace();

    [GeneratedRegex(@"RE-?ROLL", RegexOptions.IgnoreCase)]
    private static partial Regex RegexRerollKeyword();

    // "[SURVEY][A ←→ B]" and its variants, as written by the standard macros.
    [GeneratedRegex(@"\[\s*([1-4ABCDabcd])\s*(?:←→|<-+>|↔|<>|-+)\s*([1-4ABCDabcd])\s*\]")]
    private static partial Regex RegexSurveyLane();

    // Referees join re-roll names with any of "/", "vs", "and", "," or "&".
    [GeneratedRegex(@"\s*(?:/|\bvs\.?\b|\band\b|,|&)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex RegexNameSeparator();

    #endregion
}
