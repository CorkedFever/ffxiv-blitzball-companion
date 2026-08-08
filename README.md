# Blitzball Companion

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin that follows a live game of
Blitzball — the roleplay sport played in Final Fantasy XIV by the Eorzean Blitzball
League — by reading the match out of Yell chat.

Blitzball is played with emotes and `/random` rolls. A referee calls phases, players post
their actions, everyone rolls, and someone tries to hold the whole thing in their head
while also playing. This watches the chat instead: it knows the rules, tracks where every
player is standing, and shows the match on a field view in-game.

> **Status: working, pre-release.** It tracks a full match and has a substantial test
> suite, but it has not yet been through a season of real play. Rules corrections are
> very welcome — see [Getting the rules right](#getting-the-rules-right).

---

## What it does

**Follows the match.** Phases, rounds, sets, score, possession, and every player's
position on the sphere, kept from the chat log alone.

**Knows the rules.** It is not a log viewer — it is a rules engine. It knows that
forwards cannot enter their own goal, that a keeper who catches the ball owes an
immediate clearing pass, that a dazed player drops what they catch, and that a basic
move is not rolled for. When something does not match the rules it says so, and says
why.

**Shows the dice.** Every roll is visible with its modifier as arithmetic rather than a
folded-in total, because the number is the record and "was the bonus counted" is what
people actually argue about.

**Advises, never overrules.** Referees are the authority. Where the tracker disagrees
with what was announced it raises a flag and carries on — it does not try to correct the
match.

**Joins a match already under way.** Chat announces what happens, never what is
currently true, so a log can never tell you where anyone is standing halfway through.
The plugin reads it off the arena instead — everyone is physically on a waymark. Phase,
round, score and possession arrive with the next referee call; what genuinely cannot be
recovered starts blank and says so.

**Practises without twelve other people.** A seeded match generator plays complete games
through the real parser, so the plugin can be developed and demonstrated without an
event to attend.

---

## Installing

Requires Dalamud (API 15).

Add this to **Dalamud Settings → Experimental → Custom Plugin Repositories**:

```
https://raw.githubusercontent.com/CorkedFever/ffxiv-blitzball-companion/main/pluginmaster.json
```

Save, then install **Blitzball Companion** from the plugin installer like any other
plugin. Open it with `/blitz`.

<details>
<summary>Running it from source instead</summary>

1. `dotnet build BlitzballTracker.sln -c Release`
2. **Dalamud Settings → Experimental → Dev Plugin Locations**, add the folder holding
   the built `BlitzballTracker.dll`
3. **Dev Tools → Installed Dev Plugins → BlitzballTracker → Enable**

</details>

### The match arrives on two channels

Players declare and roll in **Yell**. Referees post the structure — phases, rounds and
the score — in the league's **cross-world linkshell**.

With both, everything is tracked. With only Yell, which is what a spectator outside the
league sees, actions, rolls, contests and possession are still followed, but the phase,
round and score stay unknown — and the tracker says so rather than reporting Pre-Game
for an hour.

### Before a match

Load a roster first, on the **Roster** screen. This is not optional and the tracker will
tell you so: without a team sheet it cannot tell a player from a spectator, and a crowd
shouting "BLITZOFF!" turns into twelve phantom players standing on Centre. Rosters can be
saved as named presets, since league sides recur.

---

## Building

```bash
dotnet build BlitzballTracker.sln -c Release
```

```bash
dotnet test BlitzballTracker.sln -c Release
```

The plugin targets `net9.0-windows` via `Dalamud.NET.Sdk/15.0.0`; everything else is
`net8.0`.

### Releasing

```bash
git tag v0.3.0 && git push --tags
```

That is the whole process. The workflow runs the tests, builds Release at that version,
attaches `latest.zip` to a GitHub release, and updates `pluginmaster.json` — which is the
only file Dalamud reads, so a release where that step did not run is one nobody can
install.

### Chat logs are not in the repository

Match logs are full of real players' character names, and those belong to the people who
played rather than to this project. None are committed, and `.gitignore` keeps them out.

Six tests read a recorded match to cover the messiness of real human chat — stray
spacing, world suffixes, smart quotes — which a generator will not reproduce faithfully.
They skip themselves when there is no log to read, so a fresh clone builds and runs green
without one. Drop a log at `BlitzballTracker.Tests/Fixtures/real-match-sample.log` and
they come to life.

---

## Layout

| Project | What it is |
|---|---|
| `BlitzballTracker.Core` | The rules engine, chat parser, and match generator. No UI, no Dalamud. |
| `BlitzballTracker` | The Dalamud plugin: field view, arena overlay, roster editor, stats. |
| `BlitzballTracker.Web` | Blazor app, aimed at a stream overlay. |
| `BlitzballTracker.App` | Console front end for parsing or tailing a recorded log. |
| `BlitzballTracker.Tests` | 350+ tests, most of them rules. |

Assemblies and namespaces still say `BlitzballTracker`. That is internal naming and can
be renamed whenever it is worth the churn; the name people see is Blitzball Companion.

### Design notes

A few decisions that are load-bearing, and easy to undo by accident:

**One rules implementation.** The match generator does not keep its own model of the
field. It drives a real game through the real parser and reads the state back out. When
the two disagree, the parser wins — it is the thing that has to be right. Several bugs
have been caught by the generator being flagged by its own parser.

**Roster-first.** The parser only recognises names on the team sheet. Everything else in
chat — commentary, crowd noise, spectators rolling dice — is ignored by construction
rather than filtered by heuristic.

**Colour means team, and only team.** Status is carried by shape, rings and symbols.
Recolouring a dazed player made them look like they had changed sides.

**Contests are deferred.** Blocks, dives, surveys, fumbles and tie-break rerolls each
arm a state that fires later, at the moment the rules say it fires — not when it was
declared. Several of them deliberately sit outside the one-roll-per-phase rule, and must
never overwrite a player's phase roll.

---

## Getting the rules right

Most of the work here is not code, it is rules.

[`RULES-BACKLOG.md`](RULES-BACKLOG.md) tracks every rule against the published guide with
slide numbers: what is implemented, what is not, what the deck gets wrong, and what is
still an open question. It also records rules that were *settled by a player or referee*
and the reasoning behind them, including several places where the tracker deliberately
departs from the written deck because the game has moved on.

Two habits worth keeping:

- **Retired rules are disabled, not deleted.** `RuleOptions` keeps them behind a switch
  so an old recording stays readable under the rules it was played by. STANDBY is the
  current example.
- **Corrections get recorded with their reasoning**, so the next person does not
  "fix" them back. Several entries exist purely to explain why something that looks
  wrong is correct.

If you play in the league and something here is wrong, that is the most valuable thing
you can report. Say what actually happens at the table — the code follows.

[`ROADMAP.md`](ROADMAP.md) covers what is planned beyond tracking: a stream overlay for
broadcasts, and a play designer for coaches.

---

## Not affiliated

A fan project. Not affiliated with or endorsed by Square Enix or the Eorzean Blitzball
League. FINAL FANTASY XIV is a registered trademark of Square Enix Holdings Co., Ltd.
