-- Harness for 3s-farmer (docs/Plan-Map-Farmer.md phase 2): the chassis. Room payload
-- shapes follow the same captures the mapper harnesses use.
--
-- The property at the bottom: everything it ever sends is a single bare direction, a
-- 'kill <word>', the panic command or the after-kill command.
--
-- Equivalent mutants (documented, not hunted): pick_target's map branch once read
-- `los.mobs[num] and not los.players[num]` - a grid cell is one glyph, 'm' or 'p', never
-- both, so the second half could not be false when the first was true; it is gone.

-- Run from anywhere: paths are resolved relative to THIS file, which sits
-- beside the plugin it tests.
local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
-- The plugin ships (src/Scrye.App/plugins/3s-farmer, promoted at 1.0.0); this file lives in
-- tests/plugins, not beside it, because the plugin folder is copied into the build output
-- and a test has no business shipping. CI runs it (tests/plugins/run.sh).
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-farmer/main.lua"
local MANIFEST = HERE .. "../../src/Scrye.App/plugins/3s-farmer/plugin.json"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

-- ---- a real JSON codec, because phase 1 round-trips its store through it ----
local function encode(v)
  local t = type(v)
  if t == "number" then return tostring(v) end
  if t == "boolean" then return tostring(v) end
  if t == "nil" then return "null" end
  if t == "string" then return '"' .. v:gsub('[\\"]', '\\%0'):gsub('\n', '\\n') .. '"' end
  if t ~= "table" then error("cannot encode " .. t) end
  local n = 0
  for _ in pairs(v) do n = n + 1 end
  if n == #v and n > 0 then
    local out = {}
    for _, x in ipairs(v) do out[#out + 1] = encode(x) end
    return "[" .. table.concat(out, ",") .. "]"
  end
  local keys = {}
  for k in pairs(v) do keys[#keys + 1] = tostring(k) end
  table.sort(keys)
  local out = {}
  for _, k in ipairs(keys) do
    local val = v[k] ; if val == nil then val = v[tonumber(k)] end
    out[#out + 1] = encode(k) .. ":" .. encode(val)
  end
  return "{" .. table.concat(out, ",") .. "}"
end

local decode
do
  local pos, src
  local function ws() pos = src:find("[^ \t\r\n]", pos) or #src + 1 end
  local function value()
    ws()
    local c = src:sub(pos, pos)
    if c == "{" then
      local t = {} ; pos = pos + 1 ; ws()
      if src:sub(pos, pos) == "}" then pos = pos + 1 ; return t end
      while true do
        ws()
        local k = value() ; ws() ; pos = pos + 1        -- past ':'
        t[k] = value() ; ws()
        local d = src:sub(pos, pos) ; pos = pos + 1
        if d == "}" then return t end
      end
    elseif c == "[" then
      local t = {} ; pos = pos + 1 ; ws()
      if src:sub(pos, pos) == "]" then pos = pos + 1 ; return t end
      while true do
        t[#t + 1] = value() ; ws()
        local d = src:sub(pos, pos) ; pos = pos + 1
        if d == "]" then return t end
      end
    elseif c == '"' then
      local out, i = {}, pos + 1
      while true do
        local ch = src:sub(i, i)
        if ch == "\\" then out[#out + 1] = src:sub(i + 1, i + 1) ; i = i + 2
        elseif ch == '"' then pos = i + 1 ; return table.concat(out)
        else out[#out + 1] = ch ; i = i + 1 end
      end
    else
      local lit = src:match("^%-?%d+%.?%d*", pos)
      if lit then pos = pos + #lit ; return tonumber(lit) end
      lit = src:match("^%a+", pos)
      pos = pos + #(lit or "")
      if lit == "true" then return true elseif lit == "false" then return false end
      return nil
    end
  end
  decode = function(s)
    if type(s) ~= "string" then return nil end
    src, pos = s, 1
    local ok, v = pcall(value)
    return ok and v or nil
  end
end

-- ---- the client, as far as the plugin can tell ----
local printed, sent
local emitted
local triggers, prompt_hooks
local shared
local gmcp_hooks, aliases, timers, connect_hooks, disconnect_hooks, command_hooks
local event_hooks
local idle_hooks
local store, repeats, next_timer
local panel, state
local notified = {}
local timer_secs = {}           -- timer id -> the seconds it was armed with
local every_sent = {}          -- every command sent all suite long, never reset

local function fresh_client(keep_store)
  printed, sent, emitted = {}, {}, {}
  gmcp_hooks, aliases, timers, triggers, prompt_hooks = {}, {}, {}, {}, {}
  event_hooks = {}
  connect_hooks, disconnect_hooks, command_hooks = {}, {}, {}
  idle_hooks = {}
  repeats = {}
  panel, state = nil, {}
  -- a healthy character by default: the HP gate (2 Sep) refuses to start with a floor set
  -- and no feed, and every test before it assumed a fit farmer
  state["char.vitals.hp"], state["char.vitals.maxhp"] = "1000", "1000"
  next_timer = 0
  if not keep_store then store = {} ; shared = {} end
  scrye = {
    id = "3s-farmer",
    print = function(s) printed[#printed + 1] = tostring(s) end,
    notify = function(s) notified[#notified + 1] = tostring(s) end,
    send  = function(s)
      sent[#sent + 1] = tostring(s)
      every_sent[#every_sent + 1] = tostring(s)
    end,
    json  = { decode = decode, encode = encode },
    store = {
      get = function(k) return store[k] end,
      set = function(k, v) store[k] = v end,
      delete = function(k) store[k] = nil end,
      setMany = function(t) for k, v in pairs(t) do store[k] = v end end,
    },
    shared = {
      get = function(k) return shared[k] end,
      set = function(k, v) shared[k] = v end,
      delete = function(k) shared[k] = nil end,
      setMany = function(t) for k, v in pairs(t) do shared[k] = v end end,
    },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    on = function(ev, fn) event_hooks[#event_hooks + 1] = { ev = ev, fn = fn } end,
    emit = function(ev, data) emitted[#emitted + 1] = { ev = ev, data = data } end,
    onConnect = function(fn) connect_hooks[#connect_hooks + 1] = fn end,
    onDisconnect = function(fn) disconnect_hooks[#disconnect_hooks + 1] = fn end,
    onIdle = function(fn) idle_hooks[#idle_hooks + 1] = fn end,
    onCommand = function(fn) command_hooks[#command_hooks + 1] = fn end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addTrigger = function(t) triggers[#triggers + 1] = t end,
    onPrompt = function(fn) prompt_hooks[#prompt_hooks + 1] = fn end,
    getVariable = function() return nil end,
    addPanel = function(pn) panel = pn end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    after = function(secs, fn) next_timer = next_timer + 1; timers[next_timer] = fn; timer_secs[next_timer] = secs; return next_timer end,
    every = function(_, fn) next_timer = next_timer + 1; repeats[next_timer] = fn; return next_timer end,
    cancel = function(id) if id then timers[id] = nil; repeats[id] = nil end end,
  }
end

local function load_plugin() dofile(PLUGIN) end
-- every widget of every tab, in order (the panel grew tabs on 20 Sep)
local function pw()
  local out = {}
  for _, t in ipairs(panel and panel.tabs or {}) do for _, w in ipairs(t.widgets) do out[#out + 1] = w end end
  return out
end

-- ---- driving it the way the client does ----
local function room(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Info" then h.fn(json) end end
end
local function cmd(text) for _, h in ipairs(command_hooks) do h(text) end end
local function emit(ev, data, source)
  for _, h in ipairs(event_hooks) do if h.ev == ev then h.fn(data, nil, source) end end
end
local function farm(arg) aliases[1].run(arg) end
local function prompt() for _, fn in ipairs(prompt_hooks) do fn() end end
local function contents(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Contents" then h.fn(json) end end
end
local function combat(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Char.Combat" then h.fn(json) end end
end
local function NV_of()
  local t = {}
  for line in ((state["plugin.3s-farmer.neverlist"] or "") .. "\n"):gmatch("([^\n]*)\n") do
    if line ~= "" and not line:find("^%(") then t[#t + 1] = line end
  end
  return t
end
local function fire_trigger(needle, ...)
  for _, t in ipairs(triggers) do
    if t.pattern:find(needle, 1, true) then t.run(...) return true end
  end
  return false
end
local function fire_timers()
  local t = timers; timers = {}
  for _, fn in pairs(t) do fn() end
end
local function tick() for _, fn in pairs(repeats) do fn() end end
local function disconnect() for _, fn in ipairs(disconnect_hooks) do fn() end end
local function idle() for _, fn in ipairs(idle_hooks) do fn() end end
local function connect() for _, fn in ipairs(connect_hooks) do fn() end end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end
local function last_line() return printed[#printed] or "" end
-- the last emitted event of a name, decoded
local function emitted_ev(name)
  for i = #emitted, 1, -1 do
    if emitted[i].ev == name then return decode(emitted[i].data) end
  end
  return nil
end
local function emitted_count(name)
  local n = 0
  for _, e in ipairs(emitted) do if e.ev == name then n = n + 1 end end
  return n
end
local function clear() printed = {} end
-- nothing attacked: at pace 0 (1.8.0) a prompt with nothing to fight steps on at once,
-- so "no fight" is "no kill", not "nothing sent"
local function no_kill()
  for _, c in ipairs(sent) do if c:find("^kill ") then return false end end
  return true
end

-- ---- the world ----
-- A little farmland: 1 -e- 2 -e- 3, with 3 also touching the Woods (room 4, mapped, other
-- area) and a door n to room 9 nobody has stood in. Exit values are real numbers, as the
-- ordinary world gives them.
local F1 = '{ "num": 1, "name": "Barn",    "area": "Farmland", "exits": { "e": 2 } }'
local F2 = '{ "num": 2, "name": "Yard",    "area": "Farmland", "exits": { "w": 1, "e": 3 } }'
local F3 = '{ "num": 3, "name": "Fence",   "area": "Farmland", "exits": { "w": 2, "e": 4, "n": 9 } }'
local W4 = '{ "num": 4, "name": "Thicket", "area": "Woods",    "exits": { "w": 3 } }'
local U0 = '{ "num": 7, "name": "A Vortex","area": "Unknown",  "exits": { "s": 8 } }'

-- walk the farmland so it is explored: 1, 2, 3, into the woods and back, ending on 3
local function explore()
  room(F1) ; tick()
  room(F2) ; tick()
  room(F3) ; tick()
  room(W4) ; tick()
  room(F3) ; tick()
end

-- ============================================================
fresh_client()
load_plugin()

-- 1. It reads the room feed and the fight feed, and only those.
check(#gmcp_hooks == 7, "seven GMCP hooks registered")
do
  local pkgs = {}
  for _, h in ipairs(gmcp_hooks) do pkgs[h.pkg] = true end
  check(pkgs["Room.Info"] and pkgs["Room.Contents"] and pkgs["Room.Map"] and pkgs["Char.Combat"]
        and pkgs["Guild.State"] and pkgs["Room.Death"] and pkgs["Char.XP"],
        "...Room.Info, Room.Contents, Room.Map, Char.Combat, Guild.State (Seid), Room.Death and Char.XP, nothing else")
end
check(#prompt_hooks == 1, "one prompt hook - attacks are decided at the prompt")
check(#aliases == 1, "one alias dispatcher")
check(panel ~= nil, "the panel is registered")

-- 1b. THE PANEL SPEAKS THE HOST'S SCHEMA. The first version of this panel used keys the
--     host has never had (`layout`/`kind`/`children`) and the HUD rendered an empty box,
--     silently, for weeks (Joakim's screenshot, 25 Aug) - the harness captured the spec
--     and never looked inside it. These checks are that look: `widgets` + `type` from the
--     PanelSpec vocabulary, buttonrows carrying `buttons`, binds carrying state.
do
  local KNOWN = { label=1, value=1, progress=1, gauge=1, text=1, colorgrid=1, button=1,
                  row=1, list=1, table=1, buttonrow=1, input=1 }
  check(type(panel.tabs) == "table" and #panel.tabs == 3 and panel.tabs[1].title == "Patrol"
        and panel.tabs[2].title == "Mobs" and panel.tabs[3].title == "Settings" and #panel.tabs[1].widgets > 0,
        "the panel declares three tabs of `widgets` - Patrol, Mobs, Settings (not the imaginary `layout`)")
  local bad = 0
  for _, w in ipairs(pw()) do
    if not KNOWN[tostring(w.type)] then bad = bad + 1 end
    if w.type == "buttonrow" and type(w.buttons) ~= "table" then bad = bad + 1 end
  end
  check(bad == 0, "every widget has a `type` the host knows; buttonrows carry `buttons`")
end

-- 1c. The buttons DO what they say, and the counters count.
do
  local function button(label)
    for _, w in ipairs(pw()) do
      if w.type == "buttonrow" then
        for _, b in ipairs(w.buttons) do if b.text == label then return b.action end end
      end
    end
  end
  check(button("Start") and button("Pause") and button("Stop"), "Start, Pause and Stop exist")
  explore()                                  -- a real little world to step in
  clear()
  button("Start")()
  check(said("patrolling Farmland"), "the Start button starts the patrol")
  for _ = 1, 65 do tick() end                -- a minute passes, NOTHING else happens
  local counters = state["plugin.3s-farmer.counters"] or ""
  check(counters:find("Session\t1:0", 1, true) ~= nil,
        "the session clock ticks visibly between arrivals, m:ss")
  check(counters:find("Kills\t0", 1, true) ~= nil, "kills starts at zero")
  sent = {}
  fire_timers() ; room(F2) ; tick()          -- one room traveled
  check((state["plugin.3s-farmer.counters"] or ""):find("Rooms\t1", 1, true) ~= nil,
        "rooms traveled counts the step")
  clear()
  button("Pause")()
  check(said("PAUSED"), "the Pause button pauses")
  check(button("Resume") ~= nil and panel.tabs[1].widgets[1].color == "warning",
        "paused: the button reads Resume and the status line turns warning-coloured (panel rebuilt)")
  button("Resume")()
  check(said("resuming"), "...and unpauses")
  check(button("Pause") ~= nil and panel.tabs[1].widgets[1].color == "success",
        "...and the panel is rebuilt back: Pause, status line success-coloured")
  clear()
  button("Stop")()
  check(said("patrol stopped"), "the Stop button stops the patrol")
  local frozen = (state["plugin.3s-farmer.counters"] or ""):match("Session\t([%d:]+)")
  check(frozen ~= nil and frozen ~= "0:00",
        "the stopped run's clock still shows the length it reached, not zero")
  for _ = 1, 30 do tick() end
  farm("status") ; -- any redraw path
  room(F3) ; tick()
  check((state["plugin.3s-farmer.counters"] or ""):match("Session\t([%d:]+)") == frozen,
        "...and it stays frozen there while time passes after the stop")
  fresh_client() ; load_plugin()             -- leave following tests a clean slate
end

-- 2. The graph is what you have stood in.
explore()
clear()
farm("status")
check(said("4 room(s) stood in"), "an arrival per room puts four rooms in the graph")

-- 3. Start refuses the places it must refuse.
fresh_client() ; load_plugin()
clear() ; farm("start")
check(said("not anywhere yet"), "start from nowhere refuses")
room(U0)
clear() ; farm("start")
check(said("refusing to lock"), "start in 'Unknown' refuses - that is not one place")
check(not said("patrolling"), "...and does not start")

-- 4. THE PATROL: lock, then one step per confirmed arrival, toward the stalest room.
fresh_client() ; load_plugin()
explore()                          -- standing on 3; room 1 is the stalest
clear() ; sent = {}
farm("start")
check(said("patrolling Farmland"), "start locks to the area underfoot")
check(#sent == 0, "...and the first step waits for the pace timer")
fire_timers()
check(#sent == 1 and sent[1] == "w", "the first step goes toward the stalest room (1)")
room(F2) ; fire_timers()
check(#sent == 2 and sent[2] == "w", "...arrival confirmed by number, next step follows")
room(F1) ; fire_timers()
check(#sent == 3 and sent[3] == "e", "...and at the stalest room it turns to the next stalest")
clear()
farm("status")
check(said("2 visited"), "the run counts the rooms it stepped into")

-- 5. THE FENCE, decisively: stand in a Farmland room whose ONLY neighbours are a mapped
--    room in the WRONG area (e -> 4, Woods) and a door nobody has stood in (n -> 9). Both
--    are refused even though they are the only options - the patrol waits rather than
--    leaks. Then the positive control: once an in-area neighbour is explored, it walks it.
fresh_client() ; load_plugin()
room(F3) ; tick()                  -- only 3 (Farmland) and...
room(W4) ; tick()                  -- ...4 (Woods) are in the graph
room(F3) ; tick()                  -- standing back on 3; 2 was never stood in
farm("start")
sent = {}
fire_timers()
check(#sent == 0, "wrong-area and never-stood-in exits are both refused, even alone")
clear() ; farm("status")
check(said("patrolling Farmland"), "...the patrol waits inside the fence, still on")
farm("stop")
room(F2) ; tick()                  -- now 2 IS explored...
room(F3) ; tick()
farm("start")
sent = {}
fire_timers()
check(#sent == 1 and sent[1] == "w", "...and the moment an in-area room exists, it walks it")

-- 6. An arrival nothing ordered stops the patrol: a wimpy, a teleport, a summon.
fresh_client() ; load_plugin()
explore()
farm("start") ; fire_timers()      -- step w outstanding
room(F2)                           -- lands: explained
clear() ; sent = {}
room(F1)                           -- nothing ordered this
check(said("moved by something that was not the patrol"), "an unexplained arrival stops it")
fire_timers()
check(#sent == 0, "...and no further step goes out")

-- 7. A step that lands in the WRONG room stops the patrol.
fresh_client() ; load_plugin()
explore()
farm("start") ; fire_timers()      -- step w outstanding, expecting 2
clear()
room(F1)                           -- arrived in 1 instead
check(said("expecting 2 but arrived in 1"), "a wrong-numbered landing stops it and says both")

-- 8. You typing a move stops it; our own step echoing back does not.
fresh_client() ; load_plugin()
explore()
farm("start") ; fire_timers()
cmd(sent[#sent])
check(not said("you moved yourself"), "our own step echoing through onCommand is not you")
cmd("n")
check(said("you moved yourself"), "a move of yours is, and it stops the patrol")

-- 9. A refusal drops the lying edge and picks another way - here, standing on 2 with the
--    step toward 1 refused, the other in-area way (e, to 3) is taken instead.
fresh_client() ; load_plugin()
explore()
farm("start") ; fire_timers()      -- step w toward 2, from 3
room(F2)                           -- standing on 2 now; next step is scheduled
fire_timers()                      -- step w toward 1 goes out
check(sent[#sent] == "w", "(the step about to be refused is w)")
clear() ; sent = {}
fire_trigger("You cannot go")
check(said("edge dropped"), "a refusal drops the edge")
fire_timers()
check(#sent == 1 and sent[1] == "e", "...and the patrol takes the other in-area way instead")

-- 10. Combat pauses the patrol and its end resumes it - the one deliberate resume.
fresh_client() ; load_plugin()
explore()
farm("start") ; fire_timers()
room(F2)                           -- arrived; next step now on the pace timer
clear()
combat('{ "attacker": "an angry bull", "attacker_hp": "healthy", "rounds": 1, "target": "you" }')
check(said("combat - patrol waiting"), "combat starting parks the patrol")
sent = {}
fire_timers()
check(#sent == 0, "...no step goes out while fighting")
farm("pause") ; farm("pause")      -- unpausing schedules a fresh timer even mid-fight...
fire_timers()
check(#sent == 0, "...and even a freshly scheduled step holds while the fight is on")
clear()
combat('{ "attacker": "", "rounds": 0 }')
check(said("combat over - patrol resuming"), "combat ending resumes it")
fire_timers()
check(#sent == 1, "...and the next step goes out")

-- 10b. GMCP-only combat end (MIP coexists on 3Scapes - verified 29 Aug - but a no-MIP
--      character has only GMCP, where the fight END is unreliable): a terminal
--      Char.Combat may keep the attacker's name
--      with the rounds zeroed, or never come at all. A ZOMBIE terminal is a report, not a
--      fight - the patrol resumes.
fresh_client() ; load_plugin()
explore()
farm("start") ; fire_timers()
room(F2)
combat('{ "attacker": "an angry bull", "attacker_hp": "healthy", "rounds": 2, "target": "you" }')
clear()
combat('{ "attacker": "an angry bull", "attacker_hp": "dead", "rounds": 0, "target": "you" }')
check(said("combat over - patrol resuming"),
      "a terminal that keeps the attacker's name but zeroes the rounds still ends the fight")
sent = {}
fire_timers()
check(#sent == 1, "...and the patrol steps on")

-- 10c. NO terminal at all: the rounds just stop coming. The shelf life ends the fight
--      through the ordinary combat-over path - same note, same resume.
fresh_client() ; load_plugin()
explore()
farm("start") ; fire_timers()
room(F2)
combat('{ "attacker": "an angry bull", "attacker_hp": "healthy", "rounds": 1, "target": "you" }')
clear()
for _ = 1, 9 do tick() end                 -- nine silent seconds outlive the shelf life
check(said("combat over - patrol resuming"),
      "total silence at the kill cannot wedge the patrol: staleness ends the fight")
sent = {}
fire_timers()
check(#sent == 1, "...and the patrol steps on")

-- 10d. ...but a LONG fight with live rounds is not ended by impatience: each heartbeat
--      refreshes the shelf life, well past its length.
fresh_client() ; load_plugin()
explore()
farm("start") ; fire_timers()
room(F2)
clear()
for _ = 1, 10 do
  combat('{ "attacker": "an angry bull", "attacker_hp": "healthy", "rounds": 3, "target": "you" }')
  tick()
end
check(not said("combat over"), "rounds arriving keep the fight alive past the shelf life")
sent = {}
fire_timers()
check(#sent == 0, "...and no step goes out mid-fight")
combat('{ "attacker": "", "rounds": 0 }')
fire_timers()

-- 11. A step that never lands stops the patrol via the clock.
fresh_client() ; load_plugin()
explore()
farm("start") ; fire_timers()      -- step outstanding
clear()
for _ = 1, 12 do tick() end
check(said("never landed"), "a silent step times out and stops the patrol")

-- 12. Pause is a hand brake, and start-while-on refuses.
fresh_client() ; load_plugin()
explore()
farm("start")
clear() ; farm("start")
check(said("already patrolling"), "start while on refuses")
farm("pause")
sent = {}
fire_timers()
check(#sent == 0, "paused sends nothing")
farm("pause")
fire_timers()
check(#sent == 1, "unpaused resumes")

-- 13. Excludes: per area, settable now, enforced in phase 3.
fresh_client() ; load_plugin()
explore()
farm("start")
clear()
farm("exclude smurfette")
check(said("excluded in Farmland: smurfette"), "an exclude lands in the locked area's list")
farm("exclude smurfette")
check(said("already excluded"), "...once")
clear() ; farm("excludes")
check(said("smurfette"), "the list shows it")
farm("include smurfette")
clear() ; farm("excludes")
check(said("nothing excluded"), "include removes it")

-- 14. The graph and the excludes survive a reload.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("exclude gnome")
disconnect()                       -- flushes the store
fresh_client(true) ; load_plugin() -- same store, fresh client
clear()
farm("status")
check(said("4 room(s) stood in"), "the graph survives a reload")
room(F3)
farm("start")
clear() ; farm("excludes")
check(said("gnome"), "...and so do the excludes")

-- 15. A one-room area patrols by standing still, not by crashing.
fresh_client() ; load_plugin()
room('{ "num": 50, "name": "Clearing", "area": "Glade", "exits": { } }')
farm("start")
sent = {}
fire_timers()
check(#sent == 0, "one room, nowhere to step: it waits where it is")
clear() ; farm("status")
check(said("patrolling Glade"), "...and is still on patrol")

-- ============================================================
-- THE FISTS (phase 3): the target list IS the feed; a kill is a room being cleared
-- before the patrol moves on.

local CUR    = '{ "full": 1, "items": [ { "name": "Small cur", "type": "monster" } ] }'
local TWO    = '{ "full": 1, "items": [ { "name": "Small cur", "type": "monster" }, '
            .. '{ "name": "Blob-like abhorrence", "type": "monster" } ] }'
local BLOB   = '{ "full": 1, "items": [ { "name": "Blob-like abhorrence", "type": "monster" } ] }'
local EMPTY  = '{ "full": 1, "items": [] }'
local MIXED  = '{ "full": 1, "items": [ { "name": "Small cur", "type": "monster" }, '
            .. '{ "name": "Stabby, the Entropy Herald {Offensive}", "type": "player" } ] }'
local GUARD  = '{ "full": 1, "items": [ '
            .. '{ "name": "A warband in service to Goran [Legendary] [Grey-bearded] [6]", "type": "monster" } ] }'

-- 17. A monster in the room is attacked at the prompt, by its last plain word, and the
--     patrol holds its step while the hunt is on.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(CUR)
sent = {}
prompt()
check(#sent == 1 and sent[1] == "kill cur", "the mob is attacked by keyword at the prompt")
fire_timers()
check(#sent == 1, "...and the pace timer sends no step while hunting")
farm("pause") ; farm("pause")              -- unpausing schedules a fresh step timer...
fire_timers()
check(#sent == 1, "...which the hunt still holds: no step while the fists are out")

-- 17b. The pace timer can fire in the gap between the roster arriving and the prompt
--      deciding. The step must hold for the imminent fight - walking away from a mob the
--      prompt was about to attack leaves a trail of half-decisions around the area.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(CUR)
sent = {}
fire_timers()                              -- pace timer fires BEFORE any prompt
check(#sent == 1 and sent[1] == "kill cur",
      "a live target holds the step, and the tick attacks it itself - no prompt required")
prompt()
check(#sent == 1, "...and the prompt that follows does not attack it twice")
check((state["plugin.3s-farmer.counters"] or ""):find("Waiting\thunting Small cur", 1, true) ~= nil,
      "the panel's Waiting row says what the patrol is standing still for")

-- 18. The killing blow counts, breathes, consults the FRESH roster, and moves to the next
--     mob - then, roster empty, the patrol resumes.
state["enemy.name"] = "cur"
fire_trigger("dealt the killing blow", "You", "You")
state["enemy.name"] = nil                  -- combat over by the time the breath ends
contents(BLOB)                             -- the server refreshed the room: cur is gone
sent = {}
fire_timers()                              -- the breath timer
check(#sent == 1 and sent[1] == "kill abhorrence",
      "after the breath the next mob from the fresh roster is attacked")
fire_trigger("dealt the killing blow", "You", "You")
contents(EMPTY)
sent = {}
fire_timers()                              -- breath again: roster now empty
check(#sent == 0 or sent[1] ~= "kill abhorrence", "(no re-attack on an empty roster)")
fire_timers()                              -- the patrol's pace timer
local stepped = false
for _, c in ipairs(sent) do if c == "w" or c == "e" then stepped = true end end
check(stepped, "...and with the room clear the patrol steps on")
clear() ; farm("status")
check(said("2 kill(s)"), "both kills counted")

-- 19. Excluded mobs are not touched - and with only excluded mobs, the patrol walks on.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("exclude small cur")
contents(CUR)
sent = {}
prompt()
check(no_kill(), "an excluded mob is not attacked")
fire_timers()
check(#sent == 1 and (sent[1] == "w" or sent[1] == "e"), "...and the patrol simply walks on")

-- 19b. Excluding from the roster itself: a left click on a mob's row toggles it in this
--      area's excludes; the right-click menu offers the same and the never-list, every
--      entry the typed command. The row's verdict follows on the panel.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(TWO)
local roster
for _, w in ipairs(pw()) do if w.bind == "plugin.3s-farmer.roster" then roster = w end end
check(roster and type(roster.onRowClick) == "function", "the roster table takes row clicks")
clear()
roster.onRowClick(nil, 1)                          -- row 1 is Small cur
check(said("excluded in Farmland: small cur"), "clicking a mob row excludes it in this area")
check((state["plugin.3s-farmer.roster"] or ""):find("Small cur\t1\texcluded", 1, true) ~= nil,
      "...and its row now says so")
sent = {}
prompt()
check(#sent == 1 and sent[1] == "kill abhorrence", "...so the next mob is the one attacked")
fire_trigger("dealt the killing blow", "You", "You") ; contents(CUR) ; fire_timers()
clear()
roster.onRowClick(nil, 1)
check(said("no longer excluded in Farmland: small cur"), "clicking it again includes it")
local m = roster.onRowMenu(nil, 1)
check(m and m[1][1] == "Exclude here" and m[1][2] == "farm exclude small cur"
        and m[2][1] == "Never attack anywhere" and m[2][2] == "farm never small cur",
      "the row menu offers the exclude and the never-list, as typed commands")
farm("never small cur")
m = roster.onRowMenu(nil, 1)
check(m and m[2][1] == "Allow everywhere again" and m[2][2] == "farm never -small cur",
      "...and the reverse once it is on the never-list")
check((state["plugin.3s-farmer.roster"] or ""):find("Small cur\t1\texcluded", 1, true) ~= nil,
      "a never-listed mob's row says excluded straight away")
clear() ; farm("never small cur")
check(said("already on the never-list"), "the never-list does not take a name twice")
farm("never -small cur")
check(roster.onRowMenu(nil, 99) == nil, "a row past the roster has no menu")

-- 19c. The lists are ON the panel (17 Sep): the never-list and this area's excludes as
--      tables, a row click allows that name again, and a box adds to the never-list by
--      hand - for the follower that is not in the room right now.
fresh_client() ; load_plugin()
explore()
farm("start")
local never_w, box_w, ex_w
for _, w in ipairs(pw()) do
  if w.bind == "plugin.3s-farmer.neverlist" then never_w = w end
  if w.bind == "plugin.3s-farmer.neverbox" then box_w = w end
  if w.bind == "plugin.3s-farmer.exlist" then ex_w = w end
end
check(never_w and never_w.type == "table" and box_w and box_w.type == "input" and ex_w and ex_w.type == "table",
      "the panel carries the never-list table, its add box, and the excludes table")
check((state["plugin.3s-farmer.neverlist"] or ""):find("nothing", 1, true) ~= nil, "an empty never-list says so on the panel")
never_w.onRowClick(nil, 1)                       -- a placeholder row carries no name
check(#NV_of() == 0, "clicking the placeholder row does nothing")
box_w.onSubmit("  Hirdmadr ")
check((state["plugin.3s-farmer.neverlist"] or "") == "hirdmadr", "the box adds to the never-list, trimmed and lowercased")
check((state["plugin.3s-farmer.neverbox"] or "") == "", "...and clears itself")
box_w.onSubmit("warband")
check((state["plugin.3s-farmer.neverlist"] or "") == "hirdmadr\nwarband", "one name per row, in list order")
clear()
never_w.onRowClick(nil, 1)
check(said("never-list: warband"), "clicking a never-list row allows that name again")
check((state["plugin.3s-farmer.neverlist"] or "") == "warband", "...and the table follows")
farm("exclude small cur") ; farm("exclude blob")
check((state["plugin.3s-farmer.exlist"] or "") == "small cur\nblob", "this area's excludes are listed the same way")
clear()
ex_w.onRowClick(nil, 2)
check(said("no longer excluded in Farmland: blob"), "clicking an exclude row includes it again")
check((state["plugin.3s-farmer.exlist"] or "") == "small cur", "...and the table follows")
farm("stop")
fresh_client() ; load_plugin()
check((state["plugin.3s-farmer.exlist"] or ""):find("per area", 1, true) ~= nil, "with no area locked yet the excludes table says the list is per area")

-- 19e. THE PARTY ON THE PANEL (17 Sep): players are on the roster by name - "party" or
--      "stranger - hands off" - a click on a player's row toggles them in the party list,
--      the list is a table (click to drop) with a box to add by name.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(MIXED)                             -- a cur and a stranger
local ros = state["plugin.3s-farmer.roster"] or ""
check(ros:find("\t\tstranger - hands off", 1, true) ~= nil, "a player in the room is a roster row, named, hands off")
check((state["plugin.3s-farmer.partylist"] or ""):find("nobody", 1, true) ~= nil, "an empty party list says so")
local roster_w, party_w, pbox_w
for _, w in ipairs(pw()) do
  if w.bind == "plugin.3s-farmer.roster" then roster_w = w end
  if w.bind == "plugin.3s-farmer.partylist" then party_w = w end
  if w.bind == "plugin.3s-farmer.partybox" then pbox_w = w end
end
check(party_w and party_w.type == "table" and pbox_w and pbox_w.type == "input", "the party table and its box are on the panel")
-- find the player's row
local prow
local i = 0
for line in (ros .. "\n"):gmatch("([^\n]*)\n") do i = i + 1 ; if line:find("stranger", 1, true) then prow = i end end
check(prow ~= nil, "(the stranger's row is found)")
local pname = ros:match("([^\n]-)\t\tstranger")
clear()
roster_w.onRowClick(nil, prow)
check(said("party: " .. pname:lower()), "clicking a player's row puts them in the party")
check((state["plugin.3s-farmer.roster"] or ""):find(pname .. "\t\tparty", 1, true) ~= nil, "...and the row now says party")
check((state["plugin.3s-farmer.partylist"] or "") == pname:lower(), "...and the party table lists them")
local m = roster_w.onRowMenu(nil, prow)
check(m and m[1][1] == "Not in my party" and m[1][2] == "farm party -" .. pname:lower(), "the row menu offers the reverse")
sent = {}
prompt()
check(#sent == 1 and sent[1] == "kill cur", "with the player in the party the cur is fair game")
clear()
party_w.onRowClick(nil, 1)
check(said("party: (empty)"), "clicking the party row drops them")
pbox_w.onSubmit(" Bjorn ")
check((state["plugin.3s-farmer.partylist"] or "") == "bjorn", "the box adds a name, trimmed and lowercased")
clear() ; pbox_w.onSubmit("bjorn")
check(said("already in the party list"), "...and not twice")
farm("stop")

-- 20. A player in the room means hands off EVERYTHING, and walk on.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(MIXED)
clear() ; sent = {}
prompt()
check(said("a player is here"), "a non-party player parks the fists")
check(no_kill(), "...nothing is attacked")
fire_timers()
check(#sent == 1 and (sent[1] == "w" or sent[1] == "e"), "...and the patrol walks on")

-- 21. A PARTY player does not count as a stranger; the mob beside them is fair game.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("party stabby")
contents(MIXED)
sent = {}
prompt()
check(#sent == 1 and sent[1] == "kill cur", "a party member watching is not a stranger")

-- 22. GUILD FOLLOWERS. A warband (and every guild's cousin of it) is mob-typed, follows
--     you across every area, and drifts in a round BEHIND you - so it is not in the
--     arrival's roster, it appears in the REFRESH that its own entrance causes. The
--     never-list has to catch it there, mid-room, and in every area without re-adding.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("never warband")
sent = {}
contents(EMPTY)                            -- we arrive first: the room is empty...
prompt()
fire_timers()                              -- ...so the patrol steps out
check(#sent == 1, "(arrived first, room empty, patrol stepping)")
room(F2)                                   -- landing in the next room...
contents(GUARD)                            -- ...and the warband drifts in behind us
sent = {}
prompt()
check(no_kill(), "the follower's own entrance refreshes the roster; the never-list holds")
-- and the never-list is GLOBAL: same follower, different area, no re-adding
farm("stop")
room(W4)                                   -- standing in the Woods now
farm("start")
contents(GUARD)
sent = {}
prompt()
check(#sent == 0, "...in every area, without excluding it again")

-- 22b. The never-list survives a reload, like the rest of the configuration.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("never warband")
disconnect()
fresh_client(true) ; load_plugin()
room(F3)
farm("start")
contents(GUARD)
sent = {}
prompt()
check(no_kill(), "the never-list survives a reload")

-- 23. 'There is no X here.' mid-hunt drops the stale name and consults the roster.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(TWO)
prompt()                                   -- kill cur goes out
sent = {}
fire_trigger("There is no", "cur")         -- the parser does not take "cur" for it...
check(#sent == 1 and sent[1] == "kill small",
      "the name's next word is tried before the mob is given up on (2 Sep: keyword candidates)")
sent = {}
fire_trigger("There is no", "small")       -- ...nor "small": it is not here under any word
check(#sent == 1 and sent[1] == "kill abhorrence",
      "a target missing under every word is dropped and the next one attacked")

-- 23a. A fight that ends WITHOUT our killing-blow line - the mob fled, or died to a wound
--      we did not land last - still continues the hunt off the Char.Combat edge, rather
--      than the patrol strolling away from what is left in the room.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(TWO)
prompt()                                   -- kill cur goes out
combat('{ "attacker": "Small cur", "attacker_hp": "hurt", "rounds": 2, "target": "you" }')
contents(BLOB)                             -- cur gone from the room...
sent = {}
combat('{ "attacker": "", "rounds": 0 }')  -- ...and the fight just ends, no blow of ours
fire_timers()                              -- the breath scheduled off the combat edge
check(#sent == 1 and sent[1] == "kill abhorrence",
      "a fight ending without our blow still hunts what is left, not the exit")

-- 23b. The breath does not swing while something is still hitting us: the roster is only
--      consulted once the client's combat state clears.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(TWO)
prompt()                                   -- kill cur
state["enemy.name"] = "cur"
fire_trigger("dealt the killing blow", "You", "You")
contents(BLOB)
sent = {}
fire_timers()                              -- breath ends but enemy.name is still set
check(#sent == 0, "still in combat at the breath's end: no swing yet")
state["enemy.name"] = nil
fire_timers()                              -- the re-armed breath
check(#sent == 1 and sent[1] == "kill abhorrence", "...combat clear, the hunt continues")

-- 23b. A stale enemy.name (MIP said fighting, nothing ever cleared it, no Char.Combat
--      has spoken) used to freeze the patrol for good: the tick saw combat, returned, and
--      nothing re-armed it. Now it holds, says why ONCE, and keeps looking - and steps
--      the moment the state clears. Same for a live target no prompt ever decided.
fresh_client() ; load_plugin()
explore()
state["enemy.name"] = "a ghost of a fight"
farm("start")
clear()
sent = {}
fire_timers()
check(#sent == 0, "a stale enemy.name still holds the step (it might be a real fight)")
check(said("holding - in combat (enemy.name = 'a ghost of a fight')"),
      "...but the patrol says what it is waiting on")
check((state["plugin.3s-farmer.counters"] or ""):find("Waiting\tin combat (enemy.name", 1, true) ~= nil,
      "...on the panel too")
clear() ; farm("")
check(said("waiting: in combat"), "...and in 'farm'")
clear()
fire_timers()
check(not said("holding"), "the same hold is not repeated every tick")
state["enemy.name"] = nil
fire_timers()
check(#sent == 1 and sent[1] ~= "kill cur" and #sent[1] <= 2,
      "the state cleared: the next tick steps out, nothing had to re-arm it")

-- 24. An explicitly EMPTY room attacks nothing and patrols on; so does a SILENT room -
--     absence proves nothing, but it also targets nothing.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(EMPTY)
sent = {}
prompt()
check(no_kill(), "an explicitly empty room starts no fight")
fire_timers()
check(#sent == 1, "...and the patrol steps")
room(F2)                                   -- next room: Contents suppressed, nothing arrives
sent = {}
prompt()
check(no_kill(), "a silent room starts no fight either")

-- 25. THE ROSTER CARRIES ACROSS THE THRESHOLD - deliberately. The server suppresses a
--     Room.Contents identical to the last one sent, so a silent arrival means "same
--     population as the room you left". Walk from one cur-room into the next and the
--     suppressed Contents IS the server saying the cur is here too: the carried roster
--     attacks it. (A differing population always arrives and replaces the roster.)
--     This was nearly built the other way; the mutation run caught it.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("exclude small cur")                  -- so the patrol keeps walking past the first cur
contents(CUR)
fire_timers()                              -- steps out of the cur-room normally
room(F2)                                   -- next room: Contents suppressed (identical)
farm("include small cur")                  -- now curs are fair game again
sent = {}
prompt()
check(#sent == 1 and sent[1] == "kill cur",
      "a suppressed Contents means same population: the carried roster attacks")

-- 26. Seid resting: below the floor no step goes out; recovery resumes the patrol.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("rest 100 30")
state["vik.seid"] = "40"
sent = {}
fire_timers()                              -- pace timer: rest check trips
check(#sent == 0, "below the Seid floor no step goes out")
check(said("Seid low"), "...and it says it is resting")
state["vik.seid"] = "150"
fire_timers()                              -- the rest timer
check(said("recovered - patrol resuming"), "recovery is announced")
fire_timers()                              -- the rescheduled pace timer
check(#sent == 1, "...and the patrol steps again")

-- 26b. Seid over GMCP (the Viking Guild.State, live 27 Aug). MIP's vik.* state is dead
--      on a GMCP world; Guild.State page carries the number as points.vitka - the value
--      in the status line's S[cur|max] slot - and once seen it OUTRANKS the vik.* reads.
local function guild(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Guild.State" then h.fn(json) end end
end
fresh_client() ; load_plugin()
explore()
farm("start")
farm("rest 100 30")
-- no vik.* state exists at all: only the GMCP number
guild('{ "guild": "viking", "page": 3, "pages": 3, "points": { "vitka": 40, "mvitka": 7152, "viga": 8082 } }')
sent = {}
fire_timers()
check(#sent == 0 and said("Seid low"), "Guild.State's points.vitka drives the rest floor")
guild('{ "guild": "viking", "page": 3, "pages": 3, "points": { "vitka": 150, "mvitka": 7152 } }')
fire_timers()
check(said("recovered - patrol resuming"), "...and its recovery resumes the patrol")
fire_timers()
check(#sent == 1, "...which steps again")
room(F2) ; tick()                          -- land the step so the patrol is free again
-- a Guild.State PAGE WITHOUT the points block (page 1 has bars/gline instead) must not
-- forget the number: the last seen vitka stands until a points block replaces it
clear()
guild('{ "guild": "viking", "page": 1, "pages": 3, "bars": { "gp1": 150, "gp1_max": 7152 } }')
state["vik.seid"] = "40"                   -- even a (stale) MIP value must not outrank it
sent = {}
fire_timers()
check(#sent == 1 and not said("Seid low"),
      "a pointless page keeps the last GMCP Seid, and it outranks vik.*")

-- 28. THE MAPPER'S MEMORY. The live gap this closes (Joakim, 25 Aug): the farmer looped
--     only rooms stepped this session, ignoring everything explored and saved to the
--     mapper's map in earlier sessions. 'farm start' now ASKS the mapper for the area
--     (map.query.area) and adopts its answer - so "explored" means every room anybody
--     ever stood in, not every room this plugin happened to be loaded for.
fresh_client() ; load_plugin()
room(F3) ; tick()                          -- this session we have only stood in room 3
farm("start")
check(emitted[#emitted] ~= nil and emitted[#emitted].ev == "map.query.area",
      "farm start asks the mapper about the area")
-- the mapper's answer: rooms 1 and 2, explored in some earlier session
emit("map.area.rooms",
  '{ "area": "Farmland", "rooms": [ '
  .. '{ "num": 1, "name": "Barn", "area": "Farmland", "exits": { "e": 2 } }, '
  .. '{ "num": 2, "name": "Yard", "area": "Farmland", "exits": { "w": 1, "e": 3 } } ] }',
  "3s-map-gmcp")
check(said("offered 2 room(s) for this area - 2 adopted, 0 corrected"), "the mapper's rooms are adopted and announced")
sent = {}
fire_timers()
check(#sent == 1 and sent[1] == "w",
      "...and the patrol heads straight for them: adopted rooms are the stalest")
room(F2) ; fire_timers()
check(#sent == 2 and sent[2] == "w", "...walking the adopted map exactly as its own")

-- 28b. The farmer's own fresher knowledge is never overwritten by the mapper's older copy.
fresh_client() ; load_plugin()
explore()                                  -- room 3 stood in, exits w/e/n known fresh
farm("start")
emit("map.area.rooms",
  '{ "area": "Farmland", "rooms": [ '
  .. '{ "num": 3, "name": "Stale Fence", "area": "Farmland", "exits": { } } ] }',
  "3s-map-gmcp")
check(said("offered 1 room(s) for this area - 0 adopted, 0 corrected"),
      "a room the farmer has stood in, filed under the SAME area, is not adopted over")
clear() ; farm("status")
check(not said("Stale Fence"), "...and keeps its own fresher record")

-- 28c. The mapper's answer never smuggles other areas past the fence: adopted or not,
--      the area lock still holds. (The mapper only sends the asked-for area, but the
--      farmer does not TRUST that - a wrong-area room in the payload is still fenced.)
fresh_client() ; load_plugin()
room(F3) ; tick()
farm("start")
emit("map.area.rooms",
  '{ "area": "Farmland", "rooms": [ '
  .. '{ "num": 4, "name": "Thicket", "area": "Woods", "exits": { "w": 3 } } ] }',
  "3s-map-gmcp")
sent = {}
fire_timers()
check(#sent == 0, "an adopted wrong-area room is still outside the fence")
check(said("filed under other area names, outside this fence: Woods (1)"),
      "...and the answer SAYS which offered rooms live under other names")

-- 27. THE SHARED STORE (API 1.14): world-truth is shared across characters; knobs are not.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("never warband")
farm("exclude smurfette")
farm("party stabby")
farm("pace 3")
disconnect()
check(shared["graph"] ~= nil and shared["never"] ~= nil and shared["excludes"] ~= nil
      and shared["party"] ~= nil, "graph, never-list, excludes and party land in the shared store")
check(store["graph"] == nil and store["never"] == nil, "...not in the private one")
check(store["pace"] == "3", "...while the pace knob stays private to this profile")
check(shared["pace"] == nil, "...and never leaks into the shared file")

-- 27b. Pre-shared data is adopted once; a shared value already present wins.
fresh_client()
store["never"] = '["warband"]'
load_plugin()
room(F3)
farm("start")
sent = {}
contents(GUARD)
prompt()
check(#sent == 0, "an old private never-list is adopted and enforced")
check(shared["never"] ~= nil, "...into the shared store")

-- 27c. No scrye.shared on the host: everything still works out of the private store.
fresh_client()
scrye.shared = nil
load_plugin()
explore()
farm("start")
farm("never warband")
disconnect()
check(store["never"] ~= nil, "no scrye.shared: the never-list saves privately as before")

-- 29. STAMPS DO NOT SURVIVE A RELOAD - the live 4-room-orbit bug (Joakim, 25 Aug).
--     The clock is session-local, so persisted stamps from last session (large) made every
--     already-touched room this session (stamps near zero) permanently "staler" than the
--     rooms not yet reached, and the patrol orbited the same few rooms forever. On load
--     every room is stamp-zero: not visited THIS run, all equally due.
fresh_client() ; load_plugin()
explore()                                  -- rooms 1..4 with stamps 1..5 on the old clock
for _ = 1, 200 do tick() end               -- the old session runs long: big clock
room(F1) ; room(F3)                        -- restamp a couple of rooms up at ~200
farm("start") ; farm("stop")
disconnect()                               -- persists the graph, big stamps and all
fresh_client(true) ; load_plugin()         -- new session: the clock is back at zero
room(F3) ; tick()                          -- we stand in 3; its stamp is now ~0
farm("start")
local visited = {}
for i = 1, 12 do
  sent = {}
  fire_timers()
  local mv = sent[1]
  if not mv then break end
  -- answer honestly from room 3's little world: 3 -w- 2 -w- 1
  local at = { w = { [3] = F2, [2] = F1 }, e = { [1] = F2, [2] = F3 } }
  -- track where we are by replaying: simplest is to accept either direction and feed the
  -- right room; the check below is about COVERAGE, not the exact route
  if mv == "w" then
    room(visited.at == 1 and F1 or (visited.at == 2 and F1 or F2))
    visited.at = (visited.at == 2) and 1 or 2
  else
    room(visited.at == 1 and F2 or F3)
    visited.at = (visited.at == 1) and 2 or 3
  end
  visited[visited.at] = true
end
check(visited[1] and visited[2], "after a reload the patrol reaches the OLD rooms, not an orbit")

-- 30. A POISONED GRAPH HEALS - the second live orbit mechanism (Joakim, 25 Aug): the
--     shared graph held rooms under the right NUMBERS but the wrong (empty) area, so the
--     mapper's offer said "already known" while the exact-area fence could not see them,
--     and no amount of re-offering ever fixed it. The mapper is the authority on where a
--     room is: a filed-area disagreement is corrected in place, stamp kept, and the
--     correction is saved - the poison is gone from the store, not just from this session.
fresh_client()
shared["graph"] = '{ "rooms": [ '
  .. '{ "num": 1, "name": "Ruin",   "area": "", "exits": { } }, '
  .. '{ "num": 2, "name": "Rubble", "area": "", "exits": { } } ] }'
load_plugin()
room(F3) ; tick()
farm("start")                              -- counts 1 room: the poison is invisible
emit("map.area.rooms",
  '{ "area": "Farmland", "rooms": [ '
  .. '{ "num": 1, "name": "Barn", "area": "Farmland", "exits": { "e": 2 } }, '
  .. '{ "num": 2, "name": "Yard", "area": "Farmland", "exits": { "w": 1, "e": 3 } } ] }',
  "3s-map-gmcp")
check(said("offered 2 room(s) for this area - 0 adopted, 2 corrected"),
      "rooms known by number but filed under the wrong area are corrected, and it says so")
sent = {}
fire_timers()
check(#sent == 1 and sent[1] == "w", "...and the fence reaches them at once")
local g30 = decode(shared["graph"] or "") or {}
local persisted = false
for _, r in ipairs(g30.rooms or {}) do
  if tonumber(r.num) == 1 and r.area == "Farmland" and r.name == "Barn"
     and type(r.exits) == "table" and tonumber(r.exits.e) == 2 then persisted = true end
end
check(persisted, "...and the whole correction - area, name, exits - is saved: the stored graph is healed for good")

-- 30b. An answer that lost its per-room areas in transit still files rooms under the
--      area the farmer ASKED about - it is the only area the mapper was asked for, and
--      "" would just brew fresh poison.
fresh_client() ; load_plugin()
room(F3) ; tick()
farm("start")
emit("map.area.rooms",
  '{ "area": "Farmland", "rooms": [ { "num": 2, "name": "Yard", "exits": { "w": 1, "e": 3 } } ] }',
  "3s-map-gmcp")
check(said("offered 1 room(s) for this area - 1 adopted, 0 corrected"),
      "a room offered with no area at all is still adopted")
sent = {}
fire_timers()
check(#sent == 1 and sent[1] == "w", "...filed under the asked-for area, inside the fence")

-- 31. THE MAPPER'S RESOLVED EXITS FILL THIS GRAPH'S GAPS - the live 2295 case (Joakim,
--     25 Aug): the server reports some exits with destination 0 even outside the sea
--     (Smurfland 1849's n reads 0), so the room behind one is in the graph but no edge
--     reaches it - NO PATH inside the fence, forever. The mapper resolves such exits by
--     walking them; its offer carries the number, and the merge adopts it. 'farm rooms'
--     is the report that shows the room cut off before and reachable after.
fresh_client()
shared["graph"] = '{ "rooms": [ '
  .. '{ "num": 1, "name": "Barn", "area": "Farmland", "exits": { "e": 2 } }, '
  .. '{ "num": 2, "name": "Yard", "area": "Farmland", "exits": { "w": 0, "e": 3 } } ] }'
load_plugin()
room(F3) ; tick()
farm("start")
clear() ; farm("rooms")
check(said("1  Barn  NO PATH inside the fence"),
      "a room behind an unresolved (0) exit is reported cut off")
check(said("2  Yard  not yet this run"), "...its neighbour is reachable, just not visited")
clear()
emit("map.area.rooms",
  '{ "area": "Farmland", "rooms": [ '
  .. '{ "num": 2, "name": "Yard", "area": "Farmland", "exits": { "w": 1, "e": 3 } } ] }',
  "3s-map-gmcp")
check(said("and 1 exit destination(s) the mapper had resolved were filled in"),
      "the mapper's resolved destination fills the gap, and the answer says so")
clear() ; farm("rooms")
check(said("1  Barn  not yet this run"), "...and the cut-off room is now inside the fence")
local g31 = decode(shared["graph"] or "") or {}
local merged = false
for _, r in ipairs(g31.rooms or {}) do
  if tonumber(r.num) == 2 and type(r.exits) == "table" and tonumber(r.exits.w) == 1 then merged = true end
end
check(merged, "...and the filled-in edge is saved with the graph")
clear()
emit("map.area.rooms",
  '{ "area": "Farmland", "rooms": [ '
  .. '{ "num": 2, "name": "Yard", "area": "Farmland", "exits": { "w": 1, "e": 3, "n": 0 } } ] }',
  "3s-map-gmcp")
check(not said("filled in"),
      "an unresolved (0) destination in the offer fills nothing - and does not claim to")
sent = {}
fire_timers() ; room(F2) ; tick()
fire_timers()
check(#sent == 2 and sent[1] == "w" and sent[2] == "w",
      "...and the patrol walks through the healed edge to the once-cut-off room")

-- 32. NEAREST-STALEST AND THE CIRCUIT NOTE. Among equally-stale rooms the patrol goes to
--     the NEAREST (the old lowest-number rule marched a fresh start across the area to
--     its lowest-numbered corner - watched live, that reads as an orbit). And the first
--     time the stalest reachable room is one already visited this run, the patrol says a
--     full circuit is done - once - so "the same rooms again" reads as looping, not stuck.
room(F1) ; tick()
clear()
fire_timers()                              -- all visited now: the next target is a re-visit
check(said("full circuit - every reachable Farmland room visited this run; looping"),
      "the first re-visit target announces the circuit is complete")
clear()
room(F2) ; tick()
fire_timers()
check(not said("full circuit"), "...and it is said once per patrol, not every lap")
clear() ; farm("rooms")
check(said("visited 3 of 3 this run"), "'farm rooms' counts the whole circuit as visited")

-- 32b. NEAREST beats lowest-numbered. Room 9 one step away (n) and room 1 two steps away
--      (w,w) are equally stale; the patrol must take the near one. The old rule picked
--      the lowest number - room 1 - and a fresh start in a big area walked its whole
--      length before touching the room next door.
fresh_client()
shared["graph"] = '{ "rooms": [ '
  .. '{ "num": 1, "name": "Barn", "area": "Farmland", "exits": { "e": 2 } }, '
  .. '{ "num": 9, "name": "Shed", "area": "Farmland", "exits": { "s": 3 } } ] }'
load_plugin()
room(F2) ; tick()                          -- rooms 2 and 3 are stamped by standing in them;
room(F3) ; tick()                          -- 1 (far) and 9 (adjacent) are equally stale
farm("start")
sent = {}
fire_timers()
check(#sent == 1 and sent[1] == "n",
      "of two equally-stale rooms the patrol goes to the NEAR one, not the low-numbered one")

-- 32c. A new patrol gets its own circuit note.
room('{ "num": 9, "name": "Shed", "area": "Farmland", "exits": { "s": 3 } }') ; tick()
room(F1) ; tick()                          -- an unexplained move: stops the patrol, stamps 1
farm("start")                              -- every reachable room now carries a stamp
sent = {} ; clear()
fire_timers()                              -- the very first pick is a re-visit
check(said("full circuit"), "a restarted patrol announces its own completed circuit anew")
room(F2) ; tick()                          -- land the step so the next patrol starts clean
farm("stop")
farm("start")
sent = {} ; clear()
fire_timers()
check(said("full circuit"),
      "...and again after the NEXT restart: the flag is per patrol, not per load")

-- 33. TRAVEL (phase 4): 'farm go <area>' asks the mapper (map.goto) and starts the patrol
--     on map.walk.arrived. The farmer never walks the trip itself - the mapper's walk
--     contract answers with exactly one of arrived/stopped, and both ends are handled.

-- 33a. The ask goes out; silence (no mapper loaded) is caught, not waited on forever.
fresh_client() ; load_plugin()
room(F3) ; tick()
farm("go farmland")
local ask = emitted_ev("map.goto")
check(ask ~= nil and ask.area == "farmland", "'farm go' asks the mapper with the typed area")
check(said("asking the mapper"), "...and says it is asking")
clear()
fire_timers()                                -- the nobody-answered timer
check(said("no mapper answered"), "silence is reported: no mapper, or one too old for map.goto")
clear() ; farm("status")
check(not said("traveling"), "...and the trip state is cleared")

-- 33b. A refusal (map.walk.stopped) ends the trip and repeats the mapper's reason.
farm("go farmland")
emit("map.walk.stopped", '{ "reason": "no known route into \'farmland\'" }', "3s-map-gmcp")
check(said("did not finish - no known route"), "a stopped walk ends the trip, reason and all")
clear()
fire_timers()
check(not said("no mapper answered"), "...and the silence timer went down with it")

-- 33b2. An answered trip's timer is CANCELLED, not just outlived: a second trip started
--       inside the first one's two-second window must not be shot down by the first
--       trip's stale timer when it fires.
farm("go farmland")
emit("map.walk.stopped", '{ "reason": "held" }', "3s-map-gmcp")
farm("go woods")                             -- within the first trip's window
emit("map.walk.started", '{ "target": 9, "steps": 4 }', "3s-map-gmcp")
clear()
fire_timers()
check(not said("no mapper answered"),
      "an earlier trip's silence timer cannot kill the trip after it")
clear() ; farm("status")
check(said("traveling to 'woods'"), "...which is still in flight")
farm("stop")

-- 33c. The good path: started is narrated, arrived starts the patrol where we landed.
farm("go farmland")
emit("map.walk.started", '{ "target": 1, "steps": 2 }', "3s-map-gmcp")
check(said("walking us toward"), "the mapper taking the walk is narrated")
clear()
emit("map.walk.arrived", '{ "num": 3 }', "3s-map-gmcp")   -- we stand in 3 already
check(said("patrolling Farmland"), "arrival starts the patrol, locked where we landed")
clear() ; farm("status")
check(not said("traveling"), "...and the trip is over - arrived consumed it")
farm("stop")

-- 33d. The dispatch-order race (phase B, F5): the mapper announces the arrival BEFORE
--      our own Room.Info hook has processed the same burst. Its map.room feed - emitted
--      right before the arrival, in Room.Info's shape - is what closes the gap: fed
--      through the same handler, `here` is right by the time arrived follows it, and the
--      start is immediate. No timer is armed for it any more.
clear()
farm("go farmland")
emit("map.room", F1, "3s-map-gmcp")                        -- the mapper saw room 1 first
emit("map.walk.arrived", '{ "num": 1 }', "3s-map-gmcp")   -- our own hook has not run yet
check(said("patrolling Farmland"), "map.room ahead of the arrival: the start is immediate and locks the right area")
room(F1) ; tick()                            -- our own copy of the burst lands late...
clear() ; farm("status")
check(said("patrolling") and not said("stopped"), "...and the late second copy is not a move: the patrol is untouched")
farm("stop")

-- 33e. 'farm stop' mid-trip cancels it and takes the mapper's walk down too.
clear()
farm("go farmland")
farm("stop")
check(said("travel cancelled"), "'farm stop' cancels a trip in flight")
check(emitted[#emitted] ~= nil and emitted[#emitted].ev == "map.stop",
      "...and tells the mapper to stop the walk")
clear()
emit("map.walk.arrived", '{ "num": 1 }', "3s-map-gmcp")
check(not said("patrolling"), "...and a late arrival event starts nothing")

-- 33f. 'farm go' while patrolling stops the patrol first - one driver at a time.
farm("start")
clear()
farm("go elsewhere")
check(said("patrol stopped - traveling first"), "a running patrol yields to the trip")
farm("stop")

-- =====================================================================
-- PHASE A SAFETY (2 Sep 2026, Plan-Map-Farmer-Round2 F1-F3): the three that decide
-- whether the bot can be left alone for ten minutes.

-- 34. THE HP GATE. Under the start floor no fight is started and no step goes out - it
--     rests exactly as the Seid floor does - and the prompt honours it too: a mob in the
--     room is not a reason to fight at 20% HP.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("hp 50")
state["char.vitals.hp"] = "200"            -- 20% of 1000
contents(CUR)
sent = {}
prompt()
check(#sent == 0, "hp gate: a mob in the room is NOT attacked under the start floor")
check(said("HP low (20% < 50%)"), "...and the reason names the numbers")
fire_timers()                              -- pace timer / rest timer
check(#sent == 0, "hp gate: no step goes out either")
state["char.vitals.hp"] = "900"
fire_timers()                              -- the rest timer finds us fit
check(said("recovered - patrol resuming"), "hp gate: recovery resumes")
sent = {}
prompt()
check(#sent == 1 and sent[1] == "kill cur", "hp gate: ...and the mob is attacked once fit")

-- 34b. MIP is the fallback feed for the same gate.
fresh_client() ; load_plugin()
state["char.vitals.hp"], state["char.vitals.maxhp"] = nil, nil
state["character.health.current"], state["character.health.max"] = "300", "1000"
explore()
farm("start")
farm("hp 50")
contents(CUR)
sent = {}
prompt()
check(#sent == 0 and said("HP low (30% < 50%)"), "hp gate: reads character.health.* when Char.Vitals is absent")

-- 34c. A floor with no number behind it refuses to run - "unknown" is not "fine".
fresh_client() ; load_plugin()
state["char.vitals.hp"], state["char.vitals.maxhp"] = nil, nil
explore()
clear()
farm("start")
check(said("no HP feed has spoken") and not said("patrolling Farmland"), "hp gate: no feed + a floor = refused start")
farm("hp 0 0")
farm("start")
check(said("patrolling Farmland"), "hp gate: ...and with the floors off it starts")

-- 34d. Floors persist, and 'farm hp' alone reports them.
fresh_client(true) ; load_plugin()
clear() ; farm("hp")
check(said("start off, panic off"), "hp: the floors survive a reload and are reported")
farm("hp 40 15") ; farm("panic flee")
fresh_client(true) ; load_plugin()
clear() ; farm("hp")
check(said("start 40%, panic 15%"), "hp: set floors survive a reload")
clear() ; farm("panic")
check(said("'flee'"), "panic: the command survives a reload")
farm("panic -") ; farm("hp 50 0")

-- 34x. THE ELEVATOR IS NOT A ROOM TO PATROL (17 Sep). The car is in the same area as its
--      lobbies and the lobby's 'n' leads into it, so by area alone it is fenced in - and
--      the patrol rode the Megacity lift. The mapper's shifting mark on the car's 's'
--      rides along in map.area.rooms and map.room; a link into a room whose way back is
--      marked shifting is not a patrol link - the mapper's own layout rule.
local LOBBY = '{ "num": 30, "name": "Floor 30 Lobby", "area": "Tower", "exits": { "n": 46, "e": 31 } }'
local HALL  = '{ "num": 31, "name": "Floor 30 Hall",  "area": "Tower", "exits": { "w": 30 } }'
local CAR   = '{ "num": 46, "name": "Elevator",       "area": "Tower", "exits": { "s": 30 } }'
fresh_client() ; load_plugin()
room(LOBBY) ; tick() ; room(CAR) ; tick()          -- stood in the car once, by the server's
room(LOBBY) ; tick() ; room(HALL) ; tick() ; room(LOBBY) ; tick()   -- mark-less payload
farm("start")
emit("map.area.rooms",
  '{ "area": "Tower", "rooms": [ '
  .. '{ "num": 30, "name": "Floor 30 Lobby", "area": "Tower", "exits": { "n": 46, "e": 31 } }, '
  .. '{ "num": 31, "name": "Floor 30 Hall",  "area": "Tower", "exits": { "w": 30 } }, '
  .. '{ "num": 46, "name": "Elevator",       "area": "Tower", "exits": { }, "shift": { "s": true } } ] }',
  "3s-map-gmcp")
sent = {}
for _ = 1, 6 do fire_timers() ; if #sent > 0 then break end end
check(#sent >= 1 and sent[1] ~= "n",
      "the car - never visited, so the stalest room - is NOT stepped into: its way back shifts")
check(sent[1] == "e", "...the hall is the patrol's next room instead")
clear() ; farm("rooms")
check(not said("cut off") or said("46"), "(the car is simply outside the fence)")
-- The mark survives the round trip through the graph store, and arrives by map.room too.
disconnect() ; fresh_client(true) ; load_plugin()
room(HALL) ; tick() ; room(LOBBY) ; tick()  -- these two fresh, the car (last 0) the stalest
farm("start") ; sent = {}
for _ = 1, 6 do fire_timers() ; if #sent > 0 then break end end
check(sent[1] == "e", "the mark is persisted with the graph: a fresh session still keeps out of the car")
farm("stop")
fresh_client() ; load_plugin()
room(LOBBY) ; tick()
emit("map.room", '{ "num": 46, "name": "Elevator", "area": "Tower", "exits": { "s": 30 }, "shift": { "s": true } }', "3s-map-gmcp")
room(CAR) ; tick()                          -- the server's own payload, mark-less, follows...
room(LOBBY) ; tick() ; room(HALL) ; tick() ; room(LOBBY) ; tick()   -- ...and the car is stalest
farm("start") ; sent = {}
for _ = 1, 6 do fire_timers() ; if #sent > 0 then break end end
check(sent[1] == "e", "a mark learned from map.room is kept through the server's own mark-less Room.Info")

-- 34y. WITHHELD DESTINATIONS SURVIVE THE ARRIVAL (17 Sep). Megacity lists most exits
--      with destination 0; the mapper walks them and hands the destinations over at
--      'farm start'. Each arrival then REPLACED the room's exits with the server's
--      zeros, and the handover leaked away one room at a time until only told links
--      were left - a four-room orbit round the Central Plaza.
local PZ1 = '{ "num": 61, "name": "Plaza",  "area": "Plaza", "exits": { "e": 0, "w": 0 } }'   -- both withheld
local PZ2 = '{ "num": 62, "name": "East",   "area": "Plaza", "exits": { "w": 0 } }'
local PZ3 = '{ "num": 63, "name": "West",   "area": "Plaza", "exits": { "e": 0 } }'
fresh_client() ; load_plugin()
room(PZ1) ; tick()
farm("start")
emit("map.area.rooms",
  '{ "area": "Plaza", "rooms": [ '
  .. '{ "num": 61, "name": "Plaza", "area": "Plaza", "exits": { "e": 62, "w": 63 } }, '
  .. '{ "num": 62, "name": "East",  "area": "Plaza", "exits": { "w": 61 } }, '
  .. '{ "num": 63, "name": "West",  "area": "Plaza", "exits": { "e": 61 } } ] }',
  "3s-map-gmcp")
check(said("2 exit destination(s) the mapper had resolved were filled in"), "the mapper's walked destinations fill the plaza's withheld exits")
sent = {}
fire_timers()
check(#sent == 1 and (sent[1] == "e" or sent[1] == "w"), "...and the patrol can leave the plaza")
local went = sent[1]
room(went == "e" and PZ2 or PZ3) ; tick()          -- the server's own zeros again, on arrival
fire_timers()                                       -- back to the plaza (the only link)
room(PZ1) ; tick()                                  -- and the PLAZA's arrival: zeros for e and w
sent = {}
fire_timers()
check(#sent == 1 and sent[1] == (went == "e" and "w" or "e"),
      "an arrival's withheld destinations keep what the handover resolved: the other side is still reachable")
clear() ; farm("rooms")
check(not said("NO PATH"), "...and nothing is cut off - no orbit")
-- a direction the server no longer lists IS gone, and one it names is its own
room('{ "num": 61, "name": "Plaza", "area": "Plaza", "exits": { "e": 99 } }') ; tick()
clear() ; farm("rooms")
check(said("63  West  NO PATH"), "a direction the server dropped is dropped: the west room is cut off now")

-- 35. THE PANIC FLOOR. Mid-fight, HP under it: the patrol stops for good, a notify goes
--     out (the phone hears it), and the chosen command is sent once.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("hp 0 30") ; farm("panic flee")
contents(CUR)
prompt()                                   -- kill cur
combat('{ "attacker": "Small cur", "rounds": 3, "target": "you" }')
state["char.vitals.hp"] = "250"            -- 25%
sent = {}
clear()
tick()
check(said("HP 25% under the panic floor of 30%") and said("patrol stopped"), "panic: the patrol stops with the reason")
check(#sent == 1 and sent[1] == "flee", "panic: the chosen command goes out once")
check(#notified > 0 and notified[#notified]:find("panic floor", 1, true) ~= nil, "panic: ...and it notifies")
sent = {}
tick()
check(#sent == 0, "panic: nothing more is sent on the next tick - stopped is stopped")
clear() ; farm("status")
check(said("off."), "panic: ...and the patrol is off")
farm("panic -")

-- 36. THE HUNT WATCHDOG. A 'kill' nothing answers - no combat, no "There is no X here" -
--     gives the name up for this room and asks the roster again; with nothing left the
--     patrol steps on instead of standing forever with its fists up.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(TWO)
prompt()                                   -- kill cur, and then... nothing
sent = {}
for _ = 1, 8 do tick() end                 -- hunt_wait is 8 s
check(#sent == 0, "watchdog: nothing happens inside the wait")
clear()
tick()
check(said("went unanswered for 8s") and said("giving up on Small cur"), "watchdog: the name is given up after the wait")
check(#sent == 1 and sent[1] == "kill abhorrence", "watchdog: ...and the next name is tried")
sent = {}
for _ = 1, 9 do tick() end
check(#sent == 0 or sent[1] ~= "kill abhorrence", "watchdog: the second name is given up too")
fire_timers()                              -- the pace timer the hunt's end scheduled
local stepped = false
for _, c in ipairs(sent) do if c == "w" or c == "e" then stepped = true end end
check(stepped, "watchdog: ...and with nothing left to swing at, the patrol steps on")
-- the give-ups are per room: back in the room later, the roster is trusted again
-- the give-ups are per room: the step lands in the Yard, the same two names there are
-- a fresh roster, and the cur is swung at again
room(sent[#sent] == "w" and F2 or W4)
sent = {}
contents(TWO)
prompt()
check(#sent == 1 and sent[1] == "kill cur", "watchdog: give-ups are forgotten in the next room")

-- 36b. A fight that DID start is not a watchdog case, however long it runs.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(CUR)
prompt()
combat('{ "attacker": "Small cur", "rounds": 1, "target": "you" }')
sent = {} ; clear()
for _ = 1, 12 do combat('{ "attacker": "Small cur", "rounds": 5, "target": "you" }') ; tick() end
check(#sent == 0 and not said("went unanswered"), "watchdog: a live fight never trips it")

-- 36c. NO SWITCHING MID-FIGHT (1.8.1, Joakim 18 Oct 2026: it opened on a Wiremouth guard
--      and a few rounds later swung at a mob with another name in the same room). The
--      watchdog timed a 'kill' from when it was SENT, so one quiet Char.Combat more than
--      hunt_wait into a long fight read as "unanswered", gave the live target up and
--      swung at the next name. A new 'kill' now goes out only once the fight is over.
local function fight_two()
  fresh_client() ; load_plugin()
  explore()
  farm("start")
  contents(TWO)
  prompt()                                 -- kill cur
  combat('{ "attacker": "Small cur", "rounds": 1, "target": "you" }')
  for _ = 1, 10 do combat('{ "attacker": "Small cur", "rounds": 3, "target": "you" }') ; tick() end
  sent = {} ; clear()
end
local function swung_other()
  for _, c in ipairs(sent) do if c == "kill abhorrence" then return true end end
  return false
end

-- one quiet payload between rounds, ten seconds into the fight
fight_two()
combat('{ "attacker": "", "rounds": 0, "target": "" }')
tick() ; tick()
combat('{ "attacker": "Small cur", "rounds": 3, "target": "you" }')   -- the fight goes on
fire_timers() ; tick()
check(not swung_other(), "mid-fight: a quiet moment does not switch to another mob")
check(not said("giving up on Small cur"), "mid-fight: ...and the live target is not given up on")

-- a second mob joining in: still the first one until it is dead
fight_two()
combat('{ "attacker": "Vile abhorrence", "rounds": 1, "target": "you" }')
for _ = 1, 5 do tick() end
fire_timers()
check(not swung_other(), "mid-fight: a second attacker does not pull the target over")

-- the payloads simply stop (no terminal at all): over only once it has gone stale, and
-- then the same mob is checked first - the cur is still listed, so it is the cur
fight_two()
for _ = 1, 14 do tick() end                -- CC_STALE is 8: over by staleness, well past hunt_wait
fire_timers()
check(not swung_other(), "mid-fight: a fight gone quiet does not jump to another name")
check(not said("giving up on Small cur"), "mid-fight: ...nor give up the mob it was fighting")
check(sent[#sent] == "kill cur", "mid-fight: the room is checked with the same mob first ("
      .. tostring(sent[#sent]) .. ")")

-- wounded, it is a new roster name, and the refresh may list the other mob first: the
-- mob being fought is still the one swung at once the fight has gone quiet
fight_two()
contents('{ "full": 1, "items": [ { "name": "Vile abhorrence", "type": "monster" }, '
         .. '{ "name": "Small cur [scratched]", "type": "monster" } ] }')
for _ = 1, 14 do tick() end
fire_timers()
check(sent[#sent] == "kill cur", "mid-fight: a wound tag and a reordered roster do not change the target ("
      .. tostring(sent[#sent]) .. ")")

-- and once the cur is really dead, the next mob IS fought - after the breath
fight_two()
fire_trigger("dealt the killing blow", "You", "Small cur")
combat('{ "attacker": "", "rounds": 0, "target": "" }')
fire_timers()
check(swung_other(), "after the kill: the next mob in the room is fought")

-- a 'kill' that is refused mid-fight ("There is no X here") waits for the fight as well
fight_two()
fire_trigger("There is no", "cur")
check(#sent == 0, "mid-fight: a refusal does not send another kill while the fight is on")

-- 37. KEYWORD CANDIDATES: the owner in "in service to <owner>" is never the first swing.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(GUARD)
sent = {}
prompt()
check(#sent == 1 and sent[1] == "kill warband", "keywords: 'A warband in service to Goran' swings at the warband, not at Goran")
fresh_client() ; load_plugin()
explore()
farm("start")
contents('{ "full": 1, "items": [ { "name": "A guard of the gate (sleeping)", "type": "monster" } ] }')
sent = {}
prompt()
check(#sent == 1 and sent[1] == "kill gate", "keywords: last plain word first, a (parenthesis) stripped")
fire_trigger("There is no", "gate")
check(sent[#sent] == "kill guard", "keywords: ...then the next word back")

-- =====================================================================
-- PHASE B (Plan-Map-Farmer-Round2 F4/F5/F7): the line-of-sight grid decides where the
-- patrol goes next, and one command of yours goes out after every kill.

local function losmap(rows)
  local q = {}
  for i, r in ipairs(rows) do q[i] = '"' .. r .. '"' end
  local json = '{ "kind": "los", "rows": [ ' .. table.concat(q, ", ") .. ' ] }'
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Map" then h.fn(json) end end
end
local function first_move()
  for _, c in ipairs(sent) do if MOVES_B[c] then return c end end
  return nil
end
MOVES_B = { n=1,s=1,e=1,w=1,ne=1,nw=1,se=1,sw=1,u=1,d=1 }

-- 40. Mobs in sight beat staleness. Standing in the Yard (2) with the Fence (3) the
--     stalest room but the grid showing monsters west in the Barn (1): the patrol goes west.
fresh_client() ; load_plugin()
explore()                                   -- ends on 3
room(F2) ; tick() ; room(F1) ; tick() ; room(F2) ; tick()   -- 3 is now the stalest
losmap({ "m-@-O" })
farm("start")
sent = {}
fire_timers()
check(first_move() == "w", "LOS: the room the grid shows monsters in is chosen over the stalest one")
farm("stop")

-- 40a. Your own followers are not mobs in sight. Standing in the Yard (2) with never-listed
--      hirdmadrs in the roster, step east to the Fence (3): they trail a room behind, so
--      the grid on arrival shows 'm' in the Yard. The Yard was quiet when we left it
--      (nothing attackable), so that 'm' is them - the patrol does not turn round for it.
fresh_client() ; load_plugin()
explore()                                   -- ends on 3
farm("never hirdmadr")
room(F2) ; tick()
contents('{ "full": 1, "items": [ { "name": "Goran\'s hirdmadr", "type": "monster" }, { "name": "Goran\'s hirdmadr", "type": "monster" } ] }')
room(F1) ; tick() ; room(F2) ; tick()       -- 3 is now the stalest, we stand in 2
farm("start")
sent = {}
fire_timers()                               -- steps e to 3
check(first_move() == "e", "(setup) the patrol steps east to the stalest room")
room(F3) ; tick()
losmap({ "m-@-O" })                         -- the guards, one room behind, in the Yard
sent = {}
fire_timers()
check((state["plugin.3s-farmer.counters"] or ""):find("(stalest)", 1, true) ~= nil,
      "LOS: an 'm' on the quiet room we just left is our own followers, not a target - staleness led the step")
farm("stop")
-- ...but a room we left EMPTY is not "quiet" in that sense: an 'm' appearing there is
-- something new (a spawn, a wanderer), and the patrol goes to look
fresh_client() ; load_plugin()
explore()
room(F2) ; tick()
contents(EMPTY)
room(F1) ; tick() ; room(F2) ; tick()
farm("start")
sent = {}
fire_timers()                               -- steps e to 3
room(F3) ; tick()
losmap({ "m-@-O" })
sent = {}
fire_timers()
check((state["plugin.3s-farmer.counters"] or ""):find("(mobs in sight)", 1, true) ~= nil,
      "LOS: an 'm' on a room that was empty when we left it is a room worth going back to - the map led")
farm("stop")

-- 41. SEEK (1.9.0, Joakim 20 Oct 2026): "search for a party member in an area ... it
--     wouldn't stop to fight mobs, just keeps going until it finds the person".
local WALKER   = '{ "full": 1, "items": [ { "name": "Jarl of Ein, Walker hinn Hersir af Sigurdsson", "type": "player" } ] }'
local STRANGER = '{ "full": 1, "items": [ { "name": "Skeksis the Shadow", "type": "player" } ] }'
local SKY      = '{ "full": 1, "items": [ { "name": "Lord Skywalkers", "type": "player" } ] }'

-- it fights nothing on the way
fresh_client() ; load_plugin()
explore()
farm("seek walker")
check(said("searching for walker"), "seek: it says what it is looking for")
contents(TWO)
sent = {}
prompt() ; fire_timers()
local killed = false
for _, c in ipairs(sent) do if c:find("^kill ") then killed = true end end
check(not killed, "seek: monsters in the room are not attacked")
check(first_move() ~= nil, "seek: ...it walks on past them")
farm("stop")

-- it goes TOWARD a player the grid shows (the patrol goes away from them)
fresh_client() ; load_plugin()
explore()
room(F2) ; tick() ; room(F1) ; tick() ; room(F2) ; tick()   -- 3 is the stalest, we stand in 2
losmap({ "p-@-O" })                         -- a player west, in the Barn
farm("seek walker")
sent = {}
fire_timers()
check(first_move() == "w", "seek: the room the grid shows a player in is chosen over the stalest one")

-- the wrong player: it keeps going, and that room stops pulling it for a while
room(F1) ; tick()
contents(STRANGER)
check(not said("found"), "seek: somebody else is not who it is looking for")
sent = {}
fire_timers()                               -- to the stalest room, the Fence: Barn > Yard > Fence
check(#sent == 2 and sent[1] == "e" and sent[2] == "e",
      "seek: a burst - the leg's steps go out at once (" .. table.concat(sent, ",") .. ")")
room(F2) ; tick()                           -- the first lands; the second is already on its way
losmap({ "p-@-O" })                         -- Skeksis is still in the Barn
sent = {}
fire_timers()
check(#sent == 0, "seek: mid-burst nothing more goes out, and the checked Barn does not pull it back")
clear()
for _ = 1, 9 do tick() end
check(not said("never landed") and not said("patrol stopped"), "seek: the burst's later step is awaited like any step")

-- the right player: it stops, says where, and buzzes the phone
room(F3) ; tick()
clear() ; notified = {}
contents(WALKER)
check(said("found walker here - Fence (3)"), "seek: finding them stops the search and says where")
check(#notified > 0 and notified[#notified]:find("found walker", 1, true) ~= nil, "seek: ...and notifies")
sent = {}
for _ = 1, 15 do tick() end ; fire_timers()
check(first_move() == nil, "seek: nothing walks after the find")
clear() ; farm("status")
check(said("off."), "seek: ...the search is off")

-- names match whole words only (checked in the room the search starts from: moving the
-- character ourselves would read as "something moved us" and stop it)
fresh_client() ; load_plugin()
explore()
farm("seek walker")
clear()
contents(SKY)
check(not said("found"), "seek: 'walker' does not find 'Skywalkers'")
check((state["plugin.3s-farmer.status"] or ""):find("SEEKING", 1, true) ~= nil, "seek: ...and the search goes on")
farm("stop")

-- a search leaves no verdicts behind: the rooms it walked past mobs in are not "nothing
-- worth fighting" to the next patrol, which still goes where the grid shows monsters
fresh_client() ; load_plugin()
explore()                                   -- ends on 3
farm("seek walker")
contents(TWO) ; prompt()                    -- mobs in the Fence, not fought
farm("stop")
room(F2) ; contents(EMPTY) ; tick() ; room(F3) ; contents(TWO) ; tick()
room(F2) ; contents(EMPTY) ; tick()         -- the Barn (1) is the stalest now
losmap({ "O-@-m" })                         -- the Fence's mobs, east
farm("start")
sent = {}
fire_timers()
check(first_move() == "e", "seek: the next patrol still goes to the mobs the search walked past")
farm("stop")

-- the rotation never moves a search on to another area
fresh_client() ; load_plugin()
explore()
farm("rota add Farmland") ; farm("rota add Woods") ; farm("rota idle 30") ; farm("rota on")
farm("seek walker")
sent = {} ; fire_timers() ; cmd(sent[#sent]) ; room(F2) ; fire_timers() ; cmd(sent[#sent]) ; room(F1)
fire_timers() ; cmd(sent[#sent]) ; room(F2)
check(said("no walker yet; going round again"), "seek: a full circuit without them says so and keeps going")
clear()
for _ = 1, 40 do tick() end
check(not said("moving on"), "seek: the rotation does not take a search to another area")
farm("stop")

-- already in the room: nothing to search
fresh_client() ; load_plugin()
explore()
contents(WALKER)
clear()
farm("seek Walker")
check(said("walker is right here"), "seek: the player already here is not searched for")
clear() ; farm("status")
check(said("off."), "seek: ...and nothing started")

-- attacked, it walks on (Joakim, 20 Oct 2026: "if I get attacked I want it to keep going")
fresh_client() ; load_plugin()
explore()
farm("seek walker")
clear()
combat('{ "attacker": "Small cur", "rounds": 2, "target": "you" }')
check(said("attacked - the search walks on"), "seek: being attacked is noted, not obeyed")
sent = {}
fire_timers() ; tick()
check(first_move() ~= nil, "seek: a step goes out while something is hitting us")
local swung = false
for _, c in ipairs(sent) do if c:find("^kill ") then swung = true end end
check(not swung, "seek: ...and it never swings back")
farm("stop")

-- low HP is not a reason for a search to stand still being hit (the patrol would rest)
fresh_client() ; load_plugin()
explore()
state["char.vitals.hp"] = "200"            -- 20%, under the default start floor of 50%
farm("seek walker")
sent = {}
fire_timers()
check(first_move() ~= nil and not said("resting"), "seek: the HP start floor does not park a search")
farm("stop")
state["char.vitals.hp"] = "1000"

-- seen in the FIRST room of a burst: the rest lands, then it walks back and stops there
fresh_client() ; load_plugin()
explore()                                   -- ends on 3
room(F2) ; tick() ; room(F3) ; tick()       -- the Barn (1) is the stalest now; we stand in 3
farm("seek walker")
sent = {}
fire_timers()
check(#sent == 2 and sent[1] == "w" and sent[2] == "w", "(setup) a two-step burst west to the Barn")
room(F2) ; tick()
clear()
contents(WALKER)                            -- there they are - and the second step is already gone
check(said("walker spotted in Yard (2) - the burst is still moving; walking back"), "seek: a sighting mid-burst is held")
room(F1) ; tick()                           -- the burst's last step lands
sent = {}
fire_timers()
check(first_move() == "e", "seek: ...and it walks back toward where they were seen")
room(F2) ; tick()
check(said("found walker here - Yard (2): seen as we went through, walked back"), "seek: back there, the search stops")

-- a step of a burst refused: the rest still lands from where we are, and the search goes on
fresh_client() ; load_plugin()
explore()
room(F2) ; tick() ; room(F3) ; tick()
farm("seek walker")
sent = {}
fire_timers()                               -- w, w toward the Barn
clear()
fire_trigger("You cannot go", "west")       -- the first 'w' refused: we are still in the Fence
check(said("1 step(s) of the burst still in flight"), "seek: a refusal mid-burst says what is still coming")
room(F2) ; tick()                           -- the second 'w' lands - from the Fence, into the Yard
check(not said("not the patrol") and not said("patrol stopped"), "seek: ...and its arrival is expected, not a stop")
sent = {}
fire_timers()
check(first_move() ~= nil, "seek: then it plans again from where it really is")
farm("stop")

-- ...and until the rest of the burst has landed nothing new goes out, even when something
-- asks for a step (here: pause and resume in between); a lost one is given up after the
-- step timeout, and the search plans again
fresh_client() ; load_plugin()
explore()
room(F2) ; tick() ; room(F3) ; tick()
farm("seek walker")
sent = {}
fire_timers()                               -- w, w
fire_trigger("You cannot go", "west")
room(F3)                                    -- the Fence described again: its 'w' is back
farm("pause") ; farm("pause")               -- resume schedules a step
sent = {}
fire_timers() ; prompt()
check(#sent == 0, "seek: with burst steps still in flight, no new step goes out")
for _ = 1, 12 do tick() end                 -- the second 'w' never lands nor is refused
fire_timers()
check(first_move() ~= nil, "seek: a burst step that never lands is given up, and the search plans again")
farm("stop")

-- the burst is a setting: 1 is one step at a time, like the patrol
fresh_client() ; load_plugin()
explore()
room(F2) ; tick() ; room(F3) ; tick()
farm("seek burst 1")
check(store["seek_burst"] == "1", "seek burst: kept")
farm("seek walker")
sent = {}
fire_timers()
check(#sent == 1, "seek burst 1: one step at a time")
farm("stop")

-- a running patrol turns into a search; a stop ends it, and the next start is a patrol again
fresh_client() ; load_plugin()
explore()
farm("start")
farm("seek walker")
check((state["plugin.3s-farmer.status"] or ""):find("SEEKING walker in Farmland", 1, true) ~= nil,
      "seek: a running patrol becomes the search (the panel says so)")
farm("stop")
farm("start")
contents(CUR)
sent = {}
prompt()
check(sent[1] == "kill cur", "seek: after a stop the next start is an ordinary patrol that fights")
farm("stop")

-- the panel has the box
local seekbox = false
for _, tab in ipairs(panel.tabs or {}) do
  for _, w in ipairs(tab.widgets or {}) do
    if w.type == "input" and tostring(w.bind):find("seekbox", 1, true) then seekbox = w end
  end
end
check(seekbox and seekbox.onSubmit, "seek: a 'seek <player>' box on the panel")
check(seekbox and seekbox.width == 30, "seek: the box is 30 characters wide")
fresh_client() ; load_plugin()
explore()
clear()
farm("seek " .. string.rep("w", 40))
check(said("searching for " .. string.rep("w", 30) .. " -") or said("searching for " .. string.rep("w", 30) .. " in"),
      "seek: a name is cut to 30 characters")
check(not said(string.rep("w", 31)), "seek: ...not one character more")
farm("stop")

-- 40b. Without a grid the fallback still walks to the stalest room (3, east).
fresh_client() ; load_plugin()
explore()
room(F2) ; tick() ; room(F1) ; tick() ; room(F2) ; tick()
farm("start")
sent = {}
fire_timers()
check(first_move() == "e", "no grid: the stalest room is still the target")
farm("stop")

-- 40c. Players in sight are avoided, both as a mob room and as a stale room.
fresh_client() ; load_plugin()
explore()
room(F2) ; tick() ; room(F1) ; tick() ; room(F2) ; tick()
losmap({ "O-@-p" })                         -- someone stands in the stalest room (3)
farm("start")
sent = {}
fire_timers()
check(first_move() == "w", "LOS: a room with a player in it is skipped even when it is the stalest")
farm("stop")
fresh_client() ; load_plugin()
explore()
room(F2) ; tick() ; room(F1) ; tick() ; room(F2) ; tick()
losmap({ "m-@-p" })
farm("start")
sent = {}
fire_timers()
check(first_move() == "w", "LOS: monsters west and a player east - west")
farm("stop")

-- 40d. The grid cannot lead the patrol out of the fence or into a room the graph has not
--      stood in: on the Fence (3), monsters east in the Woods (4) and north in unmapped
--      room 9 are both ignored, and the patrol walks west to the stalest farmland room.
fresh_client() ; load_plugin()
explore()                                   -- ends on 3; 1 is the stalest
losmap({ "  m  ", "  |  ", "O-@-m" })
farm("start")
sent = {}
fire_timers()
check(first_move() == "w", "LOS: monsters outside the fence or in unmapped rooms do not pull the patrol")
farm("stop")

-- 40e. The grid is only read along real links: an 'm' two cells east with NO link glyph
--      between is not the room east (a diagonal neighbour drawn on the same row, say).
fresh_client() ; load_plugin()
explore()
room(F2) ; tick()                           -- on 2; the Barn (1) is the stalest, west
losmap({ "O-@ m" })
farm("start")
sent = {}
fire_timers()
check(first_move() == "w", "LOS: a glyph with no link to it is not a neighbour - the stale fallback (west) wins")
farm("stop")

-- 40f. Two hops, and the picture is dropped on arrival. From the Barn (1) the grid shows a
--      PLAYER two rooms east (3, the stalest); the patrol heads for the Yard (2) instead
--      (first step e). Landing in 2 with no grid yet for it, the stale picture must not
--      keep steering: 3 is the stalest and is walked to (e) - a remembered 'p' there would
--      have sent the patrol back west.
fresh_client() ; load_plugin()
explore()                                   -- ends on 3
room(F2) ; tick() ; room(F1) ; tick()      -- on 1; stamps: 3 oldest, then 2
losmap({ "@-O-p" })
farm("start")
sent = {}
fire_timers()
check(first_move() == "e", "LOS: a player two links away is seen through the graph (the patrol still steps toward 2)")
room(F2) ; tick()                           -- arrived; the grid for THIS room has not come yet
sent = {}
fire_timers()
check(first_move() == "e", "...and on arrival the old picture is gone: the stalest room (3) is walked to, not avoided")
farm("stop")

-- 40d. ROOM RULES (17 Sep): 'farm room <n> avoid' - the patrol never enters it, and a
--      route through it is found around it; 'farm room <n> pass' - walked through, never a
--      destination, never a fight. Set from the map's right-click menu; cleared from the
--      panel's table. Persisted with the graph.
fresh_client() ; load_plugin()
explore()                                   -- 1-2-3, ends on 3
room(F2) ; tick() ; room(F1) ; tick() ; room(F2) ; tick()   -- 3 stalest, we stand in 2
farm("start")
clear() ; farm("room 3 avoid")
check(said("Fence (3): the patrol will never enter it"), "'farm room <n> avoid' names the room and the rule")
sent = {}
fire_timers()
check(#sent == 1 and sent[1] == "w", "an avoided room is not a destination: the patrol goes the other way")
clear() ; farm("rooms")
check(said("3  Fence  NO PATH"), "...and 'farm rooms' shows it cut off")
check((state["plugin.3s-farmer.rules"] or ""):find("3 Fence\tnever enter", 1, true) ~= nil, "the rule is on the panel")
check(state["plugin.3s-farmer.roomrules"] == "3:avoid", "...and published for the map to colour")
farm("room 3 -")
check((state["plugin.3s-farmer.rules"] or ""):find("none", 1, true) ~= nil, "'-' clears it")
-- pass: standing in a pass room, a mob there is not attacked, and the patrol moves on
room(F1) ; tick() ; room(F2) ; tick()       -- now in 2 again, 3 stalest (moving by hand stopped the patrol)
farm("start")
farm("room 2 pass")
contents(CUR)
sent = {}
prompt() ; fire_timers()
check(#sent == 1 and sent[1] == "e", "in a pass room the cur is not attacked and the patrol walks on")
check((state["plugin.3s-farmer.roster"] or ""):find("Small cur\t1\tpass room", 1, true) ~= nil, "...and the roster says why")
room(F3) ; tick()
farm("stop")
-- a pass room is never a destination: not for mobs in sight, not for staleness
fresh_client() ; load_plugin()
explore()                                   -- ends on 3
room(F2) ; tick()                           -- in 2: 1 is the stalest, 3 fresher
farm("room 3 pass")
losmap({ "O-@-m" })                         -- monsters east, in the pass room
farm("start")
sent = {}
fire_timers()
check(#sent == 1 and sent[1] == "w" and (state["plugin.3s-farmer.counters"] or ""):find("(stalest)", 1, true) ~= nil,
      "a pass room is never 'mobs in sight': it is transit, not a place to farm")
farm("stop")
fresh_client() ; load_plugin()
explore() ; room(F2) ; tick()
farm("room 1 pass")                         -- the stalest room is a pass room
farm("start")
sent = {}
fire_timers()
check(#sent == 1 and sent[1] == "e", "...nor the stalest: the patrol goes to the stalest room it may farm")
farm("stop")
fresh_client() ; load_plugin()
explore() ; room(F2) ; tick() ; farm("start") ; farm("room 2 pass")
-- the panel's table clears a rule by row click
local rules_w
for _, w in ipairs(pw()) do if w.bind == "plugin.3s-farmer.rules" then rules_w = w end end
check(rules_w ~= nil, "the rules table is on the panel")
clear()
rules_w.onRowClick(nil, 1)
check(said("Yard (2): rule cleared"), "clicking a rule row clears it")
-- persistence
farm("room 1 avoid")
disconnect() ; fresh_client(true) ; load_plugin()
check((state["plugin.3s-farmer.rules"] or ""):find("1 Barn\tnever enter", 1, true) ~= nil, "room rules survive a reload")
clear() ; farm("room")
check(said("1 room rule(s)") and said("never enter"), "'farm room' lists them")
clear() ; farm("room 5 wibble")
check(said("farm room <n> avoid | pass | rest | -"), "a bad rule word is usage")

-- 41. 'farm after <cmd>' goes out once after each kill's breath, before the next swing;
--     it survives a reload; '-' clears it; and the property test below knows about it.
fresh_client() ; load_plugin()
explore()
farm("after get all from corpse")
check(said("after each kill: 'get all from corpse'"), "'farm after' is confirmed")
farm("start")
contents(BLOB)
sent = {}
prompt()
check(sent[1] == "kill abhorrence", "(a fight starts)")
state["enemy.name"] = "abhorrence"
fire_trigger("dealt the killing blow", "You", "You")
state["enemy.name"] = nil
contents(EMPTY)
sent = {}
fire_timers()                              -- the breath
check(sent[1] == "get all from corpse", "the after-kill command goes out when the breath ends")
local n_after = 0
for _, c in ipairs(sent) do if c == "get all from corpse" then n_after = n_after + 1 end end
check(n_after == 1, "...exactly once")
farm("stop")
fresh_client(true) ; load_plugin()
clear() ; farm("after")
check(said("1. get all from corpse"), "the after-kill command survives a reload")
farm("after -")
check(said("after-kill commands cleared"), "'farm after -' clears it")
explore() ; farm("start")
contents(BLOB) ; prompt()
state["enemy.name"] = "abhorrence"
fire_trigger("dealt the killing blow", "You", "You")
state["enemy.name"] = nil ; contents(EMPTY)
sent = {}
fire_timers()
check(#sent == 0, "...and nothing is sent after a kill once cleared")
farm("stop")

-- 42. THE HUD (F6): target, roster with verdicts, kills per hour, what led the last step,
--     and a panel rebuilt in the fight's colour while hunting.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("exclude blob")
contents('{ "full": 1, "items": [ { "name": "Blob-like abhorrence", "type": "monster", "count": 3 }, { "name": "A mangy cur", "type": "monster" }, { "name": "A statue of nothing", "type": "monster" } ] }')
prompt()
local roster = state["plugin.3s-farmer.roster"] or ""
check(roster:find("Blob-like abhorrence\t3\texcluded", 1, true) ~= nil, "HUD roster: an excluded mob says so, with its count")
check(roster:find("A mangy cur\t1\tfighting", 1, true) ~= nil, "HUD roster: the mob under attack says 'fighting'")
check(roster:find("A statue of nothing\t1\tnext", 1, true) ~= nil, "HUD roster: the one after it says 'next'")
check(state["plugin.3s-farmer.target"] == "A mangy cur", "HUD target names the mob being fought")
check(panel.tabs[1].widgets[1].color == "error", "hunting: the status line is fight-coloured")
for _ = 1, 10 do tick() end                  -- the hunt watchdog gives the cur up
roster = state["plugin.3s-farmer.roster"] or ""
check(roster:find("A mangy cur\t1\tgave up", 1, true) ~= nil, "HUD roster: a name the watchdog gave up on says so")
farm("stop")
check(state["plugin.3s-farmer.target"] == "-", "off: no target shown")
check(panel.tabs[1].widgets[1].color == "dim", "off: the status line is dim")

-- 42b. Kills per hour needs a minute of session first, then is kills * 3600 / secs.
fresh_client() ; load_plugin()
explore()
farm("start")
check((state["plugin.3s-farmer.counters"] or ""):find("Kills/h\t-", 1, true) ~= nil, "kills/h is '-' in the first minute")
contents(BLOB) ; prompt()
state["enemy.name"] = "abhorrence"
fire_trigger("dealt the killing blow", "You", "You")
state["enemy.name"] = nil ; contents(EMPTY) ; fire_timers()
for _ = 1, 120 do tick() end                 -- two minutes in
check((state["plugin.3s-farmer.counters"] or ""):find("Kills/h\t30", 1, true) ~= nil, "one kill in two minutes reads 30/h")

-- 42c. 'Next room' says whether the map led the last step or staleness did.
fresh_client() ; load_plugin()
explore()
room(F2) ; tick() ; room(F1) ; tick() ; room(F2) ; tick()
losmap({ "m-@-O" })
farm("start") ; fire_timers()
check((state["plugin.3s-farmer.counters"] or ""):find("(mobs in sight)", 1, true) ~= nil, "a map-led step is labelled on the HUD")
room(F1) ; tick() ; fire_timers()            -- arrived; no grid for this room: staleness leads
check((state["plugin.3s-farmer.counters"] or ""):find("(stalest)", 1, true) ~= nil, "...and a staleness-led step too")
farm("stop")

-- 16. THE PROPERTY: everything the farmer ever sends is a single bare direction.
local MOVES = {}
for k in pairs({ n=1,s=1,e=1,w=1,ne=1,nw=1,se=1,sw=1,u=1,d=1 }) do MOVES[k] = true end
do
  local bad = {}
  for _, c in ipairs(every_sent) do
    if not MOVES[c] and not c:match("^kill %l+$") and c ~= "flee" and c ~= "get all from corpse" then
      bad[#bad + 1] = c
    end
  end
  check(#bad == 0, "nothing but bare directions, 'kill <word>', the panic command and the after-kill command was ever sent ("
        .. table.concat(bad, ", ") .. ")")
  check(#every_sent > 0, "...and the suite did exercise sending")
end

-- =====================================================================
-- ROUND ONE (20 Sep 2026): the leg the map lights, the kill log, the rest room and the
-- retreat to it, the SP floor and the resume bar.

-- 50. THE ROUTE. Every step publishes the leg it is on - the rooms to the room it is
--     heading for - and the panel names that room; the patrol stopping clears it.
fresh_client() ; load_plugin()
explore()                          -- standing on 3; room 1 is the stalest, two rooms west
farm("start")
sent = {} ; fire_timers()          -- the first step: toward 1, via 2
check(sent[1] == "w" and state["plugin.3s-farmer.route"] == "2,1",
      "a step publishes the leg to the room it is heading for (" .. tostring(state["plugin.3s-farmer.route"]) .. ")")
check((state["plugin.3s-farmer.counters"] or ""):find("Next room\tBarn (stalest)", 1, true) ~= nil,
      "...and the panel names the room the leg ends in")
cmd("w") ; room(F2) ; fire_timers()   -- arrived in 2, the next step
check(state["plugin.3s-farmer.route"] == "1", "the leg shortens as it is walked")
farm("stop")
check(state["plugin.3s-farmer.route"] == "", "stopping clears the leg")

-- 51. THE KILL LOG. A killing blow is logged under the roster name swung at, the fight
--     timed from the 'kill' that opened it; the table sorts most-killed first; it
--     persists; 'farm log' prints it and 'farm log clear' empties it.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(CUR)
prompt()                           -- 'kill cur'
tick() ; tick() ; tick()           -- three seconds of fighting
fire_trigger("dealt the killing blow", "You", "a small cur")
check((state["plugin.3s-farmer.killlog"] or ""):find("Small cur\t1\t3s\t", 1, true) ~= nil,
      "the log row: name, kills, average fight length (" .. tostring(state["plugin.3s-farmer.killlog"]) .. ")")
contents(CUR)                      -- the server refreshed: another cur is here
fire_timers()                      -- the breath, then 'kill cur' again
prompt()
tick()
fire_trigger("dealt the killing blow", "You", "a small cur")
check((state["plugin.3s-farmer.killlog"] or ""):find("Small cur\t2\t2s\t", 1, true) ~= nil,
      "a second kill averages the fight length")
clear() ; farm("log")
check(said("Small cur") and said("2 kill(s)") and said("avg fight 2s"), "'farm log' prints the log")
farm("stop")
-- it persists: a reload shows the same kills
fire_timers()
for _, fn in pairs(repeats) do end
farm("log")                        -- (save runs on the 30 s tick; force one by stopping)
disconnect() ; local saved = store["killlog"]
check(type(saved) == "string" and saved:find("Small cur", 1, true) ~= nil, "the log is written to the store")
fresh_client(true) ; load_plugin() ; explore()
check((state["plugin.3s-farmer.killlog"] or ""):find("Small cur\t2\t", 1, true) ~= nil, "...and comes back on reload")
farm("log clear")
check((state["plugin.3s-farmer.killlog"] or ""):find("no kills logged", 1, true) ~= nil, "'farm log clear' empties it")
check(panel ~= nil and (function()
  for _, w in ipairs(panel.tabs and panel.tabs[1].widgets or panel.widgets or {}) do
    if w.bind == "plugin.3s-farmer.killlog" and w.type == "table" then return true end
  end
  return false
end)(), "the panel carries the kill log table")

-- 52. THE REST ROOM. 'farm room <n> rest' marks it (published to the map like the other
--     rules); the patrol never fights there and never picks it as a destination; at a
--     floor it WALKS there first, fighting nothing on the way, and rests where you chose;
--     the rest ends at the resume bar, not the floor.
fresh_client() ; load_plugin()
explore()
clear() ; farm("room 1 rest")
check(said("rests here"), "'farm room <n> rest' says what it does")
check((state["plugin.3s-farmer.roomrules"] or ""):find("1:rest", 1, true) ~= nil, "the rest room is published to the map")
check((state["plugin.3s-farmer.rules"] or ""):find("rest room", 1, true) ~= nil, "...and the rules table names it")
farm("start")
farm("hp 50 0 80")
state["char.vitals.hp"] = "400"            -- 40%: under the start floor
sent = {} ; clear()
fire_timers()                              -- the pace timer finds us unfit
check(said("going to rest at Barn (1)"), "under the floor with a rest room in the fence, the patrol goes there")
check(#sent == 1 and sent[1] == "w", "...one step at a time, the first toward it")
check(state["plugin.3s-farmer.route"] == "2,1", "...the leg lit for the map")
check((state["plugin.3s-farmer.counters"] or ""):find("Waiting\tretreating to rest room 1", 1, true) ~= nil,
      "...and the panel says so")
cmd("w") ; room(F2)
contents(CUR)                              -- a mob in the room on the way
sent = {} ; prompt()
check(#sent == 0, "on the way to the rest room nothing is attacked")
fire_timers()                              -- the pace timer: the next retreat step
check(#sent == 1 and sent[1] == "w", "...and the next step toward the rest room goes out")
clear() ; cmd("w") ; room(F1)
check(said("at the rest room - resting"), "arriving at the rest room, the rest begins")
check((state["plugin.3s-farmer.counters"] or ""):find("Waiting\tresting", 1, true) ~= nil, "...and the panel says resting")
state["char.vitals.hp"] = "600"            -- 60%: over the floor, under the resume bar
sent = {} ; clear()
fire_timers()                              -- the rest timer
check(said("HP low (60% < 80%) still"), "the rest waits for the RESUME bar, not the floor")
check(#sent == 0, "...nothing sent meanwhile")
state["char.vitals.hp"] = "850"
fire_timers()
check(said("recovered - patrol resuming"), "at the resume bar the patrol goes on")
sent = {} ; fire_timers()
check(#sent == 1 and sent[1] == "e", "...stepping out of the rest room, which is never a destination")
-- the rest room is transit: standing in it, its mobs are left alone
cmd("e") ; room(F2) ; cmd("w") ; room(F1) ; contents(CUR) ; sent = {} ; prompt()
check(#sent == 0, "a mob in the rest room is never fought")
farm("stop")

-- 52a. The rest room is never a destination, even as the stalest room: standing on 2
--      with 1 (rest, stalest) west and 3 east, the patrol goes east.
fresh_client() ; load_plugin()
explore()
cmd("w") ; room(F2) ; tick()               -- on 2: 1 is the stalest, 3 fresher
farm("room 1 rest")
farm("start")
sent = {} ; fire_timers()
check(#sent == 1 and sent[1] == "e", "the stalest room being the rest room, the patrol goes the other way")
farm("stop")

-- 52a'. Fit again on the way to the rest room: the patrol resumes there and then.
fresh_client() ; load_plugin()
explore()
farm("room 1 rest")
farm("start")
farm("hp 50")
state["char.vitals.hp"] = "400"
sent = {} ; fire_timers()                  -- unfit: the retreat's first step
cmd("w") ; room(F2)
state["char.vitals.hp"] = "900"
clear() ; sent = {}
fire_timers()                              -- the rest timer finds us fit on the way
check(said("recovered - patrol resuming") and not said("on the way"), "fit again mid-retreat, the patrol resumes where it is")
farm("stop")

-- 52b. No rest room: rest in place, as before.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("hp 50")
state["char.vitals.hp"] = "400"
sent = {} ; clear()
fire_timers()
check(said("resting 30s before going on") and #sent == 0, "with no rest room the patrol rests where it stands")
farm("stop")

-- 52c. THE PANIC FLOOR with a rest room: the fight is abandoned, the panic command sent,
--      and the patrol RETREATS instead of stopping.
fresh_client() ; load_plugin()
explore()
farm("room 1 rest")
farm("start")
farm("hp 50 30")
farm("panic flee")
contents(CUR) ; prompt()                   -- 'kill cur'
state["char.vitals.hp"] = "200"            -- 20%: under the panic floor
sent = {} ; clear()
tick()                                     -- the 1 s tick is the panic watch
check(sent[1] == "flee", "at the panic floor the panic command goes first")
check(said("going to rest at Barn (1)") and not said("patrol stopped"), "...then the patrol retreats to the rest room instead of stopping")
check(sent[2] == "w", "...the first step of the retreat")
check(state["plugin.3s-farmer.status"]:find("PATROLLING", 1, true) ~= nil, "the patrol is still on")
tick()
check(#sent == 2, "the panic floor fires once - not every second while resting")
farm("stop")

-- 52d. ...and without one it stops, as before.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("hp 50 30")
contents(CUR) ; prompt()
state["char.vitals.hp"] = "200"
clear() ; tick()
check(said("patrol stopped"), "no rest room: the panic floor stops the patrol")

-- 53. THE SP FLOOR: like the HP floor, off char.vitals.sp/maxsp; a feed whose max is
--     under its current is not a percent and never fires it.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("sp 40")
state["char.vitals.sp"], state["char.vitals.maxsp"] = "30", "100"
contents(CUR) ; sent = {} ; clear() ; prompt()
check(#sent == 0 and said("SP low (30% < 40%)"), "sp gate: a mob is not attacked under the SP floor")
state["char.vitals.sp"], state["char.vitals.maxsp"] = "4885", "53"
fresh_client() ; load_plugin() ; explore() ; farm("start") ; farm("sp 40")
state["char.vitals.sp"], state["char.vitals.maxsp"] = "4885", "53"
contents(CUR) ; sent = {} ; prompt()
check(#sent == 1 and sent[1] == "kill cur", "sp gate: the 17 Sep feed (sp over maxsp) is no percent - the floor stays quiet")
clear() ; farm("sp")
check(said("unknown - no usable"), "...and 'farm sp' says why")
farm("stop")

-- =====================================================================
-- ROUND TWO (20 Sep 2026): the after-kill chain and the no-loot list; run limits.

-- 54. THE CHAIN. 'farm after add' appends; the commands go out in order after the
--     breath; 'del <n>' removes one; the panel lists them and its box adds one; a mob on
--     the area's no-loot list gets none of them.
fresh_client() ; load_plugin()
explore()
farm("start")
clear()
farm("after add get all from corpse")
farm("after add skin corpse")
farm("after add bury corpse")
check(said("after each kill, 3rd: 'bury corpse'"), "'farm after add' appends, numbered")
check((state["plugin.3s-farmer.afterlist"] or "") == "1. get all from corpse\n2. skin corpse\n3. bury corpse", "the panel lists the chain in order")
farm("after del 2")
check((state["plugin.3s-farmer.afterlist"] or "") == "1. get all from corpse\n2. bury corpse", "'farm after del <n>' removes one, the rest close up")
contents(CUR) ; prompt()                   -- 'kill cur'
fire_trigger("dealt the killing blow", "You", "a small cur")
contents(EMPTY) ; sent = {}
fire_timers()                              -- the breath
check(sent[1] == "get all from corpse" and sent[2] == "bury corpse", "after the breath the chain goes out in order (" .. table.concat(sent, "; ") .. ")")
-- the panel's box and row click
local after_tbl, after_box
for _, w in ipairs(pw()) do
  if w.bind == "plugin.3s-farmer.afterlist" then after_tbl = w end
  if w.bind == "plugin.3s-farmer.afterbox" then after_box = w end
end
check(after_tbl and after_box, "the panel carries the after-kill table and box")
after_box.onSubmit("sacrifice corpse")
check((state["plugin.3s-farmer.afterlist"] or ""):find("3. sacrifice corpse", 1, true) ~= nil, "the box appends a command")
after_tbl.onRowClick(nil, 1)
check((state["plugin.3s-farmer.afterlist"] or "") == "1. bury corpse\n2. sacrifice corpse", "a row click removes that command")
-- no-loot: the chain is skipped for that mob, in this area only
clear() ; farm("noloot cur")
check(said("no loot in Farmland: cur"), "'farm noloot <name>' lists the mob for this area")
check((state["plugin.3s-farmer.nolootlist"] or "") == "cur", "...and the panel shows it")
contents(CUR) ; prompt()
fire_trigger("dealt the killing blow", "You", "a small cur")
contents(EMPTY) ; sent = {}
fire_timers()
local looted = false
for _, c in ipairs(sent) do if c:find("corpse", 1, true) then looted = true end end
check(not looted, "a no-loot mob gets none of the chain")
contents(BLOB) ; prompt()
fire_trigger("dealt the killing blow", "You", "a blob-like abhorrence")
contents(EMPTY) ; sent = {}
fire_timers()
check(sent[1] == "bury corpse", "...while any other mob still does")
clear() ; farm("noloot -cur")
check(said("gets the after-kill commands again"), "'farm noloot -<name>' allows it again")
-- it persists with the graph
farm("noloot cur")
disconnect()
fresh_client(true) ; load_plugin()
room(F3) ; farm("start")
check((state["plugin.3s-farmer.nolootlist"] or "") == "cur", "the no-loot list survives a reload")
check((state["plugin.3s-farmer.afterlist"] or "") == "1. bury corpse\n2. sacrifice corpse", "...and so does the chain")
farm("stop")

-- 55. RUN LIMITS. 'farm limit 50k' / '45m': between fights, the limit reached parks the
--     patrol in the rest room (stopping on arrival, a notify) or stops it on the spot.
fresh_client() ; load_plugin()
explore()
farm("room 1 rest")
farm("start")
clear() ; farm("limit 2k")
check(said("run limit: 2 kill(s)"), "'farm limit <N>k' sets a kill limit")
check((state["plugin.3s-farmer.counters"] or ""):find("Limit\t2 kill(s) left", 1, true) ~= nil, "...shown on the panel")
contents(CUR) ; prompt()
fire_trigger("dealt the killing blow", "You", "a small cur")
contents(CUR) ; fire_timers()              -- a fresh cur; breath, 'kill cur' again
tick()
check(state["plugin.3s-farmer.status"]:find("PATROLLING", 1, true) ~= nil, "one kill: the patrol goes on")
prompt()
fire_trigger("dealt the killing blow", "You", "a small cur")
contents(EMPTY) ; fire_timers()            -- the breath: roster empty, hunt ends
sent = {} ; clear()
tick()                                     -- the tick sees the limit reached, no fight on
check(said("run limit: 2 kill(s) reached - parking at the rest room"), "the second kill reaches the limit: parking")
check(sent[1] == "w", "...the first step toward the rest room")
check((state["plugin.3s-farmer.counters"] or ""):find("parking at rest room 1", 1, true) ~= nil, "...and the panel says parking")
cmd("w") ; room(F2)
contents(CUR) ; sent = {} ; prompt()
check(#sent == 0, "on the way nothing is fought")
fire_timers() ; check(sent[1] == "w", "...the next step")
clear() ; cmd("w") ; room(F1)
check(said("parked at the rest room - patrol stopped"), "at the rest room the patrol stops")
check(#notified > 0 and notified[#notified]:find("parked at the rest room", 1, true) ~= nil, "...and the phone hears")
check(state["plugin.3s-farmer.status"]:find("off", 1, true) ~= nil, "...off")
check((state["plugin.3s-farmer.counters"] or ""):find("Limit\t-", 1, true) ~= nil, "a limit is for one run")

-- 55b. A time limit, no rest room: stops on the spot; and never mid-fight.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("limit 1m")
contents(CUR) ; prompt()                   -- fighting
state["enemy.name"] = "cur"
for _ = 1, 65 do tick() end                -- a minute passes, mid-fight
state["enemy.name"] = nil
check(state["plugin.3s-farmer.status"]:find("PATROLLING", 1, true) ~= nil, "a time limit does not stop a fight in progress")
clear()
fire_trigger("dealt the killing blow", "You", "a small cur")
contents(EMPTY) ; fire_timers()            -- the hunt ends
tick()
check(said("run limit: 1 minute(s) up") and said("patrol stopped"), "...the fight over, it stops")
check(notified[#notified]:find("1 minute(s) up", 1, true) ~= nil, "...and notifies")
clear() ; farm("limit")
check(said("(now: none)"), "the limit is spent")

-- 55c. A rest timer still pending when the limit parks the patrol must not resume it.
fresh_client() ; load_plugin()
explore()
farm("start")
farm("hp 50")
state["char.vitals.hp"] = "400"
fire_timers()                              -- unfit, no rest room: resting in place (timer armed)
farm("room 1 rest")                        -- now there is one
farm("limit 1m")
for _ = 1, 61 do tick() end                -- the limit: parking at the rest room
check((state["plugin.3s-farmer.counters"] or ""):find("parking at rest room 1", 1, true) ~= nil, "resting in place, the limit parks the patrol")
state["char.vitals.hp"] = "900"
clear() ; fire_timers()                    -- the old rest timer fires, fit again
check(not said("recovered - patrol resuming"), "...and the stale rest timer does not resume it")
check((state["plugin.3s-farmer.counters"] or ""):find("parking at rest room 1", 1, true) ~= nil, "...still parking")
farm("limit 3h") ; check(said("3 hour(s)"), "'farm limit <N>h'")
farm("limit -") ; check(said("run limit cleared"), "'farm limit -' clears")

-- =====================================================================
-- ROUND THREE (20 Sep 2026): fight order and the always-list; area rotation; settings
-- boxes on the panel.

-- 56. PREFER. With a cur and an abhorrence in the room the roster's order attacks the
--     cur; 'farm prefer abhor' puts the abhorrence first; per area; persisted.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(TWO) ; sent = {} ; prompt()
check(sent[1] == "kill cur", "the roster's own order: the cur first")
farm("stop") ; farm("start")
clear() ; farm("prefer abhor")
check(said("preferred in Farmland, in order: abhor"), "'farm prefer <name>' lists the fragment for this area")
check((state["plugin.3s-farmer.preferlist"] or "") == "1. abhor", "...and the panel shows the order")
contents(TWO) ; sent = {} ; prompt()
check(sent[1] == "kill abhorrence", "the preferred mob is attacked first")
farm("prefer cur")
farm("prefer -abhor")
check((state["plugin.3s-farmer.preferlist"] or "") == "1. cur", "'-<name>' drops one, the rest close up")
farm("stop")
room(W4) ; farm("start")                    -- another area: no preference here
check((state["plugin.3s-farmer.preferlist"] or ""):find("roster's own order in Woods", 1, true) ~= nil, "preferences are per area")
farm("stop")
disconnect() ; fresh_client(true) ; load_plugin()
room(F3) ; farm("start")
check((state["plugin.3s-farmer.preferlist"] or "") == "1. cur", "the preference survives a reload")
farm("stop")

-- 57. ALWAYS. A stranger in the room parks the fists - unless the mob is on the
--     always-list; then it is fought, and the Waiting row says what is held back.
fresh_client() ; load_plugin()
explore()
farm("start")
contents(MIXED) ; sent = {} ; clear() ; prompt()
check(no_kill() and said("a player is here - leaving the room's mobs alone"), "a stranger: hands off (as before)")
check((state["plugin.3s-farmer.counters"] or ""):find("Waiting\ta player is here - leaving Small cur alone", 1, true) ~= nil,
      "...and the Waiting row names what is held back")
clear() ; farm("always cur")
check(said("attacked even with a stranger in the room: cur"), "'farm always <name>' adds a mob")
check((state["plugin.3s-farmer.alwayslist"] or "") == "cur", "...and the panel lists it")
contents(MIXED) ; sent = {} ; prompt()
check(sent[1] == "kill cur", "an always-list mob is attacked with the stranger there")
farm("stop") ; fresh_client() ; load_plugin() ; explore() ; farm("start")
farm("always cur") ; farm("always -cur")
contents(MIXED) ; sent = {} ; prompt()
check(no_kill(), "'farm always -<name>' drops it: hands off again")
farm("stop")

-- 58. ROTATION. Areas farmed in turn: the circuit done and no kill for the idle time, the
--     patrol asks the mapper for the next area; never mid-fight; off by default.
fresh_client() ; load_plugin()
explore()
farm("start")
clear()
farm("rota add Farmland") ; farm("rota add Woods") ; farm("rota idle 30")
check(said("rotation: Farmland -> Woods"), "'farm rota add' builds the list")
check((state["plugin.3s-farmer.rotalist"] or "") == "Farmland\nWoods", "...shown on the panel")
-- walk the circuit: 1 and 2 are stale; after both, the stalest is one already visited
sent = {} ; fire_timers() ; cmd(sent[#sent]) ; room(F2) ; fire_timers() ; cmd(sent[#sent]) ; room(F1)
fire_timers() ; cmd(sent[#sent]) ; room(F2)          -- (the pace timer stays pending: no step in flight)
check(said("full circuit"), "(the circuit is done)")
for _ = 1, 40 do tick() end
check(#emitted == 0 or emitted[#emitted].ev ~= "map.goto", "rotation off: nothing moves on")
farm("rota on")
clear()
for _ = 1, 40 do tick() end
check(said("rotation: no kill in Farmland for 30s since the circuit - moving on to Woods"), "rotation on: the idle time up, it moves on to the next area")
check(emitted[#emitted] and emitted[#emitted].ev == "map.goto" and emitted[#emitted].data:find("Woods", 1, true) ~= nil,
      "...by asking the mapper for a walk there")
check(state["plugin.3s-farmer.status"]:find("traveling", 1, true) ~= nil or not state["plugin.3s-farmer.status"]:find("PATROLLING", 1, true),
      "...the patrol handed over to the trip")
-- the list wraps: from the last area back to the first
emit("map.walk.started", '{ "target": 4, "steps": 1 }', "3s-map-gmcp")
cmd("e") ; room(W4)
emit("map.walk.arrived", '{ "num": 4 }', "3s-map-gmcp")
check(said("patrolling Woods"), "arrived: the patrol starts in the next area")
-- a one-room area is its own circuit: the stalest room is this one. The list wraps.
fire_timers()                              -- the pace timer: nowhere to go, the circuit is done
clear()
for _ = 1, 40 do tick() end
check(said("moving on to Farmland"), "from the last area the rotation wraps to the first")
farm("stop")

-- 58b. A kill resets the idle clock, and a fight in progress never rotates.
fresh_client() ; load_plugin()
explore()
farm("start") ; farm("rota add Farmland") ; farm("rota add Woods") ; farm("rota idle 30") ; farm("rota on")
sent = {} ; fire_timers() ; cmd(sent[#sent]) ; room(F2) ; fire_timers() ; cmd(sent[#sent]) ; room(F1)
fire_timers() ; cmd(sent[#sent]) ; room(F2)          -- (the pace timer stays pending: no step in flight)
for _ = 1, 25 do tick() end
contents(CUR) ; prompt()                   -- a fight at second 25
state["enemy.name"] = "cur"
for _ = 1, 10 do tick() end                -- second 35, mid-fight
check(not said("moving on"), "mid-fight the rotation waits")
state["enemy.name"] = nil
fire_trigger("dealt the killing blow", "You", "a small cur")
contents(EMPTY) ; fire_timers()
for _ = 1, 20 do tick() end                -- 20 s after the kill
check(not said("moving on"), "a kill reset the idle clock")
for _ = 1, 15 do tick() end
check(said("moving on to Woods"), "...which runs out again 30 s after it")
farm("stop")

-- 59. SETTINGS BOXES. Each is the typed command with its arguments.
fresh_client() ; load_plugin()
local boxes = {}
for _, w in ipairs(pw()) do
  if w.type == "input" and w.bind and w.bind:find("plugin.3s-farmer.set_", 1, true) then boxes[w.bind:match("set_(%w+)")] = w end
end
check(boxes.pace and boxes.hp and boxes.sp and boxes.rest and boxes.limit and boxes.panic, "six settings boxes on the panel")
clear() ; boxes.hp.onSubmit("60 30 90")
check(said("HP floors: start below 60%, panic below 30%, rest until 90%"), "the hp box is 'farm hp'")
clear() ; boxes.pace.onSubmit("3")
check(said("pace"), "the pace box is 'farm pace'")
clear() ; boxes.limit.onSubmit("45m")
check(said("run limit: 45 minute(s)"), "the limit box is 'farm limit'")
clear() ; boxes.panic.onSubmit("flee")
check(said("at the panic floor: 'flee'"), "the panic box is 'farm panic'")

-- 60. KILLS OFF PATROL (22 Sep). Following someone else's bot, your killing blows still
--     go in the log and the tally; the patrol's own counter and the fight timing do not.
fresh_client() ; load_plugin()
explore()                                  -- off: nobody patrolling
clear()
fire_trigger("dealt the killing blow", "You", "a small cur")
tick() ; tick()
fire_trigger("dealt the killing blow", "You", "the Blob-like abhorrence")
fire_trigger("dealt the killing blow", "You", "a small cur")
local kl = state["plugin.3s-farmer.killlog"] or ""
check(kl:find("Small cur\t2\t-\t", 1, true) ~= nil, "off patrol: a kill is logged under the blow's name, no fight time (" .. kl:gsub("\n", "|") .. ")")
check(kl:find("Blob-like abhorrence\t1\t-\t", 1, true) ~= nil, "...the article stripped, capitalised like a roster name")
check((state["plugin.3s-farmer.counters"] or ""):find("Kills\t0", 1, true) ~= nil, "...the patrol's own counter stays at zero")
check((state["plugin.3s-farmer.counters"] or ""):find("Tally\t3 since", 1, true) ~= nil, "...and the tally counts them")
check(#sent == 0, "...nothing is sent - the after-kill chain is the patrol's")
clear() ; farm("status")
check(said("tally: 3 kill(s) since load"), "'farm' reports the tally")
-- a party member's blow is tallied under their name, in the same log
fire_trigger("dealt the killing blow", "Bob", "a small cur")
fire_trigger("dealt the killing blow", "Bob", "a rat")
kl = state["plugin.3s-farmer.killlog"] or ""
check(kl:find("Small cur\t3\t-\t", 1, true) ~= nil and kl:find("Rat\t1\t-\t", 1, true) ~= nil, "a party member's kills go in the same log")
check((state["plugin.3s-farmer.counters"] or ""):find("Tally\t5 since", 1, true) ~= nil
      and (state["plugin.3s-farmer.counters"] or ""):find("- you 3, Bob 2", 1, true) ~= nil, "...and the tally counts per killer (" .. tostring((state["plugin.3s-farmer.counters"] or ""):match("Tally[^\n]*")) .. ")")
clear() ; farm("status")
check(said("(you 3, Bob 2)"), "'farm' names the killers")
-- a party member's blow mid-patrol-hunt is theirs, not the patrol's: the hunt goes on
farm("start")
contents(CUR) ; prompt()                   -- 'kill cur', hunting
sent = {}
fire_trigger("dealt the killing blow", "Bob", "a small cur")
fire_timers()
check((state["plugin.3s-farmer.counters"] or ""):find("Kills\t0", 1, true) ~= nil, "a party member's blow during our hunt is not the patrol's kill")
check(#sent == 0 or not sent[1]:find("corpse", 1, true), "...and does not run our after-kill chain")
farm("stop")
-- a patrol kill adds to the same log entry and times it; the average is over timed fights
farm("start")
contents(CUR) ; prompt() ; tick() ; tick() ; tick() ; tick()
fire_trigger("dealt the killing blow", "You", "a small cur")
kl = state["plugin.3s-farmer.killlog"] or ""
check(kl:find("Small cur\t5\t4s\t", 1, true) ~= nil, "a patrol kill joins the entry and the average is over TIMED fights only (" .. kl:gsub("\n", "|") .. ")")
check((state["plugin.3s-farmer.counters"] or ""):find("Kills\t1", 1, true) ~= nil and (state["plugin.3s-farmer.counters"] or ""):find("Tally\t7 since", 1, true) ~= nil,
      "...the run counter and the tally both move")
farm("stop")
disconnect() ; fresh_client(true) ; load_plugin() ; explore()
check((state["plugin.3s-farmer.killlog"] or ""):find("Small cur\t5\t4s\t", 1, true) ~= nil, "the timed count survives a reload with the log")
clear() ; farm("log clear")
check(said("kill log (every place) and tally cleared") and (state["plugin.3s-farmer.counters"] or ""):find("Tally\t0", 1, true) ~= nil, "'farm log clear' clears the tally too")

-- 61. ROOM.DEATH (23 Sep). The server reports a kill in GMCP too; with the text line it is
--     ONE kill, whichever comes first. Pairing "You dealt..." with a Room.Death learns
--     your name, so a Room.Death alone then counts as yours.
local function death(killer, name, npc)
  -- the live shape (23 Sep 15:34 capture): corpse and npc are numbers
  local j = string.format('{ "corpse": 1, "killer": "%s", "npc": %s, "name": "%s" }',
                          killer, npc == false and "0" or (npc == "bool" and "false" or "1"), name)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Death" then h.fn(j) end end
end
local function tally_row() return (state["plugin.3s-farmer.counters"] or ""):match("Tally[^\n]*") or "" end
fresh_client() ; load_plugin()
explore()
clear()
death("Goran", "Wiremouth guard")          -- GMCP first, name not known yet
check(tally_row():find("Tally\t1 since", 1, true) ~= nil and tally_row():find("Goran 1", 1, true) ~= nil,
      "a Room.Death alone is a kill, under its killer's name while yours is unknown (" .. tally_row() .. ")")
check((state["plugin.3s-farmer.killlog"] or ""):find("Wiremouth guard\t1\t-\t", 1, true) ~= nil, "...logged under Room.Death's name")
fire_trigger("dealt the killing blow", "You", "the Wiremouth guard")
check(tally_row():find("Tally\t1 since", 1, true) ~= nil, "the text line of the same kill does not count it twice (" .. tally_row() .. ")")
check(store.me == "Goran" and said("your character is Goran"), "...and learns your name, and keeps it")
clear() ; farm("status")
check(said("(you 1)") and not tally_row():find("Goran", 1, true), "...it moves the kill over to 'you'")
tick() ; tick() ; tick() ; tick()
death("Goran", "Wiremouth guard")
clear() ; farm("status")
check(tally_row():find("Tally\t2 since", 1, true) ~= nil and said("(you 2)"),
      "a later Room.Death by your name alone counts as yours (" .. tally_row() .. ")")
fire_trigger("dealt the killing blow", "You", "the Wiremouth guard")
check(tally_row():find("Tally\t2 since", 1, true) ~= nil, "...and its own text line pairs with it")
tick() ; tick() ; tick() ; tick()
fire_trigger("dealt the killing blow", "You", "the Wiremouth guard")
check(tally_row():find("Tally\t3 since", 1, true) ~= nil, "a text line with no Room.Death still counts (the fallback)")
tick() ; tick() ; tick() ; tick()
death("Goran", "Wiremouth guard")
check(tally_row():find("Tally\t4 since", 1, true) ~= nil, "...and a Room.Death after the pairing window is a new kill")
-- text first, a party member's
fire_trigger("dealt the killing blow", "Bob", "a rat")
death("Bob", "rat")
check(tally_row():find("Tally\t5 since", 1, true) ~= nil and tally_row():find("Bob 1", 1, true) ~= nil,
      "a party member's kill reported both ways is one kill (" .. tally_row() .. ")")
check(store.me == "Goran", "...and does not rename you")
-- the same mob twice in a breath: each report pairs with its own
fire_trigger("dealt the killing blow", "You", "a small cur") ; death("Goran", "small cur")
fire_trigger("dealt the killing blow", "You", "a small cur") ; death("Goran", "small cur")
check(tally_row():find("Tally\t7 since", 1, true) ~= nil, "two kills of one mob name in the same second are two kills")
tick() ; tick() ; tick() ; tick()
death("Goran", "small cur") ; fire_trigger("dealt the killing blow", "You", "a small cur")
fire_trigger("dealt the killing blow", "You", "a small cur")
check(tally_row():find("Tally\t9 since", 1, true) ~= nil, "a report pairs once: a second text line is a second kill")
death("Goran", "Bob", false)
check(tally_row():find("Tally\t9 since", 1, true) ~= nil, "a Room.Death with npc 0 is a player's death, not a kill")
death("Goran", "Bob", "bool")
check(tally_row():find("Tally\t9 since", 1, true) ~= nil, "...and so is a boolean npc false")
death("Goran", "Stray")
check(tally_row():find("Tally\t10 since", 1, true) ~= nil, "npc 1: a kill")
death("Hirdmadr", "Wiremouth guard")        -- the capture's killer: your hird
clear() ; farm("status")
check(said("(you 10, Bob 1)") and not tally_row():find("Hirdmadr", 1, true), "your hird's kill counts as yours (Joakim, 23 Sep)")
-- the name survives a reload
fresh_client(true) ; load_plugin()
explore()
death("Goran", "Wiremouth guard")
clear() ; farm("status")
check(said("(you 1)"), "your learned name survives a reload (" .. tally_row() .. ")")
-- a patrol kill reported only by Room.Death still runs the patrol's own consequences, once
farm("after get all from corpse")
tick() ; tick() ; tick() ; tick()
farm("start")
contents(CUR) ; prompt()                   -- 'kill cur', hunting
tick() ; tick()
sent = {}
death("Goran", "small cur")
check((state["plugin.3s-farmer.counters"] or ""):find("Kills\t1", 1, true) ~= nil, "a Room.Death by you mid-hunt is the patrol's kill")
fire_trigger("dealt the killing blow", "You", "a small cur")
check((state["plugin.3s-farmer.counters"] or ""):find("Kills\t1", 1, true) ~= nil, "...and its text line does not count it again")
contents(EMPTY)
fire_timers()
local loots = 0
for _, c in ipairs(sent) do if c == "get all from corpse" then loots = loots + 1 end end
check(loots == 1, "...the after-kill chain runs once (" .. loots .. ")")
check((state["plugin.3s-farmer.killlog"] or ""):find("Small cur\t1\t2s\t", 1, true) ~= nil,
      "...and the fight is timed (" .. (state["plugin.3s-farmer.killlog"] or ""):gsub("\n", "|") .. ")")
farm("stop")
-- text first, name unknown: the patrol's text kill, then Room.Death, learns the name without a second chain
fresh_client() ; load_plugin()
explore()
farm("after get all from corpse")
farm("start")
contents(CUR) ; prompt()
sent = {}
fire_trigger("dealt the killing blow", "You", "a small cur")
death("Lobo", "small cur")
contents(EMPTY) ; fire_timers()
loots = 0
for _, c in ipairs(sent) do if c == "get all from corpse" then loots = loots + 1 end end
check(store.me == "Lobo" and loots == 1 and (state["plugin.3s-farmer.counters"] or ""):find("Kills\t1", 1, true) ~= nil,
      "text first: one kill, one chain, and the name learned from the Room.Death that follows")
farm("stop")
clear() ; farm("log clear")
check(tally_row():find("Tally\t0", 1, true) ~= nil, "'farm log clear' clears it all")

-- 62. 'farm mine' (23 Sep): the killers whose kills are yours - the hird by default.
fresh_client() ; load_plugin()
explore()
store.me = nil
clear() ; farm("mine")
check(said("kills counted as yours: hirdmadr"), "'farm mine' lists the hird by default")
fire_trigger("dealt the killing blow", "Hirdmadr", "a small cur")
death("Hirdmadr", "small cur")
clear() ; farm("status")
check(said("(you 1)") and store.me == nil, "a hird kill reported both ways is one kill, yours - and names nobody as you")
tick() ; tick() ; tick() ; tick()
death("Hirdmadr", "small cur")
fire_trigger("dealt the killing blow", "Hirdmadr", "a small cur")
clear() ; farm("status")
check(said("(you 2)") and store.me == nil, "...the other way round too")
tick() ; tick() ; tick() ; tick()
farm("mine -hirdmadr")
check(store.mine == "", "'farm mine -<name>' drops it, and the empty list is kept")
fresh_client(true) ; load_plugin()
explore()
death("Hirdmadr", "small cur")
check(tally_row():find("Hirdmadr 1", 1, true) ~= nil, "...after which the hird's kills are its own again")
farm("mine Warband")
fresh_client(true) ; load_plugin()
explore()
clear() ; farm("mine")
check(said("kills counted as yours: warband") and not said("hirdmadr"), "the list survives a reload, lower-cased")
farm("mine hirdmadr")
-- mid-hunt, the hird's Room.Death is the patrol's kill: counters, chain, next target
farm("after get all from corpse")
farm("start")
contents(CUR) ; prompt()
tick() ; tick()
sent = {}
death("Hirdmadr", "small cur")
check((state["plugin.3s-farmer.counters"] or ""):find("Kills\t1", 1, true) ~= nil, "a hird kill mid-hunt is the patrol's kill")
contents(EMPTY) ; fire_timers()
local hl = 0
for _, c in ipairs(sent) do if c == "get all from corpse" then hl = hl + 1 end end
check(hl == 1, "...and loots the corpse once")
farm("stop")

-- 63. CHAR.XP (23 Sep 15:49 capture - it arrives since Core asks for it). The server's
--     rate is shown as it comes; the run's gain is xp now minus xp at the start, frozen
--     at the stop, and divided by the run's kills.
local function xp(v, ph, g30)
  local j = string.format('{ "eta_secs": 0, "modifier": 0, "to_spend": 7736091820, "to_next": 0, "maxed": 1, "per_hour": %d, "gain30": %d, "xp": %d }', ph, g30, v)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Char.XP" then h.fn(j) end end
end
local function xp_row() return (state["plugin.3s-farmer.counters"] or ""):match("XP\t[^\n]*") or "" end
fresh_client() ; load_plugin()
explore()
farm("status")
check(xp_row():find("no Char.XP", 1, true) ~= nil, "no feed yet: the XP row says so")
xp(198895068482, 0, 0)
xp(198895079035, 21106, 10553)
clear() ; farm("status")
check(said("XP: 21.1k/h - +10.6k in 30m - +10.6k since load"), "off patrol: the server's rate and the gain since load (" .. xp_row() .. ")")
farm("start")
contents(CUR) ; prompt()
xp(198895579035, 56046676, 28023338)      -- +500k on the kill
fire_trigger("dealt the killing blow", "You", "a small cur")
clear() ; farm("status")
check(said("56.0M/h - +28.0M in 30m - run +500.0k (500.0k/kill)"), "on patrol: the run's gain and per kill, big numbers shortened")
farm("stop")
xp(198896579035, 56046676, 28023338)      -- more XP after the stop
clear() ; farm("status")
check(said("run +500.0k (500.0k/kill)"), "the run's gain is frozen at the stop")
farm("start")
clear() ; farm("status")
check(said("run +0"), "a new run counts from its own start")
farm("stop")
-- a run started before the feed counts from the first message
fresh_client() ; load_plugin()
explore()
farm("start")
xp(1000, 0, 0) ; xp(4000, 0, 0)
clear() ; farm("status")
check(said("run +3000"), "a run that began before the feed counts from the first message")
farm("stop")

-- 64. PAGED ROOM.CONTENTS (23 Sep 15:49): a crowded room over several pages, items
--     counted. Page 1 starts the roster, later pages add to it.
fresh_client() ; load_plugin()
explore()
contents('{ "items": [ { "type": "item", "count": 6, "name": "The sliced up, freshly slain corpse of Wiremouth" } ], "full": 1, "entry": 1, "pages": 3, "page": 1 }')
contents('{ "items": [ { "type": "monster", "count": 2, "name": "Wiremouth guard" } ], "full": 1, "entry": 1, "pages": 3, "page": 2 }')
contents('{ "items": [ { "type": "item", "count": 1, "name": "A truss" } ], "full": 1, "entry": 1, "pages": 3, "page": 3 }')
farm("start")
sent = {}
prompt()
check(sent[1] == "kill guard", "a monster on page 2 survives page 3 (" .. tostring(sent[1]) .. ")")
farm("stop")
contents('{ "entry": 1, "full": 1, "items": [ ] }')
farm("start")
sent = {}
prompt()
check(sent[1] ~= "kill guard", "an unpaged list starts afresh")
farm("stop")

-- 65. ONE KILL LOG PER PLACE, AND ITS XP (23 Sep): Megacity's floors are one area to the
--     server, so the place is the area plus the floor the room's name gives. The Last
--     column became XP: what the last kill of that mob was worth (Char.XP over the fight).
local M30 = '{ "num": 44942, "name": "Central Plaza Open Core floor 30", "area": "Megacity", "exits": { "n": 44943 } }'
local L30 = '{ "num": 44401, "name": "Floor 30   Elevator Lobby", "area": "Megacity", "exits": { "s": 44346 } }'
local M40 = '{ "num": 45042, "name": "Central Plaza Open Core floor 40", "area": "Megacity", "exits": { "n": 45043 } }'
local GS  = '{ "num": 45664, "name": "Grinder Street", "area": "Megacity", "exits": { "e": 45665 } }'
local function kl_state() return state["plugin.3s-farmer.killlog"] or "" end
local function kl_place() return state["plugin.3s-farmer.klplace"] or "" end
fresh_client() ; load_plugin()
xp(1000, 0, 0)
room(M30)
combat('{ "target": "you", "rounds": 1, "attacker": "Wiremouth guard", "attacker_hp": 93 }')
xp(1300, 0, 0) ; xp(1500, 0, 0)
fire_trigger("dealt the killing blow", "You", "the Wiremouth guard")
xp(1600, 0, 0)                               -- the kill's own award, just after the blow
fire_timers()
check(kl_place() == "Kill log: Megacity floor 30", "the place is the area and the floor (" .. kl_place() .. ")")
check(kl_state():find("Wiremouth guard\t1\t-\t600", 1, true) ~= nil, "the XP column: the fight's whole take, the award after the blow included (" .. kl_state() .. ")")
xp(1700, 0, 0)                               -- the next guard, same fight
fire_trigger("dealt the killing blow", "You", "the Wiremouth guard")
fire_timers()
check(kl_state():find("Wiremouth guard\t2\t-\t100", 1, true) ~= nil, "...a mob killed straight after another is charged only its own XP (" .. kl_state() .. ")")
room(L30)
check(kl_state():find("Wiremouth guard\t2", 1, true) ~= nil, "the floor's elevator lobby is the same place")
room(M40)
check(kl_state():find("no kills logged here", 1, true) ~= nil and kl_place():find("floor 40 (1 other place", 1, true) ~= nil,
      "another floor is another log, and the line says one other place has one (" .. kl_place() .. ")")
combat('{ "target": "", "rounds": 0, "attacker": "", "attacker_hp": 0 }')
combat('{ "target": "you", "rounds": 1, "attacker": "Rat", "attacker_hp": 9 }')
xp(1750, 0, 0)
fire_trigger("dealt the killing blow", "Bob", "a rat")
fire_timers()
check(kl_state():find("Rat\t1\t-\t50", 1, true) ~= nil and not kl_state():find("Wiremouth", 1, true), "floor 40 logs its own kills")
room(GS)
check(kl_place() == "Kill log: Megacity (2 other places - 'farm log all')", "a room with no floor in its name is the area itself")
clear() ; farm("log all")
check(said("kill log, Megacity floor 30 (1 mob(s))") and said("kill log, Megacity floor 40 (1 mob(s))"), "'farm log all' prints every place")
check(said("last 100 XP"), "...with the XP of the last kill")
clear() ; farm("log floor 30")
check(said("Megacity floor 30") and not said("floor 40"), "'farm log <text>' prints the places matching it")
room(M40)
clear() ; farm("log clear here")
check(said("kill log for Megacity floor 40 cleared") and kl_state():find("no kills logged here", 1, true) ~= nil, "'farm log clear here' clears this place only")
clear() ; farm("log all")
check(said("floor 30") and not said("floor 40"), "...the others stay")
-- saved per place, and a log from before places reads back as "earlier"
disconnect()
fresh_client(true) ; load_plugin() ; room(M30)
check(kl_state():find("Wiremouth guard\t2\t-\t100", 1, true) ~= nil, "the place and the XP survive a reload")
fresh_client() ; store["killlog"] = '[{"name":"Rat","kills":3,"secs":12,"timed":3}]' ; load_plugin()
clear() ; farm("log all")
check(said("kill log, earlier (1 mob(s))") and said("Rat"), "a pre-1.5 log reads back under 'earlier'")
-- no XP feed, or no fight seen: the column says so
fresh_client() ; load_plugin() ; room(M30)
fire_trigger("dealt the killing blow", "You", "a rat")
fire_timers()
check(kl_state():find("Rat\t1\t-\t-", 1, true) ~= nil, "no Char.XP: the XP column is '-'")

-- 66. THIS SESSION OR ALL TIME (1.6.0, 1 Oct): the all-time log persists as before; a
--     session log starts empty each time the plugin loads, and the panel shows either.
local function kl_view() return state["plugin.3s-farmer.klview"] or "" end
local function kl_button(label)
  for _, w in ipairs(panel and panel.tabs and panel.tabs[1].widgets or {}) do
    if w.type == "buttonrow" then
      for _, b in ipairs(w.buttons) do if b.text == label then return b.action end end
    end
  end
end
fresh_client() ; load_plugin() ; room(M30)
fire_trigger("dealt the killing blow", "You", "a rat")
fire_timers()
check(kl_view() == "Showing: all time" and kl_state():find("Rat\t1\t", 1, true) ~= nil, "all time is the default view")
kl_button("This session")()
check(kl_view() == "Showing: this session" and kl_state():find("Rat\t1\t", 1, true) ~= nil
      and kl_place():find("Kill log, this session: Megacity floor 30", 1, true) ~= nil, "the session view has this session's kill")
disconnect()
fresh_client(true) ; load_plugin() ; room(M30)
check(kl_view() == "Showing: this session", "the chosen view is remembered")
check(kl_state():find("no kills here this session", 1, true) ~= nil, "a new session starts its log empty")
fire_trigger("dealt the killing blow", "You", "a rat")
fire_timers()
check(kl_state():find("Rat\t1\t", 1, true) ~= nil, "...and counts from there")
kl_button("All time")()
check(kl_view() == "Showing: all time" and kl_state():find("Rat\t2\t", 1, true) ~= nil, "all time has both sessions")
clear() ; farm("log session")
check(said("kill log this session, Megacity floor 30 (1 mob(s))") and said("1 kill(s)"), "'farm log session' prints this session's")
clear() ; farm("log")
check(said("kill log, Megacity floor 30 (1 mob(s))") and said("2 kill(s)"), "'farm log' still prints all time")
farm("log clear session")
check(said("this session's kill log and tally cleared") and kl_state():find("Rat\t2\t", 1, true) ~= nil, "'farm log clear session' keeps all time")
farm("log view session")
check(kl_state():find("no kills here this session", 1, true) ~= nil, "'farm log view session' switches the panel")
-- ...and its XP column
fresh_client() ; load_plugin()
xp(1000, 0, 0)
room(M30)
farm("log view session")
combat('{ "target": "you", "rounds": 1, "attacker": "Wiremouth guard", "attacker_hp": 93 }')
xp(1500, 0, 0)
fire_trigger("dealt the killing blow", "You", "the Wiremouth guard")
xp(1600, 0, 0)
fire_timers()
check(kl_state():find("Wiremouth guard\t1\t-\t600", 1, true) ~= nil, "the session log has the XP too (" .. kl_state() .. ")")
farm("log clear here")
check(kl_state():find("no kills here this session", 1, true) ~= nil, "'farm log clear here' clears the place in both logs")
-- a fight the patrol timed is timed in the session log too
fresh_client() ; load_plugin()
explore()
farm("log view session")
farm("start")
contents(CUR)
prompt()                           -- 'kill cur'
tick() ; tick()                    -- two seconds of fighting
fire_trigger("dealt the killing blow", "You", "a small cur")
check(kl_state():find("Small cur\t1\t2s\t", 1, true) ~= nil,
      "the session log times the fight too (" .. kl_state():gsub("\n", "|") .. ")")
farm("stop")

-- 6 Oct: after a kill the patrol swung at three tag-variant guards and the mob it had
-- just killed, one "There is no X here" each, because the refreshed Room.Contents had not
-- come yet. The dead leave the roster at once; one refusal spends a word for every name.
local WIRE = '{ "full": 1, "items": [ { "name": "Wiremouth {angry}", "type": "monster" }, '
  .. '{ "name": "A guard {angry}", "type": "monster" }, { "name": "A guard [scratched]", "type": "monster" }, '
  .. '{ "name": "A guard", "type": "monster" } ] }'
fresh_client() ; load_plugin()
explore()
farm("start")
contents(WIRE)
prompt()
check(sent[#sent] == "kill wiremouth", "(setup) the fight opens on Wiremouth (" .. tostring(sent[#sent]) .. ")")
sent = {}
fire_trigger("dealt the killing blow", "You", "Wiremouth")
fire_timers()
local wire_again = false
for _, c in ipairs(sent) do if c == "kill wiremouth" then wire_again = true end end
check(not wire_again, "a mob just killed is not swung at again before the refresh (" .. table.concat(sent, ",") .. ")")
check(sent[#sent] == "kill guard", "...the hunt goes on to what is left")
sent = {}
fire_trigger("There is no", "guard")
local guards = 0
for _, c in ipairs(sent) do if c == "kill guard" then guards = guards + 1 end end
check(guards == 0, "one 'There is no guard here' spends 'guard' for every guard on the roster (" .. table.concat(sent, ",") .. ")")

-- ...and a death another player lands (Room.Death, no text line) does the same
fresh_client() ; load_plugin()
explore()
contents('{ "full": 1, "items": [ { "name": "Small cur", "type": "monster", "count": 2 } ] }')
local death_fn
for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Death" then death_fn = h.fn end end
death_fn('{ "killer": "Bob", "name": "a small cur", "npc": 1 }')
check((state["plugin.3s-farmer.roster"] or ""):find("Small cur\t1", 1, true) ~= nil,
      "a Room.Death takes one off the count (" .. tostring(state["plugin.3s-farmer.roster"]) .. ")")
death_fn('{ "killer": "Bob", "name": "a small cur", "npc": 1 }')
check((state["plugin.3s-farmer.roster"] or ""):find("Small cur", 1, true) == nil,
      "...and the last one off the roster")

-- 1.7.0 READ THE AREA FIRST (Joakim, 7 Oct 2026: "you cant add excluded mobs to the list
--     before you start to patrol"). Off patrol the lock follows the named area you stand
--     in, so excludes land there; 'farm area' / Read area reads it in without starting.
fresh_client() ; load_plugin()
do
  local function button(label)
    for _, w in ipairs(pw()) do
      if w.type == "buttonrow" then
        for _, b in ipairs(w.buttons) do if b.text == label then return b.action end end
      end
    end
  end
  check(button("Read area") ~= nil, "the panel has a Read area button")
  clear() ; farm("exclude small cur")
  check(said("not standing in a named area"), "before any room, an exclude says why it cannot")
  room(F1) ; tick()
  clear() ; farm("exclude small cur")
  check(said("excluded in Farmland: small cur"), "standing in Farmland, never started: the exclude lands in Farmland")
  check(state["plugin.3s-farmer.status"]:find("ready", 1, true) ~= nil, "the status line says ready, off patrol")
  sent = {} ; emitted = {}
  clear() ; button("Read area")()
  check(said("Farmland read in") and said("Not patrolling yet"), "Read area reads the area in and says it is not patrolling")
  check(said("already excluded here: small cur"), "...and names what is already excluded")
  check(emitted_ev("map.query.area") ~= nil, "...asking the mapper for the area's rooms")
  check(#sent == 0, "...and sends nothing to the MUD")
  check(state["plugin.3s-farmer.status"]:find("PATROLLING", 1, true) == nil, "...the patrol is not running")
  contents(CUR) ; sent = {} ; prompt()
  check(#sent == 0, "a mob in the room is not attacked before Start")
  room(F2) ; tick() ; room(F3) ; tick()
  clear() ; button("Start")()
  check(said("patrolling Farmland"), "Start starts the patrol in the area read in")
  contents(CUR) ; sent = {} ; prompt()
  local swung = false
  for _, c in ipairs(sent) do if c:find("^kill") then swung = true end end
  check(not swung, "the exclude set before Start is honoured on patrol")
  farm("stop")
  -- after a stop, walking into another area moves the lock with you
  room(W4) ; tick()
  clear() ; farm("exclude thorn")
  check(said("excluded in Woods: thorn"), "stopped and walked into the Woods: the exclude lands in the Woods, not Farmland")
  clear() ; farm("excludes")
  check(said("excluded in Woods: thorn") and not said("small cur"), "'farm excludes' lists the Woods' own")
  room(U0) ; tick()
  clear() ; farm("exclude thing")
  check(said("not standing in a named area"), "in 'Unknown' there is no area to exclude in")
  clear() ; farm("area")
  check(said("is not one place"), "'farm area' in 'Unknown' refuses")
  clear() ; room(F3) ; tick() ; farm("start") ; clear() ; farm("area")
  check(said("already - 'farm stop' first"), "'farm area' while patrolling refuses")
  farm("stop")
end

-- 1.8.0 PACE 0 = FAST (Joakim, 7 Oct 2026: "speed up the farmer ... when its walking
--     between mobs"). Host timers tick once a second; at pace 0 the step that is due goes
--     out on the room's prompt instead, and the timer stays as the fallback.
fresh_client() ; load_plugin()
explore()
farm("start")
fire_timers()                              -- the first step goes out on the timer
local first = sent[#sent]
check(first == "w" or first == "e" or first == "n", "(setup) a first step (" .. tostring(first) .. ")")
sent = {}
cmd(first) ; room(F2)                      -- arrival: the next step is due
contents(EMPTY)
check(#sent == 0, "pace 0: nothing goes out on the arrival itself")
prompt()
check(#sent == 1 and not sent[1]:find("^kill"), "pace 0: the room's prompt sends the next step at once (" .. table.concat(sent, ",") .. ")")
local n1 = #sent
prompt()
check(#sent == n1, "...and a second prompt before the arrival sends nothing more")
-- a mob in the room: the prompt fights, it does not step
cmd(sent[#sent]) ; room(sent[#sent] == "w" and F1 or F3)
contents(CUR) ; sent = {} ; clear() ; prompt()
check(#sent == 1 and sent[1] == "kill cur", "pace 0: a mob on the prompt is fought, not walked past (" .. table.concat(sent, ",") .. ")")
sent = {} ; prompt()
check(no_kill() and #sent == 0, "...and while hunting a prompt steps nowhere")
check(not said("holding"), "...nor wakes the patrol to say it is holding")
farm("stop")
-- in combat nothing a prompt does moves us: the hold is not a step
fresh_client() ; load_plugin()
explore()
farm("start")
state["enemy.name"] = "cur"
fire_timers()                              -- the tick finds a fight: hold
sent = {} ; prompt() ; prompt()
check(#sent == 0, "pace 0: a held patrol (in combat) does not step on prompts")
do
  local shortest = math.huge
  for id in pairs(timers) do shortest = math.min(shortest, timer_secs[id] or 0) end
  check(shortest >= 5, "pace 0: a hold still looks again in seconds, not every tick (" .. tostring(shortest) .. ")")
end
state["enemy.name"] = nil
farm("stop")
-- pace 1 keeps the old timing: the prompt does not step, the timer does
fresh_client() ; load_plugin()
explore()
clear() ; farm("pace 1")
check(said("one step every 1s"), "'farm pace 1' sets the old timing")
farm("start")
fire_timers()
local d = sent[#sent]
cmd(d) ; room(F2) ; contents(EMPTY)
sent = {} ; prompt()
check(#sent == 0, "pace 1: the prompt does not step")
fire_timers()
check(#sent == 1, "pace 1: the timer does")
clear() ; farm("pace fast")
check(said("pace: fast"), "'farm pace fast' is pace 0")
clear() ; farm("pace -1")
check(said("0 = fast"), "a negative pace is refused, naming 0 = fast")
farm("stop")

-- ...a stored 1 (the old default and floor) moves to fast once; a pace set since sticks
fresh_client() ; store["pace"] = "1" ; load_plugin()
clear() ; farm("help")
check(said("0 = fast (now 0)"), "a stored pace 1 from before 1.8.0 becomes fast")
fresh_client(true) ; load_plugin()
clear() ; farm("help")
check(said("0 = fast (now 0)"), "...and is still fast after the next load")
fresh_client() ; store["pace"] = "3" ; load_plugin()
clear() ; farm("help")
check(said("(now 3)"), "...any other stored pace is kept")
fresh_client() ; load_plugin() ; farm("pace 1")
fresh_client(true) ; load_plugin()
clear() ; farm("help")
check(said("(now 1)"), "...and a pace 1 chosen after the move stays 1")

-- 1.8.0 DUD ROOMS (Joakim, 7 Oct 2026: stuck in a corner of The Corpse Dump with mobs
--     elsewhere). The grid's 'm' is any monster; a room whose roster held only things we
--     do not fight stops pulling the patrol until it is walked again in its turn.
local GOLEM = '{ "full": 1, "items": [ { "name": "A stone golem", "type": "monster" } ] }'
local function dud_run(wait, rosters)
  fresh_client() ; load_plugin()
  explore()                                -- ends on 3
  farm("never golem") ; farm("pace 1")
  room(F2) ; tick() ; room(F1) ; tick()    -- stand in the Barn (1)
  farm("start")
  for _, r in ipairs(rosters or { GOLEM }) do contents(r) ; prompt() end   -- only a never-listed golem: a dud
  sent = {} ; fire_timers()
  local d1 = first_move()
  cmd(d1 or "e") ; room(F2) ; tick() ; contents(EMPTY) ; prompt()
  sent = {} ; fire_timers()
  local d2 = first_move()
  cmd(d2 or "e") ; room(F3) ; tick() ; prompt()
  for _ = 1, wait do tick() end
  losmap({ "m-O-@" })                      -- the golem, two rooms west, in the Barn
  sent = {} ; fire_timers()
  local why = (state["plugin.3s-farmer.counters"] or "")
  farm("stop")
  return d1, d2, why
end
do
  local d1, d2, why = dud_run(0)
  check(d1 == "e" and d2 == "e", "(setup) the patrol walks east to the Fence (" .. tostring(d1) .. "," .. tostring(d2) .. ")")
  check(why:find("(stalest)", 1, true) ~= nil and why:find("(mobs in sight)", 1, true) == nil,
        "a room found holding only a never-listed mob does not pull the patrol back - staleness leads")
  local _, _, why2 = dud_run(600)
  check(why2:find("(stalest)", 1, true) ~= nil and why2:find("(mobs in sight)", 1, true) == nil,
        "...however long ago: it is walked in its turn, never chased")
  local _, _, why3 = dud_run(0, { EMPTY })
  check(why3:find("(mobs in sight)", 1, true) ~= nil, "a room left empty is no dud - an 'm' there later is something new")
  local _, _, why4 = dud_run(0, { GOLEM, EMPTY })
  check(why4:find("(mobs in sight)", 1, true) ~= nil, "a dud that is then seen empty is a dud no longer")
end
print(string.format("%d passed, %d failed", pass, fail))
os.exit(fail == 0 and 0 or 1)
