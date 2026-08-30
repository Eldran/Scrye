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
| ~~V8~~ | ~~Skills tab pool pairing~~ | viking-status 2.19.0 | **Answered 30 Aug.** All four paired on the first scan: `visindi=vitka`, `kappi=viga`, `soemd=drotta`, `audr=buandi` — so the gxp tracks and the skill pools ARE the same quantity, and the two pairings that could only be inferred by name (Kappi/Soemd vs viga/drotta) were settled by the game rather than by guess. 65 skills, 29 trainable. The pairing is persisted; the learner stays in for a fresh install and for the day a fifth track appears. |

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
anything V3/V5 turn up. Also **no GMCP source for the skill listing** — no skill
field in any capture and `Core.Supported` says `"Merc.Skills": 0`, so the Skills
tab has to scan `vskills`; a `Guild.Skills` package (name, level, point cost,
daler cost, tree) would retire it, and unlike terrain nothing was promised, so
this is an ask rather than a chase. Also the thank-yous: per-village TradeGoods
and Guild.Warehouse made the text scans retirable, chat channels work.

## Later — wants a decision or the server first

**L1. Raid extras (raid-gmcp).** Grudge-aware targeting (prefer towns that
raided you), a raid log tab mirroring the trade log. Wants V5 confirmed first
so targeting trusts the indexes.

**L2. Guild.Livestock Herds tab (viking-status).** The feed exists in the
captures; a tab is straightforward. Only if you actually run herds — dead UI
is worse than no UI.

**L2b. Skills tab follow-ups (viking-status, 30 Aug).** Two things the merge
left open, both wanting live play rather than code: (a) whether Kappi and Soemd
ever pair at all — the learner needs a scan taken while a track's value equals
the listed pool total exactly, and if a pool never matches, `vsk feed` says so
and `vsk src <pool> <path>` is the manual escape; (b) whether the four gxp
tracks and the four skill pools are even the same quantity. If they are not,
the learner simply never pairs and the scanned totals stand — no wrong answer
gets invented, which is why it was built to match rather than assume.

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
