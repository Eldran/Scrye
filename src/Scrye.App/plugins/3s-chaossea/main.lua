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
local steps_done = 0
local seanum = 1
local sea_time = nil           -- `now` value when the current sea was entered
local now = 0                  -- seconds since plugin load (1 s ticker)

local cs_flags = { item = false, gold = false, player = false }

-- forward declarations (mutually recursive)
local cs_draw, cs_step, cs_advance, mark_dirty

local function note(s) scrye.print(s) end

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
  last_dir = plan.dirs[plan.idx]
  scrye.send(plan.dirs[plan.idx])
end

local function cs_on_room(short)
  if not enabled then return end
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
    -- the bot didn't order this move: wimpy kicked in (HP retreat)
    paused = true
    note("WIMPY! Moved during combat - bot PAUSED. Walk back to the fight room,")
    note("then 'cs pause' to continue (or 'cs set x y z' if unsure where you are).")
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

-- the GOAL: a room with a cask (or whatever 'cs goal' is set to)
local function cs_reach_goal(gw)
  -- target item (cask/portal) is in the room: just PAUSE the bot. paused is the
  -- robust stop - cs_step/cs_advance/cs_on_prompt/watchdog all honour it.
  paused = true
  plan = nil
  pending_mob = nil
  room_goal_idx = nil; room_goal_word = nil   -- handled; don't re-pause on unpause
  note(string.upper(gw) .. " found - bot PAUSED ('cs pause' / Pause button to continue).")
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
  local party = scrye.getVariable("party")
  if party ~= nil and party ~= "" then
    for member in party:gmatch("[^\n]+") do
      if name:find(member, 1, true) then return end
    end
  end
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
local function frontier_remove(i) table.remove(frontier, i) end

local function valid_entry(e)
  local from_room = get_room({ x = e.x, y = e.y, z = e.z })
  local t = moved({ x = e.x, y = e.y, z = e.z }, e.dir)
  if t and not get_room(t) and from_room and from_room.exits[e.dir] then
    return t
  end
  return nil
end

