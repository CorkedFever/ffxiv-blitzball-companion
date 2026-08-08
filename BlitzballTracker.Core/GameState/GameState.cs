namespace BlitzballTracker.Core.GameState;

/// <summary>
/// The 7 waymarks on the Blitzsphere grid.
/// </summary>
public enum Waymark
{
    None,
    D,      // Red Goal Zone (top)
    One,    // Red Strike Zone (number lane)
    A,      // Red Strike Zone (letter lane)
    C,      // Center Zone
    Two,    // Yellow Strike Zone (number lane)
    B,      // Yellow Strike Zone (letter lane)
    Four,   // Yellow Goal Zone (bottom)
}

/// <summary>
/// The three ways the ball gets put back into play (slide 15).
/// </summary>
public enum BlitzoffKind
{
    /// <summary>Both midfielders roll for it. Kickoff, and after a goal that levelled the scores.</summary>
    Standard,

    /// <summary>
    /// After a goal that did not level the scores: the side that is behind simply
    /// receives the ball, with no roll-off at all.
    /// </summary>
    Blitzon,

    /// <summary>
    /// The opening of Set 2: a roll-off, but the trailing side takes +10 per point of
    /// deficit.
    /// </summary>
    HalftimeRestart,
}

public enum GamePhase
{
    PreGame,
    Blitzoff,           // Midfielders roll for ball (or Blitzon: losing team auto-gets)
    OuterHuddle,        // 15s planning — players in Strike Zones (A/1 & B/2)
    OuterPhase,         // Players in A/1 & B/2 act (60s max). Ball Carrier does NOT act.
    OuterReposition,    // Successful moves from Outer Phase resolve
    BallCarrierOuter,   // BC acts if in Strike Zone (A/1 or B/2). No reposition needed.
    InnerHuddle,        // 15s planning — players in Center (C) & Goal (D/4)
    InnerPhase,         // Players in C & D/4 act (60s max). Ball Carrier does NOT act.
    InnerReposition,    // Successful moves from Inner Phase resolve
    BallCarrierInner,   // BC acts if in C or D/4. Round 10 = MUST SHOOT.
    BuzzerPhase,        // End of Round 10, ball in strike zone: same-waymark players act, then BC MUST SHOOT
    Halftime,           // Between Set 1 and Set 2. Teams switch sides.
    Shootout,           // Tied after Set 2: 5 shots each (M, LF, RF, LD, RD), flat rolls, no modifiers
    SuddenDeath,        // Tied after Shootout: captains at C, alternating blitzoff/block/shoot
    PostGame,
}

/// <summary>
/// Player positions/roles on a team (6 per team).
/// </summary>
public enum PlayerRole
{
    None,
    Midfield,       // M  — starts at C
    LeftForward,    // LF — starts in enemy strike zone
    RightForward,   // RF — starts in enemy strike zone
    LeftDefender,   // LD — starts in own strike zone
    RightDefender,  // RD — starts in own strike zone
    Goalkeeper,     // GK — starts at own goal (D or 4), does not move
}

public enum ActionType
{
    None,
    Tackle,
    Block,
    Move,
    Dive,
    Pass,
    Shoot,
    Guard,
    Taunt,
    Rally,
    Shove,
    Survey,
    Rush,
}

public enum ActionOutcome
{
    Pending,
    Success,
    Fail,
    Dazed,
    Fumble,
    Caught,
    Goal,
}

public record struct Score(int Home, int Away);

/// <summary>
/// A gate placed on the field by a goalkeeper, with enough context to attribute it
/// and to reason about how long it has been standing.
/// </summary>
public sealed record RushGate(
    string PlacedBy,
    string Team,
    Waymark Position,
    int Set,
    int Round,
    DateTime PlacedAt)
{
    /// <summary>
    /// Which of the keeper's turns this was placed on.
    ///
    /// A gate lasts until the start of its placer's next turn (slide 65), and a
    /// goalkeeper's turn comes round in the inner phase — so that, not the round, is
    /// the clock it runs on.
    /// </summary>
    public int PlacedOnTurn { get; init; }
}

public class PlayerState
{
    public string Name { get; set; } = string.Empty;
    public string? World { get; set; }
    public string Team { get; set; } = string.Empty;
    public PlayerRole Role { get; set; } = PlayerRole.None;
    public Waymark Position { get; set; } = Waymark.None;
    public bool IsDazed { get; set; }
    public bool IsBlocked { get; set; }
    public bool IsDiving { get; set; }
    public bool IsSurveying { get; set; }
    public bool IsGuarding { get; set; }
    public bool HasBall { get; set; }

    /// <summary>
    /// Placed on standby, having declared nothing before the timer ran out.
    ///
    /// Only ever set when <see cref="RuleOptions.StandbyStatus"/> is switched on: the
    /// status was retired, though the loss of action it represented was not.
    /// </summary>
    public bool IsStandby { get; set; }

    /// <summary>
    /// Whether this player has been substituted off.
    ///
    /// They stay in the player list so the match keeps their stats — the goals and
    /// tackles belong to whoever earned them — but they are off the field and do not
    /// come back on at the next goal.
    /// </summary>
    public bool IsSubstituted { get; set; }

    /// <summary>
    /// Granted a follow-up move by reaching their own side's Rush Gate.
    ///
    /// The gate acts as a relay: a teammate who moves onto it may move on again,
    /// which is how the ball reaches places no single lane connects, such as D to C
    /// or C to Four. Cleared when the phase ends.
    /// </summary>
    public bool HasGateMove { get; set; }

    /// <summary>
    /// The lane a surveyor is watching, as the pair of zones it runs between.
    ///
    /// Survey guards movement along a lane rather than holding a node, and the lane
    /// is never declared: the player picks it by swimming between two markers in the
    /// arena. So it can only be read from where they are actually standing.
    /// </summary>
    public (Waymark From, Waymark To)? SurveyedLane { get; set; }
    public bool IsGoalkeeper => Role == PlayerRole.Goalkeeper;

