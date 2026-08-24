-- ============================================================
-- 3S Chaos Sea - Scrye port of ThreeS_ChaosSea (MUSHclient)
-- Maps rooms on a 3D grid as you move, queues unexplored exits and
-- walks to the nearest frontier room (BFS through known rooms).
-- ============================================================
-- NOTE: dropped / simplified vs the original:
--  * 'cs win' and all miniwindow show/hide/drag/resize handling: the HUD
--    panel is managed by Scrye itself.
--  * Area/Deadman tab handover (CallPlugin into the area-bot plugin) and
--    the 'Bots' tab bar: no cross-plugin calls in Scrye.
--  * The 'Set #' inputbox button -> alias 'cs seanum <n>' (1-120) instead
--    (utils.inputbox does not exist here); the New Sea button remains.
--  * Party check in cs_on_player reads the world variable 'party'
--    (GetPluginVariable into the stepper plugin is not available).
--  * Combat truth (MIP enemy_name) read via scrye.getState("enemy.name");
--    Seid via scrye.getState("vik.seid") with a vik.ser fallback.
--  * No os.time: elapsed time (rest countdown, watchdog, sea age) is
--    counted with a 1 s ticker. The sea-age clock is persisted as elapsed
--    seconds, so it does not advance while Scrye is closed.
--  * Sub-second DoAfterSpecial delays (0.5 s) rounded up to 1 s
--    (scrye.after granularity is 1 s).
--  * Map miniwindow -> colorgrid widget showing the current Z level
--    centred on you (north = up). Exit connector lines are not drawn;
--    a room with a down exit shows as 'v' (the original's blue dot).
--    Original colours kept in the palette.
-- ============================================================
-- Reads the room from GMCP and from the MUD's own room header, so none of the '=S=',
-- '=M=', '=P=' or '=A|W|I=' display markers are needed. Those triggers remain as a fallback
-- for a character or MUD without GMCP; each stands down for any room GMCP has described.
--
-- The sea has NO room identity: Room.Info.num is 60494 for every room on every layer,
-- and every exit destination is 60494 or 0. The whole sea is one room object re-dressed
-- as you walk, so the 3s-map-gmcp trick -- key rooms by the number the server gives them
-- and position can never drift -- does not apply here. x/y is still dead-reckoned.
--
-- What GMCP does give the sea:
--   * Room.Info fires on a room CHANGE and at no other time ('look' produces none), so
--     its arrival IS the move. That retires the whole sent_q apparatus.
--   * Room.Info.name is the LAYER, so depth is told rather than counted.
--   * Room.Info.exits keys are the exit list, authoritative and complete.
-- and, the point of the exercise: none of it costs a MUD display setting, so the '=S='
-- markers can be switched off and the room read the way a player reads it.
-- ============================================================

local P = "plugin." .. scrye.id .. "."

-- ---------- state ----------
local rooms = {}            -- rooms[z][x][y] = { exits = {dir=true} }
local frontier = {}         -- LIFO list of { x=, y=, z=, dir= }
local pos = { x = 0, y = 0, z = 0 }
local enabled = false
local auto = false
local fighting = false
local pending_mob = nil        -- mob seen this room; attack decided at the prompt
local idle_fight_prompts = 0   -- self-heal counter when combat fizzles out
local armed = false            -- a room was parsed; ok to auto-step on next prompt
local plan = nil               -- walking plan: one confirmed move at a time
local last_dir = nil           -- last direction actually sent (avoid instant backtrack)
-- The last movement direction YOU typed, and when. Observe-only, and deliberately a
-- SCALAR: nothing about correctness depends on it any more, so it needs no queue.
--
-- It used to. Arrival was learned by parsing the '=S=' room header out of the output,
-- which meant working out which command that block of output was answering -- a real
-- problem, because commands pipeline and every plugin's sends share the stream. That is
-- what sent_q existed for, with a TTL, a self-limit, a prune on every prompt and the
-- watchdog's '!glance' to shake a header loose; and it still drifted by one whenever a
-- command produced no prompt at all.
--
-- GMCP removes the question instead of answering it: Room.Info is sent when you change
-- room and at no other time, so its arrival IS the move. All that survives is telling
-- "you walked out of a fight" from "wimpy dragged you out" -- which picks the wording of
-- a message and decides nothing.
local last_typed = nil         -- { dir = "n", at = tick } -- a direction the USER sent
local TYPED_TTL = 3            -- ticks a typed direction stays a plausible explanation
local layer_base = nil         -- pos.z + layer, at the room where the two were tied together
local blind_steps = 0          -- consecutive re-orient steps after a maze shift
local killname = "mutant"
local goal = "cask portal"
local goals = {}
local kill_delay = 2.5
local paused = false
local rest_below = 0           -- 0 = off
local rest_secs = 60
local resting = false
local rest_until = 0
local goal_found = false
local room_goal_idx = nil      -- index in `goals` of the best match seen this room
local room_goal_word = nil     -- the matched word, acted on at the prompt
local excluded = {}            -- mob long names never to attack
local party = {}               -- lowercased group member names: seeing one is NOT "a player is here"
local steps_done = 0
local kills = 0                -- mobs killed since the map was last reset (a new sea zeroes
                               -- it, so it reads as "this sea" rather than "for ever")
local seanum = 1
local sea_time = nil           -- `now` value when the current sea was entered
local sea_started_here = false -- true only if THIS session started the sea (see cs_new_sea)
local sea_entering = false     -- New Sea sequence in flight: the swap is sent over several
                               -- seconds, and TWO of those commands change room -- 'retreat
                               -- from the sea' and 'enter sea' -- so Room.Info arrives twice
                               -- mid-swap. Nothing may walk or unpause while it is set, and
                               -- arrivals are ignored: the first is a room outside the sea
                               -- and mapping it would seed the new maze with a phantom.
local now = 0                  -- seconds since plugin load (1 s ticker)

-- A direction the user typed recently enough to still explain a move we did not order.
-- Only ever used to word a message.
local function typed_recently()
  if last_typed and now - last_typed.at < TYPED_TTL then return last_typed.dir end
  return nil
end
local map_serial = 1           -- bumped on every GRAPH change (rooms/exits), not moves —
                               -- the wasm pathfinder caches the graph keyed on this

local cs_flags = { item = false, gold = false, player = false }
-- Has Room.Contents spoken for the room we are standing in? It arrives BEFORE the MUD
-- prints the same contents as text, so when it has, the '=M=' / '=P=' / '=A|W|I=' triggers
-- are redundant and must not run: they would set the same flags a second time from lines
-- naming the same creatures. Cleared on arrival, so a room whose contents only ever come
-- as text still works exactly as before -- which is what lets the markers be turned off one
-- at a time rather than all at once.
local contents_seen = false
-- The sea moves you without being asked: "Suddenly you find yourself elsewhere..". The
-- Room.Info that follows is NOT the step we sent, and confirming it as one would advance
-- pos by a direction we never travelled and quietly poison every plan after it.
local teleported = false
-- A real wimpy retreat announces itself AND names the direction: "Your legs run away with
-- you east". So it costs us nothing -- we know exactly where we ended up. What matters more
-- is what its ABSENCE means: an arrival mid-fight with no retreat line and nothing typed is
-- not a retreat at all, it is the maze rearranging itself around us, which it does
-- constantly. Guessing "wimpy" from the absence of everything else stopped the bot several
-- times an hour for nothing.
local wimpy_dir = nil

-- forward declarations (mutually recursive)
local cs_draw, cs_step, cs_advance, mark_dirty, build_panel
-- what the buttons last showed, so the panel is only rebuilt when it would look different
local panel_on, panel_auto, panel_paused = nil, nil, nil

-- the original drew ":: CSS ::" in red/grey/red before every message
local function note(s) scrye.print("@{#FD2083,bold}::@{} @{#4BE4FF}CSS@{} @{#FD2083,bold}::@{} " .. s) end

-- ---------- phone notifications (plugin.<id>.notify convention) ----------
-- Default ON: this is an AFK bot, and every notify below is a moment it has STOPPED
-- and is waiting on you — the goal in the room, a wimpy retreat, the sea explored out.
local notify_on = scrye.store.get("notify") ~= "0"

local function publish_notify_state()
  scrye.setState(P .. "notify",
    string.format("Bot pauses & finds\tgoal found / wimpy pause / out of rooms\t%s\tcs notify %s",
      notify_on and "on" or "off", notify_on and "off" or "on"))
end

local function pnotify(s) if notify_on then scrye.notify(s) end end

local function parse_goals()
  goals = {}
  for w in goal:lower():gmatch("[%w_]+") do goals[#goals + 1] = w end
end
parse_goals()

-- ---------- coordinates (diagonals: x/y only) ----------
local DELTA = {
  n = {0,1,0},  s = {0,-1,0}, e = {1,0,0},  w = {-1,0,0},
  u = {0,0,1},  d = {0,0,-1},
  ne = {1,1,0}, nw = {-1,1,0}, se = {1,-1,0}, sw = {-1,-1,0},
  north = {0,1,0}, south = {0,-1,0}, east = {1,0,0}, west = {-1,0,0},
  up = {0,0,1}, down = {0,0,-1},
  northeast = {1,1,0}, northwest = {-1,1,0}, southeast = {1,-1,0}, southwest = {-1,-1,0},
}
-- opposite direction (short forms), to avoid an immediate backtrack on a blind step
local OPP = { n="s", s="n", e="w", w="e", u="d", d="u", ne="sw", sw="ne", nw="se", se="nw" }

local function moved(p, dir)
  local d = DELTA[dir]
  if not d then return nil end
  return { x = p.x + d[1], y = p.y + d[2], z = p.z + d[3] }
end

local function get_room(p)
  local zt = rooms[p.z]; if not zt then return nil end
  local xt = zt[p.x]; if not xt then return nil end
  return xt[p.y]
end

local function add_room(p)
  rooms[p.z] = rooms[p.z] or {}
  rooms[p.z][p.x] = rooms[p.z][p.x] or {}
  if not rooms[p.z][p.x][p.y] then
    rooms[p.z][p.x][p.y] = { exits = {} }
    map_serial = map_serial + 1
  end
  return rooms[p.z][p.x][p.y]
end

local function room_count()
  local n = 0
  for _, zt in pairs(rooms) do
    for _, xt in pairs(zt) do
      for _ in pairs(xt) do n = n + 1 end
    end
  end
  return n
end

-- `state` is what the SERVER says about this exit: "new" (never walked), "old" (walked), or
-- nil when we only know the exit's name -- the room header lists directions and no
-- destinations, so it must not overwrite what we already knew.
local function add_exit(p, dir, state)
  local room = get_room(p) or add_room(p)
  if not room.exits[dir] then
    room.exits[dir] = state or true
    map_serial = map_serial + 1
    local target = moved(p, dir)
    -- The server's answer comes FIRST. Our own test -- "is there a room at the coordinate
    -- this exit leads to?" -- is only as good as the dead reckoning behind it, and a
    -- collision (two real rooms landing on one square) makes an unwalked exit look visited.
    -- That is how a barely-touched sea once reported "Out of rooms!". An exit the server
    -- still reports as 0 has never been walked, whatever our coordinates believe.
    if state == "new" or (target and not get_room(target)) then
      frontier[#frontier + 1] = { x = p.x, y = p.y, z = p.z, dir = dir }
    end
  elseif state then
    room.exits[dir] = state
  end
end

-- ---------- persistence (scrye.store, strings only) ----------
-- "map":      one room per line  "z|x|y|dir1,dir2,..."
-- "frontier": one entry per line "z|x|y|dir"
-- "pos":      "x|y|z"
-- "excluded": one long name per line
-- scalars:    killname, goal, kill_delay, rest_below, rest_secs, seanum,
--             sea_elapsed (seconds into the current sea, if one is running)
local function save_state()
  local out = {}
  for z, zt in pairs(rooms) do
    for x, xt in pairs(zt) do
      for y, room in pairs(xt) do
        local ex = {}
        for d in pairs(room.exits) do ex[#ex + 1] = d end
        out[#out + 1] = z .. "|" .. x .. "|" .. y .. "|" .. table.concat(ex, ",")
      end
    end
  end
  scrye.store.set("map", table.concat(out, "\n"))
  local fr = {}
  for _, e in ipairs(frontier) do fr[#fr + 1] = e.z .. "|" .. e.x .. "|" .. e.y .. "|" .. e.dir end
  scrye.store.set("frontier", table.concat(fr, "\n"))
  scrye.store.set("pos", pos.x .. "|" .. pos.y .. "|" .. pos.z)
  scrye.store.set("killname", killname)
  scrye.store.set("goal", goal)
  scrye.store.set("kill_delay", tostring(kill_delay))
  scrye.store.set("rest_below", tostring(rest_below))
  scrye.store.set("rest_secs", tostring(rest_secs))
  scrye.store.set("seanum", tostring(seanum))
  if sea_time then
    scrye.store.set("sea_elapsed", tostring(now - sea_time))
  else
    scrye.store.delete("sea_elapsed")
  end
  local exl = {}
  for n in pairs(excluded) do exl[#exl + 1] = n end
  scrye.store.set("excluded", table.concat(exl, "\n"))
end

-- debounced write-through: any cs_draw marks dirty, flushed 3 s later
local dirty_timer = nil
mark_dirty = function()
  if dirty_timer then return end
  dirty_timer = scrye.after(3, function()
    dirty_timer = nil
    save_state()
  end)
end

local function load_state()
  local r = scrye.store.get("map")
  if r and r ~= "" then
    for line in r:gmatch("[^\n]+") do
      local z, x, y, ex = line:match("^(-?%d+)|(-?%d+)|(-?%d+)|(.*)$")
      if z then
        local room = add_room({ x = tonumber(x), y = tonumber(y), z = tonumber(z) })
        for d in ex:gmatch("[^,]+") do room.exits[d] = true end
      end
    end
  end
  local f = scrye.store.get("frontier")
  if f and f ~= "" then
    for line in f:gmatch("[^\n]+") do
      local z, x, y, d = line:match("^(-?%d+)|(-?%d+)|(-?%d+)|(.*)$")
      if z then frontier[#frontier + 1] = { x = tonumber(x), y = tonumber(y), z = tonumber(z), dir = d } end
    end
  end
  local p = scrye.store.get("pos")
  if p then
    local x, y, z = p:match("^(-?%d+)|(-?%d+)|(-?%d+)$")
    if x then pos = { x = tonumber(x), y = tonumber(y), z = tonumber(z) } end
  end
  local exl = scrye.store.get("excluded")
  if exl then for n in exl:gmatch("[^\n]+") do excluded[n] = true end end
  local pty = scrye.store.get("party")
  if pty then for n in pty:gmatch("[^\n]+") do party[n] = true end end
  killname = scrye.store.get("killname") or "mutant"
  goal = scrye.store.get("goal") or "cask portal"
  parse_goals()
  kill_delay = tonumber(scrye.store.get("kill_delay") or "") or 2.5
  rest_below = tonumber(scrye.store.get("rest_below") or "") or 0
  rest_secs = tonumber(scrye.store.get("rest_secs") or "") or 60
  seanum = tonumber(scrye.store.get("seanum") or "") or 1
  local se = tonumber(scrye.store.get("sea_elapsed") or "")
  if se then sea_time = now - se end
  if not get_room(pos) then add_room(pos) end
end

-- ---------- party whitelist ----------
-- The MUSHclient version read the area-bot plugin's "party" variable, which Scrye has
-- no equivalent for (no cross-plugin reads), so the whitelist had no producer and every
-- =P= line stopped the bot killing. Names are kept here instead; a world variable named
-- "party" is still honoured if anything else sets one.

local function party_list()
  local t = {}
  for n in pairs(party) do t[#t + 1] = n end
  table.sort(t)
  return t
end

local function save_party()
  scrye.store.set("party", table.concat(party_list(), "\n"))
end

local function in_party(name)
  local low = (name or ""):lower()
  for member in pairs(party) do
    if low:find(member, 1, true) then return true end
  end
  local wv = scrye.getVariable("party")
  if wv ~= nil and wv ~= "" then
    for member in wv:gmatch("[^\n,]+") do
      member = member:gsub("^%s+", ""):gsub("%s+$", ""):lower()
      if member ~= "" and low:find(member, 1, true) then return true end
    end
  end
  return false
end

-- ---------- wasm pathfinder delegation ----------
-- Same protocol 3s-map speaks (see sdk/rust/plugins/3s-pathfinder): searches are
-- delegated over synchronous inter-plugin events, the graph ships only when our
-- map_serial says the pathfinder's per-area cache is stale, and no reply at all means
-- no pathfinder is loaded — callers fall back to the local bfs below.
local SHORT_DIR = {
  n="n", s="s", e="e", w="w", u="u", d="d", ne="ne", nw="nw", se="se", sw="sw",
  north="n", south="s", east="e", west="w", up="u", down="d",
  northeast="ne", northwest="nw", southeast="se", southwest="sw",
}

local function rooms_to_list()
  local list = {}
  for z, zt in pairs(rooms) do
    for x, xt in pairs(zt) do
      for y, r in pairs(xt) do
        local seen, ex = {}, {}
        for d in pairs(r.exits) do
          local c = SHORT_DIR[d]
          if c and not seen[c] then seen[c] = true; ex[#ex + 1] = c end
        end
        list[#list + 1] = { x = x, y = y, z = z, exits = ex }
      end
    end
  end
  return list
end

local path_req_id = 0
local path_reply = nil
scrye.on("map.path.result", function(data)
  local t = scrye.json.decode(data)
  if type(t) == "table" then path_reply = t end
end)

-- One synchronous exchange. opts = { to = {x,y,z} } or { targets = { {x,y,z}, ... } }
-- (targets in PRIORITY order — the reply's index is the first reachable one).
-- Returns the reply table, or nil when no pathfinder answered.
local function ask_pathfinder(opts, allow_up)
  path_req_id = path_req_id + 1
  local req = {
    id = path_req_id, area = "chaos-sea", serial = map_serial,
    allowUp = allow_up and true or false,
    from = { x = pos.x, y = pos.y, z = pos.z },
    to = opts.to, targets = opts.targets,
  }
  path_reply = nil
  scrye.emit("map.path.find", scrye.json.encode(req))
  if type(path_reply) == "table" and path_reply.id == path_req_id and path_reply.needArea then
    req.rooms = rooms_to_list()
    path_reply = nil
    scrye.emit("map.path.find", scrye.json.encode(req))
  end
  local r = path_reply
  path_reply = nil
  if type(r) ~= "table" or r.id ~= path_req_id or r.needArea then return nil end
  return r
end

-- ---------- BFS through known rooms, returns dir list ----------
-- allow_up: if false/nil, the path never climbs 'up' (so exploration won't
-- wander back onto an already-cleared floor). cs_leave passes true to go home.
local function bfs(src, dst, allow_up)
  if src.x == dst.x and src.y == dst.y and src.z == dst.z then return {} end
  local key = function(p) return p.z .. "|" .. p.x .. "|" .. p.y end
  local came = { [key(src)] = { dir = false } }
  local queue, qi = { src }, 1
  local found = nil
  while queue[qi] do
    local cur = queue[qi]; qi = qi + 1
    if cur.x == dst.x and cur.y == dst.y and cur.z == dst.z then found = cur break end
    local room = get_room(cur)
    if room then
      for dir in pairs(room.exits) do
        if allow_up or (dir ~= "u" and dir ~= "up") then
          local nxt = moved(cur, dir)
          if nxt and get_room(nxt) and not came[key(nxt)] then
            came[key(nxt)] = { dir = dir, prev = cur }
            queue[#queue + 1] = nxt
          end
        end
      end
    end
    if qi > 50000 then break end
  end
  if not found then return nil end
  local path = {}
  local node = came[key(found)]
  while node and node.dir do
    table.insert(path, 1, node.dir)
    node = came[key(node.prev)]
  end
  return path
end

-- ---------- room exits (from Room.Info) ----------
-- The server states exits as an OBJECT, direction -> destination room number. In the sea
-- every destination is the sea's own number or 0, so the VALUES carry nothing at all. The
-- KEYS are the answer, and they are complete -- which the '(n,s,e)' text in the room header
-- was not always, and which no longer costs a MUD display setting to obtain.
-- `dests` is Room.Info's exits object: direction -> destination room number. Optional,
-- because the room header gives names only.
--
-- The destination NUMBER is worthless in the maze -- every room is 60494 -- but whether it is
-- zero is not. 0 means you have not taken that exit; a number means you have. That is the
-- server keeping the exploration record for us, and it cannot drift the way our coordinates
-- can. `out` is the exception that proves it: it always points at 266, the sea's entrance.
local function set_exits(keys, dests)
  local room = get_room(pos) or add_room(pos)
  local prev = room.exits or {}
  room.exits = {}
  for _, exit in ipairs(keys) do
    -- What the server says about this exit, or what we already knew if it said nothing.
    -- Never downgrade: the header path carries no destinations and must not erase the bit.
    local state
    local d = dests and dests[exit]
    if d ~= nil then state = (d == 0) and "new" or "old"
    else               state = prev[exit] end

    if exit == "out" then
      -- the way out of the sea, don't queue it
    elseif exit == "u" or exit == "up" then
      -- remember it for pathing back, but never queue it for exploring
      room.exits[exit] = state or true
    elseif DELTA[exit] then
      add_exit(pos, exit, state)
      if exit == "d" or exit == "down" then
        local t = moved(pos, exit)
        if state == "new" or (t and not get_room(t)) then note("DOWN exit here!") end
      end
    end
  end
end

-- "Layer three of the Sea of Chaos" -> 3. Word forms, because that is what the server
-- writes; anything unrecognised returns nil and dead reckoning simply stands.
local LAYER_WORD = { one=1, two=2, three=3, four=4, five=5, six=6, seven=7, eight=8,
  nine=9, ten=10, eleven=11, twelve=12, thirteen=13, fourteen=14, fifteen=15,
  sixteen=16, seventeen=17, eighteen=18, nineteen=19, twenty=20 }
local function layer_of(name)
  local w = tostring(name or ""):match("^[Ll]ayer%s+(%w+)")
  if not w then return nil end
  return LAYER_WORD[w:lower()] or tonumber(w)
end

-- The layer the server names is the one coordinate in the sea that cannot drift, so where
-- dead reckoning disagrees with it the server wins. ANCHORED rather than assigned: nothing
-- here assumes which way the layer numbers run or where they start, only that the layer and
-- z move together by one. The first layer seen ties the two together; every one after that
-- is checked against it.
local function anchor_layer(layer)
  if not layer then return end
  if not layer_base then layer_base = pos.z + layer return end
  local z = layer_base - layer
  if z ~= pos.z then
    note(string.format("layer %d: z corrected %d -> %d", layer, pos.z, z))
    pos.z = z
  end
end

-- ---------- MIP-fed truths ----------
local function get_seid()
  local s = tonumber(scrye.getState("vik.seid"))
  if s then return s end
  local ser = scrye.getState("vik.ser") or ""
  return tonumber(ser:match("%f[%w]SEID=(%d+)"))
end

-- the MIP feed knows the truth about combat: enemy name set = fighting
local function in_combat()
  local e = scrye.getState("enemy.name")
  return e ~= nil and e ~= ""
end

-- ---------- seid resting ----------
local function cs_rest_over()
  if not resting then return end
  resting = false
  local seid = get_seid()
  if rest_below > 0 and seid and seid < rest_below then
    -- still low: rest another round (cs_start_rest inlined below via recursion)
    resting = true
    rest_until = now + rest_secs
    note(string.format("Seid low (%s < %d) - resting %ds before moving on", tostring(seid), rest_below, rest_secs))
    scrye.after(rest_secs, cs_rest_over)
    cs_draw()
    return
  end
  note("Seid recovered - resuming")
  if enabled and not paused and not fighting then
    if plan then cs_advance()
    elseif auto then cs_step() end
  end
  cs_draw()
end

local function cs_start_rest(seid)
  resting = true
  rest_until = now + rest_secs
  note(string.format("Seid low (%s < %d) - resting %ds before moving on", tostring(seid), rest_below, rest_secs))
  scrye.after(rest_secs, cs_rest_over)
  cs_draw()
end

-- ---------- walking (one confirmed move at a time) ----------
-- plan = { dirs = {...}, idx = n, awaiting = bool, kind = "explore"|"leave", entry = frontier-entry }

cs_advance = function()
  if not enabled or not plan or plan.awaiting or paused or resting then return end
  if fighting or in_combat() then fighting = true return end
  if plan.idx > #plan.dirs then
    local kind = plan.kind
    plan = nil
    if kind == "explore" then
      steps_done = steps_done + 1
      -- Deliberately NOT cs_step() here. This runs on the PROMPT, and the prompt can arrive
      -- before Room.Map does -- Room.Map being what ends an arrival burst. Stepping straight
      -- on would put the next move on the wire while the burst for the room we are standing
      -- in is still coming, and that burst's Room.Map would then confirm the step that had
      -- only just gone out. The real arrival would find nothing awaiting it, and mid-fight
      -- that reads as a wimpy retreat.
      --
      -- What it does instead is ASK whether the burst has ended, and `armed` is exactly that
      -- answer: only Room.Map sets it. So when Room.Map has already been and gone, stepping
      -- straight on is safe and the walk keeps its old pace; when it has not, this does
      -- nothing and the arming itself starts the step on the next prompt.
      --
      -- Waiting unconditionally was wrong, and visibly so: after every kill the prompt that
      -- retires the plan is the one AFTER the fight, by which time Room.Map is long past.
      -- The bot sat there until the watchdog nudged it, twenty seconds a corpse.
      if auto then cs_step() end   -- the gate in cs_advance decides whether it may go
    else
      note("arrived back at start")
      auto = false
    end
    cs_draw()
    return
  end
  -- THE GATE, and the only one. Every movement this plugin ever sends leaves through the
  -- line below -- the idle step, the continuation of a route, the blind re-orient step -- so
  -- this is where the rule belongs rather than in each caller.
  --
  -- The rule: a step may go out only once Room.Map has ended the burst for the room we are
  -- standing in, and `armed` is exactly that fact -- nothing else sets it. Send inside a
  -- burst and that burst's own Room.Map, still in flight, confirms the step which has only
  -- just left; pos then advances twice for one real move, two rooms land on one coordinate,
  -- and the router starts planning through exits that are not there. The symptom is a
  -- refusal for a direction the room visibly does not have.
  --
  -- Gating the callers one at a time missed this. The CONTINUATION of a multi-step route
  -- goes out from here on the prompt, which can beat Room.Map -- so single-step plans were
  -- fine and long routes drifted, which is why it only happened sometimes.
  if not armed then return end
  armed = false
  plan.awaiting = true
  plan.sent_at = now            -- anything older than this is not what is printing
  -- The room we are leaving stops being interesting the moment we commit to leaving it, and
  -- clearing HERE rather than on arrival is what makes a Room.Info-less move safe: the new
  -- room's Room.Contents can land before anything confirms we arrived.
  cs_flags.item = false; cs_flags.gold = false; cs_flags.player = false
  room_goal_idx = nil; room_goal_word = nil
  pending_mob = nil
  contents_seen = false
  last_dir = plan.dirs[plan.idx]
  scrye.send(plan.dirs[plan.idx])
end

-- ---------- arrival (Room.Info) ----------
-- Room.Info is sent when you CHANGE ROOM and at no other time -- 'look' and '!glance'
-- produce none -- so this firing IS a move. None of the old "was that our step, or a
-- redisplay somebody else asked for?" reasoning is needed; what remains is only WHOSE
-- move it was, and the bot already knows whether it ordered one.
local function on_room_info(json)
  if not enabled then return end
  local ok, info = pcall(scrye.json.decode, json)
  if not ok or type(info) ~= "table" then
    note("Room.Info did not decode as an object - ignoring")
    return
  end
  -- Keys AND destinations. The number itself says nothing -- every maze room is 60494 -- but
  -- whether it is ZERO is the server's own record of which exits you have walked, which is
  -- worth more than anything dead reckoning can work out. Sorted so the exit list is stable
  -- between rooms and can be compared.
  local keys, dests = {}, {}
  if type(info.exits) == "table" then
    for d, v in pairs(info.exits) do
      local k = tostring(d):lower()
      keys[#keys + 1] = k
      local n = tonumber(v)
      if n then dests[k] = n end     -- unparseable: leave it unknown rather than guess
    end
    table.sort(keys)
  end
  local layer = layer_of(info.name)
  local tp = teleported
  teleported = false

  -- Two ways the arrival is not ours to confirm.
  --
  -- A teleport: the sea moved us, so the room is real but our position is not. Nothing in
  -- the payload can tell us where we are -- every maze room is num 60494 -- so the honest
  -- answer is to stop and say so, exactly as a wimpy retreat does.
  --
  -- Or we are not in the maze at all. The layer rooms are named "Layer N of the Sea of
  -- Chaos"; the sea's entrance is "A swirling Sea of Chaos", num 266, area "The Sea of
  -- Chaos", with 'out' for an exit. A room that names no layer is not one of the maze rooms
  -- this bot maps, and mapping it would put a room that does not belong onto the grid.
  if not paused and (tp or not layer) then
    plan = nil
    paused = true
    -- Three cases, and they need three different things done about them. You cannot leave
    -- the maze by accident -- the only ways out are the 'out' exit (never queued), 'retreat
    -- from the sea', and 'enter portal' at the cask -- so a room outside it means either the
    -- sea threw us out or you took one of those deliberately. Either way the recovery is to
    -- go back IN, not to reset a map that is about to be replaced.
    if tp and not layer then
      note(string.format("the sea threw us out to %s - 'enter sea' to go back in.", tostring(info.name)))
      pnotify("Chaos sea: thrown out of the maze - bot paused")
    elseif tp then
      note("teleported inside the maze - pos is now unknown. 'cs reset' here, then unpause.")
      pnotify("Chaos sea: teleported - bot paused, position unknown")
    else
      note(string.format("left the maze (%s) - bot PAUSED.", tostring(info.name)))
      pnotify("Chaos sea: left the maze - bot paused")
    end
    cs_draw()
    return
  end

  -- A retreat we were told about, with the direction it took. Apply it: the map stays
  -- right, the pause happens at a position we actually know, and 'cs pause' is enough to
  -- carry on -- no 'cs set x y z' guessing.
  if wimpy_dir and not (plan and plan.awaiting) then
    local d = wimpy_dir
    wimpy_dir = nil
    local p2 = moved(pos, d)
    if p2 then pos = p2 end
    add_room(pos)
    plan = nil
    paused = true
    fighting = false
    set_exits(keys, dests)
    note(string.format("WIMPY - your legs ran away with you %s. Bot PAUSED at [%d %d %d];"
      .. " 'cs pause' to carry on.", d, pos.x, pos.y, pos.z))
    pnotify("Chaos sea: wimpy retreat " .. d .. " - bot paused")
    cs_draw()
    return
  end

  if plan and plan.awaiting then
    -- the bot's own move arriving: always confirm it, even mid-pause
    local p2 = moved(pos, plan.dirs[plan.idx])
    if p2 then pos = p2 end
    add_room(pos)
    plan.idx = plan.idx + 1
    plan.awaiting = false
    last_typed = nil          -- our step, not yours: do not let it explain a later move
    anchor_layer(layer)
    if paused then
      set_exits(keys, dests)         -- it's the bot's own maze room: map it
      cs_draw()
      return
    end
  elseif paused then
    -- Frozen: rooms you walk through while paused are ignored. This also covers the New Sea
    -- swap, which runs entirely while paused and changes room twice on the way -- 'retreat
    -- from the sea' lands somewhere that is not in the sea at all, and mapping it would seed
    -- the new maze with a phantom room and a frontier pointing at rooms that do not exist.
    return
  elseif fighting then
    local typed = typed_recently()
    if typed then
      -- You walked out of the fight yourself. Still worth stopping for: the bot was mid-kill
      -- and you have taken the controls.
      note("You walked " .. typed .. " during a fight - bot PAUSED. Walk back to the fight room,")
      pnotify("Chaos sea: you moved mid-fight - bot paused")
      paused = true
      note("then 'cs pause' to continue (or 'cs set x y z' if unsure where you are).")
      cs_draw()
      return
    end
    -- Nothing ordered a move and nothing announced one. A retreat SAYS so and names its
    -- direction, so this is not one: the maze rearranged itself around us and the server
    -- re-sent the room because its exits changed. We have not moved. Re-read the exits and
    -- carry on fighting -- calling this a wimpy is what was pausing the bot mid-kill and
    -- leaving the monster it had just walked up to unattacked.
    set_exits(keys, dests)
    cs_draw()
    return
  end
  anchor_layer(layer)
  cs_flags.item = false; cs_flags.gold = false; cs_flags.player = false
  room_goal_idx = nil; room_goal_word = nil
  pending_mob = nil
  contents_seen = false          -- a new room: nothing has spoken for it yet
  set_exits(keys, dests)
  -- NOT armed here. Room.Info is the FIRST thing in an arrival burst, not the last; arming
  -- on it let the bot send its next step while Room.Contents and Room.Map for the room it
  -- was standing in were still in flight -- and Room.Map confirms an outstanding step, so
  -- the tail of the old burst would confirm the step that had only just gone out. The step
  -- then had nothing awaiting it when the real arrival landed, and a Room.Info during a
  -- fight with no step outstanding reads as a wimpy retreat. It was not one.
  cs_draw()
end

-- the GOAL: a room with a cask (or whatever 'cs goal' is set to)
local function cs_reach_goal(gw)
  -- target item (cask/portal) is in the room: just PAUSE the bot. paused is the
  -- robust stop - cs_step/cs_advance/cs_on_prompt/watchdog all honour it.
  paused = true
  plan = nil
  pending_mob = nil
  room_goal_idx = nil; room_goal_word = nil   -- handled; don't re-pause on unpause
  note(string.upper(gw) .. " found - bot PAUSED ('cs pause' / Pause button to continue).")
  pnotify("Chaos sea: " .. string.upper(gw) .. " found - bot paused")
  cs_draw()
end

local function cs_on_item(line)
  if not enabled or paused then return end
  cs_flags.item = true
  if not goal_found then
    local low = line:lower()
    for i, gw in ipairs(goals) do
      -- whole-word match: gw must be bounded by non-word chars (or line ends),
      -- so "cask" does not fire on "casket"/"cascade". gw is alnum/underscore
      -- only (see parse_goals), so it needs no pattern-escaping.
      if low:find("%f[%w]" .. gw .. "%f[%W]") then
        -- record only the highest-priority goal in the room (lower index in
        -- `goals` = higher priority). The action runs at the prompt, so cask
        -- beats portal no matter which item line printed first.
        if not room_goal_idx or i < room_goal_idx then
          room_goal_idx = i
          room_goal_word = gw
        end
        break
      end
    end
  end
end

local function cs_blocked()
  if not enabled or not plan or not plan.awaiting then return end
  local d = plan.dirs[plan.idx]
  plan.awaiting = false
  local room = get_room(pos)
  if room and d then room.exits[d] = nil end
  -- The refused step consumed the arming, and no Room.Map follows a move that did not
  -- happen -- so nothing would re-arm us and the bot would sit here. We are still standing
  -- in a room whose burst ended long ago; say so.
  armed = true
  note("blocked going " .. (d or "?") .. " - exit removed, replanning")
  if plan.entry then table.insert(frontier, 1, plan.entry) end   -- don't lose the in-flight target
  plan = nil
  if auto then scrye.after(1, function() cs_step() end) end
  cs_draw()
end

-- ---------- mobs / combat ----------
local function cs_on_mob(name)
  if not enabled or fighting or paused then return end
  if excluded[name] then return end
  if name:match("Tibbers the bright calico cat") or name:match("otter") then return end
  -- don't attack yet: wait for the prompt so we know if a player is here
  pending_mob = killname
end

local function cs_on_player(name)
  if not enabled then return end
  if in_party(name) then return end
  cs_flags.player = true
end

local function cs_no_mob(name)
  if not (enabled and fighting) then return end
  if name ~= killname then return end      -- only OUR target counts as room-clear
  if in_combat() then return end           -- something is still hitting us
  fighting = false
  idle_fight_prompts = 0
  if plan then scrye.after(1, function() cs_advance() end)
  elseif auto then scrye.after(1, function() cs_step() end) end
  cs_draw()
end

local function cs_after_kill()
  if not (enabled and fighting) then return end
  if in_combat() then
    -- still fighting the next one: check again soon instead of giving up
    scrye.after(2, cs_after_kill)
    return
  end
  -- the room may hold more mobs: try again; "There is no X here." resumes the walk
  scrye.send("kill " .. killname)
end

local function cs_killblow()
  if not (enabled and fighting) then return end
  kills = kills + 1
  cs_draw()          -- the count is on the panel; show it now, not at the next redraw
  -- wait kill_delay so your own killing-blow triggers act first
  scrye.after(math.max(1, kill_delay), cs_after_kill)
end

-- ---------- exits from the line-of-sight map ----------
-- Room.Map draws the rooms in sight as a grid: rooms every 2 columns and 2 rows, with the
-- link characters between them. The links touching '@' ARE the current room's exits.
--
-- This exists because Room.Info is not sent for every move. The server appears to send it
-- only when the payload CHANGES, and in the maze every room is num 60494, area "Unknown",
-- named for its layer -- so stepping between two rooms with the same exit set produces a
-- byte-identical payload and nothing goes out at all. Room.Map is sent every time.
--
-- Planar exits only. Up and down are not read from the payload's 'up'/'down' fields: they
-- have been 0 in every capture, so what a non-zero value means is unverified, and a phantom
-- down exit is the most damaging kind of error this bot can make -- DOWN always wins.
-- A room that gains or loses a 'd' has an exit set that DIFFERS, so Room.Info is sent for
-- it, and the map is not the only thing describing it.
local function exits_from_map(m)
  local rows = m and m.rows
  if type(rows) ~= "table" then return {} end
  local function at(r, c)
    local line = rows[r]
    if type(line) ~= "string" or c < 1 then return " " end
    return line:sub(c, c)
  end
  local r0, c0
  for r = 1, #rows do
    local line = rows[r]
    if type(line) == "string" then
      local c = line:find("@", 1, true)
      if c then r0, c0 = r, c break end
    end
  end
  if not r0 then
    -- No '@'. That is not a broken map: when the room you are standing in has a down exit,
    -- the marker for it REPLACES you at your own cell -- 'v' where '@' would be. '@' has
    -- been horizontally centred in every capture (col 8 of w:17), so look down that column
    -- for a marker instead.
    --
    -- What is deliberately NOT done here is turning that 'v' into a 'd' exit. Reading the
    -- position slightly wrong shows up immediately, because the exits then disagree with
    -- Room.Info. A phantom DOWN sends the bot hunting a descent that does not exist, and
    -- DOWN outranks everything in valid_entry -- the costs are not symmetric, and one
    -- sighting is not enough to spend the expensive one on. A room that HAS a 'd' has an
    -- exit set that differs from its neighbour's, so Room.Info is sent for it anyway.
    local mid = math.floor((tonumber(m.w) or 17) / 2) + 1
    for r = 1, #rows do
      local ch = at(r, mid)
      if ch == "v" or ch == "^" or ch == "+" then r0, c0 = r, mid break end
    end
  end
  -- Still nothing to stand on. Return nil rather than an empty list: an empty list would be
  -- written over the room's exits as "dead end", which is a far worse answer than "I could
  -- not read this map, keep what Room.Info already told us".
  if not r0 then return nil end
  local function orth(ch) return ch == "-" or ch == "|" end
  local found = {}
  if orth(at(r0, c0 - 1)) then found[#found + 1] = "w" end
  if orth(at(r0, c0 + 1)) then found[#found + 1] = "e" end
  if orth(at(r0 - 1, c0)) then found[#found + 1] = "n" end
  if orth(at(r0 + 1, c0)) then found[#found + 1] = "s" end
  -- Diagonals are drawn with the slash that points along them. 'X' is two links crossing
  -- and says nothing about which of them touches us, so it is not read.
  if at(r0 - 1, c0 - 1) == "\\" then found[#found + 1] = "nw" end
  if at(r0 - 1, c0 + 1) == "/"  then found[#found + 1] = "ne" end
  if at(r0 + 1, c0 - 1) == "/"  then found[#found + 1] = "sw" end
  if at(r0 + 1, c0 + 1) == "\\" then found[#found + 1] = "se" end
  table.sort(found)
  return found
end

-- ---------- arrival with no Room.Info (Room.Map) ----------
-- The step landed, the server redrew the map, and said nothing about the room because there
-- was nothing new to say. Confirm from the map instead of sitting out the step timeout.
--
-- Nothing here clears the per-room flags. Room.Contents arrives BEFORE Room.Map in the
-- burst, so by now it may already have told us what is in the room we just walked into --
-- clearing here would throw that away. The clearing happens when the step is SENT.
local function on_room_map(json)
  if not enabled or paused then return end
  local ok, m = pcall(scrye.json.decode, json)
  if not ok or type(m) ~= "table" then return end
  if not (plan and plan.awaiting) then
    -- Not an arrival -- Room.Map also fires when something moves in sight. Arm anyway: it
    -- is the end of whatever burst it belongs to, and arming is only permission to think
    -- about a step, not a step.
    armed = true
    return
  end

  if teleported then
    teleported = false
    plan = nil
    paused = true
    note("teleported - the sea moved us and pos is now unknown. 'cs reset' here, then unpause.")
    pnotify("Chaos sea: teleported - bot paused, position unknown")
    cs_draw()
    return
  end

  local p2 = moved(pos, plan.dirs[plan.idx])
  if p2 then pos = p2 end
  add_room(pos)
  plan.idx = plan.idx + 1
  plan.awaiting = false
  last_typed = nil
  local ex = exits_from_map(m)
  if ex then set_exits(ex) end   -- unreadable map: keep what we were told, do not blank it
  -- The burst is over: the room is as described as it is going to get, and only now may the
  -- next step go out. Everything after this belongs to the room we walk into next.
  armed = true
  cs_draw()
end

-- ---------- the sea's own room header ----------
-- The one arrival signal the server never suppresses.
--
-- GMCP withholds ANY package whose payload has not changed since the last one -- Room.Info,
-- Room.Map and Room.Contents alike. Walk a uniform corridor and all three can be identical
-- to the room before, so NOTHING is sent and there is no arrival to detect. Captured over
-- four steps in one corridor: two produced no Room.Map whatsoever, and 'look' produces no
-- GMCP at all.
--
-- The MUD's own header prints every time regardless, carries the exits, and needs no display
-- setting switched on:
--     Layer five of the Sea of Chaos (s,n)
-- It is also the LAST thing in a room's output before the prompt, which makes it a better
-- burst terminator than Room.Map: nothing can still be in flight behind it.
--
-- 'look' prints it too, which is why only the CONFIRM half is gated on a step being
-- outstanding. Typing 'look' in the moment a step is in flight would confirm it wrongly --
-- the same risk the old '=S=' design carried, and rarer than the stall it replaces.
local function on_sea_header(exitstr)
  if not enabled or paused then return end
  local keys = {}
  for d in tostring(exitstr or ""):gmatch("[^,%s]+") do keys[#keys + 1] = d:lower() end
  table.sort(keys)

  if plan and plan.awaiting then
    if teleported then
      teleported = false
      plan = nil
      paused = true
      note("teleported - the sea moved us and pos is now unknown. 'cs reset' here, then unpause.")
      pnotify("Chaos sea: teleported - bot paused, position unknown")
      cs_draw()
      return
    end
    local p2 = moved(pos, plan.dirs[plan.idx])
    if p2 then pos = p2 end
    add_room(pos)
    plan.idx = plan.idx + 1
    plan.awaiting = false
    last_typed = nil
    if #keys > 0 then set_exits(keys) end
    cs_draw()
  end
  -- Arms whether or not it confirmed anything: the header is the end of the room's output
  -- either way, and that is all `armed` claims.
  armed = true
end

-- ---------- room contents (Room.Contents) ----------
-- Typed, named and counted, so the mob / player / item triggers -- and the '=M=', '=P='
-- and '=A|W|I=' display markers they depend on -- are not needed to read a room.
--
-- It is NOT sent for every room -- 78 messages against 134 arrivals in one capture -- but
-- when it does come for an empty room it comes as { "full": 1, "items": [] }, an explicit
-- "nothing here" rather than silence. So absence still proves nothing and the flags are
-- cleared on ARRIVAL; an empty payload is simply a room with nothing to loop over.
-- Ordering is settled by capture: Room.Info, then Room.Contents, then Room.Map.
--
-- Names arrive whole ("A warband in service to Goran [Legendary] [Grey-bearded] [6]"), and
-- go to the same handlers the text triggers fed, so `excluded` and the party whitelist keep
-- working -- provided their entries match the name the SERVER gives, which is not always
-- the one the '=M=' line printed.
local function on_room_contents(json)
  if not enabled then return end
  local ok, info = pcall(scrye.json.decode, json)
  if not ok or type(info) ~= "table" then
    note("Room.Contents did not decode as an object - ignoring")
    return
  end
  -- Set even for an EMPTY payload. Room.Contents arrives as { "full": 1, "items": [] } for
  -- a room with nothing in it, which is a positive statement that the room is empty -- and
  -- the marker triggers must stand down for it just as firmly as for a room full of mobs.
  contents_seen = true
  if type(info.items) ~= "table" then return end
  for _, it in ipairs(info.items) do
    if type(it) == "table" then
      local name = tostring(it.name or "")
      local kind = tostring(it.type or ""):lower()
      if name ~= "" then
        -- Anything not named a monster or a player is treated as an item, which is what
        -- the '=A|W|I=' markers covered between them: armour, weapon, item. A type we have
        -- never seen is more likely to be another kind of thing lying there than a
        -- creature, and reading it as an item risks nothing worse than a goal match.
        if kind == "monster" then cs_on_mob(name)
        elseif kind == "player" then cs_on_player(name)
        else cs_on_item(name) end
      end
    end
  end
end

-- ---------- prompt: goals + keep the plan moving ----------
local function cs_on_prompt()
  if not enabled or paused or resting then return end
  -- whole room is printed now: if a goal item was here, act on the
  -- highest-priority one (cask before portal), ignoring print order.
  if not goal_found and room_goal_idx then
    cs_reach_goal(room_goal_word)
    return
  end
  if pending_mob and not fighting and not in_combat() then
    local mob = pending_mob
    pending_mob = nil
    if cs_flags.player then
      note("player in the room - leaving the mob alone, moving on")
    else
      fighting = true
      scrye.send("kill " .. mob)
      cs_draw()
      return
    end
  end
  if fighting or in_combat() then
    if in_combat() then
      fighting = true
      idle_fight_prompts = 0
    elseif fighting then
      -- flagged as fighting but MIP says no enemy (mob fled / killed by
      -- someone else): after a few prompts, probe the room to recover
      idle_fight_prompts = idle_fight_prompts + 1
      if idle_fight_prompts >= 3 then
        idle_fight_prompts = 0
        scrye.send("kill " .. killname)
      end
    end
    return
  end
  if plan then
    cs_advance()
  elseif auto and armed then
    cs_step()          -- cs_advance consumes `armed` when the step actually goes out
  end
end

-- ---------- frontier selection: DOWN exits always win ----------
local function valid_entry(e)
  local from_room = get_room({ x = e.x, y = e.y, z = e.z })
  local t = moved({ x = e.x, y = e.y, z = e.z }, e.dir)
  if not (t and from_room and from_room.exits[e.dir]) then return nil end
  -- Unexplored by the server's reckoning, or by ours. The server's is the one that survives a
  -- coordinate collision; ours is the fallback for an exit it has told us nothing about.
  if from_room.exits[e.dir] == "new" then return t end
  if not get_room(t) then return t end
  return nil
end

-- One pass over the pile, newest first, validating each entry once. Returns the
-- candidate list in pop priority order: an unexplored down exit in the CURRENT room
-- first, then queued down exits newest-first, then the rest newest-first. Consumes
-- the frontier -- stale entries are pruned, valid ones become candidates, and
-- cs_step's stash/restore puts the losers back.
--
-- The dive candidate is synthesized from the room's own exits, NOT taken from the
-- pile -- which is why this is one pass and not a pop-until-empty loop: popping
-- repeatedly re-synthesized the same dive forever (the instruction budget caught
-- exactly that as "script exceeded its execution budget" mid-exploration).
local function collect_candidates()
  local dive = nil
  local cur = get_room(pos)
  if cur then
    for _, dd in ipairs({ "d", "down" }) do
      if not dive and cur.exits[dd] then
        local t = moved(pos, dd)
        if t and not get_room(t) then
          dive = { entry = { x = pos.x, y = pos.y, z = pos.z, dir = dd }, target = t }
        end
      end
    end
  end
  local downs, rest = {}, {}
  for i = #frontier, 1, -1 do
    local e = frontier[i]
    -- entries duplicating the dive are consumed with it, like the old step 1 did
    local dup = dive and e.x == pos.x and e.y == pos.y and e.z == pos.z
                and (e.dir == "d" or e.dir == "down")
    if not dup then
      local t = valid_entry(e)
      if t then
        local list = (e.dir == "d" or e.dir == "down") and downs or rest
        list[#list + 1] = { entry = e, target = t }
      end
    end
    frontier[i] = nil
  end
  local out = {}
  if dive then out[#out + 1] = dive end
  for _, c in ipairs(downs) do out[#out + 1] = c end
  for _, c in ipairs(rest) do out[#out + 1] = c end
  return out
end

-- ---------- pause / stepping ----------
local function cs_pause_toggle()
  if sea_entering then note("swapping seas - the new one comes up unpaused") return end
  paused = not paused
  if paused then
    note("PAUSED - 'cs pause' (or the Pause button) to continue")
  else
    note("resuming")
    if enabled and not fighting and not in_combat() then
      if plan then cs_advance()
      elseif auto then scrye.after(1, function() cs_step() end) end
    end
  end
  cs_draw()
end

-- `asked` marks a step YOU asked for -- the 'cs step' command or the Step button -- as
-- opposed to one the bot took on its own.
--
-- The burst gate in cs_advance refuses to send until Room.Map or the room header has ended
-- the arrival burst, which is what stops a step being sent inside somebody else's output and
-- confirmed by it. Nothing has ended a burst when you have only just enabled the bot on a map
-- restored from the store, so a deliberate 'cs step' would sit there doing nothing at all,
-- silently. One step you asked for is not the race the gate exists to stop: the automatic
-- paths -- the walk continuation, the watchdog, the post-kill resume -- stay gated, and they
-- are the ones that fire in the middle of arriving output.
cs_step = function(asked)
  if not enabled then note("not enabled - 'cs enable' first") return end
  if paused then note("paused - unpause first") return end
  if resting then note("resting (Seid low) - back in " .. math.max(0, rest_until - now) .. "s") return end
  if asked then armed = true end
  if rest_below > 0 then
    local seid = get_seid()
    if seid and seid < rest_below then
      cs_start_rest(seid)
      return
    end
  end
  if fighting or in_combat() then
    fighting = true
    note("in combat - will continue when the room is clear")
    return
  end
  if plan then cs_advance() return end
  -- Candidates in pop-priority order (collect_candidates prunes stale entries).
  -- With the wasm pathfinder loaded, ONE delegated sweep answers the whole list — the
  -- reply's index is the first reachable candidate, exactly what the old
  -- one-bfs-per-candidate loop computed. Without it, the same loop runs locally.
  local candidates = collect_candidates()
  local chosen, path = nil, nil
  if #candidates > 0 then
    local tgts = {}
    for i, c in ipairs(candidates) do
      tgts[i] = { x = c.entry.x, y = c.entry.y, z = c.entry.z }
    end
    local r = ask_pathfinder({ targets = tgts }, false)
    if r then
      if r.found and r.index then chosen, path = r.index, r.dirs or {} end
    else
      -- Local fallback: BFS per candidate until one is reachable, as before the wasm
      -- pathfinder existed -- but bounded, so a pile of unreachable exits degrades to
      -- the blind-step recovery below instead of tripping the instruction budget.
      for i = 1, math.min(#candidates, 40) do
        local c = candidates[i]
        local p = bfs(pos, { x = c.entry.x, y = c.entry.y, z = c.entry.z })
        if p then chosen, path = i, p break end
      end
    end
  end
  local entry, target
  local stash = {}
  if chosen and candidates[chosen] then
    entry = candidates[chosen].entry
    target = candidates[chosen].target
    for i = 1, chosen - 1 do stash[#stash + 1] = candidates[i].entry end
    -- candidates past the winner were never really "popped": restore them so the
    -- next pop sees them in the same order
    for i = #candidates, chosen + 1, -1 do frontier[#frontier + 1] = candidates[i].entry end
  else
    for i = 1, #candidates do stash[#stash + 1] = candidates[i].entry end
  end
  -- unreachable exits go back at the BOTTOM of the pile, never lost
  for i = #stash, 1, -1 do table.insert(frontier, 1, stash[i]) end
  if not entry then
    -- The sea maze shifts, so our dead-reckoned map can go stale and BFS finds no route to any
    -- unexplored exit. Rather than dropping auto, step blindly through a REAL exit of this room
    -- (exits are re-parsed accurately every room) to re-orient, then keep exploring. The fresh
    -- room's exits become a reachable frontier, so normal pathing resumes on the next tick.
    -- Not conditional on there being unreachable exits left. An EMPTY frontier in the sea
    -- does not mean the sea is explored -- it means our dead-reckoned map says so, and in a
    -- maze that rearranges itself that is a statement about the map, not about the sea.
    -- Two different rooms collide onto one x,y often enough that every neighbour ends up
    -- "known" while most of the sea has never been walked; the giveaway is the router
    -- planning a direction the room in front of you does not have, and being told so.
    -- Blind-stepping is the same remedy either way: walk a real exit, let the fresh room's
    -- exits become a reachable frontier, and normal pathing resumes.
    if auto and blind_steps < 20 then
      local cur = get_room(pos)
      local choices = {}
      if cur then
        for d in pairs(cur.exits) do
          if DELTA[d] and d ~= "u" and d ~= "up" and d ~= OPP[last_dir] then choices[#choices + 1] = d end
        end
        if #choices == 0 then   -- only the way we came: take it rather than stall
          for d in pairs(cur.exits) do
            if DELTA[d] and d ~= "u" and d ~= "up" then choices[#choices + 1] = d end
          end
        end
      end
      if #choices > 0 then
        blind_steps = blind_steps + 1
        local d = choices[math.random(#choices)]
        note(string.format("maze shifted - blind step %s (re-orient %d) from [%d %d %d]",
          d, blind_steps, pos.x, pos.y, pos.z))
        plan = { dirs = { d }, idx = 1, awaiting = false, kind = "explore" }
        cs_advance(); cs_draw()
        return
      end
    end
    -- Nothing left to try: no route, and no exit to step blindly through either (or twenty
    -- re-orients have not found one). Only now is stopping the honest answer.
    if #stash > 0 then
      note(string.format("%d unexplored exits but no path and no exit to blind-step from [%d %d %d] - stopping",
        #stash, pos.x, pos.y, pos.z))
      pnotify("Chaos sea: stuck - unexplored exits but no path (bot stopped)")
    elseif blind_steps >= 20 then
      note("20 blind steps and still no reachable exit - stopping. 'cs reset' here clears a stale map.")
      pnotify("Chaos sea: re-orienting got nowhere - bot stopped")
    else
      note("no exits to walk at all - stopping")
      pnotify("Chaos sea: nowhere to go - bot stopped")
    end
    auto = false
    blind_steps = 0
    cs_draw()
    return
  end
  blind_steps = 0   -- found a reachable exit: real progress, reset the re-orient counter
  if #stash > 0 then
    note(string.format("(%d unreachable exits kept for later)", #stash))
  end
  path[#path + 1] = entry.dir
  plan = { dirs = path, idx = 1, awaiting = false, kind = "explore", entry = entry }
  note(string.format("[%d %d %d] -> [%d %d %d]: %d steps",
    pos.x, pos.y, pos.z, target.x, target.y, target.z, #path))
  cs_advance()
  cs_draw()
end

local function cs_leave()
  if plan and plan.entry then table.insert(frontier, 1, plan.entry) end
  plan = nil
  auto = false
  local path
  local r = ask_pathfinder({ to = { x = 0, y = 0, z = 0 } }, true)
  if r then path = r.found and (r.dirs or {}) or nil
  else path = bfs(pos, { x = 0, y = 0, z = 0 }, true) end   -- going home may need to climb up
  if not path then note("no path back to start!") return end
  if #path == 0 then note("already at start") return end
  note("returning to 0 0 0 (" .. #path .. " steps)")
  plan = { dirs = path, idx = 1, awaiting = false, kind = "leave" }
  cs_advance()
end

local function reset()
  rooms = {}; frontier = {}
  wimpy_dir = nil              -- ...and a retreat nobody acted on
  contents_seen = false        -- ...and what we thought had described the room
  teleported = false           -- ...and a teleport nobody acted on
  last_typed = nil             -- "start over" includes what we thought explained a move
  layer_base = nil             -- a new sea re-numbers the layers: re-anchor on the next one
  map_serial = map_serial + 1
  pos = { x = 0, y = 0, z = 0 }
  plan = nil; fighting = false; steps_done = 0; kills = 0
  goal_found = false; paused = false
  add_room(pos)
  cs_draw()
end

-- ---------- watchdog (every 5 s) ----------
local stuck_since = nil
local idle_ticks = 0
local cs_last_sea_min = nil

local function cs_watchdog()
  -- tick the sea-age timer even while idle/paused: redraw once per minute change
  if sea_time then
    local m = math.floor((now - sea_time) / 60)
    if m ~= cs_last_sea_min and m <= 60 then cs_last_sea_min = m; cs_draw() end
  end
  if not enabled or paused or resting or goal_found then
    stuck_since = nil
    idle_ticks = 0
    return
  end
  if fighting and not in_combat() then
    -- fight flag stuck but the mud says no enemy (missed killblow, fled mob...)
    stuck_since = stuck_since or now
    if now - stuck_since >= 5 then
      stuck_since = nil
      note("watchdog: combat seems over - checking the room")
      scrye.send("kill " .. killname)
    end
    return
  end
  stuck_since = nil
  -- auto mode but nothing happening at all for ~20s: nudge it
  if auto and not plan and not fighting and not in_combat() then
    idle_ticks = idle_ticks + 1
    if idle_ticks >= 4 then
      idle_ticks = 0
      note("watchdog: nudging the explorer")
      cs_step()
    end
  else
    idle_ticks = 0
  end
end

-- ---------- a step that was never confirmed (every second) ----------
-- A step went out and no Room.Info came back. With GMCP that is evidence rather than a
-- guess -- the server reports every room change -- so after a few seconds the step did not
-- land. Re-sending the direction is not an option: if it DID land and only the report was
-- lost, sending it again walks us one room further than we think.
--
-- The likeliest cause in the sea is the maze moving the exit out from under the step, which
-- is why the exit is DROPPED as well as the plan. Without that, the router keeps finding
-- the same phantom exit, walking into it, and waiting again -- which is what a stuck bot
-- looks like from the outside. Nothing is lost by being wrong: the next arrival in that
-- room rewrites its exits from Room.Info wholesale.
--
-- On its own second, not the 5 s watchdog's: three seconds of silence is the whole of the
-- pause a player sees, and waiting three watchdog ticks made it fifteen.
local STEP_TIMEOUT = 3
local awaiting_secs = 0

local function cs_step_timeout()
  if not enabled or paused or resting then awaiting_secs = 0 return end
  if not (plan and plan.awaiting) then awaiting_secs = 0 return end
  awaiting_secs = awaiting_secs + 1
  if awaiting_secs < STEP_TIMEOUT then return end
  awaiting_secs = 0
  local d = plan.dirs[plan.idx]
  -- The exit is NOT dropped any more. A wall announces itself -- "You cannot go north."
  -- fires cs_blocked at once, with evidence, and removes it there. Silence is not evidence
  -- of a wall; it has an innocent cause. Walk a uniform corridor and every room is the same
  -- layer, the same exits, the same num, and the same picture on the LOS map -- so nothing
  -- in the payload CHANGES and the server sends nothing at all. There is no arrival signal
  -- to miss, and dropping an exit each time deleted the corridor out from under the bot:
  -- first 'n', then 's', until the room had no exits and it stopped with "no exit to
  -- blind-step from". It had walked into a perfectly ordinary corridor and dismantled it.
  note(string.format("no room from '%s' in %ds - the step is unconfirmed, replanning (the exit is kept)",
    tostring(d or "?"), STEP_TIMEOUT))
  plan.awaiting = false
  armed = true       -- as with a refusal: we never left, so nothing else would re-arm us
  if plan.entry then table.insert(frontier, 1, plan.entry) end
  plan = nil
  if auto then scrye.after(1, function() cs_step() end) end
  cs_draw()
end

-- ---------- new sea (only while paused at the cask) ----------
-- The one button you press at the cask: loot it, swap to a fresh sea, throw the old
-- maze away and glance at the room you land in. Everything up to and including the
-- reset used to be four separate clicks (open cask / get all / New Sea / Reset).
local function cs_new_sea()
  if sea_entering then note("already swapping seas - hold on") return end
  if not paused then note("New Sea only works while paused (at the cask)") return end
  -- the game's `unsetsea` only works once the sea is 60 min old; refuse until then so
  -- we never fire the sequence early (which would leave you in the same sea). When there
  -- is no active timer we can't tell the age, so allow it and let the game decide.
  -- `now` is seconds since plugin load, not wall clock, so a sea_elapsed restored from
  -- the store stops ageing while Scrye is closed. Only enforce the 60 min guard when we
  -- started this sea in this session and can therefore trust the clock; otherwise let the
  -- game reject an early `unsetsea` itself (which is what the MUSHclient version did).
  if sea_time and sea_started_here then
    local elm = math.floor((now - sea_time) / 60)
    if elm < 60 then
      note(string.format("sea only %dm old - new sea in %dm (unsetsea needs 60m)", elm, 60 - elm))
      return
    end
  end
  note("looting the cask & starting new sea #" .. seanum)
  sea_entering = true
  scrye.send("open cask")
  scrye.after(1, function() scrye.send("get all") end)     -- the cask is why you came
  scrye.after(2, function() scrye.send("retreat from the sea") end)
  scrye.after(3, function() scrye.send("unsetsea") end)
  scrye.after(4, function() scrye.send("setsea " .. seanum) end)
  -- The reset goes HERE, immediately before the command that enters the new sea, and not
  -- after it. A new sea is a NEW MAZE, so the old map is not stale, it is wrong; reset()
  -- drops it, recentres on 0,0,0 and clears paused/goal_found. Entering IS a room change,
  -- so Room.Info maps the arrival room and arms the stepper by itself -- but only if the
  -- map it lands in is already the fresh one. Resetting afterwards, as this did while a
  -- glance was doing the mapping, would wipe the one arrival that mattered and leave the
  -- bot in a new sea with no room and nothing able to seed it.
  scrye.after(5, function()
    reset()                  -- clears the old maze, the goal flag and the pause
    sea_entering = false     -- ...and from here the next arrival is the new sea's own
    scrye.send("enter sea")
  end)
  scrye.after(7, function()
    if auto then
      note("new sea #" .. seanum .. " ready - auto-exploring")
    else
      note("new sea #" .. seanum .. " ready - press Auto (or Step) to go")
    end
    cs_draw()
  end)
  sea_time = now   -- start the sea-age clock
  sea_started_here = true
  cs_last_sea_min = nil
  cs_draw()
end

-- adjust the sea number New Sea will use (clamped 1-120); persists + redraws via cs_draw
local function cs_set_seanum(n)
  seanum = math.max(1, math.min(120, math.floor(tonumber(n) or seanum)))
  note("sea number set to " .. seanum)
  cs_draw()
end

-- ---------- HUD panel state (replaces the miniwindow) ----------
local COLS, ROWS = 20, 16   -- same viewport as the original 280x224 @ 14px cells

cs_draw = function()
  -- exposed for other plugins, as the original did with SetVariable
  scrye.setVariable("cs_auto", auto and "1" or "0")
  scrye.setVariable("cs_enabled", enabled and "1" or "0")

  -- The buttons carry the bot's state in their colour, so the panel has to be rebuilt
  -- when that state changes -- but ONLY then. cs_draw runs on every room and every
  -- watchdog tick, and replacing the panel at that rate would be wasteful and visibly
  -- twitchy for a picture that has not changed.
  if build_panel and (enabled ~= panel_on or auto ~= panel_auto or paused ~= panel_paused) then
    panel_on, panel_auto, panel_paused = enabled, auto, paused
    build_panel()
  end

  -- status banner: what is the bot doing RIGHT NOW
  local modetxt
  if not enabled then       modetxt = "OFF - press On in your start room"
  elseif paused then        modetxt = "PAUSED - press Pause to continue"
  elseif resting then       modetxt = string.format("RESTING - Seid low, %ds left", math.max(0, rest_until - now))
  elseif fighting then      modetxt = "FIGHTING - resumes when clear"
  elseif plan then          modetxt = "WALKING..."
  elseif auto then          modetxt = "AUTO-EXPLORING"
  else                      modetxt = "READY - Step or Auto"
  end
  scrye.setState(P .. "status", modetxt)
  -- The layer the SERVER named, not one inferred from z. `1 - pos.z` was the old guess and
  -- it assumed you entered on layer one: on a sea entered at layer two it read 4 where the
  -- game said "Layer five". layer_base ties z to the layer the first Room.Info reported, so
  -- this is the same number the room header prints. Falls back to the old guess only before
  -- anything has been anchored.
  local layer_now = layer_base and (layer_base - pos.z) or (1 - pos.z)
  scrye.setState(P .. "stats", string.format("%d   rooms %d   kills %d",
    layer_now, room_count(), kills))
  scrye.setState(P .. "hunt", string.format("%s  (stops at: %s, delay %.1fs)", killname, goal, kill_delay))

  -- sea timer: time since the last New Sea (unsetsea only works after 60 min)
  local seatxt
  if sea_time then
    local elm = math.floor((now - sea_time) / 60)
    local left = 60 - elm
    if left > 0 then
      seatxt = string.format("#%d  age %dm, new sea in %dm", seanum, elm, left)
    else
      seatxt = string.format("#%d  ready for a new sea", seanum)
    end
  else
    seatxt = string.format("#%d  no active sea timer", seanum)
  end
  scrye.setState(P .. "sea", seatxt)
  scrye.setState(P .. "seanum", tostring(seanum))

  -- map of current level, centered on player, north = up
  local x0 = pos.x - COLS // 2
  local y0 = pos.y + ROWS // 2
  local zt = rooms[pos.z] or {}
  -- frontier targets on this level
  local fronts = {}
  for _, e in ipairs(frontier) do
    local t = moved({ x = e.x, y = e.y, z = e.z }, e.dir)
    if t and t.z == pos.z then fronts[t.x .. "|" .. t.y] = true end
  end
  local grid = {}
  for cy = 0, ROWS - 1 do
    local row = {}
    for cx = 0, COLS - 1 do
      local wx, wy = x0 + cx, y0 - cy
      local ch = "."
      local xt = zt[wx]
      local room = xt and xt[wy]
      if room then
        ch = "#"
        if room.exits.d or room.exits.down then ch = "v" end   -- the original's blue dot
      end
      if wx == 0 and wy == 0 and pos.z == 0 and room then ch = "S" end
      if fronts[wx .. "|" .. wy] then ch = "f" end
      if wx == pos.x and wy == pos.y then ch = "@" end
      row[#row + 1] = ch
    end
    grid[#grid + 1] = table.concat(row)
  end
  scrye.setState(P .. "map", table.concat(grid, "\n"))
  mark_dirty()
end

-- The panel is rebuilt whenever the bot's state changes, so its buttons can show that
-- state. addPanel with the same title REPLACES in place -- position, size and the
-- selected tab survive -- and this panel has no input fields, so a rebuild can never
-- eat something you were half-way through typing.
local LIT  = "#6BEF75"   -- armed: the same green the map draws you in
local HELD = "#DA950B"   -- deliberately held: the frontier amber

build_panel = function()
scrye.addPanel{
  title = "3S Chaos Sea",
  width = 300,
  accent = "#0B9DB3",          -- signature: chaos-sea teal (validated accent set)
  widgets = {
    { type = "value", text = "", bind = P .. "status", color = "info" },      -- semantic: status line
    { type = "value", text = "layer ", bind = P .. "stats" },
    -- Marker colours are OKLCH-stepped and validated all-pairs on the map surface
    -- (worst pair ΔE 13.0 under simulated colour-blindness); labels draws the marker
    -- letters on their tiles, so the markers never rely on colour alone.
    { type = "colorgrid", bind = P .. "map", labels = "@fvS", palette = {
        ["."] = "#080A0C",   -- unknown / wall (map background)
        ["#"] = "#856D52",   -- explored room (soft tan)
        ["v"] = "#3BAECE",   -- room with a down exit (cyan)
        ["f"] = "#DA950B",   -- frontier target (amber: still to explore)
        ["S"] = "#FFFFFF",   -- start room 0 0 0
        ["@"] = "#6BEF75",   -- you (neon green)
      } },
    -- legend, each entry in its own map color
    { type = "label", text = "@ you",       color = "#6BEF75" },
    { type = "label", text = "f frontier",  color = "#DA950B" },
    { type = "label", text = "v down exit", color = "#3BAECE" },
    { type = "label", text = "S start",     color = "#FFFFFF" },
    -- "hunt", not "kills": this row is what the bot is LOOKING for, and the stats row above
    -- now carries an actual kill count. Two rows labelled kills would be a puzzle.
    { type = "value", text = "hunt: ",  bind = P .. "hunt", color = "error" },
    { type = "value", text = "sea ",    bind = P .. "sea",  color = "#0B9DB3" },   -- sea id echoes the panel accent
    -- controls laid out two per row (Delay +/- dropped; use "cs delay <n>" if needed)
    -- The three state buttons carry their own colour: green when the thing they control
    -- is ON, amber for a deliberate hold. A bot you cannot tell the state of at a glance
    -- is a bot you end up prodding to find out.
    { type = "buttonrow", buttons = {
        { text = enabled and "On" or "Off", color = enabled and LIT or nil,
          action = function() cs_interface(enabled and "disable" or "enable") end },
        { text = "Step", action = function() cs_step(true) end },   -- you asked for it
    } },
    { type = "buttonrow", buttons = {
        { text = auto and "Auto ON" or "Auto", color = auto and LIT or nil,
          action = function() cs_interface(auto and "auto off" or "auto on") end },
        { text = paused and "PAUSED" or "Pause", color = paused and HELD or nil,
          action = function() cs_pause_toggle() end },
    } },
    { type = "buttonrow", buttons = {
        { text = "Leave", action = function() cs_leave() end },
        { text = "Reset", action = function() cs_interface("reset") end },
    } },
    { type = "value", text = "Sea #: ", bind = P .. "seanum", color = "info" },
    { type = "buttonrow", buttons = {
        { text = "Sea# -", action = function() cs_set_seanum(seanum - 1) end },
        { text = "Sea# +", action = function() cs_set_seanum(seanum + 1) end },
    } },
    { type = "button", text = "New Sea", action = function() cs_new_sea() end },
  },
}
end

build_panel()

-- ---------- command interface ----------
function cs_interface(args)
  args = (args or ""):gsub("^%s+", ""):gsub("%s+$", "")
  if args == "" then
    note("Chaos sea stepper:")
    note("  cs enable|disable     parse rooms on/off (enable in your start room)")
    note("  cs step               walk to next unexplored room")
    note("  cs auto on|off        keep stepping, kill mobs on the way")
    note("  cs leave              walk back to 0 0 0")
    note("  cs reset              wipe map        (map lives in the HUD panel)")
    note("  cs set/find <x> <y> <z>")
    note("  cs kill <name>        cs exclude <mob long name>")
    note("  cs goal <word>        stop + open it when seen (default: cask)")
    note("  cs delay <secs>       pause after killing blows (default 2.5s)")
    note("  cs rest <seid> [secs] rest when Seid drops below <seid> (0 = off)")
    note("  cs seanum <n>         set the sea number (1-120) for New Sea")
    note("  (New Sea, at the cask, also loots it and resets the map for you)")
    note("  cs party <names>      group members to ignore (comma separated; 'clear' to reset)")
    note("  cs pause              hold everything / continue (also the Pause button)")
    note("  cs notify on|off      buzz the phone when the bot pauses or runs out (now: "
      .. (notify_on and "on" or "off") .. ")")
  elseif args == "notify on" or args == "notify off" then
    notify_on = (args == "notify on")
    scrye.store.set("notify", notify_on and "1" or "0")
    note("phone notify: " .. (notify_on and "on" or "off"))
    publish_notify_state()
  elseif args == "enable" then
    enabled = true
    goal_found = false
    paused = false
    resting = false
    if not get_room(pos) then add_room(pos) end
    -- hold the world automapper: the sea is a fresh random instance, and our steps
    -- must not dead-reckon phantom rooms into whatever real area 3s-map was in
    scrye.emit("map.hold", scrye.json.encode({ on = true }))
    -- Nothing can re-describe the room you are STANDING in any more: Room.Info is sent on
    -- a room change, and 'look' produces none. The map seeds itself on your first step.
    note("enabled - walk one step to seed the map, then 'cs step' or 'cs auto on'")
  elseif args == "disable" then
    enabled = false; auto = false
    scrye.emit("map.hold", scrye.json.encode({ on = false }))   -- release the automapper
    note("disabled")
  elseif args == "reset" then
    reset(); note("map reset")
  elseif args == "step" then
    cs_step(true)          -- you asked for it
  elseif args == "auto on" then
    if not enabled then scrye.emit("map.hold", scrye.json.encode({ on = true })) end
    enabled = true; auto = true; goal_found = false; blind_steps = 0
    note("auto-exploring (kill: " .. killname .. ", stops at: " .. goal .. ")")
    -- map the CURRENT room first (a reload wipes the map, so the frontier may be empty). The
    -- glance re-parses the room, seeds the frontier, arms us, and the next prompt starts stepping.
    -- Only fall straight into cs_step() if we already have somewhere to go.
    --
    -- Mid-swap, do neither: the room we are standing in is the old sea (or nowhere at all)
    -- and the map is about to be thrown away. Arming `auto` here is the whole point -- New
    -- Sea's own glance, seconds from now, is what will start the walking.
    if sea_entering then note("...as soon as the new sea is ready")
    elseif #frontier > 0 then cs_step()
    else note("no room known yet - walk one step and the server will name it") end
  elseif args == "auto off" then
    auto = false; note("auto off")
  elseif args == "pause" then
    cs_pause_toggle()
  elseif args == "leave" then
    cs_leave()
  elseif args == "win" then
    -- NOTE: dropped - the map is a HUD panel now; Scrye manages show/hide
    note("'cs win' is gone: the map lives in the HUD sidebar (Scrye manages show/hide)")
  elseif args == "debug_all" then
    note(string.format("Current position %d %d %d", pos.x, pos.y, pos.z))
    for z, zt in pairs(rooms) do
      note("Level " .. z)
      for x, xt in pairs(zt) do
        for y, room in pairs(xt) do
          local ex = {}
          for d in pairs(room.exits) do ex[#ex + 1] = d end
          note(string.format("  [ %d %d ] : %s", x, y, table.concat(ex, ", ")))
        end
      end
    end
  else
    local parts = {}
    for w in args:gmatch("%S+") do parts[#parts + 1] = w end
    if parts[1] == "set" then
      pos.x = tonumber(parts[2]) or pos.x
      pos.y = tonumber(parts[3]) or pos.y
      pos.z = tonumber(parts[4]) or pos.z
      add_room(pos)
      note(string.format("position set to %d %d %d", pos.x, pos.y, pos.z))
    elseif parts[1] == "find" then
      local dst = { x = tonumber(parts[2]) or 0, y = tonumber(parts[3]) or 0, z = tonumber(parts[4]) or 0 }
      local path
      local rr = ask_pathfinder({ to = dst }, true)
      if rr then path = rr.found and (rr.dirs or {}) or nil
      else path = bfs(pos, dst, true) end
      note("Result is: " .. (path and table.concat(path, " ") or "no path"))
    elseif parts[1] == "kill" then
      killname = parts[2] or "mutant"; note("kill target: " .. killname)
    elseif parts[1] == "rest" then
      rest_below = tonumber(parts[2]) or 0
      if parts[3] then rest_secs = math.max(10, math.min(600, tonumber(parts[3]) or 60)) end
      if rest_below > 0 then
        note("rest when Seid < " .. rest_below .. " (for " .. rest_secs .. "s at a time)")
      else
        resting = false
        note("seid rest: off")
      end
    elseif parts[1] == "delay" then
      kill_delay = math.max(0.5, math.min(10, tonumber(parts[2]) or 2.5))
      note("post-kill delay: " .. kill_delay .. "s")
    elseif parts[1] == "goal" then
      goal = args:gsub("^goal%s+", "")
      goal_found = false
      parse_goals()
      note("goal: stop when any of these is in the room: " .. goal)
    elseif parts[1] == "exclude" then
      local name = args:gsub("^exclude%s+", "")
      excluded[name] = true; note("excluded: " .. name)
    elseif parts[1] == "party" then
      local rest = args:gsub("^party%s*", "")
      if rest == "" then
        local l = party_list()
        note("party (mobs are still fought when these are in the room): "
          .. (#l > 0 and table.concat(l, ", ") or "(nobody)"))
      elseif rest:lower() == "clear" then
        party = {}; save_party(); note("party list cleared")
      else
        local added = {}
        for n in rest:gmatch("[^,]+") do
          n = n:gsub("^%s+", ""):gsub("%s+$", ""):lower()
          if n ~= "" then party[n] = true; added[#added + 1] = n end
        end
        save_party()
        note("party: " .. table.concat(party_list(), ", ")
          .. (#added > 0 and ("   (added " .. table.concat(added, ", ") .. ")") or ""))
      end
    elseif parts[1] == "seanum" then
      local n = tonumber(parts[2])
      if n and n >= 1 and n <= 120 then
        seanum = math.floor(n)
        note("sea number set to " .. seanum)
      else
        note("sea number must be 1-120")
      end
    else
      note("unknown command '" .. args .. "' - 'cs' for help")
    end
  end
  cs_draw()
end

-- ---------- triggers ----------
-- The room header. Not a marker -- this prints with every display setting off -- and the
-- only thing that arrives for EVERY move.
--
-- Unanchored at the END because the client renders the map on the same line, and the '=S='
-- marker (when the player still has it switched on) appears there too.
--
-- Optional '=S=' at the START for the same reason, and it is not cosmetic: the marker is a
-- PREFIX, so with it enabled the line does not begin with "Layer" at all --
--     =S=Layer three of the Sea of Chaos (s,n)                   =S=
-- and a pattern anchored on "Layer" misses every one of them. That would have handed the
-- corridor stall straight back to anyone who had not turned the markers off.
scrye.addTrigger{ pattern = [[^(?:=S=)?Layer \w+ of the Sea of Chaos \(([^)]*)\)]], regex = true,
  run = function(ex) on_sea_header(ex or "") end }

-- A wimpy retreat, which names the direction it dragged you. GMCP reports the room that
-- results, never why -- and "why" is the whole difference between a retreat and the maze
-- shifting under a fight.
scrye.addTrigger{ pattern = [[^Your legs run away with you (\w+)]], regex = true,
  run = function(w1) if enabled then wimpy_dir = tostring(w1 or ""):lower() end end }

-- The sea's own teleport. Text, because GMCP does not say WHY a room arrived -- only that
-- one did -- and this is the difference between our step landing and being thrown across
-- the maze.
scrye.addTrigger{ pattern = [[^Suddenly you find yourself elsewhere]], regex = true,
  run = function() if enabled then teleported = true end end }

-- The marker triggers are now the FALLBACK, not the source: they stand down for any room
-- Room.Contents has already described. Keeping them means the markers can be switched off
-- one at a time, and that a MUD or a character without GMCP still works.
scrye.addTrigger{ pattern = [[^(?:=M= ?|\[MONSTAR!\])(.+)$]], regex = true,
  run = function(w1) if not contents_seen then cs_on_mob(w1 or "") end end }
scrye.addTrigger{ pattern = [[^(?:=P= ?|\[PLAYAR!\])(.+)$]], regex = true,
  run = function(w1) if not contents_seen then cs_on_player(w1 or "") end end }
scrye.addTrigger{ pattern = [[^=[AWI]= ?(.*)$]], regex = true,
  run = function(w1) if not contents_seen then cs_on_item(w1 or "") end end }
scrye.addTrigger{ pattern = [[ gold coins\.$]], regex = true,
  run = function() cs_flags.gold = true end }
scrye.addTrigger{ pattern = [[^There is no (.+) here\.$]], regex = true,
  run = function(w1) cs_no_mob(w1 or "") end }
-- Deliberately NOT anchored at the end. This was [[^You cannot go (\w+)\.$]], which fails
-- on a single trailing space -- and a refusal that does not match costs the full step
-- timeout before the bot works out for itself that it did not move. The direction was
-- captured and then ignored anyway: cs_blocked reads the one it sent.
scrye.addTrigger{ pattern = [[^You cannot go ]], regex = true,
  run = function() cs_blocked() end }
scrye.addTrigger{ pattern = [[^You are unable to penetrate the wall that]], regex = true,
  run = function() cs_blocked() end }
scrye.addTrigger{ pattern = [[dealt the killing blow to (.+)\.]], regex = true,
  run = function() cs_killblow() end }

-- the original's "^>\s*$" prompt trigger
scrye.onPrompt(cs_on_prompt)

-- Every command that goes to the MUD, whoever sent it. Observe-only, and now ONLY so
-- that "you walked out of a fight" can be told from "wimpy dragged you out" -- which
-- words a message and decides nothing. Directions alone are worth recording, and not
-- while the bot has a step of its own in flight: that one is already accounted for.
scrye.onCommand(function(text)
  local first = tostring(text or ""):match("^%s*(%S+)")
  if not first then return end
  first = first:lower()
  if DELTA[first] and not (plan and plan.awaiting) then
    last_typed = { dir = first, at = now }
  end
end)

-- Room.Info is the arrival signal; Room.Contents is what is in the room when there is
-- anything. Nothing else is subscribed to.
scrye.onGmcp("Room.Info", on_room_info)
scrye.onGmcp("Room.Contents", on_room_contents)
-- Room.Map is the only one sent for EVERY move, so it is the backstop arrival signal.
scrye.onGmcp("Room.Map", on_room_map)

-- ---------- alias ----------
scrye.addAlias{ pattern = [[^cs(?:\s+(.*))?$]], regex = true,
  run = function(w1) cs_interface(w1) end }

-- ---------- timers ----------
scrye.every(1, function() now = now + 1; cs_step_timeout() end)   -- the clock (no os.time in Scrye)
scrye.every(5, cs_watchdog)

-- MUSHclient called OnPluginSaveState on world save/close; Scrye has no such hook, so
-- flush the 3 s debounce when the world goes away rather than losing the last edits.
scrye.onDisconnect(function()
  if dirty_timer then scrye.cancel(dirty_timer); dirty_timer = nil end
  save_state()
end)

-- The client's idle guard fired: nobody is at the keyboard. The MUSHclient deadman switch
-- did exactly this by reaching in from the area-bot plugin ("cs auto off"); now the client
-- owns the clock and simply tells us. Mapping state is kept, so 'cs auto on' picks it up.
scrye.onIdle(function()
  if auto then
    auto = false
    note("idle guard fired - auto off. 'cs auto on' when you are back.")
    -- the one notify aimed squarely at the phone: the guard firing MEANS you are away
    pnotify("Chaos sea: idle guard stopped the bot")
    cs_draw()
  end
end)

-- ---------- startup (was OnPluginInstall) ----------
load_state()
cs_draw()
publish_notify_state()