local function pop_frontier()
  -- 1. unexplored down exit in the CURRENT room: dive immediately
  local cur = get_room(pos)
  if cur then
    for _, dd in ipairs({ "d", "down" }) do
      if cur.exits[dd] then
        local t = moved(pos, dd)
        if t and not get_room(t) then
          for i = #frontier, 1, -1 do
            local e = frontier[i]
            if e.x == pos.x and e.y == pos.y and e.z == pos.z
               and (e.dir == "d" or e.dir == "down") then
              frontier_remove(i)
            end
          end
          return { x = pos.x, y = pos.y, z = pos.z, dir = dd }, t
        end
      end
    end
  end
  -- 2. newest queued down exit anywhere on the map
  for i = #frontier, 1, -1 do
    local e = frontier[i]
    if e.dir == "d" or e.dir == "down" then
      local t = valid_entry(e)
      frontier_remove(i)
      if t then return e, t end
    end
  end
  -- 3. normal: newest unexplored exit
  while #frontier > 0 do
    local e = frontier[#frontier]
    frontier[#frontier] = nil
    local t = valid_entry(e)
    if t then return e, t end
  end
  return nil
end

-- ---------- pause / stepping ----------
local function cs_pause_toggle()
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

cs_step = function()
  if not enabled then note("not enabled - 'cs enable' first") return end
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
  local stash = {}
  local entry, target, path
  while true do
    entry, target = pop_frontier()
    if not entry then break end
    path = bfs(pos, { x = entry.x, y = entry.y, z = entry.z })
    if path then break end
    stash[#stash + 1] = entry
    entry, target = nil, nil
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
    else
      note("Out of rooms!")
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
  local path = bfs(pos, { x = 0, y = 0, z = 0 }, true)   -- going home may need to climb up
  if not path then note("no path back to start!") return end
  if #path == 0 then note("already at start") return end
  note("returning to 0 0 0 (" .. #path .. " steps)")
  plan = { dirs = path, idx = 1, awaiting = false, kind = "leave" }
  cs_advance()
end

local function reset()
  rooms = {}; frontier = {}
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
local function cs_new_sea()
  if not paused then note("New Sea only works while paused (at the cask)") return end
  note("opening cask & starting new sea #" .. seanum)
  scrye.send("open cask")
  scrye.after(1, function() scrye.send("retreat from the sea") end)
  scrye.after(2, function() scrye.send("unsetsea") end)
  scrye.after(3, function() scrye.send("setsea " .. seanum) end)
  scrye.after(4, function() scrye.send("enter sea") end)
  sea_time = now   -- start the sea-age clock
  cs_last_sea_min = nil
  cs_draw()
end

-- ---------- HUD panel state (replaces the miniwindow) ----------
local COLS, ROWS = 20, 16   -- same viewport as the original 280x224 @ 14px cells

cs_draw = function()
  -- exposed for other plugins, as the original did with SetVariable
  scrye.setVariable("cs_auto", auto and "1" or "0")
  scrye.setVariable("cs_enabled", enabled and "1" or "0")

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

  -- map of current level, centered on player, north = up
  local x0 = pos.x - math.floor(COLS / 2)
  local y0 = pos.y + math.floor(ROWS / 2)
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

scrye.addPanel{
  title = "3S Chaos Sea",
  width = 300,
  widgets = {
    { type = "value", text = "", bind = P .. "status" },
    { type = "value", text = "pos ",   bind = P .. "stats" },
    { type = "colorgrid", bind = P .. "map", palette = {
        ["."] = "#080A0C",   -- unknown / wall (map background)
        ["#"] = "#806040",   -- explored room
        ["v"] = "#30C0F0",   -- room with a down exit (blue dot)
        ["f"] = "#3070FF",   -- frontier target
        ["S"] = "#40C0FF",   -- start room 0 0 0
        ["@"] = "#60FF60",   -- you
      } },
    { type = "label", text = "@ you  f frontier  v down exit  S start" },
    { type = "value", text = "kills: ", bind = P .. "hunt" },
    { type = "value", text = "sea ",    bind = P .. "sea" },
    { type = "button", text = "On/Off", action = function() cs_interface(enabled and "disable" or "enable") end },
    { type = "button", text = "Step",   action = function() cs_step() end },
    { type = "button", text = "Auto",   action = function() cs_interface(auto and "auto off" or "auto on") end },
    { type = "button", text = "Pause",  action = function() cs_pause_toggle() end },
    { type = "button", text = "Leave",  action = function() cs_leave() end },
    { type = "button", text = "Reset",  action = function() cs_interface("reset") end },
    { type = "button", text = "Delay -", action = function() cs_interface("delay " .. (kill_delay - 0.5)) end },
    { type = "button", text = "Delay +", action = function() cs_interface("delay " .. (kill_delay + 0.5)) end },
    { type = "button", text = "New Sea", action = function() cs_new_sea() end },
  },
}

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
    note("  cs pause              hold everything / continue (also the Pause button)")
  elseif args == "enable" then
    enabled = true
    goal_found = false
    paused = false
    resting = false
    if not get_room(pos) then add_room(pos) end
    note("enabled - glance to map this room, then 'cs step' or 'cs auto on'")
    scrye.send("!glance")
  elseif args == "disable" then
    enabled = false; auto = false; note("disabled")
  elseif args == "reset" then
    reset(); note("map reset")
  elseif args == "step" then
    cs_step()
  elseif args == "auto on" then
    enabled = true; auto = true; goal_found = false; blind_steps = 0
    note("auto-exploring (kill: " .. killname .. ", stops at: " .. goal .. ")")
    -- map the CURRENT room first (a reload wipes the map, so the frontier may be empty). The
    -- glance re-parses the room, seeds the frontier, arms us, and the next prompt starts stepping.
    -- Only fall straight into cs_step() if we already have somewhere to go.
    if #frontier > 0 then cs_step() else scrye.send("!glance") end
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
      local path = bfs(pos, { x = tonumber(parts[2]) or 0, y = tonumber(parts[3]) or 0, z = tonumber(parts[4]) or 0 }, true)
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

-- ---------- alias ----------
scrye.addAlias{ pattern = [[^cs(?:\s+(.*))?$]], regex = true,
  run = function(w1) cs_interface(w1) end }

-- ---------- timers ----------
scrye.every(1, function() now = now + 1 end)   -- the clock (no os.time in Scrye)
scrye.every(5, cs_watchdog)

-- ---------- startup (was OnPluginInstall) ----------
load_state()
cs_draw()