    /// <summary>
    /// The player's roll for the CURRENT phase. Reset when a new phase starts.
    /// Null means they haven't rolled yet this phase.
    /// </summary>
    public int? PhaseRoll { get; set; }

    /// <summary>
    /// A midfielder's roll lent to this player by a successful RALLY, used in place of
    /// their own for the rest of the phase (slide 56).
    /// </summary>
    public int? RalliedRoll { get; set; }

    /// <summary>
    /// GK's current goalie bonus modifier (from GUARD action, +10 each).
    /// Clamped 0-50. Reset if DAZED.
    /// </summary>
    public int GuardBonus { get; set; }

    // Stats
    public int ActionsAttempted { get; set; }
    public int ActionsSucceeded { get; set; }
    public int TotalRolls { get; set; }
    public long RollSum { get; set; }
    public int Goals { get; set; }
    public int Saves { get; set; }
    public int Tackles { get; set; }
    public int Blocks { get; set; }
    public int Dives { get; set; }
    public int ShootoutGoals { get; set; }

    public double RollAverage => TotalRolls > 0 ? (double)RollSum / TotalRolls : 0;
    public double SuccessRate => ActionsAttempted > 0 ? (double)ActionsSucceeded / ActionsAttempted : 0;

    /// <summary>
    /// Get the base GK catch bonus by distance from the net.
    /// </summary>
    public int GetGoalkeeperBonus(Waymark shooterPosition) => shooterPosition switch
    {
        Waymark.D or Waymark.Four => 0,          // Shooting from own goal (goal-to-goal)
        Waymark.One or Waymark.A => 10,           // Strike zone (1 away)
        Waymark.C => 20,                          // Center (2 away)
        Waymark.Two or Waymark.B => 30,           // Far strike zone (3 away)
        _ => 0,
    };
}

public class ActionEvent
{
    public DateTime Timestamp { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public ActionType Action { get; set; }
    public string? TargetName { get; set; }
    public Waymark? TargetWaymark { get; set; }
    public int? Roll { get; set; }
    public int? Modifier { get; set; }

    /// <summary>
    /// Set when this action's opposed roll came out level, so the phase can end by
    /// calling for a reroll rather than leaving it Pending forever.
    /// </summary>
    public int? TiedAt { get; set; }

    /// <summary>
    /// Defenders in a dive state who can cut this ball out of the air.
    ///
    /// Kept apart from <see cref="ContestedBy"/> because the two are not peers: a block
    /// is closer to the ball than a dive and beats it outright, so they are resolved in
    /// tiers rather than pooled into one roll-off (slide 33).
    /// </summary>
    public List<string>? DivedBy { get; set; }
    public ActionOutcome Outcome { get; set; } = ActionOutcome.Pending;

    /// <summary>
    /// What resolving this action changed. Recorded so that a referee-ordered
    /// re-roll or grace can reverse it before resolving again. Null while pending.
    /// </summary>
    public AppliedEffects? Applied { get; set; }

    /// <summary>
    /// Blockers contesting this action.
    ///
    /// Set when a blocked player tries to pass or move: the blocks that were standing
    /// against them all fire at once, and they must out-roll the best of them.
    /// </summary>
    public List<string>? ContestedBy { get; set; }
}

/// <summary>
/// The mutations a resolved action made to game state.
///
/// Referees call re-rolls (usually on ties) and give grace when someone rolls
/// before posting. Either can change an outcome that was already applied, so the
/// original effects have to be undone rather than double-counted.
/// </summary>
public sealed class AppliedEffects
{
    public bool ActorSucceeded { get; set; }
    public int ActorTackles { get; set; }
    public int ActorBlocks { get; set; }
    public int ActorDives { get; set; }
    public int ActorGoals { get; set; }

    public bool ActorBlockedSet { get; set; }
    public bool TargetBlockedSet { get; set; }
    public bool TargetDazed { get; set; }

    /// <summary>Where the actor stood before a successful tackle moved them.</summary>
    public Waymark? ActorPreviousPosition { get; set; }

    /// <summary>Guard bonus removed from a taunted goalkeeper, to be restored on undo.</summary>
    public int TargetGuardBonusRemoved { get; set; }

    /// <summary>Goalkeeper credited with a save, if any.</summary>
    public string? GoalkeeperName { get; set; }
    public int GoalkeeperSaves { get; set; }
}

public partial class BlitzGame
{
    public bool IsActive { get; set; }
    public string HomeTeam { get; set; } = string.Empty;  // Letter Lane (A-B side)
    public string AwayTeam { get; set; } = string.Empty;  // Number Lane (1-2 side)
    public Score Score { get; set; }
    public int Set { get; set; } = 1;                     // 1 or 2
    public int Round { get; set; }                        // 1-10 per set
    private GamePhase _phase = GamePhase.PreGame;

    public GamePhase Phase
    {
        get => _phase;
        set
        {
            if (_phase == value) return;

            _phase = value;
            PhaseStartedAt = DateTime.Now;
        }
    }

    /// <summary>
    /// When the current phase began, for the countdown.
    ///
    /// Stamped from the wall clock rather than the message timestamp so it stays
    /// meaningful during live play, which is the only time a countdown is useful.
    /// </summary>
    public DateTime PhaseStartedAt { get; private set; } = DateTime.Now;

