# 3s-map — Automapper for 3Scapes (Design & Plan)

Status: **M1–M4 + M6 shipped**, plus the weave-grid rendering upgrade (API 1.7)
(`src/Scrye.App/plugins/3s-map`, acceptance tests in
`tests/Scrye.Core.Tests/MapPluginTests.cs`; on-device look/companion checks are manual) ·
Target: plugin-only, `scrye` API **>=1.7 <2.0** · Language: Lua (MoonSharp) · Next: M5 (ship
it — MoonSharp hygiene pass, cost review, a user-docs section in Scrye-Guide.md, and starter
maps.json entries: `pinnacle` plus the three hub boundary rooms would give every user the
top-level world for free) · Later: M7 (cross-area goto — route through the link graph
between areas; deliberately out until the boundaries have soaked)

Implementation notes vs. this design, M4: the drift check *preserves* the recorded room on a
mismatch (the design left it open) — the user resolves via `map set`/`map undo` or a matching
re-arrival clears it; `map link <cmd>` arms a next-arrival bind that resolves by unique room
name or parks a new cell, `map link <cmd> = [area] x y z` binds explicitly; cross-area links
switch areas when used but BFS never paths through them; and `map undo` covers the last ~20
confirmed compass moves.

> **Revised for plugin API 1.6.** The 1.6 batch (`scrye.onCommand`, `scrye.json`,
> `scrye.store.setMany`, `scrye.emit`/`scrye.on`, colorgrid `onHover`, 250 ms timers) was built
> *for* this plugin, and this revision deletes every workaround the 1.5 draft carried:
> movement-capture aliases are gone, hand-rolled line serialization is gone, and the
> "another bot is walking" blind spot is closed.

A general-purpose automapper for 3Scapes, shipped as an ordinary Scrye plugin: it learns rooms as
you walk, draws the area around you in a HUD panel, and walks you to any mapped room on request.
No changes to Scrye itself beyond the (already-shipped) API 1.6 batch, and the two mapping
plugins already in the repo (3s-chaossea's grid mapper, 3s-stepper's recorder) are the
precedents for every hard part.

---

## 1. What we can actually observe (and what we can't)

3Scapes is an LPMud speaking **MIP**, not GMCP. There is no `Room.Info` packet, no room ids, no
coordinates from the server. What the game gives us, with the same MUD-side settings the stepper
and chaos-sea plugins already require, is:

| Signal | Form | Used for |
|---|---|---|
| Room marker | `=S=<short desc>=S=` line on every room entry | "we arrived somewhere" + the room's name |
| Exits | `( n, s, e, w )` style parenthetical inside the short | the room's exit set |
| Failed move | `You cannot go <dir>.` / wall messages | rolling back a predicted move |
| Mobs / players | `=M=` / `=P=` markers | room annotations (optional, later) |
| Prompt | `scrye.onPrompt` | pacing the speedwalker |

Because there are no room ids, **room identity has to be synthesized**. The mapper uses
**dead reckoning on a 3D grid** — exactly what 3s-chaossea does for the maze, generalized:

- The plugin knows every movement command *you* type (see §3) and the direction deltas
  (n/s/e/w/ne/nw/se/sw/u/d — the chaossea `DELTA` table, plus long forms).
- Position starts at `(0,0,0)` in an **area**; each confirmed room arrival (`=S=`) advances the
  position by the pending direction's delta. A failed-move line cancels the pending direction.
- A room *is* its grid cell: `rooms[z][x][y] = { name, exits, flags, note }`.

Dead reckoning is honest about its limits, and the design leans into them instead of pretending:

