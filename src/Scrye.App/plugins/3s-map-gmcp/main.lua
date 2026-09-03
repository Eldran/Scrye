-- ============================================================
-- 3S Map (GMCP) — the automapper rebuilt on what the server tells us.
-- Lives in _lab, out of the repo, and runs BESIDE the shipped 3s-map
-- rather than replacing it.
--
-- PHASE 1 — the store. Rooms are keyed by the number the server gives
-- them, links are number -> number, and the whole thing persists.
-- PHASE 2a — the picture. A HUD panel, drawn in the same visual language
-- as the shipped 3s-map so the two can be read side by side.
--
-- COORDINATES ARE A LAYOUT, NOT AN IDENTITY
--   The shipped map stores x,y,z and looks a room up by them, so a wrong
--   coordinate IS a wrong room and there is no way back. Here identity is
--   the number and coordinates are computed from the link graph on
--   demand: a bad layout is a bad drawing of a correct store, and
--   redrawing fixes it. Nothing is ever looked up by position.
--
-- ONE GRID PER AREA PER COMPONENT
--   Not one grid for the world. An area is drawn on its own grid, and an
--   area that turns out to be in two unconnected pieces gets a grid each.
--   That matters most for "Unknown", which is not one place: it is every
--   stretch of connective realm on the MUD wearing the same label. Drawn
--   as one map it would be an unreadable tangle of unrelated corridors.
--   Split by what actually connects, it is a handful of small, honest
--   maps -- and no realm field is needed to do it, only the links.
--   Components are found over COMPASS links alone, because that is
--   exactly the set of links that can be laid out relative to each other.
--   'in', 'out' and 'enter' cross between maps and are drawn as doors.
--
-- PHASE 2b — walking. The first thing here that can move you, and so the
-- first that asks for commands.send.
--
-- A STEP IS CONFIRMED, NOT ASSUMED
--   Every other MUD walker sends its next step on a prompt and hopes. It
--   cannot do better: nothing in the output says which room you are in, so
--   a step into a closed door, a mob blocking the way, or a teleport looks
--   exactly like a step that worked, and the walker keeps going, now lost.
--   Here the next step is sent when Room.Info says we arrived AND says the
--   room is the one the route expected. Anything else stops the walk on
--   the spot and says what happened. That is not caution bolted on; it is
--   the difference a room number makes. Not one command, in any code path. 'mapg path'
-- prints a route; walking it is yours to type. The manifest declares no
-- commands.send, on purpose, and the harness asserts it.
--
-- WHY A NUMBER CHANGES EVERYTHING
--   Dead reckoning has to answer "where am I" by adding up every step
--   since the last known point, so one missed step is wrong forever and
--   it cannot tell that it is wrong. That is the DRIFT? the shipped map
--   reports: not a bad walk, a bad starting point it had no way to
--   question. Here the server answers the question outright, so:
--     * identity is never inferred, and so never drifts;
--     * a link is a fact we were told or a fact we walked;
--     * and when our idea of where we were disagrees with where the
--       server says we are, we can SEE that and decline to learn from
--       it. Detecting the desync is the part dead reckoning cannot do.
--
-- WHAT IS DELIBERATELY NOT DONE
--   Reverse links are not inferred. Walking 'n' from A to B does not
--   write B.s = A. It is nearly always true -- and it is unnecessary,
--   because standing in B we are TOLD B's exits. The only case where
--   inferring would add anything is when B's 's' is 0, i.e. exactly
--   where the server has chosen to withhold the destination, which is
--   the likeliest place for the assumption to be a trap. A store of
--   facts is worth more than a fuller store of mostly-facts.
--
-- What arrives, from four captures on two characters:
--   { "num": 50942, "name": "The gatehouse of Midgard", "area": "Midgard",
--     "exits": { "nw": 50940, "sw": 50943, "in": 50943 } }
-- with every oddity the captures actually contained:
--   * exits is an OBJECT, direction -> destination room number.
--   * 0 means "there is a way here and I will not say where" -- a
--     frontier, not an error.
--   * an exit can point at the room you are standing in.
--   * two exits can lead to the same room ('sw' and 'in' above).
--   * 'in', 'out' and 'enter' are exits; not every key is a compass point.
--   * an EMPTY exits object is not a dead end -- hidden exits are omitted.
--   * area is often "Unknown": the connective realm between named areas,
--     plus the main town. An answer, not a gap.
-- ============================================================

local ROOM_PKG   = "Room.Info"
local STORE_KEY  = "rooms"
-- The MAP lives in the MUD-shared store (scrye.shared, API 1.14) when the host offers one:
-- 3Scapes' map is the same for every character, so a map built on one character should not
-- need rebuilding on the next - every profile on the same host reads one file. Preferences
-- (talking, drawing) stay in scrye.store: those are this profile's, not the world's. On an
-- older host scrye.shared is absent and WORLD degrades to the private store, exactly as
-- before this existed.
local WORLD = scrye.shared or scrye.store
local STORE_VER  = 1

local QUIET_SECS = 20     -- decide the feed is not coming
local MOVE_TTL   = 12     -- a queued move older than this is stale
local FLUSH_SECS = 15     -- dirty-write cadence
local LIST_CAP   = 40     -- lines before a listing says "and N more"
local BFS_CAP    = 20000  -- rooms a search may touch
local WALK_CAP   = 200    -- steps a single walk may take
local WALK_WAIT  = 10     -- seconds to wait for a step to land before giving up

-- The room viewport, and the woven grid it becomes: rooms sit on even
-- (0-based) cells and the connectors between them on the odd ones. Same
-- numbers as the shipped 3s-map, so the two panels are the same size.
local COLS, ROWS   = 21, 15
local HALF_C, HALF_R = COLS // 2, ROWS // 2
local TCOLS, TROWS = COLS * 2 - 1, ROWS * 2 - 1

local P = "plugin.3s-map-gmcp."   -- state prefix the panel binds to

-- Which way each compass direction moves the layout. u/d change level only,
-- so they never move x or y -- a room above is not a room to the north.
local DELTA = {
  n  = { 0,  1, 0 }, s  = { 0, -1, 0 }, e  = { 1, 0, 0 }, w  = { -1, 0, 0 },
  ne = { 1,  1, 0 }, nw = { -1, 1, 0 }, se = { 1, -1, 0 }, sw = { -1, -1, 0 },
  u  = { 0, 0, 1 },  d  = { 0, 0, -1 },
}

-- The same tile vocabulary as the shipped map, on purpose: '@' you, '#' a
-- room, '?' an exit into nothing mapped, '.' an empty grid position, '^v%'
-- up/down/both, '>' a door to another map, '!' a room the layout could not
-- place where its links say it belongs. Edge cells carry '-|/\x'.
local PALETTE = { ["@"] = "accent", ["#"] = "dim", ["?"] = "warning", ["!"] = "warning",
  ["^"] = "dim", ["v"] = "dim", ["%"] = "dim", [">"] = "info", ["."] = "inset",
  ["-"] = "line", ["|"] = "line", ["/"] = "line", ["\\"] = "line", ["x"] = "line" }
local TILE_MARKS = "^v%>!"

-- ---------- state ----------
local talking = true
local rooms   = {}        -- num -> { name, area, exits, walked, visits, shift?, vary? }
                          -- shift: dir -> true, an exit marked SHIFTING (elevator, portal:
                          -- it genuinely ends somewhere different each ride, so no single
                          -- destination is the truth). vary: dir -> how many times a WALK
                          -- through it has ended somewhere new, the auto-mark's evidence.
local known   = 0         -- #rooms
local here    = nil       -- num of the room we are in
local msgs    = 0
local dirty   = false
local moves   = {}        -- queued { dir, from, at } awaiting a Room.Info
local mapnames = {}       -- seed room number -> a name you gave that map
local maplist_seeds = {}  -- Maps tab row index -> that row's map seed, for row clicks
local maplist_filter = "" -- Maps tab search text (lowercased); "" = show everything
local favs = {}           -- favourite maps, as ROOM numbers (any member room): a room
                          -- resolves to whatever map contains it at display time, so a
                          -- favourite survives its map growing or merging with another.
                          -- Persisted with the store, shared like it; capped at FAV_CAP.
local favlist_nums = {}   -- Favs tab row index -> that row's resolved room, for clicks
local FAV_CAP = 20
local walk    = nil       -- { steps, idx, target, sent, why } while walking
local stats   = { told = 0, walked = 0, contradictions = 0, desyncs = 0, new = 0 }
local quiet_timer, flush_timer
local warned  = false
local drawing = true      -- keep the panel up to date (persisted)
local layout  = nil       -- { map=, at={num->{x,y,z}}, cells=, stretched=, id= }
local view_z  = nil       -- level being looked at, nil = follow the player
local draw_x0, draw_y0, draw_z = 0, 0, 0   -- viewport origin at the last draw
local forget_adjacency, draw               -- defined with the drawing, used by the feed
local walk_stop, walk_arrived              -- defined with the walking, used by the feed
local held_by = nil       -- transient hold via the "map.hold" event (never persisted):
                          -- another plugin owns movement in unmappable space right now
local in_sea  = false     -- last arrival was a Sea of Chaos layer; kept so entering and
                          -- leaving are each said once rather than once per room
local refresh_maplist                      -- defined with the panel, called by the drawing
local refresh_favlist                      -- likewise, for the Favs tab
local fav_toggle                           -- defined with the panel, used by the alias too
local now     = 0         -- seconds, advanced by the 1 s tick (the flush rides every 15th)

local function note(s) scrye.print("[mapg] " .. s) end

-- ---------- directions ----------
-- Compass points first and in compass order, then everything else.
-- Not tidiness: a compass exit reverses (north out is south back, almost
-- always) and a special one need not -- 'in' does not have to come back
-- as 'out'. The split in the list is the split between what a mapper may
-- reason about in both directions and what it must walk to learn.
local COMPASS = { "n", "ne", "e", "se", "s", "sw", "w", "nw", "u", "d" }
local COMPASS_AT = {}
for i, d in ipairs(COMPASS) do COMPASS_AT[d] = i end

-- What a typed command means as an exit key. The keys on the right are
-- the ones the server uses, so a match is a match without translation.
local MOVE = {
  n = "n", north = "n", s = "s", south = "s", e = "e", east = "e",
  w = "w", west = "w", ne = "ne", northeast = "ne", nw = "nw", northwest = "nw",
  se = "se", southeast = "se", sw = "sw", southwest = "sw",
  u = "u", up = "u", d = "d", down = "d",
  ["in"] = "in", out = "out", enter = "enter", exit = "out",
}

