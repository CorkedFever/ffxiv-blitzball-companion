# Roadmap

Beyond tracking a live match, the goal is a Blitzball app: something coaches and
broadcasters use, not just players.

---

## ~~Known defect: the live feed has no roster~~ — FIXED

`LiveFeedClient.SendRoster` posts the lineup as the same header format recordings use,
and the far end rebuilds it through `BlitzGame.ApplyRoster`. Sent when the feed starts
and again whenever the roster is applied, so a substitution mid-match keeps the
broadcast in step.

Two things were wrong, not one. The plugin never sent a roster at all — and the
endpoint that existed to receive one built `PlayerState` objects by hand from a bespoke
DTO, never setting `CurrentRoster`. That is what `ChatParser` keys its name index off,
so the index was never built and every player name was discarded regardless. Phases and
the scoreboard worked while the field stayed empty, which reads as a rendering fault.

Sharing `RosterHeader` rather than a parallel DTO means one format and one parser on
both sides. `LiveFeedRosterTests` covers the round trip, and pins the specific trap:
a full `Players` dictionary with no `CurrentRoster` still recognises nobody.

---

## Stream overlay

For broadcasting matches on Twitch. The Blazor app already has `LiveService`,
`Blitzsphere.razor` and a Live page, so most pieces exist.

- A dedicated `/overlay` route styled for OBS: transparent background, no chrome, no
  navigation.
- Scoreboard, phase and countdown, the sphere, and a play-by-play ticker.
- Sized and laid out for a browser source rather than a browser window.
- Should degrade gracefully when the feed stalls — a frozen overlay on stream is
  worse than one that says it lost the feed.

No longer blocked: the roster now reaches the web app, so the field renders.

## Play designer for coaches

**Sharing is not needed.** Confirmed Aug 2026: the team uses Discord voice, and the
coach calls out each player's move for them to execute. So this is a planning and
reading surface for one person, not a distribution problem — no network transport, no
export format, no teammate-side plugin support required.

That makes it much smaller than it first appears. What it needs:

- A canvas of the Blitzsphere where the coach places the six players and assigns each
  an action for the coming phase.
- Named plays, saved and reloaded — league sides face the same opponents repeatedly.
- Legible at a glance while talking: the coach is reading this aloud under a 60-second
  phase timer, so it has to be scannable, not studied.
- Validation against the rules engine would be a real differentiator: the tracker
  already knows that forwards cannot enter their own goal, that a blocked carrier can
  only shoot or pass, and how far a tackle reaches. A designer that refuses to let a
  coach draw up an illegal play is worth more than a drawing tool.

Could live in the plugin, the web app, or both. The web app is the easier canvas; the
plugin is where the coach already is during a match.

---

## ~~Open question: mid-match roster changes~~ — HANDLED

Confirmed Aug 2026 that teams do this, commonly after halftime. `BlitzGame.Substitute`
swaps one player for another without disturbing the match: the substitute takes the
role and the place on the field, the stats stay with whoever earned them, possession
goes with the shirt, and blocks the departing player was holding are released.

Re-applying an edited roster is *not* a substitution — `ApplyRoster` clears every player
and resets positions, so mid-match it would lose every stat and send both sides back to
their kickoff formation. Hence the separate path, and a separate control on the Roster
screen.

One trap it exists to avoid: `ChatParser` rebuilds its name index only when the roster
*reference* changes, so a substitution that mutated the roster in place would leave the
substitute unrecognised and every action of theirs discarded. `Substitute` builds a
fresh roster instance for exactly that reason, and there is a test on it.

Explicit `[[SUB: X for Y]]` chat parsing is still not done, and would only be worth it
if the leagues settle on a consistent format.

## Open question

Which of the two to build first was left undecided.
