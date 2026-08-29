-- 3S Viking Status (GMCP) -- the settlement half of the Viking HUD, rebuilt on the
-- Guild.* GMCP packages (docs/Plan-Viking-GMCP.md, the three-way split).
--
-- Lineage: 3s-viking-status 1.5.1 (the MIP classic, itself a conversion of
-- MUSHclient ThreeS_VikingStatus). HOW this port works: the classic funnels every
-- feed read through gv(k) and parses MIP wire strings. This port keeps all of that
-- - composers, build planner, market scanner, auto-trader - and swaps only the feed
-- layer: page-assembled Guild.* snapshots (gasm, at the bottom) are translated into
-- the same key->string table the parsers already read (vset/V). Both sides of each
-- string format are in this one file, so the format is a private contract, not a
-- wire protocol.
--
-- Packages consumed: Guild.State, Info, City, Settlement, Trade, Fleet, Roster,
-- Kingdom, Warehouse. The SEA HALF IS GONE from this plugin: Sea / Voyage / Map / Travel tabs,
-- sea-nav and the charts move to 3s-viking-sea (the travel ENGINE stays here - the
-- mission runner walks towns - and owns the vgo/vhere aliases; the sea plugin's
-- Travel tab clicks run vgo, which lands here).
--
-- Feed gaps (captures 27-28 Aug; the 28 Aug AFTERNOON server patch closed the big one):
--   * per-good warehouse stock: CLOSED - Guild.Warehouse (28 Aug pm) carries
--     good/amount/grade with the REAL freshness pct, plus wstock_cap. Its adapter
--     owns WSTOCK from its first burst; the `vtrade stock` text scan stays only as
--     the fallback for a server without the patch (ws_feed_live gates it off).
--   * production-per-tick, errand, staff, monuments, weather word,
--     kap/aud/vis/soemd: still absent - their lines say "?" or "none".
--   * Guild.TradeGoods: WIRED (29 Aug) - per-village market prices straight off
--     the feed; the 30 good codes were solved mechanically from two in-game
--     pastes cross-validated against the 23:09 capture (zero mismatches; see the
--     CODE2RES decoder ring). The 30-command mkref text scan is retired to a
--     fallback for a server without the package.
--   * cidle live-confirmed 28 Aug; routes appeared 28 Aug am (situational);
--     Guild.City raid{faction,strength,secs} + bdmg[] appeared 28 Aug pm (wired:
--     Stats' War section and the Builds tab).
--
-- 2.10.0: the MARKET FEED - Guild.TradeGoods wired via the cracked code map
-- (CODE2RES): prices per village arrive pushed, the report/dispatch/auto-trader
-- all read them live, mkref answers "feed live" instead of scanning, and the
-- price staleness clock never triggers a scan burst again.
--
-- 2.9.0: the CARTYARD CLOCK - the game allows one caravan per ~3 min and the
-- trader used to bounce a doomed dispatch off "The cartyard is preparing your
-- last caravan" every retry tick for the whole cooldown (live soak, 28 Aug).
-- Now every dispatch starts an at.yard hold (default 180 s, 'atrade yard <n>',
-- 0 = off), the refusal's own "Ready in: XmYs" corrects the clock and quietly
-- takes back the refused dispatch, and a never-left timeout releases a hold
-- that was only guessed. The Trade Auto tab shows the countdown.
--
-- 2.8.0: floors get a UI - a Floor box on the Trade tab (above Units), and a
-- RIGHT-CLICK on a good's name in the report applies it as that good's floor
-- (rclick= markup, API 1.16; same value again toggles it off; box at 0 = the
-- right button clears). On a pre-1.16 host the right button does nothing and
-- everything else still works.
--
-- 2.7.0: per-good stock FLOORS for the auto-trader ('atrade floor <good> <n>'):
-- the trader never sells a floored good below its floor (the floor RAISES the
-- raw/refined category reserve, never lowers it - manual Dispatch respects it
-- too), and a floored RAW restocks up to the floor instead of Raw>. Floored
-- names wear info-blue in the Trade tab; held (exempt) amber still wins.
--
-- NOTE: dropped / simplified vs the original:
--  * vikbar / viktab dropped: the HUD manages panel visibility and tab switching.
--    Both are still consumed by the plugin (with a note) rather than being sent to
--    the MUD as unrecognised commands.
--  * All miniwindow drawing replaced by one scrye.addPanel with tabs; Map / Sea chart /
--    City Plan grids are colorgrid widgets; reports are composed text widgets.
--  * Town travel RESTORED via the "Travel" tab: a button per settlement walks you there
--    using the built-in ROUTES table (origin = remembered curtown, else the live map
--    position). Correct a wrong start with  vhere <town>  (replaces the old right-click
--    "I am here"). The original mapped clicks to map cells; here it's a button list.
--  * Click-to-place on the Plan tab, building palette selection and the
--    text/tiles/icons view toggle dropped (no hotspots / images); the Plan tab shows
--    the grid (buildings as role-coloured cells) plus a letter palette so you can
--    type `vplan place <letter> <cell>` yourself.
--  * Voyage-chart cell click (queue course) and dynamic resolve buttons dropped;
--    pending resolve options are listed as text ("vvoyage resolve <opt>").  A static
--    "Clear voyage queue" button is kept.  Auto sea-nav (vnav) is kept in full.
--  * Patrol [change] number override dropped (needed an input box); "Commit patrol"
--    button kept, committing the last patrol's count.
--  * vikdump writes to the output pane instead of a file (no file access).
--  * Unmapped-chart-char logging to vik_unmapped.log dropped; unknown chars are shown
--    inline on the Map/Sea tabs instead (rendered as '?' cells).
--  * Modrsokn cooldown is not persisted across restarts (no clock available); it
--    counts 3:00 from the trigger line within a session.
--  * Local countdowns that needed a wall clock (sea next-move re-anchor, settler
--    "tick HH:MM" header) simplified to the raw feed values.

local P = "plugin." .. scrye.id .. "."

-- ---------------------------------------------------------------- colour
-- Feed values come from the MUD, so escape them before embedding in markup.
-- (A lone "@" is safe -- markup only starts at "@{" -- but a name could contain one.)
local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end

-- Pad BEFORE colouring: "#" counts markup characters, which are never drawn, so padding a
-- decorated string silently breaks column alignment.
local function padesc(s, n)
  s = tostring(s or "")
  return esc(s .. string.rep(" ", math.max(0, n - #s)))
end

local function col(c, s) return "@{" .. c .. "}" .. esc(s) .. "@{}" end
local function colb(c, s) return "@{" .. c .. ",bold}" .. esc(s) .. "@{}" end

-- Tier palette, carried over from the original's TIERCOL and repainted for the neon scheme.
-- Literals rather than theme tokens: a tier's colour is identity, not semantics.
-- OKLCH-stepped, colour-blind-validated; shared with 3s-build (see the note there).
local TIERCOL = { [1] = "#B99EE9", [2] = "#4BE4FF", [3] = "#93F64E", [4] = "#DEB218", [5] = "#FD2083" }
local function tiercol(t) return TIERCOL[tonumber(t) or 0] or "text" end

-- Percentage -> status token. Used for warehouse quality, mood, water, voyage bars, so one
-- rule decides "healthy / worth a look / bad" everywhere in the panel.
local function pctcol(v, good, warn)
  v = tonumber(v) or 0
  if v >= (good or 80) then return "success" end
  if v >= (warn or 50) then return "warning" end
  return "error"
end

-- Standing words the MUD uses, mapped onto the semantic tokens.
local STANDCOL = {
  allied = "success", friendly = "success", cordial = "success",
  neutral = "dim", wary = "warning", unfriendly = "warning",
  hostile = "error", war = "error", enemy = "error",
}
local function standcol(w) return STANDCOL[tostring(w or ""):lower()] or "text" end

-- Two-column layout. Padding has to be measured on the UNDECORATED string, because markup
-- characters take width in Lua but none on screen. Each item is { raw = , txt = }: raw for
-- measuring, txt for display.
local function padcell(item, width)
  return item.txt .. string.rep(" ", math.max(0, width - #item.raw))
end

-- Fill down the first column, then the second -- the same reading order the Buildings and
-- Production tabs already use, so an alphabetical list stays alphabetical down each column.
local function two_col(items, width, add)
  local rows = math.ceil(#items / 2)
  for i = 1, rows do
    local a, b = items[i], items[i + rows]
    add(padcell(a, width) .. (b and ("  " .. b.txt) or ""))
  end
end

-- ---------------------------------------------------------------- helpers
local function num(s) return tonumber(s) or 0 end

-- The translated feed: vik-key -> string, written only by vset() (bottom of file).
local V = {}
local function gv(k) return V[k:lower()] or "" end

local function q(k)
  local v = gv(k)
  if v == "" then return "?" end
  return v
end

local function split(s, sep)
  local out = {}
  if not s or s == "" then return out end
  local pat = "([^" .. sep .. "]*)" .. sep .. "?"
  local pos = 1
  while pos <= #s + 1 do
    local st, en, cap = s:find(pat, pos)
    if not st then break end
    out[#out + 1] = cap
    if en < st then break end
    pos = en + 1
  end
  if out[#out] == "" then out[#out] = nil end
  return out
end

local function clean(s)
  return (s or ""):gsub("grey:", ""):gsub("gray:", ""):gsub("red:", "")
                  :gsub("green:", ""):gsub("blue:", ""):gsub("yellow:", "")
end

-- "0|Name|a|b;1|Name2|..." -> map idx -> field list
local function parse_idx_table(s)
  local out = {}
  for entry in (s or ""):gmatch("[^;]+") do
    local f = split(entry, "|")
    local i = tonumber(f[1])
    if i then out[i] = f end
  end
  return out
end

local function parse_locs(s)
  local out = {}
  if not s or s == "" then return out end
  for entry in s:gmatch("[^;]+") do
    local f = {}
    for tok in entry:gmatch("[^|]*") do
      if tok ~= "" or #f > 0 then f[#f + 1] = tok end
    end
    local i = 1
    while i + 3 <= #f do
      if tonumber(f[i + 2]) and tonumber(f[i + 3]) then
        out[#out + 1] = { type = f[i], name = f[i + 1],
                          x = tonumber(f[i + 2]), y = tonumber(f[i + 3]) }
        i = i + 4
      else
        i = i + 1
      end
    end
  end
  return out
end

local function titlecase(s)
  return (s or ""):gsub("_", " "):gsub("(%a)([%w]*)", function(a, b) return a:upper() .. b end)
end

-- ------------------------------------------------------------- palettes
-- Original colours were MUSHclient BGR; converted here to #RRGGBB.

-- (territory-map and sea-chart palettes moved to 3s-viking-sea)

-- city plan: terrain char -> tile colour; placed buildings become role digits 1-7
local PLAN_PAL = {
  ["."] = "#484848",  -- plain
  ["f"] = "#246E24",  -- woods
  ["H"] = "#8C7050",  -- hill
  ["w"] = "#2E64A6",  -- river
  ["c"] = "#206A6A",  -- coast
  ["M"] = "#585858",  -- wall
  ["W"] = "#4E4E4E",  -- wall
  ["G"] = "#886C46",  -- gate
  ["B"] = "#886C46",  -- gate
  ["1"] = "#40E040",  -- producers  (green)
  ["2"] = "#E04030",  -- industry   (red)
  ["3"] = "#742D31",  -- grim       (maroon, darkened away from industry red)
  ["4"] = "#40D0E0",  -- trade      (cyan)
  ["5"] = "#E060E0",  -- culture    (magenta)
  ["6"] = "#E0E0E0",  -- homes      (white)
  ["7"] = "#FFD040",  -- throne     (gold)
  ["?"] = "#383838",  -- unknown
}

-- Town-plan micro-icons: one glyph per district, terrain as on the world map.
local PLAN_ICONS = {
  ["f"] = "tree", ["H"] = "hill", ["w"] = "water",
  ["G"] = "gate", ["B"] = "gate",
  ["1"] = "grass",    -- producers (fields)
  ["2"] = "hammer",   -- industry
  ["3"] = "cross",    -- grim quarter
  ["4"] = "ship",     -- trade
  ["5"] = "star",     -- culture
  ["6"] = "house",    -- homes
  ["7"] = "crown",    -- throne
}
local ROLE_DIGIT = { prod = "1", ind = "2", grim = "3", trade = "4",
                     cult = "5", home = "6", throne = "7" }

-- Image tiles (host API 1.17): hand-drawn art per Plan character, living in the
-- plugin's own tiles/ folder and riding the same Icons on/off toggle as the vector
-- glyphs (an imaged character beats its glyph; the rest keep their glyphs). A named
-- file that does not exist yet is harmless - the cell falls back to glyph/tile - so
-- this table can grow ahead of the art: draw a PNG, drop it in tiles/, done. On a
-- pre-1.17 host the field is ignored and the grid renders exactly as before.
local PLAN_IMAGES = {
  ["7"] = "tiles/tower.png",   -- throne district - the first hand-drawn tile
}

-- --------------------------------------------------- static logic tables

-- trade-hold index -> city name (VREP gives the lineage name, which is confusing)
local HOLDCITY = {
  [0]  = "Midgard",       [1]  = "Lodbrok's Hold", [2]  = "Eiriksby",  [3]  = "Imaird",
  [4]  = "Holmgard",      [5]  = "Hafrfjord",      [6]  = "Uppsala",   [7]  = "Borgarfjord",
  [8]  = "Vestergotland", [9]  = "Sverkersby",     [10] = "Ericsgard", [11] = "Birka",
  [12] = "Lejre",         [13] = "Nidaros",
}

-- known settlements; vikloc overrides
local DEFAULT_LOCNAMES = {
  ["35|17"] = "Midgard",
  ["17|9"]  = "Lodbrok's Hold",
  ["55|7"]  = "Eiriksby",
  ["13|17"] = "Imaird",
  ["57|17"] = "Holmgard",
  ["37|7"]  = "Hafrfjord",
  ["33|27"] = "Uppsala",
  ["19|25"] = "Borgarfjord",
  ["53|25"] = "Vestergotland",
  ["23|21"] = "Sverkersby",
  ["49|9"]  = "Ericsgard",
  ["15|23"] = "Birka",
  ["55|21"] = "Lejre",
  ["25|9"]  = "Nidaros",
  ["50|27"] = "Jarl",
  ["51|16"] = "Berserker",
  ["3|28"]  = "Blot",
}
local DEFAULT_LOCTYPE = {
  ["50|27"] = "Mentor", ["19|25"] = "Seer", ["51|16"] = "Mentor", ["3|28"] = "Blot",
}
-- kept (without the ROUTES walking) for the always-listed known locations + sorting
local TRAVEL_CODE = {
  ["35|17"] = "Mid", ["17|9"] = "Lod", ["55|7"] = "Eir", ["13|17"] = "Ima",
  ["57|17"] = "Hol", ["37|7"] = "Haf", ["33|27"] = "Upp", ["19|25"] = "Bor",
  ["53|25"] = "Vas", ["23|21"] = "Sve", ["49|9"] = "Eri", ["15|23"] = "Bir",
  ["55|21"] = "Ler", ["25|9"] = "Nid",
}
local SPECIAL_TRAVEL = { ["3|28"] = "Blot" }
local function travel_code(key) return TRAVEL_CODE[key] or SPECIAL_TRAVEL[key] end

-- building shortcut letter -> { letter, name, role } (for `vplan place <L> <cell>`)
local PLAN_BLD = {
  { "F", "Farm",          "prod"  }, { "M", "Mine",          "prod"  },
  { "K", "Smithy",        "ind"   }, { "E", "Beacon",        "grim"  },
  { "P", "Apiary",        "prod"  }, { "I", "Fishery",       "prod"  },
  { "S", "Smelter",       "ind"   }, { "N", "Tannery",       "ind"   },
  { "R", "Armoury",       "ind"   }, { "g", "Garrison",      "grim"  },
  { "V", "Weaponry",      "ind"   }, { "d", "Mead hall",     "throne"},
  { "B", "Bakehouse",     "ind"   }, { "W", "Warehouse",     "trade" },
  { "h", "Longhouse",     "home"  }, { "Z", "Goldsmith",     "ind"   },
  { "k", "Skald hall",    "cult"  }, { "y", "Thrall pen",    "grim"  },
  { "L", "Lumber yard",   "prod"  }, { "C", "Mead cellar",   "ind"   },
  { "O", "Courier post",  "trade" }, { "X", "Shadow house",  "grim"  },
  { "T", "Trading post",  "trade" }, { "D", "Training yard", "grim"  },
  { "u", "Muster ground", "grim"  }, { "H", "Settler plots", "home"  },
  { "G", "Salting house", "ind"   }, { "U", "Furriers lodge","ind"   },
  { "o", "Workyards",     "ind"   }, { "l", "Law stone",     "cult"  },
  { string.char(254), "Poorhouse", "cult" }, { "b", "Watch posts", "grim" },
  { "v", "Foundry row",   "ind"   }, { "r", "Granary mill",  "ind"   },
  { "e", "Weaving hall",  "ind"   }, { "w", "Quarry works",  "prod"  },
  { "i", "Midwife house", "cult"  }, { "a", "Market stalls", "trade" },
  { "n", "Fishing wharf", "cult"  }, { "t", "Timberward yd", "trade" },
  { "s", "Shipwrights yd","cult"  },
}
local PLAN_BY_L = {}
for _, b in ipairs(PLAN_BLD) do PLAN_BY_L[b[1]] = { name = b[2], role = b[3] } end

-- ------------------------------------------------------ persistent state
local last_fight = tonumber(scrye.store.get("last_fight")) or 0
local prev_rndz = nil

-- user-named map locations (vikloc x y name)
local locnames = {}
do
  local s = scrye.store.get("locnames")
  if s then
    for line in s:gmatch("[^\n]+") do
      local x, y, n = line:match("^(%-?%d+)|(%-?%d+)|(.*)$")
      if x then locnames[x .. "|" .. y] = n end
    end
  end
end
local function locname(x, y)
  local key = x .. "|" .. y
  return locnames[key] or DEFAULT_LOCNAMES[key]
end

-- ---------------------------------------------------- town travel (ported from MUSHclient)
-- town <-> Midgard base legs; any pair is chained as <From>Mid + Mid<To>.
local ROUTES = {
  BirMid = { "leave","s","e","e","e","e","e","e","e","e","e","e","e","e","e","n","e","e","e","e","e","e","e","n","n","n","n","n","n","enter" },
  BlotMid = { "e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","n","n","n","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","n","n","n","n","n","n","n","n","enter" },
  BorMid = { "leave","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","n","n","n","n","n","n","n","n","enter" },
  EirMid = { "leave","w","e","e","s","s","s","s","s","s","w","w","s","e","s","s","s","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","enter" },
  EriMid = { "leave","w","w","s","s","s","s","s","s","s","s","w","w","w","w","w","w","w","w","w","w","w","w","enter" },
  HafMid = { "leave","e","s","s","s","s","s","s","s","s","s","s","w","w","w","enter" },
  HolMid = { "leave","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","enter" },
  ImaMid = { "leave","e","s","s","s","s","s","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","n","n","n","n","n","enter" },
  LerMid = { "leave","n","n","n","n","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","enter" },
  LodMid = { "leave","s","s","s","s","s","s","s","s","s","s","s","s","s","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","n","n","n","n","n","enter" },
  MidBir = { "leave","s","s","s","s","s","s","w","w","w","w","w","w","w","s","w","w","w","w","w","w","w","w","w","w","w","w","w","n","enter" },
  MidBlot = { "leave","s","s","s","s","s","s","s","s","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","s","s","s","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w" },
  MidBor = { "leave","s","s","s","s","s","s","s","s","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","enter" },
  MidEir = { "leave","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","n","n","n","w","n","e","e","n","n","n","n","n","n","w","w","e","enter" },
  MidEri = { "leave","e","e","e","e","e","e","e","e","e","e","e","e","n","n","n","n","n","n","n","n","e","e","enter" },
  MidHaf = { "leave","e","e","e","n","n","n","n","n","n","n","n","n","n","w","enter" },
  MidHol = { "leave","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","enter" },
  MidIma = { "leave","s","s","s","s","s","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","n","n","n","n","n","w","enter" },
  MidLer = { "leave","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","s","s","s","s","enter" },
  MidLod = { "leave","s","s","s","s","s","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","n","n","n","n","n","n","n","n","n","n","n","n","n","enter" },
  MidNid = { "leave","s","s","s","s","s","w","w","w","w","w","w","w","w","w","w","n","n","n","n","n","n","n","n","n","n","n","n","n","enter" },
  MidSve = { "leave","s","s","s","s","s","w","w","w","w","w","w","w","w","w","w","w","w","n","enter" },
  MidUpp = { "leave","s","s","s","s","s","s","s","s","s","s","w","w","enter" },
  MidVas = { "leave","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","e","s","s","s","s","s","s","s","s","e","e","enter" },
  NidMid = { "leave","s","s","s","s","s","s","s","s","s","s","s","s","s","e","e","e","e","e","e","e","e","e","e","n","n","n","n","n","enter" },
  SveMid = { "leave","s","e","e","e","e","e","e","e","e","e","e","e","e","n","n","n","n","n","enter" },
  UppMid = { "leave","e","e","n","n","n","n","n","n","n","n","n","n","enter" },
  VasMid = { "leave","w","w","n","n","n","n","n","n","n","n","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","enter" },
}

-- town abbrev -> coord key (reverse of TRAVEL_CODE / SPECIAL_TRAVEL) for labels
local TOWN_COORD = {}
for coord, code in pairs(TRAVEL_CODE)   do TOWN_COORD[code] = coord end
for coord, code in pairs(SPECIAL_TRAVEL) do TOWN_COORD[code] = coord end

local function town_label(code)
  local coord = TOWN_COORD[code]
  if coord then
    local x, y = coord:match("^(%-?%d+)|(%-?%d+)$")
    return locname(tonumber(x), tonumber(y)) or code
  end
  return code
end

-- current live town: the map feed lives in 3s-viking-sea now, so gv(vmaph) is
-- always empty here and this returns nil - the remembered curtown carries travel
local function live_town()
  local hd = split(gv("vmaph"), "|")
  local px, py = tonumber(hd[3]), tonumber(hd[4])
  if not (px and py) then return nil end
  local function near(tbl)
    for k, code in pairs(tbl) do
      local tx, ty = k:match("^(%-?%d+)|(%-?%d+)$")
      if math.abs(px - tonumber(tx)) <= 2 and math.abs(py - tonumber(ty)) <= 2 then return code end
    end
  end
  return near(TRAVEL_CODE) or near(SPECIAL_TRAVEL)
end

local function send_route(origin, dest)
  local cmds = {}
  if origin ~= "Mid" then
    local leg = ROUTES[origin .. "Mid"]; if not leg then return false end
    for _, c in ipairs(leg) do cmds[#cmds + 1] = c end
  end
  if dest ~= "Mid" then
    local leg = ROUTES["Mid" .. dest]; if not leg then return false end
    for _, c in ipairs(leg) do cmds[#cmds + 1] = c end
  end
  for _, c in ipairs(cmds) do scrye.send(c) end
  return #cmds          -- how long the walk is, so a caller can pace itself around it
end

-- Walk to a town. Origin = the remembered curtown (authoritative once set by travel or
-- 'vhere'), else the live map position. After travel we KNOW where we are, so remember it.
-- Returns the number of commands sent (0 = already there), or nil if it could not go --
-- having already said why. The mission runner paces itself off that count.
-- Where we think we are: the remembered town wins (travel and `vhere` both set it and are
-- both definite), the laggy live map feed is the fallback, and nil means neither could say.
local function current_town()
  local rem = scrye.store.get("curtown"); if rem == "" then rem = nil end
  return rem or live_town()
end

local function travel_to(dest)
  local origin = current_town()
  if not origin then
    scrye.print("[viking] can't tell where you are - set it with  vhere <town>  first")
    return
  end
  if origin == dest then scrye.print("[viking] you're already at " .. town_label(dest)); return 0 end
  local n = send_route(origin, dest)
  if not n then
    scrye.print("[viking] no route known for " .. town_label(origin) .. " -> " .. town_label(dest))
    return nil
  end
  scrye.print("[viking] travelling " .. town_label(origin) .. " -> " .. town_label(dest))
  scrye.store.set("curtown", dest)
  return n
end

-- travelable towns, sorted by display name (for the Travel tab buttons + vhere matching)
local TRAVEL_TOWNS = {}
for _, code in pairs(TRAVEL_CODE)   do TRAVEL_TOWNS[#TRAVEL_TOWNS + 1] = code end
for _, code in pairs(SPECIAL_TRAVEL) do TRAVEL_TOWNS[#TRAVEL_TOWNS + 1] = code end
table.sort(TRAVEL_TOWNS, function(a, b) return town_label(a) < town_label(b) end)

local function resolve_town(s)
  s = (s or ""):gsub("^%s+", ""):gsub("%s+$", ""):lower()
  for _, code in ipairs(TRAVEL_TOWNS) do
    if code:lower() == s then return code end
    local lbl = town_label(code):lower()
    if lbl == s or lbl:find(s, 1, true) then return code end
  end
  return nil
end

-- (the Travel tab's clickable town list and the Map tab moved to 3s-viking-sea;
-- its clicks run `vgo <town>`, the alias THIS plugin owns)

-- Icons on the Plan grid are a persisted preference: the toggle rebuilds the panel
-- in place (same title keeps position/size/tab; no input fields are lost - the
-- Trade/Auto inputs are re-seeded from state on every draw).
local icons_on = scrye.store.get("icons") ~= "0"
local build_panel   -- defined below, after the widget builders it needs

local function toggle_icons()
  icons_on = not icons_on
  scrye.store.set("icons", icons_on and "1" or "0")
  build_panel()
  scrye.print("[viking] plan icons " .. (icons_on and "ON" or "OFF") .. " (vicons toggles)")
end

-- ---------------------------------------------------- seconds counter
-- No os.time in the sandbox: count elapsed seconds ourselves (throttles/cooldowns).
local now_s = 0
local connected = true
scrye.onConnect(function() connected = true end)
scrye.onDisconnect(function() connected = false end)

-- ------------------------------------------------------ City Plan cache
-- The feed only publishes the MOST-RECENTLY placed building (CPB) plus a count
-- (CPLAN[3]); we accumulate placements ourselves and persist them.
local cp_placed = {}
local function cp_load()
  cp_placed = {}
  for e in (scrye.store.get("cp_placed") or ""):gmatch("[^\n]+") do
    local col, row, w, h, letter, name = e:match("^(%d+)|(%d+)|(%d+)|(%d+)|([^|]*)|(.*)$")
    if col then
      cp_placed[col .. "," .. row] = { col = tonumber(col), row = tonumber(row),
        w = tonumber(w), h = tonumber(h), letter = letter, name = name }
    end
  end
end
local function cp_save()
  local out = {}
  for _, b in pairs(cp_placed) do
    out[#out + 1] = table.concat({ b.col, b.row, b.w, b.h, b.letter, b.name }, "|")
  end
  scrye.store.set("cp_placed", table.concat(out, "\n"))
end
local function cp_count()
  local n = 0; for _ in pairs(cp_placed) do n = n + 1 end; return n
end
local function cp_accumulate(cpb)
  local changed = false
  for entry in (cpb or ""):gmatch("[^;]+") do
    local _, col, row, w, h, _, letter, name =
      entry:match("^([^|]*)|(%d+)|(%d+)|(%d+)|(%d+)|([^|]*)|([^|]*)|(.*)$")
    if col and not cp_placed[col .. "," .. row] then
      cp_placed[col .. "," .. row] = { col = tonumber(col), row = tonumber(row),
        w = tonumber(w), h = tonumber(h), letter = letter, name = name }
      changed = true
    end
  end
  if changed then cp_save() end
end
cp_load()

-- ------------------------------------------------------- dirty / flush
local dirty = {}
local flush_pending = false
local seen_keys = {}          -- feeds tab: every non-row vik.* key seen
local flush                    -- forward decl

local function schedule_flush()
  if flush_pending then return end
  flush_pending = true
  scrye.after(1, function() flush() end)
end
local function mark_all()
  for _, s in ipairs({ "stats", "city", "builds", "production", "people", "settlers",
                       "holds", "plan", "mission", "feeds" }) do
    dirty[s] = true
  end
end

-- ------------------------------------------------------ report builders
local function build_stats()
  local L = {}
  -- section headers ("-- Foo --") take the accent colour in every tab, for free
  local function add(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
  add("-- Vitals --")
  do
    -- cur/max pools from Guild.State points (gline1's H/S/V/R, structured)
    local function bar(label, v)
      if v == "" then return label .. " ?" end
      local cur, max = v:match("^(%d+)/(%d+)$")
      local pct = (tonumber(max) and tonumber(max) > 0) and (tonumber(cur) * 100 / tonumber(max)) or 100
      return label .. " " .. col(pctcol(pct, 80, 40), v)
    end
    add(bar("HP", gv("HP")) .. "   " .. bar("Seid", gv("SEID"))
      .. "   " .. bar("Vig", gv("VIG")) .. "   " .. bar("Rad", gv("RAD")))
  end
  add("")
  add("-- War --")
  add(string.format("God %s > %s", q("GOD_POWER"), q("GOD_POWER_FOCUS")))   -- no countdown: expires_at is a wall-clock epoch the sandbox cannot anchor
  add("Blot " .. q("BLOT"))
  do
    -- incoming raid (Guild.City raid block, server-side 28 Aug pm): who, how hard,
    -- and when. secs is a snapshot countdown; the line refreshes with the feed.
    local rd = split(gv("CRAID"), "|")
    if rd[1] and rd[1] ~= "" then
      local secs = tonumber(rd[3]) or 0
      local eta = secs >= 3600
        and string.format("%dh%02dm", math.floor(secs / 3600), math.floor(secs % 3600 / 60))
        or (math.floor(secs / 60) .. "m")
      add(colb("error", string.format("RAID %s (%s) in %s",
        (rd[2] and rd[2] ~= "") and rd[2] or "?", rd[1], eta)))
    end
  end
  add("")
  add("-- " .. q("LIN") .. "  GLvl " .. q("GLVL") .. " --")
  add(string.format("Sub %s   Daler %s", q("SUB"), q("DALER")))
  add(string.format("Rank %s   Renown %s", q("RANK"), q("RENOWN")))
  add(string.format("Missions: new %s reg %s   Tick %ss", q("VMNEW"), q("VMREG"), q("NEXTTICK")))
  local dc = split(gv("DCYCLE"), "|")
  local dch = tonumber(dc[2]) and (math.floor(num(dc[2]) / 3600) .. "h left") or ""
  add(string.format("Cycle %s   %s", dc[1] or "?", dch))
  local stfx = clean(gv("STFX")):gsub("[%[%]]", "")
  if stfx ~= "" then add("Effects: " .. stfx) end
  do
    -- the four gxp tracks off Guild.State.gxp: cur/threshold coloured by how close
    -- the advance is (>=75% reads success, which covers a topped path like buandi's
    -- millions past the threshold for free), and the last tick's gain when there
    -- was one, which is the number you actually watch move while playing.
    local g = split(gv("GXP"), ";")
    if g[1] and g[1] ~= "" then
      add("")
      add("-- GXP --")
      local function big(v)
        local n = tonumber(v); if not n then return "?" end
        local s = tostring(math.floor(n))
        while true do
          local a, b = s:gsub("^(%d+)(%d%d%d)", "%1,%2")
          s = a; if b == 0 then break end
        end
        return s
      end
      for _, e in ipairs(g) do
        local f = split(e, "|")
        local cur, max = tonumber(f[2]), tonumber(f[3])
        local last = tonumber(f[4]) or 0
        local name = (f[1] or "?"):gsub("^%l", string.upper)
        local pct = (cur and max and max > 0) and (cur * 100 / max) or 0
        local line = string.format("%-7s", name) .. " "
          .. col(pctcol(pct, 75, 25), big(f[2]) .. "/" .. big(f[3]))
        if last > 0 then line = line .. "  " .. col("info", "+" .. last) end
        add(line)
      end
    end
  end
  add("")
  add("-- Combat --")
  add("Fury " .. clean(gv("FURY")):sub(1, 12))
  add(string.format("Threk %s / %s   Chain %s   Depth %s", q("THREK"), q("MTHREK"), q("CHAIN"), q("BSDEPTH")))
  add("Rounds " .. q("RNDZ"))
  add("Last fight " .. (last_fight > 0 and tostring(last_fight) or "-"))
  add(string.format("Ledung %s / %s uses", q("LDNG"), q("MLDNG")))
  add("")
  add("-- Patrol --")
  if gv("PATROL") == "" then
    add("none on patrol")
  else
    local p = split(gv("PATROL"), "|")
    local mins = tonumber(p[2]) and (math.floor(num(p[2]) / 60) .. "m left") or ""
    add(string.format("Hirdmadrs %s   %s", p[1] or "?", mins))
  end
  return table.concat(L, "\n")
end

local function build_city()
  local L = {}
  -- section headers ("-- Foo --") take the accent colour in every tab, for free
  local function add(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
  add("-- Ships --")
  local ships = split(gv("SHIPS"), ";")
  if #ships == 0 then add("none")
  else
    -- Two columns: a fleet is mostly idle ships showing "0m", which wasted a whole line each.
    -- Docked ships are dimmed so the ones actually out stand out at a glance.
    local items = {}
    for i = 1, math.min(#ships, 16) do
      local f = split(ships[i], "|")
      local mins = tonumber(f[5]) and math.floor(num(f[5]) / 60) or nil
      local eta = mins and (mins .. "m") or ""
      local dest = f[4] or ""
      local raw = string.format("%-8s %-12s %4s", (f[1] or "?"):sub(1, 8), dest:sub(1, 12), eta)
      local out
      if dest == "" and (mins or 0) == 0 then
        out = col("dim", raw)                                    -- docked, nothing to see
      else
        out = padesc((f[1] or "?"):sub(1, 8), 8) .. " "
           .. col("info", string.format("%-12s", dest:sub(1, 12))) .. " "
           .. col("warning", string.format("%4s", eta))
      end
      items[#items + 1] = { raw = raw, txt = out }
    end
    two_col(items, 26, add)
  end
  add("")
  add("-- Carts --")
  local carts = split(gv("CARTS"), ";")
  if #carts == 0 then add("no carts out")
  else
    for i = 1, math.min(#carts, 5) do
      local f = split(carts[i], "|")
      local eta = f[4] and (math.floor(num(f[4]) / 60) .. "m") or "?"
      add(string.format("%-4s %-10s -> %-16s %5s  x%s", f[1] or "?", f[2] or "?", f[3] or "?", eta, f[5] or "?"))
    end
  end
  -- Refinery -> a barlist (label | caption | value | max | refined | tooltip | stages):
  -- fill = cur/max. refined = quality-weighted units = sum over stages of qty * pct/100;
  -- it is what the two-colour fallback splits on. The sixth field is the hover tooltip:
  -- the per-quality breakdown the MUSHclient miniwindow showed on its hotspots, best
  -- quality first ('\n' becomes a line break host-side).
  --
  -- The seventh field is the real thing: "qty,pct;qty,pct;..." RAWEST FIRST, so the host
  -- can draw one segment per quality stage -- width = how many units, colour = how far
  -- along the amber->green ramp that stage is. The single amber/green split was only ever
  -- an average; this shows the stages themselves. Hosts that predate the field ignore it
  -- and still get the two-colour bar from `refined`.
  local R = {}
  for _, r in ipairs(split(gv("REFINERY"), "|")) do
    local f = split(r, ":")
    if f[1] and f[1] ~= "" then
      local name = f[1]:gsub("_", " "):gsub("(%a)([%w]*)", function(a, b) return a:upper() .. b:lower() end)
      local cur, max = num(f[3]), num(f[4])
      local refined, stages = 0, {}
      for _, s in ipairs(split(f[5] or "", ";")) do
        local g = split(s, ",")
        if g[1] and g[1] ~= "" then
          local qty, pct = num(g[2]), num(g[3])
          refined = refined + qty * pct / 100
          if qty > 0 then stages[#stages + 1] = { qty = qty, pct = pct } end
        end
      end
      table.sort(stages, function(a, b) return a.pct > b.pct end)
      local tip = {}
      for _, st in ipairs(stages) do
        tip[#tip + 1] = string.format("%d units @ %d%%%s", st.qty, st.pct,
          st.pct >= 100 and " (refined)" or st.pct == 0 and " (raw)" or "")
      end
      if #tip == 0 then tip[1] = "empty" end
      -- stages walked backwards: `stages` is sorted best-first for the tooltip, the bar
      -- wants rawest-first so refining still reads left to right.
      local seg = {}
      for i = #stages, 1, -1 do
        seg[#seg + 1] = string.format("%d,%d", stages[i].qty, stages[i].pct)
      end
      R[#R + 1] = string.format("%s\tT%s %d/%d\t%d\t%d\t%d\t%s\t%s",
        name, f[2] or "?", cur, max, cur, max, math.floor(refined + 0.5),
        table.concat(tip, "\\n"), table.concat(seg, ";"))
    end
  end
  scrye.setState(P .. "refinery", table.concat(R, "\n"))
  return table.concat(L, "\n")
end

-- ======================================================================================
-- Build planner (merged in from the 3s-build plugin, which is now a deprecation stub).
--
-- It was always reading the same vik.* feed this panel reads and rendering the same
-- buildings with the same tier palette, so it was a second window onto one dataset. The
-- Builds tab now shows the planner rows INSTEAD of the old two-column tier grid: every
-- row already carries the current tier, so the grid was saying the same thing with less
-- information.
--
-- Ported as-is where it matters. It keeps its own split/num/comma rather than borrowing
-- this file's: this panel's split() drops a trailing empty field and its num() will not
-- read a digit out of "1,234 daler", and the planner's serialization and cost parsing
-- depend on the other behaviour. Two small functions is a cheaper price than a parsing
-- bug that only shows up on a building whose cost list happens to end in a separator.
-- ======================================================================================

local function bsplit(s, sep)
  local t = {}
  for part in (s .. sep):gmatch("([^" .. sep .. "]*)" .. sep) do t[#t + 1] = part end
  return t
end
local function bnum(s) return tonumber((tostring(s or ""):match("%-?%d+"))) or 0 end
local function comma(n)
  local s, sign = tostring(math.floor(n)), ""
  if s:sub(1, 1) == "-" then sign, s = "-", s:sub(2) end
  while true do
    local a, b = s:gsub("^(%d+)(%d%d%d)", "%1,%2")
    s = a; if b == 0 then break end
  end
  return sign .. s
end

local function bnote(s) scrye.print("@{accent,bold}[build]@{} " .. s) end

-- C(daler, {resource = amount, ...}) ; tier index = build/upgrade target tier
local function C(d, r) return { d = d, r = r or {} } end
-- Built-in fallback costs. A live 'build scan' replaces these with what the game says,
-- and the result is persisted, so these matter only on a fresh profile.
local BLD = {
  { key="warehouse",     name="Warehouse",       req={},
    cost={ C(1840,{timber=10}), C(6440,{iron=10,timber=25}), C(36800,{sunstone=20,iron=40,timber=100}),
           C(151800,{sunstone=40,iron=70,runestones=10,timber=180}), C(515200,{sunstone=80,iron=120,runestones=30,timber=300}) } },
  { key="trading_post",  name="Trading Post",    req={},
    cost={ C(460,{}), C(4600,{iron=15,timber=20}), C(27600,{sunstone=20,iron=50,runestones=10,timber=80}),
           C(115920,{sunstone=40,iron=90,runestones=20,timber=140}), C(404800,{sunstone=80,iron=140,runestones=40,timber=240}) } },
  { key="dock",          name="Dock",            req={},
    cost={ C(0,{}), C(4140,{timber=20}), C(23920,{iron=20,timber=80}),
           C(104880,{iron=40,timber=140}), C(368000,{iron=80,timber=240}) } },
  { key="lumber_yard",   name="Lumber Yard",     req={{"warehouse",1}},
    cost={ C(1104,{iron=5}), C(3680,{iron=15}), C(22080,{iron=60,timber=20}),
           C(96600,{iron=110,timber=40}), C(331200,{iron=180,timber=70}) } },
  { key="smithy",        name="Smithy",          req={{"warehouse",1}},
    cost={ C(1840,{timber=15}), C(5060,{iron=5,timber=25}), C(33120,{iron=40,timber=80}),
           C(138000,{iron=80,timber=140}), C(460000,{iron=140,timber=240}) } },
  { key="tannery",       name="Tannery",         req={{"warehouse",1}},
    cost={ C(1380,{timber=5}), C(4600,{furs=10,timber=15}), C(25760,{iron=20,furs=40,timber=60}),
           C(110400,{iron=40,furs=80,timber=110}), C(368000,{iron=70,furs=140,timber=180}) } },
  { key="fishery",       name="Fishery",         req={{"warehouse",1}},
    cost={ C(1104,{timber=10}), C(3680,{iron=5,timber=20}), C(22080,{iron=30,timber=80}),
           C(96600,{iron=60,timber=140}), C(331200,{iron=100,timber=240}) } },
  { key="farm",          name="Farm",            req={{"warehouse",1}},
    cost={ C(920,{timber=5}), C(3220,{grain=20,timber=15}), C(18400,{mead=20,grain=80,timber=60}),
           C(77280,{mead=40,grain=140,timber=110}), C(257600,{mead=70,grain=240,timber=180}) } },
  { key="brewery",       name="Brewery",         req={{"warehouse",1},{"farm",1}},
    cost={ C(2300,{grain=15}), C(7360,{timber=10,grain=30}), C(46000,{sunstone=10,mead=30,timber=40,grain=120}),
           C(193200,{sunstone=20,mead=60,timber=70,grain=200}), C(644000,{sunstone=40,timber=120,mead=100,grain=320}) } },
  { key="mead_cellar",   name="Mead Cellar",     req={{"brewery",1}},
    cost={ C(2760,{grain=10,timber=15}), C(9200,{iron=5,grain=25,timber=30}), C(55200,{sunstone=10,iron=20,grain=80,timber=90}),
           C(230000,{sunstone=20,iron=40,grain=140,timber=160}), C(736000,{sunstone=40,iron=70,grain=220,timber=260}) } },
  { key="longhouse",     name="Longhouse",       req={{"warehouse",1}},
    cost={ C(1840,{timber=20}), C(6440,{iron=10,timber=40}), C(40480,{sunstone=10,iron=40,timber=140}),
           C(165600,{sunstone=20,iron=80,timber=240}), C(552000,{sunstone=40,iron=140,timber=400}) } },
  { key="garrison",      name="Garrison",        req={{"longhouse",1}},
    cost={ C(1656,{iron=10,timber=15}), C(5520,{iron=20,timber=30}), C(33120,{sunstone=10,iron=70,timber=100}),
           C(138000,{sunstone=20,iron=120,timber=180}), C(460000,{sunstone=40,iron=200,timber=300}) } },
  { key="palisade",      name="Palisade",        req={{"warehouse",1}},
    cost={ C(1380,{timber=25}), C(5060,{iron=15,timber=50}), C(29440,{iron=60,timber=160}),
           C(124200,{iron=110,timber=280}), C(423200,{iron=180,timber=460}) } },
  { key="watchtower",    name="Watchtower",      req={{"palisade",1}},
    cost={ C(1104,{iron=5,timber=10}), C(3680,{iron=10,timber=20}), C(22080,{sunstone=10,iron=40,timber=70}),
           C(96600,{sunstone=20,iron=70,timber=120}), C(331200,{sunstone=40,iron=120,timber=200}) } },
  { key="mead_hall",     name="Mead-Hall",       req={{"longhouse",1}},
    cost={ C(2300,{timber=15,grain=20}), C(8280,{timber=25,mead=10,grain=40}), C(51520,{sunstone=20,timber=80,mead=50,grain=120}),
           C(215280,{sunstone=40,mead=100,timber=140,grain=200}), C(717600,{sunstone=80,mead=160,timber=240,grain=320}) } },
  { key="thrall_pen",    name="Thrall Pen",      req={{"longhouse",1}},
    cost={ C(1656,{iron=10,timber=15}), C(5520,{iron=20,timber=30}), C(33120,{sunstone=10,iron=70,timber=100}),
           C(138000,{sunstone=20,iron=120,timber=180}), C(460000,{sunstone=40,iron=200,timber=300}) } },
  { key="muster_ground", name="Muster Ground",   req={{"garrison",1}},
    cost={ C(2300,{iron=10,timber=20}), C(7360,{iron=20,timber=40}), C(46000,{sunstone=10,iron=60,timber=120}),
           C(193200,{sunstone=20,iron=110,timber=200}), C(644000,{sunstone=40,iron=180,timber=320}) } },
  { key="settler_plots", name="Settler Plots",   req={{"warehouse",1}},
    cost={ C(2760,{iron=5,timber=20}), C(11040,{iron=15,grain=20,timber=40}), C(64400,{iron=60,mead=20,grain=80,timber=140}),
           C(248400,{iron=110,grain=140,mead=40,timber=240}), C(736000,{iron=180,grain=240,mead=70,timber=400}) } },
  { key="well",          name="Well",            req={{"warehouse",1}},
    cost={ C(736,{iron=5,timber=5}), C(2300,{iron=10,timber=15}), C(12880,{iron=40,timber=50}),
           C(55200,{iron=70,timber=80}), C(184000,{iron=120,timber=140}) } },
  { key="salting_house", name="Salting House",   req={{"fishery",1}},
    cost={ C(2760,{iron=10,timber=15}), C(9200,{iron=20,timber=30}), C(55200,{sunstone=10,iron=45,timber=90}),
           C(230000,{sunstone=20,iron=90,timber=160}), C(736000,{sunstone=40,iron=150,timber=260}) } },
  { key="bakehouse",     name="Bakehouse",       req={{"farm",1}},
    cost={ C(2760,{iron=10,timber=15}), C(9200,{iron=20,timber=30}), C(55200,{sunstone=10,iron=45,timber=90}),
           C(230000,{sunstone=20,iron=90,timber=160}), C(736000,{sunstone=40,iron=150,timber=260}) } },
  { key="furriers_lodge",name="Furrier's Lodge", req={{"tannery",1}},
    cost={ C(2760,{iron=10,timber=15}), C(9200,{iron=20,timber=30}), C(55200,{sunstone=10,iron=45,timber=90}),
           C(230000,{sunstone=20,iron=90,timber=160}), C(736000,{sunstone=40,iron=150,timber=260}) } },
  { key="mine",          name="Mine",            req={{"warehouse",1}},
    cost={ C(1288,{iron=6,timber=12}), C(4416,{iron=15,timber=26}), C(24840,{sunstone=8,iron=35,timber=75}),
           C(105800,{sunstone=16,iron=70,timber=135}), C(349600,{sunstone=32,iron=120,timber=220}) } },
  { key="smelter",       name="Smelter",         req={{"mine",1}},
    cost={ C(2760,{iron=10,timber=15}), C(9200,{iron=20,timber=30}), C(55200,{sunstone=10,iron=45,timber=90}),
           C(230000,{sunstone=20,iron=90,timber=160}), C(736000,{sunstone=40,iron=150,timber=260}) } },
}

local plan = BLD          -- active cost table; replaced by a live 'vbuild list' scan when available
local NAME = {}
local function rebuild_names() NAME = {}; for _, b in ipairs(plan) do NAME[b.key] = b.name end end
rebuild_names()

-- preferred order for listing resources; any others are appended alphabetically
local RESORDER = { "iron", "timber", "sunstone", "runestones", "furs", "fine_furs", "grain", "mead", "honey", "gemstones" }
local function res_keys(r)
  local out, seen = {}, {}
  for _, k in ipairs(RESORDER) do if r[k] then out[#out + 1] = k; seen[k] = true end end
  local extra = {}
  for k in pairs(r) do if not seen[k] then extra[#extra + 1] = k end end
  table.sort(extra)
  for _, k in ipairs(extra) do out[#out + 1] = k end
  return out
end

-- plan serialization: one building per line; TAB-separated fields:
--   key \t name \t req(k=t,k=t) \t tier:daler:res=amt,res=amt \t ...
local function serialize_plan(p)
  local lines = {}
  for _, b in ipairs(p) do
    local req = {}
    for _, r in ipairs(b.req) do req[#req + 1] = r[1] .. "=" .. r[2] end
    local parts = { b.key, b.name, table.concat(req, ",") }
    local tiers = {}
    for t in pairs(b.cost) do tiers[#tiers + 1] = t end
    table.sort(tiers)
    for _, t in ipairs(tiers) do
      local c = b.cost[t]
      local rs = {}
      for k, v in pairs(c.r) do rs[#rs + 1] = k .. "=" .. v end
      table.sort(rs)
      parts[#parts + 1] = t .. ":" .. c.d .. ":" .. table.concat(rs, ",")
    end
    lines[#lines + 1] = table.concat(parts, "\t")
  end
  return table.concat(lines, "\n")
end

local function deserialize_plan(str)
  local p = {}
  for _, line in ipairs(bsplit(str or "", "\n")) do
    if line ~= "" then
      local f = bsplit(line, "\t")
      if f[1] and f[1] ~= "" and f[2] and f[2] ~= "" then
        local b = { key = f[1], name = f[2], req = {}, cost = {} }
        if f[3] and f[3] ~= "" then
          for _, part in ipairs(bsplit(f[3], ",")) do
            local k, t = part:match("^(.-)=(%d+)$")
            if k and k ~= "" then b.req[#b.req + 1] = { k, tonumber(t) } end
          end
        end
        for i = 4, #f do
          local t, d, rs = f[i]:match("^(%d+):(%d+):(.*)$")
          if t then
            local c = { d = tonumber(d) or 0, r = {} }
            for _, part in ipairs(bsplit(rs, ",")) do
              local k, a = part:match("^(.-)=(%d+)$")
              if k and k ~= "" then c.r[k] = tonumber(a) end
            end
            b.cost[tonumber(t)] = c
          end
        end
        p[#p + 1] = b
      end
    end
  end
  return p
end

-- restore scanned plan + toggle from the store. Note these now live under the
-- 3s-viking-status store rather than 3s-build's, so the first run after the merge
-- re-scans; 'build scan' on connect handles that without being asked.
local show_max = (scrye.store.get("bp_showmax") == "1")
do
  local saved = scrye.store.get("bp_plan")
  if saved and saved ~= "" then
    local ok, p = pcall(deserialize_plan, saved)
    if ok and p and #p > 0 then plan = p; rebuild_names() end
  end
end

-- returns rows (sorted for display) + current daler
local function compute()
  local daler = bnum(gv("DALER"))
  -- warehouse stock, summed per good
  local stock = {}
  for _, e in ipairs(bsplit(gv("WSTOCK") or "", ";")) do
    local f = bsplit(e, "|")
    -- f[2] required: WSTOCK's leading field is the warehouse capacity, not a good.
    if f[1] and f[1] ~= "" and f[2] then stock[f[1]] = (stock[f[1]] or 0) + bnum(f[2]) end
  end
  -- current tiers
  local tier = {}
  for _, e in ipairs(bsplit(gv("BUILDINGS") or "", ",")) do
    local k, t = e:match("^(.-):(%d+)$")
    if k then tier[k] = tonumber(t) end
  end
  -- in-progress builds (normalise name -> key form)
  local building = {}
  for _, e in ipairs(bsplit(gv("BUILDS") or "", ";")) do
    local nm = (bsplit(e, "|")[1] or ""):lower():gsub("[%s%-]", "_")
    if nm ~= "" then building[nm] = true end
  end

  -- match a scanned building key to the game's feed key, tolerating a dropped possessive 's'
  -- (display "Goldsmith's" -> goldsmiths but the feed uses "goldsmith").
  local function feed_key(k)
    if tier[k] ~= nil or building[k] then return k end
    local ks = k:gsub("s(_)", "%1"):gsub("s$", "")
    if tier[ks] ~= nil or building[ks] then return ks end
    for fk in pairs(tier)     do if ks:sub(1, #fk + 1) == fk .. "_" then return fk end end
    for fk in pairs(building) do if ks:sub(1, #fk + 1) == fk .. "_" then return fk end end
    return k
  end

  local rows = {}
  for _, b in ipairs(plan) do
    local cur   = tier[feed_key(b.key)] or 0
    local nextt = cur + 1
    local row = { b = b, cur = cur, nextt = nextt }

    if cur >= 5 then
      row.cat, row.locked = 4, "MAX (T5)"
    elseif building[feed_key(b.key)] then
      row.cat, row.locked = 3, "building..."
    else
      local unmet = {}
      for _, p in ipairs(b.req) do
        if (tier[feed_key(p[1])] or 0) < p[2] then unmet[#unmet + 1] = (NAME[p[1]] or p[1]) .. " T" .. p[2] end
      end
      local c = b.cost[nextt]
      row.cost = c
      if #unmet > 0 then
        row.cat, row.locked = 2, "needs " .. table.concat(unmet, ", ")
      elseif not c then
        row.cat, row.locked = 2, "no cost data (build scan)"
      else
        local toks = {}
        local dok  = daler >= c.d
        toks[#toks + 1] = { text = comma(c.d) .. "d", ok = dok }
        local allok, missing = dok, (dok and 0 or 1)
        for _, res in ipairs(res_keys(c.r)) do
          local need = c.r[res]
          if need then
            local ok = (stock[res] or 0) >= need
            toks[#toks + 1] = { text = need .. " " .. res, ok = ok }
            if not ok then allok = false; missing = missing + 1 end
          end
        end
        row.toks, row.buildable = toks, allok
        row.cat, row.missing, row.dcost = (allok and 0 or 1), missing, c.d
      end
    end
    rows[#rows + 1] = row
  end

  -- buildable first, then closest-to-affordable (fewest missing, then cheapest)
  table.sort(rows, function(a, b)
    if a.cat ~= b.cat then return a.cat < b.cat end
    if a.cat == 1 and a.missing ~= b.missing then return a.missing < b.missing end
    local ad, bd = a.dcost or (a.cost and a.cost.d) or 0, b.dcost or (b.cost and b.cost.d) or 0
    if ad ~= bd then return ad < bd end
    return a.b.name < b.b.name
  end)
  return rows, daler
end

local function cost_str(c)
  local parts = { comma(c.d) .. "d" }
  for _, res in ipairs(res_keys(c.r)) do
    if c.r[res] then parts[#parts + 1] = c.r[res] .. " " .. res end
  end
  return table.concat(parts, " +")
end

-- last rendered planner rows, so the "build" command can echo the tab into the output
-- window without recomputing and without the section header.
local planner_lines = {}

local function build_builds()
  local L = {}
  -- section headers ("-- Foo --") take the accent colour in every tab, for free
  local function add(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end

  local ok, rows, daler = pcall(compute)
  if not ok then rows, daler = {}, bnum(gv("DALER")) end

  add(string.format("-- Buildings   Daler %s   %s --",
      comma(daler), show_max and "[all]" or "[+max]"))

  -- battle scars (Guild.City bdmg, server-side 28 Aug pm): buildings carrying
  -- damage, worth seeing before deciding what to spend on next
  do
    local dmg = gv("BDMG")
    if dmg ~= "" then
      local parts = {}
      for id, pct in dmg:gmatch("([^:,]+):(%d+)") do
        parts[#parts + 1] = id:gsub("_", " ") .. " " .. pct .. "%"
      end
      if #parts > 0 then add(colb("error", "Damaged: " .. table.concat(parts, ", "))) end
    end
  end

  -- filter maxed unless show_max
  local shown = {}
  for _, r in ipairs(rows) do if show_max or r.cat ~= 4 then shown[#shown + 1] = r end end

  local plines = {}
  for _, r in ipairs(shown) do
    local mark, body
    if r.cat == 4 then
      mark, body = "@{accent,bold}max@{}", "@{dim}" .. esc(r.locked) .. "@{}"
    elseif r.cat == 3 then
      mark, body = "@{info,bold}wip@{}", "@{dim}" .. esc(r.locked) .. "@{}"
    elseif r.cat == 2 then
      mark, body = "@{dim}req@{}", "@{dim}" .. esc(r.locked) .. "@{}"
    else
      mark = r.buildable and "@{success,bold}OK @{}" or "   "   -- short: the red says it
      local toks = {}
      for _, t in ipairs(r.toks) do
        -- every cost token is coloured: green when you can afford it, red+bold when short
        -- (bold is the colour-blind fallback)
        toks[#toks + 1] = t.ok
          and ("@{success}" .. esc(t.text) .. "@{}")
          or  ("@{error,bold}" .. esc(t.text) .. "@{}")
      end
      body = table.concat(toks, "  ")
    end
    local tierstr = (r.nextt <= 5) and string.format("T%d>%d", r.cur, r.nextt) or ("T" .. r.cur)
    local tc = tiercol(r.nextt <= 5 and r.nextt or r.cur)
    plines[#plines + 1] = string.format("%s %s @{%s}%s@{} %s",
      mark, padesc(r.b.name, 15), tc, padesc(tierstr, 5), body)
  end
  planner_lines = plines

  if #plines == 0 then add("no buildings known yet - run 'build scan'")
  else
    for _, l in ipairs(plines) do add(l) end
    if gv("WSTOCK"):find(";%a") == nil then   -- same goods-rows test the auto-trader guard uses
      add("")
      add(col("warning", "no stock data - resource costs shown as short ('vstock' scans the warehouse)"))
    end
  end

  -- what the yard is actually working on right now, kept from the original tab
  local builds = split(gv("BUILDS"), ";")
  if #builds > 0 then
    add("")
    for i = 1, math.min(#builds, 3) do
      local f = split(builds[i], "|")
      local secs = num(f[5])
      local rem = f[5] and (secs >= 3600
          and string.format("%dh %dm left", math.floor(secs / 3600), math.floor((secs % 3600) / 60))
          or (math.floor(secs / 60) .. "m left")) or "?"
      add(string.format("constr: %-16s %s", f[1] or "?", rem))
    end
  end

  -- how many are affordable right now, for the value line above the text
  local ready = 0
  for _, r in ipairs(shown) do if r.cat == 0 and r.buildable then ready = ready + 1 end end
  scrye.setState(P .. "buildsummary", string.format("%d ready  |  %s daler", ready, comma(daler)))

  return table.concat(L, "\n")
end

-- ---------------- live 'vbuild list' scan (keeps costs in sync with the game) ----------
local KNOWN = { timber = true, iron = true, sunstone = true, runestones = true, furs = true,
                fine_furs = true, grain = true, mead = true, gemstones = true, honey = true }
local function nkey(s)
  s = tostring(s or "")
  s = s:gsub("^%s+", ""):gsub("%s+$", "")
  s = s:lower():gsub("'", ""):gsub("[%s%-]+", "_")
  return s
end
local function parse_req(txt)
  local req = {}
  if not txt or txt:find("none") then return req end
  for part in (txt .. ","):gmatch("%s*(.-)%s*,") do
    local nm, t = part:match("^(.-)%s+tier%s+(%d+)")
    if nm and nm ~= "" then req[#req + 1] = { nkey(nm), tonumber(t) } end
  end
  return req
end

local bp_parse, bp_cur, bp_cur_tier, bp_scanning = {}, nil, nil, false
local scan_active = false          -- emulates the enabled/disabled "vblist" trigger group
local scan_timer = nil

-- redraw through the panel's own dirty/flush cycle rather than setting state directly,
-- so a scan finishing coalesces with whatever feed traffic arrives alongside it
local function bp_redraw() dirty.builds = true; schedule_flush() end

local function bp_scan_done()
  if not bp_scanning and not scan_active then return end
  bp_scanning, scan_active = false, false
  if scan_timer then scrye.cancel(scan_timer); scan_timer = nil end
  -- drop any unnamed placeholder entries (a building whose header we couldn't read)
  local clean_rows = {}
  for _, b in ipairs(bp_parse) do if b.key ~= "" and b.name ~= "?" then clean_rows[#clean_rows + 1] = b end end
  bp_parse = clean_rows
  if #bp_parse > 0 then
    plan = bp_parse
    rebuild_names()
    scrye.store.set("bp_plan", serialize_plan(plan))   -- persist scanned costs
    bnote("scanned " .. #bp_parse .. " buildings from vbuild list")
  else
    bnote("vbuild list scan found nothing - using built-in costs")
  end
  bp_redraw()
end

-- called per decorated line during a scan; runs a small state machine
local function bp_scan_line(cap)
  -- drop the closing *~- marker (and anything after it) before trimming; on a wrapped
  -- header line the marker is absent, which is fine.
  local c = (cap or ""):gsub("%*~%-.*$", ""):gsub("^%s+", ""):gsub("%s+$", "")
  if c == "" then return end
  if c:find("Available Buildings") then bp_parse, bp_cur, bp_cur_tier, bp_scanning = {}, nil, nil, true; return end
  if not bp_scanning then return end
  if c:match("^Commands") or c:find("'vbuild") then bp_scan_done(); return end
  -- building header:  "<Name>   Req: <req>"
  local nm, req = c:match("^(.-)%s+Req:%s*(.*)$")
  if nm and nm ~= "" then
    bp_cur = { key = nkey(nm), name = nm, req = parse_req(req), cost = {} }
    bp_parse[#bp_parse + 1] = bp_cur; bp_cur_tier = nil
    return
  end
  -- tier line:  "Tier N: X daler"
  local tn, dal = c:match("^Tier (%d+):%s*([%d,]+)%s*daler")
  if tn and bp_cur then
    local t = tonumber(tn)
    -- safety net: if tiers stop increasing, a new building's header was missed -- start a
    -- fresh (unnamed) entry rather than overwriting the current building's costs.
    if bp_cur_tier and t <= bp_cur_tier then
      bp_cur = { key = "", name = "?", req = {}, cost = {} }
      bp_parse[#bp_parse + 1] = bp_cur
    end
    bp_cur_tier = t
    bp_cur.cost[bp_cur_tier] = { d = tonumber((dal:gsub(",", ""))) or 0, r = {} }
    return
  end
  -- resource line:  "+ 10 Iron, 25 Timber"  (or a bare continuation line when a cost wraps).
  if bp_cur and bp_cur_tier and bp_cur.cost[bp_cur_tier] then
    local body = c:match("^%+%s*(.+)$")
    if not body and c:match("^%d") then            -- wrapped continuation of a resource list
      local okc = true
      for item in (c .. ","):gmatch("%s*(.-)%s*,") do
        local a, m = item:match("^(%d+)%s+([%a][%a ]*)$")
        if not (a and KNOWN[nkey(m)]) then okc = false; break end
      end
      if okc then body = c end
    end
    if body then
      for item in (body .. ","):gmatch("%s*(.-)%s*,") do
        local a, m = item:match("^(%d+)%s+([%a][%a ]*)$")
        if a and m then bp_cur.cost[bp_cur_tier].r[nkey(m)] = tonumber(a) end
      end
    end
  end
end

local function bp_scan()
  bp_parse, bp_cur, bp_cur_tier, bp_scanning = {}, nil, nil, true
  scan_active = true
  scrye.send("vbuild list")
  if scan_timer then scrye.cancel(scan_timer) end
  scan_timer = scrye.after(8, bp_scan_done)   -- safety finalize if the closing line is missed
end

-- captures 'vbuild list' output (decorated -~* ... *~- lines); active only during a scan.
-- Match on the opening -~* marker alone: a long header line can wrap so its closing *~-
-- falls on the next physical line; requiring both markers would drop that header.
scrye.addTrigger{
  pattern = [[^-~\*(.*)]],
  regex   = true,
  run     = function(cap)
    if not scan_active then return end
    pcall(bp_scan_line, cap)
  end,
}

-- gag the decorated lines while a scan is active
scrye.onLine(function(line)
  if scan_active and line:match("^%-~%*") then return false end
end)

local function bp_toggle_all()
  show_max = not show_max
  scrye.store.set("bp_showmax", show_max and "1" or "0")
  bnote(show_max and "showing maxed buildings" or "hiding maxed buildings")
  bp_redraw()
end

-- print the planner to the output window (the tab is the primary view; this is for
-- when the panel is closed or you want it in the scrollback)
local function bp_print()
  scrye.setState(P .. "builds", build_builds())
  for _, l in ipairs(planner_lines) do bnote(l) end
  bnote("commands: build all | build refresh | build scan | build start <name>")
end

-- "build start <name>": affordability-checked replacement for click-to-build
local function bp_start(arg)
  local k = nkey(arg)
  if k == "" then bnote("usage: build start <building>"); return end
  local rows = compute()
  local target
  for _, r in ipairs(rows) do
    if r.b.key == k or nkey(r.b.name) == k then target = r; break end
  end
  if not target then bnote("unknown building: " .. arg); return end
  if target.cat == 4 then bnote(target.b.name .. " is already MAX (T5)"); return end
  if target.cat == 3 then bnote(target.b.name .. " is already building"); return end
  if target.cat == 2 then bnote(target.b.name .. ": " .. target.locked); return end
  if not target.buildable then
    bnote("cannot afford " .. target.b.name .. " T" .. target.nextt ..
          (target.cost and (":  " .. cost_str(target.cost)) or ""))
    return
  end
  scrye.send("vbuild start " .. target.b.key)
  bnote("vbuild start " .. target.b.key)
  scrye.after(2, bp_redraw)   -- refresh after the build registers
end

scrye.addAlias{ pattern = [[^build$]],            regex = true, run = function() bp_print() end }
scrye.addAlias{ pattern = [[^build all$]],        regex = true, run = function() bp_toggle_all() end }
scrye.addAlias{ pattern = [[^build refresh$]],    regex = true, run = function() bp_redraw() end }
scrye.addAlias{ pattern = [[^build scan$]],       regex = true, run = function() bp_scan() end }
scrye.addAlias{ pattern = [[^build start (.+)$]], regex = true, run = function(w1) bp_start(w1) end }

-- re-scan costs on connect, after a delay so we're logged in first
scrye.onConnect(function() scrye.after(20, bp_scan) end)

local function build_production()
  local L = {}
  -- section headers ("-- Foo --") take the accent colour in every tab, for free
  local function add(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
  add("-- Production / tick --")
  local prod = {}
  for pair in gv("PRODUCTION"):gmatch("[^,]+") do
    local r, a = pair:match("^(%a+):(%-?%d+)$")
    if r then prod[#prod + 1] = { r = r, a = tonumber(a) } end
  end
  if #prod == 0 then add("no data")
  else
    table.sort(prod, function(x, y) return x.r < y.r end)
    local rows = math.ceil(#prod / 2)
    -- the original tinted the delta green when producing, red when draining
    local function pcell(e, width)
      if not e then return "" end
      local name = (e.r:gsub("^%l", string.upper))
      local amt = (e.a >= 0 and "+" or "") .. e.a
      local raw = string.format("%-9s %s", name, amt)
      local padding = width and string.rep(" ", math.max(0, width - #raw)) or ""
      return padesc(name, 9) .. " " .. col(e.a >= 0 and "success" or "error", amt) .. padding
    end
    for i = 1, rows do
      add(pcell(prod[i], 24) .. " " .. pcell(prod[i + rows]))
    end
  end
  add("")
  add("-- Routes --")
  if gv("ROUTES") == "" then add("none")
  else
    local rlist = {}
    for _, r in ipairs(split(gv("ROUTES"), ";")) do
      local f = split(r, "|")
      if f[2] then
        local road = not (f[7] or "No"):find("No")
        local fort = not (f[8] or "No"):find("No")
        -- the original coloured these green / yellow / grey; same three states, as tokens
        -- the original coloured these green / yellow / grey; same three states, as tokens
        local mkraw = (road and fort) and "[road+fort]" or (road and "[road]")
                   or (fort and "[fort]") or "[none]"
        local mk = (road and fort) and colb("success", mkraw)
                or (road and col("success", mkraw))
                or (fort and col("warning", mkraw))
                or col("dim", mkraw)
        local name = f[2]:sub(1, 12)
        rlist[#rlist + 1] = {
          sort = name,
          raw  = string.format("%-14s%s", name, mkraw),
          txt  = padesc(name, 14) .. mk,
        }
      end
    end
    table.sort(rlist, function(a, b) return a.sort < b.sort end)
    two_col(rlist, 26, add)      -- 14 towns was 14 lines; now 7
  end
  add("")
  -- warehouse with freshness
  --
  -- WSTOCK opens with a bare number before the goods -- "7367;furs|301|100;mead|60|100|aged"
  -- -- and that number is the warehouse's EFFECTIVE capacity. Two bugs came out of not knowing
  -- that: it rendered as a stock row called "7367" holding 0 at q100%, and the capacity shown
  -- beside the total came from the tier table below.
  --
  -- That table is not wrong, which is why it is still here: it is the BASE capacity per
  -- warehouse tier, and it is exactly right for a character who has raised none of the skills
  -- that increase storage. Raise one and the server's number climbs above it -- a tier-5 hold
  -- reads 5250 from the table and 7367 from the feed. So the feed wins whenever it is present,
  -- and the table serves as the floor before WSTOCK has arrived. Do not delete it.
  local wb = gv("BUILDINGS") .. ","
  local tier = wb:match("warehouse:(%d+),") or "1"
  local wmax = tonumber(split(gv("WSTOCK"), ";")[1] or "")
             or ({ ["1"] = 400, ["2"] = 1000, ["3"] = 1750, ["4"] = 3000, ["5"] = 5250 })[tier]
             or 400
  local byg, tot = {}, 0
  for _, e in ipairs(split(gv("WSTOCK"), ";")) do
    local f = split(e, "|")
    -- A real stock record always carries an amount; the capacity field does not. Keying off
    -- that rather than off the name means no list of non-goods has to be maintained.
    if f[1] and f[2] and f[1] ~= "amber" then
      local amt, qq = num(f[2]), tonumber(f[3]) or 100
      local g = byg[f[1]]
      if not g then g = { amt = 0, qsum = 0, minq = 100, stale = 0, grades = {} }; byg[f[1]] = g end
      g.amt = g.amt + amt
      g.qsum = g.qsum + qq * amt
      if qq < 100 then
        g.stale = g.stale + amt
        if qq < g.minq then g.minq = qq end
      end
      -- field 4: the quality grade NAME; field 3 rode along as 100 from the text
      -- scan, but the Guild.Warehouse feed (28 Aug pm) puts the REAL pct there
      if f[4] and f[4] ~= "" then g.grades[#g.grades + 1] = { grade = f[4], amt = amt, pct = qq } end
      tot = tot + amt
    end
  end
  local stock = {}
  for good, g in pairs(byg) do stock[#stock + 1] = { good = good, g = g } end
  table.sort(stock, function(a, b) return a.good < b.good end)
  add(string.format("-- Warehouse  %d / %d --", tot, wmax))
  if #stock == 0 then
    add(col("warning", "no stock data yet - Guild.Warehouse fills this in; Refresh below scans as fallback"))
  else
    for _, f in ipairs(stock) do
      local g = f.g
      if #g.grades > 0 then
        -- grade NAMES shown straight; when the FEED supplied a real pct under 100
        -- it rides along ("251 stale 78%") and forces warning - scanned stock only
        -- ever says 100 there, so its rendering is unchanged (words still colour)
        local parts = {}
        for _, e in ipairs(g.grades) do
          local low = (e.pct or 100) < 100
          local tok = (low or e.grade:find("stale") or e.grade:find("old")) and "warning" or "dim"
          parts[#parts + 1] = col(tok, e.amt .. " " .. e.grade .. (low and (" " .. e.pct .. "%") or ""))
        end
        add(padesc(f.good:gsub("_", " "), 12) .. " " .. esc(string.format("%4d", g.amt))
            .. "  " .. table.concat(parts, ", "))
      else
        local avgq = g.amt > 0 and math.floor(g.qsum / g.amt + 0.5) or 100
        local st = g.stale > 0
          and ("  " .. col(pctcol(g.minq, 95, 80), string.format("stale %d@%d%%", g.stale, g.minq)))
          or ""
        add(padesc(f.good:gsub("_", " "), 12) .. " " .. esc(string.format("%4d", g.amt)) .. "  "
            .. col(pctcol(avgq, 95, 80), string.format("q%3d%%", avgq)) .. st)
      end
    end
  end
  return table.concat(L, "\n")
end

local function build_people()
  local L = {}
  -- section headers ("-- Foo --") take the accent colour in every tab, for free
  local function add(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
  add("-- Forces --")
  add(string.format("Thralls %s   Followers %s", q("THRALLS"), q("THRALL_FOLLOWER"):sub(1, 30)))
  add(string.format("Garrison %s   Threk %s", q("GARRISON"), q("MTHREK")))
  add("")
  add("-- Hird Guard " .. (gv("HIRDCAP") ~= "" and ("(" .. gv("HIRDCAP") .. ") ") or "") .. "--")
  local hird = split(gv("HIRD"), ";")
  if #hird == 0 then add("no hird")
  else
    for i = 1, math.min(#hird, 10) do
      local f = split(hird[i], "|")
      add(string.format("%-16s %s/%s/%s/%s  %s  %s",
        f[2] or "?", f[4] or "?", f[5] or "?", f[6] or "?", f[7] or "?", f[9] or "?", f[10] or "?"))
    end
  end
  add("")
  add("-- Staff --")
  local staff = split(gv("STAFF"), ";")
  if #staff == 0 then add("no staff hired")
  else
    add(string.format("%-16s %-13s %-4s%-4s%-4s%-4s%-4s%-4s%-4s",
      "", "", "Cbt", "Trd", "Cft", "Sea", "Wld", "Lnd", "Chm"))
    for i = 1, math.min(#staff, 10) do
      local f = split(staff[i], "|")
      local s = split(f[4] or "", ",")
      add(string.format("%-16s %-13s %-4s%-4s%-4s%-4s%-4s%-4s%-4s",
        f[1] or "?", f[2] or "?",
        s[1] or "?", s[2] or "?", s[3] or "?", s[4] or "?", s[5] or "?", s[6] or "?", s[7] or "?"))
    end
  end
  add("")
  add("-- Bonds --")
  local bonds = split(gv("BONDS"), ";")
  if #bonds == 0 then add("no bonds")
  else
    local name = {}
    for _, h in ipairs(split(gv("HIRD"), ";")) do
      local f = split(h, "|")
      if f[1] and f[2] then name[f[1]] = (f[2]:match("^(%S+)") or f[2]) end
    end
    local m, ids, seen = {}, {}, {}
    for _, b in ipairs(bonds) do
      local f = split(b, "|")
      local a, c, val = f[1], f[2], f[4] or "?"
      if a and c then
        m[a] = m[a] or {}; m[a][c] = val
        m[c] = m[c] or {}; m[c][a] = val
        if not seen[a] then seen[a] = true; ids[#ids + 1] = a end
        if not seen[c] then seen[c] = true; ids[#ids + 1] = c end
      end
    end
    table.sort(ids, function(x, y) return (tonumber(x) or 0) < (tonumber(y) or 0) end)
    local hdr = string.rep(" ", 11)
    for _, c in ipairs(ids) do hdr = hdr .. string.format("%-4s", c) end
    add(hdr)
    for _, a in ipairs(ids) do
      local row = string.format("%-2s%-9s", a, (name[a] or ("#" .. a)):sub(1, 8))
      for _, c in ipairs(ids) do
        if a == c then row = row .. string.format("%-4s", "-")
        else row = row .. string.format("%-4s", (m[a] and m[a][c]) or ".") end
      end
      add(row)
    end
  end
  return table.concat(L, "\n")
end

local function build_settlers()
  local L = {}
  -- section headers ("-- Foo --") take the accent colour in every tab, for free
  local function add(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
  add("-- Settlement --")
  add(string.format("Blot %s   Sproj %s", q("BLOT"):sub(1, 14), (gv("SPROJ") ~= "" and gv("SPROJ")) or "-"))
  add("")
  -- fed from Guild.Settlement's settlerx/settlers blocks (the SEVENTS tick-report
  -- text this section used to parse has no GMCP equivalent - the numbers do, richer)
  local pop, mood, sentiment = gv("SPOP"), tonumber(gv("SMOOD")), tonumber(gv("SSENT"))
  add("-- Settlers --")
  add("Population " .. esc(pop ~= "" and pop or "?")
      .. "   Mood " .. col(mood and pctcol(mood, 70, 40) or "dim",
                           mood and (mood .. "/100") or "?")
      .. "   Sentiment " .. col(sentiment and (sentiment >= 0 and "success" or "error") or "dim",
                                gv("SSENT") ~= "" and gv("SSENT") or "?"))
  if gv("SWATER") ~= "" then
    add("Water reserve " .. esc(gv("SWATER")))
  end
  add("")
  add("-- Economy --")
  local tax, upkeep, net = tonumber(gv("STAX")), tonumber(gv("SUPK")), tonumber(gv("SNET"))
  if tax then
    add(string.format("Income  +%d d/tick (tax)", tax))
  end
  if upkeep then
    add(string.format("Upkeep  %d d/tick%s", upkeep,
      net and string.format("   Net %s%d", net >= 0 and "+" or "", net) or ""))
  end
  local nextt = tonumber(gv("NEXTTICK"))
  if nextt then
    local h, m = math.floor(nextt / 3600), math.floor((nextt % 3600) / 60)
    add("Next tick in " .. (h > 0 and (h .. "h " .. m .. "min") or (m .. "min")))
  end
  add("")
  add("-- Civic --")
  local civ = {}
  for entry in gv("SCIVICS"):gmatch("[^;]+") do
    local name, lvl = entry:match("^(.-):(%d+)$")
    if name then civ[#civ + 1] = titlecase(name) .. " T" .. lvl end
  end
  if #civ == 0 then add("none")
  else
    table.sort(civ)
    local rows = math.ceil(#civ / 2)
    for i = 1, rows do
      add(string.format("%-22s %s", civ[i] or "", civ[i + rows] or ""))
    end
  end
  add("")
  add("-- Consumption (per cycle) --")
  local cons = {}
  for entry in gv("SCONSUME"):gmatch("[^;]+") do
    local res, amt = entry:match("^(.-):(%-?%d+)$")
    if res then cons[#cons + 1] = res .. " " .. amt end
  end
  add(#cons > 0 and table.concat(cons, "   ") or "none")
  return table.concat(L, "\n")
end

local function build_holds()
  local L = {}
  -- section headers ("-- Foo --") take the accent colour in every tab, for free
  local function add(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
  add("-- Holds: standing & reputation --")
  local vrep = parse_idx_table(gv("VREP"))
  local stand = parse_idx_table(gv("STANDINGS"))
  add(string.format("%-16s %-10s %6s %6s", "Hold", "Standing", "Score", "Rep"))
  for i = 0, 20 do
    local v, s = vrep[i], stand[i]
    if v or s then
      local name = HOLDCITY[i] or (v and v[2]) or (s and s[2]) or ("?" .. i)
      local standing = (s and s[4]) or "-"
      add(padesc(name:sub(1, 16), 16) .. " "
          .. col(standcol(standing), string.format("%-10s", standing))
          .. esc(string.format(" %6s %6s", (s and s[3]) or "-", (v and v[3]) or "-")))
    end
  end
  add("")
  add("-- War status --")
  add("Blot " .. q("BLOT") .. "   Garrison " .. q("GARRISON"))
  local varang = gv("VARANG")
  add(string.format("Monuments %s   Varangians %s", q("MONUMENTS"),
    (varang == "^" or varang == "") and "none" or varang))
  return table.concat(L, "\n")
end

-- (build_voyage moved to 3s-viking-sea)

-- The MISSIONS feed as a list we can act on, not just print:
--   id | desc | rep | ? | expiry | (empty) | town | goods(good:qty,...)
-- `code` is the town resolved to a travel code (Mid/Hol/Lod/...), or nil when the town
-- is not one we have a route for -- which is what makes a mission runnable or not.
local function parse_missions()
  local out = {}
  if gv("MISSIONS") == "" then return out end
  for _, ms in ipairs(split(gv("MISSIONS"), ";")) do
    local f = split(ms, "|")
    local town = f[7] or ""
    out[#out + 1] = {
      id    = f[1] or "?",
      town  = town,
      goods = (f[8] or ""):gsub(":", ""):gsub(",", " "),
      rep   = f[3] or "?",
      code  = resolve_town(town),
    }
  end
  return out
end

local function build_mission()
  local L = {}
  -- section headers ("-- Foo --") take the accent colour in every tab, for free
  local function add(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
  add("-- Missions  (click one to walk there and hand it in) --")
  local ms = parse_missions()
  if #ms == 0 then add("no missions")
  else
    add(string.format("%-4s %-16s %-24s %s", "no", "town", "needs", "rep"))
    for _, m in ipairs(ms) do
      local row = string.format("%-4s %-16s %-24s %s",
        m.id, m.town:sub(1, 16), m.goods:sub(1, 24), m.rep)
      -- A mission we have no route to stays plain text: better a row you cannot click
      -- than one that clicks into "no route known".
      if m.code then
        add(string.format("@{accent,click=vmgo %s}%s@{}", m.id, esc(row)))
      else
        add(esc(row) .. "  (no route)")
      end
    end
  end
  add("")
  add("-- Errand --")
  if gv("ERRAND") == "" then add("no errand")
  else
    local e = split(gv("ERRAND"), "|")
    -- id | desc | rep | timelimit(s) | from | to | good | qty
    add((e[2] or "?"):sub(1, 58))
    local mins = tonumber(e[4]) and (math.floor(num(e[4]) / 60) .. "m") or "?"
    add(string.format("%s -> %s   %s x%s   %s rep   %s",
      e[5] or "?", e[6] or "?", e[7] or "?", e[8] or "?", e[3] or "?", mins))
  end
  return table.concat(L, "\n")
end

-- ---------------------------------------------------------- mission runner
-- Clicking a mission row walks you to its town and hands it in; the Run button does
-- the same for every mission you hold and finishes at Midgard.
--
-- Paced, one mission at a time. The Travel tab fires a whole ~40-command route in one
-- burst and that is fine; a five-mission chain would be four hundred queued commands,
-- and one blocked move part-way would put every leg after it in the wrong place. So
-- each mission goes out as its own burst.
--
-- The pause between them does NOT wait for the walking. 3Scapes reads commands as fast
-- as they arrive, so a route is spent almost the moment it is sent; the only thing worth
-- waiting on is the `vmission fulfill` at the end of it landing before the next leg
-- starts walking away from the town. Two seconds covers that. (An earlier version scaled
-- the wait by route length and spent half a minute between missions doing nothing.)
local mrun_pause = tonumber(scrye.store.get("mrun_pause")) or 2
local mrun = { on = false, queue = nil, idx = 0, token = 0 }

local function publish_mission()
  scrye.setState(P .. "mission", build_mission())
  scrye.setState(P .. "mrun", mrun.on
    and string.format("RUNNING - mission %d of %d (Stop to break off)", mrun.idx, #mrun.queue)
    or "idle - Run walks every mission, then home to Midgard")
end

local function mrun_stop(why)
  if not mrun.on then return end
  mrun.on = false
  -- Invalidate anything already scheduled. `mrun.on = false` is not enough on its own:
  -- Stop followed by Run starts a NEW run with `on` true again, and the first run's
  -- pending tick would then drive it -- advancing the queue twice per tick.
  mrun.token = mrun.token + 1
  mrun.queue = nil
  scrye.print("[viking] mission run stopped" .. (why and (" - " .. why) or ""))
  publish_mission()
end

-- Walk to the mission's town (if we are not already standing in it) and fulfil it.
-- Returns the walk length for pacing, or nil if we could not get there -- travel_to
-- has already printed the reason in that case.
local function mission_go(m)
  if not m.code then
    scrye.print("[viking] mission " .. m.id .. ": no route known to " .. (m.town ~= "" and m.town or "?"))
    return nil
  end
  local n = travel_to(m.code)     -- 0 when we are already there
  if not n then return nil end
  -- The route ends with `enter`, so by the time this lands we are inside the town.
  -- The MUD runs commands in the order it received them, so no delay is needed here;
  -- if a move was blocked the fulfil simply fails, which is the loud failure we want.
  scrye.send("vmission fulfill " .. m.id)
  return n
end

local mrun_next
mrun_next = function()
  if not mrun.on then return end
  local tok = mrun.token
  mrun.idx = mrun.idx + 1
  local m = mrun.queue[mrun.idx]
  if not m then
    mrun.on = false
    scrye.print("[viking] missions done - heading back to Midgard")
    travel_to("Mid")
    publish_mission()
    return
  end
  scrye.print(string.format("[viking] mission %d/%d: %s -> %s",
    mrun.idx, #mrun.queue, m.id, town_label(m.code)))
  publish_mission()
  mission_go(m)   -- nil means it could not go, and has already said why; carry on regardless
  -- Just long enough for the fulfil to land. Not for the walk -- that is already over.
  scrye.after(mrun_pause, function()
    if mrun.on and mrun.token == tok then mrun_next() end
  end)
end

local function mrun_start()
  if mrun.on then mrun_stop("by request") return end
  local q, skipped = {}, {}
  for _, m in ipairs(parse_missions()) do
    if m.code then q[#q + 1] = m else skipped[#skipped + 1] = (m.town ~= "" and m.town or "?") end
  end
  if #skipped > 0 then
    scrye.print("[viking] skipping, no route known: " .. table.concat(skipped, ", "))
  end
  if #q == 0 then scrye.print("[viking] nothing to run") return end
  -- The run ends at Midgard and starting there is the natural loop, so when neither the
  -- remembered town nor the map feed can say where we are, that is the assumption rather
  -- than a stalled run that fails once per mission. Said out loud, because if it is wrong
  -- the first leg walks a Midgard route from somewhere else and you want to know to Stop.
  if not current_town() then
    scrye.store.set("curtown", "Mid")
    scrye.print("[viking] don't know where you are - assuming Midgard (vhere <town> if not)")
  end
  mrun.on = true; mrun.queue = q; mrun.idx = 0
  scrye.print(string.format("[viking] running %d mission%s, then back to Midgard",
    #q, #q == 1 and "" or "s"))
  mrun_next()
end

-- one mission, by number: what a click on its row runs
local function mrun_one(id)
  id = tostring(id or ""):gsub("%s", "")
  for _, m in ipairs(parse_missions()) do
    if m.id == id then
      if mrun.on then mrun_stop("single mission clicked") end
      mission_go(m)
      return
    end
  end
  scrye.print("[viking] no mission numbered " .. id)
end

scrye.addAlias{ pattern = [[^vmgo (\d+)$]], regex = true, run = function(id) mrun_one(id) end }
scrye.addAlias{ pattern = [[^vmrun$]],       regex = true, run = function() mrun_start() end }
scrye.addAlias{ pattern = [[^vmrun stop$]],  regex = true, run = function() mrun_stop("by request") end }
scrye.addAlias{
  pattern = [[^vmrun pace ([\d.]+)$]], regex = true,
  run = function(n)
    mrun_pause = math.max(0.5, math.min(30, tonumber(n) or mrun_pause))
    scrye.store.set("mrun_pause", tostring(mrun_pause))
    scrye.print(string.format("[viking] mission run pause: %.1fs between missions", mrun_pause))
  end,
}

-- A dropped connection is not a reason to keep walking when it comes back.
scrye.onDisconnect(function() mrun_stop("disconnected") end)

scrye.store.delete("mrun_step")   -- the old route-length pacing; `mrun_pause` replaced it

publish_mission()   -- so the tab has its status line before the first feed arrives

-- Feeds tab: one row per Guild.* package (bursts seen, last-burst age), then the
-- translated keys underneath - the debugging window for everything else.
local PKGS = {}          -- pkg -> { bursts = n, at = now_s }
local function pkg_seen(pkg)
  local e = PKGS[pkg] or { bursts = 0 }
  e.bursts = e.bursts + 1
  e.at = now_s
  PKGS[pkg] = e
  dirty.feeds = true
end

local function build_feeds()
  local L = { "@{accent,bold}-- GMCP packages --@{}" }
  local names = {}
  for k in pairs(PKGS) do names[#names + 1] = k end
  table.sort(names)
  if #names == 0 then
    L[#L + 1] = "(no Guild.* burst yet - waiting for the GMCP feed)"
  else
    for _, k in ipairs(names) do
      local e = PKGS[k]
      local age = now_s - (e.at or 0)
      local agestr = age < 60 and (age .. "s ago") or (math.floor(age / 60) .. "m ago")
      L[#L + 1] = string.format("%-18s %5d bursts   last %s", k, e.bursts, agestr)
    end
  end
  L[#L + 1] = ""
  L[#L + 1] = "@{accent,bold}-- translated keys --@{}"
  local keys = {}
  for k in pairs(seen_keys) do keys[#keys + 1] = k end
  table.sort(keys)
  for _, k in ipairs(keys) do
    L[#L + 1] = string.format("%-14s %s", k:sub(1, 14), gv(k):sub(1, 40):gsub("@", "@@"))
  end
  return table.concat(L, "\n")
end

-- (the Map tab moved to 3s-viking-sea)

-- -------------------------------------------------------------- Plan tab
local function build_plan()
  local plan = split(gv("CPLAN"), "|")
  cp_accumulate(gv("CPB"))
  local placed = tonumber(plan[3]) or 0
  local allowed = plan[4] or "?"
  local tracked = cp_count()
  local hdr = string.format("City Plan  --  Placed %d / %s", placed, allowed)
  if tracked < placed then
    hdr = hdr .. string.format("   (%d tracked - resync with 'vplan' in-game; 'vplan clear' to reset)", tracked)
  end
  scrye.setState(P .. "planhdr", hdr)
  if gv("CPLAN") == "" then
    scrye.setState(P .. "plangrid", "")
    scrye.setState(P .. "planlist", "no city-plan data yet - waiting for Guild.City")
    return
  end
  -- expand each building's w x h footprint
  local pmap, pcount = {}, {}
  for _, b in pairs(cp_placed) do
    pcount[b.letter] = (pcount[b.letter] or 0) + 1
    for dr = 0, b.h - 1 do
      for dc = 0, b.w - 1 do
        pmap[(b.row + dr) .. "," .. (b.col + dc)] = b
      end
    end
  end
  -- 12x12 grid. The MIP feed carried the plan's TERRAIN rows (woods/river/coast);
  -- Guild.City does not (yet) - so unbuilt ground renders plain. The buildings are
  -- the real content and they come straight from cityplan_buildings.
  local dim = tonumber(split(gv("CPLAN"), "|")[1]) or 12
  dim = math.max(1, math.min(24, dim))
  local grid = {}
  for r = 0, dim - 1 do
    local out = {}
    for c = 0, dim - 1 do
      local bld = pmap[r .. "," .. c]
      if bld then
        local role = PLAN_BY_L[bld.letter] and PLAN_BY_L[bld.letter].role
        out[#out + 1] = (role and ROLE_DIGIT[role]) or "6"
      else
        out[#out + 1] = "."
      end
    end
    grid[#grid + 1] = table.concat(out)
  end
  scrye.setState(P .. "plangrid", table.concat(grid, "\n"))
  local L = {
    "rows A-L top to bottom, cols 1-12; place with: vplan place <letter> <cell>  (e.g. A5)",
    "colours: green prod  red ind  maroon grim  cyan trade  magenta cult  white home  gold throne",
    "",
    "-- Palette (x n = placed) --",
  }
  local pal = {}
  for _, b in ipairs(PLAN_BLD) do
    local n = pcount[b[1]]
    pal[#pal + 1] = string.format("%s %-14s%s", b[1], b[2], n and (" x" .. n) or "")
  end
  local rows = math.ceil(#pal / 2)
  for i = 1, rows do
    L[#L + 1] = string.format("%-24s %s", pal[i] or "", pal[i + rows] or "")
  end
  scrye.setState(P .. "planlist", table.concat(L, "\n"))
end

-- (auto sea-navigation and the Sea tab moved to 3s-viking-sea)

-- ------------------------------------------------------------ the flush
local BUILDERS = {
  stats      = function() scrye.setState(P .. "stats", build_stats()) end,
  city       = function() scrye.setState(P .. "city", build_city()) end,
  builds     = function() scrye.setState(P .. "builds", build_builds()) end,
  production = function() scrye.setState(P .. "production", build_production()) end,
  people     = function() scrye.setState(P .. "people", build_people()) end,
  settlers   = function() scrye.setState(P .. "settlers", build_settlers()) end,
  holds      = function() scrye.setState(P .. "holds", build_holds()) end,
  plan       = build_plan,
  mission    = function() publish_mission() end,
  feeds      = function() scrye.setState(P .. "feeds", build_feeds()) end,
}

flush = function()
  flush_pending = false
  for sec in pairs(dirty) do
    local b = BUILDERS[sec]
    if b then pcall(b) end
  end
  dirty = {}
end

-- key -> sections that must rebuild when it changes
local KEYMAP = {}
local function keymap(sec, keys)
  for k in keys:gmatch("%S+") do
    local t = KEYMAP[k] or {}
    t[#t + 1] = sec
    KEYMAP[k] = t
  end
end
keymap("stats", "god_power god_power_focus blot lin glvl sub daler rank renown hp seid vig rad "
  .. "vmnew vmreg nexttick dcycle stfx fury threk mthrek chain bsdepth "
  .. "rndz ldng mldng patrol craid gxp")
keymap("city", "ships carts refinery")
-- wstock joined this list with the planner: affordability is half resources, so a stock
-- change moves rows between "OK" and short exactly as a daler change does.
keymap("builds", "buildings builds daler wstock bdmg")
keymap("production", "production routes buildings wstock")
keymap("people", "thralls thrall_follower garrison mthrek hird hirdcap staff bonds")
keymap("settlers", "blot sproj spop smood ssent swater stax supk snet nexttick scivics sconsume")
keymap("holds", "vrep standings blot garrison monuments varang")
keymap("mission", "missions errand")
keymap("plan", "cplan cpb")

-- vset: the write side of the translated feed. Everything the classic's
-- scrye.watch("vik") did per key-change happens here instead, driven by the
-- Guild.* adapters at the bottom of the file.
local mk_on_feed   -- set after MK is built; called so the auto-trader settles/reacts
local mk_goods_feed, mk_set_towns   -- likewise: the Guild.TradeGoods market feed

local function vset(key, value)
  key = key:lower()
  value = value == nil and "" or tostring(value)
  if V[key] == value then return end
  V[key] = value
  seen_keys[key] = true
  dirty.feeds = true
  local secs = KEYMAP[key]
  if secs then
    for _, s2 in ipairs(secs) do dirty[s2] = true end
  end
  -- combat-round tracking: when RNDZ drops, the value before the drop was the fight length
  if key == "rndz" then
    local cur = tonumber(value)
    if cur then
      if prev_rndz and cur < prev_rndz then
        last_fight = prev_rndz
        scrye.store.set("last_fight", tostring(last_fight))
        dirty.stats = true
      end
      prev_rndz = cur
    end
  end
  if key == "cpb" then cp_accumulate(value) end
  if key == "cplan" then
    -- a building was removed: our tracked list is stale, resync from CPB
    local realn = tonumber(split(value or "", "|")[3]) or 0
    if realn < cp_count() then
      cp_placed = {}
      cp_accumulate(gv("CPB"))
      cp_save()
    end
  end
  schedule_flush()
end

-- ------------------------------------------------- vtrade stock scan
-- STOPGAP until per-good warehouse stock reaches GMCP (plan §2 feed gap): the
-- framed `vtrade stock` report is parsed into the same WSTOCK records the MIP
-- feed used to carry - "cap;good|amt|100|grade;..." with one record per quality
-- grade, amounts summed by every consumer (warehouse display, build planner,
-- auto-trader). Quality PERCENTS are not in the text, so every record says 100
-- and the grade NAME rides in field 4 for display; no invented numbers.
--
-- The parser runs on every framed block it recognises, so typing `vtrade stock`
-- yourself refreshes the panel too; the Production tab's Refresh button (and the
-- `vstock` alias) send the command with the output gagged. The day Guild.Trade
-- ships real per-good rows, wiring them into compose_wstock() below retires this
-- scan - the GMCP data should win.
local ws_goods = ""            -- the scanned "good|amt|100|grade;..." tail
local ws_cap_scan = nil        -- capacity from the report header
local ws_cap_feed = nil        -- capacity from Guild.Trade wstock_cap (fresher)
local ws_scanned_at = -math.huge   -- now_s of the last completed scan
local ws_active = false        -- a button/alias scan is in flight (gag window)
local ws_used = nil            -- units stored, from the report header
local ws_attempted_at = -math.huge -- now_s of the last AUTO scan attempt (throttle)
local ws_cart_sig = nil        -- sorted cart-id set; a change = a cart left or came home
local ws_warned = false        -- "waiting for stock" said once, reset by a good scan
local ws_feed_live = false     -- Guild.Warehouse has spoken: the FEED owns the stock now.
                               -- Every scan path is gated off it - the feed pushes changes
                               -- itself, and a text scan would only overwrite real per-grade
                               -- pcts with the 100s the text cannot better.
local mk_armed                 -- set after MK is built; tells whether the auto-trader is on
local ws_timer = nil

local function compose_wstock()
  local cap = ws_cap_feed or ws_cap_scan
  if not cap and ws_goods == "" then return "" end
  return tostring(cap or 0) .. ";" .. ws_goods    -- (tostring: the adapters' S() is declared below this block)
end

local ws_cur, ws_rows = nil, nil   -- collection state (nil = idle)

local function ws_key(name)
  return tostring(name or ""):gsub("^%s+", ""):gsub("%s+$", ""):lower():gsub("[%s%-]+", "_")
end

local function ws_close()
  ws_active = false
  if ws_timer then scrye.cancel(ws_timer); ws_timer = nil end
end

local function ws_finish()
  if not ws_rows then return end
  if ws_feed_live then
    -- a hand-typed `vtrade stock` while the feed is live: the report was shown,
    -- but its 100s must not overwrite the feed's real per-grade pcts
    ws_cur, ws_rows = nil, nil
    return
  end
  local out, n = {}, 0
  for _, r in ipairs(ws_rows) do
    if #r.grades == 0 then
      out[#out + 1] = r.key .. "|" .. r.amt .. "|100|"
      n = n + 1
    else
      -- one record per grade; consumers sum per good (grade totals ARE the total)
      for _, g in ipairs(r.grades) do
        out[#out + 1] = r.key .. "|" .. g.amt .. "|100|" .. g.grade
      end
      n = n + 1
    end
  end
  ws_goods = table.concat(out, ";")
  ws_scanned_at = now_s
  ws_warned = false
  ws_cur, ws_rows = nil, nil
  vset("wstock", compose_wstock())
  if mk_on_feed then pcall(mk_on_feed) end
  scrye.print(string.format("@{#DEB218,bold}[stock]@{} %d goods scanned (%s/%s units)",
    n, tostring(ws_used or "?"), tostring(ws_cap_scan or "?")))
end

local function ws_line(cap)
  -- strip the closing *~- and trim (same framing the build-planner scan strips)
  local c = (cap or ""):gsub("%*~%-.*$", ""):gsub("^%s+", ""):gsub("%s+$", "")
  if c == "" then return end
  local tier, used, wcap = c:match("^Warehouse Stock Tier (%d+)%s+(%d+)/(%d+) stored")
  if tier then
    ws_cur, ws_rows = nil, {}
    ws_used, ws_cap_scan = tonumber(used), tonumber(wcap)
    return
  end
  if not ws_rows then return end          -- not inside a report
  -- terminators: the footer lines after the goods
  if c:match("^%(next shift") or c:match("^Daler:") then
    ws_finish()
    return
  end
  -- grade breakdown line: starts with the amount (the good's name column is blank)
  local gamt, grade = c:match("^(%d+) units%s+(.-)%s*$")
  if gamt and ws_cur then
    ws_cur.grades[#ws_cur.grades + 1] = { amt = tonumber(gamt), grade = grade }
    return
  end
  -- good line: "Name   N units [total] [(cap N)]"
  local name, amt = c:match("^([%a][%a' ]-)%s%s+(%d+) units")
  if name then
    ws_cur = { key = ws_key(name), amt = tonumber(amt), grades = {} }
    ws_rows[#ws_rows + 1] = ws_cur
  end
end


scrye.addTrigger{
  pattern = [[^-~\*(.*)]],
  regex   = true,
  run     = function(cap) pcall(ws_line, cap) end,
}

-- gag the framed lines only for button/alias scans; a hand-typed `vtrade stock`
-- stays visible (and is still parsed)
scrye.onLine(function(line)
  if ws_active and line:match("^%-~%*") then
    -- the hint footer is the last framed line; close the gag window there
    if line:find("vtrade stock unblocked", 1, true) then ws_close() end
    return false
  end
end)

local function vstock_scan(quiet)
  if ws_feed_live then
    if not quiet then
      scrye.print("@{#DEB218,bold}[stock]@{} the warehouse feed (Guild.Warehouse) is live - the panel already tracks stock")
    end
    return
  end
  ws_active = true
  if ws_timer then scrye.cancel(ws_timer) end
  ws_timer = scrye.after(8, function() ws_timer = nil; ws_close() end)   -- safety
  scrye.send("vtrade stock")
  if not quiet then scrye.print("@{#DEB218,bold}[stock]@{} refreshing warehouse stock...") end
end

scrye.addAlias{ pattern = "^vstock$", regex = true, run = function() vstock_scan(false) end }

-- Quiet automatic refresh - fired when a cart leaves or comes home (the moment
-- the warehouse actually changes) and by the auto-trader's guard/staleness paths.
-- Gated so it never spams: only when a scan already ran this session or the
-- auto-trader is armed, and never more than one attempt per 15 s (the manual
-- button/alias bypasses the throttle).
local function ws_auto_scan()
  if ws_feed_live then return end   -- the feed pushes changes; nothing to scan for
  if ws_scanned_at == -math.huge and not (mk_armed and mk_armed()) then return end
  if (now_s - ws_attempted_at) < 15 then return end
  ws_attempted_at = now_s
  vstock_scan(true)
end

-- ----------------------------------------------------- Modrsokn cooldown
-- 3-minute combat-ability cooldown, started by the "rage inward" line.
local MORDSOKN_CD = 180
local mordsokn_left = 0

-- raid-alarm shelf life: raid{} rides Guild.City's UNPAGED stream, whose keys merge
-- and are never removed, and the paged full-replace deliberately spares unpaged-only
-- keys - so a raid that ended would leave its alarm up forever. While a raid is
-- inbound its secs tick down burst by burst (CRAID keeps changing); ten minutes of
-- silence means the raid is over or resolved, and the alarm clears itself.
-- VERIFY LIVE: how the server actually announces a raid ending.
local CRAID_TTL = 600
local craid_seen_at = -math.huge

scrye.addTrigger{
  pattern = "You close your eyes and turn the rage inward",
  regex = true,
  run = function()
    mordsokn_left = MORDSOKN_CD
    scrye.print("[Modrsokn] used - 3:00 cooldown")
  end,
}

-- 1s heartbeat: elapsed-seconds counter + Modrsokn countdown + raid-alarm shelf life
scrye.every(1, function()
  now_s = now_s + 1
  if V["craid"] ~= nil and V["craid"] ~= "" and (now_s - craid_seen_at) > CRAID_TTL then
    vset("craid", "")
  end
  if mordsokn_left > 0 then
    mordsokn_left = mordsokn_left - 1
    if mordsokn_left <= 0 then
      scrye.setState(P .. "mordsokn", "ready")
      scrye.print("[Modrsokn] ready")
    else
      scrye.setState(P .. "mordsokn",
        string.format("%d:%02d", math.floor(mordsokn_left / 60), mordsokn_left % 60))
    end
  end
end)
scrye.setState(P .. "mordsokn", "ready")

-- ------------------------------------------------- vplan full resync
-- Triggers on the in-game 'vplan' framed grid; regroups adjacent same-letter
-- cells into single buildings (a 2x2 warehouse is ONE building).
local cp_scan, cp_capturing = {}, false

scrye.addTrigger{
  pattern = [[^-~\*  City Plan -- ]],
  regex = true,
  run = function() cp_capturing = true; cp_scan = {} end,
}
scrye.addTrigger{
  pattern = [[^-~\*  ([A-L]) (.*\S)\s+\*~-]],
  regex = true,
  run = function(letter, rowstr)
    if not cp_capturing then return end
    local toks = {}
    for t in rowstr:gmatch("%S+") do toks[#toks + 1] = t end
    local h1, h2
    for i = 1, #toks do
      if toks[i] == "#" then if not h1 then h1 = i elseif not h2 then h2 = i end end
    end
    if not (h1 and h2) then return end
    local row = string.byte(letter) - 65
    for c = 1, 12 do
      local ch = toks[h1 + c]
      if ch and PLAN_BY_L[ch] then
        cp_scan[(c - 1) .. "," .. row] = { col = c - 1, row = row, w = 1, h = 1,
          letter = ch, name = PLAN_BY_L[ch].name }
      end
    end
  end,
}
scrye.addTrigger{
  pattern = [[^-~\*  Placed: (\d+) of]],
  regex = true,
  run = function()
    if not cp_capturing then return end
    cp_capturing = false
    local seen, built = {}, {}
    for k, v in pairs(cp_scan) do
      if not seen[k] then
        local c0, r0 = k:match("^(%d+),(%d+)$"); c0, r0 = tonumber(c0), tonumber(r0)
        local minc, maxc, minr, maxr = c0, c0, r0, r0
        local stack = { { c0, r0 } }; seen[k] = true
        while #stack > 0 do
          local cell = table.remove(stack); local cc, rr = cell[1], cell[2]
          if cc < minc then minc = cc end; if cc > maxc then maxc = cc end
          if rr < minr then minr = rr end; if rr > maxr then maxr = rr end
          for _, d in ipairs({ { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } }) do
            local nk = (cc + d[1]) .. "," .. (rr + d[2])
            if cp_scan[nk] and cp_scan[nk].letter == v.letter and not seen[nk] then
              seen[nk] = true; stack[#stack + 1] = { cc + d[1], rr + d[2] }
            end
          end
        end
        built[minc .. "," .. minr] = { col = minc, row = minr,
          w = maxc - minc + 1, h = maxr - minr + 1, letter = v.letter, name = v.name }
      end
    end
    cp_placed = built
    cp_save()
    scrye.print("[plan] synced " .. cp_count() .. " buildings from vplan")
    dirty.plan = true
    schedule_flush()
  end,
}

-- ------------------------------------------------------------- keepalive
local tick_timer = nil
local function tick_start()
  if not tick_timer then
    tick_timer = scrye.every(300, function() scrye.send("l") end)
  end
end
local function vik_tick(state)
  if state == "on" then
    tick_start()
    scrye.store.set("tick", "1")
  else
    if tick_timer then scrye.cancel(tick_timer); tick_timer = nil end
    scrye.store.set("tick", "0")
  end
  scrye.print("[viking] keepalive 'l' every 5 min: " .. state)
end
if scrye.store.get("tick") == "1" then tick_start() end

-- --------------------------------------------------------------- aliases
-- (vikbar / viktab dropped: HUD-managed)

-- (vmapon dropped: it requested MIP feeds; this plugin is GMCP-fed)

scrye.addAlias{
  pattern = "^vtick (on|off)$", regex = true,
  run = function(state) vik_tick(state) end,
}

scrye.addAlias{
  pattern = "^vikdump$", regex = true,
  run = function()
    local keys = {}
    for k in pairs(seen_keys) do keys[#keys + 1] = k end
    table.sort(keys)
    if #keys == 0 then scrye.print("[viking] no feed keys seen yet") return end
    for _, k in ipairs(keys) do
      scrye.print(k .. " = " .. gv(k))
    end
  end,
}

-- (vnav moved to 3s-viking-sea)

-- (vikloc moved to 3s-viking-sea with the Map tab)

-- Walk to a settlement by name or abbreviation. The clickable Travel/Map lists route through
-- this alias rather than calling travel_to directly, so mouse and keyboard take the same path
-- and a hand-typed name gets the same fuzzy matching the lists get for free.
scrye.addAlias{
  pattern = [[^vgo (.+)$]], regex = true,
  run = function(name)
    local code = resolve_town(name)
    if not code then scrye.print("[viking] no settlement matching '" .. tostring(name) .. "'"); return end
    travel_to(code)
  end,
}

scrye.addAlias{
  pattern = [[^vhere (.+)$]], regex = true,
  run = function(arg)
    local key = resolve_town(arg)
    if not key then
      scrye.print("[viking] unknown town '" .. arg .. "'. Use the name on a Travel button (e.g. vhere Midgard).")
      return
    end
    scrye.store.set("curtown", key)
    scrye.print("[viking] current location set: " .. town_label(key))
  end,
}

-- 3s-viking-sea's Map tab asks for walks over the event bus (its colorgrid click
-- cannot ride the command pipeline the way text click-links can)
scrye.on("viking.travel", function(data)
  local ok, t = pcall(scrye.json.decode, data)
  if not ok or type(t) ~= "table" then return end
  local code = resolve_town(t.town)
  if not code then
    scrye.print("[viking] travel request for unknown town '" .. tostring(t.town) .. "'")
    return
  end
  travel_to(code)
end)

-- consumed, not passed to the MUD: the HUD owns panel visibility and tab selection
scrye.addAlias{
  pattern = "^vikbar$", regex = true,
  run = function() scrye.print("[viking] the Viking panel is managed by Scrye - show or hide it from the HUD.") end,
}
scrye.addAlias{
  pattern = "^viktab(?: .*)?$", regex = true,
  run = function() scrye.print("[viking] tabs are switched in the HUD panel itself, not with a command.") end,
}

scrye.addAlias{
  pattern = "^vplan clear$", regex = true,
  run = function()
    cp_placed = {}; cp_save()
    scrye.print("[plan] tracked placements cleared")
    dirty.plan = true
    schedule_flush()
  end,
}

-- ----------------------------------------------------------------- panel
local function patrol_commit()
  local p = split(gv("PATROL"), "|")
  local n = tonumber(p[1])
  if n then scrye.send("vpatrol commit " .. n)
  else scrye.print("[viking] no patrol number known") end
end

build_panel = function()
-- ======================================================================================
-- Trade (merged in from the 3s-market plugin, which is now a deprecation stub).
--
-- Wrapped in an immediately-called function rather than spliced in flat, and that is not
-- style: Lua allows at most 200 local variables live at once in a function, and a file's
-- top level IS a function. This panel already declares ~136 and the market code declares
-- ~91, so a flat merge fails to compile outright with "too many local variables". A nested
-- function gets its own register window, so the market body keeps its own ~91 and costs the
-- outer chunk exactly one local: MK.
--
-- It is otherwise a faithful port -- same scan, same auto-trader, same commands (mkref,
-- mkdispatch, mkunits, atrade ...), same three tabs, now Trade / Trade Auto / Trade Log.
-- It declares its own P, esc, comma and friends inside the wrapper, so it borrows nothing
-- from this file and there are no upvalues to keep in step.
--
-- The state keys are unchanged in shape but now live under plugin.3s-viking-status.*;
-- none of them collided with this panel's own (checked key by key at merge time).
-- ======================================================================================

local MK = (function()
-- 3S Market -- 3Scapes Viking market arbitrage finder (Scrye port of ThreeS_Market)
--
-- `mkref` (or the panel's Refresh button) runs `vtrade goods <resource>` for every
-- good with the output gagged, parses the buy/sell prices per town, and renders the
-- best trade route per good -- buy cheap in one town, sell dear in another -- ranked
-- by profit per unit, in the HUD panel. Results persist across restarts.
--
-- NOTE: dropped / simplified vs the MUSHclient original:
--   * `markwin` show/hide alias DROPPED -- the HUD manages panel visibility.
--   * The entire auto-trader subsystem DROPPED (the `atrade` alias and config,
--     scalper/restock/flush/clearing logic, cart-dispatch cooldown handling,
--     session stats, Log/Stats tabs, 3s_autotrade.log file) -- it relied on
--     click hotspots, inputboxes, io.* log files, os.time and the MIP companion
--     plugin's broadcasts, and is outside this port's scope (market scan + report).
--   * Town-click dispatch is BACK (inline click markup): every town cell in the
--     report dispatches the configured Units of that row's good (buy side blue,
--     sell side green), and clicking a GOOD's name toggles it held/released --
--     held goods show amber and are never auto-traded. Units/Escort live in the
--     panel inputs.
--   * The "updated HH:MM" stamp DROPPED (no clock in the sandbox); the status line
--     says whether data is from this session or restored from the last one.
--   * Low-stock towns were orange in the miniwindow; here they are marked with "*"
--     (the 2nd/3rd-best town is still shown when the better ones are low, as before).
--   * Scan sends were spaced 0.4 s apart; scrye.after has 1 s granularity, so they
--     go out 1 s apart. The settle check (finish when output goes quiet, hard cap)
--     is kept, plus an early finish once the last good's reply has been seen.
--   * The auto-refresh on the "[Viking-Trade] ... %" market tick is kept (debounced,
--     quiet), but only after the first manual refresh this session -- the original
--     gated it on "window visible or auto-trader on", neither of which exists here.
--   * Best sell prices are still published for other plugins: world variable
--     "prices" (same "cmd=price;..." format) and state plugin.3s-market.prices.

local P = "plugin." .. scrye.id .. "."

-- the tradeable goods (command word for `vtrade goods <word>`)
local RES = {
  { name = "Timber",      cmd = "timber"     },
  { name = "Iron",        cmd = "iron"       },
  { name = "Grain",       cmd = "grain"      },
  { name = "Furs",        cmd = "furs"       },
  { name = "Fish",        cmd = "fish"       },
  { name = "Mead",        cmd = "mead"       },
  { name = "Sunstone",    cmd = "sunstone"   },
  { name = "Runestones",  cmd = "runestones" },
  { name = "Spoils",      cmd = "spoils"     },
  { name = "Ore",         cmd = "ore"        },
  { name = "Salted Fish", cmd = "salted"     },
  { name = "Bread",       cmd = "bread"      },
  { name = "Fine Furs",   cmd = "fine"       },
  { name = "Tools",       cmd = "tools"      },
  { name = "Gemstones",   cmd = "gems"       },
  { name = "Finery",      cmd = "finery"     },
  -- Added later by the game. Every one is a single word, so the command word is just the
  -- lowercase name -- unlike the older multi-word goods, which the game abbreviates
  -- ("Salted Fish" -> salted, "Fine Furs" -> fine, "Gemstones" -> gems).
  { name = "Wool",        cmd = "wool"       },
  { name = "Eggs",        cmd = "eggs"       },
  { name = "Milk",        cmd = "milk"       },
  { name = "Pork",        cmd = "pork"       },
  { name = "Mutton",      cmd = "mutton"     },
  { name = "Poultry",     cmd = "poultry"    },
  { name = "Beef",        cmd = "beef"       },
  { name = "Horsemeat",   cmd = "horsemeat"  },
  { name = "Weapons",     cmd = "weapons"    },
  { name = "Armour",      cmd = "armour"     },
  { name = "Cloth",       cmd = "cloth"      },
  -- "Smoked Meat" abbreviates the way the older multi-word goods do (Salted Fish ->
  -- salted, Fine Furs -> fine), which is the rule the single-word guesses above assume.
  { name = "Smoked Meat", cmd = "smoked"     },
  { name = "Cheese",      cmd = "cheese"     },
  -- Honey: a raw material (also in RAWBUILD below). Appended LAST deliberately:
  -- LAST_NAME is derived from the final entry, so the scan's early-finish still
  -- fires on the last good the game replies to.
  { name = "Honey",       cmd = "honey"      },
}
local DISPLAY = {}                       -- lower cmd -> nice name
for _, r in ipairs(RES) do DISPLAY[r.cmd] = r.name end
local LAST_NAME = RES[#RES].name:lower() -- header of the final reply => scan nearly done

local LOWSTOCK = 100   -- below this stock, also show the next-best town (marked *)

-- ====================== auto-trader constants ======================
local DCMD = {}                          -- market header name (lowercased) -> short vtrade word
for _, r in ipairs(RES) do DCMD[r.name:lower()] = r.cmd end
local function disp_cmd(res) return DCMD[res] or res end

-- display town name -> the word vtrade expects (default: first word lowercased)
local TOWNCMD = { ["lodbrok's hold"] = "lodbrok", ["lodbrok's hol"] = "lodbrok" }
local function town_cmd(town)
  local key = (town or ""):lower()
  return TOWNCMD[key] or key:match("^%a+") or key
end

local function comma(n)
  local s = tostring(math.floor(tonumber(n) or 0))
  while true do local a, b = s:gsub("^(%d+)(%d%d%d)", "%1,%2"); s = a; if b == 0 then break end end
  return s
end

-- refined goods (towns only buy these) - matched by market-key name and by cmd
local REFINED = { ["salted fish"] = true, salted = true, ["fine furs"] = true, fine = true,
                  bread = true, finery = true, tools = true,
                  -- the butchery line: refined the same way bread is refined from grain, so the
                  -- auto-trader sells them without holding back the raw reserve it keeps on
                  -- timber and iron. Wool, eggs and milk are the raw side and are deliberately
                  -- absent from all three tables: they keep the reserve, and the auto-BUYER
                  -- leaves them alone (RAWBUILD is the list it spends daler on).
                  pork = true, mutton = true, poultry = true, beef = true, horsemeat = true,
                  weapons = true, armour = true, cloth = true,
                  ["smoked meat"] = true, smoked = true, cheese = true }
-- special commodities (not raw materials): sellable, but keep a small reserve
local SPECIAL = { runestones = true, gemstones = true, gems = true }
-- raw materials the auto-buyer will restock up to the Raw> buffer when they run low
local RAWBUILD = { timber = true, iron = true, furs = true, grain = true, mead = true,
                   fish = true, sunstone = true, spoils = true, honey = true }

local function trim(s) return (s or ""):gsub("^%s+", ""):gsub("%s+$", "") end
-- escape MUD-sourced text before embedding it in colour markup
local function esc(x) return (tostring(x or ""):gsub("@", "@@")) end
local function titlecase(s)
  return (s:gsub("(%a)([%w]*)", function(a, b) return a:upper() .. b:lower() end))
end

-- ====================== captured market data ======================
local market  = {}      -- market[resource][town] = { buy=, sup=, sell=, dem=, aff= }
local results = {}      -- computed arbitrage rows, sorted by profit desc
local cur_res = nil     -- resource currently being parsed

local scanning       = false  -- a refresh is in flight (gag window open)
local quiet          = false  -- suppress the refresh notes (auto/background refreshes)
local got_data       = false  -- new market lines arrived since the last settle check
local checks         = 0      -- settle-check counter (hard cap)
local settle_running = false
local scan_token     = 0      -- invalidates timers from an abandoned scan
local update_pending = false  -- a market-tick refresh is already queued (debounce)
local user_refreshed = false  -- at least one manual refresh this session

local mk_refreshed_at = 0     -- os.time() the market data was last finalised (for the auto-trader)
local connected = true        -- tracked via onConnect/onDisconnect. Starts true: a plugin
                              -- (re)load mid-session never receives an onConnect, and the
                              -- auto-trader must not sit idle waiting for one.
local mk_last_dispatch = 0    -- os.time() of the last auto-dispatch (self-rescan guard)
local last_status = ""        -- last status line passed to mk_render (for re-rendering in place)

-- ====================== auto-trader settings (persisted via scrye.store) ======================
local function sget(k) local x = scrye.store.get(k); if x == nil or x == "" then return nil end; return x end
local function sset(k, v) scrye.store.set(k, tostring(v)) end

local at = {
  on      = false,                                  -- ALWAYS off on load, like 3s-raid's `ar.on`:
                                                    -- a bot that spends daler must not come back
                                                    -- armed from a restart you have forgotten about.
                                                    -- Deliberately not read from the store, so the
                                                    -- toggles below do not write it either.
  reserve = tonumber(sget("at_reserve")) or 5000,   -- keep-back daler (scalper won't spend below this)
  margin  = tonumber(sget("at_margin"))  or 1,      -- min profit/unit for arbitrage buys
  stock   = tonumber(sget("at_stock"))   or 300,    -- Raw> buffer to keep for raw building materials
  carts   = tonumber(sget("at_carts"))   or 0,      -- cap (0 = auto from Trading Post tier)
  refined = (sget("at_refined") ~= "0"),            -- also sell refined goods (default yes)
  min_pct = tonumber(sget("at_minpct")) or 70,      -- min % of cart capacity before sending a cart
  min_rel = tonumber(sget("at_minrel")) or 40,      -- min % of the best available load's value
  keep    = tonumber(sget("at_keep"))   or 20,      -- units of EVERY good to keep (mission reserve)
  scalp   = (sget("at_scalp") ~= "0"),              -- buy-low/sell-high scalp (default on)
  restock = (sget("at_restock") == "1"),            -- actively buy raws to top up Raw> (default off)
  flush   = tonumber(sget("at_flush")) or 500,      -- pile >= this jumps the queue, ignores value floor (0=off)
  soft    = tonumber(sget("at_soft"))     or 70,    -- % full: rank by biggest pile, stop scalping
  full    = tonumber(sget("at_full"))     or 90,    -- % full that switches to clearing mode
  clear_pct = tonumber(sget("at_clearpct")) or 25,  -- min cart fill % while clearing
  escort  = tonumber(sget("at_escort")) or 5,       -- escort size for auto-dispatched carts
  yard    = tonumber(sget("at_yard")) or 180,       -- cartyard cooldown after each cart (secs);
                                                    -- a hold this long starts at every dispatch,
                                                    -- and the yard's own "Ready in" refusal
                                                    -- corrects the clock. 0 = no provisional
                                                    -- hold (refusals still set it).
  notify  = (sget("at_notify") == "1"),             -- buzz the phone per auto-dispatch (default off)
  pending = 0, last_carts = nil, cd_wait = false, pending_check = false,
  stats = { buys = 0, sells = 0, spent = 0, earned = 0, since = os.time(), recent = {} },
  exempt = {},
  floors = {},   -- per-good minimum stock: cmd -> units. The trader never sells a
                 -- floored good below its floor (the floor RAISES the category
                 -- reserve, never lowers it), and restock tops a floored raw up to
                 -- the floor instead of Raw>. "grain=500,mead=750" in the store.
}
for w in (sget("at_exempt") or ""):gmatch("[^,]+") do at.exempt[w] = true end
for pair in (sget("at_floors") or ""):gmatch("[^,]+") do
  local g, n = pair:match("^(.-)=(%d+)$")
  if g and g ~= "" and tonumber(n) and tonumber(n) > 0 then at.floors[g] = tonumber(n) end
end

-- Manual dispatch cart size (the MUSHclient window had a Units hotspot, 20-350). The ceiling
-- is only a sanity clamp on what you type -- the game rejects an over-large cart itself, and
-- the AUTO-trader never uses this number at all: at_capacity() reads the real cart size out of
-- the CIDLE/CARTS feed. So it is set well above the largest cart anyone has, and left there,
-- rather than tracking each capacity rise. It was 350 and carts outgrew it.
local MK_UNITS_MIN, MK_UNITS_MAX = 20, 1000
local mk_units = math.max(MK_UNITS_MIN, math.min(MK_UNITS_MAX, tonumber(sget("mk_units")) or 100))

-- The Trade tab's Floor box (2.8.0): the value a RIGHT-CLICK on a good's name in
-- the report applies as that good's floor ('atrade floorset <good>', via the
-- rclick= markup, API 1.16). Right-clicking a good already floored at exactly
-- this value clears it - the same toggle feel as the exempt left-click. 0 turns
-- the right button into an eraser: it only clears floors.
local mk_floorset = math.max(0, math.floor(tonumber(sget("mk_floorset")) or 500))

-- forward declarations (mk_finish / the feed watch call these before they're defined)
local at_schedule, at_draw, auto_trade_tick, publish_dispatch

-- ---------- phone notifications (plugin.<id>.notify convention) ----------
-- One source, default OFF: a healthy auto-trader dispatches all day, and a phone that
-- buzzes per cart is a phone that gets muted. Turn it on to watch the bot from afar.
local function publish_notify_state()
  scrye.setState(P .. "notify",
    string.format("Auto-trade dispatches\teach cart the auto-trader sends\t%s\tatrade notify %s",
      at.notify and "on" or "off", at.notify and "off" or "on"))
end

-- forward declaration (mk_header schedules an early finish)
local start_settle

local function mk_header(res)
  cur_res = trim(res):lower()
  market[cur_res] = {}             -- fresh data for this good
  got_data = true
  if cur_res == LAST_NAME then     -- last good replying: finish as soon as it settles
    start_settle(scan_token)
  end
end

local function mk_row(kind, town, price, qty, aff)
  if not cur_res then return end
  got_data = true
  town = trim(town)
  if town == "" then return end
  local m = market[cur_res]
  m[town] = m[town] or {}
  if kind == "buy" then
    m[town].buy = tonumber(price); m[town].sup = tonumber(qty)
  else
    m[town].sell = tonumber(price); m[town].dem = tonumber(qty)
  end
  m[town].aff = trim(aff)
end

-- rank towns to buy (cheapest) and to sell (dearest) per good, keeping stock.
-- ties break toward more stock so the headline town is also the best supplied.
local function mk_compute()
  results = {}
  for res, towns in pairs(market) do
    local buys, sells = {}, {}
    for town, d in pairs(towns) do
      if d.buy  then buys[#buys + 1]   = { price = d.buy,  town = town, qty = d.sup or 0 } end
      if d.sell then sells[#sells + 1] = { price = d.sell, town = town, qty = d.dem or 0 } end
    end
    if #sells > 0 then      -- include sell-only goods (produced, no town supplies them)
      table.sort(sells, function(a, b)
        if a.price ~= b.price then return a.price > b.price end return a.qty > b.qty end)
      local profit = nil
      if #buys > 0 then
        table.sort(buys, function(a, b)
          if a.price ~= b.price then return a.price < b.price end return a.qty > b.qty end)
        profit = sells[1].price - buys[1].price
      end
      -- profit is nil for sell-only goods (no buys), a number otherwise.
      results[#results + 1] = {
        res = DISPLAY[res] or titlecase(res), cmd = res, buys = buys, sells = sells, profit = profit,
      }
    end
  end
  -- arbitrage goods (with a buy side) first by profit; sell-only goods after, by sell price
  table.sort(results, function(a, b)
    if a.profit and b.profit then return a.profit > b.profit end
    if a.profit ~= nil then return true end
    if b.profit ~= nil then return false end
    return a.sells[1].price > b.sells[1].price
  end)
end

-- ====================== persistence (scrye.store, strings only) ======================
local function mk_serialize()
  local out = {}
  for res, towns in pairs(market) do
    for town, d in pairs(towns) do
      out[#out + 1] = table.concat({
        res, town,
        d.buy and tostring(d.buy) or "", d.sup and tostring(d.sup) or "",
        d.sell and tostring(d.sell) or "", d.dem and tostring(d.dem) or "",
        (d.aff or ""):gsub("[\t\n]", " "),
      }, "\t")
    end
  end
  return table.concat(out, "\n")
end

local function mk_deserialize(s)
  local m = {}
  for line in s:gmatch("[^\n]+") do
    local res, town, buy, sup, sell, dem, aff =
      line:match("^([^\t]*)\t([^\t]*)\t([^\t]*)\t([^\t]*)\t([^\t]*)\t([^\t]*)\t(.*)$")
    if res and res ~= "" and town and town ~= "" then
      m[res] = m[res] or {}
      m[res][town] = {
        buy = tonumber(buy), sup = tonumber(sup),
        sell = tonumber(sell), dem = tonumber(dem), aff = aff,
      }
    end
  end
  return m
end

-- ====================== report rendering (HUD text widget) ======================
local FMT = "%-11s %4s %-14s %6s  %4s %-14s %6s  %s"

local function cell(e)                 -- price, town, stock strings for one side
  if not e then return "-", "", "" end
  local q = tostring(e.qty) .. (e.qty < LOWSTOCK and "*" or "")
  return tostring(e.price), e.town:sub(1, 14), q
end

-- extra towns to show for a side: 2nd if the 1st is low (<100), 3rd if the 1st two are both low
local function extra_towns(list)
  local t1 = list and list[1]
  if not t1 then return nil, nil end
  local t2 = (t1.qty < LOWSTOCK) and list[2] or nil
  local t3 = (t2 and t2.qty < LOWSTOCK) and list[3] or nil
  return t2, t3
end

local function mk_render(status)
  last_status = status or last_status
  scrye.setState(P .. "status", last_status)
  local lines = {}
  lines[#lines + 1] = string.format(FMT, "Good", "Buy", "Town", "Stk", "Sell", "Town", "Stk", "Profit")
  if #results == 0 then
    lines[#lines + 1] = "No data - click Refresh (or type mkref)."
  else
    -- Markup characters are invisible to the renderer, so cells are padded on the
    -- RAW text and wrapped in markup AFTER -- never string.format over markup.
    local function padl(v, w) v = tostring(v or ""); return string.rep(" ", math.max(0, w - #v)) .. v end
    local function padr(v, w) v = tostring(v or ""); return v .. string.rep(" ", math.max(0, w - #v)) end
    -- a town cell dispatches the configured Units of this row's good there on click
    -- (buy side blue = daler out, sell side green = daler in). cell() truncates the
    -- display name to 14; TOWNCMD knows the truncated forms, so the click still lands.
    local function town_link(town, side, cmd)
      if town == "" then return padr("", 14) end
      return string.format("@{%s,click=mkdispatch %s %s %s}%s@{}%s",
        side == "buy" and "info" or "success", side, cmd, town_cmd(town),
        esc(town), string.rep(" ", math.max(0, 14 - #town)))
    end
    for _, r in ipairs(results) do
      local cmd = disp_cmd(r.cmd)
      local bp, bt, bq = cell(r.buys[1])
      local sp, st, sq = cell(r.sells[1])
      local profit
      if r.profit then
        profit = (r.profit >= 0 and "+" or "") .. r.profit
      else
        profit = "sell"          -- sell-only good, no buy side
      end
      -- the good's name toggles held/released on click; held goods wear amber
      -- (the MUSHclient window tinted them blue -- same idea, theme-aware colour),
      -- FLOORED goods wear info-blue (held wins when a good is both: held means
      -- it is not traded at all, which is the stronger statement)
      -- menu= FIRST (API 1.19): the right button offers hold/floor as a NAMED menu, with
      -- the labels reflecting this good's current state. The value stays comma-free on
      -- purpose - a pre-1.19 host reads menu= as unknown flags and falls through to the
      -- rclick= behind it (the 1.16 floor toggle), and a pre-1.16 host to the click=.
      local mtail = at.floors[cmd]
        and string.format(";Clear floor (%d)|atrade floor %s 0", at.floors[cmd], cmd) or ""
      local goodcell = string.format(
        "@{%s,menu=%s|atrade exempt %s;Set floor %d|atrade floorset %s%s,rclick=atrade floorset %s,click=atrade exempt %s}%s@{}",
        at.exempt[cmd] and "warning" or (at.floors[cmd] and "info" or "text"),
        at.exempt[cmd] and "Release from hold" or "Hold (never trade)", cmd,
        mk_floorset, cmd, mtail, cmd, cmd, padr(esc(r.res), 11))
      local function row(label, b)
        return label .. " " .. padl(b.bp, 4) .. " " .. town_link(b.bt, "buy", cmd) .. " "
          .. padl(b.bq, 6) .. "  " .. padl(b.sp, 4) .. " " .. town_link(b.st, "sell", cmd) .. " "
          .. padl(b.sq, 6) .. "  " .. (b.profit or "")
      end
      lines[#lines + 1] = row(goodcell, { bp = bp, bt = bt, bq = bq, sp = sp, st = st, sq = sq, profit = profit })
      local b2, b3 = extra_towns(r.buys)
      local s2, s3 = extra_towns(r.sells)
      if b2 or s2 then
        local xbp, xbt, xbq = "", "", ""
        if b2 then xbp, xbt, xbq = cell(b2) end
        local xsp, xst, xsq = "", "", ""
        if s2 then xsp, xst, xsq = cell(s2) end
        lines[#lines + 1] = row(padr("  or", 11), { bp = xbp, bt = xbt, bq = xbq, sp = xsp, st = xst, sq = xsq })
      end
      if b3 or s3 then
        local xbp, xbt, xbq = "", "", ""
        if b3 then xbp, xbt, xbq = cell(b3) end
        local xsp, xst, xsq = "", "", ""
        if s3 then xsp, xst, xsq = cell(s3) end
        lines[#lines + 1] = row(padr("  or", 11), { bp = xbp, bt = xbt, bq = xbq, sp = xsp, st = xst, sq = xsq })
      end
    end
    lines[#lines + 1] = ""
    lines[#lines + 1] = "@{dim}* stock under " .. LOWSTOCK .. " (next-best town shown)@{}"
    lines[#lines + 1] = "@{dim}click a good to hold it (@{}@{warning}held@{}@{dim} = never auto-traded) - click a town to send Units@{}"
  end
  scrye.setState(P .. "report", table.concat(lines, "\n"))
  if publish_dispatch then publish_dispatch() end
end

-- ====================== refresh scan ======================
local mk_finish

-- keep waiting while new market data is still arriving; finalize when it settles
start_settle = function(tok)
  if settle_running then return end
  settle_running = true
  local function step()
    if not scanning or tok ~= scan_token then settle_running = false; return end
    checks = checks + 1
    if got_data and checks < 25 then     -- output still flowing: wait another beat
      got_data = false
      scrye.after(2, step)
    else
      settle_running = false
      mk_finish()
    end
  end
  scrye.after(2, step)
end

mk_finish = function()
  if not scanning then return end
  scanning = false                       -- close the gag window
  mk_compute()
  -- publish best sell prices for other plugins (e.g. Viking Status settler upkeep)
  local px = {}
  for _, r in ipairs(results) do
    local p = (r.sells and r.sells[1] and r.sells[1].price)
           or (r.buys and r.buys[1] and r.buys[1].price)
    if p then px[#px + 1] = r.cmd .. "=" .. p end
  end
  scrye.setVariable("prices", table.concat(px, ";"))
  scrye.setState(P .. "prices", table.concat(px, ";"))
  scrye.store.set("market", mk_serialize())     -- survive restarts
  mk_refreshed_at = os.time()
  mk_render("updated this session - " .. #results .. " goods")
  if not quiet then scrye.print("refreshed " .. #results .. " goods") end
  if at.on and at_schedule then at_schedule() end   -- dispatch on the freshly-refreshed prices
end

-- ---------- Guild.TradeGoods: the market straight from the feed ----------
-- Server-side since 28 Aug pm; the codes cracked 29 Aug. The per-village price
-- table the 30-command `vtrade goods` TEXT SCAN collects arrives pushed, every
-- burst carrying one village's rows. Goods come as 30 SHORT CODES; this decoder
-- ring was solved MECHANICALLY, never guessed: the overview's PP..DD symbol grid
-- (14 villages) and a `vtrade goods midgard` price table were each matched to
-- the 23:09 capture by assignment optimisation, and the two independent answers
-- agreed on all 30 codes with zero mismatches (the codes are arbitrary - c is
-- wool, d is eggs, x is beef - which is exactly why guessing was refused).
-- lin 0 is Midgard (confirmed numerically); lin i>=1 is rtargets_lineage[i]
-- (Guild.Fleet hands the live list over; TG_ORDER is the 29 Aug overview's
-- order as fallback). From its first rows the feed OWNS the market: mkref stops
-- sending, the staleness clock stays fresh, and the text scan remains only for
-- a server without the package.
local CODE2RES = { a="sunstone", b="bread", c="wool", cs="cheese", d="eggs",
  e="fine furs", f="furs", g="grain", h="fish", hm="horsemeat", i="iron",
  j="gemstones", k="salted fish", l="tools", m="mead", mi="milk", n="finery",
  o="ore", p="pork", q="mutton", r="runestones", s="spoils", sm="smoked meat",
  t="timber", u="armour", v="poultry", w="weapons", x="beef", y="honey", z="cloth" }
local AFF = { [-3]="export+", [-2]="export", [-1]="minor export", [0]="neutral",
              [1]="slight demand", [2]="in demand", [3]="high demand" }
local TG_ORDER = { [0]="Midgard", "Lodbrok's Hold", "Eiriksby", "Imaird", "Holmgard",
  "Hafrfjord", "Uppsala", "Borgarfjord", "Vestergotland", "Sverkersby", "Ericsgard",
  "Birka", "Lejre", "Nidaros" }
local tg_feed_live = false
local tg_towns = nil          -- lin -> town, live from Guild.Fleet rtargets_lineage
local tg_pending = false      -- one recompute per burst run, not one per page

local function mk_refresh(is_quiet)
  if tg_feed_live then
    if not is_quiet then
      scrye.print("the market feed (Guild.TradeGoods) is live - prices update on their own; nothing to scan")
    end
    return
  end
  if scanning then
    if not is_quiet then scrye.print("refresh already in progress") end
    return
  end
  scanning = true
  quiet = is_quiet and true or false
  if not is_quiet then user_refreshed = true end
  cur_res = nil
  market = {}            -- drop stale data so a good that stops responding disappears
  got_data = false
  checks = 0
  settle_running = false
  scan_token = scan_token + 1
  local tok = scan_token
  -- space the sends out (original throttled at 0.4 s; scrye.after ticks at 1 s)
  for i, r in ipairs(RES) do
    local cmd = "vtrade goods " .. r.cmd
    if i == 1 then
      scrye.send(cmd)
    else
      scrye.after(i - 1, function()
        if scanning and tok == scan_token then scrye.send(cmd) end
      end)
    end
  end
  -- settle fallback in case the last good's header never arrives
  scrye.after(#RES + 2, function()
    if scanning and tok == scan_token then start_settle(tok) end
  end)
  -- absolute hard cap: never leave the gag window open forever
  scrye.after(#RES + 45, function()
    if scanning and tok == scan_token then mk_finish() end
  end)
  scrye.setState(P .. "status", "refreshing market...")
  if not quiet then scrye.print("refreshing market...") end
end

-- one village's rows from a completed Guild.TradeGoods burst -> the market table
local function tg_feed(t)
  if type(t.goods) ~= "table" then return end
  local changed = false
  for _, row in ipairs(t.goods) do
    if type(row) == "table" then
      local res  = CODE2RES[tostring(row.good or "")]
      local lin  = tonumber(row.lin)
      local town = res and lin and ((tg_towns and tg_towns[lin]) or TG_ORDER[lin])
      if res and town then
        market[res] = market[res] or {}
        market[res][town] = { buy = tonumber(row.buy), sup = tonumber(row.sup),
                              sell = tonumber(row.sell), dem = tonumber(row.dem),
                              aff = AFF[tonumber(row.score)] }
        changed = true
      end
    end
  end
  if not changed then return end
  if not tg_feed_live then
    tg_feed_live = true
    scrye.print("@{#DEB218,bold}[market]@{} Guild.TradeGoods is live - prices now arrive on their own (mkref retired)")
  end
  mk_refreshed_at = os.time()      -- the auto-trader's staleness clock: always fresh now
  if not tg_pending then
    tg_pending = true
    scrye.after(2, function()
      tg_pending = false
      mk_compute()
      scrye.store.set("market", mk_serialize())
      mk_render("live from the feed - " .. #results .. " goods")
      if publish_dispatch then publish_dispatch() end
      if at_draw then at_draw() end
      -- fresh prices are a dispatch trigger, same as mk_finish after a scan -
      -- also what un-parks a trader that armed before the first burst computed
      if at.on and at_schedule then at_schedule() end
    end)
  end
end

-- ====================== gag + parse (replaces the "market" trigger group) ======================
-- while a scan is in flight, the vtrade-goods block is parsed and gagged
-- (return false), exactly like the original's omit_from_output trigger group,
-- which was only enabled during a refresh.
--
-- The whole vtrade block is wrapped in a "-~*  ...  *~-" decoration frame, e.g.
--   -~*   Timber - Market Overview                       *~-
--   -~*  Lodbrok's Hol    22  413 avail export++ Vinur    *~-
-- The MUSHclient regexes were unanchored, so they matched the town/price/qty
-- substring inside the frame. We strip the frame first, then match on the
-- clean text, and gag every framed line (the block is all ours during a scan).

-- remove the leading "-~*" and trailing "*~-" decoration (and the padding spaces)
local function strip_frame(s)
  s = s:gsub("^%s*%-~%*%s*", "")   -- leading  "-~*" + spaces
  s = s:gsub("%s*%*~%-%s*$", "")   -- trailing "*~-" + spaces
  return s
end

-- match a market row on frame-stripped text: town, price, qty, then the keyword
-- ("avail"/"wants") on a word boundary, then the affinity remainder.
--
-- Structure: a cheap plain-text find gates out the lines that can't match (most of the
-- block), then small separate patterns pick the pieces apart — faster and easier to
-- reason about than one monolithic backtracking pattern. (Originally split to dodge a
-- MoonSharp "pattern too complex" abort; kept on native Lua 5.4 because it's simply
-- the better shape.)
local function rowmatch(line, word)
  if not line:find(word, 1, true) then return nil end             -- cheap gate: keyword must be present
  local price, qty, tail = line:match("(%d+)%s+(%d+)%s+" .. word .. "(.*)$")
  if not price then return nil end
  local town = line:match("^%s*([%a][%a' ]*)%s+%d") or ""          -- leading letters/'/space run before the price
  town = (town:gsub("%s+$", ""))                                    -- drop the greedy town's trailing spaces
  if tail == "" then return town, price, qty, "" end
  local aff = tail:match("^[^%w](.*)$")   -- word boundary: "available" (tail "able...") is rejected
  if aff == nil then return nil end
  return town, price, qty, aff
end

scrye.onLine(function(line)
  if not scanning then return end
  -- our own scan commands, if echoed
  if line:match("^%s*vtrade goods %a") then return false end
  -- only touch the framed vtrade block; leave everything else (tells, etc.) alone
  if not line:match("^%s*%-~%*") then return end
  local clean = strip_frame(line)
  -- "<Good> - Market Overview" header (plain-find gate first: skips the backtracking
  -- pattern on the long all-letter column-header lines it can never match)
  if clean:find("Market Overview", 1, true) then
    local res = clean:match("^([%a][%a ]*)%s*%-%s*Market Overview")
    if res then
      pcall(mk_header, res)
      return false
    end
  end
  -- buy row:  Town  <price>  <qty> avail [affinity]
  local town, price, qty, aff = rowmatch(clean, "avail")
  if town then
    pcall(mk_row, "buy", town, price, qty, aff)
    return false
  end
  -- sell row: Town  <price>  <qty> wants [affinity]
  town, price, qty, aff = rowmatch(clean, "wants")
  if town then
    pcall(mk_row, "sell", town, price, qty, aff)
    return false
  end
  -- The remaining vtrade decoration. Only the lines the MUSHclient version gagged are
  -- hidden here: a blanket `return false` also swallowed unrelated framed output (channel
  -- banners, `vbuild list`) whenever a background auto-trader scan happened to be running.
  if clean:find("price", 1, true) and clean:match("price%s+%d+%s+daler") then return false end
  if clean:find("Trading Post tier", 1, true) then return false end
  if clean:find("Best places to", 1, true) then return false end
  if clean:find("Settlement", 1, true) and clean:match("Settlement%s+Price") then return false end
  if clean:match("^[%s%-=~%*%+_|]*$") then return false end   -- separators / empty frame lines
  return
end)

-- market tick: the periodic price-update line carries percentages (e.g. "Mead +3%").
-- Other [Viking-Trade] lines (cart returns etc.) have no percentages and are ignored.
-- Debounced (the trade update is a burst of lines); quiet background refresh; only
-- after the user has refreshed once this session (stand-in for the original's
-- "window visible or auto-trader on" gate).
scrye.addTrigger{
  pattern = [[\[Viking-Trade\].*\d%]],
  regex = true,
  run = function()
    if not user_refreshed then return end
    -- our own `vtrade dispatch` echo comes back as a [Viking-Trade] line with a
    -- percentage in it; without this guard every auto-dispatch kicks off a full rescan.
    if os.time() - mk_last_dispatch < 10 then return end
    if update_pending or scanning then return end
    update_pending = true
    scrye.after(2, function()
      update_pending = false
      if not scanning then mk_refresh(true) end
    end)
  end,
}

-- ====================== auto-trader ======================
local function note(s) scrye.print("@{#DEB218,bold}[auto]@{} " .. s) end

-- read the shared viking feed (daler / warehouse / carts / buildings) from vik.* state
local function feed(k) return V[k] or "" end   -- the translated feed (outer V, via upvalue)
local function at_getvars()
  return {
    DALER = feed("daler"), WSTOCK = feed("wstock"), CARTS = feed("carts"),
    CIDLE = feed("cidle"), CUPG = feed("cupg"), CDTIME = feed("cdtime"),
    BUILDINGS = feed("buildings"),
  }
end

-- Trading Post tier -> (max carts = tier, largest cart capacity seen in the feed)
local WCAP = { 400, 1000, 1750, 3000, 5250 }   -- warehouse unit cap, tier 1..5
local function at_capacity(v)
  local tier = tonumber((v.BUILDINGS or ""):match("trading_post:(%d+)")) or 1
  local function nth(s, n)
    local i = 0
    for p in (s .. "|"):gmatch("([^|]*)|") do i = i + 1; if i == n then return tonumber(p) end end
  end
  local cap = 0
  for e in (v.CIDLE or ""):gmatch("[^;]+") do local c = nth(e, 4);  if c and c > cap then cap = c end end
  for e in (v.CARTS or ""):gmatch("[^;]+") do local c = nth(e, 11); if c and c > cap then cap = c end end
  if cap <= 0 then cap = ({ 20, 30, 65, 90, 125 })[tier] or 20 end
  return tier, cap
end
local function at_warehouse(v)
  local used = 0
  for entry in (v.WSTOCK or ""):gmatch("[^;]+") do
    local a = entry:match("^[^|]+|(%d+)"); if a then used = used + tonumber(a) end
  end
  local tier = tonumber((v.BUILDINGS or ""):match("warehouse:(%d+)")) or 3
  -- WSTOCK's leading field (before the first good) is the EFFECTIVE capacity, and it is the one
  -- to believe. WCAP is the base capacity per tier -- correct only for a character who has
  -- raised none of the storage skills, which add on top of it. A tier-5 hold reads 5250 from
  -- the table and 7367 from the feed once those skills are in.
  --
  -- This mattered beyond display: the auto-trader stops buying as it approaches capacity, so
  -- trusting the base table made it stop roughly 2000 units early for any skilled character --
  -- silently refusing to fill space that was there. WCAP stays as the pre-feed fallback.
  local fed = tonumber((v.WSTOCK or ""):match("^(%d+);"))
  return used, fed or WCAP[tier] or 1750, tier
end

-- ---------- trade log + stats ----------
local function at_record(side, qty, cmd, town, amount)
  amount = amount or 0
  local s = at.stats
  if side == "sell" then s.sells = s.sells + 1; s.earned = s.earned + amount
  else s.buys = s.buys + 1; s.spent = s.spent + amount end
  local line = string.format("[%s] %-4s %4d %-11s %-4s %-14s ~%dd",
    os.date("%Y-%m-%d %H:%M:%S"), side:upper(), qty, cmd,
    (side == "buy") and "from" or "to", town, amount)
  s.recent[#s.recent + 1] = line
  while #s.recent > 40 do table.remove(s.recent, 1) end
  scrye.log(line)
  -- the one choke point every auto-dispatch passes through, so the phone notify
  -- lives here instead of at the three send sites
  if at.notify then
    scrye.notify(string.format("auto-trade: %s %d %s %s %s (~%dd)",
      side, qty, cmd, (side == "buy") and "from" or "to", town, amount))
  end
  if at_draw then at_draw() end
end

-- ---------- a dispatch is not a trade until a cart goes out ----------
-- at_record used to run on the SEND, which meant the log and the running totals recorded
-- the ATTEMPT. A cart that is refitting, a cooldown that has not expired, daler that ran
-- short -- the MUD refuses, nothing leaves, and the trade log says it happened anyway.
--
-- So the record is held until the CART FEED proves a cart went out. Structured data rather
-- than English, which means it cannot be broken by a reworded refusal and it catches every
-- reason at once, including the ones neither of us has thought of. Unconfirmed after
-- CONFIRM_SECS it is dropped and said out loud -- an under-counted log is a smaller lie
-- than an inflated one, but a SILENT under-count is worse than either.
--
-- Two things this got wrong when it was written, both of which lose a trade that really
-- happened:
--
--  * One pending slot. A second dispatch inside CONFIRM_SECS overwrote the first with no
--    log line and no complaint. The cooldown retry runs at 10s and CONFIRM_SECS is 12, so
--    two dispatches overlapping is the ordinary cadence rather than a corner case. Pending
--    dispatches are a LIST now, oldest first.
--
--  * Counting carts instead of identifying them. "More carts than before" cannot see a cart
--    coming home in the same feed tick as one going out: the count is unchanged, so a cart
--    plainly on the road read as "nothing left". Carts are matched by kind+good now, so what
--    is compared is which carts are out, not how many.
local CONFIRM_SECS = 12
local at_pend = {}        -- dispatches sent, not yet proved by the feed; oldest first
local at_seen = nil       -- the carts that were out when the oldest of them went

-- THE CARTYARD CLOCK (2.9.0, live soak 28 Aug): the game allows one caravan
-- roughly every 3 minutes, and answers a dispatch inside that window with a
-- framed "The cartyard is preparing your last caravan. / Ready in: 2m 40s"
-- instead of a cart - which the trader used to bounce off every retry tick,
-- a doomed command every ~15s for the whole cooldown. Now a hold starts at
-- every dispatch (at.yard secs, provisional), the refusal message corrects
-- the clock to the yard's own number, and the tick simply waits it out.
local at_yard_until = -math.huge   -- now_s the yard is busy until
local at_yard_provisional = false  -- true = guessed at send; false = the yard said so
local at_cy_at = -math.huge        -- when the "cartyard is preparing" line last passed

-- The carts on the road, counted per kind+good. Counted rather than a set because two carts
-- of the same good really can be out at once.
local function at_cart_sigs(v)
  local t = {}
  for e in ((v or at_getvars()).CARTS or ""):gmatch("[^;]+") do
    local kind, good = e:match("^([^|]*)|([^|]*)")
    local sig = trim(kind or ""):lower() .. "|" .. trim(good or ""):lower()
    t[sig] = (t[sig] or 0) + 1
  end
  return t
end

-- How many carts are out now that were not out before, split by kind.
local function at_appeared(before, after)
  local by = {}
  for sig, n in pairs(after) do
    local extra = n - (before[sig] or 0)
    if extra > 0 then
      local side = sig:match("^([^|]*)")
      by[side] = (by[side] or 0) + extra
    end
  end
  return by
end

-- Credit what the feed proves, then drop what has waited too long. Called on every feed
-- tick and once per dispatch from its own timeout timer.
local function at_settle(v)
  if #at_pend == 0 then at_seen = nil; return end

  local cur = at_cart_sigs(v)
  if at_seen then
    local appeared = at_appeared(at_seen, cur)

    -- Walk the pending list in the order the dispatches went out -- so the log reads in
    -- that order too -- and give each one a cart of its own kind if one appeared.
    --
    -- Kind, and not the good as well: the feed names goods the way it DISPLAYS them
    -- ("smoked meat") and a dispatch names them the way you COMMAND them ("smoked"), so a
    -- mapping between the two would go quietly wrong the first time a good was renamed.
    -- Matching on kind alone can put a cart against the wrong one of two sells sent
    -- seconds apart; it can never lose one, which is the property that matters.
    local i = 1
    while i <= #at_pend do
      local p = at_pend[i]
      local n = appeared[p.side] or 0
      if n > 0 then
        appeared[p.side] = n - 1
        table.remove(at_pend, i)
        at_record(p.side, p.qty, p.cmd, p.town, p.amount)
      else
        i = i + 1
      end
    end

    -- Carts of a kind nothing pending claimed. If the feed ever spells its kinds
    -- differently than we do, they land on the oldest dispatch -- a cart we can SEE on the
    -- road must not strand a trade in the holding pen until it times out.
    local spare = 0
    for _, n in pairs(appeared) do if n > 0 then spare = spare + n end end
    while spare > 0 and at_pend[1] do
      spare = spare - 1
      local p = table.remove(at_pend, 1)
      at_record(p.side, p.qty, p.cmd, p.town, p.amount)
    end
  end
  at_seen = cur

  local dropped = false
  while at_pend[1] and now_s - at_pend[1].at >= CONFIRM_SECS do
    local p = table.remove(at_pend, 1)
    dropped = true
    note(string.format("%s %d %s never left (no cart went out) - not logged",
      p.side, p.qty, p.cmd))
    if at_draw then at_draw() end
  end
  -- a dispatch that never left did not take the cartyard either: release a hold we
  -- only GUESSED at send time (a real "Ready in" refusal re-sets the clock itself,
  -- and that one stands - the yard said so)
  if dropped and at_yard_provisional then
    at_yard_until = -math.huge
    at_yard_provisional = false
  end
  if #at_pend == 0 then at_seen = nil end
end

-- Provisionally spend the market we just dispatched against: a sell cart consumes the
-- town's demand, a buy cart its supply. Guild.TradeGoods pushes only when prices MOVE,
-- so between bursts the local table is the only memory that a cart is already on the
-- road - without this the trader re-picked the same town every pass until the feed
-- spoke, stacking carts into demand the first one had already satisfied (29 Aug live).
-- In-memory only (never persisted - it is a guess about the near future, not data);
-- the next feed row for that good overwrites it with the server's truth.
local function mk_debit(rescmd, town, side, qty)
  local m = market[rescmd] and market[rescmd][town]
  if not m then return end
  if side == "sell" then
    if m.dem then m.dem = math.max(0, m.dem - qty) end
  else
    if m.sup then m.sup = math.max(0, m.sup - qty) end
  end
end

-- Queue a dispatch we have just sent. Replaces the at_record call at each send site.
local function at_queue(side, qty, cmd, town, amount)
  -- The baseline belongs to the OLDEST outstanding dispatch, so a later one joining the
  -- queue must not move it: a cart that appeared since the last settle is still owed to
  -- whoever was waiting for it.
  if #at_pend == 0 then at_seen = at_cart_sigs() end
  at_pend[#at_pend + 1] = { side = side, qty = qty, cmd = cmd, town = town,
                            amount = amount or 0, at = now_s }
  scrye.after(CONFIRM_SECS + 1, function() at_settle() end)
end

local at_cd_retry

-- one dispatch pass: pick the single most worthwhile cart to send (restock > sells/scalps)
auto_trade_tick = function()
  at.pending_check = false
  if not at.on then return end
  if not connected then return end
  if scanning then return end   -- a refresh is in flight; mk_finish re-runs us on fresh data
  if now_s < at_yard_until then return end   -- the cartyard is still preparing the last caravan

  local v = at_getvars()
  -- STOCK GUARD: a trader that cannot see stock reads every pile as zero - it
  -- would never sell and, with restock on, would buy without limit. So it refuses
  -- to run until WSTOCK carries goods rows. With Guild.Warehouse live (28 Aug pm)
  -- those rows arrive on their own; without it the quiet scan fetches them.
  if not (v.WSTOCK or ""):find(";%a") then
    ws_auto_scan()
    if not ws_warned then
      note("auto-trade waiting: no warehouse stock yet"
        .. (ws_feed_live and " - Guild.Warehouse has not listed goods" or " - scanning (vtrade stock)"))
      ws_warned = true
    end
    return
  end
  -- Staleness only matters for TEXT-SCANNED stock (a snapshot that ages while the
  -- warehouse moves): past 90 s a quiet re-scan runs instead and the next pass
  -- dispatches on fresh numbers. Feed-owned stock never ages this way - the server
  -- pushes every change - so the feed skips the hold entirely (a re-scan it would
  -- wait for can never come, and would stall the trader for good).
  if not ws_feed_live and (now_s - ws_scanned_at) > 90 then
    ws_auto_scan()
    return
  end
  local maxc, cap = at_capacity(v)
  if at.carts and at.carts > 0 then maxc = math.min(maxc, at.carts) end

  if v.CARTS ~= at.last_carts then at.pending = 0; at.last_carts = v.CARTS end
  local active = 0
  for _ in (v.CARTS or ""):gmatch("[^;]+") do active = active + 1 end
  local upgrading = 0
  for _ in (v.CUPG or ""):gmatch("[^;]+") do upgrading = upgrading + 1 end
  local free = maxc - active - upgrading - (at.pending or 0)
  if free <= 0 then return end
  local cd = tonumber(v.CDTIME) or 0
  if cd > 0 then
    if not at.cd_wait then at.cd_wait = true; scrye.after(cd + 1, at_cd_retry) end
    return
  end
  free = math.min(free, 1)   -- one dispatch per pass (each starts a fresh cooldown)

  -- Staleness only matters for TEXT-SCANNED prices, exactly like the warehouse
  -- above: once Guild.TradeGoods owns the market the server pushes every change,
  -- so age means "nothing moved", not "we can't see". Gating the feed on this
  -- clock BRICKED the trader (29 Aug live): the feed goes quiet whenever prices
  -- hold still, mk_refresh under a live feed deliberately does nothing, and every
  -- pass bailed here forever - market perfect, zero carts.
  if #results == 0 or (not tg_feed_live and (os.time() - mk_refreshed_at) > 60) then
    mk_refresh(true); return   -- prices stale: refresh, mk_finish re-runs us
  end

  -- warehouse stock per good (normalise "fine_furs" -> "fine furs")
  local stock = {}
  for entry in (v.WSTOCK or ""):gmatch("[^;]+") do
    local g, a = entry:match("^([^|]+)|(%d+)")
    if g then local k = trim(g):lower():gsub("_", " "); stock[k] = (stock[k] or 0) + tonumber(a) end
  end
  local function have_of(r)
    return stock[(r.cmd or ""):gsub("_", " ")] or stock[(r.res or ""):lower():gsub("_", " ")] or 0
  end

  -- goods already inbound on a BUY cart: don't double up
  local inbound = {}
  for entry in (v.CARTS or ""):gmatch("[^;]+") do
    local kind, good = entry:match("^(%a+)|([^|]+)")
    if kind == "buy" and good then inbound[trim(good):lower()] = true end
  end

  local used, wcap = at_warehouse(v)
  local fillpct  = (wcap > 0) and (used * 100 / wcap) or 0
  local pressure = fillpct >= (at.soft or 70)
  local clearing = fillpct >= (at.full or 90)
  local fillmin  = clearing and (at.clear_pct or 25) or (at.min_pct or 70)
  local need = math.max(1, math.min(cap, math.ceil(cap * fillmin / 100)))

  -- SELL: offload warehouse stock to its best-paying town. Candidates are ranked
  -- by CART VALUE (units x best sell price) -- the market report's Profit column is
  -- never consulted here; profit is an arbitrage number and these are goods we
  -- already own. A small cart of something expensive legitimately outranks a full
  -- cart of something cheap.
  local cand = {}
  for _, r in ipairs(results) do
    local dc = disp_cmd(r.cmd)
    if not at.exempt[dc] and r.sells and r.sells[1] then
      local have  = have_of(r)
      local isref = REFINED[r.cmd] and true or false
      local reserve = at.keep or 20
      if not (isref or SPECIAL[r.cmd]) then reserve = math.max(reserve, at.stock or 300) end
      -- a per-good floor RAISES the reserve, never lowers it: "keep 500 grain"
      -- protects the pile whatever category the good falls into
      reserve = math.max(reserve, at.floors[dc] or 0)
      local avail = have - reserve
      if isref and not at.refined then avail = 0 end
      -- Candidacy needs only a dispatchable amount (the game minimum), NOT the cart
      -- fill-minimum: gating sells on `need` starved high-price goods -- finery had
      -- to pile up past 70% of a cart before it could even enter the race, while
      -- cheap bulk qualified every pass. Junk carts still stay off the road: the
      -- value floor below refuses anything under min_rel% of the best load.
      if avail >= MK_UNITS_MIN then
        local best, bestq = nil, nil
        for _, s in ipairs(r.sells) do
          local dem = tonumber(s.qty)
          -- dem 0 is an ANSWER, not a gap: the town won't buy more, so it gets no
          -- cart (the next town in r.sells competes instead). The cap fallback is
          -- only for a row whose demand column never parsed - treating a real 0 as
          -- unknown shipped full carts into satisfied towns (29 Aug live).
          local qty = math.min(avail, cap, dem or cap)
          if qty >= MK_UNITS_MIN then   -- below the game's dispatch minimum isn't a cart
            local val = qty * (s.price or 0)
            if not best or val > best.value then best = { town = s.town, qty = qty, value = val } end
            if not bestq or qty > bestq.qty or (qty == bestq.qty and val > bestq.value) then
              bestq = { town = s.town, qty = qty, value = val }
            end
          end
        end
        local isflush = (at.flush and at.flush > 0 and have >= at.flush) or false
        local pick = isflush and bestq or best
        if pick then
          cand[#cand + 1] = { kind = "sell", cmd = r.cmd, town = pick.town, qty = pick.qty,
                              value = pick.value, avail = avail, flush = isflush }
        end
      end
    end
  end

  -- SCALPER: buy low / sell high (competes on value with the sells above)
  local daler  = tonumber(v.DALER) or 0
  local budget = math.max(0, daler - (at.reserve or 0))
  local space  = math.max(0, wcap - used - 200)
  if at.scalp and not pressure and budget > 0 and space >= need then
    for _, r in ipairs(results) do
      if not at.exempt[disp_cmd(r.cmd)] and not inbound[disp_cmd(r.cmd)]
         and r.buys and r.buys[1] and r.sells and r.sells[1] then
        local buy, sell = r.buys[1], r.sells[1]
        local per = (sell.price or 0) - (buy.price or 0)
        if per >= (at.margin or 1) and (buy.price or 0) > 0 then
          local supply = tonumber(buy.qty)  or cap
          local demand = tonumber(sell.qty) or cap
          local afford = math.floor(budget / buy.price)
          local qty = math.min(cap, supply, demand, afford, space)
          if qty >= need then
            cand[#cand + 1] = { kind = "buy", cmd = r.cmd, town = buy.town, qty = qty,
                                value = qty * per, cost = qty * buy.price, unit = buy.price, per = per }
          end
        end
      end
    end
  end

  -- RESTOCK (top priority): buy raws back up to Raw> when low
  local restock = {}
  if at.restock and not clearing and budget > 0 and space >= need then
    for _, r in ipairs(results) do
      if RAWBUILD[r.cmd] and not at.exempt[disp_cmd(r.cmd)]
         and not inbound[disp_cmd(r.cmd)] and r.buys and r.buys[1] then
        local buy = r.buys[1]
        local have = have_of(r)
        -- a floored raw restocks up to its floor, not just to Raw>
        local goal = math.max(at.stock or 300, at.floors[disp_cmd(r.cmd)] or 0)
        if have < goal and (buy.price or 0) > 0 then
          local supply = tonumber(buy.qty) or cap
          local afford = math.floor(budget / buy.price)
          local qty = math.min(cap, supply, afford, space)
          if qty >= need then
            restock[#restock + 1] = { cmd = r.cmd, town = buy.town, qty = qty,
                                      cost = qty * buy.price, unit = buy.price, have = have }
          end
        end
      end
    end
    table.sort(restock, function(a, b) return a.have < b.have end)
  end

  -- rank sell/scalp candidates by cart value; flush piles jump the queue
  table.sort(cand, function(a, b)
    if (a.flush or false) ~= (b.flush or false) then return a.flush or false end
    return a.value > b.value
  end)
  local bestnf = 0
  for _, c in ipairs(cand) do if not c.flush and c.value > bestnf then bestnf = c.value end end
  local floor = pressure and 0 or (bestnf * (at.min_rel or 40) / 100)

  local sent, seen = 0, {}
  -- restock first: keep raw building materials topped up
  for _, c in ipairs(restock) do
    if sent >= free then break end
    if not seen[c.cmd] then
      local q = math.min(c.qty, space)
      local cost = q * (c.unit or 0)
      if q >= need and cost <= budget then
        scrye.send(string.format("vtrade dispatch buy %d %s %s escort %d",
          q, disp_cmd(c.cmd), town_cmd(c.town), at.escort))
        note(string.format("restock buy %d %s from %s (-%dd, had %d)", q, c.cmd, c.town, cost, c.have))
        at_queue("buy", q, disp_cmd(c.cmd), c.town, cost)
        mk_debit(c.cmd, c.town, "buy", q)
        budget = budget - cost; space = space - q; seen[c.cmd] = true
        at.pending = (at.pending or 0) + 1; sent = sent + 1
      end
    end
  end
  -- then the most valuable sell/scalp carts
  for _, c in ipairs(cand) do
    if sent >= free then break end
    if not c.flush and c.value < floor then break end
    if not seen[c.cmd] then
      if c.kind == "buy" then
        local q = math.min(c.qty, space)
        local cost = q * (c.unit or 0)
        if q >= need and cost <= budget then
          scrye.send(string.format("vtrade dispatch buy %d %s %s escort %d",
            q, disp_cmd(c.cmd), town_cmd(c.town), at.escort))
          note(string.format("scalp buy %d %s from %s (-%dd, ~+%dd margin)",
            q, c.cmd, c.town, cost, math.floor(q * (c.per or 0))))
          at_queue("buy", q, disp_cmd(c.cmd), c.town, cost)
          mk_debit(c.cmd, c.town, "buy", q)
          budget = budget - cost; space = space - q; seen[c.cmd] = true
          at.pending = (at.pending or 0) + 1; sent = sent + 1
        end
      else
        scrye.send(string.format("vtrade dispatch sell %d %s %s escort %d",
          c.qty, disp_cmd(c.cmd), town_cmd(c.town), at.escort))
        note(string.format("sell %d %s to %s (~%dd cart)", c.qty, c.cmd, c.town, c.value))
        at_queue("sell", c.qty, disp_cmd(c.cmd), c.town, c.value)
        mk_debit(c.cmd, c.town, "sell", c.qty)
        seen[c.cmd] = true; at.pending = (at.pending or 0) + 1; sent = sent + 1
      end
    end
  end

  if sent > 0 then
    mk_compute()   -- fold the mk_debit spends into `results` so the NEXT pass sees them
    mk_last_dispatch = os.time()
    -- the yard takes ~3 min per caravan: start the hold NOW rather than bouncing a
    -- doomed dispatch off the refusal every retry (at.yard 0 = old behaviour)
    local wait = 10
    if (at.yard or 0) > 0 then
      at_yard_until = now_s + at.yard
      at_yard_provisional = true
      wait = at.yard + 2
    end
    if not at.cd_wait then at.cd_wait = true; scrye.after(wait, at_cd_retry) end
  end
end

at_schedule = function()
  if at.pending_check then return end
  at.pending_check = true
  scrye.after(1, function() auto_trade_tick() end)
end

at_cd_retry = function()
  at.cd_wait = false
  auto_trade_tick()
end

-- The yard's own refusal: two framed lines, "The cartyard is preparing your last
-- caravan." then "Ready in: 2m 40s". The pair sets the clock to the yard's number
-- (authoritative - it replaces any send-time guess), quietly takes back the refused
-- dispatch (it never left; the 'never left' timeout would only add noise), and
-- books one retry for when the yard opens. Also fires on a MANUAL dispatch hitting
-- the cooldown, which is right: the yard is shared, so the auto-trader should wait.
local function at_yard_refused(txt)
  local secs = 0
  local m, s2 = tostring(txt or ""):match("(%d+)m%s*(%d+)s")
  if m then secs = tonumber(m) * 60 + tonumber(s2)
  else
    local mo = tostring(txt or ""):match("(%d+)m")
    local so = tostring(txt or ""):match("(%d+)s")
    secs = (tonumber(mo) or 0) * 60 + (tonumber(so) or 0)
  end
  if secs <= 0 then return end
  at_yard_until = now_s + secs + 2
  at_yard_provisional = false
  local p = table.remove(at_pend)         -- the refusal answers the newest dispatch
  if p then
    at.pending = math.max(0, (at.pending or 0) - 1)
    if #at_pend == 0 then at_seen = nil end
  end
  note(string.format("cartyard busy - next cart in %ds, holding", secs))
  if not at.cd_wait then at.cd_wait = true; scrye.after(secs + 3, at_cd_retry) end
  if at_draw then at_draw() end
end

scrye.addTrigger{
  pattern = [[^-~\*\s*The cartyard is preparing]],
  regex   = true,
  run     = function() at_cy_at = now_s end,
}
scrye.addTrigger{
  pattern = [[^-~\*\s*Ready in:\s*(.*)]],
  regex   = true,
  -- only trust a "Ready in" that follows the cartyard line - the phrase is too
  -- ordinary to act on alone
  run     = function(cap) if (now_s - at_cy_at) <= 3 then pcall(at_yard_refused, cap) end end,
}

local function at_driver() if at.on then at_schedule() end end

-- react to the live feed: dispatch when the warehouse gains goods or a cart frees up
local at_last_free, at_last_stock = -1, -1
local function at_on_feed()
  local v0 = at_getvars()
  at_settle(v0)   -- a cart appearing is what confirms a dispatch
  if not at.on then return end
  local v = v0
  local maxc = at_capacity(v)
  if at.carts and at.carts > 0 then maxc = math.min(maxc, at.carts) end
  local active = 0
  for _ in (v.CARTS or ""):gmatch("[^;]+") do active = active + 1 end
  local free = maxc - active
  local total = 0
  for e in (v.WSTOCK or ""):gmatch("[^;]+") do local a = e:match("|(%d+)"); if a then total = total + tonumber(a) end end
  local trig = (at_last_stock >= 0 and total > at_last_stock)
            or (at_last_free  >= 0 and free  > at_last_free)
  at_last_free, at_last_stock = free, total
  if trig then at_schedule() end
end

-- ---------- held / exempt goods ----------
local function at_save_exempt()
  local list = {}
  for k, on in pairs(at.exempt) do if on then list[#list + 1] = k end end
  table.sort(list); sset("at_exempt", table.concat(list, ",")); return list
end
local function at_toggle_exempt(word)
  word = trim(word or ""):lower(); word = DCMD[word] or word
  if word == "" then return end
  at.exempt[word] = (not at.exempt[word]) or nil
  at_save_exempt()
  mk_render(nil)          -- refresh the "#" held markers in the Market report
end

-- ---------- per-good stock floors ----------
-- A floor is "keep at least this many": Grain at 500 means the trader sells only
-- the surplus above 500, whatever the raw/refined category reserve would allow.
local function at_save_floors()
  local list = {}
  for k, n in pairs(at.floors) do list[#list + 1] = k .. "=" .. n end
  table.sort(list); sset("at_floors", table.concat(list, ","))
end
local function at_list_floors()
  local l = {}
  for k, n in pairs(at.floors) do l[#l + 1] = k .. "=" .. n end
  table.sort(l)
  note("floors (never sold below): " .. (#l > 0 and table.concat(l, ", ") or "(none)")
    .. "  - atrade floor <good> <n>; 0 or off clears")
end
local function at_set_floor(args)
  local word, n = trim(args or ""):lower():match("^(.-)%s+(%S+)$")
  if not word or word == "" then
    note("usage: atrade floor <good> <n>  (0 or off clears; bare 'atrade floor' lists)")
    return
  end
  word = DCMD[word] or word           -- same name normalisation the held list uses
  if n == "off" or tonumber(n) == 0 then
    if not at.floors[word] then note("no floor on " .. word); return end
    at.floors[word] = nil
    at_save_floors()
    note("floor cleared: " .. word .. " sells down to the normal reserve again")
  elseif tonumber(n) then
    at.floors[word] = math.floor(tonumber(n))
    at_save_floors()
    note(string.format("floor set: keep at least %d %s in the warehouse", at.floors[word], word))
  else
    note("usage: atrade floor <good> <n>  (0 or off clears)")
    return
  end
  mk_render(nil)          -- refresh the blue floored names in the Market report
end
-- the right-click path: apply (or toggle off) the Floor box's value on one good
local function at_floorset(word)
  word = trim(word or ""):lower(); word = DCMD[word] or word
  if word == "" then return end
  if mk_floorset <= 0 then
    if at.floors[word] then at_set_floor(word .. " off")
    else note("Floor box is 0 - right-click only clears floors (type a value above Units to set one)") end
    return
  end
  if at.floors[word] == mk_floorset then
    at_set_floor(word .. " off")             -- same value again: the toggle clears it
  else
    at_set_floor(word .. " " .. mk_floorset)
  end
end
local function mk_setfloorset(t)
  local n = math.floor(tonumber(t) or -1)
  if n < 0 then
    note("floor value must be a number (0 = right-click clears floors instead of setting)")
  else
    mk_floorset = n
    sset("mk_floorset", n)
    note(n > 0 and ("floor value " .. n .. " - right-click a good in the report to apply it")
               or "floor value 0 - right-click now clears a good's floor")
  end
  if at_draw then at_draw() end
end

-- ---------- numeric settings ----------
local AT_KEY   = { reserve="at_reserve", margin="at_margin", stock="at_stock", flush="at_flush",
                   min="at_minpct", rel="at_minrel", keep="at_keep", soft="at_soft",
                   full="at_full", clear="at_clearpct", carts="at_carts", escort="at_escort",
                   yard="at_yard" }
local AT_FIELD = { reserve="reserve", margin="margin", stock="stock", flush="flush",
                   min="min_pct", rel="min_rel", keep="keep", soft="soft",
                   full="full", clear="clear_pct", carts="carts", escort="escort",
                   yard="yard" }
-- clamps for settings the game itself bounds; everything else is just floored at 0
local AT_RANGE = { escort = { 1, 20 } }
local function at_setnum(name, val)
  local field, key = AT_FIELD[name], AT_KEY[name]
  if not field then note("unknown setting: " .. tostring(name)); return end
  local n = tonumber(val)
  if not n then note("not a number: " .. tostring(val)); return end
  n = math.floor(n)
  local r = AT_RANGE[name]
  if r then n = math.max(r[1], math.min(r[2], n)) else n = math.max(0, n) end
  at[field] = n; sset(key, n); at_draw()
  note(string.format("%s = %d%s", name, n, (n ~= math.floor(tonumber(val))) and " (clamped)" or ""))
end

-- ---------- status + drawing the Auto / Log tabs ----------
local function at_modeline(v)
  local used, wcap, wtier = at_warehouse(v or at_getvars())
  local pct = (wcap > 0) and (used * 100 / wcap) or 0
  local pressure, clearing = pct >= (at.soft or 70), pct >= (at.full or 90)
  local mode = clearing and "CLEARING - biggest piles, buying paused"
            or (pressure and "PRESSURE - biggest piles, scalping paused" or "normal - best value first")
  return used, wcap, wtier, pct, mode
end

at_draw = function()
  local v = at_getvars()
  local used, wcap, wtier, pct, mode = at_modeline(v)
  local cd = tonumber(v.CDTIME) or 0
  local L = {}
  L[#L+1] = string.format("Auto-trade: %s     Scalp: %s   Restock: %s   Refined: %s",
    at.on and "ON" or "OFF", at.scalp and "on" or "off", at.restock and "on" or "off", at.refined and "on" or "off")
  L[#L+1] = string.format("Warehouse %s / %s  (%d%%, tier %d)   Daler %s%s",
    comma(used), comma(wcap), math.floor(pct), wtier, comma(tonumber(v.DALER) or 0),
    cd > 0 and ("   cart cooldown " .. cd .. "s") or "")
  L[#L+1] = "mode: " .. mode
  local held = {}
  for k, on in pairs(at.exempt) do if on then held[#held+1] = k end end
  table.sort(held)
  if #held > 0 then L[#L+1] = "held (never sold): " .. table.concat(held, ", ") end
  local fl = {}
  for k, n in pairs(at.floors) do fl[#fl+1] = k .. "=" .. n end
  table.sort(fl)
  if #fl > 0 then L[#L+1] = "floors (never sold below): " .. table.concat(fl, ", ") end
  if at_yard_until > now_s then
    L[#L+1] = string.format("cartyard: next cart in ~%ds", math.floor(at_yard_until - now_s))
  end
  scrye.setState(P .. "atstatus", table.concat(L, "\n"))

  scrye.setState(P .. "v_keep",    tostring(at.keep))
  scrye.setState(P .. "v_stock",   tostring(at.stock))
  scrye.setState(P .. "v_reserve", tostring(at.reserve))
  scrye.setState(P .. "v_carts",   tostring(at.carts))
  scrye.setState(P .. "v_min",     tostring(at.min_pct))
  scrye.setState(P .. "v_rel",     tostring(at.min_rel))
  scrye.setState(P .. "v_margin",  tostring(at.margin))
  scrye.setState(P .. "v_flush",   tostring(at.flush))
  scrye.setState(P .. "v_soft",    tostring(at.soft))
  scrye.setState(P .. "v_full",    tostring(at.full))
  scrye.setState(P .. "v_clear",   tostring(at.clear_pct))
  scrye.setState(P .. "v_escort",  tostring(at.escort))
  scrye.setState(P .. "v_units",   tostring(mk_units))
  scrye.setState(P .. "v_floorset", tostring(mk_floorset))

  local s = at.stats
  local mins = math.floor((os.time() - (s.since or os.time())) / 60)
  local lg = {}
  lg[#lg+1] = string.format("this session (%dm):  sold %d (~+%s d)   bought %d (-%s d)",
    mins, s.sells, comma(s.earned), s.buys, comma(s.spent))
  lg[#lg+1] = ""
  if #s.recent == 0 then lg[#lg+1] = "(no auto-trades yet)"
  else for i = #s.recent, math.max(1, #s.recent - 25), -1 do lg[#lg+1] = s.recent[i] end end
  scrye.setState(P .. "atlog", table.concat(lg, "\n"))
end

local function at_status()
  local v = at_getvars()
  local used, wcap, wtier, pct, mode = at_modeline(v)
  note(string.format("auto %s | scalp %s | restock %s | refined %s | keep %d | raw>%d | reserve %s | carts %s",
    at.on and "ON" or "OFF", at.scalp and "on" or "off", at.restock and "on" or "off",
    at.refined and "yes" or "no", at.keep, at.stock, comma(at.reserve),
    (at.carts > 0) and tostring(at.carts) or "auto"))
  note(string.format("warehouse %s/%s (%d%%, tier %d) | mode: %s",
    comma(used), comma(wcap), math.floor(pct), wtier, mode))
end

local function at_show_stats()
  local s = at.stats
  local mins = math.floor((os.time() - (s.since or os.time())) / 60)
  note(string.format("this session (%dm): sold %d (~+%s d)  bought %d (-%s d)",
    mins, s.sells, comma(s.earned), s.buys, comma(s.spent)))
end

local function at_show_log()
  local s = at.stats
  if #s.recent == 0 then note("no trades this session (full history is in the plugin log file)"); return end
  note("recent auto-trades:")
  for i = math.max(1, #s.recent - 14), #s.recent do scrye.print("  " .. s.recent[i]) end
end

-- panel toggles
local function at_toggle_on()      at.on = not at.on; if at.on then at_schedule() end; at_draw(); at_status() end
local function at_toggle_scalp()   at.scalp = not at.scalp; sset("at_scalp", at.scalp and "1" or "0"); if at.on and at.scalp then at_schedule() end; at_draw() end
local function at_toggle_restock() at.restock = not at.restock; sset("at_restock", at.restock and "1" or "0"); if at.on and at.restock then at_schedule() end; at_draw() end
local function at_toggle_refined() at.refined = not at.refined; sset("at_refined", at.refined and "1" or "0"); at_draw() end

-- ---------- `atrade` command ----------
local function at_config(rest)
  rest = trim(rest or ""):lower()
  local key, val = rest:match("^(%a+)%s+(%-?%d+)$")
  if rest == "" or rest == "status" then at_status(); return
  -- NB: the armed flag is deliberately NOT persisted -- the plugin always loads
  -- disarmed (see `at.on` above), so writing it to the store would be a dead write.
  elseif rest == "on"  then at.on = true;  at_schedule(); at_draw()
  elseif rest == "off" then at.on = false; at_draw()
  elseif rest == "refined on"  then at.refined = true;  sset("at_refined", "1"); at_draw()
  elseif rest == "refined off" then at.refined = false; sset("at_refined", "0"); at_draw()
  elseif rest == "scalp on"    then at.scalp = true;  sset("at_scalp", "1"); if at.on then at_schedule() end; at_draw()
  elseif rest == "scalp off"   then at.scalp = false; sset("at_scalp", "0"); at_draw()
  elseif rest == "restock on"  then at.restock = true;  sset("at_restock", "1"); if at.on then at_schedule() end; at_draw()
  elseif rest == "restock off" then at.restock = false; sset("at_restock", "0"); at_draw()
  elseif rest == "notify on"   then at.notify = true;  sset("at_notify", "1"); publish_notify_state(); note("phone notify per auto-dispatch: on")
  elseif rest == "notify off"  then at.notify = false; sset("at_notify", "0"); publish_notify_state(); note("phone notify per auto-dispatch: off")
  elseif rest == "stats"       then at_show_stats(); return
  elseif rest == "stats reset" then at.stats = { buys=0, sells=0, spent=0, earned=0, since=os.time(), recent={} }; note("session stats reset"); at_draw(); return
  elseif rest == "log"         then at_show_log(); return
  elseif rest == "exempt"       then local l={}; for k,on in pairs(at.exempt) do if on then l[#l+1]=k end end; table.sort(l); note("held: " .. (#l>0 and table.concat(l, ", ") or "(none)")); return
  elseif rest == "exempt clear" then at.exempt = {}; sset("at_exempt", ""); note("held list cleared"); mk_render(nil); at_draw(); return
  elseif rest:match("^exempt%s+") then at_toggle_exempt(rest:gsub("^exempt%s+", "")); at_draw(); return
  elseif rest == "floor" or rest == "floors" then at_list_floors(); return
  elseif rest == "floor clear"  then at.floors = {}; sset("at_floors", ""); note("all floors cleared"); mk_render(nil); at_draw(); return
  elseif rest:match("^floorset%s+") then at_floorset(rest:gsub("^floorset%s+", "")); at_draw(); return
  elseif rest:match("^floor%s+") then at_set_floor(rest:gsub("^floor%s+", "")); at_draw(); return
  elseif key and AT_FIELD[key] then at_setnum(key, val); return
  else
    note("usage: atrade on|off | scalp|restock|refined|notify on|off | keep|stock|reserve|margin|min|rel|carts|escort|flush|soft|full|clear <n> | exempt <good> | floor <good> <n> | floorset <good> | yard <sec> | stats | log")
    return
  end
  at_status()
end


-- ====================== manual dispatch ======================
-- Restores the MUSHclient window's click-a-town action: it sent
--   vtrade dispatch <side> <units> <good> <town> escort <n>
-- Here it is a command instead, since panel widgets are built once at load and the
-- ranked town list changes on every refresh.
local function mnote(t) scrye.print("@{#DEB218,bold}[market]@{} " .. t) end

-- every town name present in the current market data
local function known_towns()
  local seen, out = {}, {}
  for _, towns in pairs(market) do
    for town in pairs(towns) do
      if not seen[town] then seen[town] = true; out[#out + 1] = town end
    end
  end
  table.sort(out)
  return out
end

-- exact, then prefix, then substring -- so "lodbrok" finds "Lodbrok's Hold"
local function resolve_town(str)
  local q = trim(str or ""):lower()
  if q == "" then return nil end
  local towns = known_towns()
  for _, t in ipairs(towns) do if t:lower() == q then return t end end
  for _, t in ipairs(towns) do if t:lower():sub(1, #q) == q then return t end end
  for _, t in ipairs(towns) do if t:lower():find(q, 1, true) then return t end end
  return nil
end

-- pull a leading good name off "<good> <town>". Goods can be two words ("fine furs",
-- "salted fish"), so the longest matching name or command word wins.
local function split_good_town(str)
  str = trim(str or "")
  local low = str:lower()
  local cmd, len = nil, 0
  for _, r in ipairs(RES) do
    for _, form in ipairs({ r.name:lower(), r.cmd }) do
      if #form > len and low:sub(1, #form) == form then
        local nxt = low:sub(#form + 1, #form + 1)
        if nxt == "" or nxt == " " then cmd, len = r.cmd, #form end
      end
    end
  end
  if not cmd then return nil, nil end
  return cmd, trim(str:sub(len + 1))
end

local function mk_setunits(val)
  local n = tonumber(val)
  if not n then mnote("not a number: " .. tostring(val)); return end
  n = math.max(MK_UNITS_MIN, math.min(MK_UNITS_MAX, math.floor(n)))
  mk_units = n; sset("mk_units", n)
  scrye.setState(P .. "v_units", tostring(n))
  mnote(string.format("manual dispatch units = %d", n))
end

-- ---------- quick dispatch (the window's click-a-town action, as buttons) ----------
-- The original let you click a town in the market window to send a cart there. A bound
-- buttonrow is the equivalent: the options are recomputed from the live feed, and the
-- click carries the index back so we dispatch the exact cart the label described rather
-- than re-parsing it.
-- The carts are clickable TEXT, not buttons. That matters for more than looks: a text widget
-- is driven by bound state, so refreshing the list never rebuilds the panel -- which is what
-- used to threaten the twelve settings fields and forced the carts into a panel of their own.
-- Clicking sends "mkdispatch ...", the plugin's own alias, so it goes through mk_dispatch and
-- gets the same clamping, logging and rescan-guard as a typed command.
publish_dispatch = function()
  local labels = {}
  local ok = pcall(function()
    local v = at_getvars()
    -- warehouse stock per good (same normalisation the auto-trader uses)
    local stock = {}
    for entry in (v.WSTOCK or ""):gmatch("[^;]+") do
      local g, a = entry:match("^([^|]+)|(%d+)")
      if g then
        local k = trim(g):lower():gsub("_", " ")
        stock[k] = (stock[k] or 0) + tonumber(a)
      end
    end
    local _, cap = at_capacity(v)
    if not cap or cap <= 0 then return end

    -- one candidate per good: what you hold, sold to its best-paying town
    local cand = {}
    for _, r in ipairs(results) do
      local best = r.sells[1]
      if best then
        local dc = disp_cmd(r.cmd)
        local have = (stock[(r.cmd or ""):gsub("_", " ")] or stock[(r.res or ""):lower()] or 0)
                     - math.max(at.keep or 0, at.floors[dc] or 0)  -- mission reserve AND floor
        if have > 0 and not at.exempt[dc] then               -- and the held list
          local qty = math.floor(math.min(have, cap, best.qty))
          if qty >= MK_UNITS_MIN then
            cand[#cand + 1] = {
              side = "sell", qty = qty, cmd = disp_cmd(r.cmd), town = best.town,
              value = qty * best.price,
              label = string.format("%d %s>%s", qty, r.res, best.town:sub(1, 10)),
            }
          end
        end
      end
    end
    table.sort(cand, function(a, b) return a.value > b.value end)
    while #cand > 8 do table.remove(cand) end   -- keep the panel a sensible height
    for _, c in ipairs(cand) do
      -- the click runs the plugin's own alias; disp_cmd/town_cmd give the words vtrade wants
      labels[#labels + 1] = string.format(
        "@{success,click=mkdispatch sell %d %s %s}%4d %-12s %s %-12s@{} @{dim}~%sd@{}",
        c.qty, c.cmd, town_cmd(c.town),
        c.qty, esc(DISPLAY[c.cmd] or c.cmd), ">", esc(c.town:sub(1, 12)), esc(comma(c.value)))
    end
  end)
  if not ok then labels = {} end

  if #labels == 0 then
    labels[1] = "@{dim}nothing worth sending - Refresh to update@{}"
  end
  scrye.setState(P .. "carts", table.concat(labels, "\n"))
end

local MK_USAGE = "usage: mkdispatch buy|sell [qty] <good> <town>   (qty defaults to the Units setting)"

local function mk_dispatch(rest)
  local side, tail = trim(rest or ""):match("^(%a+)%s+(.+)$")
  side = side and side:lower()
  if side ~= "buy" and side ~= "sell" then mnote(MK_USAGE); return end

  local qty = mk_units
  local n, remainder = tail:match("^(%d+)%s+(.+)$")
  if n then qty = tonumber(n); tail = remainder end
  qty = math.max(MK_UNITS_MIN, math.min(MK_UNITS_MAX, math.floor(qty)))

  local cmd, townstr = split_good_town(tail)
  if not cmd then mnote("don\'t recognise a good in: " .. tail); mnote(MK_USAGE); return end
  if townstr == "" then mnote("no town given. " .. MK_USAGE); return end

  -- fall back to the word as typed when there is no scan data to match against
  local town = resolve_town(townstr) or townstr
  local escort = math.max(1, math.min(20, at.escort or 5))

  scrye.send(string.format("vtrade dispatch %s %d %s %s escort %d",
    side, qty, disp_cmd(cmd), town_cmd(town), escort))
  mnote(string.format("dispatch %s %d %s %s %s (escort %d)",
    side, qty, DISPLAY[cmd] or cmd, (side == "buy") and "from" or "to", town, escort))

  -- record it in the Log tab, but keep it out of the auto-trader's counters
  local line = string.format("[%s] MAN  %-4s %4d %-11s %-4s %-14s",
    os.date("%Y-%m-%d %H:%M:%S"), side:upper(), qty, disp_cmd(cmd),
    (side == "buy") and "from" or "to", town)
  scrye.log(line)
  local rec = at.stats.recent
  rec[#rec + 1] = line
  while #rec > 40 do table.remove(rec, 1) end

  mk_last_dispatch = os.time()   -- don't let our own echo trigger a rescan
  if at_draw then at_draw() end
end

-- ====================== aliases ======================
scrye.addAlias{
  pattern = "^mkref$", regex = true,
  run = function() mk_refresh(false) end,
}

-- manual dispatch:  mkdispatch buy 200 mead lodbrok   /   mkdispatch sell bread eirik
scrye.addAlias{
  pattern = "^mkdispatch$", regex = true,
  run = function() mnote(MK_USAGE) end,
}
scrye.addAlias{
  pattern = "^mkdispatch (.+)$", regex = true,
  run = function(rest) mk_dispatch(rest) end,
}
-- default cart size for manual dispatch
scrye.addAlias{
  pattern = "^mkunits$", regex = true,
  run = function() mnote(string.format("manual dispatch units = %d (range %d-%d)",
    mk_units, MK_UNITS_MIN, MK_UNITS_MAX)) end,
}
scrye.addAlias{
  pattern = "^mkunits (.+)$", regex = true,
  run = function(v) mk_setunits(v) end,
}

-- consumed, not passed to the MUD: the HUD owns panel visibility
scrye.addAlias{
  pattern = "^markwin$", regex = true,
  run = function() mnote("the Market panel is managed by Scrye - show or hide it from the HUD.") end,
}

-- auto-trader command:  atrade | atrade <setting> <value> | atrade on|off | ...
scrye.addAlias{
  pattern = "^atrade$", regex = true,
  run = function() at_config("") end,
}
scrye.addAlias{
  pattern = "^atrade (.+)$", regex = true,
  run = function(rest) at_config(rest) end,
}

-- ====================== HUD panel (Market / Auto / Log tabs) ======================

-- ====================== init: restore the last scan ======================
local saved = scrye.store.get("market")
if saved and saved ~= "" then
  local ok = pcall(function() market = mk_deserialize(saved) end)
  if ok then
    mk_compute()
    mk_render("restored from previous session - " .. #results .. " goods (mkref to update)")
  else
    market = {}
    mk_render("not loaded yet - click Refresh or type mkref")
  end
else
  mk_render("not loaded yet - click Refresh or type mkref")
end

-- ====================== auto-trader wiring ======================
-- connection tracking: dispatch any idle carts on connect (if auto is on)
scrye.onConnect(function() connected = true; at_driver() end)
scrye.onDisconnect(function() connected = false end)

-- react to live warehouse/cart changes: only dispatch on a real edge (goods gained /
-- cart freed). No vik state to watch any more: the Guild.* adapters call this
-- through mk_on_feed after every burst they translate.

at_draw()   -- seed the Auto / Log tab state
publish_notify_state()

-- A (re)load mid-session gets no onConnect, and this is where the driver used to be
-- started for an installation that came back armed. It cannot any more: `at.on` is false
-- for every load now. Drop the leftover key so an upgrade does not leave a dead "1"
-- sitting in the store looking like it still means something.
scrye.store.delete("at_on")

-- The panel lives in the outer chunk, so hand it the handful of entry points its widgets
-- need. Everything else stays private to this block.
return {
  on_feed        = function() at_on_feed(); publish_dispatch() end,
  armed          = function() return at.on end,
  refresh        = function(quiet) mk_refresh(quiet) end,
  goods_feed     = function(t) tg_feed(t) end,
  set_towns      = function(t) tg_towns = t end,
  setunits       = function(t) mk_setunits(t) end,
  setfloorset    = function(t) mk_setfloorset(t) end,
  -- the panel's label, built from the clamp rather than repeating it: the two drifted apart
  -- once already, and a field labelled with the wrong range is worse than an unlabelled one
  units_hint     = string.format("Units (%d-%d) ", MK_UNITS_MIN, MK_UNITS_MAX),
  setnum         = function(field, t) at_setnum(field, t) end,
  toggle_on      = function() at_toggle_on() end,
  toggle_scalp   = function() at_toggle_scalp() end,
  toggle_restock = function() at_toggle_restock() end,
  toggle_refined = function() at_toggle_refined() end,
}
end)()
mk_on_feed = MK.on_feed
mk_armed = MK.armed
mk_goods_feed = MK.goods_feed
mk_set_towns = MK.set_towns


scrye.addPanel{
  title = "Viking Status",
  width = 560,
  accent = "#6288E1",          -- signature: viking steel-blue (validated accent set)
  tabs = {
    -- The HP/Seid/Vig/Rad + Enemy gauges moved to their own plugin (3s-vitals):
    -- they bind global state, so the split cost nothing and the bars can float
    -- small next to the output while this panel stays parked.
    { title = "Stats", widgets = {
        { type = "value", text = "Modrsokn: ", bind = P .. "mordsokn", color = "info" },            -- semantic
        { type = "text", bind = P .. "stats" },
        { type = "button", text = "Commit patrol (last count)", action = patrol_commit },
    } },
    { title = "City", widgets = {
        { type = "text", bind = P .. "city" },
        { type = "label", text = "-- Refinery --   one segment per quality stage, raw (amber) to refined (green) - hover a bar for the numbers", color = "dim" },
        { type = "barlist", bind = P .. "refinery" },
    } },
    { title = "Builds", widgets = {
        -- semantic colour: the count of buildings you can start right now
        { type = "value", text = "", bind = P .. "buildsummary", color = "success" },
        { type = "text", bind = P .. "builds" },
        -- one button: the rows redraw themselves on every feed change, so a manual
        -- refresh would do nothing Scan does not do better ('build refresh' still exists).
        { type = "button", text = "Scan costs", action = function() bp_scan() end },
    } },
    { title = "Production", widgets = {
        { type = "text", bind = P .. "production" },
        { type = "button", text = "Refresh stock (vtrade stock)", action = function() vstock_scan(false) end },
    } },
    { title = "People", widgets = {
        { type = "text", bind = P .. "people" },
    } },
    { title = "Settlers", widgets = {
        { type = "text", bind = P .. "settlers" },
    } },
    { title = "Holds", widgets = {
        { type = "text", bind = P .. "holds" },
    } },
    -- (Sea / Voyage / Map / Travel tabs live in 3s-viking-sea now)
    { title = "Plan", widgets = {
        { type = "value", text = "", bind = P .. "planhdr", color = "#6288E1" },   -- section header echoes the accent
        { type = "colorgrid", bind = P .. "plangrid", palette = PLAN_PAL, icons = icons_on and PLAN_ICONS or nil,
          images = icons_on and PLAN_IMAGES or nil },
        { type = "text", bind = P .. "planlist" },
    } },
    { title = "Mission", widgets = {
        { type = "text", bind = P .. "mission" },
        { type = "label", bind = P .. "mrun", color = "dim" },
        { type = "buttonrow", buttons = {
            { text = "Run all", action = function() mrun_start() end },
            { text = "Stop",    action = function() mrun_stop("by request") end },
        } },
        { type = "buttonrow", buttons = {
            { text = "Fetch",  action = function() scrye.send("vmission newbie fetch") end },
            { text = "Submit", action = function() scrye.send("vmission newbie submit") end },
        } },
    } },
    { title = "Trade", widgets = {
        { type = "button", text = "Refresh", action = function() MK.refresh(false) end },
        { type = "label",  bind = P .. "status", color = "#AC811E" },   -- market gold, kept: it reads as its own subsystem
        { type = "text",   bind = P .. "report" },
        { type = "label",  text = "Quick dispatch - click a cart to send it:", color = "dim" },
        { type = "text",   bind = P .. "carts" },
        { type = "input",  text = "Floor (right-click a good) ", bind = P .. "v_floorset",
          onSubmit = function(t) MK.setfloorset(t) end },
        { type = "input",  text = MK.units_hint, bind = P .. "v_units",
          onSubmit = function(t) MK.setunits(t) end },
        { type = "input",  text = "Escort (1-20) ",  bind = P .. "v_escort",
          onSubmit = function(t) MK.setnum("escort", t) end },
    } },
    { title = "Trade Auto", widgets = {
        { type = "text", bind = P .. "atstatus" },
        { type = "buttonrow", buttons = {
            { text = "Auto On/Off", action = function() MK.toggle_on() end },
            { text = "Scalp On/Off", action = function() MK.toggle_scalp() end },
        } },
        { type = "buttonrow", buttons = {
            { text = "Restock On/Off", action = function() MK.toggle_restock() end },
            { text = "Refined On/Off", action = function() MK.toggle_refined() end },
        } },
        { type = "label", text = "Settings (type a value, Enter):", color = "dim" },
        { type = "input", text = "Keep (every good) ",  bind = P .. "v_keep",    onSubmit = function(t) MK.setnum("keep", t) end },
        { type = "input", text = "Raw> buffer ",        bind = P .. "v_stock",   onSubmit = function(t) MK.setnum("stock", t) end },
        { type = "input", text = "Daler reserve ",      bind = P .. "v_reserve", onSubmit = function(t) MK.setnum("reserve", t) end },
        { type = "input", text = "Cart cap (0=auto) ",  bind = P .. "v_carts",   onSubmit = function(t) MK.setnum("carts", t) end },
        { type = "input", text = "Cart fill min % ",    bind = P .. "v_min",     onSubmit = function(t) MK.setnum("min", t) end },
        { type = "input", text = "Value floor % ",      bind = P .. "v_rel",     onSubmit = function(t) MK.setnum("rel", t) end },
        { type = "input", text = "Scalp margin/unit ",  bind = P .. "v_margin",  onSubmit = function(t) MK.setnum("margin", t) end },
        { type = "input", text = "Flush cap (0=off) ",  bind = P .. "v_flush",   onSubmit = function(t) MK.setnum("flush", t) end },
        { type = "input", text = "Pressure % ",         bind = P .. "v_soft",    onSubmit = function(t) MK.setnum("soft", t) end },
        { type = "input", text = "Clearing % ",         bind = P .. "v_full",    onSubmit = function(t) MK.setnum("full", t) end },
        { type = "input", text = "Clearing fill % ",    bind = P .. "v_clear",   onSubmit = function(t) MK.setnum("clear", t) end },
        { type = "input", text = "Escort size ",        bind = P .. "v_escort",  onSubmit = function(t) MK.setnum("escort", t) end },
        { type = "label", text = "Hold a good: click its name in the Trade tab (or: atrade exempt <good>)", color = "dim" },
        { type = "label", text = "Floor a good: atrade floor <good> <n> - never sold below n; its name turns blue", color = "dim" },
    } },
    { title = "Trade Log", widgets = {
        { type = "text", bind = P .. "atlog" },
    } },
    { title = "Feeds", widgets = {
        { type = "text", bind = P .. "feeds" },
    } },
  },
}
end
build_panel()

scrye.addAlias{ pattern = [[^vicons$]], regex = true, run = function() toggle_icons() end }



-- ================================================================================
-- The GMCP feed layer: page assembler + one adapter per Guild.* package.
-- Each adapter translates a merged snapshot into the vik-key strings the composers
-- above parse (via vset), then pokes the auto-trader (mk_on_feed). String formats
-- here are a PRIVATE contract with this file's own parsers - see the header note.
-- ================================================================================

-- ---------- Guild.* page assembler (shared snippet; docs/Plan-Viking-GMCP.md §3) ----------
-- Guild packages arrive paged: {page=i, pages=N, full=1?} with list keys split across
-- pages. gasm(pkg, on_snap) subscribes to the package and calls on_snap(snap) with the
-- merged snapshot each time a burst completes:
--   * a message with no "pages" is unpaged: its keys merge into the snapshot directly;
--   * a burst whose pages carry full=1 REPLACES the paged keys of the snapshot
--     (keys only ever seen on the unpaged stream survive it); a burst without
--     full merges - keys it never mentions keep their last value;
--   * a (non-empty) array key met on several pages of one burst CONCATENATES;
--     everything else is last-write;
--   * page/pages/full/guild are bookkeeping, never data;
--   * a page that doesn't continue the current burst (different pages count, or page
--     not past the last one seen) abandons the stale burst and starts fresh.
local function gasm(pkg, on_snap)
  local snap, burst, bfull, expect, last_page = {}, nil, false, nil, 0
  local paged_keys = {}     -- keys that have ever arrived in a paged burst
  local function is_list(v) return type(v) == "table" and v[1] ~= nil end
  scrye.onGmcp(pkg, function(json)
    local ok, t = pcall(scrye.json.decode, json)
    if not ok or type(t) ~= "table" then return end
    local page, pages = tonumber(t.page), tonumber(t.pages)
    if not pages then
      for k, v in pairs(t) do if k ~= "guild" and k ~= "full" then snap[k] = v end end
      pkg_seen(pkg)
      pcall(on_snap, snap)
      if mk_on_feed then pcall(mk_on_feed) end
      return
    end
    if not burst or pages ~= expect or (page or 0) <= last_page then
      burst, bfull, expect = {}, false, pages
    end
    last_page = page or 0
    if tonumber(t.full) == 1 then bfull = true end
    for k, v in pairs(t) do
      if k ~= "page" and k ~= "pages" and k ~= "full" and k ~= "guild" then
        if is_list(v) and is_list(burst[k]) then
          for _, e in ipairs(v) do burst[k][#burst[k] + 1] = e end
        else
          burst[k] = v
        end
      end
    end
    if page == pages then
      if bfull then
        -- full replaces the PAGED keys; keys that only ever arrive on the unpaged
        -- stream (City's dcycle/patrol/nexttick ride outside the bursts) survive
        local keep = {}
        for k, v in pairs(snap) do if not paged_keys[k] then keep[k] = v end end
        snap = keep
      end
      for k, v in pairs(burst) do snap[k] = v; paged_keys[k] = true end
      burst, bfull, expect, last_page = nil, false, nil, 0
      pkg_seen(pkg)
      pcall(on_snap, snap)
      if mk_on_feed then pcall(mk_on_feed) end
    end
  end)
end

local function S(x) return x == nil and "" or tostring(x) end

-- join a list of records into "f1|f2|..;f1|f2|.." with a field-picker per record
local function join(list, fields)
  local out = {}
  for _, e in ipairs(type(list) == "table" and list or {}) do
    if type(e) == "table" then
      local f = {}
      for i, k in ipairs(fields) do f[i] = S(e[k]):gsub("[|;]", " ") end
      out[#out + 1] = table.concat(f, "|")
    end
  end
  return table.concat(out, ";")
end

-- Every adapter translates the merged SNAPSHOT unconditionally: a key absent from
-- the snapshot is either not-yet-seen or dropped by a full-replace burst, and in
-- both cases the derived string must be empty rather than a stale leftover. The
-- composers all tolerate "" (they print "?" / "none" / skip the section).
local function T(t, k) return type(t[k]) == "table" and t[k] or {} end

gasm("Guild.State", function(t)
  vset("daler", t.daler)
  vset("fury", T(t, "points").fury)
  -- the guild pools, cur/max. gline1 is the decoder ring for these (28 Aug):
  -- S[vitka|mvitka]=Seid  V[viga|mviga]=Vigor  R[drotta|mdrotta]=Rad  H=hp
  do
    local pt = T(t, "points")
    local function pool(cur, max)
      return (cur ~= nil and max ~= nil) and (S(cur) .. "/" .. S(max)) or ""
    end
    vset("seid", pool(pt.vitka, pt.mvitka))
    vset("vig",  pool(pt.viga, pt.mviga))
    vset("rad",  pool(pt.drotta, pt.mdrotta))
  end
  do
    local hp = T(t, "hp")
    vset("hp", (hp.cur ~= nil and hp.max ~= nil) and (S(hp.cur) .. "/" .. S(hp.max)) or "")
  end
  vset("threk", T(t, "hp").threk); vset("mthrek", T(t, "hp").mthrek)
  vset("chain", T(t, "chain").chain); vset("bsdepth", T(t, "chain").bsdepth)
  vset("rndz", T(t, "encounter").rounds)
  vset("ldng", T(t, "ledung").charges); vset("mldng", T(t, "ledung").max)
  vset("stfx", T(t, "fx").stfx)
  vset("god_power", T(t, "god").name)
  vset("god_power_focus", T(t, "god").focus)
  vset("vmnew", t.missions_newbie)
  vset("vmreg", t.missions_reg)
  do
    -- the four guild-xp tracks (Guild.State.gxp, in the 28 Aug captures): current,
    -- the advance threshold (_max), and the last tick's gain (_last). Serialized in
    -- a fixed order; a track the server drops simply stops appearing.
    local g = T(t, "gxp")
    local out = {}
    for _, k in ipairs({ "buandi", "drotta", "viga", "vitka" }) do
      if g[k] ~= nil then
        out[#out + 1] = k .. "|" .. S(g[k]) .. "|" .. S(g[k .. "_max"]) .. "|" .. S(g[k .. "_last"])
      end
    end
    vset("gxp", table.concat(out, ";"))
  end
end)

gasm("Guild.Info", function(t)
  vset("lin", t.lineage)
  vset("glvl", t.glvl)
  vset("sub", t.subguild)
  vset("rank", t.rank_name)
  vset("renown", t.renown)
end)

gasm("Guild.City", function(t)
  vset("nexttick", t.nexttick)
  local dc = T(t, "dcycle")
  vset("dcycle", dc.name and (S(dc.name) .. "|" .. S(dc.secs)) or "")
  local bl = T(t, "blot")
  if bl.state ~= nil then
    local mins = math.floor((tonumber(bl.reset_in) or 0) / 60)
    vset("blot", string.format("%s/%s %s, reset %dm", S(bl.filled), S(bl.total), S(bl.state), mins))
  else vset("blot", "") end
  local pt = T(t, "patrol")
  vset("patrol", pt.count and (S(pt.count) .. "|" .. S(pt.remaining)) or "")
  -- incoming raid (server-side 28 Aug pm): faction|strength|secs, "" when no raid
  -- is inbound - unconditional, so a full-replace clears the alarm the moment the
  -- server stops sending it
  local rd = T(t, "raid")
  if rd.faction then
    craid_seen_at = now_s     -- feeds the alarm's shelf-life clock (see CRAID_TTL)
    vset("craid", S(rd.faction) .. "|" .. S(rd.strength) .. "|" .. S(rd.secs))
  else
    vset("craid", "")
  end
  -- building damage (28 Aug pm): id:pct,... - same clearing rule
  do
    local out = {}
    for _, b in ipairs(T(t, "bdmg")) do out[#out + 1] = S(b.id) .. ":" .. S(b.pct) end
    vset("bdmg", table.concat(out, ","))
  end
  do
    local out = {}
    for _, b in ipairs(T(t, "buildings")) do out[#out + 1] = S(b.id) .. ":" .. S(b.tier) end
    vset("buildings", table.concat(out, ","))
  end
  -- f1 = id (compute() lowercases it to the key form), f5 = seconds left
  vset("builds", join(T(t, "builds"), { "id", "tier", "done", "total", "secs" }))
  local cp = T(t, "cityplan")
  vset("cplan", cp.dim and (S(cp.dim) .. "|" .. S(cp.wall) .. "|" .. S(cp.placed) .. "|" .. S(cp.cap)) or "")
  if type(t.cityplan_buildings) == "table" and t.cityplan_buildings[1] then
    -- cp_accumulate's shape: ?|col|row|w|h|?|letter|name
    local out = {}
    for _, b in ipairs(t.cityplan_buildings) do
      out[#out + 1] = table.concat({ "-", S(b.x), S(b.y), S(b.w), S(b.h), "-",
        S(b.glyph), S(b.name):gsub("[|;]", " ") }, "|")
    end
    vset("cpb", table.concat(out, ";"))
  end
end)

gasm("Guild.Settlement", function(t)
  local se = T(t, "settlers")
  vset("spop", se.settlers)
  vset("smood", se.mood)
  vset("swater", se.water)
  local x = T(t, "settlerx")
  vset("ssent", x.sentiment)
  vset("stax", x.tax_income)
  vset("supk", x.comm_upkeep and ((tonumber(x.comm_upkeep) or 0) + (tonumber(x.housing_upkeep) or 0)) or "")
  vset("snet", x.net)
  do
    local out = {}
    for _, c in ipairs(T(t, "scivics")) do out[#out + 1] = S(c.id) .. ":" .. S(c.count) end
    vset("scivics", table.concat(out, ";"))
  end
  if type(t.sconsume) == "table" then
    local ks = {}
    for k in pairs(t.sconsume) do ks[#ks + 1] = k end
    table.sort(ks)
    local out = {}
    for _, k in ipairs(ks) do
      local n = tonumber(t.sconsume[k]) or 0
      if n ~= 0 then out[#out + 1] = k .. ":" .. n end
    end
    vset("sconsume", table.concat(out, ";"))
  end
  do
    local out = {}
    for _, pr in ipairs(T(t, "sproj")) do out[#out + 1] = type(pr) == "table" and S(pr.id) or S(pr) end
    vset("sproj", table.concat(out, ";"))
  end
end)

gasm("Guild.Trade", function(t)
  -- field 1 = mode (kind) and field 11 = cap: both the City tab and the
  -- auto-trader's at_capacity/at_cart_sigs read exactly those positions
  vset("carts", join(T(t, "carts"), { "mode", "good", "village", "secs", "amount",
    "escort", "horses", "durability", "quality_pct", "tier", "cap" }))
  -- a cart LEAVING or COMING HOME is the moment the warehouse changes, so it is
  -- the moment to refresh the stock scan. Keyed on the sorted cart-ID SET - the
  -- raw carts string changes every burst (secs ticks), the id set only changes
  -- when a cart actually departs or docks. The first burst is just the baseline.
  do
    local ids = {}
    for _, c in ipairs(T(t, "carts")) do ids[#ids + 1] = S(c.cart_id) end
    table.sort(ids)
    local sig = table.concat(ids, ",")
    if ws_cart_sig ~= nil and sig ~= ws_cart_sig then ws_auto_scan() end
    ws_cart_sig = sig
  end
  -- cidle: live-confirmed 28 Aug - real fields are {slot, tier, cap, durability,
  -- horses, refit}; at_capacity reads field 4 as the cart's capacity, so cap goes there
  vset("cidle", join(T(t, "cidle"), { "slot", "tier", "refit", "cap", "durability", "horses" }))
  do
    local out = {}
    for _, e in ipairs(T(t, "cupg")) do out[#out + 1] = type(e) == "table" and S(e.cart_id) or S(e) end
    vset("cupg", table.concat(out, ";"))
  end
  vset("cdtime", t.cdtime)
  -- routes: NEW in the 28 Aug capture ({name, village, road_name, road_tier,
  -- road_maint, fort_name, fort_tier, fort_maint}). build_production reads
  -- f2 = town name, f7 = road name ("No ..." = none), f8 = fort name.
  vset("routes", join(T(t, "routes"), { "village", "name", "road_tier", "road_maint",
    "fort_tier", "fort_maint", "road_name", "fort_name" }))
  -- parse_missions reads f1=id f3=rep f7=town f8=goods
  vset("missions", join(T(t, "missions"), { "id", "label", "rep", "reward", "secs",
    "origin", "town", "goods" }))
  if type(t.refinery) == "table" then
    -- build_city's shape: name:tier:cur:max:stages, stages = "grade,qty,pct;..."
    local grades = {}
    for _, g in ipairs(type(t.refinery_grades) == "table" and t.refinery_grades or {}) do
      local k = S(g.bldg)
      grades[k] = grades[k] or {}
      grades[k][#grades[k] + 1] = S(g.grade):gsub("[,;:|]", " ") .. "," .. S(g.qty) .. "," .. S(g.pct)
    end
    local out = {}
    for _, r in ipairs(t.refinery) do
      out[#out + 1] = table.concat({ S(r.bldg), S(r.tier), S(r.stock), S(r.cap),
        table.concat(grades[S(r.bldg)] or {}, ";") }, ":")
    end
    vset("refinery", table.concat(out, "|"))
  end
  -- wstock_cap rides Guild.Trade's last page as well as Guild.Warehouse's;
  -- whichever spoke last wins (they agree - same server figure)
  if t.wstock_cap ~= nil then ws_cap_feed = tonumber(t.wstock_cap) end
  vset("wstock", compose_wstock())
end)

-- Guild.Warehouse: NEW server-side 28 Aug pm - the per-good stock the `vtrade
-- stock` scan was standing in for (plan §2's feed gap, now closed). One record
-- per good+grade with the REAL freshness pct (the text report could only say
-- 100), and wstock_cap on the last page. From its first goods burst the feed
-- OWNS the stock: ws_feed_live gates every scan path off, and the composed tail
-- is the same "good|amt|pct|grade;..." contract every consumer already reads -
-- goods arrive in key form (salted_fish, fine_furs), matching the scan's ws_key.
gasm("Guild.Warehouse", function(t)
  if t.wstock_cap ~= nil then ws_cap_feed = tonumber(t.wstock_cap) end
  if type(t.wstock) == "table" then
    local out = {}
    for _, r in ipairs(t.wstock) do
      if type(r) == "table" and r.good ~= nil then
        out[#out + 1] = S(r.good):gsub("[|;]", " ") .. "|" .. S(r.amount) .. "|"
          .. (r.pct ~= nil and S(r.pct) or "100") .. "|" .. S(r.grade):gsub("[|;]", " ")
      end
    end
    ws_goods = table.concat(out, ";")
    ws_feed_live = true
    ws_scanned_at = now_s        -- the trader's freshness clock: fed now
    ws_warned = false
  end
  vset("wstock", compose_wstock())
end)

gasm("Guild.Fleet", function(t)
  -- build_city reads f1=name f3=state f4=target f5=secs (3s-viking-sea reads f3)
  vset("ships", join(T(t, "ships"), { "name", "tier", "state", "target", "secs" }))
  -- the village order for Guild.TradeGoods: lin 0 = Midgard, lin i = lineage[i]
  -- (the 29 Aug overview confirmed the lineage list IS the market's row order)
  if type(t.rtargets_lineage) == "table" and t.rtargets_lineage[1] and mk_set_towns then
    local towns = { [0] = "Midgard" }
    for i, e in ipairs(t.rtargets_lineage) do
      towns[i] = tostring(e):match("^([^:]+)") or tostring(e)
    end
    mk_set_towns(towns)
  end
end)

-- Guild.TradeGoods: each burst is one village's price rows (see the decoder ring
-- and CODE2RES in the market block) - handed to the MK closure, which owns the
-- market table the report, dispatch tab and auto-trader all read.
gasm("Guild.TradeGoods", function(t)
  if mk_goods_feed then pcall(mk_goods_feed, t) end
end)

gasm("Guild.Roster", function(t)
  -- build_people reads f2=name f4..f7 f9 f10; bonds' name lookup reads f1=id f2=name
  vset("hird", join(T(t, "hird"), { "id", "name", "level", "atk", "def", "loyalty",
    "level", "mode", "status", "age" }))
  do
    local out = {}
    for _, b in ipairs(T(t, "bonds")) do
      out[#out + 1] = S(b.a) .. "|" .. S(b.b) .. "|" .. S(b.ticks) .. "|T" .. S(b.tier)
    end
    vset("bonds", table.concat(out, ";"))
  end
  vset("thralls", T(t, "thralls").total)
  local gn = T(t, "gneeds")
  vset("garrison", gn.garrison_cap and (S(gn.garrisoned) .. "/" .. S(gn.garrison_cap)) or "")
  vset("hirdcap", gn.hird_cap and (S(gn.hird_count) .. "/" .. S(gn.hird_cap)) or "")
  local f = T(t, "thrall_follower")
  vset("thrall_follower",
    f.state == nil and "" or (S(f.name) == "" and "none" or (S(f.name) .. " (" .. S(f.state) .. ")")))
end)

gasm("Guild.Kingdom", function(t)
  if type(t.vrep) == "table" then
    -- parse_idx_table + build_holds: idx|name|rep
    local out = {}
    for _, v in ipairs(t.vrep) do
      out[#out + 1] = S(v.lin) .. "|" .. S(v.name):gsub("[|;]", " ") .. "|" .. S(v.rep)
    end
    vset("vrep", table.concat(out, ";"))
  end
  if type(t.standings) == "table" then
    -- idx|name|score|label ("Score" column + the standing word)
    local out = {}
    for _, v in ipairs(t.standings) do
      out[#out + 1] = S(v.lin) .. "|" .. S(v.name):gsub("[|;]", " ") .. "|"
        .. S(v.score) .. "|" .. S(v.label)
    end
    vset("standings", table.concat(out, ";"))
  end
  if type(t.varang_in) == "table" or type(t.varang_out) == "table" then
    local nin  = type(t.varang_in)  == "table" and #t.varang_in  or 0
    local nout = type(t.varang_out) == "table" and #t.varang_out or 0
    vset("varang", (nin + nout) == 0 and "" or ("in " .. nin .. ", out " .. nout))
  end
end)

-- ------------------------------------------------------------------ init
mark_all()
flush()