- **Non-Euclidean areas** (wrapping corridors, `e` that doesn't come back with `w`) will produce
  overlapping cells. Mitigation: per-area maps (a small area rarely lies), a **drift check** (on
  arrival, if the recorded room name at the predicted cell differs from the `=S=` name, warn and
  offer `map set` / `map undo`), and **special links** (§4) for anything that isn't a plain
  compass move.
- **Teleports, `enter <thing>`, portals** are never inferred; they are recorded only as explicit
  special links.
- **Identical adjacent rooms** (mazes) are fine — identity is the cell, not the name. That is the
  reason to prefer dead reckoning over name-fingerprinting for this MUD.

## 2. Scope and non-goals

In scope: learning rooms while walking, a live HUD map, room notes/flags, pathfinding + speedwalk
to any mapped cell (including click-to-walk), per-area persistent maps, export/import.

Out of scope (deliberately): the viking overland map (already fed by MIP `VMAPH` and a different
shape of problem), the chaos sea (has its own plugin, and the maze resets), auto-*exploration*
(walking on its own into unmapped exits — chaossea does this for one maze; a general "explore the
MUD" bot is a liability, and the idle guard exists precisely because of bots like that). If wanted
later, an explore mode is a small addition on top of the frontier data the mapper keeps anyway.

## 3. Movement capture

One hook: **`scrye.onCommand`** (API 1.6). It fires for *every* command that goes to the MUD —
typed input after alias processing, macro keys, sequences, trigger sends, and **other plugins'**
`scrye.send` — observe-only, on the loop thread, before the bytes leave. The mapper filters for
the ~23 movement words (plus recorded special-link commands) and pushes each onto a **FIFO of
pending directions**; the next `=S=` consumes one and advances the position; `You cannot go …` /
wall messages flush the queue. That is the entire capture story.

What this buys over the 1.5 draft's pass-through-alias workaround:

- **No alias registration at all** — nothing sits between the user's keystrokes and the MUD, so
  there is nothing the mapper can break at the worst moment.
- **Bot moves are visible.** When the stepper or chaos-sea plugin drives the character, their
  `scrye.send` moves land in the same hook, so the map keeps up instead of silently drifting.
  The drift check (§4) remains as the backstop for teleports and anything non-command.
- **One discipline for all sources.** The mapper's own speedwalk needs no special-casing — its
  sends come back through `onCommand` like everyone else's.

The one rule `onCommand` imposes: the handler must never `scrye.send` (the guide is explicit —
that re-fires every hook, including yours). The mapper's handler only classifies the command and
queues; all sending happens in the speedwalker, driven by arrivals and prompts.

## 4. The map model

```
area = {
  name  = "sybarus",
  rooms = { [z] = { [x] = { [y] = {
              name  = "Village square",     -- from =S=, for drift check + search
              exits = { n=true, e=true },   -- compass exits seen in the parens
              links = { ["enter well"] = {x=…,y=…,z=…} },  -- special links (optional)
              flags = "S",                  -- one-char flags: Shop, Trainer, …
              note  = "free heals",         -- user note (optional)
            } } } },
  start = { x=0, y=0, z=0 },                -- where (0,0,0) is, textually ("login room")
}
```

- **Areas** partition the world. `map area <name>` switches (creating if new) and resets position
  to the area's remembered last cell; crossing between areas is a special link. Per-area maps keep
  dead reckoning short-leashed and keep any one stored blob small.
- **World structure (M6).** 3Scapes is Pinnacle → three hubs (chaos / fantasy / science) → areas,
  and the split between maps goes exactly where the geometry breaks: at area entrances. So:
  `pinnacle` is one map, each hub overworld is one map (one consistent geometry, however big),
  and every sub-area is its own map, named with a hub prefix (`fantasy-elvenwood`) so
  `map areas` stays readable. Boundaries are links — and since most MUD area entrances are plain
  compass exits, a link may sit on a compass direction: `onCommand` checks the current room's
  links *before* compass dead reckoning, so walking `n` through a recorded boundary switches
  maps instead of reckoning a phantom room. A compass crossing records its own return link on
  first use (`n` out ⇒ `s` back — knowable for compass, never guessed for portals), and
  `map enter <area> [x y z]` arms the next command you send as the boundary into `<area>`
  (created if new), bound both ways when it was a compass move. Record each boundary once and
  the whole world tracks itself. Linked compass exits draw their connector stub but no frontier
  `?` — the other side lives on another map, it is not unexplored.
- **Special links** are recorded manually: `map link enter well` records "from here, `enter well`
  lands at the next room I arrive in" — the next `=S=` closes the link. Works for portals, area
  transitions, one-way drops.
- **Frontier** (exits leading to unmapped cells) is derived, not stored — recomputed per draw for
  the visible level, as chaossea does.

## 5. Rendering — the HUD panel

A `colorgrid` is the map surface (chaossea precedent: a 20×16 viewport, centered on the player,
north = up). API 1.3's `labels` lets flagged rooms draw their flag character on the tile, and
`onClick` gives click-to-walk.

Panel sketch (tabs):

- **Map** tab
  - `label` — status line: area, position, mode (`MAPPING` / `WALKING 3/7` / `OFF` / `DRIFT?`).
  - `colorgrid` 21×15 rooms on a **woven grid** (`weave = true`, API 1.7): the bound string is
    41×29 — rooms on even cells, and each pair of adjacent rooms shares an odd cell where their
    exit draws as a thin connector line (`-` `|` `/` `\`, `x` for crossing diagonals, token
    `line`). Palette via **theme tokens**: current room `accent`, mapped tile `dim`, frontier
    `warning`, flagged rooms lettered via `labels` (`info`), goto-target `success`. A room with
    an up/down exit is marked `^`/`v`/`%` on its tile (vertical exits have no between-cell to
    draw on), a **boundary room** — one with a cross-area link, like the hub gates out of
    Pinnacle — is marked `>` (`info`; a user flag outranks it, so flagging the gates C/F/S
    still works), and every unmapped room position carries a faint `.` grid dot (`inset`) so
    the viewport reads as graph paper. Clicking a mapped cell starts a goto; edges and dots peek.
    **`onHover` (1.6)** drives a peek line: pointer over a room cell shows that room's
    name/note/exits in the `value` widget below; edge cells and the `(-1,-1,"")` leave signal
    restore the current room's line. Hover is desktop-only enrichment — on the companion the
    same information is one tap away (click shows the peek for an unmapped/`?` cell instead of
    walking), and the companion renders the same woven characters as an ASCII map.
  - `buttonrow` — `Up` / `Down` (view z±1), `Center`, `Stop` (abort walk).
  - `value` — the peek/note line (hover target, also set on arrival for the current room).
- **Rooms** tab
  - `input` — search; `table` (`Room  Pos  Note`, align `llr`) bound to the last search / flagged
    rooms list. Search results are numbered; `map go <n>` walks to one.

All dynamic content flows through `plugin.3s-map.*` state paths; the panel itself is built once.
Colors are tokens, not literals, so all six themes and the companion render correctly — and the
companion's Panels tab gives a **tappable map on the phone** for free.

Draw cost: rebuilding a 21×15 grid string on every room is trivial (chaossea does it plus BFS
today, under the 50 ms budget). Draws are also prompt-coalesced: one draw per arrival, not per
line.

## 6. Pathfinding and the speedwalker

- **BFS** over the area's rooms (chaossea's `find_path`, generalized to include special links).
  BFS at area scale (hundreds of rooms) is well under budget; if an area grows huge, bound the
  search radius and say "too far" rather than blow the callback budget.
- **Confirmation-driven walking**, not fire-and-forget: send one direction, wait for the `=S=`
  (or a special link's arrival), send the next. A failed move, a `DRIFT?` mismatch, combat
  (`enemy.name` non-empty — the MIP truth chaossea uses), or `map stop` aborts cleanly. A
  watchdog (`scrye.after(10, …)`) aborts a walk whose confirmation never came, and 1.6's 250 ms
  timers let the inter-step pacing delay be `0.25`–`0.5` s instead of a mandatory whole second —
  a 20-room walk stops feeling like a slideshow.
- **`scrye.onIdle`** stops any active walk and does not auto-resume — same contract as
  the stepper and chaossea.

## 7. Commands (aliases)

| Command | Does |
|---|---|
| `map` | status + help |
| `map on` / `off` | capture on/off (`onCommand` observation simply pauses) |
| `map area <name>` | switch/create area |
| `map goto <x> <y> [z]` · `map go <n>` | walk to a cell / a search result |
| `map stop` | abort the walk |
| `map find <text>` | search names+notes into the Rooms tab |
| `map note <text>` / `map flag <S\|T\|…>` | annotate current room |
| `map link <command>` | record a special link (closed by next arrival) |
| `map set <x> <y> [z]` / `map undo` | fix drift: re-seat position / remove last learned room |
| `map wipe <area>` | delete an area (confirm) |
| `map export [area]` | print the area as a paste-able JSON block |

## 8. Persistence

`scrye.store`, which is already per-world and crash-safe (atomic replace). With 1.6 the format
and the write pattern both simplify:

- **`scrye.json.encode`/`decode`** serialize each area — the same JSON shape as `maps.json`, so
  store values, seed data, and `map export` output are one format, round-trippable by
  definition. The chaossea-style `z|x|y|…` line format is no longer needed (it survives only as
  the *import* path for anything old).
- **One key per area** (`map:<name>`), plus an `areas` index key and per-area `pos:<name>`.
  Saves go through **`scrye.store.setMany`** — the dirty areas, the index, and the position in
  one call, one disk write. A light debounce (dirty-flag, flush after ~3 s of quiet, plus flush
  on `onDisconnect`/`onIdle`) stays, but it now guards against *frequency*, not against the
  per-key rewrite cost that made chaossea invent it.
- **Seeding**: the manifest's `data` map ships starter maps (`"data": { "maps": "maps.json" }`),
  merged read-only under any areas the user hasn't mapped themselves. `map export` prints the
  same JSON, ready to paste into `maps.json`.

## 9. Manifest

```json
{
  "id": "3s-map",
  "name": "3S Map",
  "version": "0.1.0",
  "author": "Joakim",
  "description": "Automapper: learns rooms as you walk, draws a HUD map, and walks you back to anything it knows.",
  "mudIds": ["*"],
  "entry": "main.lua",
  "lang": "lua",
  "data": { "maps": "maps.json" },
  "enabled": true,
  "requires": { "scryeApi": ">=1.6 <2.0" },
  "permissions": [
    "output.read", "commands.send", "aliases.manage", "triggers.manage",
    "timers.manage", "state.write", "storage.private", "ui.panels"
  ]
}
```

(`>=1.6` because the design leans on `onCommand`, `scrye.json`, `setMany`, events, and hover —
on an older client the plugin refuses to load with a clear message instead of half-working.
`aliases.manage` stays declared: movement capture no longer uses aliases, but the `map …`
command surface still does.)

## 10. Milestones

**M1 — Walk-and-map core.** Manifest + skeleton; `onCommand` movement classifier with the
pending FIFO; `=S=` room trigger + exit parsing; failed-move rollback; grid model;
`map on/off/area`; `scrye.json` + `setMany` persistence with the debounce. *Done when: walking a
small area by hand produces a correct, persisted room set (verified via `map export`) — and the
same walk driven by the stepper maps identically.*

**M2 — See it.** The panel: colorgrid viewport with palette/labels, `onHover` peek line, status
line, level buttons, notes/flags/find, Rooms tab. *Done when: the map draws live while walking,
on desktop and companion, in at least two themes, and hovering a mapped cell shows its room.*

**M3 — Walk it.** BFS + confirmation-driven speedwalk; click-to-walk; `map goto/go/stop`; combat
pause; watchdog; `onIdle`. *Done when: click a mapped room three z-levels away and arrive, and a
blocked door aborts the walk with a clear message.*

**M4 — Trust it.** Drift check + `DRIFT?` state; `map set/undo`; special links (`map link`,
BFS through them); area-crossing links; seeded `maps.json` merge + export round-trip. Publish
the mapper's events (`scrye.emit`): `map.room` on each arrival and `map.walk.started/stopped` —
the stepper and chaos-sea can listen (`scrye.on`) to know where they are without parsing
anything, and future plugins get a position feed for free.

**M5 — Ship it.** MoonSharp hygiene pass (initialized locals, `:find` gates before patterns —
the guide's gotchas); slow-callback and quarantine review against the 50 ms budget; user docs
section in `Scrye-Guide.md`; a starter `maps.json` for one or two real areas.

**M6 — Stitch the world** *(shipped)*. Compass-direction links (`map link n = fantasy 0 0 0`,
compass words stored canonical so `north` and `n` are one link); `onCommand` link-before-compass
precedence; automatic return links for compass crossings (recorded on first use, never
clobbering an existing link); `map enter <area> [x y z]` / `map enter -`; `map back <cmd>` —
after any cross-area jump, binds `<cmd>` in the arrival room to the room the jump came from
(the no-coordinates way to record a portal boundary's return; the arrival note suggests it
when the crossing wasn't compass); frontier `?` suppressed on linked directions; cross-area
crossings stop an active walk. *Done when: walking
`n` out of a gate with a recorded boundary switches areas and tracks you, walking `s` brings
you back without recording anything by hand, and `map enter` creates and binds a brand-new
area both ways in one crossing.*

## 11. Risks, called out

- **Dead reckoning drift** in non-Euclidean areas is the big one — mitigated (per-area maps,
  drift check, manual fixes, special links), not solved. That is inherent to a MUD with no room
  ids; every 3K-family mapper lives with it.
- **`=S=` markers must be enabled MUD-side** (same prerequisite the stepper/chaossea already
  document). If they're off, the mapper visibly does nothing — the status line should detect
  "capture on but no `=S=` seen" and say so.
- **Non-command movement** — teleports, summons, being dragged — still bypasses `onCommand`
  (nothing was sent). The drift check and `map set` remain the answer; this is inherent, not a
  workaround debt. (Bot-driven movement, the 1.5 draft's big blind spot, is now captured.)
- **Store growth**: hundreds of areas would make even sharded saves heavy. Fine for realistic
  use; `map export` + wipe is the pressure valve.
