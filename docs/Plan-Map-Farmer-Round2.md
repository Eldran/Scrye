# Plan: the mapper/farmer pair, round two

*Drafted 2 Sep 2026 from a code review of `_lab/plugins/3s-map-explorer` (0.4.0) and
`_lab/plugins/3s-farmer` (0.6.0) against the shipped `3s-map-gmcp` (1.7.0), the harnesses,
the live captures in `_lab/captures/` and the client's plugin runtime. Everything below was
checked against the source; the line numbers are today's. Nothing here is built yet.*

## Where things stand, verified today

- **mapx is byte-identical to `derive_explorer.py`(mapg 1.7.0).** No drift. The fold-in
  (Plan-Improvements B2) is still the right end state; this plan front-loads the sweep fixes
  into mapg so the fold-in carries them.
- **The farmer has all four phases** of Plan-Map-Farmer.md: graph, patrol, fists, travel.
  Harness sections 1–33e cover them. It has never had an HP number in it.
- **The client is at plugin API 1.19**; the Guide still says "Current API version: 1.12".
  Everything from 1.13 (button colour) to 1.19 (`menu=` markup) is documented only in
  `ScryeApi.cs` comments. Both plugins already use 1.14/1.15/1.18.
- **Two GMCP packages arrive on every room entry that neither plugin reads:** `Room.Map`
  (a 17×`h` line-of-sight grid with `m` = monsters, `p` = players, `#` = dark rooms beyond
  sight) and `Char.Vitals` (`hp`/`maxhp`). The first is the farmer's missing sense; the
  second is the safety floor both bots lack. Both are in every capture from 28 Aug on.

## The one-line verdicts

The explorer's sweep is sound but brittle at exactly two doors: a probe that does not land
kills the whole sweep and is not remembered, and nothing fences it to an area or to an HP
floor. The farmer's chassis and fists are sound; its blind spots are that it has no idea how
hurt it is, that a `kill` that never becomes a fight freezes it, and that it picks the mob's
keyword by a rule that is wrong for every "X of Y" name. None of the fixes are large.

## Findings — 3s-map-explorer (`mapx`)

