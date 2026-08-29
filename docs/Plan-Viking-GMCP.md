# Plan: Viking plugins over GMCP

Status: **approved 2026-08-27; core builds done, in live soak.** Companion to `docs/Plan-Map-Farmer.md`.

Built so far (all with lab harnesses + mutation passes, all uncommitted):
3s-raid-gmcp 2.0.0 - 3s-viking-fx 1.0.0 (effect timer bar) - 3s-viking-status-gmcp
2.6.0 (the land tabs; classics frozen) - 3s-viking-sea 1.0.0 - 3s-viking-kingdom
1.0.0 - plus the `vtrade stock` scan (status 2.1.0) closing the wstock gap as a
stopgap, and Honey added to the tradeable goods (a raw material).

Live soak + the 28 Aug capture (archived as
`_lab/captures/gmcp-fields-Goran-20260828.md`): sea chart CONFIRMED (rows carry a
new 'M' landmass tile, now charted); cidle CONFIRMED with its real shape
{slot,tier,cap,...}; **Guild.Trade.routes is NEW** - the town-routes gap closed,
Production's Routes section renders it; vboons is a counter object, vmem carries
crew memories (both on the Voyage tab now); ship/crew traits are plain string
arrays; Comm gained an `afar` flag (soul emotes). Guild.Map terrain is a
CONFIRMED GAP (two sessions, edge grids + pos only). The kingdom plugin's tabs
all populate (War empty = no war, as designed).
**28 Aug PM server patch** (capture `_lab/captures/gmcp-fields-Goran-20260828-1725.md`):
three NEW packages. **Guild.Warehouse** - per-good stock with grade + REAL freshness
pct + wstock_cap: THE wstock gap is closed, wired in status **2.6.0** (the feed owns
WSTOCK from its first burst; the `vtrade stock` scan, cart-event and staleness
re-scans remain only as the fallback and are gated off by ws_feed_live; the
Production tab now shows real pcts on under-100 grades). **Guild.TradeGoods** -
per-village market prices (lin 0-13 = 14 villages; buy/sell/sup/dem/score), but
goods are 30 short CODES (t,o,i,f...mi,hm,sm,cs) with no self-evident mapping:
UNWIRED until a `vtrade market`/`vtrade goods` paste taken alongside the feed nails
code->good (guessing would poison the auto-trader). **Guild.Market** - answers now
(market_shown/market_total 0; the old Guild.Trade.market[] is presumably dead).
Also new in old packages, wired in 2.6.0: **Guild.City raid{faction,strength,secs}**
-> RAID alarm on Stats' War section (10-min shelf life, since raid rides the
unpaged stream and would otherwise never clear - VERIFY LIVE how a raid actually
ends) and **bdmg[]** -> "Damaged:" line on Builds. Not yet wired: Guild.Voyage
vgoods (ship cargo) + vaids notes; carts' half_in; Guild.Livestock (herds, breeding
queue, per-village livestock markets - a possible Herds tab of its own); Guild.War
full schema now visible (all-zero while inactive). Room.Info now sends se/sw
diagonals (mapg copes as-is).

Raid **2.1.0** (28 Aug): both target groups in the town table (foreign/historical
towns were fed but never shown) and 'araid pool home|foreign' picks the auto-
target pool - foreign has no heat in the feed, so it spreads at random under the
same hold rotation. VERIFY LIVE: the first foreign auto-dispatch (vlongship
raid/convoy is assumed to accept historical towns).

Status **2.7.0** (28 Aug): per-good stock floors for the auto-trader - 'atrade
floor <good> <n>' keeps at least n of that good (raises the raw/refined reserve,
never lowers it; manual Dispatch respects it; floored raws restock up to the
floor). Floored names show info-blue in the Trade tab; held amber wins.

