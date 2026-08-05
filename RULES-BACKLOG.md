# Rules backlog

Rules from the official EBL guide that the tracker does not model yet.

**Source:** `2026 Blitzball Mechanics [PLAYERS GUIDE].pptx`, edition **v3.2**, 77 slides.
Slide numbers below refer to that deck.

Everything here is *known missing*, not a bug. The tracker's current behaviour is
either to ignore the rule or to approximate it; each entry says which, and where the
change would go.

---

## 1. FUMBLE — slide 50

A DAZED Midfielder, Forward or Defender who is **targeted with a PASS** fumbles it.
Every player **in that zone** then makes an opposed roll and the highest gains the
ball.

- Fumble rolls are **flat `/random 100`** and must be made **even if the player
  already rolled this phase** — the one-roll-per-phase rule does not apply.
- A dazed player who fumbled can win the fumble themselves; doing so does not
  trigger a second fumble.
- Dazed players only fumble on **receiving** the ball.
- A goalkeeper passing more than 2 zones also causes a fumble (slide 42).

**DONE.** `BlitzGame.FumblesOnReceipt` decides it, `FumbleContest` holds the scramble,
and `ChatParser.TryTakeFumbleRoll` routes rolls into it ahead of the phase roll — so a
contender who already rolled this phase keeps that roll intact. Ties fall to the
intended receiver, then to the side that lost it (slide 32).

Rerolls before that default are **not** modelled; see item 4. Contests left unfinished
at a phase boundary are settled on whatever rolls arrived, because an open one would
quietly swallow every later roll from the players in it.

## 2. BACK PASS — slide 43

A backward pass **is legal**, under conditions:

- Only when there are **no unblocked allies 1–3 zones ahead**.
- May target any ally in your own zone, or up to **1 zone behind**.
- After using one, **your team cannot back pass again next round**.
- Cannot be used while in the enemy goal.
- A goalkeeper's pass **never counts** as a back pass (slide 42).

**DONE.** `BlitzGame.AssessPass` recognises a legal back pass and flags only the rest,
with the reason. The once-per-round lockout is `LastBackPassRound`, spent by
`RecordBackPass` when one is announced.

## 3. Interception priority — slide 33

When several actions could stop the same ball, resolve in this order:

1. **Block**
2. **Dive**
3. **Goalkeeper**

> "the closest successful interception beats all other attempts, even if it did not
> roll the highest."

Ties at equal distance go to the highest roll; still tied, roll off repeatedly. That
tiebreak roll **does not replace** either player's phase roll.

**DONE for Block and Dive, Aug 2026.** `ResolveBlockContest` now resolves in tiers rather
than pooling everyone into one roll-off: blocks settle it first, and only if none of them
beats the passer do the divers get looked at. A blocker on 40 takes the ball from a diver
on 99, because they are closer to it.

**Confirmed:** a diver rolls against **the passer** — the player who put the ball in the
air — not the team-mate it was aimed at.

DIVE previously did nothing at all. It armed the state, worked out who was eligible,
printed that they could contest, and never awarded anyone the ball. It is now attached to
the pass at declaration (`ActionEvent.DivedBy`), which also makes the pass a contest so
both sides know a roll is owed.

**The goalkeeper tier is in too.** `ResolveShoot` now runs all three in order: blocks,
then dives, then the net. A shot cut out on the way never reaches the keeper at all — no
save is credited and no goal is scored, because the ball never got there. Blocks and
dives are attached to a SHOOT at declaration the same way they are to a PASS, with a
shot measured to the goal it is travelling toward.

One wiring trap worth remembering: a contested shot used to be diverted to the generic
block contest, which knows nothing about the net behind it, so the blocker "held the
shooter in place" and possession never moved. SHOOT is now matched before the contested
check so all three tiers stay in one place.

Worth recording why this was nearly implemented the wrong way. **"Everyone in the zone
rolls and highest wins" is the FUMBLE rule** (slide 50, item 1), not the interception
rule. The two sit close together and are easy to conflate — both end in a scramble of
rolls — but a fumble is a ball already loose in a zone, while slide 33 is about several
*actions* competing to stop a ball still in flight.

## 4. Tied rolls — slide 32 — DONE

