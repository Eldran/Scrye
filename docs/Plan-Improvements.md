# Plugin improvement plan — 29 Aug 2026

The running to-do across the plugin suite, now that v1.7.0 + the 2.11.0 trader
batch are pushed. Three lanes: **verify** (needs live play, no code until the
world answers), **build** (code ready to write now), **later** (wants a decision
or a server-side change first). Struck items get dated, not deleted — this doc
is the memory.

## Verify live — play normally, note what happens

| # | What | Where | What to watch for |
|---|------|-------|-------------------|
| ~~V1~~ | ~~Trader demand rotation~~ | viking-status 2.11.0 | **Partly answered 30 Aug.** A cart **cannot** come home part sold — so the conservative sell cap was not needed and `dem or cap` stands as it is. That answer also moved the ledger: one payment per cart, at the dock, which is where the log now records a sale (2.21.0). Still worth watching that dispatch notes rotate towns as demand is claimed and no cart goes to a 0-demand town. |
| V2 | Cartyard rhythm | viking-status 2.9.0+ | ~3 min between carts, "Ready in" refusals rare (only after manual dispatches outside the clock). |
| ~~V3~~ | ~~`vresolve` shape~~ | viking-world | **Answered 31 Aug.** A voyage encounter carried `"vresolve": [ "scout", "plunder", "resupply" ]` — a flat array of option words, no objects, exactly what the adapter guessed. **But the words depend on the encounter** (Joakim), and one encounter is all we have seen, so the vocabulary is open-ended — `scout` was already absent from the `vnav resolve` default. 1.4.4 stops pretending otherwise: a node offering nothing in your list names what it WAS offered and holds, once per node, instead of stalling silently. Ending the list with `*` takes the first offer. |
| V4 | CRAID end behaviour | viking-status 2.6.0 | The raid alarm ages out on a 10-min TTL **guess**. Does the feed say anything when a raid ends? If yes, clear on that instead. |
| V5 | heat ↔ town index pairing | raid-gmcp | Sanity-check a raid target's heat number lines up with the town the UI names. |
| V6 | MIP+GMCP chat dedupe | host (MudSession) | With both feeds live: chat panes show every line exactly once, plugin onChannel hooks fire once. |
| V7 | Shifting exits in anger | map-gmcp 1.6.0 | Ride a few more elevators/ferries; auto-mark should catch them after two changed arrivals, `~` on the map. |
| ~~V8~~ | ~~Skills tab pool pairing~~ | viking-status 2.19.0 | **Answered 30 Aug.** All four paired on the first scan: `visindi=vitka`, `kappi=viga`, `soemd=drotta`, `audr=buandi` — so the gxp tracks and the skill pools ARE the same quantity, and the two pairings that could only be inferred by name (Kappi/Soemd vs viga/drotta) were settled by the game rather than by guess. 65 skills, 29 trainable. The pairing is persisted; the learner stays in for a fresh install and for the day a fifth track appears. |
| V9 | Voyage encounter vocabulary | viking-world 1.4.4 | Sail into encounters and note the `[sea-nav] node offers …` lines. Each names an option set never seen before; those words are what a sensible default `vnav resolve` list should eventually contain. |

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
anything V5 turns up. Guild.Map terrain is now a **four-session** gap (27, 28, 29,
31 Aug): every one of them sent only the east/south edge grids, `pos` and `w`,
while `enc` has promised `{terrain: "glyph"}` the whole time — worth saying
plainly that the declaration has outlived four captures. Also **no GMCP source for the skill listing** — no skill
field in any of the four captures, so the Skills tab has to scan `vskills`; a `Guild.Skills` package (name, level, point cost,
daler cost, tree) would retire it, and unlike terrain nothing was promised, so
this is an ask rather than a chase. Also the thank-yous: per-village TradeGoods
and Guild.Warehouse made the text scans retirable, chat channels work.

**B5. Subscribe to `Merc` (31 Aug).** Scrye asks for
`["Char 1", "Room 1", "Comm 1", "Guild 1"]` and nothing else, so the packages
reading 0 in every capture are simply the ones never requested — their 0 says
nothing about whether they work (Plan-Viking-GMCP §2b).

Mercenaries are **hireable NPCs, the same for every player whatever their guild**,
so nothing gates them: if `Merc.Talents` / `Vitals` / `Info` / `Skills` / `Stats`
carry anything, Goran will see them the moment we ask. Add `"Merc 1"` to
`GmcpPackages` in `MudSession.cs`, reconnect, and `.gmcp` answers it. This moved
from Later to Build because the only thing that made it a question — "maybe you
have to be one" — turned out not to exist.

