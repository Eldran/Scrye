-- 3S Viking Status -- Scrye conversion of ThreeS_VikingStatus (MUSHclient)
--
-- NOTE: dropped / simplified vs the original:
--  * vikbar / viktab dropped: the HUD manages panel visibility and tab switching.
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

-- ---------------------------------------------------------------- helpers
local function num(s) return tonumber(s) or 0 end

local function gv(k) return scrye.getState("vik." .. k:lower()) end

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

-- territory map: terrain char -> colour
local MAP_PAL = {
  ["."] = "#101010",                                       -- void / walls
  ["t"] = "#606060", ["T"] = "#707070",                    -- tundra grey
  ["h"] = "#C0C020", ["H"] = "#C0C020",                    -- hills yellow
  ["A"] = "#D03030",                                       -- mountains red
  ["f"] = "#208020", ["F"] = "#30A030",                    -- forest
  ["p"] = "#60C060",                                       -- plains
  ["W"] = "#00A0D0", ["w"] = "#00A0D0", ["~"] = "#00A0D0", -- water
  ["r"] = "#181818", ["="] = "#303030",                    -- road / bridge
  ["P"] = "#909090",                                       -- gate / passage
  ["L"] = "#E08020",                                       -- lin hold
  ["S"] = "#E0C040",                                       -- settlement
  ["C"] = "#C02020",                                       -- capital
  ["R"] = "#C060C0",                                       -- ruins
  ["M"] = "#C02020",                                       -- Midgard (capital)
  ["*"] = "#E060E0",                                       -- point of interest
  ["X"] = "#FFFFFF",                                       -- you (feed marker)
  [" "] = "#000000",                                       -- masked (unexplored)
  ["?"] = "#282828",                                       -- unmapped char
}

-- voyage chart char -> colour
local SEA_PAL = {
  ["#"] = "#303030",                      -- unrevealed
  ["O"] = "#00A0D0", ["~"] = "#00A0D0",   -- open sea
  ["F"] = "#909090",                      -- fog
  ["?"] = "#505050",                      -- unknown
  ["I"] = "#C0C020",                      -- island
  ["H"] = "#40C040",                      -- harbor
  ["W"] = "#E04040",                      -- wreck
  ["T"] = "#F05050",                      -- storm
  ["X"] = "#E060E0",                      -- objective
  ["S"] = "#FFFFFF",                      -- your ship
  ["+"] = "#60E060", [">"] = "#E0E060",   -- queued path / destination
  ["="] = "#0060D0",                      -- crosscurrent
  ["^"] = "#606040",                      -- deadwater
  ["B"] = "#C04080",                      -- stormbelt
  ["*"] = "#408030",                      -- resolved node
  [" "] = "#302820",                      -- sea
}

-- city plan: terrain char -> tile colour; placed buildings become role digits 1-7
local PLAN_PAL = {
  ["."] = "#484848",  -- plain
  ["f"] = "#246E24",  -- woods
  ["H"] = "#8C7050",  -- hill
  ["w"] = "#1C5C9A",  -- river
  ["c"] = "#206A6A",  -- coast
  ["M"] = "#585858",  -- wall
  ["W"] = "#4E4E4E",  -- wall
  ["G"] = "#886C46",  -- gate
  ["B"] = "#886C46",  -- gate
  ["1"] = "#40E040",  -- producers  (green)
  ["2"] = "#E04030",  -- industry   (red)
  ["3"] = "#A02828",  -- grim       (maroon)
  ["4"] = "#40D0E0",  -- trade      (cyan)
  ["5"] = "#E060E0",  -- culture    (magenta)
  ["6"] = "#E0E0E0",  -- homes      (white)
  ["7"] = "#FFD040",  -- throne     (gold)
  ["?"] = "#383838",  -- unknown
}
local ROLE_DIGIT = { prod = "1", ind = "2", grim = "3", trade = "4",
                     cult = "5", home = "6", throne = "7" }

-- --------------------------------------------------- static logic tables
local POI_LABEL = { L = "Lin", S = "Set", C = "Cap", ["*"] = "POI", R = "Ruin", M = "Cap" }

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
  EriMid = { "leave","w","w","s","s","s","s","s","s","s","s","w","w","w","w","w","w","w","w","w","w","w","w","w","enter" },
  HafMid = { "leave","e","s","s","s","s","s","s","s","s","s","s","w","w","w","enter" },
  HolMid = { "leave","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","w","enter" },
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

-- current live town from the (laggy) map feed, or nil
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
  return true
end

-- Walk to a town. Origin = the remembered curtown (authoritative once set by travel or
-- 'vhere'), else the live map position. After travel we KNOW where we are, so remember it.
local function travel_to(dest)
  local rem = scrye.store.get("curtown"); if rem == "" then rem = nil end
  local origin = rem or live_town()
  if not origin then
    scrye.print("[viking] can't tell where you are - set it with  vhere <town>  first")
    return
  end
  if origin == dest then scrye.print("[viking] you're already at " .. town_label(dest)); return end
  if not send_route(origin, dest) then
    scrye.print("[viking] no route known for " .. town_label(origin) .. " -> " .. town_label(dest))
    return
  end
  scrye.print("[viking] travelling " .. town_label(origin) .. " -> " .. town_label(dest))
  scrye.store.set("curtown", dest)
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