- The referee calls a reroll **at the end of the phase**, not immediately.
- The reroll settles **only that action**; everything else in the phase stands.
- Up to **3 rerolls**. After the third, the tie goes to the **defending** player.
- Defender = the target of the action. In a fumble it is the intended receiver, or
  the team that fumbled.

**Clarified Aug 2026 by a player:** the reroll is *private to the pair*. It is compared
only against the person you tied with — never against anyone else's roll that phase, and
it does not revisit comparisons your original roll already won or lost. Somebody who was
beaten by that roll stays beaten by it.

**Implemented.** `TieBreak` holds the pair, `ChatParser.OpenTieBreaks` calls them at the
phase boundary, and `TryTakeTieBreakRoll` routes rerolls ahead of the phase roll so a
reroll is never mistaken for a roll in the phase that has just begun. A tie-break that
never gets its rerolls is given one phase and then falls to the defender, because an
open one would swallow both players' rolls thereafter.

## 5. PASS distance rules — slides 41, 42

- **1–3 zones ahead:** succeeds automatically, no roll.
- **4 zones (goal to goal):** opposed roll against the goalkeeper. If the keeper
  wins, **they** take the ball.
- Goalkeepers **cannot receive** passes, but can catch fumbles.
- A goalkeeper may pass 0–2 zones; 3 only if no ally is within 2. Beyond that is a
  fumble.
- ~~A goalkeeper who receives the ball **must pass immediately**, resolving before any
  other action (slide 62).~~ **DONE** — confirmed Aug 2026. `BlitzGame.KeeperMustClear`
  is derived from possession, `CanActThisPhase` lets the clearing pass land outside the
  keeper's ring, and `ChatParser.ReportUnclearedKeeper` flags play moving on while they
  still hold it. A keeper's pass is also exempt from the back-pass check per slide 42.

**DONE**, via `BlitzGame.AssessPass`: 1–3 zones carries, 4 is contested by the keeper,
and a keeper's own reach is 0–2 stretching to 3 when nobody is closer. Over that opens
a fumble in the receiving zone.

Two caveats. The goal-to-goal contest is **reported, not resolved** — the tracker says
the keeper contests it and lets the referees settle it, because nothing yet routes a
second roll into that particular opposed pair. And the ambiguity below is resolved in
the strict direction: a legal 3-zone keeper pass does *not* fumble.

**Ambiguity in slide 42 — resolved Aug 2026.** It permits a 3-zone keeper pass when no
ally is within 2, then says a keeper "passing more than 2 zones causes a Fumble", which
describes the pass it just allowed. **Both hold**: the forced long throw is legal *and*
comes loose. The exception is not a safe way to throw further, it is permission to put
the ball into a contest rather than have no option at all.

`PassKind.ForcedLong` covers it — `IsLegal` so it draws no flag, `Arrives` false so the
receiving zone contests it.

## 5b. Goalkeeper action restrictions — slide 62

Two more exclusions on the same slide, neither modelled:

- Goalkeepers **cannot receive passes** (they can still catch fumbles). Nothing
  currently rejects `[PASS -> <keeper>]`.
- Goalkeepers have **no Move, Survey or Block action**. **DONE** — Survey now refuses
  the same way Block already did, and Move is covered by the keeper never leaving goal.

The first is reported by `AssessPass` returning `KeeperCannotReceive`; it is advisory,
like everything else here, because the referees announced the pass.

## 6. Round 10 Inner Ball Carrier must SHOOT — slide 23 — DONE

On round 10 of either set, during the Inner Ball Carrier phase, the carrier **must**
shoot. No other action is valid. The Buzzer phase ends the same way (slide 27).

**Implemented Aug 2026.** `BlitzGame.BallCarrierMustShoot` covers both cases;
`ChatParser.TryParseAction` refuses any other declared action outright rather than
crediting it, and the Match view shows a **MUST SHOOT** pill so it is visible before
anyone declares.

Scoped to the *Inner* carrier turn and the Buzzer, never the Outer carrier turn.
**Confirmed by a referee (Ffon Aveross) on 4 Aug 2026: "BC Inner or Buzzer."**
Slide 19 places no such requirement on the Outer turn, which reads as deliberate:
a whole Inner phase still follows it.

## 7. Blitzon and the halftime restart — slide 15

