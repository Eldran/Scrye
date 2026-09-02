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

**B4. Dev report to the 3Scapes devs.** ~~To write~~ — **written 1 Sep**,
`docs/Dev-Report-3Scapes-2026-09-01.md`, for Joakim to send. Leads with the
thank-yous (TradeGoods and Warehouse each retired a text-scan burst; MIP+GMCP
chat coexists), then the one blocking gap — Guild.Map declaring
`enc={terrain:"glyph"}` and never sending terrain, across 96 Guild.Map messages
in four sessions. Two confirmations asked: whether `Guild.City.heat[]` is
index-aligned with `Guild.Fleet.rtargets_lineage[]` (both 13 entries, and the
auto-raid targeting assumes it), and whether anything signals an incoming raid
ENDING (the alarm currently ages out on a ten-minute guess). Two asks: a package
for the skill listing, and whether `"Merc 1"` is a real subscription with data
behind it. Closes by flagging Guild.Market waking up on 31 Aug in case that is
news to them. Craft left out — not live yet.

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

**~~V10~~. Cyborg — the four unclear figures, ANSWERED 1 Sep (3s-cyborg 1.1.0).**
Shipped as raw labelled numbers in 1.0.0 rather than as guessed bars; Joakim
answered all four and the panel now shows each as what it is:

| Field | What it means | How it shows now |
|---|---|---|
| `stored_power` | a **reserve** pool an implant grants, separate from power/power_max | both shown — "Reserve 7,600 stored" beside the power bar |
| `si_pct` | hundredths of a percent toward the **next** SI level | `107 -> 108   54.78%` (5478/100; the two decimals are the precision the feed sends) |
| `control_used` / `control_avail` | what active implants draw, and what is left to activate more with | `10,630/10,754  99%` with "124 free" — the sum is the capacity, since the feed sends no total |
| `ammo` / `ammo_rounds` | the **magazine** and the **case** that refills it at 0 | two stores side by side, never a ratio |

That the last one was worth getting right: dividing magazine by case would have
read "41% of your ammo left" when the true figure is the two added together.

Still unconfirmed: whether `stored_power` has a ceiling of its own, and whether
`power_change` is a delta or a target (it read 4,912, equal to `power_max`, in
the one full burst).

**V11. Gentech — the unnamed fields (3s-gentech 1.0.0 → 1.1.0).** The capture
came from another player's character, so unlike the Cyborg's unknowns these
could not be settled by asking Joakim. Seven were shipped on the Status tab
under the server's own field names, raw, in a "Not yet understood" block — the
point being that a Gentech player could read them straight off the panel and say
what they were. **On 1 Sep that is what happened, and five came back answered:**

| Field | Value seen | Answer, now acted on (1.1.0) |
|---|---|---|
| `g2n` / `g2n_pct` | 853,877 and 14% | gexp still owed before the next glevel, and the same as a percent. Guild levels cap at 50 and become **echelons** after. Now a `Guild exp` bar on Status |
| `reset_pct` | 16–64% across the session | the **timeslide** refill clock: at 100% you get a fresh set. Now a `refill` line under Timeslides |
| `phase_rank` | 865 | how far the experiments have been trained/phased. Now a labelled row on Progress |
| `rush` | 1 | a **healing power**. Now a Systems row (its switch is the one system flag the server keeps on `Guild.State`, hence the on-source field on `SYSROWS`) |

The `g2n` reading brought a bonus the answer did not have to give: `gexp` 146,123
plus `g2n` 853,877 is a round **1,000,000**, and 146,123 of that floors to
exactly the **14%** the feed sent as `g2n_pct`. Two independent numbers agreeing
on a threshold the server never sends, so the panel derives it and prints the
feed's percent beside the bar as a live check, with a test pinning the identity.

Still unnamed, still in the block:

| Field | Value seen | Guess, deliberately not acted on |
|---|---|---|
| `dgexp` | 0 | a daily gexp figure? |
| `illuminated` | 0 | a flag, meaning unknown |
| `*_cs` on Stats | all 0 in the capture | the feed's own suffix on exp/gexp/rc rates — "current session"? |

**A label 1.0.0 got wrong, corrected in 1.1.0.** `echelon_gexp` 1,962 against
`echelon_required` 3,000 was shipped as "progress" *within* the current echelon,
on the strength of it matching the feed's own `echelon_pct` exactly. The
arithmetic was right and the label was not: it is echelons **held** against
echelons **needed for the next Order**. The lesson is one this file keeps
relearning — a cross-check that two numbers agree says nothing about what either
of them counts.

**The split, settled.** Earned gxp divides between two destinations, and the
player sets the ratio in game and can change it at any time (`gexp_split`, 100 in
the capture):

| `gexp_split` | Destination | Fields | What it does |
|---|---|---|---|
| 100 | research credits | `res_creds` | spent to phase experiments, raising `phase_rank` |
| 0 | echelons | `echelon_gexp` / `echelon_required` / `echelon_pct` | accumulates toward the next Order |

The first draft of 1.1.0 had this as "a gexp pool vs echelons", reading the
`gexp` field as the spendable side. It is not — research credits are. `gexp` /
`g2n` is its own counter and keeps a neutral label. The panel now spells out both
halves of the split (`100% to research credits · 0% to echelons`) with the
remainder computed rather than echoed, so the line cannot be read as pointing at
whichever number happens to sit nearest it, and it sits directly under
`Res credits` — one of the two destinations it routes between. It also moved out
of the Progress tab's bonus factors, which are passive multipliers; this is a
live control.

**Three wrong labels on this cluster across two versions** — "echelon progress",
"gexp pool", "pool vs echelons" — every one of them from a number that verified
cleanly. The pattern is worth naming: arithmetic that cross-checks confirms a
*relationship*, never a *meaning*. On a guild nobody at hand plays, a field's
meaning has exactly one source, and it is a player.

**Still open (VERIFY LIVE).** How `echelon` 25, with its title and insignia,
relates to the 1,962 echelons held — 25 is plainly not a count of the same thing.
Whether `gexp` / `g2n` is itself fed by the split or counts total earned gxp
regardless of routing is also untested; if the split is moved off 100 and the
`gexp` bar stops advancing, that answers it.

**Char.Vitals differs by guild — now two guilds to one.** A cyborg's and a
gentech's both carry `guild`, `qp` and `qp_required`; a viking's carries none of
the three, across four captures spanning 28 Aug to 31 Aug and two non-viking
captures on 1 Sep. Two guilds having them and one not makes guild-conditional the
likelier reading over "recently added", though a viking capture taken after 1 Sep
would settle it outright. Either way, do NOT rely on them: 3s-vitals reads
`guild.state.guild` for identity, which every guild sends on every page.

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