**E1. A blocked probe ends the sweep and is never remembered.** `explore_continue` sends one
step through an unexplored exit as a probe (line 1195). A closed door, a guard, or a
`You cannot go` gets no `Room.Info`, so the watchdog fires after `WALK_WAIT` (10 s) and
`walk_stop(why)` takes the explore down with it (lines 956–959). The exit is still frontier
(`frontier_dirs` knows nothing about the failure), so the next `mapx explore all` walks
straight back to the same door. There is no `You cannot go` trigger in mapx at all.
*Fix:* a probe is a special case of the walk contract: when no `Room.Info` follows, our
position is still exact — nothing moved. So on `You cannot go`/`The door is closed`/the
watchdog *during a probe*, record `rooms[here].blocked[dir] = n` (persisted count, like
`vary`), drop it from `frontier_dirs`, and let the sweep continue to the next frontier. A
requested walk keeps today's stop rule. `mapx blocked` lists them, `mapx blocked <n> <dir>
off` clears one. (Only a probe, and only when nothing arrived — a probe that landed
somewhere is a success whatever the room.)

**E2. No fence.** `explore all` sweeps everything reachable through known links: across
area boundaries, into aggro areas, into "Unknown" corridors that go on for hundreds of rooms.
That is what the plan called "an automaton that wants supervision". The farmer solved the
same problem with a fence; the explorer needs one too. *Fix:* `mapx explore area` (and make
`explore all` the explicit whole-world form). A frontier exit's destination is by definition
not in the store, so its area cannot be checked before probing — lab-areabot's rule applies:
probe, and if the arrival's `area` differs from the locked area, step back through the
reverse direction (compass only; a non-compass door is left marked and not re-probed) and
mark the exit `edge[dir] = <area seen>` on the source room, persisted. The boundary room is
still mapped — that is a free gift to the world map — but the sweep never counts that exit
frontier for that area again. Locking to "Unknown" gets a warning, not a refusal: sometimes a
corridor is exactly what you want to fill in.

**E3. No HP floor.** Nothing in mapx reads vitals; a sweep walks into whatever is behind
the door at any HP. *Fix:* `mapx explore hp <pct>` (persisted, default 50). Read
`char.vitals.hp`/`maxhp` from the state tree (GMCP), fall back to
`character.health.current/max` (MIP). Below the floor: stop the sweep with a reason, exactly
like combat. Never resumes on its own, same as everything else here.

**E4. The move queue's clock is 15 s coarse.** `now` advances only in the flush tick
(`now = now + FLUSH_SECS`, line 1973), so `MOVE_TTL = 12` is really "somewhere between 0 and
15 seconds": a move typed just before a flush tick is pruned by `prune_moves` almost at once,
its `Room.Info` pairs with nothing, and the walked link is silently not learned. The explorer
already runs a 1 s tick for the fight flag (`cc_now`). *Fix, in mapg:* one 1 s tick drives
`now`; flush every 15 ticks. Derive follows. Cheap, and it closes a real, if rare, hole in
link learning on a laggy connection.

**E5. Sweep budget and summary.** A walk is capped at `WALK_CAP` steps but the sweep is
unbounded. Add `mapx explore area [N]` / `explore all [N]` — stop after N new rooms and say
so — and put the sweep summary (rooms learned, exits blocked, edges found, why it stopped)
on the panel as well as in the chat.

**E6. The panel has no explore controls.** There is a Stop button and nothing to start a
sweep from; status says nothing while exploring. *Fix:* an Explore button beside Stop,
coloured by state (API 1.13, as the farmer plan promised for its own buttons), and a status
line `EXPLORING <area> · 14 unexplored · +23 rooms`. The frontier count belongs in
`maps_cache` so it is not recomputed on every draw.

**E7. Fold-in (Plan-Improvements B2).** After E1–E4 land — E4 in mapg directly, E1–E3 in
the explorer block of the derive script — port the sweep into mapg 1.8.0 as `mapg explore
area|all`, append mapx's explorer harness block to `mapgmcp_test.lua`, delete
`derive_explorer.py` and the `_lab` copy, and update the Guide's `mapg` section. The farmer's
`map.*` contract is untouched by this.

**E8. Store growth, worth measuring not guessing.** `save()` re-encodes every room and
`scrye.shared.set` rewrites the whole file, every 15 s while dirty — during a sweep that is
constantly. At a few thousand rooms it is a ~1 MB JSON rewrite per flush on the loop thread.
The plugin diagnostics already report slow dispatches; run one long sweep and look. If it
shows: rooms keyed per area with `setMany` (the Guide's own advice), or a longer flush while
a sweep is running. Not before it shows.

**E9. Small things, noted for the fold-in.** The held/sea early return sets `dirty = true`
having changed nothing (line 425). `mapx stop` while exploring-without-a-walk is unreachable
today, as the comment says; after E1 it becomes reachable (the sweep is between probes when a
blocked exit is being recorded), so that branch stops being belt-and-braces.

## Findings — 3s-farmer (`farm`)

**F1. No HP floor and no danger stop — the most important line in this document.** The
bot attacks anything not excluded, at any HP, and nothing in `main.lua` reads a vital. The
Seid rest gate (`C.rest_below`) is the only "am I fit to fight" check, and it is guild-specific.
*Fix:* `farm hp <start%> [<panic%>]`, persisted per character in `scrye.store`. Below
*start* no new fight is started: rest like the Seid gate (fold both into one "fit to fight"
gate that checks every configured floor) and re-check on a timer. Below *panic* mid-fight:
stop the patrol, `scrye.notify`, and optionally send one configured command
(`farm panic <cmd>`, default none — "flee" and "wimpy" are the user's call, and either one
moves you, which already stops the patrol by the arrival-nothing-ordered rule). Source:
`char.vitals.hp`/`maxhp` (GMCP), fallback `character.health.*` (MIP). Panel gets an HP gauge.

**F2. A hunt has no watchdog.** `attack()` sends `kill <kw>` and sets `B.hunting = true`
(line 452). If no fight follows — an unattackable NPC, a keyword the parser consumed
without a "There is no X here", a mob that left between roster and swing — nothing ever
clears `hunting`: `step_out` returns on it (line 319), `on_prompt` returns on it (line
459), and the only timeout in the clock is for `S.step`. The patrol is frozen until a human
notices. *Fix:* arm a hunt timer in `attack()` (`C.hunt_wait`, ~8 s): if neither
`Char.Combat` nor `enemy.name` shows a fight by then, put that name on a per-room-visit skip
list, consult the roster again, and if nothing is left, patrol on. Harness: a `kill` with no
combat payload ever arriving.

**F3. The keyword rule is wrong for "X of Y" names.** `keyword()` takes the last plain word
(line 221). "A warband in service to Goran [Legendary]" → `kill goran`; "A guard of the
gate" → `kill gate`; a name ending in `)` matches nothing and the mob is skipped forever
(only `[]` and `{}` are stripped). A wrong keyword today ends in "There is no X here",
which *drops the mob* (line 794) rather than trying another word. *Fix:* build a candidate
list per name — words from the end, skipping stop words (`the of a an in to and service
with`) — and rotate to the next candidate on "There is no X here"; drop the mob only when
the list is exhausted. Strip `%b()` too. `count` from `Room.Contents` is worth carrying for
the panel ("2× Neon Fang") even though targeting does not need it.

**F4. `Room.Map` is the farmer's missing sense.** Every arrival burst ends with a
line-of-sight grid (`kind:"los"` once cartography is trained; `compass` below it). In it
`m` marks a neighbouring room with monsters and `p` one with players, several rooms out.
Today the patrol finds mobs by walking to the stalest room and looking; with the grid it can
*go where the mobs are*. Cells map to room numbers without any coordinates: from `@`, follow
each link glyph (`-` `|` `/` `\`) one cell to a room glyph, the direction of that hop names
the exit, `G[here].exits[dir]` names the room, recurse. *Fix:* `pick_target` prefers the
nearest reachable in-area room flagged `m`, skips rooms flagged `p` (a stranger there means
walk in, park, walk out — better not to walk in), and falls back to stalest-first when the
grid shows nothing. Degrades to today's behaviour on `kind:"compass"`. The capture rows in
`gmcp-fields-Goran-20260831-0942.md` are the harness fixtures — twelve real grids, including
diagonals and a `+` updown cell.

**F5. The 1-second dispatch race can go.** `map.walk.arrived` may reach the farmer before
its own `Room.Info` hook has run, so `start()` is deferred by a timer (lines 1022–1030).
The mapper emits `map.room` `{ num, name, area, exits }` on every mapped arrival *before*
`walk_arrived` — so the farmer can update `G` and `here` from `map.room` (idempotent with
its own hook) and `start()` immediately in `map.walk.arrived`. Timer and race both go;
harness 33d still applies, now asserting no delay.

**F6. The panel shows the bot's state but not the fight.** Status, three counters, excludes.
Add: an HP gauge (F1), the current target and its `attacker_hp` from `Char.Combat` (already
in every payload), the roster as a `table` (name · count · verdict: target / excluded /
never / party / stranger), kills per hour, and buttons coloured by state (1.13) — Start lit
while patrolling, Pause lit while paused — which the original plan promised and 0.6.0 does
not do. `farm stats` persisted per area (kills, minutes, rooms) is the later step that lets
the farmer say which area pays.

**F7. Looting is left to the user's triggers.** That is a fine default, but a
`farm after <cmd>` (sent once after the breath, off by default) removes a setup step nobody
remembers until the first corpse. One line of config, one line of code.

**F8. Own followers.** The never-list is the right tool and the manual entry is the honest
one: no capture carries the character's own name in a way the farmer could read, so an
automatic "in service to <me>" rule has no source yet. If a `Char.Status`/name field ever
shows up, add it then.

**F9. Small things.** `farm never`/`farm party` append without dedupe. `farm exclude` while
unlocked refuses; `farm exclude <area>: <name>` would let you prepare excludes before
travelling. A self-pointing exit can never become a step — `survey()` marks `here` seen
before it starts, so `pick_target` cannot choose it — which is worth a harness line so it
stays that way.

**F10. Promotion (Plan-Improvements B3).** Gate on F1, F2, F3 — the three that decide
whether a bot can be left alone for ten minutes — then live runs in two areas with different
mob naming. Then `src/Scrye.App/plugins/3s-farmer` at 1.0.0, a description pass on
`plugin.json`, a Guide section, and the C# delegation-style test the plan asked for in phase
3 (`ChaosSeaDelegationTests` is the pattern; it can only reference the plugin once it is in
`src`, which is why it does not exist yet).

## Scrye client / API

**C1. The Guide is seven minor versions behind.** `ScryeApi.Current` is 1.19; the Guide
says 1.12. 1.13 button `color`, 1.14 `scrye.shared`, 1.15 `onRowClick`, 1.16 `rclick=`,
1.17 colorgrid `images`, 1.18 right-click menus / `onRowMenu`, 1.19 `menu=` are all used by
shipped plugins and documented nowhere a plugin author would look. Copy the `ScryeApi.cs`
comments into the Guide's version history and the widget table. No code.

**C2. Request/response between plugins (API 1.20, additive).** Today `map.query.area` →
`map.area.rooms` and `map.goto` → `map.walk.*` work because events dispatch synchronously
and in load order, so the answer arrives *inside* the `emit` — true, relied on by the farmer
(comment at line 571), and documented nowhere. Travel needs a 2 s "nobody answered" timer to
tell an absent mapper from a slow one. Proposal: `scrye.request(name, data) → reply | nil`
and `scrye.onRequest(name, function(data, source) return reply end)`; first non-nil reply
wins, same depth cap as `emit`. The farmer's `start()` becomes one line, `travel()` loses its
timer, and "no mapper answered" is `nil` rather than silence. `emit`/`on` stay for the
broadcast cases (`map.room`, `map.hold`).

**C3. Plugin dispatch order is load order and undocumented.** `_runtimes.Add` in
`PluginManager` is the whole rule. F5 removes the one place it bit, but a sentence in the
Guide's inter-plugin section ("hooks fire in plugin load order; a plugin that consumes
another plugin's event from inside a GMCP burst may run before its own hook for that
package") saves the next author the hour.

**C4. Store writes are one file rewrite per `set`, synchronously.** `PluginDataStore.Save`
serialises the whole map and writes it on every call; `setMany` only batches within one
call. A host-side coalescer — dirty flag, write at most every ~500 ms, flush on unload and
disconnect — would make every plugin's `save()` free and let the mapper and farmer drop
their own flush timers and `dirty` bookkeeping. Do E8's measurement first; if the sweep never
shows a slow dispatch, this stays on the list rather than in the code.

**C5. A harness runner with a drift check.** No `lua5.4` on the dev VM, but
`_stage_tmp/lua54.exe` exists. A `_lab/run_all.ps1` that runs every `*_test.lua`, then
regenerates the explorer with `derive_explorer.py` and diffs it against the checked-in copy
(the drift the plan names as its first risk), is an afternoon and becomes the pre-commit
habit. Moot for mapx after E7, still useful for everything else in `_lab`.

**C6. Burst order, written down.** `Room.Info` → `Room.Contents` → `Room.Map` is the order
every capture shows, and the chaos-sea bot already leans on `Room.Map` as the burst's end.
Add it to the Guide's "if you are building a mapper" section; F4 will lean on it too.

## Order of work, each step gated by its harness and a mutation pass

**~~Phase A~~ — built 2 Sep.** F1 (HP gate + panic: `farm hp <start%> [<panic%>]`,
`farm panic <cmd>`, the prompt honours the gate too, a floor with no feed refuses to
start), F2 (hunt watchdog, 8 s, give-ups per room), F3 (keyword candidates, owner half of
"in service to" last, rotated on "There is no X here"), E1 (blocked probes: watchdog or
`You cannot go` during a probe marks the exit for the session, `mapx blocked`, the sweep
goes on; a refusal during a requested walk stops it at once), E3 (`mapx explore hp`,
default 50, checked before each step and on the tick), E4 (mapg's clock is 1 s; the
flush rides it). Shipped as farmer 0.7.0 (176 checks, 10 mutants killed), mapx 0.5.0
(306, 7 killed), mapg 1.7.1 (335, 1 killed — the old clock). mapx re-derived, no drift.
Blocked marks are deliberately session-only until the fold-in (E7) decides where a
persisted count belongs.

**Phase B — smarter, next.** F4 (Room.Map targeting, with the capture grids as fixtures), E2
(area fence + `explore area`), F5 (drop the race timer), E5/E6 and F6 (panels and budgets),
F7 (`farm after`). Farmer 0.8.0, mapx 0.6.0.

**Phase C — consolidate.** E7 (fold the sweep into mapg 1.8.0, retire mapx and the derive
script), F10 (promote the farmer to `src` at 1.0.0 with its C# test), C1 and C6 (Guide),
C5 (runner).

**Phase D — API, when B and C are done and the seams still itch.** C2 (`scrye.request`),
C4 (store coalescing), C3 (one paragraph, can go with C1).

## Risks named now

- **Room.Map cell→room pairing is only as good as `G[here].exits`.** A grid link with no
  matching exit in the graph (a hidden exit the server drew but never listed) is skipped, not
  guessed — the farmer walks only where the graph says it can, same fence as today.
- **The HP gate reads two feeds.** A character with neither (no GMCP `Char.Vitals`, no MIP
  health) has no number; the gate must say so at `farm start` and refuse to run with a floor
  set and no source, rather than treating "unknown" as "fine".
- **Stepping back at an area edge is itself a move** (E2). It goes through the same
  confirmed-arrival path as any step, and a step-back that lands elsewhere stops the sweep like
  any other wrong arrival.
- **Keyword rotation can hit the wrong mob** when two names share a word. The roster check
  after each kill already catches "wrong mob died" (the roster changes either way); the
  exclude list is the remedy if a specific room keeps doing it.