    /// <summary>Time left in the current phase, or null when the phase has no clock.</summary>
    public TimeSpan? PhaseRemaining
    {
        get
        {
            var duration = PhaseTiming.For(Phase);
            if (duration is null) return null;

            var remaining = duration.Value - (DateTime.Now - PhaseStartedAt);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    public string? BallCarrier { get; set; }
    public string? BallTeam { get; set; }
    public int RoundsRemaining { get; set; }

    /// <summary>
    /// Whether the final whistle has gone.
    ///
    /// Deliberately separate from <see cref="IsActive"/>. A finished match is still
    /// worth looking at — the final score, where everyone ended up, the whole
    /// play-by-play — so clearing IsActive would send the display back to "waiting for
    /// kickoff" the moment the game got interesting to review.
    /// </summary>
    public bool IsFinished { get; set; }

    /// <summary>
    /// Whether any referee phase call has been seen.
    ///
    /// Referees post the structure — phases, rounds, the score — in the league's
    /// cross-world linkshell, while players declare and roll in Yell. A spectator
    /// without the linkshell sees the play but never the structure, so the phase, round
    /// and score stay at their opening values however long the match runs. Worth
    /// distinguishing from a match that genuinely has not started.
    /// </summary>
    public bool HasPhaseFeed { get; set; }

    /// <summary>
    /// Which team attacks which goal. Home attacks 4 (yellow) in Set 1, D (red) in Set 2.
    /// </summary>
    public Waymark HomeGoalTarget { get; set; } = Waymark.Four;
    public Waymark AwayGoalTarget { get; set; } = Waymark.D;

    /// <summary>
    /// Track BC turn count so we know when DAZE expires ("end of BC's next turn").
    /// Incremented each time a BC phase starts.
    /// </summary>
    public int BallCarrierTurnCount { get; set; }

    /// <summary>
    /// Players dazed this turn: maps player name → BC turn count when DAZE was applied.
    /// DAZE clears at end of BC turn when BallCarrierTurnCount > dazeAppliedTurn.
    /// </summary>
    public Dictionary<string, int> DazeTracker { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Waymark where a Rush Gate is placed (by GK). None = no active gate.
    /// </summary>
    /// <summary>
    /// Rush Gates standing on the field, keyed by the team that placed them.
    ///
    /// A goalkeeper is the one role that cannot move, so a gate is their only way to
    /// reach beyond their own goal. Each keeper may have one at a time, and since
    /// there is one keeper per side, keying by team enforces that on its own:
    /// placing a second simply moves theirs.
    /// </summary>
    public Dictionary<string, RushGate> RushGates { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Blocks standing against each player this phase: the blocked player's name,
    /// mapped to everyone blocking them.
    ///
    /// A block does not resolve when it is declared. It waits, and fires as a
    /// roll-off if the blocked player later tries to pass or move. Several blocks can
    /// stand against the same player at once, and all of them roll together.
    /// </summary>
    public Dictionary<string, List<string>> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockersOf(string playerName) =>
        Blocks.TryGetValue(playerName, out var blockers) ? blockers : [];

    /// <summary>At most three enemies can block one player; a fourth converts to Survey.</summary>
    public const int MaxBlockersPerPlayer = 3;

    /// <summary>
    /// Whether this player can declare a block at all.
    ///
    /// Goalkeepers have no access to Move, Survey or Block: they hold their line and
    /// use their own specialities instead.
    /// </summary>
    public static bool CanBlock(PlayerState actor) => actor.Role != PlayerRole.Goalkeeper;

    /// <summary>
    /// Clear every diving state.
    ///
    /// Run at the start of an acting phase rather than at the carrier's turn: a dive
    /// is armed during the ring's phase and has to still be live when the ball
    /// actually moves.
    /// </summary>
    public void ClearDives()
    {
        foreach (var player in Players.Values)
            player.IsDiving = false;
    }

    /// <summary>
    /// Whether an action is decided by a roll at all.
    ///
    /// A basic move is not. You declare where you are going and you go there — the
    /// dice only come out when something contests the movement, which in practice
    /// means a block standing against you. Everything else on the list is opposed by
    /// somebody and is settled by rolling.
    ///
    /// This matters beyond cosmetics: a roll must not be consumed by an action that
    /// never needed one. A player who declares a move and then a contest in the same
    /// phase has one roll, and it belongs to the contest.
    ///
    /// Note this only decides whether a roll belongs to the action. How a blocked move
    /// then resolves — a roll-off, or refused outright — is the open question in
    /// RULES-BACKLOG, and either answer leaves the attribution here correct.
    /// </summary>
    public bool CallsForRoll(ActionEvent action)
    {
        // GUARD is a buff, not a contest. The keeper takes +10 to their bonus and there
        // is nothing on the other side of it to roll against.
        //
        // SURVEY is not rolled for when it is declared either. It arms a guard over a
        // lane, and the roll-off happens at Reposition if somebody actually tries to
        // come through it.
        if (action.Action is ActionType.Guard or ActionType.Survey) return false;

        // A pass inside its range carries automatically (slide 41). What turns one into
        // a contest is somebody trying to stop it: a block standing against the passer,
        // or a defender diving on its destination. Goal to goal is opposed by the
        // keeper, but that is reported rather than resolved, so nothing binds here.
        if (action.Action == ActionType.Pass)
            return action.ContestedBy is { Count: > 0 } || action.DivedBy is { Count: > 0 };

        if (action.Action != ActionType.Move) return true;
        if (action.ContestedBy is { Count: > 0 }) return true;

        // Being blocked is the thing that turns a move into a contest.
        return Players.TryGetValue(action.PlayerName, out var mover) && mover.IsBlocked;
    }

    /// <summary>Diving is the defender's speciality.</summary>
    public static bool CanDive(PlayerState actor) =>
        actor.Role is PlayerRole.LeftDefender or PlayerRole.RightDefender;

    /// <summary>
    /// Enemy defenders in a diving state who could contest a ball arriving in
    /// <paramref name="destination"/>.
    ///
    /// The ball has to travel <em>into</em> their zone, so a pass or shot released
    /// from within it cannot be dived on. Zone rather than waymark: a defender on A
    /// can reach a ball headed for 1, which is the whole point of the action.
    /// </summary>
    public List<PlayerState> DiversCovering(Waymark origin, Waymark destination, string movingTeam)
    {
        var caught = new List<PlayerState>();

        var destinationZone = ZoneRank(destination);
        if (destinationZone < 0) return caught;

        // Released from inside the zone it lands in: nothing to intercept on the way.
        if (ZoneRank(origin) == destinationZone) return caught;

        foreach (var player in Players.Values)
        {
            if (!player.IsDiving) continue;

            // Never intercepts their own side's ball.
            if (player.Team.Equals(movingTeam, StringComparison.OrdinalIgnoreCase)) continue;

            if (ZoneRank(player.Position) == destinationZone)
                caught.Add(player);
        }

        return caught;
    }

    /// <summary>
    /// Register a block. Returns false when it cannot stand: goalkeepers are immune,
    /// and a player already held by three blockers cannot be blocked again.
    /// </summary>
    public bool AddBlock(string blocked, string blocker)
    {
        // Goalkeepers are completely immune to BLOCK, though they can be tackled.
        if (Players.TryGetValue(blocked, out var target) && target.Role == PlayerRole.Goalkeeper)
            return false;

        if (!Blocks.TryGetValue(blocked, out var blockers))
        {
            blockers = [];
            Blocks[blocked] = blockers;
        }

        if (blockers.Contains(blocker, StringComparer.OrdinalIgnoreCase)) return true;
        if (blockers.Count >= MaxBlockersPerPlayer) return false;

        blockers.Add(blocker);
        return true;
    }

    /// <summary>
    /// Take away every block this player was holding on somebody else.
    ///
    /// Blocking a blocker negates what they were doing — block battles are how a team
    /// frees a held ball carrier. Returns who they had been blocking, for the record.
    /// </summary>
    public List<string> CancelBlocksBy(string blocker)
    {
        var freed = new List<string>();

        foreach (var (blocked, blockers) in Blocks)
        {
            if (blockers.RemoveAll(b => b.Equals(blocker, StringComparison.OrdinalIgnoreCase)) == 0)
                continue;

            freed.Add(blocked);

            // Nobody left holding them means they are not blocked any more.
            if (blockers.Count == 0 && Players.TryGetValue(blocked, out var released))
                released.IsBlocked = false;
        }

        return freed;
    }

    /// <summary>
    /// Sweep every standing block.
    ///
    /// Done at the start of an acting phase, not when the ball carrier's turn opens:
    /// blocks are declared during the ring's phase and have to survive into the
    /// carrier's turn, since that is when they fire.
    /// </summary>
    public void ClearBlocks()
    {
        Blocks.Clear();

        foreach (var player in Players.Values)
            player.IsBlocked = false;
    }

    /// <summary>The player watching a given lane, if anyone is.</summary>
    public PlayerState? SurveyorOf(Waymark from, Waymark to)
    {
        foreach (var player in Players.Values)
        {
            if (player.SurveyedLane is not { } lane) continue;

            if ((lane.From == from && lane.To == to) || (lane.From == to && lane.To == from))
                return player;
        }

        return null;
    }

    /// <summary>The gate standing on a zone, if any.</summary>
    public RushGate? RushGateAt(Waymark waymark)
    {
        foreach (var gate in RushGates.Values)
        {
            if (gate.Position == waymark) return gate;
        }

        return null;
    }

    public void PlaceRushGate(RushGate gate) =>
        RushGates[gate.Team] = gate with { PlacedOnTurn = InnerPhaseCount };

    /// <summary>
    /// How many inner phases have opened.
    ///
    /// The clock a Rush Gate runs on. A gate belongs to a goalkeeper and a keeper's
    /// turn comes round in the inner phase, so "the start of your next turn" is the
    /// next inner phase — not the next round.
    /// </summary>
    public int InnerPhaseCount { get; set; }

    /// <summary>
    /// Sweep gates whose placer has come round to their next turn (slide 65).
    ///
    /// Previously cleared at the start of each round instead. The two nearly coincide,
    /// because a keeper acts once a round — but they part company when a goal resets
    /// play mid-round, and slide 65 is the authority.
    /// </summary>
    public void ExpireRushGates()
    {
        List<string>? spent = null;

        foreach (var (team, gate) in RushGates)
        {
            if (gate.PlacedOnTurn >= InnerPhaseCount) continue;
            (spent ??= []).Add(team);
        }

        if (spent is null) return;

        foreach (var team in spent)
            RushGates.Remove(team);
    }

    /// <summary>Sweep every gate, whatever its age. For a reset, not for play.</summary>
    public void ClearRushGates() => RushGates.Clear();

    public Dictionary<string, PlayerState> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ActionEvent> CurrentPhaseActions { get; } = new();
    public List<ActionEvent> GameLog { get; } = new();
    public List<string> PlayByPlay { get; } = new();

    /// <summary>
    /// Which edition of the rules this match is being read under.
    ///
    /// Defaults to how the game is played now. Switch individual options on to read a
    /// recording back the way it was played at the time.
    /// </summary>
    public RuleOptions Rules { get; set; } = RuleOptions.Current();

    /// <summary>
    /// The team sheets for this match. Null until one is loaded.
    /// Parsing is gated on this: without it the parser cannot tell players from spectators.
    /// </summary>
    public Roster? CurrentRoster { get; private set; }

    /// <summary>
    /// Whether there are players to track.
    ///
    /// Deliberately keyed on the populated player list rather than on the roster
    /// object. A roster of blank rows would otherwise report as loaded while
    /// <see cref="Players"/> stayed empty, and everything player-related would fail
    /// silently while phases and scores carried on parsing normally.
    /// </summary>
    public bool HasRoster => Players.Count > 0;

    /// <summary>
    /// Populate the player list from a roster, with team, role, and starting
    /// position all known up front. This replaces discovering players as they
    /// happen to act, which cannot distinguish a player from a spectator.
    /// </summary>
    public void ApplyRoster(Roster roster)
    {
        CurrentRoster = roster;

        HomeTeam = roster.HomeTeam;
        AwayTeam = roster.AwayTeam;

        Players.Clear();
        foreach (var entry in roster.Entries)
        {
            var name = PlayerNames.StripWorld(entry.Name);
            if (name.Length == 0) continue;

            Players[name] = new PlayerState
            {
                Name = name,
                World = entry.World,
                Team = entry.Team,
                Role = entry.Role,
            };
        }

        ResetPositions();
    }

    /// <summary>
    /// Swap a player off the field for one coming on. Returns false if the outgoing
    /// player is not being tracked, or the incoming one already is.
    ///
    /// Surgical on purpose. Re-applying a whole roster would clear every player and
    /// reset positions, which mid-match means losing every stat earned so far and
    /// teleporting both sides back to their kickoff formation.
    ///
    /// The substitute takes over the team, role and place on the field, but not the
    /// stats: goals and tackles belong to whoever made them. The player coming off is
    /// kept in the list rather than deleted, for the same reason.
    /// </summary>
    public bool Substitute(string outgoingName, string incomingName, string? world = null)
    {
        var outgoing = PlayerNames.StripWorld(outgoingName);
        var incoming = PlayerNames.StripWorld(incomingName);

        if (incoming.Length == 0) return false;
        if (!Players.TryGetValue(outgoing, out var leaving)) return false;
        if (Players.ContainsKey(incoming)) return false;

        Players[incoming] = new PlayerState
        {
            Name = incoming,
            World = world,
            Team = leaving.Team,
            Role = leaving.Role,

            // They step straight into the place on the field that was being held.
            Position = leaving.Position,
        };

        // Possession goes with the shirt. A carrier walking off with the ball would
        // leave the match with nobody holding it.
        if (BallCarrier is not null && BallCarrier.Equals(outgoing, StringComparison.OrdinalIgnoreCase))
        {
            leaving.HasBall = false;
            Players[incoming].HasBall = true;
            BallCarrier = incoming;
        }

        leaving.IsSubstituted = true;
        leaving.Position = Waymark.None;
        leaving.IsDazed = false;
        leaving.IsBlocked = false;
        leaving.IsDiving = false;
        leaving.IsSurveying = false;
        leaving.IsGuarding = false;
        leaving.SurveyedLane = null;
        leaving.GuardBonus = 0;
        leaving.PhaseRoll = null;
        leaving.RalliedRoll = null;

        // Blocks the departing player was holding leave with them.
        CancelBlocksBy(outgoing);
        Blocks.Remove(outgoing);

        // A fresh roster instance, deliberately: ChatParser rebuilds its name index
        // only when the roster reference changes, so mutating the old one in place
        // would leave the substitute unrecognised — every action of theirs discarded.
        CurrentRoster = BuildRosterFromPlayers();

        return true;
    }

    /// <summary>
    /// The current lineup as a roster, so a substitution produces something that can be
    /// saved, re-sent to the live feed, and written into a recording.
    /// </summary>
    private Roster BuildRosterFromPlayers()
    {
        var roster = new Roster { HomeTeam = HomeTeam, AwayTeam = AwayTeam };

        foreach (var player in Players.Values)
        {
            if (player.IsSubstituted) continue;

            roster.Entries.Add(new RosterEntry
            {
                Name = player.Name,
                World = player.World,
                Team = player.Team,
                Role = player.Role,
            });
        }

        return roster;
    }

    /// <summary>
    /// State that cannot be recovered when picking up a match already under way.
    ///
    /// Chat announces what *happens*; it does not restate what is currently true. These
    /// are the things a late joiner has no way to reconstruct, so they are dropped
    /// rather than assumed — and named, because a tracker quietly guessing at them is
    /// worse than one saying it does not know.
    /// </summary>
    public static readonly string[] UnknowableOnJoin =
    [
        "who is dazed",
        "blocks and dives already standing",
        "goalkeeper guard bonuses",
        "Rush Gates on the field",
        "which side has spent their back pass",
    ];

    /// <summary>
    /// Pick up a match that is already in progress.
    ///
    /// Positions come from the arena rather than from chat — every player is physically
    /// standing somewhere, and the plugin can read that — so the one thing a log parser
    /// could never recover is the one thing that comes for free here. Phase, round,
    /// score and possession all arrive with the next referee call.
    ///
    /// What cannot be recovered is cleared outright. Carrying over whatever happened to
    /// be in memory would be worse than starting from nothing, because it would look
    /// authoritative.
    /// </summary>
    public void JoinInProgress()
    {
        IsActive = true;
        IsFinished = false;

        ClearBlocks();
        ClearDives();
        ClearRushGates();
        ClearFumble();
        ClearTieBreaks();
        ClearSurveyContests();
        ClearBuzzerShot();

        DazeTracker.Clear();
        LastBackPassRound.Clear();

        foreach (var player in Players.Values)
        {
            player.IsDazed = false;
            player.IsBlocked = false;
            player.IsDiving = false;
            player.IsSurveying = false;
            player.IsGuarding = false;
            player.IsStandby = false;
            player.SurveyedLane = null;
            player.GuardBonus = 0;
            player.PhaseRoll = null;
            player.RalliedRoll = null;
            player.HasGateMove = false;

            // Nobody is where the kickoff formation says any more — the match has been
            // running. Leaving them there is worse than admitting ignorance: the arena
            // then contradicts all twelve at once, and the log fills with faults that
            // are really just the first honest reading.
            player.Position = Waymark.None;
        }

        CurrentPhaseActions.Clear();
    }

    public void Reset()
    {
        IsActive = false;
        Score = default;
        Set = 1;
        Round = 0;
        Phase = GamePhase.PreGame;
        BallCarrier = null;
        BallTeam = null;
        RoundsRemaining = 0;
        IsFinished = false;
        HasPhaseFeed = false;
        HomeGoalTarget = Waymark.Four;
        AwayGoalTarget = Waymark.D;

        // These three were previously left dirty across a reset, so a stale Rush Gate
        // or an unexpired daze could leak into the next game.
        BallCarrierTurnCount = 0;
        InnerPhaseCount = 0;
        RushGates.Clear();
        DazeTracker.Clear();
        LastBackPassRound.Clear();
        ClearFumble();
        ClearTieBreaks();
        ClearOvertime();
        ClearBuzzerShot();
        ClearSurveyContests();
        BlitzoffVariant = BlitzoffKind.Standard;

        CurrentPhaseActions.Clear();
        GameLog.Clear();
        PlayByPlay.Clear();

        // The roster survives a game reset. It was entered by hand, often seconds
        // before kickoff, and losing it on /blitz reset would be hostile.
        if (CurrentRoster is not null)
        {
            ApplyRoster(CurrentRoster);
        }
        else
        {
            HomeTeam = string.Empty;
            AwayTeam = string.Empty;
            Players.Clear();
        }
    }

    /// <summary>
    /// Reset players to starting positions (called at game start and after each goal).
    /// Home team defends D (red goal), attacks 4 (yellow goal) in Set 1.
    /// Starting layout: GK at own goal, RD+LD at own strike, M at C, LF+RF at enemy strike.
    /// </summary>
    public void ResetPositions()
    {
        foreach (var p in Players.Values)
        {
            p.IsDazed = false;

            // Somebody who has been substituted off does not come back on at the next
            // goal. They are kept in the list for their stats, not for the field.
            if (p.IsSubstituted)
            {
                p.Position = Waymark.None;
                continue;
            }

            // Never place a player whose role or team is unknown. Without this guard
            // the switch below falls through to its Waymark.C default and silently
            // piles every unidentified player onto Center.
            if (p.Role == PlayerRole.None || string.IsNullOrEmpty(p.Team))
            {
                p.Position = Waymark.None;
                continue;
            }

            bool isHome = p.Team.Equals(HomeTeam, StringComparison.OrdinalIgnoreCase);

            // Home defends D in Set 1, 4 in Set 2. Away is opposite.
            var ownGoal = isHome ? (Set == 1 ? Waymark.D : Waymark.Four)
                                 : (Set == 1 ? Waymark.Four : Waymark.D);

            p.Position = StartingPosition(p.Role, ownGoal);
        }
    }

    /// <summary>
    /// The goal this player's team is defending in the current set.
    /// Home defends D in Set 1 and Four in Set 2; away is the opposite.
    /// </summary>
    public Waymark OwnGoal(PlayerState player)
    {
        if (string.IsNullOrEmpty(player.Team)) return Waymark.None;

        var isHome = player.Team.Equals(HomeTeam, StringComparison.OrdinalIgnoreCase);

        return isHome
            ? (Set == 1 ? Waymark.D : Waymark.Four)
            : (Set == 1 ? Waymark.Four : Waymark.D);
    }

    /// <summary>Which variation of the opening scramble is being played (slide 15).</summary>
    public BlitzoffKind BlitzoffVariant { get; set; } = BlitzoffKind.Standard;

    /// <summary>The side that is behind, or empty when the score is level.</summary>
    public string TrailingTeam =>
        Score.Home == Score.Away ? string.Empty
        : Score.Home < Score.Away ? HomeTeam
        : AwayTeam;

    /// <summary>How many points separate the sides.</summary>
    public int PointDeficit => Math.Abs(Score.Home - Score.Away);

    /// <summary>
    /// The bonus this player carries into a blitzoff roll.
    ///
    /// Only the halftime restart grants one: the side that is behind gets ten per point
    /// of deficit, so the second set does not open with them still chasing (slide 15).
    /// </summary>
    public int BlitzoffBonus(PlayerState player)
    {
        if (BlitzoffVariant != BlitzoffKind.HalftimeRestart) return 0;
        if (TrailingTeam.Length == 0) return 0;

        return player.Team.Equals(TrailingTeam, StringComparison.OrdinalIgnoreCase)
            ? PointDeficit * 10
            : 0;
    }

    /// <summary>
    /// The three actions the ball carrier keeps. Everything else is closed to them
    /// while they have it (slide 52).
    /// </summary>
    public static bool CarrierMayDeclare(ActionType action) =>
        action is ActionType.Move or ActionType.Pass or ActionType.Shoot or ActionType.None;

    /// <summary>
    /// Whether the ball carrier may move to a waymark.
    ///
    /// Carrying the ball narrows movement sharply: it has to go toward the enemy goal,
    /// which rules out both retreating and crossing within the same zone (slide 52).
    /// A player without the ball has none of these limits — they move freely, and only
    /// the goals are closed to them.
    ///
    /// Moving along a lane rather than into a new zone is barred too, but that needs no
    /// check here: the sphere's walkable connections never join two waymarks in the same
    /// lane, so there is no such move to make without a Rush Gate opening one.
    /// </summary>
    public bool CarrierMayMoveTo(PlayerState carrier, Waymark destination)
    {
        if (!CanOccupy(carrier, destination)) return false;

        // Strictly forward. Level means crossing between the two lanes of one zone,
        // which is exactly what a carrier may not do.
        return ZonesAhead(carrier, carrier.Position, destination) > 0;
    }

    /// <summary>The last round of a set, after which halftime or the game follows.</summary>
    public const int FinalRound = 10;

    /// <summary>
    /// Whether the ball carrier has no choice but to shoot.
    ///
    /// The Inner Ball Carrier phase on the last round of either set leaves no other
    /// action valid, and the Buzzer phase ends in a compulsory shot once everyone
    /// else has acted.
    /// </summary>
    public bool BallCarrierMustShoot
    {
        get
        {
            if (Phase == GamePhase.BuzzerPhase) return true;
            if (Phase != GamePhase.BallCarrierInner || Round < FinalRound) return false;

            // Only binds a carrier who is actually taking this turn. One still out in
            // the outer ring acts in the outer carrier phase instead, and demanding a
            // shot from them contradicts the ring they are standing in.
            if (BallCarrier is null) return false;

            return Players.TryGetValue(BallCarrier, out var carrier)
                   && PhaseRules.IsInnerZone(carrier.Position);
        }
    }

    /// <summary>
    /// The action this player has declared during the current phase, or null.
    ///
    /// Searched from the end, so a re-declared action supersedes an earlier one.
    /// This runs in the draw path, so it scans rather than allocating.
    /// </summary>
    public ActionEvent? CurrentActionFor(string playerName)
    {
        for (var i = CurrentPhaseActions.Count - 1; i >= 0; i--)
        {
            var action = CurrentPhaseActions[i];

            if (action.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                return action;
        }

        return null;
    }

    /// <summary>
    /// Whether a goalkeeper is holding the ball and owes an immediate clearing pass.
    ///
    /// However a keeper comes by the ball — a save, a caught fumble — they send it
    /// straight back out rather than carrying it, and that pass resolves before
    /// anything else happens (slide 62). It is not a ball carrier turn: play does not
    /// wait for the next phase.
    ///
    /// Derived rather than stored, so it cannot fall out of step with possession: the
    /// moment the pass lands the ball belongs to someone else and this is false again.
    /// </summary>
    public bool KeeperMustClear =>
        BallCarrier is not null
        && Players.TryGetValue(BallCarrier, out var holder)
        && holder.IsGoalkeeper;

    /// <summary>
    /// Whether this player is one of the people acting right now.
    ///
    /// A phase activates one ring of the sphere and everyone standing in it acts at
    /// the same time. The ball carrier sits that out and acts in their own turn
    /// instead, so they are never part of the ring's action.
    /// </summary>
    public bool CanActThisPhase(PlayerState player)
    {
        var isCarrier = BallCarrier is not null &&
                        BallCarrier.Equals(player.Name, StringComparison.OrdinalIgnoreCase);

        // A keeper who has just taken the ball owes an immediate pass. It resolves
        // before anything else and does not wait for a phase to come round to them,
        // so they are acting legally whenever it lands.
        if (isCarrier && KeeperMustClear) return true;

        // At the buzzer the ring does not act: only players sharing the ball's own
        // waymark get one last turn, and the carrier shoots after them.
        if (Phase == GamePhase.BuzzerPhase)
        {
            if (BallCarrier is null) return false;
            if (!Players.TryGetValue(BallCarrier, out var carrier)) return false;
            if (carrier.Position == Waymark.None) return false;

            return player.Position == carrier.Position && !isCarrier;
        }

        if (!PhaseRules.ActsThisPhase(player.Position, Phase)) return false;

        if (PhaseRules.IsSimultaneousActionPhase(Phase)) return !isCarrier;
        if (PhaseRules.IsBallCarrierPhase(Phase)) return isCarrier;

        return false;
    }

    /// <summary>
    /// How far along the field a zone sits, measured from goal D.
    ///
    /// The two lanes run in parallel, so the strike zones either side of a goal share
    /// a rank: a ball moving from 1 to A has not advanced, it has crossed.
    /// </summary>
    public static int ZoneRank(Waymark waymark) => waymark switch
    {
        Waymark.D => 0,
        Waymark.One or Waymark.A => 1,
        Waymark.C => 2,
        Waymark.Two or Waymark.B => 3,
        Waymark.Four => 4,
        _ => -1,
    };

    /// <summary>The goal this player's team is attacking, opposite the one they defend.</summary>
    public Waymark AttackingGoal(PlayerState player)
    {
        var own = OwnGoal(player);
        if (own == Waymark.None) return Waymark.None;

        return own == Waymark.D ? Waymark.Four : Waymark.D;
    }

    /// <summary>
    /// Whether a pass retreats toward the passer's own goal.
    ///
    /// The ball may cross laterally between the two lanes at the same rank, but it
    /// must not go backwards. Which direction counts as forward depends on the side,
    /// and flips at halftime when the teams swap ends.
    /// </summary>
    public bool IsBackwardPass(PlayerState passer, Waymark from, Waymark to)
        => ZonesAhead(passer, from, to) < 0;

    /// <summary>
    /// How many zones down the field a pass travels, from the passer's point of view.
    ///
    /// Zero means level — the two lanes sit at the same rank, so a ball crossing from
    /// 1 to A has not advanced. Negative means it retreated toward their own goal.
    /// </summary>
    public int ZonesAhead(PlayerState passer, Waymark from, Waymark to)
    {
        var fromRank = ZoneRank(from);
        var toRank = ZoneRank(to);
        if (fromRank < 0 || toRank < 0) return 0;

        var attacking = AttackingGoal(passer);
        if (attacking == Waymark.None) return 0;

        return attacking == Waymark.Four ? toRank - fromRank : fromRank - toRank;
    }

    /// <summary>The round each team last used a back pass, so the cooldown can be checked.</summary>
    public Dictionary<string, int> LastBackPassRound { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this player's team may back pass right now.
    ///
    /// Using one locks the team out for the following round (slide 43), so a team that
    /// used one in round 5 is clear again in round 7.
    /// </summary>
    public bool BackPassAvailable(PlayerState passer)
    {
        if (string.IsNullOrEmpty(passer.Team)) return true;

        return !LastBackPassRound.TryGetValue(passer.Team, out var last) || Round > last + 1;
    }

    /// <summary>
    /// Whether the passer has an unblocked team-mate 1–3 zones ahead.
    ///
    /// This is the test that decides whether a back pass is available at all: the ball
    /// only goes backwards when there is genuinely nothing on ahead of it. Keepers are
    /// not counted, since they cannot receive a pass.
    /// </summary>
    public bool HasUnblockedAllyAhead(PlayerState passer)
    {
        foreach (var mate in Players.Values)
        {
            if (ReferenceEquals(mate, passer)) continue;
            if (mate.IsGoalkeeper) continue;
            if (mate.IsBlocked) continue;

            // Somebody the tracker has not placed is not standing anywhere, and must
            // not be counted as an option: unplaced reads as distance zero.
            if (mate.Position == Waymark.None) continue;
            if (!mate.Team.Equals(passer.Team, StringComparison.OrdinalIgnoreCase)) continue;

            var ahead = ZonesAhead(passer, passer.Position, mate.Position);
            if (ahead is >= 1 and <= 3) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether any team-mate is within <paramref name="zones"/> of the passer, in
    /// either direction.
    ///
    /// Used for the goalkeeper's reach: they may only throw three zones when there is
    /// nobody closer, and blocked team-mates still count as somebody (slide 42).
    /// </summary>
    public bool HasAllyWithin(PlayerState passer, int zones)
    {
        foreach (var mate in Players.Values)
        {
            if (ReferenceEquals(mate, passer)) continue;
            if (mate.IsGoalkeeper) continue;
            if (mate.Position == Waymark.None) continue;
            if (!mate.Team.Equals(passer.Team, StringComparison.OrdinalIgnoreCase)) continue;

            var distance = Math.Abs(ZonesAhead(passer, passer.Position, mate.Position));
            if (distance <= zones) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether this player may tackle at all. Tackling is a forward's ability.
    /// </summary>
    public static bool CanTackle(PlayerState actor) =>
        actor.Role is PlayerRole.LeftForward or PlayerRole.RightForward;

    /// <summary>
    /// Whether a forward can reach a target to tackle them.
    ///
    /// Reach runs along their row rather than being confined to their own zone,
    /// which is how a forward at Center gets at a goalkeeper: both goals share the
    /// middle row.
    /// </summary>
    public bool CanTackle(PlayerState actor, PlayerState target) =>
        CanTackle(actor)
        && BlitzsphereLayout.SharesLine(actor.Position, target.Position)
        // A tackle is a movement that stuns, and it ends with the tackler standing in
        // the waymark they declared. The goal restrictions therefore bind it exactly as
        // they bind a move: a forward cannot tackle into their own goal, and a defender
        // cannot tackle into the enemy's. Tackling a keeper in the goal you are
        // attacking stays legal — that is the whole point of the ability.
        && CanOccupy(actor, target.Position);

    /// <summary>
    /// Whether a role is allowed to stand in a zone.
    ///
    /// Each role has ground it does not cover: keepers never leave their goal,
    /// forwards never drop into the goal they are defending, and defenders never
    /// push into the goal they are attacking.
    /// </summary>
    public bool CanOccupy(PlayerState player, Waymark waymark)
    {
        if (waymark == Waymark.None) return false;

        var own = OwnGoal(player);
        if (own == Waymark.None) return true; // team unknown, nothing to judge against

        var enemy = own == Waymark.D ? Waymark.Four : Waymark.D;

        return player.Role switch
        {
            PlayerRole.Goalkeeper => waymark == own,

            PlayerRole.LeftForward or PlayerRole.RightForward => waymark != own,

            PlayerRole.LeftDefender or PlayerRole.RightDefender => waymark != enemy,

            _ => true,
        };
    }

    /// <summary>
    /// Move a player to a zone, refusing moves the rules do not allow.
    ///
    /// Without this the tracker will happily render an impossible field: a mistyped
    /// action, or a tackle resolving in the keeper's favour (which moves the tackler
    /// to their target's zone), was enough to walk a keeper out to Center with the
    /// ball.
    ///
    /// Returns false when the move was refused.
    /// </summary>
    public bool TryPlace(PlayerState player, Waymark waymark)
    {
        if (!CanOccupy(player, waymark)) return false;

        player.Position = waymark;
        return true;
    }

    /// <summary>
    /// Where a role starts, given the goal that player's team defends.
    ///
    /// Left and right are relative to each team's facing, because the two sides face
    /// opposite ways down the pool. The waymarks themselves never move: only the
    /// left/right labelling mirrors. This is why a right forward lines up against the
    /// opposing left defender rather than their right defender, both standing on the
    /// same static marker.
    /// </summary>
    public static Waymark StartingPosition(PlayerRole role, Waymark ownGoal)
    {
        if (role == PlayerRole.None) return Waymark.None;

        var enemyGoal = ownGoal == Waymark.D ? Waymark.Four : Waymark.D;

        // For the side defending D the letter lane (A/B) is their left flank;
        // for the side defending Four it is their right.
        var leftIsLetterLane = ownGoal == Waymark.D;

        return role switch
        {
            PlayerRole.Goalkeeper => ownGoal,
            PlayerRole.Midfield => Waymark.C,

            // Defenders hold their own strike zone.
            PlayerRole.LeftDefender => GetStrikeWaymark(ownGoal, isLetter: leftIsLetterLane),
            PlayerRole.RightDefender => GetStrikeWaymark(ownGoal, isLetter: !leftIsLetterLane),

            // Forwards press the enemy strike zone.
            PlayerRole.LeftForward => GetStrikeWaymark(enemyGoal, isLetter: leftIsLetterLane),
            PlayerRole.RightForward => GetStrikeWaymark(enemyGoal, isLetter: !leftIsLetterLane),

            _ => Waymark.C,
        };
    }

    /// <summary>
    /// Set initial position for a single player based on their team and role.
    /// Only sets position if both team and role are known, and position hasn't been set yet.
    /// </summary>
    public void SetInitialPosition(PlayerState player)
    {
        // Deliberately no "already placed" early-return. Positions must stay
        // recomputable: when this returned early on Position != None, a later team
        // correction could fix a player's team but never move them off the wrong spot.
        if (string.IsNullOrEmpty(player.Team) || player.Role == PlayerRole.None) return;

        bool isHome = player.Team.Equals(HomeTeam, StringComparison.OrdinalIgnoreCase);
        var ownGoal = isHome ? (Set == 1 ? Waymark.D : Waymark.Four)
                             : (Set == 1 ? Waymark.Four : Waymark.D);

        player.Position = StartingPosition(player.Role, ownGoal);
    }

    /// <summary>
    /// Get the strike zone waymark adjacent to a goal.
    /// D goal → strike zone is 1 (number) and A (letter).
    /// 4 goal → strike zone is 2 (number) and B (letter).
    /// </summary>
    public static Waymark GetStrikeWaymark(Waymark goal, bool isLetter) => goal switch
    {
        Waymark.D => isLetter ? Waymark.A : Waymark.One,
        Waymark.Four => isLetter ? Waymark.B : Waymark.Two,
        _ => Waymark.C,
    };

    /// <summary>
    /// Switch sides for Set 2 (halftime).
    /// </summary>
    public void SwitchSides()
    {
        Set = 2;
        HomeGoalTarget = Waymark.D;
        AwayGoalTarget = Waymark.Four;
    }
}