-- 2-column clickable town buttons, sorted by name (shared by the Travel tab and the Map tab).
local function town_button_rows()
  local rows, pending = {}, nil
  for _, code in ipairs(TRAVEL_TOWNS) do
    local b = { text = town_label(code), action = function() travel_to(code) end }
    if pending then
      rows[#rows + 1] = { type = "buttonrow", buttons = { pending, b } }; pending = nil
    else
      pending = b
    end
  end
  if pending then rows[#rows + 1] = { type = "button", text = pending.text, action = pending.action } end
  return rows
end

-- Travel tab widget list (clickable town buttons, two per row).
local travel_widgets = {
  { type = "label", text = "Walk to a settlement (uses the built-in route from where you are):" },
}
for _, w in ipairs(town_button_rows()) do travel_widgets[#travel_widgets + 1] = w end
travel_widgets[#travel_widgets + 1] =
  { type = "label", text = "Walks from the wrong place? Set where you are:  vhere <town>" }

-- Map tab widgets: the rendered map (also clickable) + a clickable town list + the full location list.
local map_widgets = {
  { type = "value", text = "", bind = P .. "maphdr", color = "#5A93D4" },   -- section header
  { type = "colorgrid", bind = P .. "map", palette = MAP_PAL,
    onClick = function(col, row, ch)
      local key = col .. "|" .. row
      local code = travel_code(key)
      if code then
        travel_to(code)
      else
        local name = locname(col, row)
        if name then
          scrye.print(string.format("[viking] %s (%d,%d) - no travel route", name, col, row))
        else
          scrye.print(string.format("[viking] map (%d,%d) '%s' - nothing to travel to", col, row, ch))
        end
      end
    end },
  { type = "label", text = "grey tundra  yellow hills  red mtn/capital  green forest/plains  blue water  dark road  orange lin  gold settlement  white you  black unexplored" },
  { type = "label", text = "Click a town to travel there:", color = "#E0C040" },
}
for _, w in ipairs(town_button_rows()) do map_widgets[#map_widgets + 1] = w end
map_widgets[#map_widgets + 1] = { type = "text", bind = P .. "maplocs" }   -- full location list w/ coords

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
local sea_nav_pending = false
local flush                    -- forward decl

local function schedule_flush()
  if flush_pending then return end
  flush_pending = true
  scrye.after(1, function() flush() end)
end
local function mark_all()
  for _, s in ipairs({ "stats", "city", "builds", "production", "people", "settlers",
                       "holds", "sea", "voyage", "map", "plan", "mission", "feeds" }) do
    dirty[s] = true
  end
end

-- ------------------------------------------------------ report builders
local function build_stats()
  local L = {}
  local function add(s) L[#L + 1] = s end
  add("-- War --")
  add(string.format("God %s > %s   next %ss", q("GOD_POWER"), q("GOD_POWER_FOCUS"), q("GOD_POWER_NEXT")))
  add(string.format("Raid %s   Blot %s", q("RAID"), q("BLOT"):sub(1, 16)))
  add("")
  add("-- " .. q("LIN") .. "  GLvl " .. q("GLVL") .. " --")
  add(string.format("Sub %s   Daler %s", q("SUB"), q("DALER")))
  add(string.format("Kap %s   Aud %s   Vis %s   Soemd %s", q("KAP"), q("AUD"), q("VIS"), q("SOE")))
  add(string.format("VKxp %s   New %s   Reg %s   Tick %ss", q("VKXP"), q("VMNEW"), q("VMREG"), q("NEXTTICK")))
  local wx = split(gv("WEATHER"), "|")
  local dc = split(gv("DCYCLE"), "|")
  add(string.format("Weather %s/%s   Cycle %s", wx[1] or "?", wx[2] or "?", dc[1] or "?"))
  local stfx = clean(gv("STFX")):gsub("[%[%]]", "")
  if stfx ~= "" then add("Effects: " .. stfx) end
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
  local function add(s) L[#L + 1] = s end
  add("-- Ships --")
  local ships = split(gv("SHIPS"), ";")
  if #ships == 0 then add("none")
  else
    for i = 1, math.min(#ships, 10) do
      local f = split(ships[i], "|")
      local eta = tonumber(f[5]) and (math.floor(num(f[5]) / 60) .. "m") or ""
      add(string.format("%-12s %-10s %s", f[1] or "?", f[4] or "", eta))
    end
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
  -- Refinery -> a barlist (label | caption | value | max | refined): fill = cur/max,
  -- the filled part splits into refined (green) and raw (amber). refined = quality-weighted
  -- units = sum over stages of qty * pct/100.
  local R = {}
  for _, r in ipairs(split(gv("REFINERY"), "|")) do
    local f = split(r, ":")
    if f[1] and f[1] ~= "" then
      local name = f[1]:gsub("_", " "):gsub("(%a)([%w]*)", function(a, b) return a:upper() .. b:lower() end)
      local cur, max = num(f[3]), num(f[4])
      local refined = 0
      for _, s in ipairs(split(f[5] or "", ";")) do
        local g = split(s, ",")
        if g[1] and g[1] ~= "" then refined = refined + num(g[2]) * num(g[3]) / 100 end
      end
      R[#R + 1] = string.format("%s\tT%s %d/%d\t%d\t%d\t%d",
        name, f[2] or "?", cur, max, cur, max, math.floor(refined + 0.5))
    end
  end
  scrye.setState(P .. "refinery", table.concat(R, "\n"))
  return table.concat(L, "\n")
end

local function build_builds()
  local L = {}
  local function add(s) L[#L + 1] = s end
  add("-- Buildings   Daler " .. q("DALER") .. " --")
  local blist = {}
  for entry in gv("BUILDINGS"):gmatch("[^,]+") do
    local name, tier = entry:match("^(.-):(%d+)$")
    if name then blist[#blist + 1] = titlecase(name) .. " T" .. tier end
  end
  table.sort(blist)
  if #blist == 0 then add("none")
  else
    local rows = math.ceil(#blist / 2)
    for i = 1, rows do
      add(string.format("%-30s %s", blist[i] or "", blist[i + rows] or ""))
    end
  end
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
  return table.concat(L, "\n")
end

local function build_production()
  local L = {}
  local function add(s) L[#L + 1] = s end
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
    local function pcell(e)
      if not e then return "" end
      return string.format("%-9s %s%d", (e.r:gsub("^%l", string.upper)), e.a >= 0 and "+" or "", e.a)
    end
    for i = 1, rows do
      add(string.format("%-24s %s", pcell(prod[i]), pcell(prod[i + rows])))
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
        local mk = (road and fort) and "[road+fort]" or (road and "[road]") or (fort and "[fort]") or ""
        rlist[#rlist + 1] = string.format("%-14s %s", f[2]:sub(1, 12), mk)
      end
    end
    table.sort(rlist)
    for _, r in ipairs(rlist) do add(r) end
  end
  add("")
  -- warehouse with freshness
  local wb = gv("BUILDINGS") .. ","
  local tier = wb:match("warehouse:(%d+),") or "1"
  local wmax = ({ ["1"] = 400, ["2"] = 1000, ["3"] = 1750, ["4"] = 3000, ["5"] = 5250 })[tier] or 400
  local byg, tot = {}, 0
  for _, e in ipairs(split(gv("WSTOCK"), ";")) do
    local f = split(e, "|")
    if f[1] and f[1] ~= "amber" then
      local amt, qq = num(f[2]), tonumber(f[3]) or 100
      local g = byg[f[1]]
      if not g then g = { amt = 0, qsum = 0, minq = 100, stale = 0 }; byg[f[1]] = g end
      g.amt = g.amt + amt
      g.qsum = g.qsum + qq * amt
      if qq < 100 then
        g.stale = g.stale + amt
        if qq < g.minq then g.minq = qq end
      end
      tot = tot + amt
    end
  end
  local stock = {}
  for good, g in pairs(byg) do stock[#stock + 1] = { good = good, g = g } end
  table.sort(stock, function(a, b) return a.good < b.good end)
  add(string.format("-- Warehouse  %d / %d --", tot, wmax))
  if #stock == 0 then add("empty")
  else
    for _, f in ipairs(stock) do
      local g = f.g
      local avgq = g.amt > 0 and math.floor(g.qsum / g.amt + 0.5) or 100
      local st = g.stale > 0 and string.format("  stale %d@%d%%", g.stale, g.minq) or ""
      add(string.format("%-10s %4d  q%3d%%%s", f.good, g.amt, avgq, st))
    end
  end
  return table.concat(L, "\n")
end

local function build_people()
  local L = {}
  local function add(s) L[#L + 1] = s end
  add("-- Forces --")
  add(string.format("Thralls %s   Followers %s", q("THRALLS"), q("THRALL_FOLLOWER"):sub(1, 30)))
  add(string.format("Garrison %s   Threk %s", q("GARRISON"), q("MTHREK")))
  add("")
  add("-- Hird Guard --")
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
  local function add(s) L[#L + 1] = s end
  add("-- Settlement --")
  add(string.format("Blot %s   Sproj %s", q("BLOT"):sub(1, 14), (gv("SPROJ") ~= "" and gv("SPROJ")) or "-"))
  add("")
  -- driven by the SEVENTS tick report; SETTLERS is the fallback for pop / water
  local s = split(gv("SETTLERS"), "|")
  local sts, srep = gv("SEVENTS"):match("^(%d+)|(.*)$")
  srep = srep or ""
  local pop            = srep:match("Settlers:%s*(%d+)")               or s[1]
  local moodname, mood = srep:match("Mood:%s*(%a+)%s*%((%d+)/100%)")
  local sentiment      = srep:match("Sentiment:%s*([%+%-]?%d+)")
  local inc, tax, comm = srep:match("Income:%s*%+?(%d+)%s*daler%s*%(tax%s*(%d+)%s*%+%s*community%s*(%d+)%)")
  local wr, wm         = srep:match("Water reserve:%s*(%d+)/(%d+)")
  if not wr then wr = s[4] end
  add("-- Settlers --")
  add(string.format("Population %s   Mood %s %s/100   Sentiment %s",
    pop or "?", moodname or "?", mood or "?", sentiment or "?"))
  if wr then add(string.format("Water reserve %s%s", wr, wm and ("/" .. wm) or "")) end
  add("")
  add(sts and "-- Economy (last tick) --" or "-- Economy --")
  if inc then
    add(string.format("Income  +%s d/tick   (tax %s + community %s)", inc, tax, comm))
  end
  local up = split(gv("UPKEEP"), "|")
  local upkeep = tonumber(up[#up])
  if upkeep then
    local net = (tonumber(inc) or 0) - upkeep
    add(string.format("Upkeep  %d d/tick   Net %s%d", upkeep, net >= 0 and "+" or "", net))
  end
  local nextt = tonumber(gv("NEXTTICK"))
  if nextt then
    local h, m = math.floor(nextt / 3600), math.floor((nextt % 3600) / 60)
    add("Next tick in " .. (h > 0 and (h .. "h " .. m .. "min") or (m .. "min")))
  end
  add("")
  if srep ~= "" then
    add("-- Demand (last tick) --")
    local parts = {}
    for _, k in ipairs({ "Food", "Spoils", "Mead" }) do
      local got, want = srep:match(k .. ":%s*consumed%s*(%d+)/(%d+)")
      if got then
        parts[#parts + 1] = string.format("%s %s/%s%s", k, got, want,
          (tonumber(got) >= tonumber(want)) and "" or " (short)")
      end
    end
    add(#parts > 0 and table.concat(parts, "   ") or "no tick report yet")
    add("")
  end
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
  local function add(s) L[#L + 1] = s end
  add("-- Holds: standing & reputation --")
  local vrep = parse_idx_table(gv("VREP"))
  local stand = parse_idx_table(gv("STANDINGS"))
  add(string.format("%-16s %-10s %6s %6s", "Hold", "Standing", "Bond", "Rep"))
  for i = 0, 20 do
    local v, s = vrep[i], stand[i]
    if v or s then
      local name = HOLDCITY[i] or (v and v[2]) or (s and s[2]) or ("?" .. i)
      add(string.format("%-16s %-10s %6s %6s", name:sub(1, 16),
        (s and s[4]) or "-", (s and s[3]) or "-", (v and v[3]) or "-"))
    end
  end
  add("")
  add("-- War status --")
  local blot = split(gv("BLOT"), "|")
  add(string.format("Blot %s   pool %s   (%s/%s)", blot[1] or "?", blot[2] or "?", blot[3] or "?", blot[4] or "?"))
  local raid = split(gv("RAID"), "|")
  add(string.format("Raid %s %s %s   Garrison %s", raid[1] or "?", raid[2] or "", raid[3] or "", q("GARRISON")))
  local varang = gv("VARANG")
  add(string.format("Monuments %s   Varangians %s", q("MONUMENTS"),
    (varang == "^" or varang == "") and "none" or varang))
  return table.concat(L, "\n")
end

local function build_voyage()
  local L = {}
  local function add(s) L[#L + 1] = s end
  local function listsec(title, field)
    add(title)
    local v = gv(field)
    if v == "" then add("none")
    else
      for item in v:gmatch("[^,]+") do
        add((item:gsub("^%s+", ""):gsub("%s+$", "")):sub(1, 60))
      end
    end
  end
  local function countsec(title, field)
    add(title)
    local v = gv(field)
    if v == "" then add("none")
    else
      for item in v:gmatch("[^,]+") do
        item = item:gsub("^%s+", ""):gsub("%s+$", "")
        local name, n = item:match("^(.-):(%-?%d+)$")
        if name then
          add(string.format("%-30s %s", titlecase(name):sub(1, 30), n))
        else
          add(item:sub(1, 60))
        end
      end
    end
  end
  listsec("-- Boons --", "VBOONS")
  add("")
  countsec("-- Aids --", "VAIDS")
  add("")
  countsec("-- Goods --", "VGOODS")
  add("")
  countsec("-- Curios --", "VCURIOS")
  return table.concat(L, "\n")
end

local function build_mission()
  local L = {}
  local function add(s) L[#L + 1] = s end
  add("-- Missions  (type: vmission fulfill <no>) --")
  if gv("MISSIONS") == "" then add("no missions")
  else
    add(string.format("%-4s %-16s %-24s %s", "no", "town", "needs", "rep"))
    for _, ms in ipairs(split(gv("MISSIONS"), ";")) do
      local f = split(ms, "|")
      -- id | desc | rep | ? | expiry | (empty) | town | goods(good:qty,...)
      local goods = (f[8] or ""):gsub(":", ""):gsub(",", " "):sub(1, 24)
      add(string.format("%-4s %-16s %-24s %s", f[1] or "?", (f[7] or "?"):sub(1, 16), goods, f[3] or "?"))
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

local function build_feeds()
  local keys = {}
  for k in pairs(seen_keys) do keys[#keys + 1] = k end
  table.sort(keys)
  local L = {}
  for _, k in ipairs(keys) do
    local v = gv(k)
    L[#L + 1] = string.format("%-14s %s", k:sub(1, 14), v:sub(1, 40))
  end
  if #L == 0 then L[1] = "(no feed keys seen yet - waiting for the feed)" end
  return table.concat(L, "\n")
end

-- --------------------------------------------------------------- Map tab
local function build_map()
  local hd = split(gv("VMAPH"), "|")
  local mw, mh, px, py = num(hd[1]), num(hd[2]), num(hd[3]), num(hd[4])
  if mw == 0 then
    scrye.setState(P .. "maphdr", "(waiting for map feed - loads on the next map update)")
    scrye.setState(P .. "map", "")
    scrye.setState(P .. "maplocs", "")
    return
  end
  scrye.setState(P .. "maphdr", string.format("Territory %dx%d  @ %d,%d", mw, mh, px, py))
  local unknown, pois, grid = {}, {}, {}
  for row = 0, mh - 1 do
    local rowstr = gv(string.format("VMR%02d", row))
    local mask = gv(string.format("MEE%02d", row))
    local out = {}
    for col = 1, math.min(#rowstr, mw) do
      local ch = rowstr:sub(col, col)
      if not MAP_PAL[ch] then unknown[ch] = true; ch = "?" end
      if mask ~= "" and mask:sub(col, col) == "0" then ch = " " end
      out[#out + 1] = ch
      local orig = rowstr:sub(col, col)
      if POI_LABEL[orig] then pois[#pois + 1] = { x = col - 1, y = row, ch = orig } end
    end
    grid[#grid + 1] = table.concat(out)
  end
  scrye.setState(P .. "map", table.concat(grid, "\n"))
  -- merged locations list: feed-named + grid-scanned + known defaults
  local locs = parse_locs(gv("VMAPL"))
  local merged, seen = {}, {}
  for _, lc in ipairs(locs) do
    seen[lc.x .. "|" .. lc.y] = true
    lc.name = locname(lc.x, lc.y) or lc.name
    merged[#merged + 1] = lc
  end
  for _, p in ipairs(pois) do
    local key = p.x .. "|" .. p.y
    if not seen[key] then
      seen[key] = true
      merged[#merged + 1] = { type = POI_LABEL[p.ch], name = locname(p.x, p.y), x = p.x, y = p.y }
    end
  end
  local known = {}
  for key in pairs(TRAVEL_CODE) do known[key] = true end
  for key in pairs(DEFAULT_LOCNAMES) do known[key] = true end
  for key in pairs(known) do
    if not seen[key] then
      local x, y = key:match("^(%-?%d+)|(%-?%d+)$")
      local typ
      if TRAVEL_CODE[key] then typ = (key == "35|17") and "Cap" or "Lin"
      else typ = DEFAULT_LOCTYPE[key] or "POI" end
      merged[#merged + 1] = { type = typ, name = locname(tonumber(x), tonumber(y)),
                              x = tonumber(x), y = tonumber(y) }
    end
  end
  local L = { "-- Locations --" }
  if #merged > 0 then
    local function rank(e)
      if travel_code(e.x .. "|" .. e.y) then return 1 end
      if e.name then return 2 end
      return 3
    end
    table.sort(merged, function(a, b)
      local ra, rb = rank(a), rank(b)
      if ra ~= rb then return ra < rb end
      local na, nb = a.name or "", b.name or ""
      if na ~= nb then return na < nb end
      if a.y ~= b.y then return a.y < b.y end
      return a.x < b.x
    end)
    for _, lc in ipairs(merged) do
      local t = (lc.type or "?"):gsub("_.*$", "")
      t = t:sub(1, 1):upper() .. t:sub(2)
      L[#L + 1] = string.format("%-7s %-16s (%d,%d)", t:sub(1, 7), (lc.name or "-"):sub(1, 16), lc.x, lc.y)
    end
  end
  local u = {}
  for ch in pairs(unknown) do u[#u + 1] = ch end
  if #u > 0 then
    table.sort(u)
    L[#L + 1] = "unmapped terrain chars: " .. table.concat(u, " ")
  end
  scrye.setState(P .. "maplocs", table.concat(L, "\n"))
end

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
  if gv("CPT04") == "" then
    scrye.setState(P .. "plangrid", "")
    scrye.setState(P .. "planlist", "no city-plan feed yet - vtoggle mip_city")
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
  -- 12x12 inner grid: CPT rows 4-15, cols 5-16
  local grid = {}
  for r = 0, 11 do
    local rowstr = gv("CPT" .. string.format("%02d", r + 4))
    local out = {}
    for c = 0, 11 do
      local ch = rowstr:sub(5 + c, 5 + c)
      local bld = pmap[r .. "," .. c]
      if bld then
        local role = PLAN_BY_L[bld.letter] and PLAN_BY_L[bld.letter].role
        out[#out + 1] = (role and ROLE_DIGIT[role]) or "6"
      elseif PLAN_PAL[ch] then
        out[#out + 1] = ch
      else
        out[#out + 1] = "?"
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

-- ------------------------------------------------- auto sea-navigation
local sea_nav = {
  on      = (scrye.store.get("seanav") == "1"),   -- default OFF (safety)
  target  = nil,
  visited = {},
  voyage  = "",
  resolve = scrye.store.get("seanav_resolve")
            or "hold,evade?hull<40,hunt,ration,salvage,resupply?supplies<50,plunder",
  last_cmd_at = 0,
}
local SEANAV_TARGETS = { I = true, W = true, X = true }

local function update_seanav_state()
  local s = "Auto-nav: " .. (sea_nav.on and "ON" or "off")
  if sea_nav.on and sea_nav.target then s = s .. "  ->  " .. sea_nav.target.coord end
  if sea_nav.on and sea_nav.original then s = s .. "  (resume " .. sea_nav.original .. ")" end
  s = s .. "   resolve: " .. (sea_nav.resolve == "" and "manual" or sea_nav.resolve)
  s = s .. "   (vnav on|off | vnav resolve <opt>)"
  scrye.setState(P .. "seanav", s)
end

local function sea_chart_scan()
  local h = num(split(gv("VCHH"), "|")[2] or "")
  if h <= 0 then return nil, nil end
  local feats, ship = {}, nil
  for r = 0, h - 1 do
    local rowstr = gv(string.format("VCR%02d", r))
    if rowstr ~= "" then
      for c = 1, #rowstr do
        local s = rowstr:sub(c, c)
        if s == "S" then ship = { r = r, c = c }
        elseif SEANAV_TARGETS[s] then
          feats[#feats + 1] = { coord = string.char(65 + r) .. string.format("%02d", c), r = r, c = c, sym = s }
        end
      end
    end
  end
  return feats, ship
end

local function vqpath_dest(s)
  local nums = {}
  for n in (s or ""):gmatch("%d+") do nums[#nums + 1] = tonumber(n) end
  if #nums < 2 then return nil end
  local col, row = nums[#nums - 1], nums[#nums]
  return string.char(65 + row) .. string.format("%02d", col)
end

local function sea_nav_tick()
  if not sea_nav.on then return end
  if not connected then return end
  local vy = split(gv("VOYAGE"), "|")
  if #vy < 10 then
    sea_nav.had_voyage = false
    return
  end
  local vid = vy[4] or ""
  if vid ~= sea_nav.voyage or not sea_nav.had_voyage then
    sea_nav.voyage = vid; sea_nav.visited = {}; sea_nav.target = nil; sea_nav.original = nil
  end
  sea_nav.had_voyage = true
  local feats, ship = sea_chart_scan()
  if not ship then return end

  local has_node = gv("VRESOLVE") ~= ""

  local function try_resolve()
    if not has_node then return end
    if sea_nav.resolve == "" or sea_nav.resolve == "off" then return end
    if (now_s - (sea_nav.last_cmd_at or 0)) < 3 then return end
    local optsrc = (gv("VOFFERS") ~= "") and gv("VOFFERS") or gv("VRESOLVE")
    local opts = split(optsrc or "", ",")
    local offered = {}
    for _, o in ipairs(opts) do offered[o:gsub("%s", "")] = true end
    local vy2 = split(gv("VOYAGE"), "|")
    local metric = { hull = num(vy2[11]), morale = num(vy2[12]), supplies = num(vy2[13]), stress = num(vy2[14]) }
    local function cond_ok(c)
      if not c or c == "" then return true end
      local m, op, n = c:match("^(%a+)([<>]=?)(%d+)$")
      if not m then return true end
      local val = metric[m:lower()]; if not val then return true end
      n = tonumber(n)
      if op == "<"  then return val <  n
      elseif op == ">"  then return val >  n
      elseif op == "<=" then return val <= n
      elseif op == ">=" then return val >= n end
      return true
    end
    local pick
    if sea_nav.resolve == "first" then
      pick = opts[1] and opts[1]:gsub("%s", "")
    else
      for entry in sea_nav.resolve:gmatch("[^,]+") do
        local kw, cond = entry:match("^%s*([^?%s]+)%s*%??%s*(.-)%s*$")
        if kw and offered[kw] and cond_ok(cond) then pick = kw; break end
      end
    end
    if pick and pick ~= "" then
      sea_nav.last_cmd_at = now_s
      scrye.send("vvoyage resolve " .. pick)
      scrye.print("[sea-nav] auto-resolving node with '" .. pick .. "'")
    end
  end

  -- active target: hold course until we actually arrive (see original for rationale)
  if sea_nav.target then
    local t = sea_nav.target
    local dest = vqpath_dest(gv("VQPATH"))
    if dest == t.coord then sea_nav.enroute = true end
    local onit = (ship.r == t.r and ship.c == t.c)
    local adj  = (math.abs(ship.r - t.r) <= 1 and math.abs(ship.c - t.c) <= 1)
    local arrived = onit or (has_node and adj) or (sea_nav.enroute and dest == nil)
    if arrived then
      sea_nav.visited[t.coord] = true
      sea_nav.target = nil
      sea_nav.enroute = false
    else
      try_resolve()
      update_seanav_state()
      return
    end
  end

  if has_node then
    try_resolve()
    update_seanav_state()
    return
  end

  -- free to pick: nearest unvisited feature
  local best, bd
  for _, f in ipairs(feats) do
    if not sea_nav.visited[f.coord] then
      local d = (f.r - ship.r) ^ 2 + (f.c - ship.c) ^ 2
      if not bd or d < bd then best, bd = f, d end
    end
  end
  if not best then
    if sea_nav.original and not sea_nav.visited[sea_nav.original]
       and (now_s - (sea_nav.last_cmd_at or 0)) >= 2 then
      local orig = sea_nav.original
      sea_nav.original = nil
      sea_nav.last_cmd_at = now_s
      scrye.send("vvoyage clear")
      scrye.send("vvoyage queue " .. orig)
      scrye.print("[sea-nav] all features toured - resuming original course to " .. orig)
    end
    update_seanav_state()
    return
  end
  if (now_s - (sea_nav.last_cmd_at or 0)) >= 2 then
    if not sea_nav.original then
      local od = vqpath_dest(gv("VQPATH"))
      if od and od ~= best.coord then sea_nav.original = od end
    end
    sea_nav.target = { coord = best.coord, r = best.r, c = best.c }
    sea_nav.enroute = false
    sea_nav.last_cmd_at = now_s
    scrye.send("vvoyage clear")
    scrye.send("vvoyage queue " .. best.coord)
    scrye.print("[sea-nav] course set to " .. best.sym .. " at " .. best.coord)
  end
  update_seanav_state()
end

-- --------------------------------------------------------------- Sea tab
local function build_sea()
  local L = {}
  local function add(s) L[#L + 1] = s end
  local v = split(gv("VOYAGE"), "|")
  if #v < 10 then
    add("-- Sea --")
    add("No voyage under way.")
    local ships = split(gv("SHIPS"), ";")
    if #ships > 0 and ships[1] ~= "" then
      add("")
      add("-- Fleet --")
      if gv("SHPLOTS") ~= "" then
        add("Ship plots: " .. gv("SHPLOTS"):gsub("|", " / "))
      end
      for i = 1, math.min(#ships, 10) do
        local f = split(ships[i], "|")
        add(string.format("%-13s %-9s %s", f[1] or "?", f[3] or "?", f[4] or ""))
      end
    end
    scrye.setState(P .. "sea", table.concat(L, "\n"))
    scrye.setState(P .. "seachart", "")
    return
  end
  add(string.format("-- Voyage: %s - %s (%s) --", v[3] or "?", v[4] or "?", v[5] or "?"))
  local vpx, vpy = num(v[7]), num(v[8])
  local pos = string.char(65 + vpy) .. string.format("%02d", vpx + 1)
  add(string.format("State:    %-9s Next square: %ss", v[1] or "?", v[18] or "?"))
  add(string.format("Position: %-9s Steps: %s", pos, v[17] or "?"))
  add(string.format("Threat:   %s [%s]", v[19] or "-", v[20] or "?"))
  add(string.format("Pressure: %s   Danger: %s", v[21] or "?", v[6] or "?"))
  add(string.format("Captain:  %s", v[25] or "?"))
  add(string.format("Identity: %s", v[26] or "?"))
  add(("Traits:   " .. (v[27] or "") .. ", " .. (v[28] or "")):sub(1, 40))
  add(string.format("Hull %s%%   Morale %s%%   Supplies %s%%   Stress %s%%",
    v[11] or "?", v[12] or "?", v[13] or "?", v[14] or "?"))
  add(string.format("Crew %s/%s", v[15] or "?", v[16] or "?"))
  local wx = split(gv("WEATHER"), "|")
  add(string.format("Weather: %s/%s", wx[1] or "?", wx[2] or "?"))
  -- chart
  local ch = split(gv("VCHH"), "|")
  local cw, chh = num(ch[1]), num(ch[2])
  local unknown = {}
  if cw > 0 then
    add("")
    add(string.format("-- Chart %dx%d  (%s) --  queue a course with: vvoyage queue <coord>", cw, chh, ch[3] or "?"))
    local grid = {}
    for row = 0, chh - 1 do
      local rowstr = gv(string.format("VCR%02d", row))
      local out = {}
      for col = 1, cw do
        local c = rowstr:sub(col, col)
        if c == "" then c = "#" end
        if not SEA_PAL[c] then unknown[c] = true; c = "?" end
        out[#out + 1] = c
      end
      grid[#grid + 1] = table.concat(out)
    end
    scrye.setState(P .. "seachart", table.concat(grid, "\n"))
  else
    scrye.setState(P .. "seachart", "")
  end
  -- pending resolve options (buttons in the original; type the command here)
  local resolves = {}
  for a in gv("VRESOLVE"):gmatch("[^,]+") do
    local t = a:gsub("^%s+", ""):gsub("%s+$", "")
    if t ~= "" then resolves[#resolves + 1] = t end
  end
  if #resolves > 0 then
    add("Resolve pending: " .. table.concat(resolves, ", ") .. "   (vvoyage resolve <opt>)")
  else
    add("(no resolve pending)")
  end
  local u = {}
  for c in pairs(unknown) do u[#u + 1] = c end
  if #u > 0 then table.sort(u); add("unmapped chart chars: " .. table.concat(u, " ")) end
  -- saga
  local saga = split(gv("VSAGA"), ";")
  if #saga > 0 then
    add("")
    add("-- Saga --")
    for i = math.max(1, #saga - 2), #saga do
      add(saga[i]:sub(1, 74))
    end
  end
  scrye.setState(P .. "sea", table.concat(L, "\n"))
end

-- ------------------------------------------------------------ the flush
local BUILDERS = {
  stats      = function() scrye.setState(P .. "stats", build_stats()) end,
  city       = function() scrye.setState(P .. "city", build_city()) end,
  builds     = function() scrye.setState(P .. "builds", build_builds()) end,
  production = function() scrye.setState(P .. "production", build_production()) end,
  people     = function() scrye.setState(P .. "people", build_people()) end,
  settlers   = function() scrye.setState(P .. "settlers", build_settlers()) end,
  holds      = function() scrye.setState(P .. "holds", build_holds()) end,
  sea        = build_sea,
  voyage     = function() scrye.setState(P .. "voyage", build_voyage()) end,
  map        = build_map,
  plan       = build_plan,
  mission    = function() scrye.setState(P .. "mission", build_mission()) end,
  feeds      = function() scrye.setState(P .. "feeds", build_feeds()) end,
}

flush = function()
  flush_pending = false
  if sea_nav_pending then
    sea_nav_pending = false
    pcall(sea_nav_tick)
  end
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
keymap("stats", "god_power god_power_focus god_power_next raid blot lin glvl sub daler kap aud "
  .. "vis soe vkxp vmnew vmreg nexttick weather dcycle stfx fury threk mthrek chain bsdepth "
  .. "rndz ldng mldng patrol")
keymap("city", "ships carts refinery")
keymap("builds", "buildings builds daler")
keymap("production", "production routes buildings wstock")
keymap("people", "thralls thrall_follower garrison mthrek hird staff bonds")
keymap("settlers", "blot sproj settlers sevents upkeep nexttick scivics sconsume")
keymap("holds", "vrep standings blot raid garrison monuments varang")
keymap("sea", "voyage ships shplots weather vchh vresolve voffers vsaga vqpath")
keymap("voyage", "vboons vaids vgoods vcurios")
keymap("map", "vmaph vmapl")
keymap("mission", "missions errand")
keymap("plan", "cplan cpb")

local SEANAV_KEYS = { voyage = true, vresolve = true, vqpath = true, voffers = true }

scrye.watch("vik", function(value, path)
  local key = path:match("^vik%.(.+)$")
  if not key then return end
  local is_row = key:match("^vmr%d") or key:match("^mee%d") or key:match("^mes%d")
              or key:match("^vcr%d") or key:match("^cpt%d")
  if not is_row then
    seen_keys[key] = true
    dirty.feeds = true
  end
  local secs = KEYMAP[key]
  if secs then
    for _, s in ipairs(secs) do dirty[s] = true end
  elseif key:match("^vmr%d") or key:match("^mee%d") then
    dirty.map = true
  elseif key:match("^vcr%d") then
    dirty.sea = true
  elseif key:match("^cpt%d") then
    dirty.plan = true
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
  if SEANAV_KEYS[key] or key:match("^vcr%d") then sea_nav_pending = true end
  schedule_flush()
end)

-- ----------------------------------------------------- Modrsokn cooldown
-- 3-minute combat-ability cooldown, started by the "rage inward" line.
local MORDSOKN_CD = 180
local mordsokn_left = 0

scrye.addTrigger{
  pattern = "You close your eyes and turn the rage inward",
  regex = true,
  run = function()
    mordsokn_left = MORDSOKN_CD
    scrye.print("[Modrsokn] used - 3:00 cooldown")
  end,
}

-- 1s heartbeat: elapsed-seconds counter + Modrsokn countdown
scrye.every(1, function()
  now_s = now_s + 1
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

scrye.addAlias{
  pattern = "^vmapon$", regex = true,
  run = function()
    scrye.send("vtoggle mip_map")
    scrye.send("vtoggle mip_city")
    scrye.send("vtoggle mip_extra")
    scrye.print("[vmip] requested mip_map+city+extra feed")
  end,
}

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

scrye.addAlias{
  pattern = "^vnav (on|off)$", regex = true,
  run = function(s)
    sea_nav.on = (s == "on")
    scrye.store.set("seanav", sea_nav.on and "1" or "0")
    scrye.print("[sea-nav] auto-navigation " .. (sea_nav.on and "ON" or "OFF"))
    update_seanav_state()
    if sea_nav.on then pcall(sea_nav_tick) end
  end,
}

scrye.addAlias{
  pattern = [[^vnav resolve (.+)$]], regex = true,
  run = function(s)
    s = (s or ""):gsub("^%s*(.-)%s*$", "%1")
    sea_nav.resolve = (s == "off") and "" or s
    scrye.store.set("seanav_resolve", sea_nav.resolve)
    scrye.print("[sea-nav] auto-resolve " ..
      (sea_nav.resolve == "" and "OFF (hold at nodes, you resolve)" or ("preference: " .. sea_nav.resolve)))
    update_seanav_state()
  end,
}

scrye.addAlias{
  pattern = "^vnav reset$", regex = true,
  run = function()
    sea_nav.visited = {}; sea_nav.target = nil; sea_nav.original = nil
    scrye.print("[sea-nav] toured list cleared - all charted features are candidates again")
    update_seanav_state()
    pcall(sea_nav_tick)
  end,
}

scrye.addAlias{
  pattern = [[^vikloc (\d+) (\d+)(?:\s+(.*))?$]], regex = true,
  run = function(x, y, name)
    name = (name or ""):gsub("^%s+", ""):gsub("%s+$", "")
    local key = x .. "|" .. y
    if name == "" then locnames[key] = nil else locnames[key] = name end
    local out = {}
    for k, n in pairs(locnames) do out[#out + 1] = k .. "|" .. n end
    scrye.store.set("locnames", table.concat(out, "\n"))
    scrye.print("[viking] location (" .. x .. "," .. y .. "): " .. (name == "" and "(cleared)" or name))
    dirty.map = true
    schedule_flush()
  end,
}

-- "I am here" correction: set the remembered current town (matches abbrev or full name).
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

scrye.addPanel{
  title = "Viking Status",
  width = 560,
  accent = "#5A93D4",          -- signature: viking steel-blue
  tabs = {
    { title = "Stats", widgets = {
        { type = "gauge", text = "HP",   value = "character.health.current", max = "character.health.max" },
        { type = "gauge", text = "Seid", value = "vik.seid", max = "vik.mseid" },
        { type = "gauge", text = "Vig",  value = "vik.vig",  max = "vik.mvig" },
        { type = "gauge", text = "Rad",  value = "vik.rad",  max = "vik.mrad" },
        { type = "value", text = "Enemy: ", bind = "enemy.name", color = "#E0524D" },              -- red: enemy
        { type = "progress", text = "Enemy HP", value = "enemy.health", max = 100, color = "#E0524D" },
        { type = "value", text = "Modrsokn: ", bind = P .. "mordsokn", color = "#6FB7E0" },         -- info blue
        { type = "text", bind = P .. "stats" },
        { type = "button", text = "Commit patrol (last count)", action = patrol_commit },
    } },
    { title = "City", widgets = {
        { type = "text", bind = P .. "city" },
        { type = "label", text = "-- Refinery --   bar = fill x quality (amber raw -> green refined)", color = "#8FA0B0" },
        { type = "barlist", bind = P .. "refinery" },
    } },
    { title = "Builds", widgets = {
        { type = "text", bind = P .. "builds" },
    } },
    { title = "Production", widgets = {
        { type = "text", bind = P .. "production" },
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
    { title = "Sea", widgets = {
        { type = "value", text = "", bind = P .. "seanav", color = "#5A93D4" },   -- section header
        { type = "text", bind = P .. "sea" },
        { type = "colorgrid", bind = P .. "seachart", palette = SEA_PAL },
        { type = "label", text = "S ship  X objective  H harbor  W wreck  T storm  I island  F fog  # unrevealed  O open sea  * resolved  B stormbelt  + path  > destination" },
        { type = "button", text = "Clear voyage queue", action = function() scrye.send("vvoyage clear") end },
    } },
    { title = "Voyage", widgets = {
        { type = "text", bind = P .. "voyage" },
    } },
    { title = "Map", widgets = map_widgets },
    { title = "Travel", widgets = travel_widgets },
    { title = "Plan", widgets = {
        { type = "value", text = "", bind = P .. "planhdr", color = "#5A93D4" },   -- section header
        { type = "colorgrid", bind = P .. "plangrid", palette = PLAN_PAL },
        { type = "text", bind = P .. "planlist" },
    } },
    { title = "Mission", widgets = {
        { type = "text", bind = P .. "mission" },
        { type = "button", text = "Fetch",  action = function() scrye.send("vmission newbie fetch") end },
        { type = "button", text = "Submit", action = function() scrye.send("vmission newbie submit") end },
    } },
    { title = "Feeds", widgets = {
        { type = "text", bind = P .. "feeds" },
    } },
  },
}

-- ------------------------------------------------------------------ init
update_seanav_state()
mark_all()
flush()