- Goal scored while **tied** → reset positions, blitzoff again. *(Modelled.)*
- Goal scored while **not tied** → **Blitzon**: the losing team receives the ball
  **without rolling for it**.
- Round 1 of Set 2 → blitzoff where the losing team gets **+10 × point deficit**.

**DONE.** `BlitzoffKind` names the three restarts and `AnnounceBlitzoffVariant` picks
between them from the score and whether halftime just ended. A Blitzon says who receives
and that there is no roll-off, and flags it if the ball then goes to the side in front —
there is no roll to lose, so anything else is a miscall. The halftime restart announces
the trailing side's bonus, and `BlitzGame.BlitzoffBonus` computes it.

Reported rather than decided: the referees announce who actually ends up with the ball,
so the tracker says what should happen and flags a mismatch.

## 8. Ball carrier movement limits — slide 52

The carrier:

- **must** move toward the enemy goal,
- cannot move backwards **or within the same zone**,
- cannot move **down lanes** unless a Rush Gate enables it,
- must SHOOT or PASS if a role restriction blocks their only move.

**DONE.** `BlitzGame.CarrierMayMoveTo` requires the move to strictly advance toward the
enemy goal, which covers backwards and within-zone in one test — the two lanes of a zone
sit at the same rank, so crossing between them advances nothing. A refused move says to
SHOOT or PASS instead, and names which restriction bit.

`CarrierMayDeclare` closes everything but MOVE, PASS and SHOOT while they have it.

The "cannot move down lanes" clause needs no check of its own: the sphere's walkable
connections never join two waymarks in the same lane, so there is no such move to make
unless a Rush Gate opens one.

## 9. Shootout — slide 28

- Home team lines up on the **Letter Lane**, away on the **Number Lane**.
- Order: **Midfielder, Left Forward, Right Forward, Left Defender, Right Defender**.
- Goalkeepers do not move. Captains roll off for who shoots first.
- Each shooter moves to **C**, shoots, and leaves.
- **No modifiers.** Five flat `/random 100` per side, each opposed by a flat keeper
  roll.
- Post the action first, then roll — rolling first **voids** the roll.
- Most goals wins by 1 point.

**DONE.** Flat rolls both ends, no modifiers of any kind — `ResolveShoot` diverts to
`ResolveShootoutAttempt` during the phase, so neither the keeper's distance bonus, their
GUARD, nor the shooter's class modifier applies. Sides alternate from the roll-off winner
(`[[ FIRST -- team ]]`) and each works down its own line, with an advisory when somebody
steps up out of turn. The tally is kept apart from the match score and only the winner's
**single point** joins it, because that point is what breaks the tie.

The generator was firing five shots against a bare threshold with no keeper on the other
end and no ordering — a coin toss rather than a shootout.

Not modelled: the 180-second posting window, and rolling before posting voiding the roll
outright (it draws the ordinary advisory instead).

## 10. Sudden death — slide 29

- Sphere empties, keepers included. Both captains meet at **C**.
- Referee calls a final blitzoff; both captains roll.
- Whoever **loses** the blitzoff gets one chance to BLOCK.
- A blocked captain must win another opposed roll to shoot, or the ball is
  intercepted.
- On interception the captain who just shot gets one chance to block. Repeat.
- An unblocked, uníntercepted shot **wins instantly**.

**DONE.** `SuddenDeath` holds the duel: who has the ball, who owes the block, and whether
the holder is currently blocked. The sphere empties on the call, taking possession makes
you the shooter and the other captain the challenger, and an **unblocked shot ends the
match on the spot** — score, final whistle, `PostGame`. A blocked captain still has to
beat the block, which resolves through the ordinary opposed path so the numbers are
compared once rather than twice.

The generator plays the exchange out, bounded at eight turnovers: a duel that long has
said everything it has to say, and a generated match has to terminate.

Not modelled: substituting a co-captain, coach or veteran for the captain — that is an
organisational rule rather than a mechanical one.

---

## Terminology

The rulebook's terms are the inverse of the code's, and they are the words referees
and players actually use:

