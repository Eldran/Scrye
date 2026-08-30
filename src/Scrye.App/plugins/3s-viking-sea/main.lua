-- 3S Viking Sea -- the fleet's world: Sea / Voyage / Map / Travel, carved out of
-- 3s-viking-status per docs/Plan-Viking-GMCP.md (the three-way split) and rebuilt
-- on the Guild.* GMCP packages.
--
-- Lineage: the sea half of 3s-viking-status 1.5.1 (the MIP classic), composers
-- kept verbatim where the data allows. Same port shape as 3s-viking-status-gmcp:
-- page-assembled Guild.* snapshots (gasm, bottom of file) are translated into the
-- key->string table (vset/V) the classic parsers read. Both sides of each string
-- format live in this file - a private contract, not a wire protocol.
--
-- Packages consumed: Guild.Voyage (voyage status, chart, resolve, saga, boons/
-- aids/goods/curios), Guild.Map (territory terrain + position), Guild.Fleet
-- (the fleet list when no voyage is under way), Guild.Settlement (ship plots).
--
-- Town travel: the ENGINE lives in 3s-viking-status-gmcp (its mission runner
-- needs it), which owns the vgo/vhere aliases. This plugin's Travel list and
-- mission-free map clicks route there: text clicks run "vgo <town>" through the
-- command pipeline, the Map tab's colorgrid click emits the "viking.travel"
-- event that 3s-viking-status-gmcp listens for. Without that plugin loaded the
-- clicks go nowhere - the Travel tab says so.
--
-- Capture status (27-28 Aug):
--   * voyage_chart_rows CONFIRMED (string rows; the chart renders in play). The
--     rows also carry an 'M' tile (landmass, by its placement - verify the word).
--   * ship/crew traits CONFIRMED as plain string arrays; vboons is a counter
--     object; vmem carries crew-memory sentences (shown on the Voyage tab).
--   * Guild.Map terrain: CONFIRMED GAP - two sessions sent only the east/south
--     edge grids, pos and w, though enc={terrain:"glyph"} promises terrain. On
--     the dev-report list; the Map tab waits honestly until it ships.
--   * vqpath CONFIRMED live (23:09 capture, active voyage): an array of "x,y"
--     strings - ["10,1"] - exactly what the adapter guessed, so vqpath_dest and
--     the sea-nav read it unchanged. voyage_chart {width,height,chart_mode}
--     CONFIRMED too (16x16 "advanced"). vresolve still unseen.
--   * vmapl (named locations) has no GMCP source seen: the locations list runs
--     on the built-in defaults + your vikloc names.

local P = "plugin." .. scrye.id .. "."

-- ---------------------------------------------------------------- colour
local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end
local function col(c, s) return "@{" .. c .. "}" .. esc(s) .. "@{}" end

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

local function num(s) return tonumber(s) or 0 end

local function titlecase(s)
  return (s or ""):gsub("_", " "):gsub("(%a)([%w]*)", function(a, b) return a:upper() .. b end)
end

-- ------------------------------------------------------------- palettes
-- (carried over from the classic; original colours were MUSHclient BGR)

-- territory map: terrain char -> colour
local MAP_PAL = {
  ["."] = "#101010",                                       -- void / walls
  ["t"] = "#606060", ["T"] = "#707070",                    -- tundra grey
  ["h"] = "#BAB245", ["H"] = "#BAB245",                    -- hills yellow
  ["A"] = "#D03030",                                       -- mountains red
  ["f"] = "#208020", ["F"] = "#30A030",                    -- forest
  ["p"] = "#60C060",                                       -- plains
  ["W"] = "#3991B7", ["w"] = "#3991B7", ["~"] = "#3991B7", -- water
  ["r"] = "#232323", ["="] = "#303030",                    -- road / bridge
  ["P"] = "#909090",                                       -- gate / passage
  ["L"] = "#D76F04",                                       -- lin hold
  ["S"] = "#DEC358",                                       -- settlement
  ["C"] = "#C02020",                                       -- capital
  ["R"] = "#9256A0",                                       -- ruins
  ["M"] = "#C02020",                                       -- Midgard (capital)
  ["*"] = "#E060E0",                                       -- point of interest
  ["X"] = "#FFFFFF",                                       -- you (feed marker)
  [" "] = "#000000",                                       -- masked (unexplored)
  ["?"] = "#282828",                                       -- unmapped char
}
local MAP_ICONS = {
  ["t"] = "dashes",  ["T"] = "dashes",
  ["h"] = "hill",    ["H"] = "hill",
  ["A"] = "mountain",
  ["f"] = "tree",    ["F"] = "pine",
  ["p"] = "grass",
  ["W"] = "water",   ["w"] = "water", ["~"] = "water",
  ["P"] = "gate",
  ["L"] = "tower",
  ["S"] = "house",
  ["C"] = "crown",   ["M"] = "crown",
  ["R"] = "ruin",
  ["*"] = "star",
  ["X"] = "person",
}

