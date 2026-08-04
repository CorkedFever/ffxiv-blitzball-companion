namespace BlitzballTracker.Core.Simulation;

using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;

/// <summary>
/// Knobs for what a generated match contains.
///
/// The defaults produce a match that looks like a real one, including the noise:
/// crowd participation, commentators naming players, and referees issuing
/// corrections. That noise is the point. A clean synthetic match would not catch
/// the failures that actually happen.
/// </summary>
public sealed class SimulationOptions
{
    public int Sets { get; set; } = 2;
    public int RoundsPerSet { get; set; } = 10;

    /// <summary>Chance a contested roll is sent back for a re-roll.</summary>
    public double RerollChance { get; set; } = 0.08;

    /// <summary>Chance a player rolls before posting and is given grace.</summary>
    public double GraceChance { get; set; } = 0.05;

    /// <summary>Chance a roll arrives after its phase has already closed.</summary>
    public double LateRollChance { get; set; } = 0.05;

    /// <summary>Spectators shouting along, which must never be mistaken for players.</summary>
    public bool IncludeCrowd { get; set; } = true;

    /// <summary>Commentators naming players in prose, which must not make them players.</summary>
    public bool IncludeCommentary { get; set; } = true;

    /// <summary>Seconds of in-fiction time between consecutive lines.</summary>
    public double SecondsPerLine { get; set; } = 4;
}

/// <summary>
/// Generates a complete blitzball match as chat lines, in the formats real leagues
/// actually use.
///
/// This exists so testing does not depend on one recorded log sitting outside the
/// repository. That log is a single match under a single set of circumstances: it
/// can never produce a shootout, a sudden death, a halftime side-switch, or a
/// referee correction on demand. A seeded generator can produce all of them, the
/// same way every run.
///
/// Output is chat lines rather than direct state mutation deliberately, so the
/// parser is exercised too rather than bypassed.
/// </summary>
public sealed class MatchSimulator
{
    // Deliberately not on any roster: these must never be adopted as players.
    public const string Referee = "Sim Referee";
    public const string Scorekeeper = "Sim Scorekeeper";
    public const string Commentator = "Sim Commentator";

    public static readonly string[] CrowdNames =
    [
        "Sim Spectator", "Sim Onlooker", "Sim Fan", "Sim Bystander", "Sim Heckler",
    ];

    /// <summary>
    /// A full twelve-player roster for generated matches, so tests and the in-game
    /// demo do not each invent their own.
    /// </summary>
    public static Roster StandardRoster(string home = "SIM RED", string away = "SIM GOLD")
    {
        var roster = new Roster { HomeTeam = home, AwayTeam = away };

        var roles = new[]
        {
            (PlayerRole.Goalkeeper, "Keeper"),
            (PlayerRole.Midfield, "Mid"),
            (PlayerRole.LeftDefender, "Leftback"),
            (PlayerRole.RightDefender, "Rightback"),
            (PlayerRole.LeftForward, "Leftwing"),
            (PlayerRole.RightForward, "Rightwing"),
        };

        foreach (var (team, prefix) in new[] { (home, "Red"), (away, "Gold") })
        {
            foreach (var (role, title) in roles)
            {
                roster.Entries.Add(new RosterEntry
                {
                    Name = $"{prefix} {title}",
                    Team = team,
                    Role = role,
                });
            }
        }

        return roster;
    }

    private readonly Roster _roster;
    private readonly SimulationOptions _options;
    private readonly Random _random;
    private readonly List<LogLine> _lines = [];

    private readonly List<RosterEntry> _home;
    private readonly List<RosterEntry> _away;

    /// <summary>
    /// Squads minus their goalkeepers.
    ///
    /// Keepers hold their goal and never come out, so they take no part in field
    /// play: they do not contest the blitzoff, join phase contests, receive passes,
    /// or take shootout attempts. Generating any of that produces matches that
    /// cannot happen.
    /// </summary>
    private readonly List<RosterEntry> _homeOutfield;
    private readonly List<RosterEntry> _awayOutfield;

    /// <summary>
    /// A live game fed by everything this generator emits.
    ///
    /// Choosing legal play needs to know where people are, and maintaining a second
    /// opinion about that is a losing game: a referee re-roll makes the parser
    /// un-apply and re-resolve earlier contests, which no mirror will track. So the
    /// generator asks the real parser instead. One rules implementation, no drift.
    /// </summary>
    private readonly BlitzGame _shadow = new();
    private readonly ChatParser _shadowParser;

    /// <summary>
    /// Rolls already made this phase.
    ///
    /// Players roll once per phase and that roll stands against everyone who comes at
    /// them. With a whole ring acting at once, a defender can be contested several
    /// times, and rolling afresh for each attacker would be wrong.
    /// </summary>
    private readonly Dictionary<RosterEntry, int> _phaseRolls = [];

