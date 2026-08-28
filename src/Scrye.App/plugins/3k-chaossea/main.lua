-- CLASSIC (frozen legacy, restored 25 Aug from the pre-GMCP line at 8fc791b^).
-- This is the marker/dead-reckoning sea bot exactly as it ran live for months - for
-- 3K-3Kingdoms and any character without GMCP. Identity changes ONLY: plugin id
-- 3k-chaossea, alias 'csc' (so it can load beside the GMCP 3s-chaossea's 'cs'), and
-- client variables csc_auto/csc_enabled. Nothing else is maintained here: new features
-- go to the GMCP bot, and the state/panel prefix follows scrye.id automatically.
-- ============================================================
-- 3S Chaos Sea - Scrye port of ThreeS_ChaosSea (MUSHclient)
-- Maps rooms on a 3D grid as you move, queues unexplored exits and
-- walks to the nearest frontier room (BFS through known rooms).
-- ============================================================
-- NOTE: dropped / simplified vs the original:
--  * 'csc win' and all miniwindow show/hide/drag/resize handling: the HUD
--    panel is managed by Scrye itself.
--  * Area/Deadman tab handover (CallPlugin into the area-bot plugin) and
--    the 'Bots' tab bar: no cross-plugin calls in Scrye.
--  * The 'Set #' inputbox button -> alias 'csc seanum <n>' (1-120) instead
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
-- Commands on the wire whose output we have not finished reading, oldest first
-- (first word, lowercased). Tells a REPRINTED room header ('look', 'finger') apart
-- from an actual move, and an actual move apart from a wimpy retreat.
--
-- A QUEUE, not a single "last command", because commands pipeline: scrye.onCommand
-- reports every send on the wire -- yours, ours, and other plugins' -- and the
-- auto-trader in 3s-viking-status fires one `vtrade goods <x>` per second for 16
-- goods. A scalar is overwritten by that timer in the gap between our step going out
-- and its room header coming back, so our own move gets read as somebody's redisplay
-- and never confirmed. The MUD answers commands in the order it received them, so
-- the block being printed belongs to sent_q[1] and nothing else.
-- Strictly one push per command, one pop per prompt: any cleverer bookkeeping
-- (popping on the room header as well) drifts the queue by one and puts it
-- permanently out of step with the output it is supposed to be labelling.
--
-- One push per command is not enough on its own, because SOME COMMANDS PRODUCE NO
-- PROMPT and so are never popped. Scrye's own MIP handshake is three of them
-- ('3klient ...' x3), CommandSent reports every one, and they go out on
-- every connect. A single un-popped entry puts sent_q permanently one behind: from
-- then on sent_q[1] names the command BEFORE the one whose output is printing, so
-- every one of the bot's own moves reads as somebody else's redisplay, never
-- confirms, and only the watchdog's 15s !glance shakes it loose -- one room per
-- watchdog tick, indefinitely. The drift is stable, which is why it never recovers.
-- Each entry therefore carries the tick it went out on, and anything that has been
-- sitting here for seconds is dropped: a MUD answers in milliseconds, so an old
-- entry is not a pending command, it is one that will never be popped.
local sent_q = {}              -- { { cmd = "n", at = tick }, ... }
local SENT_MAX = 16            -- self-limit: a lost prompt must not grow this forever
local SENT_TTL = 3             -- ticks an unanswered command stays believable
local glance_unstick = false   -- the watchdog's !glance is asking for a move to be re-confirmed
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
local seanum = 1
local sea_time = nil           -- `now` value when the current sea was entered
local sea_started_here = false -- true only if THIS session started the sea (see cs_new_sea)
local sea_entering = false     -- New Sea sequence in flight: the swap is sent over several
                               -- seconds and the map reset + opening glance come at the end
                               -- of it, so nothing may walk, unpause or re-glance until then.
local now = 0                  -- seconds since plugin load (1 s ticker)

-- Drop the entries at the head that the MUD has certainly already answered. This is
-- the queue correcting its own model rather than a heuristic: it holds commands whose
-- output has not finished printing, and after SENT_TTL seconds that is no longer what
-- an entry means.
local function sent_prune()
  while sent_q[1] and now - sent_q[1].at >= SENT_TTL do table.remove(sent_q, 1) end
end

-- The command whose block of output is printing now, or nil for the server itself.
local function sent_head()
  sent_prune()
  return sent_q[1] and sent_q[1].cmd or nil
end
local map_serial = 1           -- bumped on every GRAPH change (rooms/exits), not moves —
                               -- the wasm pathfinder caches the graph keyed on this

local cs_flags = { item = false, gold = false, player = false }

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

local function add_exit(p, dir)
  local room = get_room(p) or add_room(p)
  if not room.exits[dir] then
    room.exits[dir] = true
    map_serial = map_serial + 1
    local target = moved(p, dir)
    if target and not get_room(target) then
      frontier[#frontier + 1] = { x = p.x, y = p.y, z = p.z, dir = dir }
    end
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

-- ---------- room exit parsing ----------
local function parse_exits(short)
  local exit_desc = short:match("%((.*)%)")
  if exit_desc then
    -- the maze shifts: trust what the room says NOW over what we recorded
    local room = get_room(pos) or add_room(pos)
    room.exits = {}
    for exit in exit_desc:gmatch("[^,%s]+") do
      if exit == "out" then
        -- the way out of the sea, don't queue it
      elseif exit == "u" or exit == "up" then
        -- remember it for pathing back, but never queue it for exploring
        local r = get_room(pos) or add_room(pos)
        r.exits[exit] = true
      elseif DELTA[exit] then
        add_exit(pos, exit)
        if exit == "d" or exit == "down" then
          local t = moved(pos, exit)
          if t and not get_room(t) then note("DOWN exit here!") end
        end
      end
    end
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
      if auto then armed = false; cs_step() end
    else
      note("arrived back at start")
      auto = false
    end
    cs_draw()
    return
  end
  plan.awaiting = true
  plan.sent_at = now            -- anything older than this is not what is printing
  glance_unstick = false        -- a fresh step: any earlier unstick request is spent
  last_dir = plan.dirs[plan.idx]
  scrye.send(plan.dirs[plan.idx])
end

local function cs_on_room(short)
  if not enabled then return end
  -- What produced this room header? Consume the answer: it is only valid for the
  -- block of output the command generated, and the prompt clears it again.
  local cmd = sent_head()   -- the command this block of output is answering (nil = the server)
  -- A command that was already on the wire BEFORE we stepped and still has not printed
  -- is one that never will: nothing pops an entry but a prompt, and it produced none.
  -- The block arriving is ours. Drop the impostor instead of spending a watchdog cycle
  -- on it -- otherwise the first silent command of the session costs 15s, and the next
  -- one 15s more.
  while cmd and not DELTA[cmd]
        and plan and plan.awaiting and plan.sent_at and sent_q[1].at < plan.sent_at do
    table.remove(sent_q, 1)
    cmd = sent_head()
  end
  -- ...except the watchdog's own re-glance, which exists precisely to shake a room
  -- header out of the MUD when a step went unconfirmed. Its reply IS the confirmation.
  if glance_unstick and cmd == "!glance" and plan and plan.awaiting then
    glance_unstick = false
    cmd = plan.dirs[plan.idx]
  end
  if cmd and not DELTA[cmd] then
    -- Something that REPRINTS the room header without moving you: 'look', 'finger bob',
    -- 'exits', or the bot's own '!glance'. Only a direction (or the server itself) can
    -- move you, so this is a redisplay, whatever it is. Re-read the exits and arm the
    -- stepper from it -- but do not advance the walking plan, and do not call it a wimpy
    -- retreat, which is what used to happen here.
    if not paused then parse_exits(short); armed = true; cs_draw() end
    return
  end
  if plan and plan.awaiting then
    -- the bot's own move arriving: always confirm it, even mid-pause
    local p2 = moved(pos, plan.dirs[plan.idx])
    if p2 then pos = p2 end
    add_room(pos)
    plan.idx = plan.idx + 1
    plan.awaiting = false
    if paused then
      parse_exits(short)   -- it's the bot's own maze room: map it
      cs_draw()
      return
    end
  elseif paused then
    return   -- frozen: rooms you walk through while paused are ignored
  elseif fighting then
    -- A move the bot did not order, during a fight. Two different things:
    if cmd then
      note("You walked " .. cmd .. " during a fight - bot PAUSED. Walk back to the fight room,")
      pnotify("Chaos sea: you moved mid-fight - bot paused")
    else
      -- nothing was typed, so the server moved us: wimpy kicked in (HP retreat)
      note("WIMPY! Moved during combat - bot PAUSED. Walk back to the fight room,")
      pnotify("Chaos sea: WIMPY - bot paused mid-fight")
    end
    paused = true
    note("then 'csc pause' to continue (or 'csc set x y z' if unsure where you are).")
    cs_draw()
    return
  end
  cs_flags.item = false; cs_flags.gold = false; cs_flags.player = false
  room_goal_idx = nil; room_goal_word = nil
  pending_mob = nil
  parse_exits(short)
  armed = true
  cs_draw()
end

-- the GOAL: a room with a cask (or whatever 'csc goal' is set to)
local function cs_reach_goal(gw)
  -- target item (cask/portal) is in the room: just PAUSE the bot. paused is the
  -- robust stop - cs_step/cs_advance/cs_on_prompt/watchdog all honour it.
  paused = true
  plan = nil
  pending_mob = nil
  room_goal_idx = nil; room_goal_word = nil   -- handled; don't re-pause on unpause
  note(string.upper(gw) .. " found - bot PAUSED ('csc pause' / Pause button to continue).")
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
  -- wait kill_delay so your own killing-blow triggers act first
  scrye.after(math.max(1, kill_delay), cs_after_kill)
end

-- ---------- prompt: goals + keep the plan moving ----------
local function cs_on_prompt()
  -- Before the guards: the prompt ends the block of output the oldest unanswered
  -- command produced, so drop it -- what comes next belongs to the command behind it,
  -- or, with the queue empty, to the server itself. That empty queue is what makes a
  -- real wimpy retreat distinguishable at all.
  sent_prune()
  if sent_q[1] then table.remove(sent_q, 1) end
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
    armed = false
    cs_step()
  end
end

-- ---------- frontier selection: DOWN exits always win ----------
local function valid_entry(e)
  local from_room = get_room({ x = e.x, y = e.y, z = e.z })
  local t = moved({ x = e.x, y = e.y, z = e.z }, e.dir)
  if t and not get_room(t) and from_room and from_room.exits[e.dir] then
    return t
  end
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
    note("PAUSED - 'csc pause' (or the Pause button) to continue")
  else
    note("resuming")
    if enabled and not fighting and not in_combat() then
      if plan then cs_advance()
      elseif auto then scrye.after(1, function() cs_step() end) end
    end
  end
  cs_draw()
end

cs_step = function()
  if not enabled then note("not enabled - 'csc enable' first") return end
  if paused then note("paused - unpause first") return end
  if resting then note("resting (Seid low) - back in " .. math.max(0, rest_until - now) .. "s") return end
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
    if auto and #stash > 0 and blind_steps < 20 then
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
    if #stash > 0 then
      note(string.format("%d unexplored exits but no path and no exit to blind-step from [%d %d %d] - stopping",
        #stash, pos.x, pos.y, pos.z))
      pnotify("Chaos sea: stuck - unexplored exits but no path (bot stopped)")
    else
      note("Out of rooms!")
      pnotify("Chaos sea: out of rooms - sea fully explored (bot stopped)")
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
  sent_q = {}                  -- "start over" includes what we thought was on the wire
  map_serial = map_serial + 1
  pos = { x = 0, y = 0, z = 0 }
  plan = nil; fighting = false; steps_done = 0
  goal_found = false; paused = false
  add_room(pos)
  cs_draw()
end

-- ---------- watchdog (every 5 s) ----------
local stuck_since = nil
local idle_ticks = 0
local awaiting_ticks = 0
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
  -- move sent but the room never arrived: re-glance to unstick the confirm
  if plan and plan.awaiting then
    awaiting_ticks = awaiting_ticks + 1
    if awaiting_ticks >= 3 then
      awaiting_ticks = 0
      note("watchdog: move unconfirmed - glancing")
      -- The queue said our own room header belonged to someone else, and it was wrong.
      -- Whatever is left in it has been overtaken by events, so stop believing it: the
      -- glance's reply then reads as the server's, which confirms the move.
      sent_q = {}
      glance_unstick = true
      scrye.send("!glance")
    end
    return
  end
  awaiting_ticks = 0
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
  scrye.after(5, function() scrye.send("enter sea") end)
  -- ...and once we are actually in it, finish the job the user used to finish by hand:
  -- a new sea is a NEW MAZE, so the old map is not stale, it is wrong. reset() drops it,
  -- recentres on 0,0,0 and clears paused/goal_found; the glance then maps the room we
  -- landed in and arms the stepper. Two seconds after `enter sea` so the room has printed.
  scrye.after(7, function()
    sea_entering = false
    reset()                  -- clears the old maze, the goal flag and the pause
    scrye.send("!glance")    -- map the arrival room; its reply arms the stepper
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
  scrye.setVariable("csc_auto", auto and "1" or "0")
  scrye.setVariable("csc_enabled", enabled and "1" or "0")

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
  scrye.setState(P .. "stats", string.format("%d,%d  level %d  rooms %d  left %d  dives %d",
    pos.x, pos.y, 1 - pos.z, room_count(), #frontier, steps_done))
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
  title = "Chaos Sea (classic)",
  width = 300,
  accent = "#0B9DB3",          -- signature: chaos-sea teal (validated accent set)
  widgets = {
    { type = "value", text = "", bind = P .. "status", color = "info" },      -- semantic: status line
    { type = "value", text = "pos ",   bind = P .. "stats" },
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
    { type = "value", text = "kills: ", bind = P .. "hunt", color = "error" },     -- semantic: kills
    { type = "value", text = "sea ",    bind = P .. "sea",  color = "#0B9DB3" },   -- sea id echoes the panel accent
    -- controls laid out two per row (Delay +/- dropped; use "csc delay <n>" if needed)
    -- The three state buttons carry their own colour: green when the thing they control
    -- is ON, amber for a deliberate hold. A bot you cannot tell the state of at a glance
    -- is a bot you end up prodding to find out.
    { type = "buttonrow", buttons = {
        { text = enabled and "On" or "Off", color = enabled and LIT or nil,
          action = function() cs_interface(enabled and "disable" or "enable") end },
        { text = "Step", action = function() cs_step() end },
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
    note("  csc enable|disable     parse rooms on/off (enable in your start room)")
    note("  csc step               walk to next unexplored room")
    note("  csc auto on|off        keep stepping, kill mobs on the way")
    note("  csc leave              walk back to 0 0 0")
    note("  csc reset              wipe map        (map lives in the HUD panel)")
    note("  csc set/find <x> <y> <z>")
    note("  csc kill <name>        cs exclude <mob long name>")
    note("  csc goal <word>        stop + open it when seen (default: cask)")
    note("  csc delay <secs>       pause after killing blows (default 2.5s)")
    note("  csc rest <seid> [secs] rest when Seid drops below <seid> (0 = off)")
    note("  csc seanum <n>         set the sea number (1-120) for New Sea")
    note("  (New Sea, at the cask, also loots it and resets the map for you)")
    note("  csc party <names>      group members to ignore (comma separated; 'clear' to reset)")
    note("  csc pause              hold everything / continue (also the Pause button)")
    note("  csc notify on|off      buzz the phone when the bot pauses or runs out (now: "
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
    note("enabled - glance to map this room, then 'csc step' or 'csc auto on'")
    scrye.send("!glance")
  elseif args == "disable" then
    enabled = false; auto = false
    scrye.emit("map.hold", scrye.json.encode({ on = false }))   -- release the automapper
    note("disabled")
  elseif args == "reset" then
    reset(); note("map reset")
  elseif args == "step" then
    cs_step()
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
    elseif #frontier > 0 then cs_step() else scrye.send("!glance") end
  elseif args == "auto off" then
    auto = false; note("auto off")
  elseif args == "pause" then
    cs_pause_toggle()
  elseif args == "leave" then
    cs_leave()
  elseif args == "win" then
    -- NOTE: dropped - the map is a HUD panel now; Scrye manages show/hide
    note("'csc win' is gone: the map lives in the HUD sidebar (Scrye manages show/hide)")
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
scrye.addTrigger{ pattern = [[^=S=(.*)=S=]], regex = true,
  run = function(w1) cs_on_room(w1 or "") end }
scrye.addTrigger{ pattern = [[^(?:=M= ?|\[MONSTAR!\])(.+)$]], regex = true,
  run = function(w1) cs_on_mob(w1 or "") end }
scrye.addTrigger{ pattern = [[^(?:=P= ?|\[PLAYAR!\])(.+)$]], regex = true,
  run = function(w1) cs_on_player(w1 or "") end }
scrye.addTrigger{ pattern = [[^=[AWI]= ?(.*)$]], regex = true,
  run = function(w1) cs_on_item(w1 or "") end }
scrye.addTrigger{ pattern = [[ gold coins\.$]], regex = true,
  run = function() cs_flags.gold = true end }
scrye.addTrigger{ pattern = [[^There is no (.+) here\.$]], regex = true,
  run = function(w1) cs_no_mob(w1 or "") end }
scrye.addTrigger{ pattern = [[^You cannot go (\w+)\.$]], regex = true,
  run = function() cs_blocked() end }
scrye.addTrigger{ pattern = [[^You are unable to penetrate the wall that]], regex = true,
  run = function() cs_blocked() end }
scrye.addTrigger{ pattern = [[dealt the killing blow to (.+)\.]], regex = true,
  run = function() cs_killblow() end }

-- the original's "^>\s*$" prompt trigger
scrye.onPrompt(cs_on_prompt)

-- Every command that goes to the MUD, whoever sent it -- you, an alias, the bot's own
-- scrye.send, ANOTHER PLUGIN's timer. Observe-only. Records just the first word,
-- lowercased, so 'finger bob' and 'look at chest' collapse to 'finger' / 'look' and a
-- bare direction stays a direction. Queued, because several can be in flight at once.
scrye.onCommand(function(text)
  local first = tostring(text or ""):match("^%s*(%S+)")
  if not first then return end
  sent_q[#sent_q + 1] = { cmd = first:lower(), at = now }
  if #sent_q > SENT_MAX then table.remove(sent_q, 1) end
end)

-- ---------- alias ----------
scrye.addAlias{ pattern = [[^csc(?:\s+(.*))?$]], regex = true,
  run = function(w1) cs_interface(w1) end }

-- ---------- timers ----------
scrye.every(1, function() now = now + 1 end)   -- the clock (no os.time in Scrye)
scrye.every(5, cs_watchdog)

-- MUSHclient called OnPluginSaveState on world save/close; Scrye has no such hook, so
-- flush the 3 s debounce when the world goes away rather than losing the last edits.
scrye.onDisconnect(function()
  if dirty_timer then scrye.cancel(dirty_timer); dirty_timer = nil end
  save_state()
end)

-- The client's idle guard fired: nobody is at the keyboard. The MUSHclient deadman switch
-- did exactly this by reaching in from the area-bot plugin ("csc auto off"); now the client
-- owns the clock and simply tells us. Mapping state is kept, so 'csc auto on' picks it up.
scrye.onIdle(function()
  if auto then
    auto = false
    note("idle guard fired - auto off. 'csc auto on' when you are back.")
    -- the one notify aimed squarely at the phone: the guard firing MEANS you are away
    pnotify("Chaos sea: idle guard stopped the bot")
    cs_draw()
  end
end)

-- ---------- startup (was OnPluginInstall) ----------
load_state()
cs_draw()
publish_notify_state()