Mercs and the Viking **hird** are separate systems (Joakim, 31 Aug): the hird is
guild-only, comes from `Guild.Roster`, and is already drawn on the Kingdom
plugin's Hird and Recruit tabs. Mercs share nothing with it. So a Merc feed is
new surface rather than a second view of something already covered — and being
guild-independent, it would not belong in the Viking suite at all. Its home would
be its own plugin, the way 3s-vitals serves any guild.

`Craft.*` stays unrequested on purpose: the crafting system is not enabled in
game yet and only the field names exist, so its zeros are honest.

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

**L2c. Guild.Market — a live feed nothing reads (31 Aug).** The package had been a
one-message empty envelope (`market_shown: 0, market_total: 0`) in every capture
through 29 Aug. On 31 Aug it pushed **79 messages**, every ~20 s, carrying a real
row: `market_0: [{ id: 53, good: "cloth", price: 60, remain: 600, buyer: 0,
age: 60096 }]`. The `_0` suffix is almost certainly a **lineage id**, the same
convention `lmarket_<lin>` uses in Guild.Livestock. No plugin subscribes to it.

Blocked on one question only a person in the game can answer: **is that board
everyone's listings or only your own?** The capture shows exactly one row —
Goran's own cloth, unsold, sixteen hours up — which is equally consistent with
both. Public and liquid, it is a price source the auto-trader could act on;
your own listings only, it is a small read-only tab at most. Ask before building
either.

**L3. Voyage extras (viking-sea).** The 17:25 capture's leftovers, deferred
while the trade work landed. Re-mine `gmcp-fields-Goran-20260828-*.md` once
V3 settles the encounter shapes.

**L4. Map tab terrain (viking-sea).** Blocked on the server (B4's headline
item). The tab already waits honestly; nothing to do until the devs ship it.

## Done this session — worth a second look live

**Paged packages no longer prune (host, 31 Aug).** `StateStore.SetJson` removed every
key a payload did not carry, on the documented assumption that "a package resend
replaces the whole object". True of Char.Vitals / Char.Combat / Room.Info; false of
every paged `Guild.*`, where each page deleted the one before it. That is why a
Viking's Seid/Vig/Rad gauges blinked to zero while HP held steady — `guild.state.points.*`
only existed in the gap between page 3 landing and the next page 1.

A package seen to arrive with a `pages` field is now latched and never pruned again.
Pruning is untouched for the never-paged packages, including the empty Char.Combat
snapshot it exists for. The latch is sticky because Guild.State also sends unpaged
partial payloads that would wipe just as surely.

**Cost, and who it lands on:** a paged package's tree never forgets. A leaf the server
stops sending keeps its last value, and a shrinking list leaves its tail behind (three
carts becoming one leaves `guild.trade.carts.2.*` in place). Nothing reads those paths
today — the Viking plugins assemble bursts themselves from the raw JSON — but a future
plugin that binds `guild.*` list indexes needs to know, and `ClearPrefix` is the escape.

**The guild-switch corner (31 Aug).** A character can hold several guilds and switch
which one is active (Joakim) — common on 3K, rare but possible. Since a paged package is
never pruned, the previous guild's keys survive the switch: swap away from Viking and
`guild.state.points.viga` sits there for good. 3s-vitals is unaffected — `apply()` runs
every prompt, picks its bar set from `guild.state.guild`, and rebuilds when that changes,
so it reads the new guild's paths and never looks at the old ones. What is left is ghost
keys in the state inspector and a trap for some future plugin.

The fix, when it is wanted: notice `guild.state.guild` changing VALUE and `ClearPrefix("guild")`
before the new payload is applied — it has to be before, or it wipes the message that
just arrived. Deliberately not written yet: the change above is already uncompiled, and
stacking a second untested C# change on an untested one is how a small fix becomes a bad
afternoon. Build and prove that one first.

**Not compiled or tested here** — no dotnet in the sandbox. Three tests are written in
`GmcpTests.cs` (a burst keeps every page, an unpaged partial leaves the rest alone, an
unpaged package still prunes). Build and run before trusting it.

## Standing hygiene

Harnesses stay green before every commit (vikingstatus 181, mapg 325, mapx 280,
chaossea 166, farmer 149, raid 38); every behaviour change mutation-tested;
VERIFY LIVE markers on anything the harness cannot prove; `_lab/` never enters
the repo; NilleScrye stays read-only reference.
