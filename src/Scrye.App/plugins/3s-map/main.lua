-- ============================================================
-- 3S Map — automapper for 3Scapes. Milestones M1 + M2.
-- Design: docs/Scrye-Map-Design.md. Requires plugin API 1.7 (weave grid).
--
-- M1 (walk-and-map core): watches every command that goes to the MUD via
-- scrye.onCommand (typed, macro, sequence, OTHER PLUGINS — the stepper's
-- moves map exactly like yours), queues the movement words, and confirms
-- one per =S= room marker, dead-reckoning a per-area 3D grid. Rooms
-- persist per area as JSON via scrye.json + store.setMany.
--
-- M2 (see it): a HUD panel — a 21x15-room colorgrid viewport centered on
-- you (north = up), theme-token palette, flag letters on tiles, a
-- hover/click peek line, Up/Down/Center/Stop buttons, room notes and
-- flags, and a Rooms tab with a search box + numbered table. Since API
-- 1.7 the grid is a WEAVE: rooms sit on even cells, and the exits between
-- them draw as thin connector lines on the odd cells ('-', '|', '/', '\',
-- 'x' where two diagonals cross). Unmapped room positions show a faint
-- grid dot, and a room with up/down exits is marked '^'/'v'/'%' on its
-- tile — so which exits a room has is visible at a glance.
--
-- M3 (walk it): 'map goto x y [z]', 'map go <n>' (a numbered Rooms row),
-- clicking a mapped cell, all BFS through known exits and walk there ONE
-- CONFIRMED STEP AT A TIME: send a direction, wait for its =S=, send the
-- next. Combat (enemy.name, the MIP truth) pauses the walk and it resumes
-- when clear; a refused move, a 10s silence (watchdog), 'map stop', the
-- Stop button or the idle guard abort it — the idle guard one stays
-- stopped until YOU ask again, per the client's idle contract.
--
-- M4 (trust it): a DRIFT? check — a confirmed move landing on a room whose
-- recorded name disagrees with the MUD's warns and PRESERVES the record;
-- 'map undo' forgets the last learned room; special links ('map link
-- enter well' arms, next arrival binds; 'map link <cmd> = [area] x y z'
-- binds explicitly) that BFS walks through; cross-area links that switch
-- areas on use; maps.json seeding (your own maps always win); and events
-- other plugins can scrye.on: 'map.room' per arrival, 'map.walk.started'
-- / 'map.walk.stopped'.
--
-- M6 (stitch the world): 3Scapes is Pinnacle -> three hubs (chaos,
-- fantasy, science) -> areas, and the map is ONE AREA PER MAP, stitched
-- at the boundaries. A room can record a link on a plain COMPASS
-- direction ('map link n = fantasy 0 0 0') — onCommand checks the
-- current room's links BEFORE compass dead reckoning, so walking north
-- through a recorded boundary switches maps instead of reckoning a
-- phantom room. A compass crossing records its own RETURN link on first
-- use (n out means s back — knowable for compass, never assumed for
-- portals). 'map enter <area> [x y z]' arms the next command you send as
-- the boundary into <area> (created if new), binding it both ways when
-- it was a compass move; 'map back <cmd>' on the arrival side binds the
-- way home of a PORTAL boundary (whose return is never guessable) to the
-- room you just came from, no coordinates needed. Linked compass exits
-- stop drawing a frontier '?' — the other side lives on another map, not
-- on this one.
--
-- Rules this file lives by (see the guide's gotchas): locals are
-- initialized; Lua patterns are simple and anchored; the onCommand
-- handler NEVER sends — the walk engine sends, arrivals drive it.
-- ============================================================

local P = "plugin." .. scrye.id .. "."

local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end
local function note(s) scrye.print("[map] " .. s) end

-- ---------- directions ----------
-- North = +y (up on the map), east = +x, up = +z. Same frame as the
-- chaos-sea mapper so anyone reading both isn't translating in their head.
local DELTA = {
  n  = { 0,  1, 0 }, s  = { 0, -1, 0 }, e  = { 1, 0, 0 }, w  = { -1, 0, 0 },
  ne = { 1,  1, 0 }, nw = { -1, 1, 0 }, se = { 1, -1, 0 }, sw = { -1, -1, 0 },
  u  = { 0, 0, 1 },  d  = { 0, 0, -1 },
}
-- every word we treat as a move, mapped to its canonical short form
local CANON = {
  north = "n", south = "s", east = "e", west = "w",
  northeast = "ne", northwest = "nw", southeast = "se", southwest = "sw",
  up = "u", down = "d",
}
for k in pairs(DELTA) do CANON[k] = k end

-- the way back (M6): a compass crossing into another area records its return
-- link automatically — 'n' out means 's' back. Only compass moves get this;
-- a portal's return is never guessable.
local OPP = { n = "s", s = "n", e = "w", w = "e",
              ne = "sw", sw = "ne", nw = "se", se = "nw", u = "d", d = "u" }

-- More queued moves than this means we lost the plot (or someone is
-- speedwalking blind); dead reckoning on a stale queue maps garbage, so drop it.
local MAX_PENDING = 20

-- viewport (chaos-sea precedent: fixed, centered on the player). COLS x ROWS
-- is the ROOM viewport; the woven grid string doubles it minus one — rooms on
-- even (0-based) cells, the connectors between them on the odd cells.
local COLS, ROWS = 21, 15
local HALF_C, HALF_R = COLS // 2, ROWS // 2
local TCOLS, TROWS = COLS * 2 - 1, ROWS * 2 - 1

-- ---------- state ----------
local enabled = true          -- capture on/off ('map on'/'map off', persisted)
local held_by = nil           -- transient hold via the "map.hold" event (never persisted):
                              -- another plugin owns movement in unmappable space right now
local area = "default"        -- current area name (persisted)

-- Which of 3Scapes' three realms this area belongs to (nil = unknown / Pinnacle).
-- The colours are 3scapes.org's own realm palette; the map panel's accent wears the
-- current realm's colour, so a glance at the border says which world you're in.
-- Persisted in the area blob; the hub areas self-identify by name, and crossing into
-- an unrealmed area from a realmed one passes the realm along (lineage, not magic).
local REALM_COLOR = { fantasy = "#A070FF", science = "#38D0FF", chaos = "#FF4FA0" }
local realm = nil
local build_panel, refresh_accent   -- defined with the panel, used by the crossings
local rooms = {}              -- rooms[z][x][y] = { name="", exits={}, flag="", note="" }
local room_count = 0
local pos = { x = 0, y = 0, z = 0 }
local pending = {}            -- FIFO of canonical dirs awaiting an =S= confirmation
local dirty = false           -- unsaved changes
local map_serial = 1          -- bumped on every map change / area switch; the pathfinder
                              -- plugin caches the graph keyed on this (see ask_pathfinder)
local flush_timer = nil       -- debounce timer id (nil = none scheduled)
local seen_s = false          -- ever seen an =S= marker (status hint when not)
local view_z = nil            -- nil = follow the player's level; a number = inspecting that z
local draw_x0 = 0             -- world coords of the viewport's top-left cell at the LAST draw,
local draw_y0 = 0             -- so hover/click can map (col,row) back to a room
local draw_z = 0

-- the active speedwalk (M3), or nil. dirs holds SEND WORDS — a compass code
-- or a link command; position updates come from the pending queue either way.
--   { dirs = {"n","enter well",...}, idx = next step, awaiting = bool,
--     target = {x,y,z}, combat_noted = bool }
local walk = nil
local walk_watchdog = nil     -- timer id for the no-confirmation abort
local last_list = {}          -- Rooms-tab rows as {x,y,z} targets, for 'map go <n>'

-- M4 state
local drift = false           -- the map and the world disagreed on a name
local undo_stack = {}         -- last ~20 confirmed moves: { to=, from=, created= }
local armed_link = nil        -- 'map link <cmd>' arms; the next send of <cmd> binds

-- M6 state: 'map enter <area> [x y z]' arms; the NEXT command you send becomes
-- the boundary command into that area (created if new), landing at x,y,z.
local armed_enter = nil       -- { area=, x=, y=, z= } or nil

-- where the last cross-area jump came FROM: { area=, x=, y=, z= }. 'map back
-- <cmd>' binds <cmd> in the arrival room to this — the way to record a
-- portal boundary's return without ever typing coordinates. Compass
-- crossings never need it (their return records itself). Cleared by
-- load_area and re-set by the crossing branches, so a manual 'map area'
-- switch can't leave a stale one behind.
local last_cross = nil

-- maps.json seeds (manifest data, M4): name -> area table. An area you have
-- mapped yourself (a map:<name> store key) always wins over its seed.
local SEEDS = {}
do
  local data = scrye.data and scrye.data.maps
  if type(data) == "table" and type(data.areas) == "table" then
    for _, a in ipairs(data.areas) do
      if type(a) == "table" and type(a.name) == "string" and a.name ~= "" then
        SEEDS[a.name] = a
      end
    end
  end
end

local draw = nil              -- forward decls (defined after the grid helpers)
local refresh_roomlist = nil
local walk_advance = nil
local walk_stop = nil
local start_goto = nil

-- ---------- grid ----------
local function get_room(p)
  local zt = rooms[p.z]; if not zt then return nil end
  local xt = zt[p.x]; if not xt then return nil end
  return xt[p.y]
end

local function add_room(p)
  rooms[p.z] = rooms[p.z] or {}
  rooms[p.z][p.x] = rooms[p.z][p.x] or {}
  local r = rooms[p.z][p.x][p.y]
  if not r then
    r = { name = "", exits = {}, flag = "", note = "", links = {} }
    rooms[p.z][p.x][p.y] = r
    room_count = room_count + 1
    map_serial = map_serial + 1
  end
  return r
end

local function delete_room(p)
  local zt = rooms[p.z]; if not zt then return end
  local xt = zt[p.x]; if not xt then return end
  if xt[p.y] then
    xt[p.y] = nil
    room_count = room_count - 1
    map_serial = map_serial + 1
  end
end

-- Spiral outward on the same level for the first unmapped cell — where an
-- armed link's unknown destination gets parked. 'map set' re-seats you if
-- you know where it really belongs.
local function free_cell_near(p)
  for radius = 1, 40 do
    for dx = -radius, radius do
      for dy = -radius, radius do
        if math.max(math.abs(dx), math.abs(dy)) == radius then
          local c = { x = p.x + dx, y = p.y + dy, z = p.z }
          if not get_room(c) then return c end
        end
      end
    end
  end
  return { x = p.x, y = p.y, z = p.z + 1000 }   -- 6400 mapped neighbours? take the shelf
end

local function moved(p, dir)
  local dv = DELTA[dir]
  if not dv then return nil end
  return { x = p.x + dv[1], y = p.y + dv[2], z = p.z + dv[3] }
end

-- iterate every room: fn(x, y, z, room)
local function each_room(fn)
  for z, zt in pairs(rooms) do
    for x, xt in pairs(zt) do
      for y, r in pairs(xt) do fn(x, y, z, r) end
    end
  end
end

-- ---------- (de)serialization ----------
-- Rooms serialize as a FLAT LIST of records, not as the nested [z][x][y]
-- grid: JSON object keys are strings, so numeric grid keys wouldn't survive
-- a round-trip (t["0"] is not t[0]). The list is also the export format —
-- one shape for store, export, and (M4) maps.json seeding.
local function area_to_table()
  local list = {}
  each_room(function(x, y, z, r)
    local ex = {}
    for dircode in pairs(r.exits) do ex[#ex + 1] = dircode end
    table.sort(ex)
    local rec = { x = x, y = y, z = z, name = r.name, exits = ex }
    if r.flag ~= "" then rec.flag = r.flag end
    if r.note ~= "" then rec.note = r.note end
    if next(r.links) ~= nil then rec.links = r.links end
    list[#list + 1] = rec
  end)
  table.sort(list, function(a, b)
    if a.z ~= b.z then return a.z < b.z end
    if a.x ~= b.x then return a.x < b.x end
    return a.y < b.y
  end)
  return { name = area, realm = realm, rooms = list }
end

local function load_area_table(t)
  rooms = {}
  room_count = 0
  if type(t) ~= "table" or type(t.rooms) ~= "table" then return end
  for _, rec in ipairs(t.rooms) do
    if type(rec) == "table" and rec.x and rec.y and rec.z then
      local r = add_room({ x = rec.x, y = rec.y, z = rec.z })
      r.name = tostring(rec.name or "")
      r.flag = tostring(rec.flag or "")
      r.note = tostring(rec.note or "")
      r.exits = {}
      local ex = rec.exits
      if type(ex) == "table" then
        for _, dircode in ipairs(ex) do
          if DELTA[dircode] then r.exits[dircode] = true end
        end
      end
      r.links = {}
      if type(rec.links) == "table" then
        for cmd, d in pairs(rec.links) do
          if type(cmd) == "string" and type(d) == "table"
             and type(d.x) == "number" and type(d.y) == "number" and type(d.z) == "number" then
            r.links[cmd] = { x = d.x, y = d.y, z = d.z, area = d.area }
          end
        end
      end
    end
  end
end

-- ---------- persistence ----------
-- One setMany per flush: the area blob, the position, the area index and the
-- toggles — one disk write however many keys changed (that is what 1.6's
-- batching is for). Debounced 3s behind the last change; forced on area
-- switch, disconnect and idle.
local function area_index_with(name)
  local idx = scrye.json.decode(scrye.store.get("areas") or "") or {}
  if type(idx) ~= "table" then idx = {} end
  local found = false
  for _, n in ipairs(idx) do
    if n == name then found = true end
  end
  if not found then idx[#idx + 1] = name end
  table.sort(idx)
  return idx
end

local function flush()
  if not dirty then return end
  dirty = false
  local batch = {}
  batch["map:" .. area] = scrye.json.encode(area_to_table())
  batch["pos:" .. area] = scrye.json.encode(pos)
  batch["areas"] = scrye.json.encode(area_index_with(area))
  batch["area"] = area
  batch["enabled"] = enabled and "1" or "0"
  scrye.store.setMany(batch)
end

local function mark_dirty()
  dirty = true
  if flush_timer then return end
  flush_timer = scrye.after(3, function()
    flush_timer = nil
    flush()
  end)
end

local function save_now()
  dirty = true
  flush()
end

local function load_area(name)
  area = name
  map_serial = map_serial + 1
  pending = {}
  undo_stack = {}
  armed_link = nil
  armed_enter = nil
  last_cross = nil
  drift = false
  view_z = nil
  local stored = scrye.json.decode(scrye.store.get("map:" .. name) or "")
  realm = nil
  if type(stored) == "table" and REALM_COLOR[tostring(stored.realm or "")] then
    realm = stored.realm
  elseif REALM_COLOR[name:lower()] then
    realm = name:lower()                       -- the hub areas self-identify by name
  end
  if type(stored) == "table" then
    load_area_table(stored)
  elseif SEEDS[name] then
    load_area_table(SEEDS[name])
    note(string.format("area '%s' seeded from maps.json - %d rooms (yours to extend)",
      esc(name), room_count))
  else
    load_area_table(nil)
  end
  local p = scrye.json.decode(scrye.store.get("pos:" .. name) or "")
  if type(p) == "table" and type(p.x) == "number" and type(p.y) == "number" and type(p.z) == "number" then
    pos = { x = p.x, y = p.y, z = p.z }
  else
    pos = { x = 0, y = 0, z = 0 }
  end
end

-- ---------- the viewport (M2) ----------
-- Node characters (even cells): '@' you (accent) - '#' mapped room (dim) -
-- '^'/'v'/'%' a room with an up/down/both exit, marked on the tile (dim; a
-- flag letter outranks the mark, the peek line always tells the whole story) -
-- '>' a boundary room, one with a cross-area link — the hub gates out of
-- Pinnacle, the way home on the far side (info; a flag letter outranks it,
-- so flagging a gate C/F/S still works) - '?' frontier, an exit into an
-- unmapped cell (warning) - 'A'..'Z' a flagged room, lettered on its tile
-- (info) - '*' the goto target (success) - '.' an unmapped room position, a
-- faint grid dot (inset) so the map reads as graph paper. Edge characters
-- (odd cells, drawn as thin lines by weave mode): '-' e/w, '|' n/s,
-- '/' ne/sw, '\' nw/se, 'x' two diagonals crossing (line).
local PALETTE = { ["@"] = "accent", ["#"] = "dim", ["?"] = "warning", ["*"] = "success",
  ["^"] = "dim", ["v"] = "dim", ["%"] = "dim", [">"] = "info", ["."] = "inset",
  ["-"] = "line", ["|"] = "line", ["/"] = "line", ["\\"] = "line", ["x"] = "line" }
for i = 65, 90 do PALETTE[string.char(i)] = "info" end
local FLAG_LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
local TILE_MARKS = FLAG_LETTERS .. "^v%>"  -- everything drawn as a letter on its tile

local function room_line(x, y, z, r)
  local parts = {}
  local nm = r.name
  if nm == "" then nm = "(unnamed)" end
  parts[#parts + 1] = nm
  parts[#parts + 1] = string.format("%d,%d,%d", x, y, z)
  local ex = {}
  for dircode in pairs(r.exits) do ex[#ex + 1] = dircode end
  table.sort(ex)
  if #ex > 0 then parts[#parts + 1] = "exits " .. table.concat(ex, ",") end
  local lk = {}
  for cmd in pairs(r.links) do lk[#lk + 1] = cmd end
  table.sort(lk)
  if #lk > 0 then parts[#parts + 1] = "link: " .. table.concat(lk, ", ") end
  if r.flag ~= "" then parts[#parts + 1] = "[" .. r.flag .. "]" end
  if r.note ~= "" then parts[#parts + 1] = r.note end
  return table.concat(parts, "  ")
end

local function set_peek_to_current()
  local r = get_room(pos)
  if r then
    scrye.setState(P .. "peek", room_line(pos.x, pos.y, pos.z, r))
  else
    scrye.setState(P .. "peek", "")
  end
end

draw = function()
  local z = view_z or pos.z
  draw_x0 = pos.x - HALF_C
  draw_y0 = pos.y + HALF_R
  draw_z = z

  -- the woven grid[row][col], 1-based over TROWS x TCOLS; row 1 is the northmost
  -- line. Rooms land on odd Lua indices (the control's even 0-based cells); the
  -- cells between carry the connectors. Every room position starts as a faint
  -- '.' so the empty viewport still reads as a grid.
  local grid = {}
  for row = 1, TROWS do
    local line = {}
    for col = 1, TCOLS do
      line[col] = (row % 2 == 1 and col % 2 == 1) and "." or " "
    end
    grid[row] = line
  end
  -- world (x,y) -> the 1-based node cell it draws on
  local function cell_of(x, y)
    return 2 * (x - draw_x0) + 1, 2 * (draw_y0 - y) + 1
  end
  local function put(x, y, ch, weak)
    local col, row = cell_of(x, y)
    if col < 1 or col > TCOLS or row < 1 or row > TROWS then return end
    local cur = grid[row][col]
    if weak and cur ~= "." then return end   -- frontier never covers a room
    grid[row][col] = ch
  end
  -- the connector on the edge cell between (x,y) and its dv-neighbour. One-way
  -- exits draw too (we only know the direction FROM this room, and that is
  -- exactly what the map should show). Crossing diagonals become 'x'.
  local function put_edge(x, y, dv)
    local col, row = cell_of(x, y)
    col = col + dv[1]
    row = row - dv[2]
    if col < 1 or col > TCOLS or row < 1 or row > TROWS then return end
    local ch
    if dv[2] == 0 then ch = "-"
    elseif dv[1] == 0 then ch = "|"
    elseif dv[1] * dv[2] > 0 then ch = "/"
    else ch = "\\" end
    local cur = grid[row][col]
    if (cur == "/" and ch == "\\") or (cur == "\\" and ch == "/") then ch = "x" end
    if cur ~= "x" then grid[row][col] = ch end
  end

  local zt = rooms[z] or {}
  for x, xt in pairs(zt) do
    for y, r in pairs(xt) do
      -- tile char: flag > boundary '>' > up/down mark > plain room. u/d can't
      -- be a connector (the neighbour is on another level), so the tile
      -- carries it; a cross-area link makes this a door to another map, which
      -- outranks the vertical mark but not a flag the user chose themselves.
      local ch = "#"
      if r.exits.u and r.exits.d then ch = "%"
      elseif r.exits.u then ch = "^"
      elseif r.exits.d then ch = "v" end
      for _, lk in pairs(r.links) do
        if lk.area then ch = ">" break end
      end
      if r.flag ~= "" then ch = r.flag end
      put(x, y, ch, false)
      -- connectors + frontier: same-level exits, into mapped cells or not.
      -- A dir the room has a LINK on gets its connector stub but no '?' —
      -- the other side lives on another map, it is not unexplored (M6).
      for dircode in pairs(r.exits) do
        local dv = DELTA[dircode]
        if dv and dv[3] == 0 then
          put_edge(x, y, dv)
          if not r.links[dircode] then
            local t = { x = x + dv[1], y = y + dv[2], z = z }
            if not get_room(t) then put(t.x, t.y, "?", true) end
          end
        end
      end
    end
  end
  if walk and walk.target.z == z then put(walk.target.x, walk.target.y, "*", false) end
  if pos.z == z then put(pos.x, pos.y, "@", false) end

  local lines = {}
  for row = 1, TROWS do lines[row] = table.concat(grid[row]) end
  scrye.setState(P .. "grid", table.concat(lines, "\n"))

  local mode = enabled and "MAPPING" or "OFF"
  if held_by then mode = "HELD (" .. esc(held_by) .. ")" end
  if walk then
    mode = string.format("WALKING %d/%d", math.min(walk.idx, #walk.dirs), #walk.dirs)
  elseif drift then
    mode = "DRIFT?"
  end
  local level = ""
  if view_z ~= nil and view_z ~= pos.z then level = string.format("  viewing z=%d", view_z) end
  scrye.setState(P .. "status", string.format("%s  %s  %d,%d,%d  %d rooms%s",
    mode, area, pos.x, pos.y, pos.z, room_count, level))
  scrye.setState(P .. "area", area)
  scrye.setState(P .. "rooms", tostring(room_count))
end

-- hover/click → peek. col/row are 0-based over the WOVEN grid: even/even is a
-- room cell (halve to get viewport coords), anything odd is a connector edge.
-- (-1,-1) = left the grid; edges restore the current room's line too — the
-- peek line doubles as "where am I" the rest of the time.
local function peek_cell(col, row)
  if col < 0 or row < 0 or col % 2 == 1 or row % 2 == 1 then
    set_peek_to_current()
    return
  end
  local x = draw_x0 + col // 2
  local y = draw_y0 - row // 2
  local r = get_room({ x = x, y = y, z = draw_z })
  if r then
    scrye.setState(P .. "peek", room_line(x, y, draw_z, r))
  else
    scrye.setState(P .. "peek", string.format("%d,%d,%d  (unmapped)", x, y, draw_z))
  end
end

-- ---------- pathfinding + the walk engine (M3) ----------
local function pkey(p) return p.x .. "|" .. p.y .. "|" .. p.z end

-- ---------- wasm pathfinder delegation ----------
-- When the 3s-pathfinder plugin (Rust/wasm) is loaded, goto path searches are delegated
-- to it over inter-plugin events. Emits dispatch SYNCHRONOUSLY, so the whole exchange
-- completes inside ask_pathfinder: we emit "map.path.find", the pathfinder's reply emit
-- fires our "map.path.result" handler before our emit returns. No pathfinder loaded =
-- no reply = we quietly fall back to find_path below. The area graph (area_to_table
-- shape) only ships when the pathfinder's cache is stale: requests carry map_serial,
-- and a {needArea=true} reply asks us to resend with the graph attached.
local path_req_id = 0
local path_reply = nil
scrye.on("map.path.result", function(data)
  local t = scrye.json.decode(data)
  if type(t) == "table" then path_reply = t end
end)

-- Returns (path|nil, answered): answered=false means no pathfinder responded (use the
-- local BFS); answered=true with nil path is an authoritative "unreachable".
local function ask_pathfinder(from, to)
  path_req_id = path_req_id + 1
  local req = {
    id = path_req_id, area = area, serial = map_serial,
    from = { x = from.x, y = from.y, z = from.z },
    to = { x = to.x, y = to.y, z = to.z },
  }
  path_reply = nil
  scrye.emit("map.path.find", scrye.json.encode(req))
  if type(path_reply) == "table" and path_reply.id == path_req_id and path_reply.needArea then
    req.rooms = area_to_table().rooms
    path_reply = nil
    scrye.emit("map.path.find", scrye.json.encode(req))
  end
  local r = path_reply
  path_reply = nil
  if type(r) ~= "table" or r.id ~= path_req_id or r.needArea then return nil, false end
  if not r.found then return nil, true end
  return r.dirs or {}, true
end

-- BFS through KNOWN rooms via compass exits AND in-area special links
-- (cross-area links never join a path — a goto stays in its area). Returns
-- a list of send words ("n", "enter well"), {} when already there, nil when
-- unreachable. Bounded so a huge area degrades to "too far" instead of
-- blowing the plugin callback budget.
local function find_path(from, to)
  local start = pkey(from)
  local goal = pkey(to)
  if start == goal then return {} end
  local came = {}
  came[start] = true
  local queue = { from }
  local head = 1
  local visited = 0
  local found = nil

  local function offer(cur, nxt, send)
    if found or not get_room(nxt) then return end
    local nk = pkey(nxt)
    if came[nk] then return end
    came[nk] = { prev = cur, send = send }
    if nk == goal then found = nk else queue[#queue + 1] = nxt end
  end

  while queue[head] and not found do
    local cur = queue[head]
    head = head + 1
    visited = visited + 1
    if visited > 20000 then return nil end
    local r = get_room(cur)
    if r then
      for dircode in pairs(r.exits) do
        local nxt = moved(cur, dircode)
        if nxt then offer(cur, nxt, dircode) end
      end
      for cmd, d in pairs(r.links) do
        if not d.area then offer(cur, { x = d.x, y = d.y, z = d.z }, cmd) end
      end
    end
  end

  if not found then return nil end
  local path = {}
  local at = found
  while at ~= start do
    local step = came[at]
    table.insert(path, 1, step.send)
    at = pkey(step.prev)
  end
  return path
end

local function in_combat()
  local e = scrye.getState("enemy.name")
  return e ~= nil and e ~= ""
end

walk_stop = function(reason)
  if not walk then return end
  walk = nil
  if walk_watchdog then scrye.cancel(walk_watchdog); walk_watchdog = nil end
  if reason then note(reason) end
  scrye.emit("map.walk.stopped", scrye.json.encode({ reason = reason or "" }))
  draw()
end

-- Send the next step if nothing is holding us: not mid-step, not fighting,
-- not done. Everything that could unblock a walk funnels back here.
walk_advance = function()
  if not walk or walk.awaiting then return end
  if in_combat() then
    if not walk.combat_noted then
      walk.combat_noted = true
      note("walk paused - fighting; resumes when the enemy is gone ('map stop' to abort)")
    end
    return
  end
  walk.combat_noted = false
  if walk.idx > #walk.dirs then
    local t = walk.target
    walk_stop(string.format("arrived at %d,%d,%d", t.x, t.y, t.z))
    return
  end
  walk.awaiting = true
  if walk_watchdog then scrye.cancel(walk_watchdog) end
  walk_watchdog = scrye.after(10, function()
    walk_watchdog = nil
    if walk and walk.awaiting then
      walk_stop("walk aborted - no room confirmation for 10s (lost? 'map set x y z' re-seats you)")
    end
  end)
  draw()
  scrye.send(walk.dirs[walk.idx])
end

start_goto = function(target)
  local r = get_room(target)
  if not r then
    note(string.format("%d,%d,%d is not mapped - walk there once first", target.x, target.y, target.z))
    return
  end
  if walk then walk_stop(nil) end   -- a new goto replaces the old one, quietly
  local path, answered = ask_pathfinder(pos, target)
  if not answered then path = find_path(pos, target) end
  if path == nil then
    note(string.format("no known path to %d,%d,%d - the rooms between aren't mapped yet", target.x, target.y, target.z))
    return
  end
  if #path == 0 then
    note("you are already there")
    return
  end
  walk = { dirs = path, idx = 1, awaiting = false, target = target, combat_noted = false }
  note(string.format("walking to %d,%d,%d - %d step(s). 'map stop' aborts.",
    target.x, target.y, target.z, #path))
  scrye.emit("map.walk.started", scrye.json.encode({
    area = area, x = target.x, y = target.y, z = target.z, steps = #path }))
  walk_advance()
end

-- combat ending is a state change, not a line — watch the MIP truth so the
-- walk resumes the moment the enemy drops (0.25s pacing via the timer).
scrye.watch("enemy.name", function()
  if walk and not walk.awaiting and not in_combat() then
    scrye.after(0.25, function() if walk then walk_advance() end end)
  end
end)

-- ---------- the Rooms tab ----------
-- Default listing: every flagged or noted room (the ones you bothered to
-- mark). 'map find <text>' / the search box replace it with matches.
refresh_roomlist = function(needle)
  local want = nil
  if needle and needle ~= "" then want = needle:lower() end
  local out = {}
  each_room(function(x, y, z, r)
    local hit = false
    if want then
      hit = (r.name ~= "" and r.name:lower():find(want, 1, true) ~= nil)
        or (r.note ~= "" and r.note:lower():find(want, 1, true) ~= nil)
    else
      hit = (r.flag ~= "" or r.note ~= "")
    end
    if hit then
      local extra = r.note
      if r.flag ~= "" then extra = "[" .. r.flag .. "] " .. extra end
      out[#out + 1] = { z, x, y,
        string.format("%s\t%d,%d,%d\t%s", r.name ~= "" and r.name or "(unnamed)", x, y, z, extra) }
    end
  end)
  table.sort(out, function(a, b)
    if a[1] ~= b[1] then return a[1] < b[1] end
    if a[2] ~= b[2] then return a[2] < b[2] end
    return a[3] < b[3]
  end)
  -- rows are numbered so 'map go <n>' can walk to one; last_list remembers
  -- each row's coordinates in the same order
  last_list = {}
  local lines = {}
  for i, rec in ipairs(out) do
    last_list[i] = { x = rec[2], y = rec[3], z = rec[1] }
    lines[i] = i .. ". " .. rec[4]
  end
  scrye.setState(P .. "roomlist", table.concat(lines, "\n"))
  return #out
end

-- ---------- room arrival ----------
-- "Temple yard (n, e, sw)." → name "Temple yard", exits from the trailing
-- parenthetical. Cheap :find gate before the anchored pattern (guide gotcha).
local function parse_short(short)
  local name = short
  local exit_desc = nil
  if short:find("(", 1, true) then
    exit_desc = short:match("%(([^)]*)%)%s*%.?%s*$")
    if exit_desc then name = short:gsub("%s*%([^)]*%)%s*%.?%s*$", "") end
  end
  name = name:gsub("^%s+", ""):gsub("%s+$", "")
  return name, exit_desc
end

local function on_room(short)
  seen_s = true
  if not enabled or held_by then return end

  local name, exit_desc = parse_short(short)

  -- pending entries: {dir=} a compass move, {dest=} a known link's landing,
  -- {bind=, from=} an armed 'map link', {enter=, cmd=, from=} an armed
  -- 'map enter' — both waiting for this very arrival
  local step = nil
  if #pending > 0 then step = table.remove(pending, 1) end
  local via_move = false     -- confirmed compass move → the drift check applies
  local rev = nil            -- return link to record on the arrival room (M6)
  if step then
    view_z = nil                              -- arriving somewhere ends level inspection
    if step.dir then
      local from = get_room(pos)
      if from then from.exits[step.dir] = true end   -- we used this exit, so it exists
      local np = moved(pos, step.dir)
      if np then
        undo_stack[#undo_stack + 1] = { to = np, from = { x = pos.x, y = pos.y, z = pos.z },
                                        created = get_room(np) == nil }
        if #undo_stack > 20 then table.remove(undo_stack, 1) end
        pos = np
        via_move = true
      end
    elseif step.dest then
      local d = step.dest
      if d.area and d.area ~= area then
        if walk then walk_stop("walk stopped - crossed into '" .. esc(d.area) .. "'") end
        -- a compass crossing knows its way back: queue the return link for
        -- the arrival room, pointing at where we stand NOW in the OLD area
        local from_here = { x = pos.x, y = pos.y, z = pos.z, area = area }
        if step.rev then rev = { dir = step.rev, dest = from_here } end
        note("area link - switching to '" .. esc(d.area) .. "'")
        flush()
        local from_realm = realm
        load_area(d.area)                     -- clears pending/undo; the jump invalidates both
        if not realm and from_realm then
          realm = from_realm                  -- lineage: the new area is in the realm we came from
          mark_dirty()
        end
        if refresh_accent then refresh_accent() end
        last_cross = from_here                -- after load_area, which clears it
      end
      pos = { x = d.x, y = d.y, z = d.z }
    elseif step.enter then
      -- an armed 'map enter <area>' closes: the command just sent is the
      -- boundary. Record it on the room we left, cross, and (for a compass
      -- move) queue the return link for the arrival.
      local e = step.enter
      if walk then walk_stop(nil) end
      local fromr = get_room(step.from)
      if fromr then
        fromr.links[step.cmd] = { x = e.x, y = e.y, z = e.z, area = e.area }
        map_serial = map_serial + 1
        note(string.format("boundary recorded: '%s' at %d,%d,%d -> %s:%d,%d,%d", esc(step.cmd),
          step.from.x, step.from.y, step.from.z, esc(e.area), e.x, e.y, e.z))
      end
      local from_here = { x = step.from.x, y = step.from.y, z = step.from.z, area = area }
      if OPP[step.cmd] then
        rev = { dir = OPP[step.cmd], dest = from_here }
      else
        note("portal boundary - 'map back <cmd>' here records the way home")
      end
      if e.area ~= area then
        flush()
        local from_realm = realm
        load_area(e.area)                     -- creates the area if it is new
        if not realm and from_realm then
          realm = from_realm
          mark_dirty()
        end
        if refresh_accent then refresh_accent() end
      end
      last_cross = from_here                  -- after load_area, which clears it
      pos = { x = e.x, y = e.y, z = e.z }
    elseif step.bind then
      -- close an armed 'map link': prefer the ONE room already named like
      -- this arrival; otherwise park the destination on a free cell nearby
      local dest = nil
      if name ~= "" then
        local matches = {}
        each_room(function(x, y, z, r)
          if r.name == name then matches[#matches + 1] = { x = x, y = y, z = z } end
        end)
        if #matches == 1 then dest = matches[1] end
      end
      local parked = false
      if not dest then
        dest = free_cell_near(step.from)
        parked = true
      end
      local fromr = get_room(step.from)
      if fromr then
        fromr.links[step.bind] = { x = dest.x, y = dest.y, z = dest.z }
        map_serial = map_serial + 1
        note(string.format("link '%s' bound: %d,%d,%d -> %d,%d,%d%s", esc(step.bind),
          step.from.x, step.from.y, step.from.z, dest.x, dest.y, dest.z,
          parked and "  (new cell - 'map set x y z' if you know where this really is)" or ""))
      end
      pos = dest
    end
  end
  -- no pending step = a look/glance refresh of wherever we already are

  -- the DRIFT check (M4): a confirmed compass move landing on a room whose
  -- recorded name disagrees with what the MUD just said. OUR record is
  -- preserved — the user decides, we just refuse to quietly overwrite a map
  -- that might be right while the reckoning is wrong.
  local drift_this = false
  if via_move and name ~= "" then
    local existing = get_room(pos)
    if existing and existing.name ~= "" and existing.name ~= name then
      drift_this = true
      if not drift then
        note(string.format("DRIFT? expected '%s' at %d,%d,%d but the MUD says '%s'",
          esc(existing.name), pos.x, pos.y, pos.z, esc(name)))
        note("'map set x y [z]' re-seats you - 'map undo' forgets the last learned room - or keep walking if the room was just renamed")
      end
      drift = true
      if walk then walk_stop("walk aborted - the map and the world disagree (DRIFT?)") end
    end
  end
  if via_move and name ~= "" and not drift_this then drift = false end

  local room = add_room(pos)
  if not drift_this then
    if name ~= "" then room.name = name end
    if exit_desc then
      -- trust what the room says NOW (the chaos-sea rule) — but diff first, so
      -- walking through known rooms doesn't bump the pathfinder serial
      local new_exits = {}
      for word in exit_desc:gmatch("[^,%s]+") do
        local c = CANON[word:lower()]
        if c then new_exits[c] = true end
      end
      local changed = false
      for c in pairs(new_exits) do if not room.exits[c] then changed = true break end end
      if not changed then
        for c in pairs(room.exits) do if not new_exits[c] then changed = true break end end
      end
      if changed then map_serial = map_serial + 1 end
      room.exits = new_exits
    end
  end

  -- the queued return link (M6): recorded once, never clobbering a link the
  -- room already has on that direction — an existing record was put there by
  -- someone who knew something.
  if rev and not room.links[rev.dir] then
    room.links[rev.dir] = rev.dest
    map_serial = map_serial + 1
    note(string.format("return link recorded: '%s' leads back to %s:%d,%d,%d",
      rev.dir, esc(rev.dest.area), rev.dest.x, rev.dest.y, rev.dest.z))
  end

  -- a confirmed step (not a look-refresh) while a walk step is in flight is
  -- that step landing: count it and pace the next one out 0.25s later
  if walk and walk.awaiting and step then
    walk.awaiting = false
    walk.idx = walk.idx + 1
    if walk_watchdog then scrye.cancel(walk_watchdog); walk_watchdog = nil end
    scrye.after(0.25, function() if walk then walk_advance() end end)
  end

  mark_dirty()
  draw()
  set_peek_to_current()
  -- the position feed (M4): any plugin can scrye.on("map.room", ...) and
  -- know where the character is without parsing a single line
  scrye.emit("map.room", scrye.json.encode({
    area = area, x = pos.x, y = pos.y, z = pos.z, name = room.name }))
end

-- ---------- movement capture (the 1.6 core) ----------
-- Observe-only, every source. Classify; NEVER send from here. Precedence
-- (M6): an armed 'map enter' (the next command IS the boundary), an armed
-- 'map link' command, the CURRENT room's recorded links — a compass word
-- CAN be a link, which is how walking through an area boundary switches
-- maps instead of dead-reckoning a phantom room — and only then plain
-- compass dead reckoning.
scrye.onCommand(function(cmd)
  if not enabled or held_by then return end
  local word = tostring(cmd or ""):lower():gsub("^%s+", ""):gsub("%s+$", "")
  if word == "" then return end
  local canon = CANON[word]

  local entry = nil
  if armed_enter then
    entry = { enter = armed_enter, cmd = canon or word,
              from = { x = pos.x, y = pos.y, z = pos.z } }
    armed_enter = nil
  elseif armed_link and (canon or word) == armed_link then
    entry = { bind = armed_link, from = { x = pos.x, y = pos.y, z = pos.z } }
    armed_link = nil
  else
    local r = get_room(pos)
    local d = r and (r.links[word] or (canon and r.links[canon])) or nil
    if d then
      entry = { dest = d }
      -- a compass move through a cross-area link: the arrival can record
      -- the way back (the opposite direction, in the old area)
      if canon and d.area then entry.rev = OPP[canon] end
    elseif canon then
      entry = { dir = canon }
    end
  end
  if not entry then return end

  if #pending >= MAX_PENDING then
    pending = {}
    note("pending-move queue overflowed - flushed (was the map keeping up?)")
  end
  pending[#pending + 1] = entry
end)

-- Another plugin can suspend auto-mapping while it owns movement through space
-- that must not be mapped (API 1.6 events): the chaos-sea explorer holds the map
-- while its randomly generated sea is active, so sea steps never dead-reckon
-- phantom rooms into a real area. The hold is transient by design -- never
-- persisted, released by the sender, and 'map on' always overrides it.
scrye.on("map.hold", function(data, _, source)
  local ok, t = pcall(scrye.json.decode, data)
  local on = ok and type(t) == "table" and t.on == true
  local who = source or "?"
  if on and not held_by then
    held_by = who
    pending = {}
    if walk then walk_stop("walk stopped - auto-mapping held by " .. esc(who)) end
    note("auto-mapping held by " .. esc(who) .. " - their moves will not be mapped ('map on' overrides)")
  elseif not on and held_by then
    held_by = nil
    pending = {}
    note("auto-mapping resumed (" .. esc(who) .. " released the hold)")
  else
    return
  end
  draw()
end)

-- ---------- triggers ----------
scrye.addTrigger{ pattern = [[^=S=(.*)=S=]], regex = true,
  run = function(short) on_room(short or "") end }

-- a refused move: whatever we thought was in flight, it isn't — and a walk
-- that just had a step refused (closed door, wall) must not keep marching
local function on_refused_move()
  pending = {}
  if walk then
    walk_stop("walk aborted - the MUD refused a step (a closed door?). 'map goto' again after opening it.")
  end
end
scrye.addTrigger{ pattern = [[^You cannot go (\w+)\.$]], regex = true,
  run = function() on_refused_move() end }
scrye.addTrigger{ pattern = [[^You are unable to penetrate the wall that]], regex = true,
  run = function() on_refused_move() end }

-- ---------- commands ----------
local function status()
  note(string.format("%s - area '%s'%s at %d,%d,%d - %d rooms, %d move(s) pending",
    enabled and "ON" or "OFF", esc(area), realm and (" (" .. realm .. ")") or "",
    pos.x, pos.y, pos.z, room_count, #pending))
  if held_by then
    note("auto-mapping is HELD by " .. esc(held_by) .. " (their space is unmappable; 'map on' overrides)")
  end
  if enabled and not seen_s then
    note("no =S= room markers seen yet - check your 3Scapes marker settings (the stepper/chaos-sea prerequisite)")
  end
  note("commands: map on|off - map area <name> - map areas - map set x y [z] - map undo")
  note("          map realm fantasy|science|chaos|-  (tint the panel by realm; crossings inherit it)")
  note("          map note <text>|- - map flag <A-Z>|- - map find <text>")
  note("          map goto x y [z] - map go <n> - map stop  (or click a mapped room on the panel)")
  note("          map link <cmd> (arm; next arrival binds) - map link <cmd> = [area] x y z  (cmd may be n/s/e/...)")
  note("          map enter <area> [x y z] (arm; the NEXT command is the boundary, both ways if compass)")
  note("          map back <cmd> (after crossing: bind <cmd> here to the room you came from)")
  note("          map links - map unlink <cmd> - map export [name] - map wipe <name> confirm")
end

scrye.addAlias{ pattern = [[^map$]], regex = true, run = status }

scrye.addAlias{ pattern = [[^map on$]], regex = true, run = function()
  enabled = true
  held_by = nil                -- the user outranks any plugin's hold
  save_now()
  draw()
  note("mapping ON - area '" .. esc(area) .. "'")
end }

scrye.addAlias{ pattern = [[^map off$]], regex = true, run = function()
  enabled = false
  pending = {}
  if walk then walk_stop("walk stopped - mapping turned off") end
  save_now()
  draw()
  note("mapping OFF")
end }

scrye.addAlias{ pattern = [[^map realm (fantasy|science|chaos|-)$]], regex = true, run = function(w)
  realm = (w ~= "-") and w or nil
  save_now()
  if refresh_accent then refresh_accent() end
  note(realm and ("area '" .. esc(area) .. "' is in the " .. realm .. " realm - panel tinted to match")
             or ("area '" .. esc(area) .. "' realm cleared - panel follows the theme"))
end }

scrye.addAlias{ pattern = [[^map area ([\w-]+)$]], regex = true, run = function(name)
  if not name or name == "" then return end
  if walk then walk_stop(nil) end   -- the plan belonged to the old area
  flush()               -- current area's unsaved work first
  load_area(name)
  save_now()            -- the new (possibly empty) area exists from now on
  if refresh_accent then refresh_accent() end
  draw()
  set_peek_to_current()
  refresh_roomlist(nil)
  note(string.format("area '%s' - %d rooms, position %d,%d,%d",
    esc(name), room_count, pos.x, pos.y, pos.z))
end }

-- fix dead reckoning by hand — also the way out of a DRIFT? state
scrye.addAlias{ pattern = [[^map set (-?\d+) (-?\d+)(?: (-?\d+))?$]], regex = true,
  run = function(x, y, z)
    if walk then walk_stop(nil) end   -- re-seating invalidates the plan
    pos = { x = tonumber(x) or 0, y = tonumber(y) or 0, z = tonumber(z) or pos.z }
    pending = {}
    drift = false
    view_z = nil
    mark_dirty()
    draw()
    set_peek_to_current()
    note(string.format("position set to %d,%d,%d", pos.x, pos.y, pos.z))
  end }

-- forget the last CONFIRMED move: a room the drift check flagged (or you
-- just know is wrong) disappears again if this arrival created it, and you
-- are re-seated where you stood before it
scrye.addAlias{ pattern = [[^map undo$]], regex = true, run = function()
  local u = table.remove(undo_stack)
  if not u then note("nothing to undo") return end
  if walk then walk_stop(nil) end
  if u.created then delete_room(u.to) end
  pos = { x = u.from.x, y = u.from.y, z = u.from.z }
  pending = {}
  drift = false
  mark_dirty()
  draw()
  set_peek_to_current()
  refresh_roomlist(nil)
  note(string.format("undone%s - back at %d,%d,%d",
    u.created and " (learned room forgotten)" or "", pos.x, pos.y, pos.z))
end }

-- ---------- special links (M4) ----------
-- explicit bind, in-area or cross-area: map link enter grate = sewers 0 0 0.
-- Since M6 the command may be a plain compass direction (map link n =
-- fantasy 0 0 0) — that is how a walk-through area boundary is recorded, and
-- compass words are stored canonical so 'north' and 'n' are the same link.
scrye.addAlias{ pattern = [[^map link (.+?) = (?:([\w-]+) )?(-?\d+) (-?\d+) (-?\d+)$]], regex = true,
  run = function(cmd, larea, x, y, z)
    local r = get_room(pos)
    if not r then note("not in a mapped room yet") return end
    cmd = cmd:lower():gsub("^%s+", ""):gsub("%s+$", "")
    cmd = CANON[cmd] or cmd
    if cmd == "" then return end
    local d = { x = tonumber(x) or 0, y = tonumber(y) or 0, z = tonumber(z) or 0 }
    if larea and larea ~= "" then d.area = larea end
    r.links[cmd] = d
    map_serial = map_serial + 1
    mark_dirty()
    draw()
    set_peek_to_current()
    note(string.format("link '%s' bound: this room -> %s%d,%d,%d%s", esc(cmd),
      d.area and (esc(d.area) .. ":") or "", d.x, d.y, d.z,
      d.area and "  (cross-area: goto won't path through it, but using it tracks you)" or ""))
  end }

scrye.addAlias{ pattern = [[^map link -$]], regex = true, run = function()
  if armed_link then
    note("link arming cancelled ('" .. esc(armed_link) .. "')")
    armed_link = nil
  else
    note("no link armed")
  end
end }

-- armed bind: the next time you SEND this exact command, the arrival that
-- follows closes the link (unique-name match, else a parked new cell)
scrye.addAlias{ pattern = [[^map link (.+)$]], regex = true, run = function(cmd)
  local r = get_room(pos)
  if not r then note("not in a mapped room yet") return end
  cmd = cmd:lower():gsub("^%s+", ""):gsub("%s+$", "")
  cmd = CANON[cmd] or cmd
  if cmd == "" then return end
  armed_link = cmd
  note(string.format("armed: the next '%s' you send binds a link from THIS room to wherever you arrive ('map link -' cancels)", esc(cmd)))
end }

scrye.addAlias{ pattern = [[^map links$]], regex = true, run = function()
  local r = get_room(pos)
  if not r then note("not in a mapped room yet") return end
  local any = false
  for cmd, d in pairs(r.links) do
    any = true
    note(string.format("  '%s' -> %s%d,%d,%d", esc(cmd),
      d.area and (esc(d.area) .. ":") or "", d.x, d.y, d.z))
  end
  if not any then note("no links from this room - 'map link <cmd>' records one") end
end }

scrye.addAlias{ pattern = [[^map unlink (.+)$]], regex = true, run = function(cmd)
  local r = get_room(pos)
  if not r then note("not in a mapped room yet") return end
  cmd = cmd:lower():gsub("^%s+", ""):gsub("%s+$", "")
  cmd = CANON[cmd] or cmd
  if r.links[cmd] then
    r.links[cmd] = nil
    map_serial = map_serial + 1
    mark_dirty()
    draw()
    set_peek_to_current()
    note("link '" .. esc(cmd) .. "' removed")
  else
    note("this room has no link '" .. esc(cmd) .. "'")
  end
end }

-- ---------- area boundaries (M6) ----------
-- 'map enter <area> [x y z]' arms: the NEXT command you send is taken as the
-- boundary into <area> (created if new), landing at x,y,z (default 0,0,0).
-- Send the boundary move immediately after arming — the very next command
-- closes it, whatever it is. A compass boundary records its return link too.
scrye.addAlias{ pattern = [[^map enter -$]], regex = true, run = function()
  if armed_enter then
    note("enter arming cancelled ('" .. esc(armed_enter.area) .. "')")
    armed_enter = nil
  else
    note("no enter armed")
  end
end }

scrye.addAlias{ pattern = [[^map enter ([\w-]+)(?: (-?\d+) (-?\d+)(?: (-?\d+))?)?$]], regex = true,
  run = function(name, x, y, z)
    if not name or name == "" then return end
    if not get_room(pos) then note("not in a mapped room yet") return end
    armed_enter = { area = name, x = tonumber(x) or 0, y = tonumber(y) or 0, z = tonumber(z) or 0 }
    note(string.format(
      "armed: the NEXT command you send crosses into '%s' at %d,%d,%d - send the boundary move now ('map enter -' cancels)",
      esc(name), armed_enter.x, armed_enter.y, armed_enter.z))
  end }

-- 'map back <cmd>': bind <cmd> in THIS room to the room the last cross-area
-- jump came from — the no-coordinates way to record a portal boundary's
-- return. (Compass crossings record their own return; this is for the
-- 'chaos'/'enter well' kind, whose way home is never guessable.)
scrye.addAlias{ pattern = [[^map back (.+)$]], regex = true, run = function(cmd)
  if not last_cross then
    note("no cross-area jump to point back at - 'map back' works right after crossing a boundary")
    return
  end
  local r = get_room(pos)
  if not r then note("not in a mapped room yet") return end
  cmd = cmd:lower():gsub("^%s+", ""):gsub("%s+$", "")
  cmd = CANON[cmd] or cmd
  if cmd == "" then return end
  r.links[cmd] = { x = last_cross.x, y = last_cross.y, z = last_cross.z, area = last_cross.area }
  map_serial = map_serial + 1
  mark_dirty()
  draw()
  set_peek_to_current()
  note(string.format("way home recorded: '%s' -> %s:%d,%d,%d", esc(cmd),
    esc(last_cross.area), last_cross.x, last_cross.y, last_cross.z))
end }

scrye.addAlias{ pattern = [[^map areas$]], regex = true, run = function()
  local idx = scrye.json.decode(scrye.store.get("areas") or "") or {}
  if type(idx) ~= "table" then idx = {} end
  local stored = {}
  for _, n in ipairs(idx) do stored[n] = true end
  local names = {}
  for _, n in ipairs(idx) do names[#names + 1] = n end
  for n in pairs(SEEDS) do
    if not stored[n] then names[#names + 1] = n .. " (seed)" end
  end
  table.sort(names)
  if #names == 0 then
    note("no areas yet - walking maps '" .. esc(area) .. "' as you go")
  else
    note("areas: " .. esc(table.concat(names, ", ")) .. "  (current: " .. esc(area) .. ")")
  end
end }

-- annotate the room you are standing in
scrye.addAlias{ pattern = [[^map note (.+)$]], regex = true, run = function(text)
  local r = get_room(pos)
  if not r then note("not in a mapped room yet") return end
  if text == "-" then r.note = "" else r.note = text end
  mark_dirty()
  draw()
  set_peek_to_current()
  refresh_roomlist(nil)
  note(text == "-" and "note cleared" or ("noted: " .. esc(text)))
end }

scrye.addAlias{ pattern = [[^map flag ([A-Za-z-])$]], regex = true, run = function(ch)
  local r = get_room(pos)
  if not r then note("not in a mapped room yet") return end
  if ch == "-" then r.flag = "" else r.flag = ch:upper() end
  mark_dirty()
  draw()
  set_peek_to_current()
  refresh_roomlist(nil)
  note(ch == "-" and "flag cleared" or ("flagged [" .. ch:upper() .. "] - drawn on the map tile"))
end }

scrye.addAlias{ pattern = [[^map find (.+)$]], regex = true, run = function(text)
  local n = refresh_roomlist(text)
  note(string.format("%d room(s) match '%s' - see the Rooms tab; 'map go <n>' walks to one", n, esc(text)))
end }

-- ---------- goto / go / stop (M3) ----------
scrye.addAlias{ pattern = [[^map goto (-?\d+) (-?\d+)(?: (-?\d+))?$]], regex = true,
  run = function(x, y, z)
    start_goto({ x = tonumber(x) or 0, y = tonumber(y) or 0, z = tonumber(z) or pos.z })
  end }

scrye.addAlias{ pattern = [[^map go (\d+)$]], regex = true, run = function(n)
  local target = last_list[tonumber(n) or 0]
  if not target then
    note("no row " .. esc(n) .. " - 'map find <text>' (or the Rooms tab) numbers the rows first")
    return
  end
  start_goto(target)
end }

scrye.addAlias{ pattern = [[^map stop$]], regex = true, run = function()
  if walk then walk_stop("walk stopped") else note("no walk in progress") end
end }

scrye.addAlias{ pattern = [[^map export(?: ([\w-]+))?$]], regex = true, run = function(name)
  local which = (name and name ~= "") and name or area
  local payload = nil
  if which == area then
    payload = scrye.json.encode(area_to_table())
  else
    payload = scrye.store.get("map:" .. which)
    if not payload then
      note("no area named '" .. esc(which) .. "' - 'map' lists the current one; check 'areas' in your store")
      return
    end
  end
  note("area '" .. esc(which) .. "' (paste into maps.json in M4, or keep as a backup):")
  scrye.print(esc(payload))
end }

scrye.addAlias{ pattern = [[^map wipe ([\w-]+)(?: (confirm))?$]], regex = true,
  run = function(name, confirm)
    if not name or name == "" then return end
    if confirm ~= "confirm" then
      note("this deletes area '" .. esc(name) .. "' - type: map wipe " .. esc(name) .. " confirm")
      return
    end
    scrye.store.delete("map:" .. name)
    scrye.store.delete("pos:" .. name)
    if name == area then
      load_area(area)   -- store is gone, so this resets to an empty grid
      save_now()
    end
    draw()
    refresh_roomlist(nil)
    note("wiped area '" .. esc(name) .. "'")
  end }

-- ---------- the panel (M2) ----------
build_panel = function()
scrye.addPanel{
  title = "3S Map",
  width = 280,
  accent = realm and REALM_COLOR[realm] or nil,   -- border wears the current realm's colour
  tabs = {
    { title = "Map", widgets = {
      { type = "label", bind = P .. "status", color = "dim" },
      { type = "colorgrid", bind = P .. "grid", palette = PALETTE, labels = TILE_MARKS,
        weave = true,   -- API 1.7: rooms as tiles, exits as thin lines between them
        -- click on a MAPPED room (that you aren't standing in) = walk there
        -- (M3). Anything else — unmapped cells, connector edges, your own
        -- tile — peeks, which is also how the companion (no hover on touch)
        -- reads the map. Coordinates are woven: even/even = a room cell.
        onClick = function(col, row, ch)
          if col < 0 or row < 0 or col % 2 == 1 or row % 2 == 1 then
            peek_cell(col, row)
            return
          end
          local x = draw_x0 + col // 2
          local y = draw_y0 - row // 2
          local target = { x = x, y = y, z = draw_z }
          local here = (x == pos.x and y == pos.y and draw_z == pos.z)
          if get_room(target) and not here then
            peek_cell(col, row)          -- show what you clicked...
            start_goto(target)           -- ...and start walking to it
          else
            peek_cell(col, row)
          end
        end,
        onHover = function(col, row, ch) peek_cell(col, row) end },
      { type = "buttonrow", buttons = {
        { text = "Up",     action = function() view_z = (view_z or pos.z) + 1; draw() end },
        { text = "Down",   action = function() view_z = (view_z or pos.z) - 1; draw() end },
        { text = "Center", action = function() view_z = nil; draw(); set_peek_to_current() end },
        { text = "Stop",   action = function() if walk then walk_stop("walk stopped") end end },
      } },
      { type = "value", text = "", bind = P .. "peek" },
    } },
    { title = "Rooms", widgets = {
      { type = "label", text = "flagged + noted rooms; search replaces the list", color = "dim" },
      { type = "input", text = "find",
        onSubmit = function(text) refresh_roomlist(text ~= "" and text or nil) end },
      { type = "table", bind = P .. "roomlist",
        columns = { "Room", "Pos", "Note" }, align = "lll" },
    } },
  },
}
end

-- Rebuild the panel only when the realm actually changed: a rebuild re-seeds the
-- Rooms tab's search box, so it must never happen on a mere redraw.
local shown_realm = nil
refresh_accent = function()
  if realm == shown_realm then return end
  shown_realm = realm
  build_panel()
end
build_panel()

-- ---------- lifecycle ----------
scrye.onDisconnect(function()
  if walk then walk_stop(nil) end   -- the connection took the walk with it
  flush()
end)
-- The idle guard: stop what we are driving and stay stopped — a walk that
-- silently resumes because you typed 'look' is the surprise the guard
-- exists to prevent. 'map goto' starts a fresh one when you're back.
scrye.onIdle(function()
  if walk then walk_stop("idle guard - walk stopped (it will NOT auto-resume)") end
  flush()
end)

-- ---------- startup ----------
enabled = (scrye.store.get("enabled") or "1") ~= "0"
load_area(scrye.store.get("area") or "default")
refresh_accent()   -- the restored area may carry a realm; tint before first paint
draw()
set_peek_to_current()
refresh_roomlist(nil)