local function sorted_dirs(exits)
  local list = {}
  for dir in pairs(exits or {}) do list[#list + 1] = dir end
  table.sort(list, function(a, b)
    local ra, rb = COMPASS_AT[a], COMPASS_AT[b]
    if ra and rb then return ra < rb end
    if ra then return true end
    if rb then return false end
    return a < b
  end)
  return list
end

local function count(t)
  local n = 0
  for _ in pairs(t or {}) do n = n + 1 end
  return n
end

local function name_of(num)
  local r = rooms[num]
  if not r then return "room " .. tostring(num) end
  return r.name ~= "" and r.name or ("room " .. tostring(num))
end

local function area_of(r) return (r and r.area ~= "" and r.area) or "?" end

-- Where a direction leads, as far as we know, and how we know.
--   "walked" beats "told": if the server said 0 and we went there anyway,
--   we know better than the thing that declined to say.
local function link(r, dir)
  -- A shifting exit has no destination worth reporting: whatever we walked or
  -- were told is one ride's answer, not the exit's. Everything downstream --
  -- routes, layout, the farmer's handover -- treats it as no link at all.
  if r.shift and r.shift[dir] then return nil, "shift" end
  local w = r.walked and r.walked[dir]
  if w and w ~= 0 then return w, "walked" end
  local t = r.exits and r.exits[dir]
  if t and t ~= 0 then return t, "told" end
  return nil, nil
end

-- Every way out of a room, resolved to a destination number. (Resolved is not the same as
-- explored -- see frontier_dirs.)
local function neighbours(r)
  local out = {}
  for _, dir in ipairs(sorted_dirs(r.exits)) do
    local dest = link(r, dir)
    if dest then out[#out + 1] = { dir = dir, to = dest } end
  end
  for _, dir in ipairs(sorted_dirs(r.walked)) do   -- walked a way not listed
    if not (r.exits and r.exits[dir]) and not (r.shift and r.shift[dir]) then
      out[#out + 1] = { dir = dir, to = r.walked[dir] }
    end
  end
  return out
end

-- The exits still worth taking. Two different servers' habits meet here: in unmappable
-- space the destination of an unwalked exit is WITHHELD (0) -- that was the original
-- definition of the frontier -- but in the ordinary world the server names the destination
-- of every exit whether you have walked it or not. Under the original definition nothing
-- out there was ever unexplored, and 'mapg explore' stood in a town full of unvisited doors
-- saying nothing was reachable. An exit is unexplored when the room BEHIND it is not in the
-- store, however much the server was willing to say about where it leads.
local function frontier_dirs(r)
  local out = {}
  for _, dir in ipairs(sorted_dirs(r.exits)) do
    -- A shifting exit is never unexplored: there is nothing behind it to LEARN,
    -- only somewhere to be taken. Without this guard link()'s nil would make it
    -- eternal frontier and the explorer would ride the elevator forever.
    if not (r.shift and r.shift[dir]) then
      local dest = link(r, dir)
      if not dest or not rooms[dest] then out[#out + 1] = dir end
    end
  end
  return out
end

-- "nw>50940  sw>50943  in>50943", with the cases worth seeing called out.
local function describe_exits(r, num)
  local dirs = sorted_dirs(r.exits)
  if #dirs == 0 then return "(no exits listed - they may be hidden)" end
  local parts = {}
  for _, dir in ipairs(dirs) do
    local dest, how = link(r, dir)
    local shown
    if how == "shift" then
      shown = "~"                                    -- marked shifting: no ONE destination
    elseif not dest then
      shown = "?"                                    -- a way out, destination withheld
    elseif num and dest == num then
      shown = tostring(dest) .. "(self)"
    else
      shown = tostring(dest) .. (how == "walked" and "*" or "")
    end
    parts[#parts + 1] = dir .. ">" .. shown
  end
  return table.concat(parts, "  ")
end

local function exits_differ(a, b)
  for dir, dest in pairs(a or {}) do if (b or {})[dir] ~= dest then return true end end
  for dir, dest in pairs(b or {}) do if (a or {})[dir] ~= dest then return true end end
  return false
end

-- ---------- persistence ----------
-- Rooms are saved as an ARRAY of records with the number inside, not as an
-- object keyed by number: JSON object keys are strings, so the obvious
-- shape would silently turn every room id into "50942" and back again.
local function save(force)
  if not (dirty or force) then return end
  local list = {}
  for num, r in pairs(rooms) do
    list[#list + 1] = { num = num, name = r.name, area = r.area,
                        exits = r.exits, walked = r.walked, visits = r.visits,
                        shift = r.shift, vary = r.vary }
  end
  local names = {}
  for seed, nm in pairs(mapnames) do names[#names + 1] = { seed = seed, name = nm } end
  WORLD.set(STORE_KEY, scrye.json.encode{ ver = STORE_VER, rooms = list, names = names,
                                          favs = favs })
  dirty = false
  return #list
end

local function load()
  -- One-time migration: a map saved before scrye.shared existed sits in the private
  -- store. If the shared store is real and empty while the private one has a map, adopt
  -- it - the private copy is left in place as a backup (nothing writes it again).
  if scrye.shared and scrye.shared.get(STORE_KEY) == nil then
    local old = scrye.store.get(STORE_KEY)
    if old ~= nil then
      scrye.shared.set(STORE_KEY, old)
      note("map moved to the MUD-shared store - every character on this world sees it now")
    end
  end
  local raw = WORLD.get(STORE_KEY)
  if not raw then return end
  local ok, data = pcall(scrye.json.decode, raw)
  if not ok or type(data) ~= "table" or type(data.rooms) ~= "table" then
    note("saved rooms could not be read - starting empty, and NOT overwriting the file yet")
    note("  'mapg save' will overwrite it once you are happy to lose whatever was there")
    return
  end
  for _, r in ipairs(data.rooms) do
    local num = tonumber(r.num)
    if num then
      rooms[num] = { name = tostring(r.name or ""), area = tostring(r.area or ""),
                     exits = type(r.exits) == "table" and r.exits or {},
                     walked = type(r.walked) == "table" and r.walked or {},
                     visits = tonumber(r.visits) or 0 }
      -- only kept when non-empty, so an old save (no such fields) loads unchanged
      if type(r.shift) == "table" and next(r.shift) ~= nil then rooms[num].shift = r.shift end
      if type(r.vary) == "table" and next(r.vary) ~= nil then rooms[num].vary = r.vary end
      known = known + 1
    end
  end
  for _, n in ipairs(type(data.names) == "table" and data.names or {}) do
    local seed = tonumber(n.seed)
    if seed and type(n.name) == "string" and n.name ~= "" then mapnames[seed] = n.name end
  end
  for _, f in ipairs(type(data.favs) == "table" and data.favs or {}) do
    local num = tonumber(f)
    if num and #favs < FAV_CAP then favs[#favs + 1] = num end
  end
end

-- ---------- the move queue ----------
-- One entry per movement command sent, in send order, each remembering the
-- room it was sent FROM. Pairing a move with the room that followed it is
-- how a link gets learned -- and the 'from' is what makes a wrong pairing
-- detectable instead of silently written down.
local function queue_move(dir)
  moves[#moves + 1] = { dir = dir, from = here, at = now }
end

local function prune_moves()
  while moves[1] and (now - moves[1].at) > MOVE_TTL do table.remove(moves, 1) end
end

-- ---------- the feed ----------
local function on_room_info(json)
  msgs = msgs + 1
  if quiet_timer then scrye.cancel(quiet_timer); quiet_timer = nil end

  local r = scrye.json.decode(json)
  if type(r) ~= "table" then
    note("Room.Info arrived but did not decode as an object: " .. tostring(json))
    return
  end

  local num = tonumber(r.num)
  if not num then
    note("Room.Info with no usable 'num' - " .. tostring(json))
    return
  end

  local name  = tostring(r.name or "")
  local area  = tostring(r.area or "")
  local exits = type(r.exits) == "table" and r.exits or {}

  -- ---- two reasons not to map this arrival ----
  --
  -- A HOLD: another plugin owns movement in unmappable space. Same event and same meaning
  -- as the shipped 3s-map's, so the chaos-sea explorer holds both mappers with one emit and
  -- neither has to know the other exists.
  --
  -- Or the room is a SEA OF CHAOS LAYER, which is unmappable whether anything is holding or
  -- not -- you can walk in there yourself with no bot running. Every layer room on every
  -- layer is num 60494, so recording them would not build a wrong map; it would build ONE
  -- room wearing self-links in every direction you were ever seen to walk, parked in the
  -- Unknown grid beside the main town. Matched by NAME, because the number is exactly what
  -- cannot tell them apart -- that is the whole problem.
  --
  -- The sea's ENTRANCE ("A swirling Sea of Chaos", num 266, area "The Sea of Chaos") is a
  -- real, stable room with a real exit, and stays mapped: it is how you find the sea.
  local sea = name:match("^[Ll]ayer%s+%w+%s+of%s+the%s+Sea%s+of%s+Chaos") ~= nil
  if sea ~= in_sea then
    in_sea = sea
    note(sea and "Sea of Chaos - not mapping in here: every layer room is the same number"
              or "left the Sea of Chaos - mapping again")
  end
  if held_by or sea then
    -- Forget where we were, exactly as a reconnect does. Otherwise the first arrival back in
    -- real space pairs with the move that took us in, and a link gets learned across ground
    -- nobody mapped -- the sea stitched onto Midgard by a door that does not exist.
    here = nil
    moves = {}
    dirty = true
    return
  end

  local from = here
  local prev = rooms[num]
  -- A shifting exit's TOLD destination is one ride's noise -- the elevator names
  -- whichever floor it happens to be at -- so storing it would flag EXITS CHANGED
  -- on every visit and re-teach the lie the mark exists to unlearn. Neutered to 0
  -- (a way out, destination unknown); the mark itself still draws it as '~'.
  if prev and prev.shift then
    for d in pairs(prev.shift) do if exits[d] ~= nil then exits[d] = 0 end end
  end
  local changed = prev and exits_differ(prev.exits, exits)

  local shape_changed = (not prev) or changed or (prev and prev.area ~= area)
  if not prev then
    known = known + 1
    stats.new = stats.new + 1
    rooms[num] = { name = name, area = area, exits = exits, walked = {}, visits = 1 }
  else
    prev.name, prev.area, prev.exits = name, area, exits
    prev.visits = prev.visits + 1
  end
  here = num
  dirty = true
  if shape_changed then forget_adjacency() end

  -- Pair this arrival with the move that caused it.
  prune_moves()
  local mv = table.remove(moves, 1)
  local learned, contradiction, rode_shift, auto_shift = nil, nil, false, false
  if mv then
    if mv.from ~= from then
      -- Our idea of where that move started is not where we actually were.
      -- A move failed, or something moved us. Either way the pairing is
      -- worthless: drop the whole queue rather than write a wrong link.
      stats.desyncs = stats.desyncs + 1
      moves = {}
      if talking then
        note("out of step - a move did not land where the queue expected, so nothing was learned from it")
      end
    elseif from and rooms[from] then
      local src = rooms[from]
      local told = src.exits and src.exits[mv.dir]
      src.walked = src.walked or {}
      if src.shift and src.shift[mv.dir] then
        -- Marked shifting: the ride is real -- 'here' has already moved -- but the
        -- link would be one ride's lie, so nothing is learned from it.
        rode_shift = true
      elseif src.walked[mv.dir] ~= num then
        local old = src.walked[mv.dir]
        src.walked[mv.dir] = num
        stats.walked = stats.walked + 1
        learned = (told == nil or told == 0)
        forget_adjacency()          -- a new edge changes what connects to what
        -- Our own feet have now seen this exit end somewhere NEW. Once is a
        -- correction and the fresh walk deserves the belief it just got; twice is
        -- an elevator (Joakim's Megacity lift, 28 Aug 2026: 44346's 's' names a
        -- different floor's lobby every ride). At twice the exit is marked
        -- shifting automatically and stops being believed at all.
        if old and old ~= 0 then
          src.vary = src.vary or {}
          src.vary[mv.dir] = (src.vary[mv.dir] or 0) + 1
          if src.vary[mv.dir] >= 2 then
            src.shift = src.shift or {}
            src.shift[mv.dir] = true
            src.walked[mv.dir] = nil
            if src.exits and src.exits[mv.dir] ~= nil then src.exits[mv.dir] = 0 end
            src.vary[mv.dir] = nil
            if next(src.vary) == nil then src.vary = nil end
            auto_shift = true
            forget_adjacency()
          end
        end
      end
      -- No DISAGREES for a shifting exit: disagreement is its nature, not news --
      -- and the ride that just triggered the auto-mark said something better.
      if told and told ~= 0 and told ~= num and not (rode_shift or auto_shift) then
        stats.contradictions = stats.contradictions + 1
        contradiction = told
      end
    end
  end

  if talking then
    note(string.format("%-6d %-34s [%s]  %s%s",
      num,
      name ~= "" and name or "(no name)",
      area ~= "" and area or "?",
      describe_exits(rooms[num], num),
      prev and (changed and "   EXITS CHANGED" or "   (again)") or "   NEW"))
    if learned and from then
      note(string.format("  learned %d %s> %d - the server would not say where that went",
                         from, mv.dir, num))
    end
    if contradiction and from then
      note(string.format("  DISAGREES: %d says %s leads to %d, but walking it arrived at %d",
                         from, mv.dir, contradiction, num))
      note("  a shifting exit, a portal, or a randomly generated map. The walk is believed.")
      note("  (an elevator or portal? 'mapg shift " .. from .. " " .. mv.dir
           .. "' stops it being believed at all)")
    end
    if rode_shift and from then
      note(string.format("  %s from %d is marked shifting - arrived at %d, nothing recorded",
                         mv.dir, from, num))
    end
    if auto_shift and from then
      note(string.format("  %s from %d has now ended somewhere new twice - marked SHIFTING", mv.dir, from))
      note("  an elevator or a portal: ride it yourself; routes will not use it"
           .. " ('mapg shift " .. from .. " " .. mv.dir .. " off' undoes this)")
    end
  end

  view_z = nil          -- moving means we care about our own level again
  draw()
  -- The position oracle for other plugins: every mapped arrival, exactly as stored. Held
  -- and sea arrivals never reach this line, so the feed goes quiet exactly when the map
  -- does. Emitted before walk_arrived so a listener hears the room before any step the
  -- walker takes because of it.
  scrye.emit("map.room", scrye.json.encode({
    num = num, name = name, area = area, exits = exits,
  }))
  walk_arrived()        -- last, so 'here' and the store are already correct
end

local function on_command(cmd)
  local word = tostring(cmd or ""):gsub("^%s+", ""):gsub("%s+$", ""):lower()
  local dir = MOVE[word]
  -- The server NAMES nonstandard exits in Room.Info ("exit", "vortex", "portal"...), and
  -- typing one of those names from this room IS a move - the room's own feed says so. Pair
  -- it like any compass step, or the door only ever gets learned one way and the map ends
  -- up with islands no route crosses (Joakim's roads, 25 Aug: every road<->area crossing
  -- was such a door, and 'walk to that map' could not leave the roads). Only words the
  -- CURRENT room's exits table names qualify - a guess ("look"? "flee"?) queued here would
  -- pair with the next real arrival and write a link nobody walked.
  if not dir and here and rooms[here] and rooms[here].exits and rooms[here].exits[word] ~= nil then
    dir = word
  end
  if dir then queue_move(dir) end
  if walk and dir then
    if walk.sent == dir then
      walk.sent = nil                    -- that was our own step going out
    else
      -- You moved while we were walking. Two hands on the wheel is how a
      -- walker ends up somewhere nobody chose; yours wins.
      walk_stop("you moved yourself - walk stopped where you are")
    end
  end
end

-- ---------- search ----------
-- Breadth-first over number -> number links: the shortest known route, in
-- steps. Returns a list of { dir, to } or nil plus why not.
local function bfs(from, want)
  if not rooms[from] then return nil, "we are not in a room this store knows" end
  local prev, seen, queue, touched = {}, { [from] = true }, { from }, 0
  local head = 1
  while head <= #queue do
    local at = queue[head]; head = head + 1
    touched = touched + 1
    if touched > BFS_CAP then return nil, "search got too large" end
    if want(at, rooms[at]) and at ~= from then
      local steps, cur = {}, at
      while cur ~= from do
        local p = prev[cur]
        table.insert(steps, 1, { dir = p.dir, to = cur })
        cur = p.from
      end
      return steps
    end
    for _, e in ipairs(neighbours(rooms[at])) do
      if rooms[e.to] and not seen[e.to] then
        seen[e.to] = true
        prev[e.to] = { from = at, dir = e.dir }
        queue[#queue + 1] = e.to
      end
    end
  end
  return nil
end

local function show_path(steps)
  local dirs = {}
  for _, s in ipairs(steps) do dirs[#dirs + 1] = s.dir end
  note(#steps .. " step(s): " .. table.concat(dirs, " "))
  for i, s in ipairs(steps) do
    note(string.format("  %2d. %-5s -> %-6d %s", i, s.dir, s.to, name_of(s.to)))
  end
  note("nothing was sent - phase 1 prints the route, you walk it")
end

-- ---------- maps: area x connected component ----------
-- The adjacency the LAYOUT uses. Compass links only, in both directions: a
-- drawing may place X west of Y because Y says X is east, even though the
-- store never claims you can walk back that way. Positioning is not a claim
-- about walkability -- that distinction is exactly why the store keeps them
-- apart and the picture is free to be generous.
local adj_cache = nil
-- Declared here, above the function that clears them: a local declared after
-- its clearer would leave the clearer assigning to a global of the same name,
-- and the cache would silently never invalidate.
local maps_cache, label_cache = nil, nil
local function adjacency()
  if adj_cache then return adj_cache end
  local fwd, back = {}, {}
  for num, r in pairs(rooms) do
    for _, e in ipairs(neighbours(r)) do
      if DELTA[e.dir] and rooms[e.to] then
        fwd[num] = fwd[num] or {} ; fwd[num][#fwd[num] + 1] = { dir = e.dir, to = e.to }
        back[e.to] = back[e.to] or {} ; back[e.to][#back[e.to] + 1] = { dir = e.dir, from = num }
      end
    end
  end
  adj_cache = { fwd = fwd, back = back }
  return adj_cache
end
forget_adjacency = function() adj_cache = nil ; layout = nil ; maps_cache = nil ; label_cache = nil end

-- Everything reachable from `start` without leaving its area, over compass
-- links either way. This is one map.
local function component_of(start)
  if not rooms[start] then return {}, 0, "?" end
  local area = area_of(rooms[start])
  local a = adjacency()
  local set, queue, head, n = { [start] = true }, { start }, 1, 1
  while head <= #queue do
    local at = queue[head] ; head = head + 1
    for _, e in ipairs(a.fwd[at] or {}) do
      if not set[e.to] and rooms[e.to] and area_of(rooms[e.to]) == area then
        set[e.to] = true ; n = n + 1 ; queue[#queue + 1] = e.to
      end
    end
    for _, e in ipairs(a.back[at] or {}) do
      if not set[e.from] and rooms[e.from] and area_of(rooms[e.from]) == area then
        set[e.from] = true ; n = n + 1 ; queue[#queue + 1] = e.from
      end
    end
  end
  return set, n, area
end

-- Every map, as { area, seed (lowest room number), size, label }. The seed is
-- what names a map when an area has more than one piece, so the label is
-- stable between sessions rather than depending on where you happened to walk.
-- Cached: this is recomputed on every step otherwise, and it walks every room.
local function all_maps()
  if maps_cache then return maps_cache end
  local done, out = {}, {}
  local nums = {}
  for num in pairs(rooms) do nums[#nums + 1] = num end
  table.sort(nums)
  for _, num in ipairs(nums) do
    if not done[num] then
      local set, n, area = component_of(num)
      for m in pairs(set) do done[m] = true end
      out[#out + 1] = { area = area, seed = num, size = n, set = set }
    end
  end
  -- number the pieces only where an area actually has more than one
  local pieces = {}
  for _, m in ipairs(out) do pieces[m.area] = (pieces[m.area] or 0) + 1 end
  local seen_of = {}
  for _, m in ipairs(out) do
    if pieces[m.area] > 1 then
      seen_of[m.area] = (seen_of[m.area] or 0) + 1
      m.auto = string.format("%s #%d", m.area, seen_of[m.area])
    else
      m.auto = m.area
    end
    -- A name you gave replaces the generated one everywhere: the map list, the
    -- panel, and other maps' borders. "Unknown #1" and "Unknown #2" are correct
    -- and forgettable; "Main town" and "Chaos realm" are what you actually
    -- know. Keyed by the map's seed room, so it survives restarts and does not
    -- move when you walk.
    m.label = mapnames[m.seed] or m.auto
  end
  maps_cache = out
  return out
end

-- Which map each room belongs to, by label.
local function label_of()
  if label_cache then return label_cache end
  local idx = {}
  for _, m in ipairs(all_maps()) do
    for num in pairs(m.set) do idx[num] = m.label end
  end
  label_cache = idx
  return idx
end

-- What a map touches, named by MAP rather than by area. Naming by area is
-- the obvious version and it is useless exactly where it matters: the whole
-- point of the borders line is to tell one piece of "Unknown" from another,
-- and "Unknown #1 borders Unknown" answers nothing. "borders Unknown #2"
-- names which one, so the pieces can be followed as a chain.
local function borders_of(set)
  -- A map cannot list itself: 'not set[e.to]' already excludes every room it
  -- contains, and labels are one-per-map, so there is no second name for the
  -- map we are standing on. No separate self-check is needed and adding one
  -- would imply a case that cannot arise.
  local idx = label_of()
  local names, list = {}, {}
  for num in pairs(set) do
    for _, e in ipairs(neighbours(rooms[num])) do
      local nr = rooms[e.to]
      if nr and not set[e.to] then
        local lbl = idx[e.to] or area_of(nr)
        if not names[lbl] then names[lbl] = true ; list[#list + 1] = lbl end
      end
    end
  end
  table.sort(list)
  return list
end

-- ---------- layout ----------
-- Coordinates are computed here and nowhere else. Nothing is ever LOOKED UP
-- by position, so a wrong cell is a smudge on the drawing rather than a
-- wrong room, and 'mapg redraw' is a complete repair.
local function cell_key(x, y, z) return x .. "," .. y .. "," .. z end

-- A free cell near (x,y) on level z. MUD geometry is not Euclidean -- going
-- n,e,s,w can land you somewhere new -- so two rooms wanting one cell is
-- normal, not a bug. Push the newcomer to the nearest empty spot and mark it,
-- rather than dropping it or drawing a lie without saying so.
--
-- The search is bounded by the number of rooms already placed, which is
-- always enough: a square of side 2r+1 holds (2r+1)^2 cells, so once r
-- passes the placed count there are provably more cells than rooms and one
-- of them is free. Bounding it by a fixed ring instead would let a dense
-- map fail to place a room at all -- including, eventually, the room the
-- player is standing in, which the drawing then cannot draw.
local function free_near(cells, x, y, z, placed)
  for ring = 1, placed + 1 do
    for dx = -ring, ring do
      for dy = -ring, ring do
        if math.abs(dx) == ring or math.abs(dy) == ring then
          if not cells[cell_key(x + dx, y + dy, z)] then return x + dx, y + dy end
        end
      end
    end
  end
  return nil   -- unreachable given the bound above
end

local function build_layout(seed)
  local set, size, area = component_of(seed)
  local a = adjacency()
  local at, cells, displaced, placed = {}, {}, 0, 1
  at[seed] = { x = 0, y = 0, z = 0 }
  cells[cell_key(0, 0, 0)] = seed
  local queue, head = { seed }, 1

  local function place(from, dir, to, sign)
    if at[to] or not set[to] then return end
    local dv = DELTA[dir] ; if not dv then return end
    local p = at[from]
    local x, y, z = p.x + dv[1] * sign, p.y + dv[2] * sign, p.z + dv[3] * sign
    local off = false
    if cells[cell_key(x, y, z)] then
      local nx, ny = free_near(cells, x, y, z, placed)
      if not nx then return end          -- cannot happen; see free_near
      x, y, off = nx, ny, true
      displaced = displaced + 1
    end
    at[to] = { x = x, y = y, z = z, off = off }
    cells[cell_key(x, y, z)] = to
    placed = placed + 1
    queue[#queue + 1] = to
  end

  while head <= #queue do
    local from = queue[head] ; head = head + 1
    for _, e in ipairs(a.fwd[from] or {}) do place(from, e.dir, e.to, 1) end
    for _, e in ipairs(a.back[from] or {}) do place(from, e.dir, e.from, -1) end
  end

  local placed = 0
  for _ in pairs(at) do placed = placed + 1 end
  return { area = area, seed = seed, set = set, at = at, cells = cells,
           size = size, placed = placed, displaced = displaced }
end

-- The layout is seeded from the map's LOWEST room number, not from wherever
-- you happen to be standing. Seeding at the player would make every
-- coordinate -- and so every level number -- relative to them, shifting the
-- whole map underfoot each time they move and making "level 2" mean
-- something different one room later. The lowest number is arbitrary but
-- stable, and it is the same seed 'mapg maps' names the map by.
local function current_layout()
  if not here or not rooms[here] then return nil end
  if layout and layout.set[here] then return layout end
  local set = component_of(here)
  local seed = here
  for num in pairs(set) do if num < seed then seed = num end end
  layout = build_layout(seed)
  -- The stable seed is a preference, not a requirement. If the player did
  -- not get placed, seeding at them does place them (they become the
  -- origin), and a map you are on beats a map with tidier level numbers.
  if not layout.at[here] then layout = build_layout(here) end
  return layout
end

-- ---------- drawing ----------
draw = function()
  if not drawing then return end
  local L = current_layout()
  if not L then
    scrye.setState(P .. "grid", "")
    scrye.setState(P .. "status", here and "no layout yet" or "not anywhere yet")
    -- The Maps and Favs tabs are lists, not layouts: they must not go stale just
    -- because there is nothing to draw a grid FROM - a wipe lands exactly here, and
    -- lists still showing the erased maps would be a lie.
    if refresh_maplist then refresh_maplist() end
    return
  end
  local me = L.at[here]
  local z = view_z or me.z
  draw_x0 = me.x - HALF_C
  draw_y0 = me.y + HALF_R
  draw_z  = z

  local grid = {}
  for row = 1, TROWS do
    local line = {}
    for col = 1, TCOLS do
      line[col] = (row % 2 == 1 and col % 2 == 1) and "." or " "
    end
    grid[row] = line
  end
  local function cell_of(x, y) return 2 * (x - draw_x0) + 1, 2 * (draw_y0 - y) + 1 end
  local function put(x, y, ch, weak)
    local col, row = cell_of(x, y)
    if col < 1 or col > TCOLS or row < 1 or row > TROWS then return end
    if weak and grid[row][col] ~= "." then return end   -- a frontier never covers a room
    grid[row][col] = ch
  end
  local function put_edge(x, y, dv)
    local col, row = cell_of(x, y)
    col = col + dv[1] ; row = row - dv[2]
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

  for num, p in pairs(L.at) do
    if p.z == z then
      local r = rooms[num]
      local up, down, door = false, false, false
      for _, dir in ipairs(sorted_dirs(r.exits)) do
        if dir == "u" then up = true elseif dir == "d" then down = true
        elseif not DELTA[dir] then door = true end
      end
      for _, e in ipairs(neighbours(r)) do
        if rooms[e.to] and not L.set[e.to] then door = true end   -- leads off this map
      end
      local ch = "#"
      if up and down then ch = "%" elseif up then ch = "^" elseif down then ch = "v" end
      if door then ch = ">" end
      if p.off then ch = "!" end        -- the layout could not honour its links
      put(p.x, p.y, ch, false)

      for _, dir in ipairs(sorted_dirs(r.exits)) do
        local dv = DELTA[dir]
        if dv and dv[3] == 0 then
          local dest = link(r, dir)
          local dp = dest and L.at[dest]
          -- Draw a connector only where the drawing is telling the truth: the
          -- neighbour really is in the cell that direction points at. A link to
          -- a displaced room would otherwise be a line to the wrong place.
          if dp and dp.z == z and dp.x == p.x + dv[1] and dp.y == p.y + dv[2] then
            put_edge(p.x, p.y, dv)
          elseif not dest then
            put_edge(p.x, p.y, dv)
            put(p.x + dv[1], p.y + dv[2], "?", true)   -- a way out, destination withheld
          end
        end
      end
    end
  end
  if me.z == z then put(me.x, me.y, "@", false) end

  local lines = {}
  for row = 1, TROWS do lines[row] = table.concat(grid[row]) end
  scrye.setState(P .. "grid", table.concat(lines, "\n"))

  local maps = all_maps()
  local label = L.area
  for _, m in ipairs(maps) do if m.seed == L.seed or m.set[here] then label = m.label break end end
  local bits = { label, L.placed .. " room(s)" }
  if L.displaced > 0 then bits[#bits + 1] = L.displaced .. " displaced (!)" end
  if view_z then bits[#bits + 1] = "level " .. z .. (z == me.z and "" or " (not yours)") end
  scrye.setState(P .. "status", table.concat(bits, "  -  "))
  scrye.setState(P .. "where", string.format("%d  %s", here, name_of(here)))
  if refresh_maplist then refresh_maplist() end
end

-- ---------- walking ----------
-- The contract, in one place, because a plugin that can move your character
-- has to be answerable about when it will:
--   * it walks only a route you asked for, by name or by clicking a room;
--   * one step at a time, the next sent only when Room.Info confirms the
--     last one landed in the room the route expected;
--   * anything unexpected -- a different room, no room at all within
--     WALK_WAIT, a move you typed yourself, the idle guard, a disconnect --
--     stops it, and it never resumes on its own;
--   * 'mapg stop' stops it, always.
local walk_timer = nil

walk_stop = function(why)
  if not walk then return end
  walk = nil
  if walk_timer then scrye.cancel(walk_timer) ; walk_timer = nil end
  if why then
    note(why)
    -- Stopped-with-a-reason is the abnormal end; a finished walk emits map.walk.arrived
    -- from walk_step instead. A listener that requested the walk gets exactly one of the
    -- two, never both.
    scrye.emit("map.walk.stopped", scrye.json.encode({ reason = why }))
  end
  draw()
end

local function walk_arm_watchdog()
  if walk_timer then scrye.cancel(walk_timer) end
  walk_timer = scrye.after(WALK_WAIT, function()
    walk_timer = nil
    if not walk then return end
    walk_stop(string.format(
      "step %d ('%s') never landed - nothing arrived in %ds. Walk stopped where you stand.",
      walk.idx, walk.steps[walk.idx] and walk.steps[walk.idx].dir or "?", WALK_WAIT))
    note("  a closed door, something blocking the way, or a room the server did not announce")
  end)
end

local function walk_step()
  if not walk then return end
  local st = walk.steps[walk.idx]
  if not st then
    local n = #walk.steps
    walk_stop(nil)
    note(string.format("arrived - %d step(s), %d %s", n, here, name_of(here)))
    scrye.emit("map.walk.arrived", scrye.json.encode({ num = here }))
    return
  end
  walk.sent = st.dir              -- so on_command knows this one was ours
  walk_arm_watchdog()
  scrye.send(st.dir)
end

local function walk_begin(steps, what)
  if #steps == 0 then note("you are already there"); return end
  if #steps > WALK_CAP then
    note(string.format("that route is %d steps; the cap is %d. Walk part of the way first.",
                       #steps, WALK_CAP))
    return
  end
  walk = { steps = steps, idx = 1, target = steps[#steps].to }
  local dirs = {}
  for _, st in ipairs(steps) do dirs[#dirs + 1] = st.dir end
  note(string.format("walking to %s - %d step(s): %s", what, #steps, table.concat(dirs, " ")))
  scrye.emit("map.walk.started", scrye.json.encode({ target = walk.target, steps = #steps }))
  note("  'mapg stop' stops it; so does moving yourself, or anything unexpected")
  walk_step()
end

-- Called from the feed once the arrival has been recorded, so 'here' is the
-- room we are actually in and the store already knows about it.
walk_arrived = function()
  if not walk then return end
  local st = walk.steps[walk.idx]
  if not st then return end
  if here ~= st.to then
    -- The step went somewhere else. No other walker can see this happen.
    walk_stop(string.format(
      "step %d ('%s') should have reached %d %s, but we are in %d %s. Walk stopped.",
      walk.idx, st.dir, st.to, name_of(st.to), here, name_of(here)))
    note("  the route is out of date, or something moved you. 'mapg path' will re-plan from here.")
    return
  end
  walk.idx = walk.idx + 1
  walk_step()
end

-- ---------- commands ----------
local function status()
  note("phase 1 - rooms keyed by number, links number to number, sending nothing")
  note(string.format("  %d room(s) known, %d new this session, %d Room.Info message(s)",
                     known, stats.new, msgs))
  if here and rooms[here] then
    local r = rooms[here]
    note(string.format("  here: %d  %s  [%s]", here, name_of(here), area_of(r)))
    note("  exits: " .. describe_exits(r, here))
  elseif msgs == 0 then
    note("  nothing has arrived. '.gmcp' will say whether GMCP is negotiated at all.")
  end
  if walk then
    note(string.format("  WALKING step %d of %d, to %d %s  ('mapg stop')",
                       walk.idx, #walk.steps, walk.target, name_of(walk.target)))
  end
  note(string.format("  links walked %d, disagreements %d, out-of-step %d",
                     stats.walked, stats.contradictions, stats.desyncs))
  local shifts = 0
  for _, r in pairs(rooms) do shifts = shifts + count(r.shift) end
  if shifts > 0 then
    note("  " .. shifts .. " exit(s) marked shifting - 'mapg shift' lists them")
  end
  note("  commentary is " .. (talking and "ON" or "off") .. "  ('mapg on' / 'mapg off')")
end

local function areas()
  local names, n = {}, {}
  for _, r in pairs(rooms) do
    local a = area_of(r)
    if not n[a] then names[#names + 1] = a; n[a] = 0 end
    n[a] = n[a] + 1
  end
  if #names == 0 then note("no rooms known yet"); return end
  table.sort(names)
  note(known .. " room(s) across " .. #names .. " area(s):")
  for _, a in ipairs(names) do note(string.format("  %-28s %d", a, n[a])) end
end

local function rooms_list(filter)
  local nums = {}
  for num, r in pairs(rooms) do
    if not filter or area_of(r):lower():find(filter, 1, true) then nums[#nums + 1] = num end
  end
  if #nums == 0 then
    note(filter and ("no rooms known in an area matching '" .. filter .. "'") or "no rooms known yet")
    return
  end
  table.sort(nums)
  note(#nums .. " room(s)" .. (filter and (" in areas matching '" .. filter .. "'") or "") .. ":")
  for i, num in ipairs(nums) do
    if i > LIST_CAP then note("  ... and " .. (#nums - LIST_CAP) .. " more"); break end
    local r = rooms[num]
    note(string.format("  %-6d %-34s [%s]  %d exit(s)  x%d",
      num, name_of(num), area_of(r), count(r.exits), r.visits))
  end
end

local function room_detail(num)
  local r = rooms[num]
  if not r then note("room " .. num .. " is not in the store"); return end
  note(string.format("%d  %s  [%s]  seen %d time(s)", num, name_of(num), area_of(r), r.visits))
  local ns = neighbours(r)
  if #ns == 0 then note("  no known way out") end
  for _, e in ipairs(ns) do
    local _, how = link(r, e.dir)
    note(string.format("  %-5s -> %-6d %-30s (%s)", e.dir, e.to, name_of(e.to), how or "walked"))
  end
  local fr = frontier_dirs(r)
  if #fr > 0 then
    local parts = {}
    for _, dir in ipairs(fr) do
      local dest = link(r, dir)
      parts[#parts + 1] = dest and (dir .. ">" .. dest .. "?") or dir
    end
    note("  unexplored: " .. table.concat(parts, " ")
      .. "  (bare = destination withheld, >n? = told but never seen)")
  end
  local sh = sorted_dirs(r.shift)
  if #sh > 0 then
    note("  shifting: " .. table.concat(sh, " ")
      .. "  (ends somewhere different each ride - drawn ~, never routed through)")
  end
end

local function frontier()
  local out = {}
  for num, r in pairs(rooms) do
    local fr = frontier_dirs(r)
    if #fr > 0 then out[#out + 1] = { num = num, dirs = fr } end
  end
  if #out == 0 then
    note("no unexplored exits in " .. known .. " known room(s) - every listed way out leads to a mapped room")
    note("  which is not the same as every room being found: hidden exits are omitted entirely")
    return
  end
  table.sort(out, function(a, b) return a.num < b.num end)
  local total = 0
  for _, e in ipairs(out) do total = total + #e.dirs end
  note(total .. " unexplored exit(s) in " .. #out .. " room(s):")
  for i, e in ipairs(out) do
    if i > LIST_CAP then note("  ... and " .. (#out - LIST_CAP) .. " more rooms"); break end
    note(string.format("  %-6d %-34s %s", e.num, name_of(e.num), table.concat(e.dirs, " ")))
  end
end

-- Returns the route, or nil having said why. Shared by 'path' (print it) and
-- 'go' (walk it) so the two can never disagree about what the route is.
local function route_to(target)
  if not here then note("we are not anywhere yet - walk one room first"); return nil end
  if not rooms[target] then note("room " .. target .. " is not in the store"); return nil end
  if target == here then note("you are standing in it"); return nil end
  local steps, why = bfs(here, function(at) return at == target end)
  if not steps then
    note("no known route from " .. here .. " to " .. target .. (why and (" - " .. why) or ""))
    note("  the store only links rooms it was told about or walked; 'mapg frontier' is what is unexplored")
    return nil
  end
  return steps
end

local function path_to(target)
  local steps = route_to(target)
  if not steps then return end
  note(string.format("%d -> %d  (%s)", here, target, name_of(target)))
  show_path(steps)
end

-- The route to the nearest room with an exit nobody has walked. Returns nil
-- and says why when there is nothing to go to, or when we are already on one.
local function route_to_frontier()
  if not here then note("we are not anywhere yet - walk one room first"); return nil end
  if #frontier_dirs(rooms[here] or { exits = {} }) > 0 then
    note("unexplored exits right here: " .. table.concat(frontier_dirs(rooms[here]), " "))
    return nil
  end
  local steps = bfs(here, function(_, r) return #frontier_dirs(r) > 0 end)
  if not steps then
    note("nothing unexplored is reachable from here through known links")
    note("  which is not 'everything is found': hidden exits are omitted from the feed entirely")
    return nil
  end
  return steps
end

local function explore()
  local steps = route_to_frontier()
  if not steps then return end
  local dest = steps[#steps].to
  note(string.format("nearest unexplored: %d  %s  [%s] - %s",
       dest, name_of(dest), area_of(rooms[dest]), table.concat(frontier_dirs(rooms[dest]), " ")))
  show_path(steps)
end

-- Erasing everything is one keystroke away from erasing one room ('mapg
-- forget'), and the store is the only thing here that took real walking to
-- build. So it asks -- and the confirmation has to be typed in full rather
-- than being a bare 'y', which is the sort of thing a fingers-on-autopilot
-- reflex produces by accident.
local wipe_armed = false
local function wipe(confirmed)
  if known == 0 then note("the store is already empty"); return end
  if not confirmed then
    wipe_armed = true
    note(string.format("this will erase all %d room(s) and everything walked about them.", known))
    note("  'mapg wipe yes' to go ahead. Anything else cancels it.")
    return
  end
  if not wipe_armed then
    note("nothing to confirm - 'mapg wipe' first, so this cannot happen by mistyping")
    return
  end
  local n = known
  walk_stop(nil)    -- nothing left to walk to
  rooms, known, here = {}, 0, nil
  moves = {}
  mapnames = {}
  favs = {}                       -- favourites of maps that no longer exist would dangle
  stats = { told = 0, walked = 0, contradictions = 0, desyncs = 0, new = 0 }
  wipe_armed = false
  dirty = false
  WORLD.delete(STORE_KEY)         -- delete, not save-empty: nothing left behind
  scrye.store.delete(STORE_KEY)   -- and the pre-shared backup goes too: a wipe means gone
  forget_adjacency()
  draw()
  note("erased " .. n .. " room(s). Walk into a room and it starts again from there.")
end

local function name_map(text)
  local L = current_layout()
  if not L then note("we are not anywhere yet - walk one room first"); return end
  local seed = L.seed
  local auto
  for _, m in ipairs(all_maps()) do if m.set[here] then auto = m.auto ; seed = m.seed break end end
  if text == "" then
    note("this map is called '" .. (mapnames[seed] or auto or L.area) .. "'"
         .. (mapnames[seed] and (" (it would be '" .. tostring(auto) .. "')") or ""))
    note("  'mapg name <something>' renames it, 'mapg name -' puts it back")
    return
  end
  if text == "-" then
    if not mapnames[seed] then note("that map has no name of its own"); return end
    mapnames[seed] = nil
    dirty = true ; forget_adjacency() ; draw()
    note("name cleared - it is '" .. tostring(auto) .. "' again")
    return
  end
  mapnames[seed] = text
  dirty = true ; forget_adjacency() ; draw()
  note("this map is now '" .. text .. "'")
end

-- Maps sorted for READING: alphabetically by label, case-insensitive. The old
-- biggest-first order answered "what have I explored most", which nobody was asking;
-- finding an area by its name in a list of forty is the actual job.
local function sorted_maps()
  local maps = all_maps()
  table.sort(maps, function(a, b)
    local la, lb = tostring(a.label):lower(), tostring(b.label):lower()
    if la ~= lb then return la < lb end
    return a.seed < b.seed
  end)
  return maps
end

-- Does this map match the search text? By its own label, or by a BORDERING map's label -
-- the second half is what makes "chaos" list every area hanging off Chaos realm: an
-- area's realm is its neighbour, and the borders already carry the names you gave them.
local function map_matches(m, borders, f)
  if f == "" then return true end
  if tostring(m.label):lower():find(f, 1, true) then return true end
  for _, lbl in ipairs(borders) do
    if tostring(lbl):lower():find(f, 1, true) then return true end
  end
  return false
end

local function maps_list(filter)
  filter = tostring(filter or ""):gsub("^%s+", ""):gsub("%s+$", ""):lower()
  local maps = sorted_maps()
  if #maps == 0 then note("no rooms known yet"); return end
  local shown, total = 0, #maps
  local head_said = false
  for _, m in ipairs(maps) do
    local b = borders_of(m.set)
    if map_matches(m, b, filter) then
      shown = shown + 1
      if not head_said then
        head_said = true
        note((filter == "" and (total .. " map(s) over " .. known .. " room(s):")
                            or ("maps matching '" .. filter .. "' by name or border:")))
      end
      if shown > LIST_CAP then note("  ... and more") ; break end
      local mine = here and m.set[here] and " <- you are here" or ""
      local touch = #b > 0 and ("  borders " .. table.concat(b, ", ", 1, math.min(#b, 3))) or ""
      note(string.format("  %-30s %3d room(s)%s%s", m.label, m.size, touch, mine))
    end
  end
  if filter ~= "" then
    if shown == 0 then note("nothing matches '" .. filter .. "' by name or border") end
    return
  end
  note("an area in two unconnected pieces gets a map each - 'Unknown' is usually several,")
  note("because it labels every stretch of connective realm on the MUD, not one place")
end

-- ---------- shifting exits ----------
-- An elevator, a portal, a random door: an exit that genuinely ends somewhere
-- different each time it is taken. The ROOMS are never the problem -- each floor
-- is its own number and maps fine -- the LINK is the lie, so the mark lives on
-- the exit: marked shifting, it is never learned, never routed through, never
-- counted unexplored, and its told destination is neutered. You ride it
-- yourself; the map picks you up again on arrival, wherever that turns out to be.
local function shift_list()
  local out = {}
  for num, r in pairs(rooms) do
    for _, dir in ipairs(sorted_dirs(r.shift)) do out[#out + 1] = { num = num, dir = dir } end
  end
  if #out == 0 then
    note("no exits are marked shifting")
    note("  'mapg shift <dir>' marks one here, 'mapg shift <room> <dir>' anywhere;")
    note("  the map also marks one itself after walking it to two different places")
    return
  end
  table.sort(out, function(a, b)
    if a.num ~= b.num then return a.num < b.num end
    return a.dir < b.dir
  end)
  note(#out .. " shifting exit(s) - ridden, never routed:")
  for _, e in ipairs(out) do
    note(string.format("  %-6d %-34s %s", e.num, name_of(e.num), e.dir))
  end
end

local function shift_cmd(rest)
  local words = {}
  for w in tostring(rest or ""):lower():gmatch("%S+") do words[#words + 1] = w end
  if #words == 0 then shift_list() return end
  local num = tonumber(words[1])
  local i = num and 2 or 1
  num = num or here
  local dir, off = words[i], words[i + 1]
  if not num then note("we are not anywhere yet - 'mapg shift <room> <dir>' names one"); return end
  if not dir or (off and off ~= "off") or words[i + 2] then
    note("mapg shift [room] <dir> [off] - or bare 'mapg shift' to list them")
    return
  end
  dir = MOVE[dir] or dir            -- "south" means s; a special word stays itself
  local r = rooms[num]
  if not r then note("room " .. num .. " is not in the store"); return end
  if off then
    if not (r.shift and r.shift[dir]) then
      note(dir .. " from " .. num .. " is not marked shifting")
      return
    end
    r.shift[dir] = nil
    if next(r.shift) == nil then r.shift = nil end
    -- the evidence goes too, or one changed walk would re-mark it instantly
    if r.vary then r.vary[dir] = nil; if next(r.vary) == nil then r.vary = nil end end
    dirty = true ; forget_adjacency() ; draw()
    note(dir .. " from " .. num .. " unmarked - the next walk through it is believed again")
    return
  end
  if r.shift and r.shift[dir] then
    note(dir .. " from " .. num .. " is already marked shifting")
    return
  end
  if not ((r.exits and r.exits[dir] ~= nil) or (r.walked and r.walked[dir])) then
    note("nothing known lists a '" .. dir .. "' exit from " .. num .. " - marking it anyway")
  end
  r.shift = r.shift or {}
  r.shift[dir] = true
  if r.walked then r.walked[dir] = nil end                        -- the lie already learned
  if r.exits and r.exits[dir] ~= nil then r.exits[dir] = 0 end    -- and the one told
  if r.vary then r.vary[dir] = nil; if next(r.vary) == nil then r.vary = nil end end
  dirty = true ; forget_adjacency() ; draw()
  note(dir .. " from " .. num .. " marked SHIFTING - drawn " .. dir
       .. ">~, never learned, never routed through")
end

local function forget(num)
  if not rooms[num] then note("room " .. num .. " is not in the store"); return end
  rooms[num] = nil
  known = known - 1
  if here == num then here = nil end
  dirty = true
  forget_adjacency()
  draw()
  note("forgot room " .. num .. ". Links from other rooms still point at it until they are re-walked.")
end

-- ---------- alias ----------
-- One dispatcher rather than several aliases: a pattern like '^mapg (%w+)$'
-- registered beside '^mapg$' swallows whichever was added first, which is a
-- mistake this project has made before and does not need to make again.
scrye.addAlias{
  pattern = "^mapg(?:\\s+(.*))?$",
  regex = true,
  run = function(arg)
    local a = (arg or ""):gsub("^%s+", ""):gsub("%s+$", "")
    local verb, rest = a:match("^(%S+)%s*(.*)$")
    verb = (verb or ""):lower()

    -- Any command that is not the confirmation disarms a pending wipe, so an
    -- armed one cannot sit around waiting to be triggered by a later 'yes'.
    if not (verb == "wipe" and rest:lower() == "yes") then wipe_armed = false end

    if verb == "" or verb == "status" then status()
    elseif verb == "on"    then talking = true;  scrye.store.set("talking", "1"); note("commentary ON")
    elseif verb == "off"   then talking = false; scrye.store.set("talking", "0"); note("commentary off")
    elseif verb == "areas" then areas()
    elseif verb == "rooms" then rooms_list(rest ~= "" and rest:lower() or nil)
    elseif verb == "here"  then if here then room_detail(here) else note("we are not anywhere yet") end
    elseif verb == "room"  then
      local n = tonumber(rest)
      if n then room_detail(n) elseif here then room_detail(here) else note("mapg room <number>") end
    elseif verb == "path" then
      local n = tonumber(rest)
      if n then path_to(n) else note("mapg path <room number> - 'mapg rooms' lists them") end
    elseif verb == "explore"  then
      if rest:lower() == "go" then
        local steps = route_to_frontier()
        if steps then
          local d = steps[#steps].to
          walk_begin(steps, string.format("%d %s (unexplored: %s)", d, name_of(d),
                     table.concat(frontier_dirs(rooms[d]), " ")))
        end
      else explore() end
    elseif verb == "go" then
      local n = tonumber(rest)
      if not n then note("mapg go <room number> - 'mapg path <n>' shows the route without walking it")
      else
        local steps = route_to(n)
        if steps then walk_begin(steps, string.format("%d %s", n, name_of(n))) end
      end
    elseif verb == "stop" then
      if walk then walk_stop("walk stopped") else note("not walking") end
    elseif verb == "frontier" then frontier()
    elseif verb == "maps"     then maps_list(rest)
    elseif verb == "fav"      then fav_toggle()
    elseif verb == "name"     then name_map(rest)
    elseif verb == "map"      then
      local L = current_layout()
      if not L then note("we are not anywhere yet"); return end
      local b = borders_of(L.set)
      note(string.format("this map: %s - %d room(s), %d drawn", L.area, L.size, L.placed))
      if L.displaced > 0 then
        note(string.format("  %d room(s) could not be drawn where their links say they belong (marked !)", L.displaced))
        note("  MUD geometry is not a grid; the room is right, the cell is approximate")
      end
      if #b > 0 then note("  borders " .. table.concat(b, ", ")) end
    elseif verb == "redraw"   then forget_adjacency(); draw(); note("laid out again from the links")
    elseif verb == "level"    then
      local L = current_layout()
      if not L then note("we are not anywhere yet"); return end
      local mine = L.at[here].z
      if rest == "up" then view_z = (view_z or mine) + 1
      elseif rest == "down" then view_z = (view_z or mine) - 1
      else view_z = nil end
      draw()
      note("level " .. (view_z or mine) .. (view_z and " (use 'mapg level' to come back)" or ""))
    elseif verb == "draw" then
      if rest == "off" then drawing = false; scrye.store.set("drawing", "0"); note("panel off")
      else drawing = true; scrye.store.set("drawing", "1"); draw(); note("panel on") end
    elseif verb == "shift"    then shift_cmd(rest)
    elseif verb == "forget"   then
      local n = tonumber(rest)
      if n then forget(n) else note("mapg forget <room number>") end
    elseif verb == "wipe" then wipe(rest:lower() == "yes")
    elseif verb == "save" then note("saved " .. (save(true) or 0) .. " room(s)")
    elseif verb == "help" then
      note("mapg              what the store knows and where we are")
      note("mapg here         this room in full: every link and how it is known")
      note("mapg room <n>     the same for any room")
      note("mapg rooms [area] every room known, optionally filtered by area")
      note("mapg areas        rooms grouped by area")
      note("mapg path <n>     shortest known route to a room - printed, not walked")
      note("mapg go <n>       walk that route, one confirmed step at a time")
      note("mapg explore go   walk to the nearest room with an unwalked exit")
      note("mapg stop         stop walking")
      note("mapg explore      the nearest room with an exit nobody has been through")
      note("mapg frontier     every unexplored exit")
      note("mapg map          which map you are on and what it borders")
      note("mapg maps [text]  every map, alphabetically - text filters by name or border")
      note("mapg fav          save/unsave the map you are on to the Favs tab (max 20)")
      note("mapg name <text>  call this map something you will recognise ('mapg name -' undoes it)")
      note("mapg level up|down   look at another level; 'mapg level' comes back")
      note("mapg redraw       lay the current map out again from the links")
      note("mapg draw on|off  the HUD panel")
      note("mapg shift [n] <dir> [off]  mark an exit shifting (elevator, portal): drawn ~, never routed")
      note("mapg forget <n>   drop one room")
      note("mapg wipe         erase the whole store and start again (asks first)")
      note("mapg save         write the store to disk now")
      note("mapg on|off       the running commentary")
      note("a '*' after a destination means we walked it; no star means the server said so")
      note("a '~' is a shifting exit: it ends somewhere different each ride, so you ride it yourself")
    else
      note("don't know 'mapg " .. verb .. "' - try 'mapg help'")
    end
  end,
}

-- ---------- the panel ----------
-- Same widgets and the same tile alphabet as the shipped 3s-map, so the two
-- panels can sit side by side and be read the same way. One difference, and
-- it is the whole point: clicking a room here PRINTS the route. It does not
-- walk it. Nothing in this plugin walks anything.
local function peek(col, row)
  local L = layout
  if not L or col < 0 or row < 0 or col % 2 == 1 or row % 2 == 1 then
    scrye.setState(P .. "peek", "")
    return nil
  end
  local x, y = draw_x0 + col // 2, draw_y0 - row // 2
  local num = L.cells[cell_key(x, y, draw_z)]
  if not num then scrye.setState(P .. "peek", ""); return nil end
  local r = rooms[num]
  local bits = { tostring(num), name_of(num) }
  local ex = {}
  for _, e in ipairs(neighbours(r)) do ex[#ex + 1] = e.dir end
  if #ex > 0 then bits[#bits + 1] = "exits " .. table.concat(ex, ",") end
  local fr = frontier_dirs(r)
  if #fr > 0 then bits[#bits + 1] = "unexplored " .. table.concat(fr, ",") end
  if L.at[num] and L.at[num].off then bits[#bits + 1] = "[drawn off its links]" end
  scrye.setState(P .. "peek", table.concat(bits, "  "))
  return num
end

-- A click on a panel row: walk to the map containing that room. The same guards a
-- 'map.goto' request gets, as notes rather than events - the clicker is a person looking
-- at this panel, not a plugin waiting on an answer. The walk itself is an ordinary
-- requested walk and still emits the walk contract, because listeners deserve to know
-- the mapper is moving whoever asked for it.
local function walk_to_map(num, whence)
  if not num or not rooms[num] then note("that map is not in the store any more - redraw") ; return end
  if held_by then note("mapping is held by " .. held_by .. " - not walking") ; return end
  if not here then note("we are not anywhere yet - walk one room first") ; return end
  if walk then note("already walking - 'mapg stop' first") ; return end
  local set = component_of(num)
  if set[here] then note("you are on that map already") ; return end
  local steps, why = bfs(here, function(at) return set[at] ~= nil end)
  if not steps then
    if why then
      note("no known route to that map from here - " .. why)
    else
      -- The search ran dry: no chain of KNOWN rooms connects here to there. That is not
      -- "it cannot be reached" - it is unexplored ground in between, or a door crossed
      -- with a command the mapper could not read as a move. Say which cure works.
      note("no route through rooms this map knows - unexplored ground (or an unlearned")
      note("  door) lies between. Walk the way once and this click works from then on")
    end
    return
  end
  local d = steps[#steps].to
  walk_begin(steps, string.format("%d %s [%s] (from %s)", d, name_of(d), area_of(rooms[d]), whence))
end

local function row_go(index) walk_to_map(maplist_seeds[index], "the Maps tab") end
local function fav_go(index) walk_to_map(favlist_nums[index], "Favourites") end

-- 'mapg fav' and the Favs tab's button: toggle the map you are STANDING ON in the
-- favourites. Toggling (rather than separate add/remove) means the removal path needs no
-- extra UI: stand on it, press again. Membership is by component, so a favourite saved
-- from any room of a map is found again from any other.
fav_toggle = function()
  if not here or not rooms[here] then note("we are not anywhere yet - walk one room first") ; return end
  local set = component_of(here)
  local lbl = label_of()[here] or area_of(rooms[here])
  for i, n in ipairs(favs) do
    if set[n] then
      table.remove(favs, i)
      dirty = true ; draw()
      note("no longer a favourite: " .. lbl)
      return
    end
  end
  if #favs >= FAV_CAP then
    note("favourites are full (" .. FAV_CAP .. ") - stand on one and 'mapg fav' drops it")
    return
  end
  favs[#favs + 1] = here
  dirty = true ; draw()
  note(string.format("favourite %d/%d: %s", #favs, FAV_CAP, lbl))
end

scrye.addPanel{
  title = "3S Map (GMCP)",
  width = 280,
  tabs = {
    { title = "Map", widgets = {
      { type = "label", bind = P .. "status", color = "dim" },
      { type = "colorgrid", bind = P .. "grid", palette = PALETTE, labels = TILE_MARKS,
        weave = true,
        onHover = function(col, row) peek(col, row) end,
        onClick = function(col, row)
          local num = peek(col, row)
          if num and num ~= here then path_to(num) end   -- prints it; never walks it
        end,
        -- The right-click menu (API 1.18): every entry is a command AS YOU WOULD TYPE IT,
        -- so it goes through mapg's own aliases - same caution, same narration, and a
        -- pre-1.18 host that ignores the return simply has no menu, nothing breaks.
        onRightClick = function(col, row)
          local num = peek(col, row)
          if not num or num == here then return end
          return { { "Walk there",   "mapg go "   .. num },
                   { "Show route",   "mapg path " .. num },
                   { "Room details", "mapg room " .. num } }
        end },
      { type = "buttonrow", buttons = {
        { text = "Up",     action = function()
            local L = current_layout() ; if not L then return end
            view_z = (view_z or L.at[here].z) + 1 ; draw() end },
        { text = "Down",   action = function()
            local L = current_layout() ; if not L then return end
            view_z = (view_z or L.at[here].z) - 1 ; draw() end },
        { text = "Center", action = function() view_z = nil ; draw() end },
        { text = "Stop",   action = function()
            if walk then walk_stop("walk stopped") end end },
        { text = "Redraw", action = function() forget_adjacency() ; draw() end },
      } },
      { type = "value", text = "", bind = P .. "where" },
      { type = "value", text = "", bind = P .. "peek" },
    } },
    { title = "Maps", widgets = {
      { type = "label", text = "click a map to walk there", bind = P .. "mapshint", color = "dim" },
      -- The search box: filters the list by map name OR bordering map's name - so a
      -- realm's name (once you have named it below) lists every area hanging off it.
      -- Blank Set clears. Session-local on purpose: a filter that survived a restart
      -- would read as a half-empty map store.
      { type = "input", text = "search", bind = P .. "mapfilter",
        onSubmit = function(text)
          maplist_filter = tostring(text or ""):gsub("^%s+", ""):gsub("%s+$", ""):lower()
          scrye.setState(P .. "mapfilter", maplist_filter)
          refresh_maplist()
        end },
      { type = "table", bind = P .. "maplist", columns = { "Map", "Rooms", "Borders" }, align = "lrl",
        -- The row click IS 'walk to that map' (API 1.15): the row's index looks up the
        -- map's seed recorded when the list was composed, and the walk is an ordinary
        -- requested walk - same caution, same narration, stopped by everything that stops
        -- one. The label argument is ignored: it carries the "> " you-are-here prefix,
        -- and the seed under the index is the identity the label only displays.
        onRowClick = function(_, index) row_go(index) end,
        -- Right-click a map row for the same three choices the grid menu offers, aimed at
        -- the map's seed room (the identity the row's label only displays)
        onRowMenu = function(_, index)
          local seed = maplist_seeds[index]
          if not seed then return end
          return { { "Walk there",   "mapg go "   .. seed },
                   { "Show route",   "mapg path " .. seed },
                   { "Room details", "mapg room " .. seed } }
        end },
      -- Naming from the panel, for the maps the feed cannot name itself: the realms are
      -- "Unknown" connective space (Chaos, Science... - each hangs off ONE special town
      -- room), so they all land in auto labels like "Unknown #2". The box shows the name
      -- of the map you are STANDING ON and renames that map - same machinery and rules
      -- as 'mapg name': '-' puts the auto label back, blank does nothing. Renaming edits
      -- a label and nothing else, so a slip of the finger cannot damage the map.
      { type = "input", text = "this map", bind = P .. "mapname",
        onSubmit = function(text)
          text = tostring(text or ""):gsub("^%s+", ""):gsub("%s+$", "")
          if text ~= "" then name_map(text) end
        end },
    } },
    { title = "Favs", widgets = {
      { type = "label", bind = P .. "favhint", color = "dim" },
      { type = "buttonrow", buttons = {
        -- One button, a toggle: stand on a map, press to save it; press again to drop
        -- it. Twenty slots. The rows below walk there, exactly like the Maps tab.
        { text = "Fav this map", action = function() fav_toggle() end },
      } },
      { type = "table", bind = P .. "favlist", columns = { "Map", "Rooms" }, align = "lr",
        onRowClick = function(_, index) fav_go(index) end,
        onRowMenu = function(_, index)
          local num = favlist_nums[index]
          if not num then return end
          return { { "Walk there",   "mapg go "   .. num },
                   { "Show route",   "mapg path " .. num },
                   { "Room details", "mapg room " .. num } }
        end },
    } },
  },
}

-- The Maps tab, refreshed with the drawing. Alphabetical, and filtered by the search box
-- (a map shows when the text matches its label OR a bordering map's label - so searching
-- a realm's name lists the areas hanging off it).
refresh_maplist = function()
  local maps = sorted_maps()
  -- The table widget takes tab-separated columns, newline-separated rows --
  -- a plain string, not JSON. Same as the shipped map's Rooms tab. The seeds are
  -- recorded per row AS COMPOSED - filtered, sorted, capped - so a click can never
  -- act on a map the composition moved: index and seed change together or not at all.
  local rows = {}
  maplist_seeds = {}
  local standing = ""          -- the name box shows the map under your feet
  local total = 0
  for _, m in ipairs(maps) do
    if here and m.set[here] then standing = m.label end
    local b = borders_of(m.set)
    if map_matches(m, b, maplist_filter) then
      total = total + 1
      if total <= 60 then
        maplist_seeds[total] = m.seed
        rows[total] = string.format("%s\t%d\t%s",
          (here and m.set[here] and "> " or "") .. m.label,
          m.size,
          table.concat(b, ", ", 1, math.min(#b, 2)))
      end
    end
  end
  scrye.setState(P .. "maplist", table.concat(rows, "\n"))
  scrye.setState(P .. "mapname", standing)
  scrye.setState(P .. "mapshint",
    maplist_filter == "" and "click a map to walk there"
    or string.format("matching '%s' - %d map(s). Blank search shows all", maplist_filter, total))
  refresh_favlist(maps)
end

-- The Favs tab: each saved room resolved to the map that CONTAINS it now - so a
-- favourite follows its map through growth and merges. A room the store no longer has is
-- pruned from the saved list (a wipe or a forget took it); two favourites resolved to
-- the same map (their maps merged) display once. Alphabetical, like the Maps tab.
refresh_favlist = function(maps)
  maps = maps or sorted_maps()
  local entries, seen, keep = {}, {}, {}
  for _, num in ipairs(favs) do
    if rooms[num] then
      keep[#keep + 1] = num
      for _, m in ipairs(maps) do
        if m.set[num] then
          if not seen[m.seed] then
            seen[m.seed] = true
            entries[#entries + 1] = { label = m.label, size = m.size, num = num }
          end
          break
        end
      end
    else
      dirty = true                 -- a favourite of a forgotten room: quietly let it go
    end
  end
  favs = keep
  table.sort(entries, function(a, b)
    local la, lb = tostring(a.label):lower(), tostring(b.label):lower()
    if la ~= lb then return la < lb end
    return a.num < b.num
  end)
  local rows = {}
  favlist_nums = {}
  for i, e in ipairs(entries) do
    favlist_nums[i] = e.num
    rows[i] = string.format("%s\t%d", e.label, e.size)
  end
  scrye.setState(P .. "favlist", table.concat(rows, "\n"))
  scrye.setState(P .. "favhint",
    #entries == 0 and "no favourites yet - stand on a map, press the button"
    or string.format("%d of %d saved - click one to walk there", #entries, FAV_CAP))
end

-- ---------- the quiet case ----------
-- Saying nothing when nothing arrives is the one thing this must not do:
-- "GMCP is off", "the world has GMCP off" and "you have not moved yet" all
-- look identical from here, and only the last one is fine.
local function check_quiet()
  quiet_timer = nil
  if msgs > 0 or warned then return end
  warned = true
  note("no Room.Info in " .. QUIET_SECS .. "s. This plugin needs GMCP and does nothing without it.")
  note("  '.gmcp' says whether it was negotiated and what has arrived.")
  note("  Room packages are sent when you ENTER a room - 'look' does not resend them,")
  note("  so if you have not moved since connecting, walk one room and this will clear.")
end

local function arm_quiet_check()
  if quiet_timer then scrye.cancel(quiet_timer) end
  msgs = 0
  warned = false
  walk_stop(nil)    -- a walk cannot survive the connection it was walking on
  here = nil        -- a reconnect is not a step: never pair a move across it
  moves = {}
  quiet_timer = scrye.after(QUIET_SECS, check_quiet)
end

-- ---------- lifecycle ----------
-- The chaos-sea contract, shared with the shipped 3s-map: while another plugin holds the
-- map, its moves are not mapped. Transient by design -- never persisted, released by whoever
-- took it. Source-agnostic, so 3s-chaossea and 3s-chaossea-gmcp both work without either
-- being named here.
scrye.on("map.hold", function(data, _, source)
  local ok, t = pcall(scrye.json.decode, data)
  local on = ok and type(t) == "table" and t.on == true
  local who = tostring(source or "?")
  if on and not held_by then
    held_by = who
    here = nil ; moves = {}
    walk_stop("walk stopped - mapping held by " .. who)
    note("mapping held by " .. who .. " - their moves will not be mapped")
  elseif not on and held_by then
    held_by = nil
    here = nil ; moves = {}     -- the first room back is a fresh start, not a step
    note("mapping resumed (" .. who .. " released the hold)")
  end
end)

-- The request half of the contract. Another plugin asks; the walker answers with the same
-- machinery, the same caution and the same narration a typed 'mapg go' gets - a requested
-- walk is a walk, and everything that stops one stops this one. The requester hears the
-- outcome as exactly one of map.walk.arrived / map.walk.stopped.
--
-- 'map.goto' takes { num = <room> } or { area = "<name>" }; area means the NEAREST known
-- room whose area matches (case-insensitive substring, like every other lookup here). A
-- request that cannot be served emits map.walk.stopped with the reason, so a requester
-- never has to parse our chat to learn its walk is not coming.
local function refuse_goto(reason)
  note("map.goto refused - " .. reason)
  scrye.emit("map.walk.stopped", scrye.json.encode({ reason = reason }))
end

scrye.on("map.goto", function(data, _, source)
  local ok, t = pcall(scrye.json.decode, data)
  if not ok or type(t) ~= "table" then refuse_goto("unreadable request") ; return end
  if held_by then refuse_goto("mapping is held by " .. held_by) ; return end
  if not here then refuse_goto("we are not anywhere yet") ; return end
  if walk then refuse_goto("already walking - map.stop first") ; return end
  local who = tostring(source or "?")
  local num = tonumber(t.num)
  if num then
    if num == here then scrye.emit("map.walk.arrived", scrye.json.encode({ num = here })) ; return end
    if not rooms[num] then refuse_goto("room " .. num .. " is not in the store") ; return end
    local steps, why = bfs(here, function(at) return at == num end)
    if not steps then refuse_goto("no known route to " .. num .. (why and (" - " .. why) or "")) ; return end
    walk_begin(steps, string.format("%d %s (for %s)", num, name_of(num), who))
    return
  end
  local want = tostring(t.area or "")
  if want == "" then refuse_goto("no num and no area in the request") ; return end
  local lw = want:lower()
  local function in_area(_, r) return area_of(r):lower():find(lw, 1, true) ~= nil end
  if in_area(here, rooms[here]) then
    scrye.emit("map.walk.arrived", scrye.json.encode({ num = here }))
    return
  end
  local steps, why = bfs(here, in_area)
  if not steps then refuse_goto("no known route into '" .. want .. "'" .. (why and (" - " .. why) or "")) ; return end
  local d = steps[#steps].to
  walk_begin(steps, string.format("%d %s [%s] (for %s)", d, name_of(d), area_of(rooms[d]), who))
end)

scrye.on("map.stop", function()
  if walk then walk_stop("walk stopped (map.stop)") end
end)

-- 'map.query.area' { area = "<name>" }: hand over everything known about an area, as
-- 'map.area.rooms' { area, rooms = [ { num, name, area, exits = {dir=dest} } ] }. This is
-- how the farmer knows what "explored" means without a second map: its fence is the rooms
-- somebody actually stood in, and this store is the record of that - including every
-- earlier session, which the farmer's own eyes cannot have seen. Exits are handed over
-- RESOLVED (walked beats told, like everything here); an unexplored exit has no
-- destination to hand over and is simply absent. Always answers, even with an empty list -
-- a requester left hanging cannot tell silence from absence.
scrye.on("map.query.area", function(data)
  local ok, t = pcall(scrye.json.decode, data)
  if not ok or type(t) ~= "table" then return end
  local want = tostring(t.area or "")
  if want == "" then return end
  local lw = want:lower()
  local out = {}
  for num, r in pairs(rooms) do
    if area_of(r):lower():find(lw, 1, true) then
      local ex = {}
      for _, e in ipairs(neighbours(r)) do ex[e.dir] = e.to end
      out[#out + 1] = { num = num, name = r.name, area = r.area, exits = ex }
    end
  end
  scrye.emit("map.area.rooms", scrye.json.encode({ area = want, rooms = out }))
end)

scrye.onGmcp(ROOM_PKG, on_room_info)
scrye.onCommand(on_command)
scrye.onConnect(arm_quiet_check)
scrye.onDisconnect(function() walk_stop(nil) ; save(true) end)
-- The idle guard: stop what we are driving and stay stopped. A walk that
-- silently resumes because you typed 'look' is the surprise the guard exists
-- to prevent.
scrye.onIdle(function() walk_stop("idle guard - walk stopped (it will NOT resume on its own)") end)

talking = scrye.store.get("talking") ~= "0"
drawing = scrye.store.get("drawing") ~= "0"
load()

-- One clock, one second. The move queue's TTL (MOVE_TTL) reads this clock, and when it
-- advanced only on the flush tick it was 15 s coarse: a move typed just before a flush
-- was pruned almost at once, its Room.Info paired with nothing, and the walked link went
-- unlearned - rare, invisible, and exactly the kind of hole a laggy night opens. The
-- flush still runs every FLUSH_SECS; it just rides the finer clock.
flush_timer = scrye.every(1, function()
  now = now + 1
  if now % FLUSH_SECS == 0 then save() end
end)

draw()
note(string.format("phase 1 loaded - %d room(s) known. 'mapg' for status, 'mapg help' for the rest.", known))
arm_quiet_check()
