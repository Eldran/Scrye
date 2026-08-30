-- 3S Viking World -- everywhere that is not your own settlement: Sea / Voyage /
-- Map / Mission / Plan / Travel. It owns getting places: the Guild.Map edge grids
-- as a graph, BFS over them, the recorded routes kept as the oracle that vets it,
-- vgo/vhere, and the mission runner (which walks, and so belongs with the walking).
-- The Plan grid is drawn here beside the other maps but computed by
-- 3s-viking-status-gmcp, which owns the settlement feed behind it. Carved out of
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
-- needs it). This plugin owns the vgo/vhere aliases and the travel engine
-- behind them: text clicks run "vgo <town>" through the
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
-- the settlement plugin's prefix: state paths are global, so a tab here can
-- show a picture that plugin computes without either owning the other
local SP = "plugin.3s-viking-status-gmcp."

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
-- (TOWN_COORD is built above, from every named coordinate rather than only the
-- towns with recorded routes)

-- ------------------------------------------------- the guild map as a GRAPH
-- Guild.Map ships two EDGE grids the plugins used to throw away: `east[y]` is a
-- row of w-1 flags ("1" = you may step from x to x+1 along row y) and `south[y]`
-- a row of w ("1" = you may step from (x,y) down to (x,y+1)). Together they are
-- the whole territory as a graph -- which is what the 28 hand-recorded ROUTES
-- above always were: paths through it, written down once by hand.
--
-- Verified against the 29 Aug capture before any of this was written: replaying
-- all 28 ROUTES as coordinate arithmetic lands EXACTLY on each destination
-- town's coords, and of the 540 edge lookups the capture can answer, 540 say
-- passable and none say blocked. So the grids are the routes' own map, and '1'
-- is open. What BFS adds is every route nobody recorded: today any A->B is
-- A->Midgard->B, which across the 182 town pairs wastes 41% of the walking, and
-- sends Nidaros->Lodbrok's Hold 64 steps between towns that are 8 apart.
local ST = scrye.shared or scrye.store   -- world truth: one map for every character

local gmap = { east = nil, south = nil, w = 0, h = 0, pos = nil, trusted = nil }

