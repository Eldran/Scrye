# Plugin colour system

The bundled plugins share one designed colour system instead of per-plugin eyeballed
hex. Every value below was generated in OKLCH and validated with a palette validator
(CVD separation simulated with Machado–Oliveira–Fernandes 2009, WCAG contrast, OKLab
ΔE×100) against the surface it renders on. When adding a colour to a plugin, pick from
these steps — or generate a new step the same way — rather than eyeballing a hex.

## Rule 1: semantics wear theme tokens

Anything that *means* something uses a `ThemeToken` name, never a literal, so it
follows all six app schemes (including Light):

| Meaning | Token |
|---|---|
| captions, hints, secondary text | `dim` |
| good / ready / complete | `success` |
| armed / caution | `warning` |
| enemy / failure / kills | `error` |
| neutral informational values | `info` |

Literals are reserved for **identity**: a plugin's signature accent, a chat channel,
a tier, a map tile — things that should *not* change with the app theme.

## Signature accents (panel border + title)

One hue family per plugin, stepped to a common band (OKLCH L 0.62–0.67), validated as
a categorical set on the panel surface `#1B212B`: all six checks pass (worst pair
ΔE 15.9 CVD / 20.8 normal), and every accent reads at ≥ 4.5:1 as title text.

| Plugin | Accent |
|---|---|
| 3s-build | `#5AAC47` green |
| 3s-chaossea | `#0B9DB3` teal |
| 3s-market | `#AC811E` gold |
| 3s-raid | `#E7574E` red |
| 3s-viking-status | `#6288E1` steel-blue |

(3s-map and 3s-stepper deliberately have no signature — they wear the theme accent.)
A widget echoing its panel's identity (market's status line, chaossea's sea id,
viking's section headers) repeats the accent literal.

## Chat channels (terminal output, surface `#080A0C`)

Nine chromatic hues stepped around the wheel, alternating lightness so neighbours
separate by two axes; all ≥ 4.5:1 text contrast; worst adjacent pair ΔE 9.5 CVD /
16.3 normal. The `[channel]` prefix on every line is the identity fallback. These sit
above the chart lightness band on purpose — they are text colours, judged by text
contrast. tell `#4BE4FF` · main `#FD2083` · party `#93F64E` · newbie `#2EB88F` ·
shout `#FFFFFF` · admin `#CA90FB` · events `#DEB218` · viking `#DF6E1B` ·
whine `#7263FD` · gamers `#F2A3C1` · lottery/poll `#A0A7BB`.

## Tiers T1–T5 (3s-build, 3s-viking-status)

Game-rarity convention (multi-hue), re-stepped and validated in sequence order
(worst adjacent ΔE 9.5 CVD / 18.4 normal); the printed tier number is the fallback.
T1 `#B99EE9` lavender · T2 `#4BE4FF` cyan · T3 `#93F64E` lime · T4 `#DEB218` gold ·
T5 `#FD2083` magenta.

## Map grids

Terrain keeps its legend-named hue family; markers are validated all-pairs.
Shared steps: water `#3991B7` (land map + sea chart), hills/island `#BAB245`,
sea surface `#19232A`. Collisions that were fixed by re-stepping: chaossea start
vs down-exit (start is now `#FFFFFF`), viking ruins `#9256A0` vs POI magenta,
the sea chart's three greens (path `#79D963` / harbor `#4CA563` / resolved
`#456F4E`, destination now amber `#D18E24`), plan-grid grim `#742D31` vs industry
red. Grid cells that sit below 3:1 against their surface must carry a letter
(`labels`) — that is why `=` joined SEALETTERS and the chaossea grid labels `@fvS`.
