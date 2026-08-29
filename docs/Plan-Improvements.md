# Plugin improvement plan — 29 Aug 2026

The running to-do across the plugin suite, now that v1.7.0 + the 2.11.0 trader
batch are pushed. Three lanes: **verify** (needs live play, no code until the
world answers), **build** (code ready to write now), **later** (wants a decision
or a server-side change first). Struck items get dated, not deleted — this doc
is the memory.

## Verify live — play normally, note what happens

| # | What | Where | What to watch for |
|---|------|-------|-------------------|
| V1 | Trader demand rotation | viking-status 2.11.0 | Dispatch notes rotate towns as demand is claimed; no carts into export/0-demand towns. A cart coming home **partly unsold** = the town bought less than dem promised → cap sell qty more conservatively. |
| V2 | Cartyard rhythm | viking-status 2.9.0+ | ~3 min between carts, "Ready in" refusals rare (only after manual dispatches outside the clock). |
| V3 | `vresolve` shape | viking-sea | Needs a voyage **encounter**. The adapter guesses from field names; first real payload confirms or corrects. |
| V4 | CRAID end behaviour | viking-status 2.6.0 | The raid alarm ages out on a 10-min TTL **guess**. Does the feed say anything when a raid ends? If yes, clear on that instead. |
| V5 | heat ↔ town index pairing | raid-gmcp | Sanity-check a raid target's heat number lines up with the town the UI names. |
| V6 | MIP+GMCP chat dedupe | host (MudSession) | With both feeds live: chat panes show every line exactly once, plugin onChannel hooks fire once. |
| V7 | Shifting exits in anger | map-gmcp 1.6.0 | Ride a few more elevators/ferries; auto-mark should catch them after two changed arrivals, `~` on the map. |

## Build next — code ready to write, no blockers

**B1. Trader polish (viking-status).** Whatever V1 turns up, plus small
sharp edges already known: show a town's *claimed* (debited) demand on the
Trade tab so the UI matches what the trader believes; a `atrade log` profit
summary per day off the existing Trade Log entries (bought X, sold Y, net Z);
consider demand-aware sell qty (send `min(cart, dem)` but hold the cart until
a town wants a **full** cart when warehouse pressure is low — fewer, fuller
carts).

**B2. Fold mapx `explore all` into mapg, retire mapx.** The explorer has
280 harness checks and has proven itself; it exists as a separate _lab plugin
only because it was risky once. Port `explore all` + the frontier logic into
3s-map-gmcp behind the same command, re-run both harnesses, delete the derive
script and _lab plugin. One less moving part, and 3s-farmer then leans on one
mapper, not two.

**B3. The 3s-farmer verdict.** It sits in _lab waiting on your live verdict.
If it earns promotion: move to src plugins, version 1.0.0, plugin.json
description pass, keep the harness in _lab. If not: what failed becomes its
own line here.

**B4. Dev report to the 3Scapes devs.** Short, concrete, from the captures:
Guild.Map terrain promised (`enc={terrain:"glyph"}`) but never delivered — the
big one, it blocks the sea Map tab; CRAID raid-end signal (V4) if none exists;
anything V3/V5 turn up. Also the thank-yous: per-village TradeGoods and
Guild.Warehouse made the text scans retirable, chat channels work.

## Later — wants a decision or the server first

**L1. Raid extras (raid-gmcp).** Grudge-aware targeting (prefer towns that
raided you), a raid log tab mirroring the trade log. Wants V5 confirmed first
so targeting trusts the indexes.

**L2. Guild.Livestock Herds tab (viking-status).** The feed exists in the
captures; a tab is straightforward. Only if you actually run herds — dead UI
is worse than no UI.

**L3. Voyage extras (viking-sea).** The 17:25 capture's leftovers, deferred
while the trade work landed. Re-mine `gmcp-fields-Goran-20260828-*.md` once
V3 settles the encounter shapes.

**L4. Map tab terrain (viking-sea).** Blocked on the server (B4's headline
item). The tab already waits honestly; nothing to do until the devs ship it.

## Standing hygiene

Harnesses stay green before every commit (vikingstatus 181, mapg 325, mapx 280,
chaossea 166, farmer 149, raid 38); every behaviour change mutation-tested;
VERIFY LIVE markers on anything the harness cannot prove; `_lab/` never enters
the repo; NilleScrye stays read-only reference.