local function gmap_rows(s)
  if s == "" then return nil end
  local out = {}
  for row in (s .. "\n"):gmatch("([^\n]*)\n") do out[#out + 1] = row end
  return out[1] and out or nil
end

-- restore last session's grids so a route can be planned before the first burst
gmap.east  = gmap_rows(ST.get("gmap_east") or "")
gmap.south = gmap_rows(ST.get("gmap_south") or "")
do
  local wh = split(ST.get("gmap_wh") or "", "|")
  gmap.w, gmap.h = num(wh[1]), num(wh[2])
end

-- Coordinates are 0-based, x east and y south (the ROUTES replay proves it); the
-- Lua arrays are 1-based, hence every +1. An edge lookup off the end of the grid
-- is "not passable" rather than an error: a half-arrived grid must refuse to
-- route, never guess.
local function passable(x, y, dir)
  local e, s = gmap.east, gmap.south
  if not (e and s) then return false end
  local r
  if dir == "e" then r = e[y + 1]; return r ~= nil and r:sub(x + 1, x + 1) == "1"
  elseif dir == "w" then r = e[y + 1]; return r ~= nil and x >= 1 and r:sub(x, x) == "1"
  elseif dir == "s" then r = s[y + 1]; return r ~= nil and r:sub(x + 1, x + 1) == "1"
  elseif dir == "n" then r = s[y];     return r ~= nil and y >= 1 and r:sub(x + 1, x + 1) == "1"
  end
  return false
end

local STEP = { n = { 0, -1 }, s = { 0, 1 }, e = { 1, 0 }, w = { -1, 0 } }
local STEP_ORDER = { "n", "s", "e", "w" }   -- fixed, so a route is reproducible
local BFS_CELLS = 4000                      -- 70x35 = 2450; the cap is a guard, not a limit

-- Shortest walk between two map cells as a list of directions, or nil when the
-- grids cannot answer (absent, or genuinely no path).
local function map_path(sx, sy, dx, dy)
  if not (gmap.east and gmap.south) then return nil end
  if sx == dx and sy == dy then return {} end
  local function key(x, y) return y * 1000 + x end
  local prev, seen = {}, { [key(sx, sy)] = true }
  local q, head = { { sx, sy } }, 1
  while head <= #q do
    if head > BFS_CELLS then return nil end
    local cur = q[head]; head = head + 1
    local x, y = cur[1], cur[2]
    for _, d in ipairs(STEP_ORDER) do
      local st = STEP[d]
      local nx, ny = x + st[1], y + st[2]
      if nx >= 0 and ny >= 0 and (gmap.w == 0 or nx < gmap.w) and (gmap.h == 0 or ny < gmap.h)
         and not seen[key(nx, ny)] and passable(x, y, d) then
        seen[key(nx, ny)] = true
        prev[key(nx, ny)] = { x = x, y = y, dir = d }
        if nx == dx and ny == dy then
          local steps, cx, cy = {}, nx, ny
          while not (cx == sx and cy == sy) do
            local p = prev[key(cx, cy)]
            table.insert(steps, 1, p.dir)
            cx, cy = p.x, p.y
          end
          return steps
        end
        q[#q + 1] = { nx, ny }
      end
    end
  end
  return nil
end

local function town_xy(code)
  local coord = TOWN_COORD[code]
  if not coord then return nil end
  local x, y = coord:match("^(%-?%d+)|(%-?%d+)$")
  return tonumber(x), tonumber(y)
end

-- THE ORACLE. The hand-recorded ROUTES are kept precisely so the feed's grids can
-- be checked against something known-good: replay every one as coordinate
-- arithmetic, and each must cross only passable edges and finish on its
-- destination town. Grids that fail are grids we do not understand -- a server
-- change, a paging bug, a flipped convention -- and travel falls back to the
-- table, so the worst case is exactly the behaviour this plugin had before.
local function grid_trustworthy()
  if gmap.trusted ~= nil then return gmap.trusted end
  if not (gmap.east and gmap.south) then return false end
  local checked = 0
  for name, cmds in pairs(ROUTES) do
    local src, dst
    if name:sub(1, 3) == "Mid" then src, dst = "Mid", name:sub(4)
    else src, dst = name:sub(1, #name - 3), "Mid" end
    local x, y = town_xy(src)
    local tx, ty = town_xy(dst)
    if x and tx then
      for _, d in ipairs(cmds) do
        if STEP[d] then
          if not passable(x, y, d) then gmap.trusted = false; return false end
          x, y = x + STEP[d][1], y + STEP[d][2]
          checked = checked + 1
        end
      end
      if x ~= tx or y ~= ty then gmap.trusted = false; return false end
    end
  end
  gmap.trusted = checked > 0
  return gmap.trusted
end

-- A walk between two settlements, straight across the map. leave/enter wrap the
-- grid path exactly as the recorded routes do: you step out of a town onto its
-- own cell and into the destination on its cell. Blot is the exception the table
-- already knew about -- MidBlot ends without `enter`, BlotMid starts without
-- `leave` -- so the wrapper asks the table rather than assuming.
local ROUTE_LEAVES, ROUTE_ENTERS = {}, {}
for name, cmds in pairs(ROUTES) do
  local src, dst
  if name:sub(1, 3) == "Mid" then src, dst = "Mid", name:sub(4)
  else src, dst = name:sub(1, #name - 3), "Mid" end
  ROUTE_LEAVES[src] = (cmds[1] == "leave")
  ROUTE_ENTERS[dst] = (cmds[#cmds] == "enter")
end
-- a site the table never mentions defaults to wrapped: towns you step out of and
-- into are the rule, and Blot -- the one open-air site -- is why this is a lookup
local function wraps(tbl, code)
  local v = tbl[code]
  if v == nil then return true end
  return v
end

local function map_route(origin, dest)
  if not grid_trustworthy() then return nil end
  local sx, sy = town_xy(origin)
  local dx, dy = town_xy(dest)
  if not (sx and dx) then return nil end
  local steps = map_path(sx, sy, dx, dy)
  if not steps then return nil end
  local out = {}
  if wraps(ROUTE_LEAVES, origin) then out[#out + 1] = "leave" end
  for _, d in ipairs(steps) do out[#out + 1] = d end
  if wraps(ROUTE_ENTERS, dest) then out[#out + 1] = "enter" end
  return out
end

-- ------------------------------------------------------ pos, and earning trust
-- Guild.Map also carries `pos`, which reads like "the cell you are standing on"
-- and would make `vhere` unnecessary. It is NOT believed on sight: pos read
-- (49,17) in all three captures, taken on three different days from different
-- places, so it may well be a view centre or a territory anchor rather than the
-- player. The known towns are the examiners. Every time pos arrives while we
-- believe we are in a town whose coords we already know, it either agrees (a
-- point towards "pos is live") or it does not (a point against). Only a pos that
-- has earned agreement is allowed to correct a coordinate, and a pos that has
-- disagreed is written off for the session. Nothing about travel changes until
-- the evidence arrives, which is the point: the mechanism switches itself on the
-- day the data proves out, and stays inert if it never does.
local POS_TRUST_AT = 3          -- agreements before pos may correct anything
local pos_agree, pos_disagree = 0, 0
local pos_verdict = ""          -- "", "live" or "not the player" once decided

local function gmap_pos_state()
  if pos_verdict ~= "" then return pos_verdict end
  return string.format("watching (%d agree / %d disagree)", pos_agree, pos_disagree)
end

local function gmap_note_pos(x, y)
  if not (x and y) then return end
  gmap.pos = { x = x, y = y }
  local code = scrye.store.get("curtown")
  if code == "" then return end
  local tx, ty = town_xy(code)
  if not tx then return end                  -- a site we have no coords for: nothing to check
  if x == tx and y == ty then
    pos_agree = pos_agree + 1
    if pos_verdict == "" and pos_agree >= POS_TRUST_AT then
      pos_verdict = "live"
      scrye.print("[viking] map position confirmed live - town coordinates will self-correct from now on")
    end
  else
    pos_disagree = pos_disagree + 1
    if pos_verdict == "" and pos_disagree >= POS_TRUST_AT then
      pos_verdict = "not the player"
      scrye.print(string.format("[viking] Guild.Map pos does not track you (reads %d,%d at %s) - "
        .. "coordinates stay as recorded", x, y, town_label(code)))
    elseif pos_verdict == "live" then
      -- trusted pos against a coord we thought we knew: the map moved, not the feed
      TOWN_COORD[code] = x .. "|" .. y
      ST.set("gmap_coords", (ST.get("gmap_coords") or "") .. code .. "=" .. x .. "|" .. y .. ";")
      gmap.trusted = nil       -- the oracle must re-examine a map that changed
      scrye.print(string.format("[viking] %s has moved to %d,%d - routes recomputed", town_label(code), x, y))
    end
  end
end

-- coords corrected in an earlier session, replayed over the recorded table
for pair in (ST.get("gmap_coords") or ""):gmatch("([^;]+)") do
  local code, xy = pair:match("^(%w+)=(%-?%d+|%-?%d+)$")
  if code and xy then TOWN_COORD[code] = xy end
end

-- Where the map feed thinks we are. This used to read the terrain header from
-- the other plugin and so could never answer; the feed lives here now, and the
-- position comes from Guild.Map's `pos` directly.
--
-- It stays SILENT until pos has earned trust. An untrusted pos naming the wrong
-- origin is worse than no answer at all: travel would plan a perfectly good
-- route from a town you are not standing in. Until the known towns have vouched
-- for it (see the pos gate above) the remembered curtown carries travel, exactly
-- as it did before.
local function live_town()
  if pos_verdict ~= "live" then return nil end
  local pt = gmap.pos
  if not pt then return nil end
  local px, py = pt.x, pt.y
  if not (px and py) then return nil end
  local function near(tbl)
    for k, code in pairs(tbl) do
      local tx, ty = k:match("^(%-?%d+)|(%-?%d+)$")
      if math.abs(px - tonumber(tx)) <= 2 and math.abs(py - tonumber(ty)) <= 2 then return code end
    end
  end
  return near(TRAVEL_CODE) or near(SPECIAL_TRAVEL)
end

-- How we got there, for the travel line: the grid route when the feed's map is
-- present and passes the oracle, otherwise the recorded table. Set by send_route.
local last_route_by = "table"

local function send_route(origin, dest)
  local cmds = map_route(origin, dest)
  if cmds then
    last_route_by = "map"
  else
    -- the recorded table: every pair chained through Midgard, which is why the
    -- grid route is usually much shorter
    last_route_by = "table"
    cmds = {}
    if origin ~= "Mid" then
      local leg = ROUTES[origin .. "Mid"]; if not leg then return false end
      for _, c in ipairs(leg) do cmds[#cmds + 1] = c end
    end
    if dest ~= "Mid" then
      local leg = ROUTES["Mid" .. dest]; if not leg then return false end
      for _, c in ipairs(leg) do cmds[#cmds + 1] = c end
    end
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
  scrye.print(string.format("[viking] travelling %s -> %s (%d steps, %s)",
    town_label(origin), town_label(dest), n,
    last_route_by == "map" and "map" or "recorded route"))
  scrye.store.set("curtown", dest)
  return n
end

-- travelable towns, sorted by display name (for the Travel tab buttons + vhere matching)
local function resolve_town(s)
  s = (s or ""):gsub("^%s+", ""):gsub("%s+$", ""):lower()
  for _, code in ipairs(TRAVEL_TOWNS) do
    if code:lower() == s then return code end
    local lbl = town_label(code):lower()
    if lbl == s or lbl:find(s, 1, true) then return code end
  end
  return nil
end

-- (the Travel tab's clickable town list and the Map tab moved to 3s-viking-world;
-- its clicks run `vgo <town>`, the alias THIS plugin owns)

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

-- ------------------------------------------------------- mission running
-- The mission runner lives here because it WALKS: it calls travel_to for every
-- leg and needs the walk length back, which an event hop cannot carry. The
-- missions themselves belong to the settlement feed, so 3s-viking-status-gmcp
-- publishes the raw strings and this reads them across -- state paths are global.

-- The MISSIONS feed as a list we can act on, not just print:
--   id | desc | rep | ? | expiry | (empty) | town | goods(good:qty,...)
-- `code` is the town resolved to a travel code (Mid/Hol/Lod/...), or nil when the town
-- is not one we have a route for -- which is what makes a mission runnable or not.
local function parse_missions()
  local out = {}
  local raw = scrye.getState(SP .. "missions_raw") or ""
  if raw == "" then return out end
  for _, ms in ipairs(split(raw, ";")) do
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
  local er = scrye.getState(SP .. "errand_raw") or ""
  if er == "" then add("no errand")
  else
    local e = split(er, "|")
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

-- The missions arrive in the OTHER plugin and are published as state; nothing in
-- this plugin's own feed changes when they do, so its dirty/flush cycle never
-- hears about it. Watch the paths instead -- the same cross-plugin mechanism
-- 3s-raid uses to follow the Viking feed. (Learned the hard way: the tab was
-- built once at load and then sat empty for good, because the load-time publish
-- ran BEFORE the first Guild.Trade burst ever landed.)
scrye.watch(SP .. "missions_raw", function() publish_mission() end)
scrye.watch(SP .. "errand_raw",   function() publish_mission() end)

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

-- the Map tab asks for walks over the event bus (its colorgrid click
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

-- --------------------------------------------------------------- panel
local icons_on = scrye.store.get("icons") ~= "0"
local build_panel

local function toggle_icons()
  icons_on = not icons_on
  scrye.store.set("icons", icons_on and "1" or "0")
  build_panel()
  scrye.print("[world] icons " .. (icons_on and "ON" or "OFF") .. " (sicons / vicons toggle)")
end

-- ---------------------------------------------------------- the Plan grid
-- The Plan TAB lives here with the other grids; the plan itself is still
-- computed by 3s-viking-status-gmcp, which owns the Guild.City feed it comes
-- from and the `vplan` command that edits it. Widget binds read the global
-- state store, so the tab simply binds across to that plugin's paths -- the
-- picture moves without the machinery behind it having to.
-- The tiles/ folder had to come along: image paths resolve relative to the
-- DECLARING plugin's folder and are sandboxed to it, so art left behind would
-- silently render as plain tiles.

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

-- Image tiles (host API 1.17): hand-drawn art per Plan character, living in the
-- plugin's own tiles/ folder and riding the same Icons on/off toggle as the vector
-- glyphs (an imaged character beats its glyph; the rest keep their glyphs). A named
-- file that does not exist yet is harmless - the cell falls back to glyph/tile - so
-- this table can grow ahead of the art: draw a PNG, drop it in tiles/, done. On a
-- pre-1.17 host the field is ignored and the grid renders exactly as before.
local PLAN_IMAGES = {
  ["7"] = "tiles/tower.png",   -- throne district - the first hand-drawn tile
}

build_panel = function()
scrye.addPanel{
  title = "Viking World",
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
    { title = "Plan", widgets = {
        { type = "value", text = "", bind = SP .. "planhdr", color = "#6288E1" },
        { type = "colorgrid", bind = SP .. "plangrid", palette = PLAN_PAL,
          icons = icons_on and PLAN_ICONS or nil, images = icons_on and PLAN_IMAGES or nil },
        { type = "text", bind = SP .. "planlist" },
        { type = "label", text = "edit the plan with  vplan  (3S Viking Status owns it)", color = "dim" },
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

-- One toggle for every grid this plugin draws - sea chart, world map and now the
-- Plan. `vicons` used to be the settlement plugin's, and came here with the Plan
-- tab; `sicons` stays as the name it has answered to since the split.
scrye.addAlias{ pattern = [[^sicons$]], regex = true, run = function() toggle_icons() end }
scrye.addAlias{ pattern = [[^vicons$]], regex = true, run = function() toggle_icons() end }

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

  -- The same package carries the territory's PASSABILITY, which is what travel is
  -- planned from. The two edge grids arrive in SEPARATE full=1 bursts (east in one,
  -- south in the next), and a full burst clears the paged keys of the one before it
  -- -- so the snapshot never holds both at once and each is latched as it lands.
  local changed = false
  local function rows_of(v)
    if type(v) ~= "table" or not v[1] then return nil end
    local out = {}
    for i, r in ipairs(v) do out[i] = S(r) end
    return out
  end
  local e, s2 = rows_of(t.east), rows_of(t.south)
  if e then gmap.east = e; ST.set("gmap_east", table.concat(e, "\n")); changed = true end
  if s2 then gmap.south = s2; ST.set("gmap_south", table.concat(s2, "\n")); changed = true end
  local gw, gh = tonumber(t.w), tonumber(t.h)
  if gw and gw > 0 then gmap.w = gw; changed = true end
  if gh and gh > 0 then gmap.h = gh; changed = true end
  if changed then
    gmap.trusted = nil                                  -- a new map re-faces the oracle
    ST.set("gmap_wh", S(gmap.w) .. "|" .. S(gmap.h))
  end
  if pos.x ~= nil and pos.y ~= nil then
    gmap_note_pos(tonumber(pos.x), tonumber(pos.y))
  end
end)

-- ------------------------------------------------------------------ init
update_seanav_state()
dirty.sea, dirty.voyage, dirty.map = true, true, true
flush()