Status **2.8.0** + host **API 1.16** (28 Aug): the floors get a UI. The text
markup gains `rclick=` (a second, right-button command on the same clickable
run; emitted before click= so pre-1.16 hosts degrade to left-click-only; the
right button also stops falling through to click=). The Trade tab gets a Floor
box above Units; right-clicking a good's name applies the box's value as its
floor, the same value again toggles it off, box at 0 = the right button only
clears. NEEDS A VS BUILD + dotnet test: Markup.cs, StyledRun.cs (LinkInfo),
StyledTextView.cs, ScryeApi.cs 1.16, MarkupTests grown.

Status **2.9.0** (29 Aug, from live soak): the CARTYARD CLOCK. The game allows
one caravan per ~3 min; the trader was bouncing a doomed dispatch off 'The
cartyard is preparing your last caravan' every retry tick. Now every dispatch
starts an at.yard hold (default 180 s, 'atrade yard <n>', 0 = off), the
refusal's 'Ready in: XmYs' corrects the clock and quietly takes back the
refused dispatch, a never-left timeout releases a hold that was only guessed,
and the Trade Auto tab shows the countdown. Foreign longship raids CONFIRMED
live (vlongship raid/convoy accept historical towns).

23:09 capture (`_lab/captures/gmcp-fields-Goran-20260828-2309.md`, an ACTIVE
voyage): **vqpath CONFIRMED** - an array of "x,y" strings, exactly the
adapter's guess, so sea-nav reads it unchanged; voyage_chart
{width,height,chart_mode} confirmed (16x16 advanced). New-but-empty:
voyage_wait/vrelics/vreagent, Guild.Fleet supg[]. Guild.Livestock gains
lneeds (per-species housing current/cap) + lpending - fodder for a Herds
tab. Still absent: vresolve, Guild.Map terrain, routes (situational),
cdtime never carries the cartyard cooldown (the 2.9.0 yard clock stands).