| Rulebook | Meaning | Code currently calls it |
|---|---|---|
| **Zone** | A column across the field: `{D}`, `{A,1}`, `{C}`, `{B,2}`, `{4}` | rank (`BlitzGame.ZoneRank`) |
| **Lane** | `A↔B`, `1↔2`, `D↔C↔4` | row (`BlitzsphereLayout.Rows`) |
| *(no term)* | Movement connections, the zig-zag | `BlitzsphereLayout.Lanes` |

The geometry is correct; only the naming is wrong. The internal names still say row and
column, but **what the tracker says out loud now uses the rulebook's words** — the
tackle-reach advisory reads "is not in their lane or their zone". Renaming the types
themselves is optional from here; nobody but us reads those.

Slide 9 calls zones "diagonal" connections, but that is relative to the deck's own
rotated diagram. Laid out as the arena actually is — D on the left, 4 on the right,
letter lane along the top — the five zones of slide 11 are **columns**: `{D}`,
`{A,1}`, `{C}`, `{B,2}`, `{4}`. That matches `ZoneRank` exactly, and it means the
cross-lane pairs `A/1` and `B/2` are zones rather than some separate concept.

## The deck is out of date: STANDBY was retired

Slides 31, 35 and 49 describe **STANDBY**, a status applied automatically when a
player declares nothing before the phase timer expires. Confirmed Aug 2026: **that
status no longer exists in the game.** The deck was not updated.

Letting a phase run out is still a loss of action and can still be flagged — it simply
is not a named state any more. By default the tracker reports it as *"Loss of action —
nothing declared"* and applies no status.

**It is disabled, not deleted.** `RuleOptions.StandbyStatus` (off by default) restores
it, and *Settings → Rules edition* exposes the switch. Turning it on names the flag
STANDBY and sets `PlayerState.IsStandby` for the phase. That is deliberate: league
rules move, a retired rule can come back, and an old recording should be readable
under the rules it was played by.

So do not delete the option because the status is gone, and do not turn it on by
default because the deck describes it. A later pass over the rulebook will find three
slides describing Standby in detail and it will look like an omission — it is not.

Same applies to the fallback conversions on slides 56, 59 and 63: a Shove with no
legal target is described as becoming Standby. With the status off, the action is
simply lost.

## 12. Buzzer shot chains — slide 27

The buzzer phase itself is now generated and parsed: it fires when Round 10 ends with
the ball in a strike zone, only players sharing the ball's **waymark** act, and the
carrier then must shoot. The final whistle sets `PostGame` and `IsFinished`.

**DONE.** `BuzzerShot` holds the chain and `BlitzGame.IsFinalExchange` decides when it
applies — the buzzer phase, and Round 10's inner carrier turn. A ball lost at that point
does not simply end the set:

- A carrier blocked or dived out during the final exchange hands the *new* holder a shot
  from wherever they stand, with the player who just lost it rolling to intercept.
- A goalkeeper who catches one answers with an immediate goal-to-goal shot.
- The chain is capped at **two** links: the second time the ball goes, the set is over
  regardless, and the log says so.

Outside the final exchange a lost ball is just a lost ball — no chain opens.

## Settled: the goal restrictions bind tackles too

Confirmed Aug 2026 by a player of the role. A tackle is a movement that ends with the
tackler standing in the waymark they declared, so `CanOccupy` applies to it exactly as
it applies to a move: **a forward cannot declare a tackle into their own goal**, and a
defender cannot declare one into the enemy's. Tackling a keeper in the goal you are
*attacking* stays legal — that is what the ability is for.

This one is refused rather than merely flagged, unlike the reach and role advisories.
Those can fire because the tracker has someone in the wrong zone; this cannot be a
legal declaration under any positioning, so it does not daze the target and Reposition
does not carry the tackler in.

## Settled: there is no occupancy limit on a waymark

Confirmed Aug 2026. Any number of players may share a waymark — five of one side on a
single marker is legal. It is simply poor play, not a rule violation.

**Do not add a limit.** A generated match will sometimes pile a whole side onto one
marker and it looks like a bug; it is not one, and the tracker must not refuse or flag
it. The generator spreads its movement out so simulated matches read more like real
ones, but that is a *taste* in `MatchSimulator`, not a rule, and it belongs nowhere near
the rules engine.

## Settled: movement is unrestricted apart from the goals

