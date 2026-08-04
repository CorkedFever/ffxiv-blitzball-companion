# Roadmap

Beyond tracking a live match, the goal is a Blitzball app: something coaches and
broadcasters use, not just players.

---

## Known defect: the live feed has no roster

**This blocks the stream overlay and is a bug today.**

`LiveFeedClient` posts raw chat lines to the web app, which re-parses them through its
own `ChatParser`. But `LiveService` constructs a bare `BlitzGame` with no roster.

The parser is roster-gated by design: with no roster it rejects every player name.
So the web app currently shows phases and the scoreboard while the field stays empty
and possession never resolves — the same failure the plugin had before roster support
landed.

**Fix:** send the roster with the feed. Either a `POST /api/live/roster` when the feed
starts and whenever the roster changes, or embed it in the first payload.
`RosterHeader` already serialises a roster to text for recordings and could carry it.

Worth doing regardless of which feature comes first.

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

Depends on the roster fix above.

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

## Open question

Which of the two to build first was left undecided.