Status **2.10.0** (29 Aug): **Guild.TradeGoods WIRED - the code map is
CRACKED.** Two in-game pastes (the `vtrade goods` overview symbol grid and the
`vtrade goods midgard` price table) were each matched to the 23:09 capture by
assignment optimisation; the two independent solutions agreed on all 30 codes
with zero mismatches. The map is arbitrary (c=wool, d=eggs, x=beef - guessing
would have poisoned the trader): a=sunstone b=bread c=wool cs=cheese d=eggs
e=fine_furs f=furs g=grain h=fish hm=horsemeat i=iron j=gemstones k=salted_fish
l=tools m=mead mi=milk n=finery o=ore p=pork q=mutton r=runestones s=spoils
sm=smoked_meat t=timber u=armour v=poultry w=weapons x=beef y=honey z=cloth.
lin 0 = Midgard, lin i = rtargets_lineage[i] (live from Guild.Fleet, overview
order as fallback). Market prices now arrive pushed; mkref's 30-command text
scan is retired to a fallback, and the price-staleness clock stays fresh off
the feed. Off the dev report: per-village market data (was 'market[] always
empty').

Remaining: vresolve shape, active campaign/war payloads, raid extras
(grudge-aware targeting, raid log), the rest of §5, the TradeGoods code->good
mapping (needs one in-game paste), and the dev report (Guild.Map terrain still the
big one; chat channels now flow via Comm.Channel.Text - vnotify confirmed; absent
keys unchanged).

Decisions taken: assembler is option (c), the shared Lua snippet. The split is **three
ways** — status core, kingdom, and a sea plugin carved out of Sea / Voyage / Map / Travel
(§4b as amended). The Viking guild is 3Scapes-only, so the current MIP versions freeze as
classics. First of the new ideas: the **effect timer bar**.

Why both-MUD support matters generally (and why classics stay shipped): 3K speaks only
MIP, and people who play on 3S carry the same plugin set over to 3K. Guild-viking plugins
escape that concern — no Viking guild on 3K — so their classics serve only 3S players who
still run MIP.
Field reference: `_lab/captures/gmcp-fields-Goran-20260827.md` (19,670 messages, 19 packages).

The situation: 3s-raid and 3s-viking-status are fed entirely by `vik.*` state, which the
client decodes from the MIP viking packet. With MIP off on 3Scapes, both are dead panels.
The Guild.* GMCP packages carry everything they used to read — and a fair amount they never
had. This plan maps every input to its GMCP source, proposes the rework (including the
two-plugin split you asked about), and lists new plugin ideas the captured fields make
possible.

---

## 1. What the two plugins actually consume today

**3s-raid 1.0.0** is small and clean about it — four feeds, read in one place:

| it reads | for |
|---|---|
| `vik.ships` | which longships are docked / raiding, names, tiers |
| `vik.buildings` (`dock:N`) | max simultaneous raids |
| `vik.heat` | per-town raid heat list |
| `vik.rtargets` | the raid target roster |

Plus `scrye.watch("vik.ships")` to drive the dispatch loop. Everything else — the
keep/hold/reserve strategy, solo-vs-convoy choice, aliases, the panel — is feed-agnostic.

**3s-viking-status 1.5.1** (17 tabs, 4,167 lines) reads ~50 distinct `vik.*` keys through
one accessor (`gv(k)`), watches `vik` wholesale, and has exactly six triggers: the build
planner scan and City Plan capture (both parse `-~*` report text — plain output, not MIP),
the Modrsokn cooldown line, and the `[Viking-Trade]` market tick. **Important nuance: the
market scanner half of the Trade tabs parses `vtrade goods` text output via triggers, so it
still works with MIP off.** What's dead is everything `gv()`-fed: all the status tabs, and
the auto-trader's cart/warehouse/daler awareness.

**nille-viking** (your private set — reference only, never enters this repo) runs the five
engines `atrade araid avoyage avfind aherd` off the same `vik.*` state, so whatever bridge
or convention we pick here determines how much of that port survives too.

## 2. The field map: every `vik.*` input has a GMCP home (almost)

Verified against the capture. Scrye already files each package into state under
`guild.<pkg>.*` paths automatically — the catch is paging, covered in §3.

| old feed | GMCP source |
|---|---|
| ships | **Guild.Fleet** `ships[]` (state/target/secs/tier/crew/held/convoy…) |
| rtargets | **Guild.Fleet** `rtargets_lineage[]` / `rtargets_historical[]` |
| heat | **Guild.City** `heat[]` (one slot per town) |
| buildings (dock tier) | **Guild.City** `buildings[]` (`{id, tier}`) |
| builds, blot, patrol, nexttick, dcycle, cityplan/CPB | **Guild.City** (`builds[]`, `blot{}`, `patrol{}`, `cityplan*` incl. placeables + placed grid) |
| daler, fury, stfx | **Guild.State** (`daler`, `points.fury`, `fx.stfx`) |
| carts, missions, refinery, wstock cap | **Guild.Trade** (`carts[]`, `missions[]`, `refinery[]` + `refinery_grades[]`, `wstock_cap`) |
| settlers, sconsume, scivics, shplots, sproj | **Guild.Settlement** (+ `settlerx{}` and `sroles[]`, richer than the old feed) |
| voyage, voffers, vsaga, vqpath, vresolve | **Guild.Voyage** (`voyage{}` live x/y/hull/morale/supplies/danger/next_move_in; longship saga pages) |
| standings, vrep, grudges | **Guild.Kingdom** (+ `diplo[]`, `dynasty_*`, `campaign*`, `army*` — mostly new) |
| hird, bonds | **Guild.Roster** (`hird[]` atk/def/loyalty/status, `gneeds`, `thralls` per building, `spy`, `bonds`, `rneeds[]`) |
| vmaph / vmapl | **Guild.Map** (`terrain`/`east`/`south` edge grids + `pos{x,y}` — same data the click-to-walk BFS needs) |
| war board | **Guild.War** (terrain, reserve, works, wall, turn — empty in capture, no war ran) |
| livestock | **Guild.Livestock** (`lneeds[]` per species + per-building) |

**Gaps — feed keys with no GMCP counterpart in the capture:** `production`, `errand`,
`sevents`, and the per-good warehouse stock behind WSTOCK (only `wstock_cap` came through).
Also `Guild.Trade.market[]` exists but was empty the whole session — worth one `mkref`
while capturing to see if the market scan can eventually drop its text triggers. Same
category as the missing chat channels: report to the 3S devs, don't build around guesses.

## 3. The one real technical problem: page assembly

Guild.* packages arrive **paged**: `{page: i, pages: N, full: 1?}`, with list content
(ships, carts, grudges…) split across pages. The automatic state filing overwrites arrays
per page — `guild.fleet.ships.0` is whichever pair arrived last — so raw state paths are
unreliable for anything list-shaped. Every consumer needs the same ~40 lines:

- buffer a burst by package; a burst with `full:1` replaces the whole snapshot, one
  without it merges into the current snapshot;
- **concatenate** list keys across the pages of one burst (ships span pages 3–6),
  scalar keys last-write;
- publish the merged snapshot when `page == pages`, and keep the previous value for
  keys a burst didn't mention (the Guild.State "pointless page" lesson from the Seid work).

Where should that live? Three options:

- **(a) In the client (C#)**, like the MIP decoder: assemble and republish as state.
  Cleanest for consumers, but bakes 3S game knowledge into the client and needs a rebuild
  per feed quirk.
- **(b) A bridge plugin** that assembles and republishes `vik.*`-compatible strings so
  existing plugins run unmodified. Tempting, but reproducing the MIP string formats
  exactly is the crufty kind of work, and plugin-to-plugin state ordering is a new
  failure mode.
- **(c) A shared Lua assembler snippet pasted into each consumer** (the same convention as
  the shared TIERCOL palette), each plugin subscribing `onGmcp("Guild.X")` for just the
  packages it needs.

**Recommendation: (c).** It's how the farmer and chaossea already consume Guild.State,
there are only 2–3 consumers, iteration needs no client rebuild, and each plugin keeps
its lab harness able to replay captured payloads straight into the assembler. If a fourth
or fifth consumer appears we can promote it into the client later with the semantics
already proven.

## 4. The rework

### 4a. 3s-raid 2.0 — first, and smallest

Swap the four feed reads for Guild.Fleet + Guild.City through the assembler; the strategy
core, aliases, and panel stay. The dispatch loop's `watch("vik.ships")` becomes
"snapshot published" from the assembler. This is the pilot that proves §3 live.

Two upgrades the GMCP data offers for free, both optional:

- **Grudge-aware targeting.** `Guild.Kingdom.grudges[]` has per-town cooldown seconds —
  the thing the old plugin dropped (`vik.grudges no longer read`) because the MIP feed
  made it awkward. Skipping towns whose grudge outlasts the sail time is a real win.
- **Raid log tab.** `Guild.Fleet.raidlog[]` + `raidlog_goods[]`: daler, thralls, losses
  and cargo per completed raid — the old plugin had nothing like it.

### 4b. The split — decided: three ways

The seam for the new content is where you pointed (roster/hird, kingdom), and the sea
tabs carve out as a third plugin rather than staying in the core.

**3s-viking-status 2.0 — the settlement.** The land half of the current plugin, GMCP-fed:
Stats / City / Builds / Production* / People / Settlers / Holds / Plan / Mission /
Trade / Trade Auto / Trade Log / Feeds. Consumes Guild.State, Info, City, Settlement,
Trade, Livestock. The `gv()` accessor is replaced by snapshot reads; tab composers
largely survive untouched. The Feeds tab changes meaning: per-package "last burst at /
pages / staleness" instead of `vik.*` keys seen.
(*Production stays a thin tab until the feed gap in §2 is answered.)

**3s-viking-sea — the fleet's world (new home for existing tabs).** Sea / Voyage / Map /
Travel, from Guild.Voyage + Guild.Map: the sea chart, the live voyage tracker
(x/y, hull, morale, supplies, danger, next_move_in, resolve options), the territory map
with click-to-walk, and the settlement travel buttons. The §5 voyage-navigator idea
lands here when its turn comes, not in a fourth plugin.

**3s-viking-kingdom — the dynasty (new).** Guild.Roster + Kingdom + War: the hird roster
grid (name, atk/def, loyalty, status), guild needs + recruit needs (`rneeds` — what stat
and trait to hire for which building), thralls per building, spymaster, bonds; grudge
board with live countdowns, lineage standings + `vrep` progress, `diplo`; the dynasty
pages (house, spouse, children, schooling — never displayed anywhere before); campaign /
army / prison; and the War board when a war fires payloads to build against.

The current 1.5.1 stays available renamed **"3S Viking Status (classic, MIP)"**, frozen —
same precedent as 3s-map classic and 3k-chaossea, for anyone on 3S still running MIP.
Same for 3s-raid 1.0.0. (Assumption: the Viking guild is 3Scapes-only, so no 3K
obligation here — say the word if that's wrong.)

### 4c. Trade Auto

The market **scanner** keeps its text triggers (still working, and `Guild.Trade.market[]`
is unproven — §2). The **auto-trader** moves its cart/warehouse/daler awareness onto
Guild.Trade `carts[]` + Guild.State `daler` — which is a straight upgrade: `secs`,
`quality_pct`, `half_in` and `durability` per cart are better dispatch inputs than the
old feed strings ever were.

## 5. New plugin ideas from the captured fields

Ranked by my sense of value-per-effort; none started until you pick.

1. **Effect timer bar** — `Guild.State.fx.stfx` is a complete `[name:secs …]` buff list,
   plus `god{name, focus, expires_at}` and the Modrsokn line. A one-row HUD strip (or a
   3s-vitals section) with expiry countdowns and a warn-at threshold. Small and useful
   every minute of play.
2. **Target assist for the bots** — `Guild.State.target{name, name5, hp_status}`: the
   server tells you the 5-letter attack keyword. The farmer currently guesses "last plain
   word" from Room.Contents; preferring `name5` when the target matches makes its attack
   line server-truthful. An enhancement to existing bots, not a new plugin.
3. **Voyage navigator** — Guild.Voyage live position, danger, hull/morale/supplies,
   `next_move_in`, resolve options: first a proper tracker tab, later the `avoyage`-style
   auto-sailor (your nille engine is the reference for the strategy thresholds).
4. **Raid economics** — the raidlog idea from §4a grown up: daler/hour per ship, per
   town, cargo mix, thrall count vs `Guild.Roster.thralls` capacity.
5. **Hird recruiter** — `rneeds[]` says which stat/trait each building wants; `avfind`
   posted jobs by stat. A "post the right notice" helper, then automation.
6. **Herd manager** — `Guild.Livestock.lneeds[]` per species/building; `aherd` reference.
7. **Coffin / threk alerts** — `Char.Vitals.coffin/coffin_max` and `hp.threk/mthrek` are
   general (not guild) fields; belongs in 3s-vitals rather than a new plugin.

## 6. Suggested order

1. **3s-raid 2.0** — pilots the page assembler on two packages; smallest surface.
2. Live-soak the assembler (the capture replays make the lab harness for it cheap).
3. **Effect timer bar** — first of the new ideas (decided), near-free once the
   assembler exists.
4. **3s-viking-status 2.0** — the big port, tab by tab, Feeds tab first (it's the
   debugging window for the rest).
5. **3s-viking-sea** — the four sea tabs move over.
6. **3s-viking-kingdom** — new build, roster grid first, War board last (needs a war).
7. Then the rest of §5 as picked; the farmer `name5` assist any time.
8. Report the §2 feed gaps (production, errand, sevents, per-good wstock, empty
   `Trade.market`) to the 3S devs alongside the missing chat channels.