    /// <summary>
    /// Where each player has said they are going this phase.
    ///
    /// The ring declares together and moves together at Reposition, so choosing a
    /// destination has to account for team-mates already headed there — they are not
    /// standing there yet.
    /// </summary>
    private readonly Dictionary<RosterEntry, Waymark> _declaredMoves = [];

    private DateTime _clock = new(2026, 1, 1, 20, 0, 0);

    private int _homeScore;
    private int _awayScore;
    private int _set = 1;

    private RosterEntry _carrier;

    public MatchSimulator(Roster roster, int seed, SimulationOptions? options = null)
    {
        _roster = roster;
        _options = options ?? new SimulationOptions();
        _random = new Random(seed);

        _home = roster.Entries.Where(e => e.Team.Equals(roster.HomeTeam, StringComparison.OrdinalIgnoreCase)).ToList();
        _away = roster.Entries.Where(e => e.Team.Equals(roster.AwayTeam, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_home.Count == 0 || _away.Count == 0)
            throw new ArgumentException("Both teams need at least one player.", nameof(roster));

        _homeOutfield = _home.Where(p => p.Role != PlayerRole.Goalkeeper).ToList();
        _awayOutfield = _away.Where(p => p.Role != PlayerRole.Goalkeeper).ToList();

        // A squad of nothing but keepers cannot play, so fall back to the full list
        // rather than generating a match where nobody can act.
        if (_homeOutfield.Count == 0) _homeOutfield = _home;
        if (_awayOutfield.Count == 0) _awayOutfield = _away;

        _shadow.ApplyRoster(roster);
        _shadowParser = new ChatParser(_shadow);

        _carrier = _homeOutfield[0];
    }

    /// <summary>The final score this match produced, valid after <see cref="Generate"/>.</summary>
    public Score FinalScore => new(_homeScore, _awayScore);

    public IReadOnlyList<LogLine> Generate()
    {
        _lines.Clear();

        Ref("<< STANDBY FOR BLITZOFF >>");
        Score();

        if (_options.IncludeCommentary)
            Say(Commentator, $"\"Welcome, everyone! {_roster.HomeTeam} against {_roster.AwayTeam} tonight!\"");

        for (_set = 1; _set <= _options.Sets; _set++)
        {
            if (_set > 1)
            {
                Ref("HALFTIME");
                if (_options.IncludeCommentary)
                    Say(Commentator, "\"Teams are switching sides for the second set!\"");
            }

            Blitzoff();

            for (var round = 1; round <= _options.RoundsPerSet; round++)
            {
                Ref($"<< ROUND {round} >>");

                if (round == _options.RoundsPerSet)
                    Ref("1 ROUNDS TO BUZZER");

                PlayPhase(outer: true);
                PlayPhase(outer: false);
            }

            // A set does not just stop. If the ball finished the last round in a strike
            // zone there is one more exchange to play out.
            BuzzerPhase();
        }

        // A draw goes to a shootout, which the recorded log could never produce.
        if (_homeScore == _awayScore)
            Shootout();

        Ref("<< GAME OVER >>");
        Score();

        return _lines;
    }

    private void Blitzoff()
    {
        Ref("\"MIDFIELDERS... SET --\"");
        Ref("\"LET'S --\"");

        if (_options.IncludeCrowd)
        {
            // The failure this guards against: at a real event a crowd of people
            // shout along, and the old parser turned every one of them into a player.
            foreach (var name in CrowdNames)
                Say(name, "\"BLITZOFF!!!\"");
        }

        Ref("<< BLITZOFF >>");

        // The referee calls for midfielders, so send midfielders.
        var homeMid = Midfielder(true);
        var awayMid = Midfielder(false);

        // A goal that did not level the scores is a Blitzon: the side behind simply
        // receives it, with no roll-off at all (slide 15).
        if (_homeScore != _awayScore && _shadow.Phase == GamePhase.Blitzoff &&
            _shadow.BlitzoffVariant == BlitzoffKind.Blitzon)
        {
            _carrier = _homeScore < _awayScore ? homeMid : awayMid;
        }
        else
        {
            // The trailing side carries their deficit bonus into the second set's
            // opening scramble.
            var homeRoll = Roll(homeMid) + BlitzoffBonus(homeMid);
            var awayRoll = Roll(awayMid) + BlitzoffBonus(awayMid);

            _carrier = homeRoll >= awayRoll ? homeMid : awayMid;
        }

        Keeper($"[[ {TeamOf(_carrier)} BALL GET ]]");
        Keeper($"[BALL to {_carrier.Name}]");
    }

    private void PlayPhase(bool outer)
    {
        Ref(outer ? "<< OUTER HUDDLE PHASE >>" : "<< INNER HUDDLE PHASE >>");

        // Phases are timed, and the timestamps matter: late-roll handling keys off
        // them, so a match generated at a few seconds per line would never produce a
        // roll far enough behind its phase to exercise that path.
        Pause(PhaseTiming.Huddle);

        Ref(outer ? "<< OUTER PHASE (A/B/1/2) >> Start!" : "<< INNER PHASE (4/C/D) >> Start!");

        var actionStarted = _clock;

        // One roll per player per phase, held against everyone who comes at them.
        _phaseRolls.Clear();
        _declaredMoves.Clear();

        // Possession can move without the generator deciding it did — a fumble hands
        // the ball to whoever wins the scramble. Asking the parser first keeps the
        // player we exclude from the ring the same one it considers the carrier;
        // otherwise our carrier sits the phase out and gets flagged for it.
        SyncCarrierFromShadow();

        // A phase activates one ring of the sphere and everyone standing in it acts
        // at the same time, both sides together. The carrier sits it out; they act
        // in their own turn afterwards.
        var ring = outer ? PhaseRules.OuterZones : PhaseRules.InnerZones;

        var actors = _home.Concat(_away)
            .Where(p => p != _carrier)
            .Where(p => Array.IndexOf(ring, ZoneOf(p)) >= 0)
            .OrderBy(_ => _random.Next())
            .ToList();

        foreach (var actor in actors)
            DeclareAction(actor);

        FillPhase(actionStarted);

        Ref("<< REPOSITION >>");
        Ref("<< BALL CARRIER TURN >>");

        var carrierStarted = _clock;

        // A new phase, so everyone rolls fresh again.
        _phaseRolls.Clear();

        BallCarrierTurn();
        FillPhase(carrierStarted);
    }

    /// <summary>Advance the in-fiction clock without emitting anything.</summary>
    private void Pause(TimeSpan span) => _clock = _clock.Add(span);

    /// <summary>
    /// Run the clock out to a full phase, so phases span about a minute as they do
    /// in a real match rather than however long the generated lines happened to take.
    /// </summary>
    private void FillPhase(DateTime startedAt)
    {
        if (_clock - startedAt < PhaseTiming.Action)
            _clock = startedAt + PhaseTiming.Action;
    }

    /// <summary>
    /// What one player does during their ring's phase.
    ///
    /// Keepers shore up their goal, anyone sharing the carrier's zone contests them,
    /// and everyone else pushes up the field.
    /// </summary>
    private void DeclareAction(RosterEntry actor)
    {
        if (actor.Role == PlayerRole.Goalkeeper)
        {
            // No roll. Guard raises the keeper's bonus; there is nothing opposing it.
            Say(actor.Name, $"|| {actor.Name} sets their stance. [GUARD]");
            return;
        }

        var zone = ZoneOf(actor);
        var carrierZone = ZoneOf(_carrier);
        var opposing = IsHome(actor) != IsHome(_carrier);

        // Forwards reach along their row to tackle; everyone else has to share the
        // carrier's zone to contest them at all.
        var isForward = actor.Role is PlayerRole.LeftForward or PlayerRole.RightForward;

        var carrierIsKeeper = _carrier.Role == PlayerRole.Goalkeeper;
        var sameZone = zone != Waymark.None && zone == carrierZone;

        // A tackle is a movement, so it needs a different waymark to travel to, and it
        // leaves the tackler standing there — which means the goal restrictions bind
        // it. A forward tackling into their own goal is not a declaration they can
        // make. Asked of the rules rather than restated here.
        var canTackle = isForward
                        && BlitzsphereLayout.SharesLine(zone, carrierZone)
                        && _shadow.Players.TryGetValue(actor.Name, out var tracked)
                        && _shadow.CanOccupy(tracked, carrierZone);

        // Keepers cannot be blocked at all; a tackle is the only way at one.
        var canBlock = sameZone && !carrierIsKeeper;

        if (opposing && (canTackle || canBlock))
        {
            DeclareContest(actor, tackle: canTackle);
            return;
        }

        // Survey guards lane movement, so it belongs to players holding the back
        // rather than being something to fall back on. It used to be emitted
        // whenever anyone had nowhere forward to go, which piled it up on the goals:
        // a forward who has reached the enemy goal has no forward neighbour left.
        var isDefender = actor.Role is PlayerRole.LeftDefender or PlayerRole.RightDefender;

        if (isDefender && _random.NextDouble() < 0.25)
        {
            // No roll. Declaring a survey arms a guard over a lane; the roll-off only
            // happens at Reposition, if somebody tries to come through it.
            Say(actor.Name, $"|| {actor.Name} watches the lane. [SURVEY]");
            return;
        }

        var neighbours = ForwardNeighbours(actor);
        if (neighbours.Count > 0)
        {
            var target = SpreadOut(actor, neighbours, mayHangBack: true);

            // No roll. A basic move is not contested, so there is nothing to roll
            // against: you declare the waymark and you go. Rolling here manufactured
            // dice that never happen in a real match.
            Say(actor.Name,
                $"|| {actor.Name} swims for position. [MOVE to {BlitzsphereLayout.Label(target)}]");
            return;
        }

        // Nowhere to advance. Block someone actually standing here if there is anyone,
        // since a block is aimed at a player and a targetless one means nothing.
        // Never a keeper: they cannot be blocked, and standing in a goal zone is the
        // one place you are guaranteed to be sharing it with one.
        var nearby = Squad(!IsHome(actor))
            .FirstOrDefault(p => p.Role != PlayerRole.Goalkeeper
                                 && ZoneOf(p) != Waymark.None
                                 && ZoneOf(p) == zone);

        if (nearby is not null)
        {
            Say(actor.Name, $"|| {actor.Name} gets in the way. [BLOCK -> {nearby.Name}]");
            RollOnce(actor);
            return;
        }

        // Nothing ahead and nobody to contest, so fall back on any legal waymark. Only
        // the ball is held to travelling forward; players move freely along the lanes,
        // barred only from the goals their role cannot enter.
        //
        // The generator never lets a phase run out on purpose. Losing your action is a
        // real thing that happens to real players, and the tracker reports it — but it
        // is a mistake rather than a move, and a generator that plays it as a strategy
        // teaches the wrong thing about the game.
        var anywhere = Neighbours(actor, forwardOnly: false);
        if (anywhere.Count > 0)
        {
            var open = SpreadOut(actor, anywhere);

            Say(actor.Name,
                $"|| {actor.Name} drops into open water. [MOVE to {BlitzsphereLayout.Label(open)}]");
        }
    }

    /// <summary>An opposed action against the carrier, with all the ways it can go wrong.</summary>
    private void DeclareContest(RosterEntry actor, bool tackle)
    {
        // The caller decides which is legal from where the two of them are standing:
        // a tackle reaches a different waymark, a block only the one you share.
        var action = tackle ? "TACKLE" : "BLOCK";

        // Rolling before posting is the most common real mistake, and the referee
        // response is a flag followed by grace.
        var rollsEarly = _random.NextDouble() < _options.GraceChance;

        if (rollsEarly)
        {
            Roll(actor);
            Keeper("[[FLAG]]");
            Say(Referee, $"[[ GRACE GIVEN -- {actor.Name} ]]");
        }

        Say(actor.Name, $"|| {actor.Name} moves in. [{action} -> {_carrier.Name}]");

        // A roll that lands after the phase closed, which used to be counted for stats
        // while resolving nothing. It hangs off a contest because contests are the
        // reliably rolled action: passes within range are not rolled for at all.
        if (_random.NextDouble() < _options.LateRollChance)
            Ref("<< PHASE SHIFT >>");

        var actorRoll = RollOnce(actor);

        // The carrier's single roll for the phase, reused against every attacker.
        var defenderRoll = RollOnce(_carrier);

        if (_random.NextDouble() < _options.RerollChance)
        {
            // Referees abbreviate names when calling these.
            Say(Referee, $"REROLL {Shorthand(actor)}/{Shorthand(_carrier)}");
            actorRoll = Reroll(actor);
            defenderRoll = Reroll(_carrier);
        }

        // The tackler moving to their target's zone is applied by the parser, and the
        // shadow game picks it up from the same messages, so nothing to mirror here.
        if (actorRoll > defenderRoll && action == "TACKLE")
            Keeper($"[[ DAZED - {_carrier.Name} ]]");

        if (_options.IncludeCommentary)
            Say(Commentator, $"\"{FirstName(actor)} goes in hard on {FirstName(_carrier)} there!\"");
    }

    private void BallCarrierTurn()
    {
        // The parser decides who has the ball, and it sees things this generator does
        // not: a save hands possession to the keeper, a fumble moves it again. Trust
        // it over our own bookkeeping, or pass direction gets computed for a player
        // who is no longer carrying.
        SyncCarrierFromShadow();

        // The last round's inner turn and the buzzer leave the carrier no choice, so
        // there is nothing to pick between. Asked of the rules rather than worked out
        // from the round number here, which is how the generator ended up declaring
        // passes its own parser refused.
        if (_shadow.BallCarrierMustShoot)
        {
            ShootAtGoal(restartAfterGoal: false);
            return;
        }

        var choice = _random.NextDouble();

        if (choice < 0.35)
        {
            ShootAtGoal(restartAfterGoal: true);
        }
        else if (choice < 0.75)
        {
            // The ball never travels backwards, so only teammates level with or ahead
            // of the carrier can receive it. Picking any teammate at random produced
            // passes retreating toward their own goal, which never happens in play.
            //
            // Routed through Receivers because the carrier can be a goalkeeper here —
            // they win fumbles and catch balls — and their reach is far shorter.
            var mates = Receivers(_carrier);
            if (mates.Count == 0) return;

            var mate = mates[_random.Next(mates.Count)];

            Say(_carrier.Name, $"|| {_carrier.Name} looks up. [PASS -> {mate.Name}]");

            // No roll. A pass inside its range carries automatically (slide 41); only
            // goal to goal is contested, and the generator never picks that far.
            Keeper($"[[PASS COMPLETE to {mate.Name} ]]");
            _carrier = mate;
        }
        else
        {
            // The carrier's movement is narrower than anyone else's: strictly toward the
            // enemy goal, so a level crossing between the two lanes of one zone is out.
            // Asked of the rules rather than restated here.
            var neighbours = ForwardNeighbours(_carrier)
                .Where(to => _shadow.Players.TryGetValue(_carrier.Name, out var tracked)
                             && _shadow.CarrierMayMoveTo(tracked, to))
                .ToList();

            if (neighbours.Count == 0) return;

            var target = neighbours[_random.Next(neighbours.Count)];

            // Carrying the ball does not turn a move into a contest, so this is not
            // rolled for either.
            Say(_carrier.Name,
                $"|| {_carrier.Name} glides forward. [MOVE to {BlitzsphereLayout.Label(target)}]");
        }
    }

    /// <summary>
    /// The carrier shoots, contested by the opposing keeper.
    ///
    /// <paramref name="restartAfterGoal"/> is false at the buzzer: the set ends on that
    /// shot, so there is no blitzoff to restart play with.
    /// </summary>
    private void ShootAtGoal(bool restartAfterGoal, int chain = 0)
    {
        // The keeper is never named as a target; they contest anything on their net.
        Say(_carrier.Name, $"|| {_carrier.Name} winds up. [SHOOT]");
        var shooter = Roll(_carrier);

        var keeper = Squad(!IsHome(_carrier)).FirstOrDefault(p => p.Role == PlayerRole.Goalkeeper);
        if (keeper is null) return;

        var save = Roll(keeper);

        if (shooter > save + 20)
        {
            if (IsHome(_carrier)) _homeScore++; else _awayScore++;

            Score();

            if (restartAfterGoal) Blitzoff();
            return;
        }

        // The keeper catches it, so possession is theirs. Leaving the shooter as
        // carrier here meant the generator kept driving a player who no longer had
        // the ball.
        _carrier = keeper;

        // Whether that was actually a save is the parser's call, not ours: it applies
        // distance and class bonuses this generator does not model, so the two can
        // disagree about the same pair of rolls. Emitting the clearing pass on our own
        // opinion sent it from whoever we imagined had the ball, and a pass from a
        // forward standing deep in the enemy half reads as travelling backwards.
        SyncCarrierFromShadow();

        if (_carrier != keeper) return;

        // A keeper who catches at the buzzer owes a goal-to-goal shot, not a clearance.
        // Two rules meet here and the shot wins: sending the ball out instead put the
        // generator in the position of being flagged by its own parser.
        //
        // Bounded by the same cap the chain itself has, because that reply can be saved
        // in turn and this would otherwise bounce between the two nets forever.
        if (_shadow.BallCarrierMustShoot && chain < BuzzerShot.MaxLinks)
        {
            ShootAtGoal(restartAfterGoal: false, chain + 1);
            return;
        }

        if (restartAfterGoal)
            ClearFromGoal(keeper);
    }

    /// <summary>
    /// The last act of a set, when the ball finishes Round 10 in a strike zone.
    ///
    /// Only players sharing the ball's own waymark get a final action — not the ring,
    /// the waymark (slide 26) — and then the carrier must shoot. If the ball is
    /// anywhere else at the end of the round there is no buzzer phase and the set
    /// simply ends.
    /// </summary>
    private void BuzzerPhase()
    {
        SyncCarrierFromShadow();

        var ballAt = ZoneOf(_carrier);
        if (!PhaseRules.IsOuterZone(ballAt)) return;

        Ref("<< BUZZER PHASE >>");

        _phaseRolls.Clear();

        var actors = _home.Concat(_away)
            .Where(p => p != _carrier)
            .Where(p => ZoneOf(p) == ballAt)
            .OrderBy(_ => _random.Next())
            .ToList();

        foreach (var actor in actors)
            DeclareAction(actor);

        Pause(PhaseTiming.Action);

        // No restart: this shot is the end of the set either way.
        ShootAtGoal(restartAfterGoal: false);
    }

    /// <summary>
    /// The keeper's clearing pass, sent before play moves on.
    ///
    /// A keeper does not carry the ball. However they come by it they send it straight
    /// back out, and that resolves before the next phase rather than waiting for a
    /// ball carrier turn.
    /// </summary>
    private void ClearFromGoal(RosterEntry keeper)
    {
        var mates = Receivers(keeper);
        if (mates.Count == 0) return;

        var mate = mates[_random.Next(mates.Count)];

        // No roll, and emphatically not one here: the keeper has just rolled to contest
        // the shot they caught. A second roll in the same phase reads as a re-roll, and
        // the parser dutifully un-applied the save and re-resolved the shot as a goal —
        // so the clearance was thrown by a keeper who no longer had the ball.
        Say(keeper.Name, $"|| {keeper.Name} sends it straight back out. [PASS -> {mate.Name}]");
        Keeper($"[[PASS COMPLETE to {mate.Name} ]]");

        _carrier = mate;
    }

    /// <summary>
    /// Five attempts a side, alternating, in the order the line is drawn up: midfielder
    /// first, then out along it (slide 28).
    ///
    /// Flat rolls opposed by a flat keeper roll — no modifiers of any kind. This used to
    /// fire five shots against a bare threshold with no keeper on the other end and no
    /// ordering at all, which is not a shootout so much as a coin toss.
    /// </summary>
    /// <summary>
    /// Pick the emptiest of the options, breaking ties at random.
    ///
    /// Taste, not a rule. Any number of players may legally share a waymark, and the
    /// tracker must never refuse it — but a generator choosing uniformly funnels a whole
    /// side onto one marker, because from Centre there are only two ways forward. That
    /// reads as a bug when you look at the field, and it makes generated matches a poor
    /// likeness of real ones.
    /// </summary>
    private Waymark SpreadOut(RosterEntry actor, List<Waymark> options, bool mayHangBack = false)
    {
        var (chosen, crowded) = LeastCrowded(actor, options);

        // Advancing is only a preference. When every way forward is already jammed —
        // and from A or 1 there is only one — dropping into space is both legal and
        // what a player would actually do, so the alternative is worth looking at.
        if (mayHangBack && crowded >= 2)
        {
            var wider = Neighbours(actor, forwardOnly: false);
            var (elsewhere, quieter) = LeastCrowded(actor, wider);

            if (quieter < crowded) chosen = elsewhere;
        }

        _declaredMoves[actor] = chosen;
        return chosen;
    }

    /// <summary>The emptiest of the options and how many are headed there, ties broken at random.</summary>
    private (Waymark Choice, int Crowd) LeastCrowded(RosterEntry actor, List<Waymark> options)
    {
        var mates = Squad(IsHome(actor));

        var fewest = int.MaxValue;
        var best = new List<Waymark>(options.Count);

        foreach (var option in options)
        {
            // Where team-mates are *heading*, not only where they stand. The whole ring
            // declares before any of it happens, so counting only current positions
            // showed every one of them the same empty waymark and sent them all to it.
            var crowd = mates.Count(p => p != actor
                                         && (_declaredMoves.TryGetValue(p, out var bound)
                                             ? bound == option
                                             : ZoneOf(p) == option));

            if (crowd < fewest)
            {
                fewest = crowd;
                best.Clear();
                best.Add(option);
            }
            else if (crowd == fewest)
            {
                best.Add(option);
            }
        }

        return (best[_random.Next(best.Count)], fewest);
    }

    /// <summary>The deficit bonus this player carries, asked of the rules.</summary>
    private int BlitzoffBonus(RosterEntry player) =>
        _shadow.Players.TryGetValue(player.Name, out var tracked) ? _shadow.BlitzoffBonus(tracked) : 0;

    private void Shootout()
    {
        Ref("<< SHOOTOUT >>");

        var homeFirst = _random.Next(2) == 0;
        var first = homeFirst ? _roster.HomeTeam : _roster.AwayTeam;

        Ref($"[[ FIRST -- {first} ]]");

        var homeKeeper = Squad(true).FirstOrDefault(p => p.Role == PlayerRole.Goalkeeper);
        var awayKeeper = Squad(false).FirstOrDefault(p => p.Role == PlayerRole.Goalkeeper);
        if (homeKeeper is null || awayKeeper is null) return;

        var homeGoals = 0;
        var awayGoals = 0;

        foreach (var role in BlitzGame.ShootoutOrder)
        {
            foreach (var atHome in homeFirst ? new[] { true, false } : [false, true])
            {
                var shooter = Squad(atHome).FirstOrDefault(p => p.Role == role);
                if (shooter is null) continue;

                var keeper = atHome ? awayKeeper : homeKeeper;

                Say(shooter.Name, $"|| {shooter.Name} steps up to Centre. [SHOOT]");

                var shot = Roll(shooter);
                var save = Roll(keeper);

                if (shot <= save) continue;

                if (atHome) homeGoals++; else awayGoals++;
            }
        }

        // The winner takes a single point; the shootout tally is not match goals.
        if (homeGoals > awayGoals) _homeScore++;
        else if (awayGoals > homeGoals) _awayScore++;
        else SuddenDeath();

        Score();
    }

    /// <summary>
    /// The captains' duel (slide 29). The loser of the final blitzoff gets one chance
    /// to block; a blocked captain must win another roll to get the shot away, and
    /// failing that the ball turns over and the roles swap. An unblocked shot wins.
    /// </summary>
    private void SuddenDeath()
    {
        Ref("<< SUDDEN DEATH >>");

        var homeCaptain = Midfielder(true);
        var awayCaptain = Midfielder(false);

        Ref("<< BLITZOFF >>");

        var holder = Roll(homeCaptain) >= Roll(awayCaptain) ? homeCaptain : awayCaptain;
        var challenger = holder == homeCaptain ? awayCaptain : homeCaptain;

        Keeper($"[BALL to {holder.Name}]");

        // Bounded rather than truly indefinite: a generated match has to terminate, and
        // a duel that runs this long has said everything it has to say.
        for (var exchange = 0; exchange < 8; exchange++)
        {
            _phaseRolls.Clear();

            var blocks = _random.NextDouble() < 0.6;

            if (!blocks)
            {
                Say(holder.Name, $"|| {holder.Name} takes it on. [SHOOT]");
                if (holder == homeCaptain) _homeScore++; else _awayScore++;
                return;
            }

            Say(challenger.Name, $"|| {challenger.Name} throws themselves at it. [BLOCK -> {holder.Name}]");

            Say(holder.Name, $"|| {holder.Name} forces the shot. [SHOOT]");

            var shot = RollOnce(holder);
            var block = RollOnce(challenger);

            if (shot > block)
            {
                if (holder == homeCaptain) _homeScore++; else _awayScore++;
                return;
            }

            // Intercepted. The roles swap and it goes again.
            (holder, challenger) = (challenger, holder);
            Keeper($"[BALL to {holder.Name}]");
        }
    }

    // --- Emitters ---

    private void Ref(string message) => Emit("Yell", Referee, message);

    private void Keeper(string message) => Emit("Yell", Scorekeeper, message);

    private void Say(string sender, string message) => Emit("Yell", sender, message);

    /// <summary>
    /// Post the scoreboard, sometimes with the teams reversed.
    ///
    /// Real referees do exactly this, and taking the leading name as home used to
    /// swap the two sides mid-match and send the whole field to the wrong end.
    /// </summary>
    private void Score()
    {
        if (_random.NextDouble() < 0.4)
            Keeper($"[[ {_roster.AwayTeam} {_awayScore}:{_homeScore} {_roster.HomeTeam} ]]");
        else
            Keeper($"[[ {_roster.HomeTeam} {_homeScore}:{_awayScore} {_roster.AwayTeam} ]]");
    }

    /// <summary>Roll for this phase, reusing the player's existing roll if they have one.</summary>
    private int RollOnce(RosterEntry player)
    {
        if (_phaseRolls.TryGetValue(player, out var existing)) return existing;

        var value = Roll(player);
        _phaseRolls[player] = value;
        return value;
    }

    /// <summary>Discard a player's phase roll and take a fresh one, as a referee-ordered re-roll does.</summary>
    private int Reroll(RosterEntry player)
    {
        _phaseRolls.Remove(player);
        return RollOnce(player);
    }

    private int Roll(RosterEntry player)
    {
        var value = _random.Next(1, 101);
        Emit("Dice Roll", player.Name, $"Random! {player.Name} rolls a {value} (out of 100).");
        return value;
    }

    private void Emit(string channel, string sender, string message)
    {
        _lines.Add(new LogLine(_clock, channel, sender, message));

        // Feed the shadow game so the generator's view of the field is exactly the
        // view any consumer will end up with.
        if (LogReplay.IsRelevantChannel(channel))
        {
            try
            {
                _shadowParser.ProcessMessage(sender, message, _clock);
            }
            catch
            {
                // A generator must not fall over because of a parsing edge case.
            }
        }

        _clock = _clock.AddSeconds(_options.SecondsPerLine);
    }

    /// <summary>
    /// Adopt the parser's view of who is carrying the ball.
    ///
    /// Keeping a second opinion about possession is the same losing game as keeping
    /// one about positions: the parser resolves saves and interceptions this
    /// generator never modelled.
    /// </summary>
    private void SyncCarrierFromShadow()
    {
        if (_shadow.BallCarrier is not { } holder) return;

        foreach (var player in _home)
        {
            if (!player.Name.Equals(holder, StringComparison.OrdinalIgnoreCase)) continue;
            _carrier = player;
            return;
        }

        foreach (var player in _away)
        {
            if (!player.Name.Equals(holder, StringComparison.OrdinalIgnoreCase)) continue;
            _carrier = player;
            return;
        }
    }

    /// <summary>Where a player is standing, according to the parser.</summary>
    private Waymark ZoneOf(RosterEntry player) =>
        _shadow.Players.TryGetValue(player.Name, out var tracked) ? tracked.Position : Waymark.None;

    // --- Helpers ---

    private Waymark HomeGoal => _set == 1 ? Waymark.D : Waymark.Four;

    private Waymark AwayGoal => _set == 1 ? Waymark.Four : Waymark.D;

    private Waymark OwnGoalOf(RosterEntry player) => IsHome(player) ? HomeGoal : AwayGoal;

    /// <summary>
    /// Zones level with or ahead of a player, in the direction they attack.
    ///
    /// The ball never travels backwards, so both pass targets and moves are drawn
    /// from here rather than picked at random.
    /// </summary>
    private bool IsForwardOrLevel(RosterEntry player, Waymark from, Waymark to)
    {
        var fromRank = BlitzGame.ZoneRank(from);
        var toRank = BlitzGame.ZoneRank(to);
        if (fromRank < 0 || toRank < 0) return false;

        return OwnGoalOf(player) == Waymark.D
            ? toRank >= fromRank
            : toRank <= fromRank;
    }

    /// <summary>Teammates the ball may legally be passed to.</summary>
    private List<RosterEntry> ForwardReceivers(RosterEntry carrier)
    {
        var from = ZoneOf(carrier);

        return Outfield(IsHome(carrier))
            .Where(p => p != carrier)
            .Where(p => IsForwardOrLevel(carrier, from, ZoneOf(p)))
            .ToList();
    }

    /// <summary>
    /// Who this player can pass to, whoever they are.
    ///
    /// A keeper's reach is two zones, stretching to three only when nobody is closer —
    /// far shorter than "any team-mate ahead". Keepers reach this path from two
    /// directions: clearing after a save, and taking a carrier turn, because they win
    /// fumbles and catch balls. Checking the reach in only one of those left the other
    /// throwing three zones with somebody standing two away.
    /// </summary>
    private List<RosterEntry> Receivers(RosterEntry carrier)
    {
        if (carrier.Role != PlayerRole.Goalkeeper) return ForwardReceivers(carrier);

        var reachable = ClearanceTargets(carrier);

        // Nobody in reach at all. A keeper must still send it out, so it goes long and
        // comes loose — which is exactly what the rules say happens.
        return reachable.Count > 0 ? reachable : ForwardReceivers(carrier);
    }

    /// <summary>
    /// Team-mates a goalkeeper may legally throw to.
    ///
    /// Asked of the rules rather than restated here, because the reach is conditional:
    /// two zones normally, three only when there is nobody closer. Reimplementing that
    /// in the generator is how the two drift apart.
    /// </summary>
    private List<RosterEntry> ClearanceTargets(RosterEntry keeper)
    {
        var options = new List<RosterEntry>();

        if (!_shadow.Players.TryGetValue(keeper.Name, out var tracked)) return options;

        foreach (var mate in Outfield(IsHome(keeper)))
        {
            if (!_shadow.Players.TryGetValue(mate.Name, out var target)) continue;
            if (target.Position == Waymark.None) continue;

            if (_shadow.AssessPass(tracked, tracked.Position, target).IsLegal)
                options.Add(mate);
        }

        return options;
    }

    /// <summary>Adjacent zones a player may advance into.</summary>
    private List<Waymark> ForwardNeighbours(RosterEntry player) => Neighbours(player, forwardOnly: true);

    /// <summary>
    /// Every adjacent zone a player may legally stand in.
    ///
    /// Movement itself is unrestricted: players travel freely along the lanes in any
    /// direction. Only three rules narrow it, and all three are about goals — the
    /// goalkeeper never leaves theirs, a forward may not enter their own, a defender
    /// may not enter the opponent's. Those live in <see cref="BlitzGame.CanOccupy"/>.
    ///
    /// Preferring to advance is the generator's strategy, not a rule, which is why
    /// <see cref="ForwardNeighbours"/> is a filter over this rather than the other way
    /// round.
    /// </summary>
    private List<Waymark> Neighbours(RosterEntry player, bool forwardOnly)
    {
        var from = ZoneOf(player);
        var options = new List<Waymark>();

        foreach (var (a, b) in BlitzsphereLayout.Lanes)
        {
            if (a == from && (!forwardOnly || IsForwardOrLevel(player, from, b))) options.Add(b);
            else if (b == from && (!forwardOnly || IsForwardOrLevel(player, from, a))) options.Add(a);
        }

        // Roles do not cover the whole field: forwards stay out of their own goal,
        // defenders out of the enemy's. Ask the rules rather than restating them.
        if (_shadow.Players.TryGetValue(player.Name, out var tracked))
            options.RemoveAll(zone => !_shadow.CanOccupy(tracked, zone));

        return options;
    }

    private List<RosterEntry> Squad(bool home) => home ? _home : _away;

    /// <summary>The squad minus its goalkeeper, who never leaves goal.</summary>
    private List<RosterEntry> Outfield(bool home) => home ? _homeOutfield : _awayOutfield;

    /// <summary>
    /// The side's midfielder, who contests the blitzoff. Falls back to any outfield
    /// player when a squad has no designated midfielder.
    /// </summary>
    private RosterEntry Midfielder(bool home)
    {
        var squad = Outfield(home);
        return squad.FirstOrDefault(p => p.Role == PlayerRole.Midfield) ?? squad[_random.Next(squad.Count)];
    }

    private bool IsHome(RosterEntry player) =>
        player.Team.Equals(_roster.HomeTeam, StringComparison.OrdinalIgnoreCase);

    private string TeamOf(RosterEntry player) => IsHome(player) ? _roster.HomeTeam : _roster.AwayTeam;

    private RosterEntry Pick(List<RosterEntry> squad) => squad[_random.Next(squad.Count)];

    private static string FirstName(RosterEntry player)
    {
        var space = player.Name.IndexOf(' ');
        return space > 0 ? player.Name[..space] : player.Name;
    }

    /// <summary>Referee shorthand: enough of the first name to be unambiguous.</summary>
    private static string Shorthand(RosterEntry player)
    {
        var first = FirstName(player);
        return first.Length <= 4 ? first : first[..4];
    }
}