-- voyage chart char -> colour
local SEA_PAL = {
  ["#"] = "#303030",                      -- unrevealed
  ["O"] = "#3991B7", ["~"] = "#3991B7",   -- open sea
  ["F"] = "#909090",                      -- fog
  ["?"] = "#505050",                      -- unknown
  ["I"] = "#BAB245",                      -- island
  ["H"] = "#4CA563",                      -- harbor
  ["W"] = "#E04040",                      -- wreck
  ["T"] = "#F05050",                      -- storm
  ["X"] = "#E060E0",                      -- objective
  ["S"] = "#FFFFFF",                      -- your ship
  ["+"] = "#79D963", [">"] = "#D18E24",   -- queued path / destination
  ["="] = "#3D73B6",                      -- crosscurrent
  ["^"] = "#57583A",                      -- deadwater
  ["B"] = "#A84E7C",                      -- stormbelt
  ["*"] = "#456F4E",                      -- resolved node
  ["M"] = "#7A6A4F",                      -- landmass (28 Aug capture; VERIFY the reading)
  [" "] = "#19232A",                      -- sea
}
local SEA_ICONS = {
  ["O"] = "water",  ["~"] = "water",
  ["F"] = "dashes",
  ["M"] = "hill",
  ["I"] = "hill",
  ["H"] = "anchor",
  ["W"] = "cross",
  ["T"] = "bolt",
  ["X"] = "star",
  ["S"] = "ship",
  ["+"] = "dot",
  [">"] = "flag",
  ["*"] = "dot",
}

local POI_LABEL = { L = "Lin", S = "Set", C = "Cap", ["*"] = "POI", R = "Ruin", M = "Cap" }

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
local TRAVEL_CODE = {
  ["35|17"] = "Mid", ["17|9"] = "Lod", ["55|7"] = "Eir", ["13|17"] = "Ima",
  ["57|17"] = "Hol", ["37|7"] = "Haf", ["33|27"] = "Upp", ["19|25"] = "Bor",
  ["53|25"] = "Vas", ["23|21"] = "Sve", ["49|9"] = "Eri", ["15|23"] = "Bir",
  ["55|21"] = "Ler", ["25|9"] = "Nid",
}
local SPECIAL_TRAVEL = { ["3|28"] = "Blot" }
local function travel_code(key) return TRAVEL_CODE[key] or SPECIAL_TRAVEL[key] end

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

local TOWN_COORD = {}
for coord, code in pairs(TRAVEL_CODE)    do TOWN_COORD[code] = coord end
for coord, code in pairs(SPECIAL_TRAVEL) do TOWN_COORD[code] = coord end

local function town_label(code)
  local coord = TOWN_COORD[code]
  if coord then
    local x, y = coord:match("^(%-?%d+)|(%-?%d+)$")
    return locname(tonumber(x), tonumber(y)) or code
  end
  return code
end

