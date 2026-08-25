# Plan: the mapper/farmer pair

*Drafted 25 Aug 2026, from the planning discussion. The vision in one sentence: explore an
area with the mapper, press Start, and a farming bot loops that area killing what lives
there — using the map, fed entirely by GMCP, with nothing saved to mob files and nothing
stepping outside the area you chose.*

## Decisions already taken

- **Two plugins, not one.** The mapper stays the only thing that knows the world and the only
  thing that walks long routes; the farmer decides what dies and where to stand. They meet at
  a small event contract, the way `map.hold` already lets the chaos-sea bot silence two
  mappers that don't know each other exists. The hard constraints force this anyway: plugin
  stores are private (the farmer cannot read the mapper's rooms), Lua caps a chunk at 200
  locals (the mapper is at ~97), and the instruction budget punishes one callback doing
  everything.
- **Lua.** JS (Jint) is supported but buys nothing here: the local cap is the only constraint
  it removes and state tables remove that for free, Jint is slower per unit of work against
  the same wall-clock budget, and every piece of proven machinery we want to reuse — the
  chaos-sea combat loop, the harness pattern, the mutation tooling — is Lua.
- **The farmer ships beside the old `3s-stepper`** under its own id until it has earned the
  replacement, exactly as the mappers did it.
- **No mob files.** `Room.Contents` names and types every monster in the room; the only
  configuration the farmer keeps is per-area *exclude* lists, substring-matched.

## The plugins when this is done

| plugin | place | role |
|---|---|---|
| `3s-map-gmcp` (`mapg`) | shipped | world model, walking, events, clickable Maps panel |
| `3s-map-explorer` (`mapx`) | `_lab` | mapg + `explore all`; folds into mapg once proven, then retires |
| `3s-farmer` (`farm`) | `_lab` → shipped | area farming on the contract below |
| `3s-stepper` | shipped | untouched until the farmer replaces it |
| `lab-areabot` | `_lab` | revived by `map.room` as a side effect; retires when the farmer works |

## The contract (phase 1)

Everything crosses between the two as events. Emitted by the mapper:

- `map.room` — on every mapped arrival: `{ num, name, area, exits }` where `exits` is
  direction → destination number as the server gave it. Not emitted while held or in the sea.
  (This is the old marker-era mapper's contract grown back; lab-areabot consumed exactly this.)
- `map.walk.started` `{ target, steps }`, `map.walk.arrived` `{ num }`,
  `map.walk.stopped` `{ reason }` — the walker narrating itself.

Accepted by the mapper:

- `map.goto` `{ num }` or `{ area }` — walk there; `area` means the nearest known room whose
  area matches. Refused (with a `map.walk.stopped`) if unknown or unreachable.
- `map.query.area` `{ area }` → answered as `map.area.rooms` `{ area, rooms }` with exits
  resolved. Added after the first live run: the farmer's own graph only grows while it is
  loaded, but "explored" must mean every room anybody ever stood in — and that record is
  the mapper's. `farm start` asks; the farmer adopts what it does not already know
  (its own fresher rooms always win, and the area fence is applied to the answer too).
- `map.stop` `{}` — same as `mapg stop`.
- `map.hold` — unchanged.

The farmer does NOT use `map.goto` for its in-area patrol (see below); the request side of
the contract exists for long-haul travel and for other plugins.

## The farmer (phases 2–3)

New plugin `3s-farmer`, alias `farm`, developed in `_lab/plugins/3s-farmer/` with its own
harness from day one. State grouped in tables from the start — the 200-local cap is not to be
crept up on twice.

**Its own eyes.** It subscribes to `Room.Info` (building a featherweight area graph of its
own: num → exits, area — the same feed arrives at every plugin for free), `Room.Contents`
(the mobs; remembering that a suppressed payload proves nothing — absence of a Contents is
not an empty room, the `contents_seen` discipline from the chaos-sea bot), and `Char.Combat`
(fight state). Kill timing reads the client-mirrored `enemy.health` / `combat.round` state
from the start — the farmer is new code, it never learns the old `kill_delay` heuristics.

**Its own feet, in-area.** One step at a time, sent by the farmer itself, verified by room
NUMBER on arrival — strictly stronger than anything the chaos sea allowed, since here every
room has an identity. The mapper maps these steps as a bonus (any movement feeds it). Rules:

- `farm start` locks to the area you are standing in. Locking to `Unknown` is refused —
  that label names every stretch of connective realm on the MUD, not a place, and a bot
  "looping" it would wander the world.
- The patrol only ever takes an exit whose destination is a KNOWN room in the locked area.
  Explore first, then farm — that is the workflow, and it is also the fence.
- Next room = nearest in-area room by its own BFS, preferring least-recently-visited, so the
  loop naturally follows respawns around the area.
- Everything that stops a walk stops the patrol: a typed move, a wimpy (pause, position is
  still exact — numbers, not reckoning), a teleport, `farm stop`. Nothing resumes on its own.

**Combat, borrowed not reinvented.** The chaos-sea bot's loop — pending-mob targeting,
kill-blow, corpse handling, Seid rest thresholds, the fighting/paused state machine — ported
with its tests. Differences: the target is *any non-excluded monster in the room* rather than
a configured name; another player's guards arrive typed as monsters (substring `in_party`
handling comes along); combat starting is not a reason to stop (it is the job) but a player
arriving can be, behind a setting.

**Excludes.** `farm exclude <name>` / `farm include <name>` / `farm excludes`, stored per
area, substring match, shown on the panel. Panel: Start/Pause/Stop lit by state, area lock,
kills this run, excludes.

## Travel and clicking (phase 4)

- `map.goto { area }` in the mapper, then `farm go <area>` = travel there, lock, start.
- The Maps panel rows become buttons: click a named map → `mapg go` its seed room. This is
  the "click on a name and it runs there" wish and it lives wholly in the mapper.

## Order of work, each gated by its harness + mutations

0. **Release the current batch** (old 3s-map retired, mapg shipped) once VS build +
   `dotnet test` pass. Nothing below starts on an uncommitted base.
1. Mapper events + goto (small; revives lab-areabot as a free regression check). Re-derive
   the explorer from the updated mapper so the copies never drift apart.
2. Farmer skeleton: graph, patrol, panel, start/stop/lock — no combat yet. Provable in the
   harness alone.
3. Combat port + excludes + rest. The big one. A C# delegation-style test (like
   `ChaosSeaDelegationTests`) loading the farmer from source is worth adding here — that
   pattern has caught real bugs twice.
4. Travel + clickable maps.
5. Live proving, then shipping: farmer beside the stepper; explorer's sweep folds into mapg;
   stepper and areabot retire when Joakim says they have been beaten.

## Known risks, named now

- **Two mapper copies drift** (mapg/mapx) until the fold-in. Mitigation: mapx is always
  re-derived from mapg by the same scripted edits, never hand-patched separately.
- **Area labels lie at the seams** — a room at an area boundary belongs to whichever label
  the server gave it; the known-room fence keeps the bot inside regardless.
- **Wandering mobs** arrive between visits; the farmer re-checks targets on every
  `Room.Contents`, not only on arrival.
- **The event round-trip** for long-haul travel is untested in feel; if it is sluggish in
  play, the farmer's own stepper does more and `map.goto` less. The contract allows either.