Confirmed Aug 2026. Players move freely between waymarks along the standard lane
connections, in any direction. Exactly three restrictions exist, and all three are
about goals:

- The **goalkeeper** never leaves their own goal waymark.
- A **forward** may not enter their own team's goal waymark.
- A **defender** may not enter the opponent's goal waymark.

That is the whole of it, and it is already `BlitzGame.CanOccupy`.

**Do not extend "the ball never travels backwards" to players.** It applies to passing
only (`IsBackwardPass`). Players retreat and cross freely; a forward with nothing ahead
of them drops back rather than losing their action. Preferring to advance is a
*strategy*, which is why it lives in the generator (`ForwardNeighbours`) and nowhere in
the rules engine.

## Open question: which other actions are rolled for

Confirmed Aug 2026: **a basic MOVE is not rolled for.** You declare the waymark and
you go; the dice only come out when something contests the movement. This is now
`BlitzGame.CallsForRoll`, and the generator no longer manufactures a roll after every
move.

**GUARD is not rolled for either.** Confirmed Aug 2026: it raises the keeper's bonus and
there is nothing on the other side of it. Handled the same way as MOVE.

**Daze takes ten off a keeper, not the lot.** Slide 59 is explicit — "their catch bonus
is lowered by 10" — and slide 66's "the GUARD is removed" means the single activation
they just made, each worth ten. A keeper who guarded twice keeps the first. This was
briefly implemented as zeroing the whole bonus, which is wrong.

**Nor is a PASS inside its range** (slide 41). Only blockers standing against the passer
turn one into a contest. This was the cause of a real defect: the generator rolled for a
keeper's clearing pass, but the keeper had *just* rolled contesting the shot they caught
— so the parser read it as a re-roll, un-applied the save, re-resolved the shot as a
goal, and the clearance was then thrown by a keeper who no longer had the ball.

**Nor is declaring a SURVEY.** Confirmed Aug 2026: the roll happens **at Reposition**,
when somebody actually tries to come through the lane being watched. Declaring it only
arms the guard.

`SurveyContest` holds the roll-off. A move caught by an opposing survey is held up
rather than landing, both players roll, and the mover only goes through if they win —
a tie holds the lane, since the surveyor is the one defending it. Contests left unrolled
at a phase boundary close the same way, with the mover staying put.

## Open conflict: Rush Gate duration

- **Told to us:** a gate lasts until the **end of the round**.
- **Slide 65:** *"It lasts until the start of your next turn."*

Since the goalkeeper acts once per round these nearly coincide, but they differ when a
goal resets play mid-round. The code currently clears gates when a new round begins,
per the first version. Worth confirming.

## Also noted

- **Rush Gates are placed with field marker 3** (slide 65). The plugin already reads
  waymarks from `MarkingController` and deliberately skips slot 6 (marker "3") — it
  could read gate positions straight from the game instead of only from chat.
- **DONE** — Survey can cancel a **Tackle** coming down the surveyed lane (slide 59).
  A beaten tackle is cancelled outright rather than merely halted, so `UnapplyAction`
  takes the daze off with it.
- **DONE** — a survey **cannot catch somebody leaving the waymark it surveys from**
  (slide 48). It watches the lane ahead; a player standing alongside the surveyor and
  setting off elsewhere was never in it.
- **DONE** — Rally with no legal target becomes a **Survey**, Tackle with no legal
  target becomes a **Move** (slides 56, 59), via `ChatParser.ConvertAction`. A tackle
  only converts when a waymark was named too: one with neither target nor destination
  is an unparsed post, and inventing a move from it would be worse than leaving it.
  Shove with no legal target is simply lost.
- Survey can also be **cancelled by a successful Survey**, and **stops a Rush** before
  it happens (slide 48). Neither is modelled — Rush contests are not either.
- A successful Block **completely negates** the target's action, which is why a Block
  can cancel another Block (slide 44). Also unmodelled: a block puts **both** players
  in the BLOCKED state, and a blocked player receiving the ball forces an opposed roll
  where the winner catches it whoever it was meant for.
- **RALLY itself is still not resolved** (slide 56). The midfielder lends their roll to
  a team-mate in their zone when it beats theirs, and it lasts only that phase. The
  no-target conversion above is in; the lending is not.