-- travelable towns, sorted by display name
-- Every coordinate with a name is a destination: the travel engine plans by BFS
-- over the Guild.Map grids now, so a site needs coordinates and a name, not a
-- pair of hand-recorded routes. Same rule as the engine's own list, so the two
-- agree without either owning the other.
local TRAVEL_TOWNS = {}
local function rebuild_travel_towns()
  local seen = {}
  TRAVEL_TOWNS = {}
  local function add(code, coord)
    if seen[code] then return end
    seen[code] = true
    TOWN_COORD[code] = TOWN_COORD[code] or coord
    TRAVEL_TOWNS[#TRAVEL_TOWNS + 1] = code
  end
  for coord, code in pairs(TRAVEL_CODE)    do add(code, coord) end
  for coord, code in pairs(SPECIAL_TRAVEL) do add(code, coord) end
  for coord in pairs(DEFAULT_LOCNAMES)     do add(travel_code(coord) or coord, coord) end
  for coord in pairs(locnames)             do add(travel_code(coord) or coord, coord) end
  table.sort(TRAVEL_TOWNS, function(a, b) return town_label(a) < town_label(b) end)
end
rebuild_travel_towns()

-- The settlement list is clickable TEXT: the click runs "vgo <town>" through the
-- command pipeline, which is 3s-viking-status-gmcp's alias (it owns the travel
-- engine). Republished whenever `vikloc` names a place, since naming one is now
-- all it takes to make it travelable.
local function publish_town_list()
  local lines, row = {}, {}
  for _, code in ipairs(TRAVEL_TOWNS) do
    local name = town_label(code)
    row[#row + 1] = string.format("@{accent,click=vgo %s}%s@{}%s",
      name, esc(name), string.rep(" ", math.max(1, 18 - #name)))
    if #row == 3 then lines[#lines + 1] = table.concat(row); row = {} end
  end
  if #row > 0 then lines[#lines + 1] = table.concat(row) end
  scrye.setState(P .. "towns", table.concat(lines, "\n"))
end

-- The travel engine lives in the other plugin and cannot read this one's store
-- (both scrye.store and scrye.shared are scoped per plugin), so the names you
-- give places are handed over the same event bus the Map tab already uses to
-- ask for walks. Every name that crosses is a destination BFS can reach.
local function publish_locnames()
  local out = {}
  for k, n in pairs(locnames) do out[#out + 1] = k .. "|" .. n end
  scrye.emit("viking.locnames", table.concat(out, "\n"))
end

publish_town_list()
publish_locnames()

-- the Map tab's colorgrid click cannot ride the command pipeline (it is a Lua
-- callback), so it asks the travel engine over the event bus instead
local function ask_travel(town)
  scrye.emit("viking.travel", scrye.json.encode({ town = town }))
  scrye.print("[sea] asking the travel engine for " .. town
    .. " (needs 3s-viking-status-gmcp loaded)")
end

-- ---------------------------------------------------- the translated feed
local V = {}
local function gv(k) return V[k:lower()] or "" end

-- ---------------------------------------------------- clocks / connection
local now_s = 0
local connected = true
scrye.onConnect(function() connected = true end)
scrye.onDisconnect(function() connected = false end)

-- ------------------------------------------------------- dirty / flush
local dirty = {}
local flush_pending = false
local sea_nav_pending = false
local flush   -- forward decl

local function schedule_flush()
  if flush_pending then return end
  flush_pending = true
  scrye.after(1, function() flush() end)
end

-- key -> tabs that must rebuild when it changes
local KEYMAP = {}
local function keymap(sec, keys)
  for k in keys:gmatch("%S+") do
    local t = KEYMAP[k] or {}
    t[#t + 1] = sec
    KEYMAP[k] = t
  end
end
keymap("sea", "voyage ships shplots weather vchh vresolve voffers vsaga vqpath")
keymap("voyage", "vboons vaids vgoods vcurios vmem")
keymap("map", "vmaph vmapl")

local SEANAV_KEYS = { voyage = true, vresolve = true, vqpath = true, voffers = true }

local function vset(key, value)
  key = key:lower()
  value = value == nil and "" or tostring(value)
  if V[key] == value then return end
  V[key] = value
  local secs = KEYMAP[key]
  if secs then
    for _, s2 in ipairs(secs) do dirty[s2] = true end
  elseif key:match("^vmr%d") or key:match("^mee%d") then
    dirty.map = true
  elseif key:match("^vcr%d") then
    dirty.sea = true
  end
  if SEANAV_KEYS[key] or key:match("^vcr%d") then sea_nav_pending = true end
  schedule_flush()
end

-- ------------------------------------------------- auto sea-navigation
-- (ported verbatim from the classic; reads the translated V keys)
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

local SEALETTERS = "SXHWTI>*B="
local SEA_LEGEND = "SXHWTIF#O*B+>=^"

local function sea_coord(cl, rw) return string.char(65 + rw) .. string.format("%02d", cl + 1) end

local SEA_NAME = {
  S = "ship", X = "objective", H = "harbor", W = "wreck", T = "storm",
  I = "island", F = "fog", ["#"] = "unrevealed", O = "open sea",
  ["*"] = "resolved", B = "stormbreak", [">"] = "course",
}

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
  -- voyage identity: ship_id + contract (a fresh voyage clears the toured list)
  local vid = (vy[10] or "") .. ":" .. (vy[4] or "")
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

  -- active target: hold course until we actually arrive
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
  local function add(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
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
  -- (the classic showed a weather word here; GMCP carries only weather_key so far)
  if gv("WEATHER") ~= "" then add("Weather key: " .. gv("WEATHER")) end
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
      for cc = 1, cw do
        local c = rowstr:sub(cc, cc)
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
  -- Pending resolve options (VOFFERS is the richer list when the MUD sends one)
  local resolves = {}
  local optsrc = (gv("VOFFERS") ~= "") and gv("VOFFERS") or gv("VRESOLVE")
  for a in (optsrc or ""):gmatch("[^,]+") do
    local t = a:gsub("^%s+", ""):gsub("%s+$", "")
    if t ~= "" then resolves[#resolves + 1] = t end
  end
  local opts = {}
  for _, t in ipairs(resolves) do
    opts[#opts + 1] = string.format("@{warning,bold,click=vvoyage resolve %s}%s@{}", t, esc(t))
  end
  scrye.setState(P .. "resolveopts", "@{dim}Resolve:@{}\n"
    .. (#opts > 0 and table.concat(opts, "\n") or col("dim", "(none pending)")))
  if #resolves > 0 then
    add(col("warning", "Resolve pending: " .. table.concat(resolves, ", "))
        .. col("dim", "   (click one below, or: vvoyage resolve <opt>)"))
  else
    add(col("dim", "(no resolve pending)"))
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

-- ------------------------------------------------------------ Voyage tab
local function build_voyage()
  local L = {}
  local function add(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
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
  countsec("-- Boons --", "VBOONS")   -- counters (28 Aug); the adapter drops zeros
  add("")
  countsec("-- Aids --", "VAIDS")
  add("")
  countsec("-- Goods --", "VGOODS")
  add("")
  countsec("-- Curios --", "VCURIOS")
  local mem = split(gv("VMEM"), ";")
  if #mem > 0 then
    add("")
    add("-- Crew memory --")
    for _, m in ipairs(mem) do add(m:sub(1, 74)) end
  end
  return table.concat(L, "\n")
end

-- --------------------------------------------------------------- Map tab
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

local function build_map()
  local hd = split(gv("VMAPH"), "|")
  local mw, mh, px, py = num(hd[1]), num(hd[2]), num(hd[3]), num(hd[4])
  if mw == 0 then
    scrye.setState(P .. "maphdr",
      "(waiting for Guild.Map terrain - VERIFY LIVE: the capture never carried it)")
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
    for colx = 1, math.min(#rowstr, mw) do
      local ch = rowstr:sub(colx, colx)
      if not MAP_PAL[ch] then unknown[ch] = true; ch = "?" end
      if mask ~= "" and mask:sub(colx, colx) == "0" then ch = " " end
      out[#out + 1] = ch
      local orig = rowstr:sub(colx, colx)
      if POI_LABEL[orig] then pois[#pois + 1] = { x = colx - 1, y = row, ch = orig } end
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

-- ------------------------------------------------------------ the flush
local BUILDERS = {
  sea    = build_sea,
  voyage = function() scrye.setState(P .. "voyage", build_voyage()) end,
  map    = build_map,
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

-- 1s heartbeat: elapsed-seconds counter (sea-nav command pacing)
scrye.every(1, function() now_s = now_s + 1 end)

-- --------------------------------------------------------------- panel
local icons_on = scrye.store.get("icons") ~= "0"
local build_panel

local function toggle_icons()
  icons_on = not icons_on
  scrye.store.set("icons", icons_on and "1" or "0")
  build_panel()
  scrye.print("[sea] map icons " .. (icons_on and "ON" or "OFF") .. " (sicons toggles)")
end

build_panel = function()
scrye.addPanel{
  title = "Viking Sea",
  width = 560,
  accent = "#3991B7",          -- signature: sea blue (validated accent set)
  tabs = {
    { title = "Sea", widgets = {
        { type = "value", text = "", bind = P .. "seanav", color = "#3991B7" },
        { type = "text", bind = P .. "sea" },
        { type = "row", widgets = {
          { type = "colorgrid", bind = P .. "seachart", palette = SEA_PAL,
            icons = icons_on and SEA_ICONS or nil, labels = SEALETTERS, cell = 24,
            onClick = function(cl, rw, ch)
              if ch == nil or ch == "" or ch == " " then return end
              local coord = sea_coord(cl, rw)
              scrye.send("vvoyage queue " .. coord)
              scrye.print("[sea] queued course to " .. coord
                .. (SEA_NAME[ch] and ("  (" .. SEA_NAME[ch] .. ")") or ""))
            end },
          { type = "text", bind = P .. "resolveopts" },
        } },
        { type = "colorgrid", bind = P .. "sealegend", palette = SEA_PAL,
          icons = icons_on and SEA_ICONS or nil, cell = 24 },
        { type = "colorgrid", bind = P .. "sealegend", palette = SEA_PAL,
          labels = SEA_LEGEND, cell = 24 },
        { type = "label", color = "dim",
          text = "S ship  X objective  H harbor  W wreck  T storm  I island  F fog  # unrevealed  O open sea  * resolved  B stormbelt  + path  > destination  = current  ^ deadwater" },
        { type = "button", text = "Clear voyage queue", action = function() scrye.send("vvoyage clear") end },
        { type = "button", text = "Icons on/off", action = function() toggle_icons() end },
    } },
    { title = "Voyage", widgets = {
        { type = "text", bind = P .. "voyage" },
    } },
    { title = "Map", widgets = {
        { type = "value", text = "", bind = P .. "maphdr", color = "#3991B7" },
        { type = "colorgrid", bind = P .. "map", palette = MAP_PAL,
          icons = icons_on and MAP_ICONS or nil,
          onClick = function(colx, row, ch)
            local key = colx .. "|" .. row
            local code = travel_code(key)
            if code then
              ask_travel(town_label(code))
            else
              local name = locname(colx, row)
              if name then
                scrye.print(string.format("[sea] %s (%d,%d) - no travel route", name, colx, row))
              else
                scrye.print(string.format("[sea] map (%d,%d) '%s' - nothing to travel to", colx, row, ch))
              end
            end
          end },
        { type = "label", text = "grey tundra  yellow hills  red mtn/capital  green forest/plains  blue water  dark road  orange lin  gold settlement  white you  black unexplored" },
        { type = "label", text = "Click a town to travel there:", color = "dim" },
        { type = "text",  bind = P .. "towns" },
        { type = "text",  bind = P .. "maplocs" },
        { type = "button", text = "Icons on/off", action = function() toggle_icons() end },
    } },
    { title = "Travel", widgets = {
        { type = "label", text = "Walk to a settlement (the travel engine lives in 3s-viking-status-gmcp):" },
        { type = "text",  bind = P .. "towns" },
        { type = "label", text = "Walks from the wrong place? Set where you are:  vhere <town>" },
    } },
  },
}
end
build_panel()

scrye.setState(P .. "sealegend", SEA_LEGEND)   -- the legend strip (static)

-- --------------------------------------------------------------- aliases
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
    scrye.print("[sea] location (" .. x .. "," .. y .. "): " .. (name == "" and "(cleared)" or name))
    rebuild_travel_towns()
    publish_town_list()
    publish_locnames()
    dirty.map = true
    schedule_flush()
  end,
}

-- 'sicons' rather than 'vicons': the first-registered alias wins a contested
-- pattern, and 3s-viking-status-gmcp already answers vicons for its Plan grid
scrye.addAlias{ pattern = [[^sicons$]], regex = true, run = function() toggle_icons() end }

-- ================================================================================
-- The GMCP feed layer: page assembler + adapters (same shape as the sibling
-- plugins; the string formats are a private contract with the parsers above).
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
      pcall(on_snap, snap)
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
        local keep = {}
        for k, v in pairs(snap) do if not paged_keys[k] then keep[k] = v end end
        snap = keep
      end
      for k, v in pairs(burst) do snap[k] = v; paged_keys[k] = true end
      burst, bfull, expect, last_page = nil, false, nil, 0
      pcall(on_snap, snap)
    end
  end)
end

local function S(x) return x == nil and "" or tostring(x) end
local function T(t, k) return type(t[k]) == "table" and t[k] or {} end

-- object {name: count} -> "name:count,name:count" (sorted, for stable display)
local function counts(o)
  if type(o) ~= "table" then return "" end
  local ks = {}
  for k in pairs(o) do ks[#ks + 1] = k end
  table.sort(ks)
  local out = {}
  for _, k in ipairs(ks) do out[#out + 1] = k .. ":" .. S(o[k]) end
  return table.concat(out, ",")
end

-- list of names (strings or {trait=}/{name=} records) -> "a, b, c"
local function names(list)
  local out = {}
  for _, e in ipairs(type(list) == "table" and list or {}) do
    if type(e) == "table" then out[#out + 1] = S(e.trait ~= nil and e.trait or (e.name ~= nil and e.name or e.id))
    else out[#out + 1] = S(e) end
  end
  return table.concat(out, ", ")
end

gasm("Guild.Voyage", function(t)
  local vy = T(t, "voyage")
  if vy.state == nil or S(vy.state) == "" then
    vset("voyage", "")            -- no voyage under way (voyage:{} clears it)
  else
    local st, ct = T(t, "voyage_ship_traits"), T(t, "voyage_crew_traits")
    local t1 = st[1] and S(st[1].trait or st[1]) or ""
    local t2 = ct[1] and S(ct[1].trait or ct[1]) or ""
    -- positional contract with build_sea/sea_nav_tick (see their reads):
    -- 1 state, 3 ship, 4 contract, 5 type, 6 danger, 7 x, 8 y, 9 weather_key,
    -- 10 ship_id, 11-14 hull/morale/supplies/stress, 15/16 crew, 17 steps,
    -- 18 next_move_in, 19-21 threat name/level/pressure, 25 captain,
    -- 26 identity, 27/28 traits
    vset("voyage", table.concat({
      S(vy.state), "", S(vy.ship_name), S(vy.contract_name), S(vy.contract_type),
      S(vy.danger), S(vy.x), S(vy.y), S(vy.weather_key), S(vy.ship_id),
      S(vy.hull), S(vy.morale), S(vy.supplies), S(vy.hull_stress),
      S(vy.crew_alive), S(vy.crew_max), S(vy.steps_sailed), S(vy.next_move_in),
      S(vy.threat_name), S(vy.threat_level), S(vy.threat_pressure),
      S(vy.paused_type), "", "", S(vy.captain_style), S(vy.ship_identity),
      t1, t2,
    }, "|"))
    vset("weather", vy.weather_key)
  end
  local ch = T(t, "voyage_chart")
  vset("vchh", (tonumber(ch.width) or 0) > 0
    and (S(ch.width) .. "|" .. S(ch.height) .. "|" .. S(ch.chart_mode)) or "")
  -- chart rows: VERIFY LIVE (voyage_chart_rows was empty for the whole capture)
  local rows = T(t, "voyage_chart_rows")
  for i = 0, 31 do
    vset(string.format("vcr%02d", i), rows[i + 1])
  end
  -- resolve / offers / queued path / saga
  do
    local out = {}
    for _, e in ipairs(T(t, "vresolve")) do out[#out + 1] = S(e):gsub(",", " ") end
    vset("vresolve", table.concat(out, ","))
  end
  do
    local out = {}
    for _, e in ipairs(T(t, "voffers")) do out[#out + 1] = S(e):gsub(",", " ") end
    vset("voffers", table.concat(out, ","))
  end
  do
    -- CONFIRMED live 23:09 28 Aug: entries are "x,y" strings (["10,1"])
    local out = {}
    for _, e in ipairs(T(t, "vqpath")) do
      if type(e) == "table" then out[#out + 1] = S(e.x) .. "," .. S(e.y)
      else out[#out + 1] = S(e) end
    end
    vset("vqpath", table.concat(out, " "))
  end
  do
    local out = {}
    for _, e in ipairs(T(t, "vsaga")) do out[#out + 1] = S(e):gsub(";", ",") end
    vset("vsaga", table.concat(out, ";"))
  end
  do
    -- vmem (28 Aug capture): the crew's memories, full sentences
    local out = {}
    for _, e in ipairs(T(t, "vmem")) do out[#out + 1] = S(e):gsub(";", ",") end
    vset("vmem", table.concat(out, ";"))
  end
  -- vboons (28 Aug capture): an OBJECT of counters ({rigging_bonus:0, ...}).
  -- Only non-zero boons are worth a line; all-zero means none active -> "none".
  do
    local b = T(t, "vboons")
    local ks = {}
    for k in pairs(b) do ks[#ks + 1] = k end
    table.sort(ks)
    local out = {}
    for _, k in ipairs(ks) do
      if (tonumber(b[k]) or 0) ~= 0 then out[#out + 1] = k .. ":" .. S(b[k]) end
    end
    vset("vboons", table.concat(out, ","))
  end
  vset("vaids", counts(t.vaids))
  vset("vgoods", counts(t.vgoods))
  vset("vcurios", names(t.vcurios))
end)

gasm("Guild.Fleet", function(t)
  -- same contract as the sibling plugins: f1=name f3=state f4=target f5=secs
  local out = {}
  for _, e in ipairs(T(t, "ships")) do
    out[#out + 1] = table.concat({ S(e.name):gsub("[|;]", " "), S(e.tier),
      S(e.state), S(e.target):gsub("[|;]", " "), S(e.secs) }, "|")
  end
  vset("ships", table.concat(out, ";"))
end)

gasm("Guild.Settlement", function(t)
  local sp = T(t, "shplots")
  local ks = {}
  for k in pairs(sp) do ks[#ks + 1] = k end
  table.sort(ks)
  local out = {}
  for _, k in ipairs(ks) do out[#out + 1] = k .. " " .. S(sp[k]) end
  vset("shplots", table.concat(out, "|"))
end)

gasm("Guild.Map", function(t)
  -- VERIFY LIVE: terrain rows never appeared in the capture (only east/south edge
  -- grids and pos); enc={terrain:"glyph"} says they exist. Until a burst carries
  -- them the Map tab keeps its honest waiting line.
  local rows = T(t, "terrain")
  local pos = T(t, "pos")
  if rows[1] then
    local w = 0
    for i, r in ipairs(rows) do
      if #S(r) > w then w = #S(r) end
      vset(string.format("vmr%02d", i - 1), r)
    end
    vset("vmaph", w .. "|" .. #rows .. "|" .. S(pos.x) .. "|" .. S(pos.y))
  end
  -- (an unpaged pos-only message - the capture's common Guild.Map shape - merges
  -- into the snapshot, so the branch above re-renders with the retained terrain;
  -- no separate pos-only path is needed)
end)

-- ------------------------------------------------------------------ init
update_seanav_state()
dirty.sea, dirty.voyage, dirty.map = true, true, true
flush()
