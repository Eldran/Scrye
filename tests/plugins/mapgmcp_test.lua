-- Harness for 3s-map-gmcp. Every Room.Info payload is verbatim from a
-- capture (Lobo and Goran, 21 Aug 2026) rather than invented, so what
-- passes here is what the server actually sends.
--
-- The property the plugin exists to keep is at the bottom: it sends NOTHING.
--
-- Equivalent mutants in the SHIFTING EXITS feature (1.6.0), documented rather than
-- faked with tests:
--   * neighbours()' shift guard on the walked-only loop: unreachable today, because a
--     mark always clears walked[dir] and a neutered told exit (0) masks the loop; kept
--     because it states the rule where the rule applies.
--   * the rode_shift half of the DISAGREES guard: told is always neutered to 0 while an
--     exit is marked, so the contradiction cannot arise; the auto_shift half is live
--     (and its removal is killed).
--   * the two vary-evidence clears in shift_cmd (on mark and on off) are individually
--     redundant - either alone keeps the "spent evidence" property, and removing BOTH
--     is killed by test 72.
--   * sweep_dirs' edge filter (the explorer, folded in at 1.8.0): a probe that crossed the
--     fence walked the link, and a walked link retires the exit from the frontier on its
--     own - so under this harness dropping the filter changes nothing. Kept for the case
--     the walk pairing misses (a move pruned on a laggy night): the edge is the persisted
--     memory that the door leads out, whatever the link store says.

-- Run from anywhere: paths are resolved relative to THIS file, which sits
-- beside the plugin it tests.
local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
-- The plugin ships (src/Scrye.App/plugins/3s-map-gmcp); this file lives in tests/plugins,
-- not beside it, because the plugin folder is copied into the build output and a test has
-- no business shipping. CI runs it (tests/plugins/run.sh).
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-map-gmcp/main.lua"
local MANIFEST = HERE .. "../../src/Scrye.App/plugins/3s-map-gmcp/plugin.json"

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
local shared
local timer_secs = {}
local gmcp_hooks, aliases, timers, connect_hooks, disconnect_hooks, command_hooks
local event_hooks, watch_hooks
local idle_hooks
local store, repeats, next_timer
local panel, state
local triggers = {}
local exports_files, exports_order = {}, {}   -- the exports folder: survives fresh_client, like a disk
local every_sent = {}          -- every command sent all suite long, never reset

local function fresh_client(keep_store)
  printed, sent, emitted = {}, {}, {}
  timer_secs = {}
  gmcp_hooks, aliases, timers, watch_hooks = {}, {}, {}, {}
  triggers = {}
  event_hooks = {}
  connect_hooks, disconnect_hooks, command_hooks = {}, {}, {}
  idle_hooks = {}
  repeats = {}
  panel, state = nil, {}
  next_timer = 0
  -- fast walk (1.18.0) is on by default; the tests written before it drive one step at a
  -- time, so a fresh store starts with it off and the fast-walk tests turn it on
  if not keep_store then store = { fast = "0" } ; shared = {} end
  scrye = {
    id = "3s-map-gmcp",
    print = function(s) printed[#printed + 1] = tostring(s) end,
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
    addPanel = function(pn) panel = pn end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    watch = function(prefix, fn) watch_hooks[#watch_hooks + 1] = { prefix = prefix, fn = fn } end,
    after = function(secs, fn)
      next_timer = next_timer + 1; timers[next_timer] = fn; timer_secs[next_timer] = secs
      return next_timer
    end,
    every = function(_, fn) next_timer = next_timer + 1; repeats[next_timer] = fn; return next_timer end,
    cancel = function(id) if id then timers[id] = nil; repeats[id] = nil end end,
    -- scrye.exports (host API 1.21): a flat folder of named files, like the real one
    exports = {
      write = function(name, text)
        if not tostring(name):match("^[%w][%w%._%-%(%) ]*%.%a+$") or name:find("..", 1, true) then
          return nil, "'" .. tostring(name) .. "' is not a file name here"
        end
        exports_files[name] = text ; exports_order[#exports_order + 1] = name
        return "C:/Users/x/AppData/Roaming/Scrye/exports/" .. name
      end,
      read = function(name)
        local t = exports_files[name]
        if t == nil then return nil, "there is no '" .. tostring(name) .. "' in the exports folder" end
        return t
      end,
      list = function()
        local out = {}
        for i = #exports_order, 1, -1 do out[#out + 1] = exports_order[i] end
        return out
      end,
    },
  }
end

local function load_plugin() dofile(PLUGIN) end

-- ---- driving it the way the client does ----
local function room(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Info" then h.fn(json) end end
end
local function cmd(text) for _, h in ipairs(command_hooks) do h(text) end end
local function emit(ev, data, source)
  for _, h in ipairs(event_hooks) do if h.ev == ev then h.fn(data, nil, source) end end
end
local function mapg(arg) aliases[1].run(arg) end
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

-- ---- the captures ----
local GATEHOUSE = '{ "exits": { "nw": 50940, "sw": 50943, "in": 50943 }, "area": "Midgard", "name": "The gatehouse of Midgard", "num": 50942 }'
local SEA       = '{ "exits": { "s": 0, "n": 60494 }, "area": "Unknown", "name": "Layer one of the Sea of Chaos", "num": 60494 }'
-- The sea is no longer mapped at all (see the Sea of Chaos section at the end), so the
-- behaviours it used to stand for -- a withheld destination of 0, and an exit pointing at
-- the room you are in -- need a room that IS mapped. Same shape, ordinary name.
-- Constructed, not captured: no single captured room happens to show both at once.
local ODDITY    = '{ "exits": { "s": 0, "n": 4242 }, "area": "Unknown", "name": "A Vortex", "num": 4242 }'
local VOID      = '{ "exits": { }, "area": "Unknown", "name": "Da Void", "num": 268 }'
local VORTEX    = '{ "exits": { "enter": 280, "s": 282, "e": 1192, "w": 3640 }, "area": "Unknown", "name": "A Vortex", "num": 281 }'
local SMURF     = '{ "exits": { "n": 1728 }, "area": "Smurfland", "name": "A very small forest", "num": 1727 }'
-- from the screenshot of the live run, 23 Aug
local CRYSTAL   = '{ "exits": { "s": 1113, "w": 1816 }, "area": "Unknown", "name": "Crystal Tree and Arch", "num": 1114 }'

-- ============================================================
fresh_client()
load_plugin()

-- 1. It subscribes to the packages it needs, and only those.
check(#gmcp_hooks == 4, "four GMCP hooks registered - the room feed, the fight detector, the sight grid, the roster")
do
  local pkgs = {}
  for _, h in ipairs(gmcp_hooks) do pkgs[h.pkg] = true end
  check(pkgs["Room.Info"] and pkgs["Char.Combat"] and pkgs["Room.Map"] and pkgs["Room.Contents"], "...Room.Info, Char.Combat, Room.Map and Room.Contents, nothing else")
end
check(#command_hooks == 1, "it observes outgoing commands - the hook that makes links learnable")

-- 2. A room is reported: number, name, area, exits.
clear()
room(GATEHOUSE)
check(said("50942"), "the room's NUMBER is reported")
check(said("The gatehouse of Midgard"), "...its name")
check(said("[Midgard]"), "...its area")
check(said("nw>50940"), "...and where each exit leads")

-- 3. Exits are compass-first, then the rest -- the split that matters,
--    because a compass exit reverses and 'in' need not.
check(last_line():find("nw>50940", 1, true) < last_line():find("in>50943", 1, true),
      "compass exits are listed before special ones")
check(said("sw>50943") and said("in>50943"), "two exits to the same room are both shown")

-- 4. A destination of 0 is a frontier, not an error.
clear()
room(ODDITY)
check(said("s>?"), "a destination of 0 shows as unknown rather than as room zero")

-- 5. An exit pointing at its own room is called out rather than hidden.
check(said("4242(self)"), "an exit leading to the room you are in is marked")

-- 6. An empty exits object is not a dead end.
clear()
room(VOID)
check(said("may be hidden"), "a room with no listed exits says they may be hidden")
check(said("268"), "...and the room is still reported")

-- 7. 'enter' is an exit like any other.
clear()
room(VORTEX)
check(said("enter>280"), "'enter' is listed as an exit")
check(last_line():find("s>282", 1, true) < last_line():find("enter>280", 1, true),
      "...after the compass points")

-- 8. NEW the first time, (again) the second.
clear()
room(SMURF)
check(said("NEW"), "a room seen for the first time says so")
clear()
room(SMURF)
check(said("(again)"), "...and a revisit says that instead")

-- 9. A room whose exits changed is worth noticing.
clear()
room('{ "exits": { "n": 1728, "s": 9 }, "area": "Smurfland", "name": "A very small forest", "num": 1727 }')
check(said("EXITS CHANGED"), "a room re-sent with different exits is called out")
check(said("EXITS CHANGED (+s)"), "...naming what changed")

-- 9b. 6 Oct 2026: a withheld destination (0) is not a change against the number another
--     visit gave - the Megacity walkways sent each exit as a number one visit, 0 the next.
clear()
room('{ "exits": { "e": 45233, "w": 45235 }, "area": "Megacity", "name": "The Southern Walkway floor 40", "num": 45234 }')
clear()
room('{ "exits": { "e": 45233, "w": 0 }, "area": "Megacity", "name": "The Southern Walkway floor 40", "num": 45234 }')
check(said("(again)") and not said("EXITS CHANGED"), "a withheld destination is not EXITS CHANGED")
check(said("w>45235"), "...and the number given before is kept")
clear()
room('{ "exits": { "e": 45233, "w": 45235 }, "area": "Megacity", "name": "The Southern Walkway floor 40", "num": 45234 }')
check(not said("EXITS CHANGED"), "...nor is getting it back")
clear()
room('{ "exits": { "e": 45240, "w": 45235 }, "area": "Megacity", "name": "The Southern Walkway floor 40", "num": 45234 }')
check(said("EXITS CHANGED (e 45233->45240)"), "a different room on the same exit still is, and says which")

-- ============================================================
-- PHASE 1: learning links by walking.
-- ============================================================
fresh_client()
load_plugin()

-- 10. Walking an exit whose destination the server withheld resolves it.
--     This is the one thing walking can add that being told cannot.
room(ODDITY)                       -- 4242, s is 0
clear()
cmd("s")
room(CRYSTAL)                   -- arrived at 1114
check(said("learned 4242 s> 1114"), "walking an exit the server would not describe learns where it goes")
clear()
mapg("room 4242")
check(said("s     -> 1114"), "...and the link is in the store")
check(said("(walked)"), "...marked as walked rather than stated")

-- 10b. THE MOVE QUEUE'S CLOCK IS ONE SECOND (1.7.1). A move waits MOVE_TTL (12 s) for its
--      room; before this the clock advanced only on the 15 s flush tick, so a move typed
--      just before a flush was pruned at once and its link never learned. Twelve ticks of
--      lag: still paired. Thirteen: stale, and honestly so.
fresh_client()
load_plugin()
room(ODDITY)
cmd("s")
for _ = 1, 12 do tick() end
clear()
room(CRYSTAL)
check(said("learned 4242 s> 1114"), "a move twelve seconds old is still paired with its room")
fresh_client()
load_plugin()
room(ODDITY)
cmd("s")
for _ = 1, 13 do tick() end
clear()
room(CRYSTAL)
check(not said("learned 4242 s> 1114"), "...thirteen seconds old, it has expired and teaches nothing")
-- and the store still flushes: fifteen ticks after a change, the write lands
fresh_client()
load_plugin()
room(ODDITY)
check(shared["rooms"] == nil, "nothing written before the flush tick")
for _ = 1, 15 do tick() end
check(shared["rooms"] ~= nil, "...and the fifteenth tick writes it")

-- 11. A stated destination that turns out to be wrong is reported, and the
--     walk is believed. The Sea of Chaos regenerates, so this is real.
fresh_client()
load_plugin()
room(VORTEX)                    -- 281, says s -> 282
clear()
cmd("s")
room(CRYSTAL)                   -- but we arrived at 1114
check(said("DISAGREES"), "an exit that does not lead where it claimed is called out")
check(said("281 says s leads to 282"), "...naming what it claimed")
check(said("arrived at 1114"), "...and where it actually went")
clear()
mapg("room 281")
check(said("s     -> 1114"), "the walk is believed over the claim")

-- 12. A move that did not land where the queue expected teaches NOTHING.
--     Dead reckoning cannot notice this; a room number can.
fresh_client()
load_plugin()
room(GATEHOUSE)                 -- we are in 50942
cmd("n")                        -- queued from 50942
room(VORTEX)                    -- fine: 50942 -n-> 281
clear()
cmd("n")                        -- queued from 281
room(SMURF)                     -- 1727
clear()
cmd("n")                        -- queued from 1727
-- ...but something moves us without a move of ours pairing: a second
-- arrival with no matching command in the queue
room(VOID)                      -- pairs 1727 -n-> 268
room(CRYSTAL)                   -- queue empty: nothing to pair, nothing learned
mapg("room 268")
check(not said("-> 1114"), "an arrival with no move behind it invents no link")

-- 13. ...and an explicit desync is announced rather than silently absorbed. (The rooms
--     walked 'n' out of list an 'n' here: since 23 Sep a move in a direction the room
--     has no exit for is itself out of step, and this test is about the OTHER kind.)
fresh_client()
load_plugin()
room((GATEHOUSE:gsub('"nw": 50940', '"n": 281, "nw": 50940')))
cmd("n")
room((VORTEX:gsub('"enter": 280', '"n": 1727, "enter": 280')))      -- 50942 -n-> 281, queue now empty
clear()
cmd("n")          -- queued from 281
room(SMURF)       -- ok, 281 -n-> 1727
cmd("n")          -- queued from 1727
room(VOID)        -- ok
clear()
-- now a move whose 'from' will not match: send from 268, but a portal
-- drops us somewhere and the NEXT Room.Info is for a room we did not
-- reach by that move, while a second queued move still expects 268.
cmd("n")
cmd("n")
room(CRYSTAL)     -- pairs first move from 268 -> 1114 (believed)
room(GATEHOUSE)   -- second move expected to start at 268, but we were in 1114
check(said("out of step"), "a move that did not start where the queue thought says so")
clear()
mapg()
check(said("out-of-step 1"), "...and it is counted in the status line")

-- 14. Only movement words are queued.
fresh_client()
load_plugin()
room(GATEHOUSE)
cmd("kill smurf")
cmd("look")
room(VORTEX)
mapg("room 50942")
check(not said("-> 281"), "a non-movement command never becomes a link")

-- 15. Long forms of directions count as moves.
fresh_client()
load_plugin()
room(ODDITY)
cmd("SOUTH")
room(CRYSTAL)
check(said("learned 4242 s> 1114"), "'SOUTH' is the same move as 's', whatever the case")

-- ============================================================
-- PHASE 1: the store as a graph.
-- ============================================================
fresh_client()
load_plugin()
-- A small connected world, all stated by the server, with TWO routes from 1
-- to 4: a three-step one and a four-step detour. A search that merely finds
-- a route will happily return the detour, so the graph has to be able to
-- tell the difference for "shortest" to mean anything.
room('{ "num": 1, "name": "One",    "area": "Test", "exits": { "n": 2, "e": 5 } }')
room('{ "num": 2, "name": "Two",    "area": "Test", "exits": { "s": 1, "e": 3 } }')
room('{ "num": 3, "name": "Three",  "area": "Test", "exits": { "w": 2, "u": 4 } }')
room('{ "num": 5, "name": "Five",   "area": "Test", "exits": { "w": 1, "e": 6 } }')
room('{ "num": 6, "name": "Six",    "area": "Test", "exits": { "w": 5, "e": 7 } }')
room('{ "num": 7, "name": "Seven",  "area": "Test", "exits": { "w": 6, "n": 4 } }')
room('{ "num": 4, "name": "Four",   "area": "Test", "exits": { "d": 3, "s": 7, "n": 0 } }')

-- 16. A route is the shortest known one, in directions.
clear()
mapg("path 4")                  -- we are standing in 4
check(said("standing in it"), "a route to where you already are says so")
-- walk back to 1. Re-stating it must carry BOTH ways out: the server's exits
-- replace what we had, so a partial re-send would quietly delete the detour
-- and make the next check prove nothing.
room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2, "e": 5 } }')
clear()
mapg("path 4")
check(said("3 step(s)"), "the route from 1 to 4 is the SHORT one, not the four-step detour")
check(said("n e u"), "...and is given as directions to type")
check(not said("4 step(s)"), "...the detour is never the answer")
check(said("nothing was sent"), "...and says out loud that it walked nothing")

-- 17. A room with no known route says so rather than guessing.
clear()
room('{ "num": 99, "name": "Island", "area": "Nowhere", "exits": { } }')
room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2, "e": 5 } }')
clear()
mapg("path 99")
check(said("no known route"), "an unreachable room is reported as unreachable")

-- 18. The frontier is what is worth walking to next.
clear()
mapg("frontier")
check(said("unexplored exit"), "the frontier lists exits with no destination")
check(said("4"), "...and room 4 is on it, because its 'n' is 0")

-- 19. 'explore' routes to the nearest one, and still sends nothing.
clear()
mapg("explore")
check(said("nearest unexplored: 4"), "explore finds the nearest room with an unwalked exit")
check(said("n e u"), "...and prints the way there")
check(#sent == 0, "...without sending a step of it")

-- 20. Standing on a frontier, explore says so instead of routing.
room('{ "num": 4, "name": "Four", "area": "Test", "exits": { "d": 3, "n": 0 } }')
clear()
mapg("explore")
check(said("right here"), "standing on an unexplored exit, explore says so")

-- ============================================================
-- PHASE 1: it survives a restart.
-- ============================================================
-- 21. What was learned is written and read back, numbers still numbers.
disconnect()                    -- forces a save
local saved = shared["rooms"]
check(type(saved) == "string" and #saved > 2, "the store is written on disconnect")
check(saved:find('"num":4', 1, true) ~= nil, "...with room numbers as numbers, not as keys")

fresh_client(true)              -- same store, brand new client
load_plugin()
local n_saved = select(2, saved:gsub('"num":', ""))
check(n_saved > 0 and said(n_saved .. " room(s) known"),
      "every saved room is read back at load (" .. n_saved .. ")")
clear()
mapg("room 3")
check(said("Three"), "a room's name survived the restart")
check(said("u     -> 4"), "...and its links")

-- 22. A corrupt store is refused, not half-read, and is not overwritten.
fresh_client()
shared["rooms"] = "{not json at all"
load_plugin()
check(said("could not be read"), "an unreadable store says so")
check(said("NOT overwriting"), "...and promises not to clobber it")
check(shared["rooms"] == "{not json at all", "...and in fact does not")

-- ============================================================
-- PHASE 1: reconnecting is not a step.
-- ============================================================
-- 23. A move queued before a disconnect must not pair with the room we
--     appear in afterwards -- that is exactly how a phantom link is born.
fresh_client()
load_plugin()
room(GATEHOUSE)
cmd("n")
connect()                       -- reconnect: position unknown again
clear()
room(VORTEX)
mapg("room 50942")
check(not said("-> 281"), "a move from before a reconnect never links across it")
-- and it is dropped quietly: forgetting where we are would otherwise make
-- every reconnect-after-a-half-typed-move report a desync that never happened.
check(not said("out of step"), "...and does not raise a false alarm on the way")

-- 23b. The sharper case, and the one that actually bites: reconnect, then
--      move BEFORE the first Room.Info arrives. Clearing the queue is not
--      enough here -- the new move is queued from wherever we THOUGHT we
--      were, and a stale 'from' matches a stale 'here', so the desync check
--      waves it through and a link between two unrelated rooms is written.
--      Position has to be forgotten, not just the queue.
fresh_client()
load_plugin()
room(GATEHOUSE)                 -- we believe we are in 50942
connect()                       -- ...we are not anywhere any more
cmd("n")                        -- typed before the first room arrives
room(VORTEX)                    -- login room, unrelated to 50942
clear()
mapg("room 50942")
check(not said("-> 281"), "after a reconnect, a move learns nothing until a room says where we are")
check(not said("out of step"), "...and it is not reported as a desync either - there was no step")

-- ============================================================
-- Housekeeping the earlier phase established, still true.
-- ============================================================
fresh_client()
load_plugin()
room(GATEHOUSE) ; room(ODDITY) ; room(SMURF)

clear() ; mapg("areas")
check(said("Midgard") and said("Unknown") and said("Smurfland"), "areas are grouped and counted")

clear() ; mapg("rooms")
check(said("50942") and said("A very small forest"), "the room list has what we have seen")

clear() ; mapg("rooms smurf")
check(said("1727"), "the room list filters by area")
check(not said("50942"), "...excluding the rest")

clear() ; mapg("off") ; room(GATEHOUSE)
check(not said("50942"), "with commentary off a room prints nothing")
check(store["talking"] == "0", "...and the choice is persisted")
mapg("on") ; clear() ; room(GATEHOUSE)
check(said("50942"), "back on again")

clear() ; mapg()
check(said("phase 1"), "status says which phase this is")
check(said("sending nothing"), "...and that it sends nothing")
check(said("room(s) known"), "...and how big the store is")

clear() ; mapg("forget 50942")
check(said("forgot room 50942"), "a room can be dropped")
clear() ; mapg("room 50942")
check(said("not in the store"), "...and is then gone")

clear() ; mapg("wibble")
check(said("don't know"), "an unknown subcommand says so")
check(said("mapg help"), "...and points at help")

clear() ; mapg("help")
check(said("mapg path"), "help mentions the route finder")
check(said("walked it"), "...and explains what the star on a destination means")

-- ============================================================
-- PHASE 2a: the picture.
-- ============================================================
local function grid() return state["plugin.3s-map-gmcp.grid"] or "" end
local function status_line() return state["plugin.3s-map-gmcp.status"] or "" end
-- the woven grid puts rooms on odd Lua indices; row 1 is the northmost line
local function grid_rows()
  local rows = {}
  for line in (grid() .. "\n"):gmatch("([^\n]*)\n") do rows[#rows + 1] = line end
  return rows
end
local function find_char(ch)
  for r, line in ipairs(grid_rows()) do
    local c = line:find(ch, 1, true)
    if c then return r, c end
  end
end

-- 24. You are on the map, and the map exists.
fresh_client()
load_plugin()
room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2 } }')
check(grid() ~= "", "a room arriving draws the panel")
check(find_char("@") ~= nil, "...with you on it")
local r0, c0 = find_char("@")
check(r0 == 15 and c0 == 21, "...in the middle of a 21x15 viewport")

-- 25. North is up, and the connector between two rooms is drawn.
room('{ "num": 2, "name": "Two", "area": "Test", "exits": { "s": 1, "e": 3 } }')
do
  local rows = grid_rows()
  local mr, mc = find_char("@")                    -- we are now in 2
  check(rows[mr + 2]:sub(mc, mc) == "#", "the room we came from is drawn one cell SOUTH")
  check(rows[mr + 1]:sub(mc, mc) == "|", "...with a connector between them")
end

-- 26. An exit whose destination the server withheld is drawn as a frontier.
room('{ "num": 9, "name": "Edge", "area": "Test", "exits": { "w": 2, "e": 0 } }')
do
  local rows = grid_rows()
  local mr, mc = find_char("@")
  check(rows[mr]:sub(mc + 2, mc + 2) == "?", "an exit with no stated destination draws a '?' east of us")
end

-- 27. Up and down mark the tile rather than moving it: a room above is not
--     a room to the north.
fresh_client()
load_plugin()
room('{ "num": 1, "name": "Cellar door", "area": "Test", "exits": { "u": 2 } }')
room('{ "num": 3, "name": "Elsewhere",   "area": "Test", "exits": { "s": 1 } }')
do
  local rows = grid_rows()
  local mr, mc = find_char("@")
  check(rows[mr + 2]:sub(mc, mc) == "^", "a room with an up exit is marked on its own tile")
end

-- 27b. ...and the room ABOVE is on another level, so it is not on this
--      drawing at all. A vertical link that displaced x,y would quietly
--      draw two floors on top of each other and call it a map.
fresh_client()
load_plugin()
room('{ "num": 1, "name": "Cellar",  "area": "Tower", "exits": { "u": 2 } }')
room('{ "num": 2, "name": "Landing", "area": "Tower", "exits": { "d": 1, "n": 3 } }')
room('{ "num": 3, "name": "Upstairs","area": "Tower", "exits": { "s": 2 } }')
do
  -- we are in 3, one north of the landing, both on level 1
  local rows = grid_rows()
  local mr, mc = find_char("@")
  check(rows[mr + 2]:sub(mc, mc) == "v", "the landing below us on THIS level shows its down exit")
  local hashes = select(2, grid():gsub("[#v%^%%]", ""))
  check(hashes == 1, "the cellar, a level below, is not drawn on this one (" .. hashes .. " other room(s))")
end
clear()
mapg("level down")
check(said("level 0"), "'mapg level down' looks at the floor below")
do
  local rows = grid_rows()
  check(grid():find("@", 1, true) == nil, "...where we are not, so no '@' is drawn")
  check(select(2, grid():gsub("%^", "")) == 1, "...and the cellar, with its up exit, is")
end
clear()
mapg("level")
check(said("level 1"), "'mapg level' comes back to ours")

-- ============================================================
-- The readability rule: one grid per area, one more per unconnected piece.
-- ============================================================
fresh_client()
load_plugin()
-- Two stretches of "Unknown" that have nothing to do with each other, each
-- attached to a different named area. This is the case that makes one big
-- Unknown grid unreadable, and the reason maps are split by what connects.
room('{ "num": 10, "name": "Corridor A1", "area": "Unknown",    "exits": { "n": 11 } }')
room('{ "num": 11, "name": "Corridor A2", "area": "Unknown",    "exits": { "s": 10, "n": 12 } }')
room('{ "num": 12, "name": "Midgard gate","area": "Midgard",    "exits": { "s": 11, "n": 13 } }')
room('{ "num": 13, "name": "Midgard road","area": "Midgard",    "exits": { "s": 12 } }')
-- ...and a second, disconnected Unknown somewhere else entirely
room('{ "num": 90, "name": "Corridor B1", "area": "Unknown",    "exits": { "e": 91 } }')
room('{ "num": 91, "name": "Corridor B2", "area": "Unknown",    "exits": { "w": 90, "e": 92 } }')
room('{ "num": 92, "name": "Smurf edge",  "area": "Smurfland",  "exits": { "w": 91 } }')

clear()
mapg("maps")
check(said("Unknown #1"), "an area in two unconnected pieces is drawn as two maps")
check(said("Unknown #2"), "...both of them")
check(said("Midgard") and said("Smurfland"), "...alongside the named areas")
check(not said("Midgard #1"), "an area in ONE piece is not numbered - the suffix means something")

-- 28. And each piece says what it borders, which is the only thing that
--     tells two stretches of "Unknown" apart at a glance.
check(said("borders Midgard"), "an Unknown corridor names the area it touches")
check(said("borders Smurfland"), "...and so does the other one")


-- 29. The map we are ON is the piece we are standing in, not all of Unknown.
room('{ "num": 10, "name": "Corridor A1", "area": "Unknown", "exits": { "n": 11 } }')
clear()
mapg("map")
check(said("2 room(s)"), "the current map is this piece of Unknown - two rooms, not four")
check(said("borders Midgard"), "...and it is the piece next to Midgard")

-- 30. Crossing into a named area switches the map under you.
room('{ "num": 11, "name": "Corridor A2", "area": "Unknown", "exits": { "s": 10, "n": 12 } }')
room('{ "num": 12, "name": "Midgard gate","area": "Midgard", "exits": { "s": 11, "n": 13 } }')
clear()
mapg("map")
check(said("this map: Midgard"), "walking into a named area switches to that area's map")
check(said("borders Unknown"), "...which knows it borders the corridor we came from")

-- 31. The room you leave by is drawn as a door, not as a plain room.
room('{ "num": 13, "name": "Midgard road","area": "Midgard", "exits": { "s": 12 } }')
do
  local rows = grid_rows()
  local mr, mc = find_char("@")            -- in 13, one north of the gate
  check(rows[mr + 2]:sub(mc, mc) == ">", "the room that leads off this map is drawn as a door")
end

-- 32. A map only ever draws its own rooms.
clear()
mapg("map")
check(said("2 room(s), 2 drawn"), "Midgard's map holds only Midgard's two rooms")

-- 28b. Two pieces of the SAME area can border each other -- joined by a
--      portal, which carries no direction and so cannot lay them out on one
--      grid. Naming that neighbour by its AREA produces "Unknown #1 borders
--      Unknown", which answers precisely the question the line exists to
--      answer. It has to name the MAP.
fresh_client()
load_plugin()
room('{ "num": 10, "name": "Pocket A1", "area": "Unknown", "exits": { "n": 11 } }')
room('{ "num": 11, "name": "Pocket A2", "area": "Unknown", "exits": { "s": 10, "enter": 90 } }')
room('{ "num": 90, "name": "Pocket B1", "area": "Unknown", "exits": { "e": 91, "out": 11 } }')
room('{ "num": 91, "name": "Pocket B2", "area": "Unknown", "exits": { "w": 90 } }')
clear()
mapg("maps")
check(said("Unknown #1") and said("Unknown #2"),
      "a portal between two Unknown pockets does not merge them - it cannot be laid out")
check(said("borders Unknown #2"), "...and each names WHICH other piece it touches")
check(not said("borders Unknown\n") and not said("borders Unknown  "),
      "...never the bare area name, which would answer nothing")

-- 28c. A map never lists itself as a border. It cannot: every room it holds
--      is excluded, and a map has exactly one label.
fresh_client()
load_plugin()
room('{ "num": 1, "name": "One", "area": "Solo", "exits": { "n": 2 } }')
room('{ "num": 2, "name": "Two", "area": "Solo", "exits": { "s": 1 } }')
clear()
mapg("map")
check(not said("borders Solo"), "a map does not report itself as one of its own borders")

-- ============================================================
-- Non-Euclidean geometry: two rooms wanting one cell.
-- ============================================================
-- 33. n,e,s,w bringing you somewhere NEW is normal on a MUD. The newcomer is
--     nudged aside and marked, rather than dropped or drawn as a lie.
fresh_client()
load_plugin()
room('{ "num": 1, "name": "A", "area": "Twist", "exits": { "n": 2 } }')
room('{ "num": 2, "name": "B", "area": "Twist", "exits": { "e": 3 } }')
room('{ "num": 3, "name": "C", "area": "Twist", "exits": { "s": 4 } }')
room('{ "num": 4, "name": "D", "area": "Twist", "exits": { "w": 5 } }')
room('{ "num": 5, "name": "E", "area": "Twist", "exits": { } }')   -- wants A's cell
clear()
mapg("map")
check(said("5 room(s), 5 drawn"), "every room is drawn, even the one that collides")
check(said("could not be drawn where their links say"), "...and the collision is admitted")
-- walk back off it: the displaced room is the one we are standing in, and
-- '@' outranks every tile mark, so the '!' only shows once we are elsewhere
room('{ "num": 1, "name": "A", "area": "Twist", "exits": { "n": 2 } }')
check(grid():find("!", 1, true) ~= nil, "...and marked on the map")

-- 33b. ...and NO connector is drawn to a displaced room. D's 'w' leads to E,
--      which had to be nudged away from the cell west of D -- and the cell
--      west of D is A. Drawing the line anyway would say "D connects west to
--      A", which is false in both directions: a tidy picture of a link that
--      does not exist is worse than a gap.
do
  local rows = grid_rows()
  local mr, mc = find_char("@")            -- A, at the origin
  check(rows[mr]:sub(mc + 2, mc + 2) == "#" or rows[mr]:sub(mc + 2, mc + 2) == "v"
        or rows[mr]:sub(mc + 2, mc + 2) == "^" or rows[mr]:sub(mc + 2, mc + 2) == ">",
        "D is drawn in the cell east of A")
  check(rows[mr]:sub(mc + 1, mc + 1) == " ",
        "...with no connector between them, because their link is not to each other")
end

-- 34. Redrawing is a complete repair, because coordinates are derived.
clear()
mapg("redraw")
check(said("laid out again"), "the layout can always be rebuilt from the links")
check(find_char("@") ~= nil, "...and we are still on it afterwards")

-- ============================================================
-- The panel itself.
-- ============================================================
-- 35. Clicking a room prints the route to it. It does NOT walk it.
fresh_client()
load_plugin()
room('{ "num": 1, "name": "One",   "area": "Test", "exits": { "n": 2 } }')
room('{ "num": 2, "name": "Two",   "area": "Test", "exits": { "s": 1 } }')
check(panel ~= nil, "the plugin declares a HUD panel")
do
  -- The Maps tab is a table widget: tab-separated columns, newline-separated
  -- rows, as a plain string. Handing it JSON would render as one long line of
  -- punctuation and look like a data bug rather than a formatting one.
  local ml = state["plugin.3s-map-gmcp.maplist"] or ""
  local cols = panel.tabs[2].widgets[3].columns
  check(#cols == 3, "the Maps tab declares three columns")
  check(ml ~= "" and not ml:find("[%[{]"), "the map list is tab/newline text, not JSON")
  local first = ml:match("^[^\n]*")
  check(select(2, first:gsub("\t", "")) == #cols - 1,
        "...with one tab fewer than there are columns")
end
check(panel.tabs[1].widgets[2].weave == true, "the map is a woven grid, rooms and connectors")
do
  -- 35a. The legend under the map: a text widget bound to a state the plugin sets once at
  --      load, one swatch per tile the drawer can put on the grid, every swatch a
  --      background in that tile's palette colour, and the markup balanced.
  local w = panel.tabs[1].widgets
  local legend_w = w[#w]
  check(legend_w.type == "text" and legend_w.bind == "plugin.3s-map-gmcp.legend",
        "the last widget on the Map tab is the legend text")
  local lg = state["plugin.3s-map-gmcp.legend"] or ""
  check(lg:find("you", 1, true) and lg:find("room", 1, true) and lg:find("unexplored", 1, true)
        and lg:find("another map", 1, true) and lg:find("off its links", 1, true)
        and lg:find(" up", 1, true) and lg:find("down", 1, true),
        "the legend names every kind of square the drawer paints")
  check(lg:find("/accent,", 1, true) and lg:find("/dim,", 1, true) and lg:find("/warning,", 1, true)
        and lg:find("/info,", 1, true), "...each swatch a background in the palette's own colour")
  check(lg:find("@{inset/dim,bold} ^ @{}", 1, true) and lg:find("@{inset/accent,bold}   @{}", 1, true),
        "marked tiles show their mark, unmarked ones a blank swatch - as the grid draws them")
  local opens, closes = select(2, lg:gsub("@{[^}]", "")), select(2, lg:gsub("@{}", ""))
  check(opens == closes and opens >= 8, "the legend's markup is balanced")
end
do
  local map_tab = panel.tabs[1].widgets[2]
  local mr, mc = find_char("@")                       -- we are in 2
  clear()
  map_tab.onClick(mc - 1, mr + 1)                     -- 0-based cell of the room south of us
  check(said("1 step(s)"), "clicking a mapped room prints the route to it")
  check(said("nothing was sent"), "...and says that it walked nothing")
  check(#sent == 0, "...and in fact sends nothing")
end

-- 35b. Right-click menus (API 1.18): the grid's onRightClick and the map/fav rows'
--      onRowMenu return { label, command } entries whose commands are exactly what
--      you would type - they route through mapg's own aliases, so the menu can never
--      drift from the commands. A cell with no room (or the room you are on) returns
--      nothing: no menu over empty sea, no "walk to where you stand".
do
  local map_tab = panel.tabs[1].widgets[2]
  local mr, mc = find_char("@")
  local menu = map_tab.onRightClick(mc - 1, mr + 1)     -- the room south of us (room 1)
  check(type(menu) == "table" and #menu == 7, "grid right-click returns a seven-entry menu")
  check(menu[1][1] == "Walk there" and menu[1][2] == "mapg go 1",
        "...whose first entry walks by typed command")
  check(menu[2][2] == "mapg path 1" and menu[3][2] == "mapg room 1",
        "...route and details ride the same alias surface")
  check(menu[4][1] == "Forget room" and menu[4][2] == "mapg forget 1",
        "...and the fourth entry forgets that room by typed command (never a left-click)")
  check(menu[5][1] == "-" and menu[6][1] == "Add note..." and menu[6][2] == "mapg note #1"
        and menu[7][1] == "Add to favourites" and menu[7][2] == "mapg fav 1",
        "...then, after a separator, a note and a favourite for that room (1.15.0)")
  check(#menu == 7, "with no farmer loaded, nothing of the farmer's is offered")
  -- the farmer's room rules appear when its panel publishes a status (17 Sep)
  state["plugin.3s-farmer.status"] = "off"
  menu = map_tab.onRightClick(mc - 1, mr + 1)
  check(#menu == 12 and menu[8][1] == "-" and menu[9][2] == "farm room 1 pass"
        and menu[10][2] == "farm room 1 avoid" and menu[11][2] == "farm room 1 rest"
        and menu[12][2] == "farm room 1 -",
        "with the farmer loaded, its four room rules follow a separator, as typed farm commands")
  state["plugin.3s-farmer.status"] = nil
  -- ...and the rules it publishes colour the rooms: 'A' never entered, 'P' passed through
  mapg("stop")                                           -- the route a click lit above
  state["plugin.3s-farmer.roomrules"] = "1:avoid"
  mapg("redraw")
  local rows = grid_rows()
  check(rows[mr + 2]:sub(mc, mc) == "A", "a room the farmer never enters is drawn 'A' (its own colour)")
  state["plugin.3s-farmer.roomrules"] = "1:pass"
  mapg("redraw")
  rows = grid_rows()
  check(rows[mr + 2]:sub(mc, mc) == "P", "...and one it passes through 'P'")
  state["plugin.3s-farmer.roomrules"] = ""
  mapg("redraw")
  rows = grid_rows()
  check(rows[mr + 2]:sub(mc, mc) ~= "A" and rows[mr + 2]:sub(mc, mc) ~= "P", "a cleared rule clears the mark")
  check((state["plugin.3s-map-gmcp.legend"] or ""):find("farmer: never enters", 1, true) ~= nil
        and (state["plugin.3s-map-gmcp.legend"] or ""):find("/error,", 1, true) ~= nil,
        "the legend explains both marks in the palette's own colours")
  -- Your own square has nowhere to walk to, so its menu is about the exits instead:
  -- one "Mark shifting" entry per exit (room 2 has exactly one, s), each the typed
  -- 'mapg shift <room> <dir>' - the elevator mark without looking up a room number.
  local own = map_tab.onRightClick(mc - 1, mr - 1)
  check(type(own) == "table" and #own == 4, "right-clicking your own room lists its exits")
  check(own[1][1] == "Mark shifting: s" and own[1][2] == "mapg shift 2 s",
        "...each entry marking that exit shifting by typed command")
  check(own[2][1] == "-" and own[3][2] == "mapg note #2" and own[4][2] == "mapg fav 2",
        "...and then a note and a favourite for it, as for any room")
  check(map_tab.onRightClick(mc + 1, mr - 1) == nil or
        select("#", map_tab.onRightClick(mc + 1, mr - 1)) == 0,
        "empty sea still offers no menu")

  -- The list is every exit the room is known to have from any source: told by the
  -- server, only ever walked (a door the feed never named), or only ever marked.
  clear() ; mapg("shift 2 vortex")                   -- marked, never told, never walked
  room('{ "num": 2, "name": "Two",   "area": "Test", "exits": { } }')   -- the server names none
  cmd("w") ; room('{ "num": 3, "name": "Three", "area": "Test", "exits": { } }')
  cmd("e") ; room('{ "num": 2, "name": "Two",   "area": "Test", "exits": { } }')   -- 2 w> 3 walked only
  mr, mc = find_char("@")
  own = map_tab.onRightClick(mc - 1, mr - 1)
  local got = {}
  for _, e in ipairs(own or {}) do if e[2] then got[e[2]] = e[1] end end
  check(got["mapg shift 2 w"] == "Mark shifting: w", "a walked-only exit is offered")
  check(got["mapg shift 2 vortex off"] == "Unmark shifting: vortex", "a marked-only exit is offered, as unmark")
  room('{ "num": 2, "name": "Two",   "area": "Test", "exits": { "s": 1 } }')
  mapg("shift 2 vortex off")

  local maps_tab = panel.tabs[2].widgets[3]
  local rmenu = maps_tab.onRowMenu(nil, 1)
  check(type(rmenu) == "table" and rmenu[1][2]:match("^mapg go %d+$") ~= nil,
        "a map row's menu walks to that row's seed room")
  check(maps_tab.onRowMenu(nil, 999) == nil, "a row index past the list returns no menu")
end

-- 36. The panel can be turned off and stays off.
clear()
mapg("draw off")
check(store["drawing"] == "0", "the panel toggle is persisted")
state["plugin.3s-map-gmcp.grid"] = "STALE"
room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2 } }')
check(grid() == "STALE", "with the panel off, a room does not redraw it")
mapg("draw on")
check(grid() ~= "STALE", "...and turning it back on redraws immediately")

-- ============================================================
-- PHASE 2b: walking. The first thing here that can move the character.
-- ============================================================
-- A four-room line: 1 -n-> 2 -e-> 3 -u-> 4
local function line_world()
  fresh_client()
  load_plugin()
  room('{ "num": 1, "name": "One",   "area": "Test", "exits": { "n": 2 } }')
  room('{ "num": 2, "name": "Two",   "area": "Test", "exits": { "s": 1, "e": 3 } }')
  room('{ "num": 3, "name": "Three", "area": "Test", "exits": { "w": 2, "u": 4 } }')
  room('{ "num": 4, "name": "Four",  "area": "Test", "exits": { "d": 3 } }')
  room('{ "num": 1, "name": "One",   "area": "Test", "exits": { "n": 2 } }')   -- back to the start
  sent = {}
end
-- the client echoing our own command back through onCommand, as it really does
local function arrive(json) cmd(sent[#sent]) ; room(json) end

local R2 = '{ "num": 2, "name": "Two",   "area": "Test", "exits": { "s": 1, "e": 3 } }'
local R3 = '{ "num": 3, "name": "Three", "area": "Test", "exits": { "w": 2, "u": 4 } }'
local R4 = '{ "num": 4, "name": "Four",  "area": "Test", "exits": { "d": 3 } }'

-- 43. One step goes out, and only one. The next waits for confirmation.
line_world()
clear()
mapg("go 4")
check(said("3 step(s): n e u"), "the walk announces the route before taking it")
check(#sent == 1 and sent[1] == "n", "exactly one step is sent - the rest wait to be confirmed")
arrive(R2)
check(#sent == 2 and sent[2] == "e", "the next step goes out once the first is confirmed")
arrive(R3)
check(#sent == 3 and sent[3] == "u", "...and the next")
clear()
arrive(R4)
check(#sent == 3, "no step is sent after the last one")
check(said("arrived"), "...and it says it arrived")
check(said("Four"), "...naming where")

-- 43b. THE MAPS TAB WALKS (API 1.15). A row click IS 'walk to that map': the row index
--      looks up the seed recorded when the list was composed, and everything else is an
--      ordinary requested walk - announced, one confirmed step at a time, refused while
--      another walk runs. Two areas linked by a door make two maps with a route between.
fresh_client() ; load_plugin()
room('{ "num": 1, "name": "One",  "area": "Test",  "exits": { "n": 2 } }')
room('{ "num": 2, "name": "Two",  "area": "Test",  "exits": { "s": 1, "e": 3 } }')
room('{ "num": 3, "name": "Camp", "area": "Other", "exits": { "w": 2 } }')
room('{ "num": 2, "name": "Two",  "area": "Test",  "exits": { "s": 1, "e": 3 } }')
room('{ "num": 1, "name": "One",  "area": "Test",  "exits": { "n": 2 } }')
sent = {}
do
  local maps_tab = panel.tabs[2].widgets[3]
  check(type(maps_tab.onRowClick) == "function",
        "the Maps table declares a row-click handler (API 1.15)")
  clear()
  maps_tab.onRowClick("Other", 1)              -- row 1: alphabetical, Other before Test
  check(#sent == 1 and sent[1] == "n", "clicking another map's row starts a walk toward it")
  clear()
  maps_tab.onRowClick("Other", 1)
  check(said("already walking"), "a click mid-walk is refused like any second walk")
  check(#sent == 1, "...and sends nothing")
  arrive('{ "num": 2, "name": "Two", "area": "Test", "exits": { "s": 1, "e": 3 } }')
  check(#sent == 2 and sent[2] == "e", "...one confirmed step at a time, like any walk")
  clear()
  arrive('{ "num": 3, "name": "Camp", "area": "Other", "exits": { "w": 2 } }')
  check(said("arrived"), "...and it announces the arrival")
  clear()
  maps_tab.onRowClick("Other", 1)
  check(said("on that map already"), "clicking the map you stand on walks nowhere")
  clear()
  maps_tab.onRowClick("gone", 99)
  check(said("not in the store any more"), "a stale row index refuses instead of guessing")
  check(#sent == 2, "...and nothing further was sent")
end

-- 43c. A no-route click says WHAT to do about it. "no known route" reads as a bug when
--      you are standing next to the place; the truth is unexplored ground (or an
--      unlearned door) between the known rooms, and walking it once is the cure.
fresh_client() ; load_plugin()
room('{ "num": 20, "name": "Isle",  "area": "Aland", "exits": { } }')
room('{ "num": 30, "name": "Shore", "area": "Bland", "exits": { } }')
clear()
panel.tabs[2].widgets[3].onRowClick("Aland", 1)
check(said("unexplored ground (or an unlearned"),
      "a dry route search names the real cause and the cure, not just 'no route'")

-- 43d. DOOR WORDS. The server names nonstandard exits in Room.Info ("vortex", "exit",
--      "portal"). Typing such a name from the room that declares it is a MOVE and pairs
--      like one - this is how the roads' doors into areas get learned at all (the live
--      island: every road<->area crossing was such a door, known one way only).
fresh_client() ; load_plugin()
room('{ "num": 10, "name": "A road", "area": "Unknown", "exits": { "e": 11, "vortex": 0 } }')
clear()
cmd("vortex")
room('{ "num": 500, "name": "Beyond", "area": "Elsewhere", "exits": { } }')
check(said("learned 10 vortex> 500"), "a named door, typed, is paired and learned")
room('{ "num": 10, "name": "A road", "area": "Unknown", "exits": { "e": 11, "vortex": 0 } }')
sent = {}
mapg("go 500")
check(#sent == 1 and sent[1] == "vortex", "...and the walker now routes through the door")
-- and the guard: a word the room does NOT name is not a move, and cannot poison pairing
cmd("stop")                                  -- our own alias-ish word, not an exit name
clear()
cmd("dance")                                 -- not an exit name either
cmd("e")                                     -- a real move follows
room('{ "num": 11, "name": "East", "area": "Unknown", "exits": { "w": 10 } }')
check(not said("out of step"), "an unnamed word does not queue, so pairing stays true")
check(not said("learned 10 dance"), "...and no invented door is written")

-- 43e. THE NAME BOX. Realms are "Unknown" connective space (Chaos, Science...), so their
--      maps land in auto labels; the Maps tab's input renames the map you are standing on
--      through the same name_map machinery the 'mapg name' command uses. The box's bound
--      state always shows the current label, so it doubles as "where am I".
fresh_client() ; load_plugin()
room('{ "num": 40, "name": "A dark road", "area": "Unknown", "exits": { } }')
do
  local box = panel.tabs[2].widgets[4]
  check(box ~= nil and box.type == "input" and type(box.onSubmit) == "function",
        "the Maps tab carries a name box below the list")
  check(state["plugin.3s-map-gmcp.mapname"] == "Unknown",
        "...showing the standing map's auto label")
  clear()
  box.onSubmit("Chaos realm")
  check(said("this map is now 'Chaos realm'"), "submitting renames the map you stand on")
  check(state["plugin.3s-map-gmcp.mapname"] == "Chaos realm", "...and the box follows")
  check((state["plugin.3s-map-gmcp.maplist"] or ""):find("Chaos realm", 1, true) ~= nil,
        "...and so does the Maps list")
  clear()
  box.onSubmit("   ")
  check(#printed == 0, "a blank submit does nothing at all")
  box.onSubmit("-")
  check(said("name cleared"), "'-' puts the auto label back, exactly like the command")
  check(state["plugin.3s-map-gmcp.mapname"] == "Unknown", "...and the box shows it")
end
-- 43f. SEARCH AND ALPHABET. The Maps list reads alphabetically now, and the search box
--      filters by a map's own name OR a bordering map's name - name a realm once and its
--      name lists every area hanging off it, which is as close to "grouped by realm" as
--      a store with no realm field can honestly get. The seeds are recorded per row AS
--      COMPOSED, so clicks stay true under any filter.
fresh_client() ; load_plugin()
room('{ "num": 60, "name": "Road", "area": "Unknown", "exits": { "e": 61, "w": 62 } }')
cmd("e") ; room('{ "num": 61, "name": "Oven", "area": "Bakery", "exits": { "w": 60 } }')
cmd("w") ; room('{ "num": 60, "name": "Road", "area": "Unknown", "exits": { "e": 61, "w": 62 } }')
cmd("w") ; room('{ "num": 62, "name": "Peak", "area": "Alps", "exits": { "e": 60 } }')
cmd("e") ; room('{ "num": 60, "name": "Road", "area": "Unknown", "exits": { "e": 61, "w": 62 } }')
mapg("name Chaos realm")
do
  local lines = {}
  for l in (state["plugin.3s-map-gmcp.maplist"] or ""):gmatch("[^\n]+") do lines[#lines + 1] = l end
  check(#lines == 3 and lines[1]:find("Alps", 1, true) and lines[2]:find("Bakery", 1, true)
        and lines[3]:find("Chaos realm", 1, true),
        "the Maps list is alphabetical: Alps, Bakery, Chaos realm")
  local search = panel.tabs[2].widgets[2]
  check(search.type == "input" and type(search.onSubmit) == "function",
        "the Maps tab carries a search box above the list")
  search.onSubmit("bak")
  -- Two rows survive "bak": Bakery by its own name, and Chaos realm because a map also
  -- matches through its BORDERS - the same rule that makes a realm's name list its
  -- areas works in both directions. Alps matches neither way and is gone.
  local labels = {}
  for l in (state["plugin.3s-map-gmcp.maplist"] or ""):gmatch("[^\n]+") do
    labels[#labels + 1] = l:match("^>? ?([^\t]+)")
  end
  check(#labels == 2 and labels[1] == "Bakery" and labels[2] == "Chaos realm",
        "searching keeps the named match and its realm, and drops the rest")
  check((state["plugin.3s-map-gmcp.mapshint"] or ""):find("matching 'bak' - 2 map", 1, true) ~= nil,
        "...and the hint line says what the list is showing")
  sent = {}
  panel.tabs[2].widgets[3].onRowClick("Bakery", 1)
  check(#sent == 1 and sent[1] == "e",
        "a click on the FILTERED list walks to the row clicked, not to row 1 of everything")
  mapg("stop")
  search.onSubmit("chaos")
  ml = state["plugin.3s-map-gmcp.maplist"] or ""
  check(ml:find("Alps", 1, true) and ml:find("Bakery", 1, true) and ml:find("Chaos realm", 1, true),
        "searching a realm's name lists the realm AND every area bordering it")
  search.onSubmit("")
  check((state["plugin.3s-map-gmcp.mapshint"] or "") == "click a map to walk there",
        "a blank search shows everything again")
end
clear()
mapg("maps bak")
do
  local alps_row = false
  for _, l in ipairs(printed) do if l:match("^  Alps%s") then alps_row = true end end
  check(said("matching 'bak' by name or border") and said("Bakery") and not alps_row,
        "'mapg maps <text>' filters the typed list the same way")
end
clear()
mapg("maps zzz")
check(said("nothing matches 'zzz'"), "...and says so when nothing does")

-- 43g. FAVOURITES (rooms since 1.15.0: points to run back to). A tab holds up to 20: the
--      button toggles the room you stand in (press again to drop it), 'mapg fav [<room>]'
--      and a room's right-click do the same for any room, and a row walks to that very
--      room. Saved with the store (shared, so favourites carry across characters).
fresh_client() ; load_plugin()
room('{ "num": 60, "name": "Road", "area": "Unknown", "exits": { "e": 61, "w": 62 } }')
cmd("e") ; room('{ "num": 61, "name": "Oven", "area": "Bakery", "exits": { "w": 60 } }')
cmd("w") ; room('{ "num": 60, "name": "Road", "area": "Unknown", "exits": { "e": 61, "w": 62 } }')
cmd("w") ; room('{ "num": 62, "name": "Peak", "area": "Alps", "exits": { "e": 60 } }')
do
  local favtab = panel.tabs[3]
  check(favtab ~= nil and favtab.title == "Favs", "the panel carries a Favs tab")
  local btn = favtab.widgets[2].buttons[1]
  local tbl = favtab.widgets[3]
  check(btn and btn.text == "Fav this room" and tbl.type == "table"
        and type(tbl.onRowClick) == "function",
        "...with a toggle button and a clickable table")
  check(state["plugin.3s-map-gmcp.favhint"] == "no favourites yet - stand in a room and press the button, or right-click one",
        "the empty tab says how to fill it")
  clear()
  btn.action()                               -- favourite the peak, where we stand
  check(said("favourite 1/20: 62 Peak [Alps]"), "the button saves the room you stand in")
  check(state["plugin.3s-map-gmcp.favlist"] == "> 62 Peak\tAlps", "...and the tab lists it, marked as where you are")
  clear()
  mapg("fav 61")                             -- any room, by number - the oven, a map away
  check(said("favourite 2/20: 61 Oven [Bakery]"), "'mapg fav <room>' saves any room")
  check(state["plugin.3s-map-gmcp.favlist"] == "> 62 Peak\tAlps\n61 Oven\tBakery", "...listed in the order saved")
  mapg("fav 60")
  check((state["plugin.3s-map-gmcp.favlist"] or ""):find("60 Road\tUnknown", 1, true) ~= nil,
        "...two rooms of one map are two favourites now, not one")
  mapg("note #61")
  check((state["plugin.3s-map-gmcp.detail"] or ""):find("favourite", 1, true) ~= nil, "a favourite's details line says so")
  do
    local map_tab = panel.tabs[1].widgets[2]
    local mr, mc = find_char("@")
    local own = map_tab.onRightClick(mc - 1, mr - 1)      -- the peak, where we stand: a favourite
    check(own[#own][1] == "Remove from favourites" and own[#own][2] == "mapg fav 62",
          "the menu of a favourite offers to remove it")
  end
  mapg("go 61")                                -- walking...
  clear()
  tbl.onRowClick("60 Road", 3)
  check(said("already walking"), "a favourite clicked mid-walk does not start a second walk")
  mapg("stop")
  sent = {}
  tbl.onRowClick("61 Oven", 2)
  check(#sent == 1 and sent[1] == "e", "clicking a favourite walks to THAT room (" .. tostring(sent[1]) .. ")")
  check(said("61 Oven (a favourite)"), "...and says where")
  mapg("stop")
  local rmenu = tbl.onRowMenu(nil, 2)
  check(rmenu and rmenu[#rmenu][1] == "Remove from favourites" and rmenu[#rmenu][2] == "mapg fav 61",
        "a favourite's row menu can drop it")
  clear()
  mapg("fav 61")                             -- the same toggle again
  check(said("no longer a favourite: 61 Oven"), "saving it again drops it")
  check(not (state["plugin.3s-map-gmcp.favlist"] or ""):find("Oven", 1, true), "...from the list too")
  -- a favourite whose room is forgotten goes with it - a room re-learned under that number
  -- later is not quietly a favourite again
  mapg("forget 60")
  room('{ "num": 60, "name": "Road", "area": "Unknown", "exits": { "e": 61, "w": 62 } }')
  check(not (state["plugin.3s-map-gmcp.favlist"] or ""):find("60 Road", 1, true), "a forgotten favourite stays gone")
  room('{ "num": 62, "name": "Peak", "area": "Alps", "exits": { "e": 60 } }')
  clear() ; mapg("fav 999")
  check(said("room 999 is not in the store"), "a room the store does not have is refused")
  clear() ; mapg("fav oven")
  check(said("mapg fav [<room>]"), "...and a word that is not a room number gets the usage")
  -- favourites survive a restart, with the store
  disconnect()
  fresh_client(true) ; load_plugin()
  room('{ "num": 62, "name": "Peak", "area": "Alps", "exits": { "e": 60 } }')
  check((state["plugin.3s-map-gmcp.favlist"] or ""):find("62 Peak", 1, true) ~= nil,
        "favourites are saved with the map and come back after a restart")
  -- the cap is real: fill to 20, the 21st is refused
  for i = 2, 20 do                             -- (the peak is still one)
    room('{ "num": ' .. (200 + i) .. ', "name": "Spot", "area": "Z' .. i .. '", "exits": { } }')
    mapg("fav")
  end
  check((state["plugin.3s-map-gmcp.favhint"] or ""):find("20 of 20 saved", 1, true) ~= nil,
        "twenty favourites fit")
  room('{ "num": 300, "name": "One too many", "area": "Overflow", "exits": { } }')
  clear()
  mapg("fav")
  check(said("favourites are full (20)"), "the twenty-first is refused, saying why")
  -- a wipe takes the favourites with it: nothing to dangle
  mapg("wipe") ; mapg("wipe yes")
  check((state["plugin.3s-map-gmcp.favlist"] or "") == "", "a wipe clears the favourites too")
end

-- 43g1. A favourite is drawn like a place, 'b'.
fresh_client() ; load_plugin()
room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2 } }')
mapg("fav")
cmd("n") ; room('{ "num": 2, "name": "Two", "area": "Test", "exits": { "s": 1 } }')
do
  local rows = grid_rows()
  local mr, mc = find_char("@")
  check(rows[mr + 2]:sub(mc, mc) == "b", "a favourite room is drawn 'b' on the map")
end

-- 43g2. ...even when the panel is OFF at wipe time. With drawing off nothing refreshes,
--       so pruning cannot cover for the wipe: if the wipe itself forgot the favourites,
--       a re-walked room wearing an old favourite's NUMBER would resurrect it - a
--       favourite pointing at whatever the new world happens to call room 70.
fresh_client() ; load_plugin()
room('{ "num": 70, "name": "Old spot", "area": "Somewhere", "exits": { } }')
mapg("fav")
mapg("draw off")
mapg("wipe") ; mapg("wipe yes")
room('{ "num": 70, "name": "A different world", "area": "Elsewhere", "exits": { } }')
mapg("draw on")
check((state["plugin.3s-map-gmcp.favlist"] or "") == "",
      "a wipe forgets favourites even with the panel off - room numbers do not resurrect them")

line_world()
mapg("go 4")
clear()
arrive('{ "num": 99, "name": "Somewhere else", "area": "Elsewhere", "exits": { } }')
check(said("should have reached 2"), "a step that lands in the wrong room is caught")
check(said("we are in 99"), "...naming where we actually are")
check(said("Walk stopped"), "...and the walk stops")
local before = #sent
arrive(R3)
check(#sent == before, "...and stays stopped - nothing further is sent")

-- 45. A step that lands nowhere at all: nothing arrives, the watchdog fires.
line_world()
mapg("go 4")
clear()
fire_timers()
check(said("never landed"), "a step that produces no room at all is caught by the watchdog")
check(said("Walk stopped") or said("stopped where you stand"), "...and the walk stops")
check(#sent == 1, "...having sent only the step that failed")

-- 46. Moving yourself stops it. Two hands on the wheel is how a walker ends
--     up somewhere nobody chose.
line_world()
mapg("go 4")
arrive(R2)
clear()
cmd("s")                              -- you typed a direction of your own
check(said("you moved yourself"), "a move you type stops the walk")
local n = #sent
room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2 } }')
check(#sent == n, "...and nothing more is sent")

-- 47. ...but a command that is not a move does not.
line_world()
mapg("go 4")
clear()
cmd("look")
cmd("score")
check(not said("you moved yourself"), "typing something that is not a move does not stop the walk")
arrive(R2)
check(#sent == 2, "...and it carries on")

-- 48. 'mapg stop' always works.
line_world()
mapg("go 4")
clear()
mapg("stop")
check(said("walk stopped"), "'mapg stop' stops it")
local m = #sent
arrive(R2)
check(#sent == m, "...and no step follows")
clear()
mapg("stop")
check(said("not walking"), "stopping when not walking says so")

-- 49. The idle guard stops it and it does NOT resume.
line_world()
mapg("go 4")
clear()
idle()
check(said("idle guard"), "the idle guard stops a walk")
check(said("NOT resume"), "...and says it will not resume on its own")
local k = #sent
arrive(R2)
check(#sent == k, "...and it does not")

-- 50. A disconnect takes the walk with it.
line_world()
mapg("go 4")
local j = #sent
disconnect()
connect()
arrive(R2)
check(#sent == j, "a walk does not survive the connection it was walking on")

-- 50b. ...and so does a reconnect that never announced a disconnect. The
--      route was planned from a room we are no longer standing in, and
--      position is unknown until a room says otherwise, so there is nothing
--      left to confirm a step against.
line_world()
mapg("go 4")
local j2 = #sent
connect()                              -- straight back, no disconnect seen
arrive(R2)
check(#sent == j2, "a reconnect alone also ends the walk")
clear()
mapg()
check(not said("WALKING"), "...and the status agrees it is not walking")

-- 51. Walking still teaches links, exactly as typing the moves would.
line_world()
mapg("go 4")
arrive(R2) ; arrive(R3) ; arrive(R4)
clear()
mapg("room 3")
check(said("u     -> 4"), "a walked route learns its links like any other walk")

-- 52. 'mapg explore go' walks to the nearest unexplored exit and stops there.
fresh_client()
load_plugin()
room('{ "num": 1, "name": "One",   "area": "Test", "exits": { "n": 2 } }')
room('{ "num": 2, "name": "Two",   "area": "Test", "exits": { "s": 1, "e": 3 } }')
room('{ "num": 3, "name": "Three", "area": "Test", "exits": { "w": 2, "e": 0 } }')   -- frontier
room('{ "num": 1, "name": "One",   "area": "Test", "exits": { "n": 2 } }')
sent = {}
clear()
mapg("explore go")
check(said("unexplored: e"), "explore go says what is unexplored at the far end")
check(#sent == 1 and sent[1] == "n", "...and starts walking")
arrive(R2)
arrive('{ "num": 3, "name": "Three", "area": "Test", "exits": { "w": 2, "e": 0 } }')
check(#sent == 2, "...stopping at the frontier rather than walking into it")
check(said("arrived"), "...and saying so")

-- 53. A route to where you already are is not a walk.
clear()
mapg("go 3")
check(said("standing in it"), "walking to where you are says so")
check(#sent == 2, "...and sends nothing")

-- 54. 'mapg go' to somewhere unreachable refuses rather than half-walking.
clear()
room('{ "num": 77, "name": "Island", "area": "Nowhere", "exits": { } }')
room('{ "num": 3, "name": "Three", "area": "Test", "exits": { "w": 2, "e": 0 } }')
clear()
mapg("go 77")
check(said("no known route"), "an unreachable target refuses")
check(#sent == 2, "...without taking a single step")

-- 55. The permission is declared, because now it is actually used.
do
  local f = io.open(MANIFEST)
  local mani = f:read("a") ; f:close()
  check(mani:find("commands.send", 1, true) ~= nil,
        "the manifest declares commands.send now that walking exists")
end

-- ============================================================
-- Naming a map.
-- ============================================================
-- "Unknown #1" and "Unknown #2" are correct and forgettable. You know one is
-- the main town and the other is where the Chaos realm starts; the map list
-- should say so.
fresh_client()
load_plugin()
room('{ "num": 10, "name": "Town square", "area": "Unknown", "exits": { "n": 11 } }')
room('{ "num": 11, "name": "Town road",   "area": "Unknown", "exits": { "s": 10, "enter": 90 } }')
room('{ "num": 90, "name": "Chaos edge",  "area": "Unknown", "exits": { "e": 91, "out": 11 } }')
room('{ "num": 91, "name": "Chaos deep",  "area": "Unknown", "exits": { "w": 90 } }')

-- 43. Name the one we are standing in.
clear()
mapg("name Chaos realm")
check(said("now 'Chaos realm'"), "a map can be given a name")
clear()
mapg("maps")
check(said("Chaos realm"), "...which replaces the generated one in the map list")
check(not said("Unknown #2"), "...entirely")

-- 44. And it replaces the generated name in OTHER maps' borders too, which is
--     where the generated names were least useful.
check(said("borders Chaos realm"), "the neighbouring map names it by its new name")

-- 45. Naming one piece leaves the other alone.
check(said("Unknown #1"), "the piece we did not name keeps its generated name")

-- 46. It survives a restart, because it is keyed by the map's seed room.
disconnect()
fresh_client(true)
load_plugin()
room('{ "num": 91, "name": "Chaos deep", "area": "Unknown", "exits": { "w": 90 } }')
clear()
mapg("maps")
check(said("Chaos realm"), "the name is still there after a restart")

-- 47. 'mapg name' on its own says what this map is called and what it would
--     be called without you.
clear()
mapg("name")
check(said("called 'Chaos realm'"), "asking says the current name")
check(said("Unknown"), "...and what it would be otherwise")

-- 48. And it can be put back.
clear()
mapg("name -")
check(said("again"), "a name can be cleared")
clear()
mapg("maps")
check(said("Unknown #") and not said("Chaos realm"), "...restoring the generated one")

-- 49. Wiping takes the names with it - a name for a map that no longer exists
--     would attach itself to whatever room number happened to come back.
fresh_client()
load_plugin()
room('{ "num": 10, "name": "Town square", "area": "Unknown", "exits": { } }')
mapg("name Main town")
mapg("wipe")
mapg("wipe yes")
room('{ "num": 10, "name": "Somewhere else entirely", "area": "Unknown", "exits": { } }')
clear()
mapg("maps")
check(not said("Main town"), "a wipe takes map names with it")

-- ============================================================
-- Starting over.
-- ============================================================
fresh_client()
load_plugin()
room(GATEHOUSE) ; room(ODDITY) ; room(SMURF)
disconnect()                                  -- something is on disk
check(shared["rooms"] ~= nil, "there is a saved store to erase")

-- 37. Asking is not doing.
clear()
mapg("wipe")
check(said("this will erase all 3 room(s)"), "wipe says how much is about to go")
check(said("mapg wipe yes"), "...and how to confirm")
clear()
mapg("rooms")
check(said("50942"), "...and has erased nothing yet")

-- 38. The confirmation only counts while it is armed, so a stray 'wipe yes'
--     typed out of the blue cannot erase anything.
fresh_client()
load_plugin()
room(GATEHOUSE)
clear()
mapg("wipe yes")
check(said("nothing to confirm"), "an unarmed confirmation does nothing")
clear()
mapg("rooms")
check(said("50942"), "...and the store is untouched")

-- 39. Anything in between disarms it: an armed wipe must not lie in wait.
clear()
mapg("wipe")
mapg("areas")                                 -- an innocent command in between
clear()
mapg("wipe yes")
check(said("nothing to confirm"), "a command between the ask and the answer cancels the wipe")
clear()
mapg("rooms")
check(said("50942"), "...and again nothing is lost")

-- 40. Armed and confirmed, it goes -- store, panel and all.
fresh_client()
load_plugin()
room(GATEHOUSE) ; room(ODDITY)
mapg("save")                                  -- there must be a file to delete, or
store["rooms"] = "an old pre-shared backup"   -- and a private leftover the wipe must also take
check(shared["rooms"] ~= nil, "the store is on disk before the wipe")   -- the next check is vacuous
mapg("wipe")
clear()
mapg("wipe yes")
check(said("erased 2 room(s)"), "an armed, confirmed wipe erases the store")
check(shared["rooms"] == nil, "...deletes the saved file rather than leaving an empty one")
check(store["rooms"] == nil, "...and takes any pre-shared private backup with it: a wipe means gone")
check(grid() == "", "...and clears the panel")
clear()
mapg("rooms")
check(said("no rooms known"), "...leaving nothing behind")
clear()
mapg()
check(said("0 room(s) known"), "...and the count agrees")

-- 41. And it starts again from wherever you are standing.
clear()
room(SMURF)
check(said("NEW"), "the next room walked into is new again")
clear()
mapg("rooms")
check(said("1727") and not said("50942"), "...and is the only thing in the store")

-- 42. Wiping an empty store is not an error, just nothing to do.
mapg("wipe")
clear()
mapg("wipe yes")
mapg("wipe")
clear()
mapg("wipe")
check(said("already empty") or said("this will erase"), "wiping says something sensible either way")

-- ============================================================
-- Forgetting a whole map or a whole area (1.7.5): the elevator floors of
-- Megacity are one map each, and a room at a time is thirty clicks for one decision.
-- ============================================================
-- Two pieces of "Tower" (floors, joined to nothing) and one of "Street".
local T10A = '{ "num": 100, "name": "Floor 10 lobby", "area": "Tower",  "exits": { "e": 101 } }'
local T10B = '{ "num": 101, "name": "Floor 10 hall",  "area": "Tower",  "exits": { "w": 100 } }'
local T30A = '{ "num": 300, "name": "Floor 30 lobby", "area": "Tower",  "exits": { "e": 301 } }'
local T30B = '{ "num": 301, "name": "Floor 30 hall",  "area": "Tower",  "exits": { "w": 300 } }'
local ST   = '{ "num": 500, "name": "Street",         "area": "Street", "exits": { "n": 100 } }'

-- 43. Asking is not doing, and the confirmation names the exact command.
fresh_client() ; load_plugin()
room(T10A) ; cmd("e") ; room(T10B) ; cmd("w") ; room(T10A)
room(T30A) ; cmd("e") ; room(T30B)
room(ST) ; cmd("n") ; room(T10A)
mapg("name floor10") ; mapg("fav")
clear() ; mapg("forget map")
check(said("drop all 2 room(s) of 'floor10'"), "'forget map' names the map you stand on and counts its rooms")
check(said("'mapg forget map 100 yes'"), "...and says the exact confirmation, seed included")
check(said("cancels"), "...and that anything else cancels")
clear() ; mapg("rooms")
check(said("100") and said("101"), "asking dropped nothing")

-- 44. The confirmation counts only while armed, and only for the SAME target.
mapg("forget map")                                -- armed for 100 again (listing disarmed it)
clear() ; mapg("forget map 300 yes")
check(said("nothing to confirm"), "a 'yes' for a different map than the one armed is refused")
clear() ; mapg("rooms")
check(said("300") and said("301"), "...and drops nothing")
clear() ; mapg("forget map 100 yes")
check(said("nothing to confirm"), "the refused confirmation disarmed the original too - a 'yes' never lies in wait")
mapg("forget map 100")
mapg("rooms")                                     -- any other command in between...
clear() ; mapg("forget map 100 yes")
check(said("nothing to confirm"), "...disarms it")

-- 45. Armed and confirmed, the map goes: rooms, its name, its favourite - and you are
--     nowhere until the next room, since you were standing in it.
mapg("forget map") ; clear() ; mapg("forget map 100 yes")
check(said("forgot 2 room(s) of 'floor10'"), "armed and confirmed, the whole map is dropped")
check(said("picks you up again"), "...and says you are nowhere now, since you stood in it")
clear() ; mapg("rooms")
check(not said("100") and not said("101") and said("300") and said("500"),
      "its rooms are gone and every other map's rooms remain")
clear() ; mapg("maps")
check(not said("floor10"), "the map list no longer shows it")
check(not (state["plugin.3s-map-gmcp.favlist"] or ""):find("floor10", 1, true)
      and not (state["plugin.3s-map-gmcp.favlist"] or ""):find("Tower", 1, true),
      "...nor does the Favs tab")
clear() ; mapg("here")
check(said("not anywhere"), "we are nowhere until the feed says otherwise")
room(ST) ; clear() ; mapg("frontier")
check(said("n"), "the street's link into the forgotten lobby is unexplored again - the truth")
cmd("n") ; room(T10A) ; clear() ; mapg("maps")
check(said("floor10"), "map the floor again and it comes back wearing the name you gave it")
mapg("forget map") ; mapg("forget map 100 yes") ; room(ST)

-- 46. 'forget area' takes every piece of an area at once, by name, case blind.
room(T30A)
clear() ; mapg("forget area tower")
check(said("drop all 2 room(s) of 'tower'"), "'forget area' counts every room carrying that area")
clear() ; mapg("forget map 300 yes")
check(said("nothing to confirm"), "an area armed is not a map confirmed, even one inside that area")
mapg("forget area tower")
clear() ; mapg("forget area TOWER yes")
check(said("forgot 2 room(s)"), "...and 'yes' with the same name, any case, drops them")
clear() ; mapg("rooms")
check(not said("300") and said("500"), "the area is gone; the street stays")
clear() ; mapg("forget area nowhere")
check(said("no rooms carry"), "an unknown area is nothing to do, not an error")
clear() ; mapg("forget area")
check(said("mapg forget area <name>"), "a bare 'forget area' says how")
clear() ; mapg("forget map floor")
check(said("mapg forget map [seed] [yes]"), "a seed that is not a number says how")

-- 47. The Maps tab row menu offers it - as the arming command, so the printed 'yes'
--     line is still the only way to make it happen.
do
  local maps_tab = panel.tabs[2].widgets[3]
  local m = maps_tab.onRowMenu(nil, 1)
  local last = m and m[#m]
  check(last and last[1] == "Forget this map" and last[2]:match("^mapg forget map %d+$") ~= nil,
        "a map row's menu ends with 'Forget this map', the arming command for that seed")
end

-- ============================================================
-- The quiet case: nothing arriving must never look like nothing wrong.
-- ============================================================
fresh_client()
load_plugin()
clear()
connect()
fire_timers()
check(said("needs GMCP"), "a silent feed says this plugin needs GMCP")
check(said(".gmcp"), "...and points at the command that diagnoses it")
check(said("ENTER a room"), "...and explains that standing still is a legitimate reason for silence")

clear()
connect()
room(GATEHOUSE)
fire_timers()
check(not said("needs GMCP"), "a feed that is working never cries wolf")

-- ============================================================
-- THE property. Up to phase 2a this was "it sends nothing", which was easy
-- to check and is no longer true. What replaces it has to be just as
-- absolute: everything this plugin has EVER sent, across every test above,
-- is a single bare movement direction. Not a command with arguments, not a
-- 'look', not an emote, nothing with a semicolon in it. A mapper that can
-- type one word from a closed list is a very different thing to trust than
-- one that can type anything.
-- ============================================================
-- Since 43d the closed list has one principled extension: a word the SERVER named as an
-- exit of a fed room ("vortex" in this suite) may be sent too - the walker walking a
-- learned door. Still one bare word from rooms' own exit vocabularies; still never an
-- argument, a 'look', or a semicolon.
local MOVES = {
  n = true, s = true, e = true, w = true, ne = true, nw = true, se = true, sw = true,
  u = true, d = true, ["in"] = true, out = true, enter = true,
  vortex = true,                      -- the named door 43d teaches and then walks
}
do
  local bad = {}
  for _, c in ipairs(every_sent) do if not MOVES[c] then bad[#bad + 1] = c end end
  check(#bad == 0, "everything sent is a bare word from the move/door vocabulary (offenders: "
        .. table.concat(bad, ", ") .. ")")
  check(#every_sent > 0, "...and the suite did exercise sending, so that check means something")
end

local f = io.open(MANIFEST)
local mani = f:read("a"); f:close()
check(mani:find('"id": "3s-map-gmcp"', 1, true) ~= nil, "the manifest carries its id")
check(mani:find("triggers.manage", 1, true) ~= nil,
      "the manifest declares triggers.manage: the sweep's 'You cannot go' trigger needs it")

-- ============================================================
-- The Sea of Chaos: not mappable space.
--
-- Every layer room on every layer is num 60494. Mapping them would not build a wrong map;
-- it would build ONE room wearing self-links in every direction it ever saw you walk, in
-- the Unknown grid beside the main town.
local SEA_L1  = '{ "exits": { "s": 0, "n": 60494 }, "area": "Unknown", "name": "Layer one of the Sea of Chaos", "num": 60494 }'
local SEA_L2  = '{ "exits": { "n": 60494, "s": 60494, "e": 60494 }, "area": "Unknown", "name": "Layer two of the Sea of Chaos", "num": 60494 }'
-- The entrance is a real, stable room with a real exit, and IS mapped: it is how you find
-- the sea. Verbatim, 23 Aug.
local SEA_DOOR = '{ "exits": { "out": 267 }, "area": "The Sea of Chaos", "name": "A swirling Sea of Chaos", "num": 266 }'

fresh_client() ; load_plugin()
room(GATEHOUSE)
clear()
room(SEA_L1) ; room(SEA_L2)
check(said("not mapping in here"), "entering the sea is called out")
clear()
room(SEA_L1)
check(not said("not mapping in here"), "...once, not once per room")
mapg("rooms")
check(said("1 room(s)"), "no sea room reaches the store")

-- Leaving is said too, and mapping resumes.
clear()
room(SMURF)
check(said("left the Sea of Chaos"), "leaving is called out")
mapg("rooms")
check(said("2 room(s)"), "...and the room outside IS mapped")

-- The entrance is not a layer, so it stays mapped.
fresh_client() ; load_plugin()
room(SEA_DOOR)
clear() ; mapg("rooms")
check(said("1 room(s)"), "the sea's entrance room is ordinary and is mapped")
check(said("266"), "...by its own number")

-- Nothing is stitched across the sea. Walk in, walk about, walk out: the room you come
-- back to must not be linked to the room you left by a door that does not exist.
fresh_client() ; load_plugin()
room(GATEHOUSE)
cmd("nw") ; room(SEA_L1)         -- in
cmd("n")  ; room(SEA_L2)         -- about
clear()
cmd("se") ; room(SMURF)          -- out, somewhere else entirely
check(not said("learned"), "no link is learned across the sea")
clear() ; mapg("room 50942")
check(not said("1727"), "...and the room you left is not stitched to the one you came out in")
check(not said("contradict"), "...nor is its real exit contradicted by the walk through the sea")

-- ---- the map.hold contract, shared with the shipped 3s-map ----
fresh_client() ; load_plugin()
room(GATEHOUSE)
clear()
emit("map.hold", '{"on":true}', "3s-chaossea-gmcp")
check(said("mapping held by 3s-chaossea-gmcp"), "a hold is taken and named")
room(SMURF)
mapg("rooms")
check(said("1 room(s)"), "arrivals while held are not mapped")
clear()
emit("map.hold", '{"on":false}', "3s-chaossea-gmcp")
check(said("mapping resumed"), "the hold is released by whoever took it")
room(SMURF)
clear() ; mapg("rooms")
check(said("2 room(s)"), "...and mapping resumes")

-- A hold is source-agnostic: the shipped chaos-sea bot holds it just as well.
fresh_client() ; load_plugin()
room(GATEHOUSE)
emit("map.hold", '{"on":true}', "3s-chaossea")
room(SMURF)
clear() ; mapg("rooms")
check(said("1 room(s)"), "the old bot's hold works too - the contract names no plugin")

-- 56. UNEXPLORED IS ABOUT THE ROOM BEHIND THE EXIT, NOT ABOUT WHAT THE SERVER SAID.
--     In unmappable space an unwalked exit's destination is withheld (0) and that was the
--     original meaning of the frontier -- but the ordinary world names the destination of
--     every exit whether you have walked it or not. Under the old definition nothing out
--     there was ever unexplored: 'mapg explore go' stood in a town full of unvisited doors
--     and said "nothing unexplored is reachable from here through known links" (Joakim,
--     live, 25 Aug). An exit is unexplored when the room behind it is not in the store.
fresh_client() ; load_plugin()
room('{ "num": 10, "name": "Square", "area": "Town", "exits": { "n": 11, "e": 12 } }')
clear() ; sent = {}
mapg("explore go")
check(said("right here"),
      "exits with named but never-seen destinations are unexplored where you stand")
check(#sent == 0, "...and standing on them, nothing is walked")

-- 56b. And explore routes TO such a room. Rooms 20 and 21 are fully linked to each other;
--      21 also lists a door to 99, which nothing has ever seen.
fresh_client() ; load_plugin()
room('{ "num": 20, "name": "Inn",    "area": "Town", "exits": { "e": 21 } }')
room('{ "num": 21, "name": "Lane",   "area": "Town", "exits": { "w": 20, "e": 99 } }')
room('{ "num": 20, "name": "Inn",    "area": "Town", "exits": { "e": 21 } }')
clear() ; sent = {}
mapg("explore go")
check(said("unexplored: e"), "a named-but-unseen destination is a frontier worth routing to")
check(#sent == 1 and sent[1] == "e", "...and the walk toward it starts")

-- 56c. Seeing the room behind the exit retires it. This is the half that keeps the new
--      definition honest -- without it, every exit in a fully-mapped town would read
--      unexplored for ever and 'explore' would never finish anywhere.
fresh_client() ; load_plugin()
room('{ "num": 30, "name": "A", "area": "Town", "exits": { "n": 31 } }')
room('{ "num": 31, "name": "B", "area": "Town", "exits": { "s": 30 } }')
clear() ; sent = {}
mapg("explore go")
check(said("nothing unexplored is reachable"),
      "an exit whose destination has been seen is explored, told or walked either way")
check(#sent == 0, "...and nothing is walked in a fully-seen map")

-- 56d. 'mapg room' says WHY each unexplored exit is unexplored: bare for a withheld
--      destination, n>NUM? for one the server named but nothing has seen.
fresh_client() ; load_plugin()
room('{ "num": 40, "name": "Mixed", "area": "Town", "exits": { "n": 41, "e": 0 } }')
clear()
mapg("room 40")
check(said("n>41?"), "a told-but-unseen destination is shown with its number and a ?")
check(said("unexplored:"), "...on the unexplored line")

-- ============================================================
-- THE EVENT CONTRACT (docs/Plan-Map-Farmer.md phase 1): the mapper as a position oracle
-- and a walker other plugins can ask. Everything below is what the farmer will stand on.

local Y1 = '{ "num": 1, "name": "Gate",  "area": "Town",  "exits": { "e": 2 } }'
local Y2 = '{ "num": 2, "name": "Road",  "area": "Town",  "exits": { "w": 1, "e": 3 } }'
local Y3 = '{ "num": 3, "name": "Edge",  "area": "Woods", "exits": { "w": 2 } }'

-- 62. map.room on every mapped arrival, carrying exactly what was stored.
fresh_client() ; load_plugin()
room(Y1)
check(emitted_count("map.room") == 1, "an arrival emits one map.room")
local ev = emitted_ev("map.room")
check(ev and ev.num == 1 and ev.name == "Gate" and ev.area == "Town" and ev.exits
      and ev.exits.e == 2, "...with num, name, area and the exits as the server gave them")
cmd("e") ; room(Y2)
check(emitted_count("map.room") == 2, "...and again on the next arrival")

-- 62b. Held and sea arrivals emit nothing: the feed goes quiet exactly when the map does.
fresh_client() ; load_plugin()
room(Y1)
emit("map.hold", '{"on":true}', "3s-chaossea")
room(Y2)
check(emitted_count("map.room") == 1, "a held arrival emits no map.room")
emit("map.hold", '{"on":false}', "3s-chaossea")
room(SEA)
check(emitted_count("map.room") == 1, "a sea layer emits no map.room either")

-- 63. The walker narrates itself: started on the way out, arrived at the end - and a
--     stopped walk emits stopped instead, never both.
fresh_client() ; load_plugin()
room(Y1) ; cmd("e") ; room(Y2) ; cmd("w") ; room(Y1)
emitted = {}
mapg("go 2")
local st = emitted_ev("map.walk.started")
check(st and st.target == 2 and st.steps == 1, "map.walk.started names the target and length")
arrive(Y2)
local ar = emitted_ev("map.walk.arrived")
check(ar and ar.num == 2, "map.walk.arrived names where it ended up")
check(emitted_count("map.walk.stopped") == 0, "...and a finished walk emits no stopped")
emitted = {}
mapg("go 1")
cmd("e")                                   -- you move yourself mid-walk
local sp = emitted_ev("map.walk.stopped")
check(sp and sp.reason and sp.reason:find("you moved", 1, true),
      "a walk that dies emits map.walk.stopped with the reason")
check(emitted_count("map.walk.arrived") == 0, "...and no arrived")

-- 64. map.goto by number: the request walks, the requester hears the outcome.
fresh_client() ; load_plugin()
room(Y1) ; cmd("e") ; room(Y2) ; cmd("w") ; room(Y1)
emitted = {}
sent = {}
emit("map.goto", '{"num":2}', "3s-farmer")
check(#sent == 1 and sent[1] == "e", "map.goto {num} starts the walk")
check(emitted_ev("map.walk.started") ~= nil, "...announcing it")
arrive(Y2)
check(emitted_ev("map.walk.arrived").num == 2, "...and announcing the arrival")

-- 64b. Already there: arrived immediately, no steps.
emitted = {} ; sent = {}
emit("map.goto", '{"num":2}', "3s-farmer")
check(#sent == 0 and emitted_ev("map.walk.arrived").num == 2,
      "goto the room you are in arrives without a step")

-- 64c. A request that cannot be served is refused as a stopped, with the reason.
emitted = {}
emit("map.goto", '{"num":99}', "3s-farmer")
local r = emitted_ev("map.walk.stopped")
check(r and r.reason:find("not in the store", 1, true), "an unknown room refuses with why")
emitted = {}
emit("map.goto", '{"wibble":true}', "3s-farmer")
r = emitted_ev("map.walk.stopped")
check(r and r.reason:find("no num and no area", 1, true), "an empty request refuses with why")

-- 65. map.goto by area: the NEAREST known room whose area matches.
fresh_client() ; load_plugin()
room(Y1) ; cmd("e") ; room(Y2) ; cmd("e") ; room(Y3) ; cmd("w") ; room(Y2) ; cmd("w") ; room(Y1)
emitted = {} ; sent = {}
emit("map.goto", '{"area":"woods"}', "3s-farmer")
check(#sent == 1 and sent[1] == "e", "goto by area routes toward the nearest matching room")
arrive(Y2) ; arrive(Y3)
check(emitted_ev("map.walk.arrived").num == 3, "...and arrives in it")
emitted = {}
emit("map.goto", '{"area":"woods"}', "3s-farmer")
check(emitted_ev("map.walk.arrived").num == 3, "standing in the area already is an arrival")
emitted = {}
emit("map.goto", '{"area":"atlantis"}', "3s-farmer")
r = emitted_ev("map.walk.stopped")
check(r and r.reason:find("no known route into", 1, true), "an unknown area refuses with why")

-- 65b. A goto while a walk is running is refused rather than hijacking it.
fresh_client() ; load_plugin()
room(Y1) ; cmd("e") ; room(Y2) ; cmd("w") ; room(Y1)
emit("map.goto", '{"num":2}', "3s-farmer")
emitted = {}
emit("map.goto", '{"num":1}', "someone-else")
r = emitted_ev("map.walk.stopped")
check(r and r.reason:find("already walking", 1, true),
      "a second goto is refused, not allowed to steal the walker")

-- 66. map.stop stops a requested walk like 'mapg stop' would.
fresh_client() ; load_plugin()
room(Y1) ; cmd("e") ; room(Y2) ; cmd("w") ; room(Y1)
emit("map.goto", '{"num":2}', "3s-farmer")
cmd(sent[#sent])                           -- our own step echoing back
emitted = {}
emit("map.stop", '{}', "3s-farmer")
r = emitted_ev("map.walk.stopped")
check(r and r.reason:find("map.stop", 1, true), "map.stop stops the walk and says who asked how")
room(Y2)
check(emitted_count("map.walk.arrived") == 0, "...and the arrival after it is no arrival of a walk")

-- 66b. 'map.query.area' hands an area's rooms to whoever asks - resolved exits only,
--      and always an answer, even an empty one.
fresh_client() ; load_plugin()
room(Y1) ; cmd("e") ; room(Y2) ; cmd("e") ; room(Y3)
emitted = {}
emit("map.query.area", '{"area":"town"}', "3s-farmer")
local ans = emitted_ev("map.area.rooms")
check(ans ~= nil and ans.area == "town", "the query is answered")
check(ans.rooms and #ans.rooms == 2, "...with the area's rooms and only those (Woods excluded)")
local by = {}
for _, r in ipairs(ans.rooms) do by[r.num] = r end
check(by[1] and by[2] and by[1].exits.e == 2 and by[2].exits.w == 1,
      "...exits resolved, number to number")
emitted = {}
emit("map.query.area", '{"area":"atlantis"}', "3s-farmer")
ans = emitted_ev("map.area.rooms")
check(ans ~= nil and #ans.rooms == 0, "an unknown area answers with an empty list, not silence")

-- ============================================================
-- THE SHARED STORE (API 1.14): the map is world-truth and every character sees it.

-- 67. The map is written to scrye.shared; the preferences stay private to the profile.
fresh_client() ; load_plugin()
room(GATEHOUSE)
mapg("off")
disconnect()
check(shared["rooms"] ~= nil, "the map lands in the MUD-shared store")
check(store["rooms"] == nil, "...not in the private one")
check(store["talking"] == "0", "...while a preference stays private to this profile")
check(shared["talking"] == nil, "...and never leaks into the shared file")

-- 68. A map saved before scrye.shared existed is adopted on load - once, with the private
--     copy left as a backup.
fresh_client()
store["rooms"] = '{ "ver": 1, "rooms": [ { "num": 50942, "name": "The gatehouse of Midgard",'
              .. ' "area": "Midgard", "exits": { "nw": 50940 }, "walked": {} } ], "names": [] }'
load_plugin()
check(said("map moved to the MUD-shared store"), "an old private map is adopted and announced")
check(shared["rooms"] ~= nil, "...into the shared store")
check(store["rooms"] ~= nil, "...leaving the private copy as a backup")
clear() ; mapg("rooms")
check(said("1 room(s)"), "...and the adopted map is actually loaded")

-- 68b. A shared map already present wins: the private leftover is NOT re-adopted over it.
fresh_client()
shared["rooms"] = '{ "ver": 1, "rooms": [ { "num": 7, "name": "Seven", "area": "T",'
               .. ' "exits": {}, "walked": {} } ], "names": [] }'
store["rooms"]  = '{ "ver": 1, "rooms": [ { "num": 8, "name": "Eight", "area": "T",'
               .. ' "exits": {}, "walked": {} }, { "num": 9, "name": "Nine", "area": "T",'
               .. ' "exits": {}, "walked": {} } ], "names": [] }'
load_plugin()
clear() ; mapg("rooms")
check(said("1 room(s)"), "a shared map already present is never overwritten by an old backup")

-- 69. On a host that predates scrye.shared the mapper still works, in the private store -
--     the fallback idiom, proven by deleting the binding before load.
fresh_client()
scrye.shared = nil
load_plugin()
room(GATEHOUSE)
disconnect()
check(store["rooms"] ~= nil, "no scrye.shared: the map saves to the private store as before")

-- ============================================================
-- SHIFTING EXITS (Joakim's Megacity elevator, 28 Aug 2026): the car is one room,
-- every floor's lobby its own -- the ROOMS were never the problem -- but the car's
-- 's' ends at a different lobby each ride, and "the walk is believed" kept
-- rewriting it. Numbers and names from the live screenshot; payload SHAPES are
-- the capture's, the per-floor exit values reconstructed from what the DISAGREES
-- line itself reported (44346 s>44345 told, 44362 walked).

local LOBBY3  = '{ "num": 44345, "name": "Floor 3 Elevator Lobby",  "area": "Megacity", "exits": { "n": 44346 } }'
local LOBBY10 = '{ "num": 44362, "name": "Floor 10 Elevator Lobby", "area": "Megacity", "exits": { "n": 44346 } }'
local LOBBY7  = '{ "num": 44380, "name": "Floor 7 Elevator Lobby",  "area": "Megacity", "exits": { "n": 44346 } }'
local LIFT3   = '{ "num": 44346, "name": "Elevator", "area": "Megacity", "exits": { "s": 44345 } }'
local LIFT10  = '{ "num": 44346, "name": "Elevator", "area": "Megacity", "exits": { "s": 44362 } }'
local LIFT7   = '{ "num": 44346, "name": "Elevator", "area": "Megacity", "exits": { "s": 44380 } }'

-- 70. Auto-detection: once is a correction, twice is an elevator. The first changed
--     arrival DISAGREES and is believed (unchanged behaviour, now with a pointer at
--     the mark); the second marks the exit shifting by itself.
fresh_client() ; load_plugin()
room(LOBBY3)
cmd("n") ; room(LIFT3)
cmd("s") ; room(LOBBY3)                  -- ride that stays on the floor: walked s -> 44345
cmd("n") ; room(LIFT3)
clear()
cmd("s") ; room(LOBBY10)                 -- the ride from the screenshot: s ends at 44362
check(said("DISAGREES"), "first changed arrival: still called out and believed")
check(said("mapg shift 44346 s"), "...and the callout now points at the mark")
cmd("n") ; room(LIFT10)                  -- the car's told 's' names the new floor
-- Before the mark, the believed ride glues the floors into one map through the car,
-- and both lobbies want the cell south of it - one is drawn displaced. This is the
-- picture Joakim sent (15 Sep 2026): floor 0's rooms pushed aside over floor 30's.
local st = state["plugin.3s-map-gmcp.status"] or ""
check(st:find("3 room(s)", 1, true) and st:find("displaced", 1, true),
      "an unmarked elevator makes one map of every floor, with a lobby displaced")
clear()
cmd("s") ; room(LOBBY7)                  -- second changed arrival
check(said("marked SHIFTING"), "twice is a pattern: the exit marks itself shifting")
check(not said("DISAGREES"), "...and the mark replaces the DISAGREES noise")
clear() ; mapg("room 44346")
check(said("shifting: s"), "room detail names the shifting exit")
check(said("no known way out"), "...and the learned s link is gone - the lie is unlearned")

-- 70b. Marked, the exit is out of every calculation: no route through it, not frontier,
--      drawn '~', and the per-floor told destination no longer churns the room.
clear() ; mapg("path 44345")             -- floor 7 -> floor 3 only goes through the car
check(said("no known route"), "routes never use a shifting exit")
clear() ; mapg("frontier")
check(said("no unexplored exits"), "a shifting exit is not frontier - nothing waits behind it to learn")
clear()
cmd("n") ; room(LIFT7)                   -- ride in again: told s names floor 7 now
check(said("s>~"), "the exit is drawn ~ where a destination would be")
check(not said("EXITS CHANGED"), "the per-floor told value is neutered - no churn on every visit")
do
  -- Standing in the car, the right-click menu on our own square offers to UNDO the
  -- mark (the exit is already shifting) - and offers the walked-only and told exits
  -- alike, since a door the server never names is exactly the sort that shifts.
  local map_tab = panel.tabs[1].widgets[2]
  local mr, mc = find_char("@")
  local own = map_tab.onRightClick(mc - 1, mr - 1)
  check(type(own) == "table" and #own == 4 and own[1][1] == "Unmark shifting: s"
        and own[1][2] == "mapg shift 44346 s off",
        "own-square menu offers to unmark an exit already shifting")
end
clear()
cmd("s") ; room(LOBBY10)                 -- and riding it while marked...
check(said("nothing recorded"), "...moves you, says so, and learns nothing")
check(not said("DISAGREES"), "...without disagreeing - disagreement is its nature, not news")
clear() ; mapg("")
check(said("1 exit(s) marked shifting"), "status counts the marks")
clear() ; mapg("shift")
check(said("shifting exit(s)") and said("44346") and said("Elevator"), "bare 'mapg shift' lists them")

-- 70d. Marked, the car belongs to no floor: a link INTO it from a lobby whose way back
--      is the shifting exit is no layout link, so each floor is its own map and the
--      lobbies stop fighting over the cell south of the car. Standing on floor 10 you
--      see floor 10 - the lobby drawn '>' (a way to another map: the elevator door) -
--      and nothing of floors 3 or 7. Routes still enter the car (bfs is not layout).
room('{ "num": 44362, "name": "Floor 10 Elevator Lobby", "area": "Megacity", "exits": { "n": 44346, "e": 44363 } }')
cmd("e") ; room('{ "num": 44363, "name": "Floor 10 Hall", "area": "Megacity", "exits": { "w": 44362 } }')
st = state["plugin.3s-map-gmcp.status"] or ""
check(st:find("2 room(s)", 1, true) and not st:find("displaced", 1, true),
      "a marked elevator splits the floors: floor 10 is a two-room map, nothing displaced")
check(grid():find(">", 1, true) ~= nil, "...with the lobby drawn as a way to another map")
check(not grid():find("!", 1, true), "...and no lobby of another floor pushed into it")
do
  -- Standing IN the lobby it is drawn '@', which hid the door: so the door is drawn in
  -- the direction it lies - a '|' north of '@' and a '>' beyond it, the car's cell.
  cmd("w") ; room('{ "num": 44362, "name": "Floor 10 Elevator Lobby", "area": "Megacity", "exits": { "n": 44346, "e": 44363 } }')
  local rows = grid_rows()
  local r, c = find_char("@")
  check(rows[r - 1]:sub(c, c) == "|" and rows[r - 2]:sub(c, c) == ">",
        "from inside the lobby, the way north into the car is drawn: a link and a '>' where the car lies")
  local map_tab = panel.tabs[1].widgets[2]
  map_tab.onHover(c - 1, r - 3)                       -- 0-based cell of that '>'
  local pk = state["plugin.3s-map-gmcp.peek"] or ""
  check(pk:find("44346", 1, true) and pk:find("Elevator", 1, true) and pk:find("another map", 1, true),
        "hovering the '>' names the room behind the door and says it is another map")
  local menu = map_tab.onRightClick(c - 1, r - 3)
  check(menu and menu[1][2] == "mapg go 44346", "...and right-clicking it offers to walk there")
  -- A told-but-unvisited exit is unexplored by the frontier's rule, and now draws '?' too
  room('{ "num": 44362, "name": "Floor 10 Elevator Lobby", "area": "Megacity", "exits": { "n": 44346, "e": 44363, "s": 44399 } }')
  rows = grid_rows() ; r, c = find_char("@")
  check(rows[r + 1]:sub(c, c) == "|" and rows[r + 2]:sub(c, c) == "?",
        "an exit the server names but nobody has walked draws '?' - the same rule 'mapg frontier' uses")
  room('{ "num": 44362, "name": "Floor 10 Elevator Lobby", "area": "Megacity", "exits": { "n": 44346, "e": 44363 } }')
  cmd("e") ; room('{ "num": 44363, "name": "Floor 10 Hall", "area": "Megacity", "exits": { "w": 44362 } }')
end
clear() ; mapg("maps")
check(said("Megacity #"), "the floors and the car are now separate pieces of Megacity")
clear() ; mapg("path 44346")
check(said("1 step(s)") or said("2 step(s)"), "a route into the car still exists - layout is not routing")
clear() ; mapg("path 44345")
check(said("no known route"), "...and still no route THROUGH it to another floor")
cmd("w") ; room('{ "num": 44362, "name": "Floor 10 Elevator Lobby", "area": "Megacity", "exits": { "n": 44346, "e": 44363 } }')
cmd("n") ; room(LIFT10)                  -- and standing in the car...
st = state["plugin.3s-map-gmcp.status"] or ""
check(st:find("1 room(s)", 1, true), "...the car is a one-room map of its own")
cmd("s") ; room(LOBBY10)                 -- back on the floor for what follows

-- 70c. The farmer's handover (map.query.area) resolves exits through the same rule:
--      the car hands over NO s link, so no fence is drawn across the elevator.
emitted = {}
emit("map.query.area", '{ "area": "Megacity" }')
local ans = emitted_ev("map.area.rooms")
local car
for _, rr in ipairs(ans and ans.rooms or {}) do if rr.num == 44346 then car = rr end end
check(car ~= nil and (car.exits == nil or car.exits.s == nil),
  "map.query.area hands the car over without the shifting exit")
check(car ~= nil and type(car.shift) == "table" and car.shift.s == true,
  "...and WITH the mark, so a listener can fence the way in (the lobby's n) as the layout does")
local lobby
for _, rr in ipairs(ans and ans.rooms or {}) do if rr.num == 44380 then lobby = rr end end
check(lobby ~= nil and lobby.shift == nil, "a room with no marks carries no shift field at all")
emitted = {}
cmd("n") ; room(LIFT7)
local fed = emitted_ev("map.room")
check(fed ~= nil and fed.num == 44346 and type(fed.shift) == "table" and fed.shift.s == true,
  "the map.room feed carries the mark too, on arrival in the car")
-- ...and RESOLVED exits: a withheld destination the store walked is filled in for the
-- listener, a shifting one stays 0 (nothing to hand over), the listing is the server's
emitted = {}
cmd("s") ; room('{ "num": 44380, "name": "Floor 7 Elevator Lobby", "area": "Megacity", "exits": { "n": 0, "e": 0 } }')
fed = emitted_ev("map.room")
check(fed ~= nil and fed.exits and fed.exits.n == 44346 and fed.exits.e == 0,
  "map.room hands over the walked destination of a withheld exit, and 0 where nothing is known")
cmd("n") ; room(LIFT7)
cmd("s") ; room(LOBBY7)

-- 71. The mark persists; 'off' clears it AND the evidence, so the next session and the
--     next ride start clean - and the auto-mark can earn its way back.
disconnect()                             -- forces a save
fresh_client(true) ; load_plugin()       -- same store, fresh session
clear() ; mapg("room 44346")
check(said("shifting: s"), "the mark survives a reload")
clear() ; mapg("shift 44346 s off")
check(said("unmarked"), "'off' clears it by room number and direction")
clear() ; mapg("room 44346")
check(said("no known way out"),
  "unmarking does not resurrect the unlearned link - the mark cleared it for good")
room(LOBBY10)                            -- pick ourselves up somewhere concrete
cmd("n") ; room(LIFT10)
cmd("s") ; room(LOBBY7)                  -- walked s -> 44380 (nothing to contradict: cleared)
cmd("n") ; room(LIFT7)
clear()
cmd("s") ; room(LOBBY3)                  -- FIRST changed ride since the clear
check(said("DISAGREES") and not said("marked SHIFTING"),
  "'off' also cleared the evidence: one changed ride does not re-mark")
cmd("n") ; room(LIFT3)
clear()
cmd("s") ; room(LOBBY10)                 -- second changed ride since the clear
check(said("marked SHIFTING"), "...but two do - the auto-mark earns its way back")

-- 72. The manual mark, for the player who knows an elevator when they see one and
--     does not want to ride it three times first.
fresh_client() ; load_plugin()
room(LOBBY3)
cmd("n") ; room(LIFT3)
cmd("s") ; room(LOBBY3)
cmd("n") ; room(LIFT3)
cmd("s") ; room(LOBBY10)                 -- one DISAGREES is enough for a human
clear() ; mapg("shift 44346 s")
check(said("s from 44346 marked SHIFTING"), "manual mark by room number and direction")
clear() ; mapg("shift 44346 s")
check(said("already marked"), "marking twice says so instead of doing nothing silently")
clear() ; mapg("room 44346")
check(said("shifting: s") and said("no known way out"),
  "the manual mark also unlearns the walked link")
-- The DISAGREES that preceded the mark is spent evidence: mark then unmark, and one
-- changed ride afterwards must NOT be treated as the second strike.
mapg("shift 44346 s off")
cmd("n") ; room(LIFT10)
cmd("s") ; room(LOBBY7)                  -- fresh walked link, nothing contradicted
cmd("n") ; room(LIFT7)
clear()
cmd("s") ; room(LOBBY3)                  -- first changed ride since mark-and-clear
check(said("DISAGREES") and not said("marked SHIFTING"),
  "the pre-mark disagreement does not carry through a mark-and-clear as evidence")

-- 72b. Forms and refusals: this room by bare direction, long names normalised,
--      unknown rooms refused, unknown directions marked with a warning.
fresh_client() ; load_plugin()
room(LIFT3)                              -- standing in the car
clear() ; mapg("shift south")
check(said("s from 44346 marked SHIFTING"), "no number means this room; 'south' means s")
clear() ; mapg("shift 99999 s")
check(said("is not in the store"), "an unknown room is refused")
clear() ; mapg("shift w")
check(said("marking it anyway"), "a direction nothing lists still marks, with a warning")
clear() ; mapg("shift s only")
check(said("mapg shift [room] <dir> [off]"), "a trailing word that is not 'off' is usage, not a guess")
clear() ; mapg("shift 44346 w off")
check(said("w from 44346 unmarked"), "the warned mark clears the same way")

-- ============================================================
-- THE EXPLORER (folded in from mapx, 1.8.0): 'mapg explore all' sweeps until nothing unexplored is reachable.

local X1 = '{ "num": 1, "name": "One",   "area": "Sweep", "exits": { "e": 2 } }'
local X2 = '{ "num": 2, "name": "Two",   "area": "Sweep", "exits": { "w": 1, "e": 3 } }'
local X3 = '{ "num": 3, "name": "Three", "area": "Sweep", "exits": { "w": 2, "n": 0 } }'
local X4 = '{ "num": 4, "name": "Four",  "area": "Sweep", "exits": { "s": 3 } }'

-- 57. The sweep: probe, arrive, probe again - one move per confirmed arrival - and a
--     summary when nothing is left. Covers both kinds of unexplored exit: named-but-unseen
--     (1's e, 2's e) and withheld (3's n).
fresh_client() ; load_plugin()
room(X1)
clear() ; sent = {}
mapg("explore all")
check(#sent == 1 and sent[1] == "e", "the sweep starts through the frontier underfoot")
arrive(X2)
check(#sent == 2 and sent[2] == "e", "...the next probe waits for the arrival, then goes")
arrive(X3)
check(#sent == 3 and sent[3] == "n", "...a withheld destination is probed the same way")
arrive(X4)
check(#sent == 3, "...and with nothing unexplored left, nothing more is sent")
check(said("explore finished"), "the sweep says it finished")
check(said("3 room(s) learned"), "...and how much it learned (started knowing only room 1)")

-- 57b. The probe learned the withheld link by walking it.
clear()
mapg("room 3")
check(said("n     -> 4"), "the walked probe wrote the link the server withheld")

-- 58. Not standing on a frontier: the sweep routes there first, then probes on arrival.
fresh_client() ; load_plugin()
room(X1) ; arrive_ok = true
cmd("e") ; room(X2)
cmd("e") ; room(X3)
cmd("w") ; room(X2)
cmd("w") ; room(X1)                       -- back at 1; the only frontier left is 3's n
clear() ; sent = {}
mapg("explore all")
check(said("walking to 3"), "the sweep routes to the frontier room first")
check(#sent == 1 and sent[1] == "e", "...one step at a time")
arrive(X2)
check(#sent == 2 and sent[2] == "e", "...the walk continues on arrival")
arrive(X3)
check(#sent == 3 and sent[3] == "n", "...and the probe goes out on arriving at the frontier")
arrive(X4)
check(said("explore finished"), "...then the sweep is done")

-- 59. A fight starting stops the sweep, mid-walk and for good.
local function combat(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Char.Combat" then h.fn(json) end end
end
fresh_client() ; load_plugin()
room(X1)
clear() ; sent = {}
mapg("explore all")
check(#sent == 1, "(sweep underway)")
combat('{ "attacker": "a large troll", "attacker_hp": "healthy", "rounds": 1, "target": "you" }')
check(said("combat"), "a combat round starting stops the sweep and says so")
arrive(X2)
check(#sent == 1, "...and the arrival after it sends nothing")
clear()
mapg("explore all")
check(said("exploring everywhere until") == false and said("not starting"),
      "...and it does not start again while the fight is on")
combat('{ "attacker": "", "rounds": 0 }')
clear()
mapg("explore all")
check(said("exploring everywhere until"), "combat over, a fresh 'explore all' is allowed - but only asked-for")

-- 59b. MIP OFF (25 Aug): the fight END is unreliable now. A ZOMBIE terminal - attacker
--      still named, rounds zeroed - is a report, not a fight: a fresh 'explore all' is
--      allowed after it. And with NO terminal at all, the fight flag's shelf life clears
--      it: eight silent seconds and the next asked-for explore starts. Neither path ever
--      RESUMES a sweep on its own - they only stop a dead fight refusing the next one.
fresh_client() ; load_plugin()
room(X1)
mapg("explore all")
combat('{ "attacker": "a large troll", "attacker_hp": "healthy", "rounds": 2, "target": "you" }')
combat('{ "attacker": "a large troll", "attacker_hp": "dead", "rounds": 0, "target": "you" }')
clear()
mapg("explore all")
check(said("exploring everywhere until"), "a zombie terminal does not hold the explore hostage")
mapg("stop")
fresh_client() ; load_plugin()
room(X1)
mapg("explore all")
combat('{ "attacker": "a large troll", "attacker_hp": "healthy", "rounds": 2, "target": "you" }')
clear()
mapg("explore all")
check(said("not starting"), "mid-fight the explore is still refused")
for _ = 1, 9 do tick() end                 -- nine silent seconds outlive the shelf life
clear() ; sent = {}
check(#sent == 0, "silence alone resumes nothing")
mapg("explore all")
check(said("exploring everywhere until"), "...but the next asked-for explore is no longer refused")

-- 60. You moving yourself stops the sweep - and stops it CLEANLY: a fresh 'explore all'
--     starts over rather than saying 'already exploring'.
fresh_client() ; load_plugin()
room(X1)
clear() ; sent = {}
mapg("explore all")
check(#sent == 1, "(sweep underway)")
cmd("s")                                   -- you typed a move of your own
check(said("you moved yourself"), "your own move stops the walk")
check(said("explore stopped with it"), "...and the sweep with it")
room(X2)
check(#sent == 1, "...and the next arrival sends nothing")
clear()
mapg("explore all")
check(said("exploring everywhere until"), "a fresh 'explore all' starts clean, not 'already exploring'")

-- 61. 'mapg stop' stops a sweep even between steps, and cleanly.
fresh_client() ; load_plugin()
room(X1)
mapg("explore all")
cmd(sent[#sent])                           -- our own probe echoing back
clear()
mapg("stop")
check(said("walk stopped") or said("explore stopped"), "'mapg stop' stops the sweep")
room(X2)
check(said("explore finished") == false, "...and the arrival does not resume it")
clear()
mapg("explore all")
check(said("exploring everywhere until"), "...and a fresh start is a fresh start")

-- 62. A SHIFTING exit is never probed. Without the frontier guard the sweep would
--     ride the elevator forever: the exit's destination is nil to link(), so it
--     would look eternally unexplored. Here room 2's 'u' is marked shifting
--     (Joakim's Megacity lift, 28 Aug); the sweep must probe the real frontier
--     (3's e) and then FINISH rather than ever sending 'u'.
local S1 = '{ "num": 1, "name": "One",   "area": "Shaft", "exits": { "e": 2 } }'
local S2 = '{ "num": 2, "name": "Lift",  "area": "Shaft", "exits": { "w": 1, "u": 900, "e": 3 } }'
local S3 = '{ "num": 3, "name": "Three", "area": "Shaft", "exits": { "w": 2, "e": 4 } }'
local S4 = '{ "num": 4, "name": "Four",  "area": "Shaft", "exits": { "w": 3 } }'
fresh_client() ; load_plugin()
room(S1)
cmd("e") ; room(S2)
mapg("shift u")                            -- the player knows a lift when they see one
clear() ; sent = {}
mapg("explore all")
check(#sent == 1 and sent[1] == "e", "the sweep goes for the real frontier, not the lift")
arrive(S3)
check(sent[#sent] == "e", "...keeps sweeping past it")
arrive(S4)
check(said("explore finished"), "...and finishes with the shifting exit still unprobed")
local sent_u = false
for _, s in ipairs(sent) do if s == "u" then sent_u = true end end
check(not sent_u, "at no point did the sweep send the shifting direction")

-- =====================================================================
-- PHASE A (2 Sep 2026, Plan-Map-Farmer-Round2 E1/E3): a blocked probe is a door, not the
-- end of the sweep; and the sweep has an HP floor.
local function fire_trigger(needle, ...)
  for _, t in ipairs(triggers) do
    if t.pattern:find(needle, 1, true) then t.run(...) return true end
  end
  return false
end
-- the watchdog is the one scrye.after the walker arms; fire_timers runs it
local B1 = '{ "num": 1, "name": "One",   "area": "Doors", "exits": { "n": 0, "e": 2 } }'
local B2 = '{ "num": 2, "name": "Two",   "area": "Doors", "exits": { "w": 1 } }'

-- 70. A probe nothing answers (the watchdog fires): the exit is marked blocked, the
--     sweep goes ON to the next frontier instead of dying, and never probes it again.
fresh_client() ; load_plugin()
room(B1)
clear() ; sent = {}
mapg("explore all")
check(#sent == 1 and sent[1] == "n", "the sweep probes the first frontier in compass order (n before e)")
local first = sent[1]
fire_timers()                              -- the watchdog: nothing arrived
check(said(first .. " from 1 is blocked (nothing arrived in 10s)"), "a probe that never lands marks its exit blocked")
check(not said("explore stopped"), "...and the sweep is NOT stopped by it")
check(#sent == 2 and sent[2] ~= first, "...it goes straight on to the other frontier (" .. table.concat(sent, ",") .. ")")
arrive(B2)
check(said("explore finished"), "...and finishes with the blocked exit left alone")
local again = 0
for _, c in ipairs(sent) do if c == first then again = again + 1 end end
check(again == 1, "the blocked exit was probed exactly once")
clear()
mapg("blocked")
check(said("1 blocked exit(s)") and said(first), "'mapg blocked' lists it")
clear()
mapg("frontier")
check(said("no unexplored exits") or not said("1      One"), "a blocked exit is not frontier")
mapg("blocked clear")
clear()
mapg("frontier")
check(said("One"), "'mapg blocked clear' makes it frontier again")

-- 70b. Blocked marks are session-only: nothing about them reaches the store.
fresh_client() ; load_plugin()
room(B1) ; sent = {}
mapg("explore all") ; fire_timers()
mapg("save")
check(not tostring(shared["rooms"] or store["rooms"] or ""):find("blocked", 1, true), "blocked marks are never saved")

-- 71. 'You cannot go' during a probe is the same door, found at once.
fresh_client() ; load_plugin()
room(B1)
clear() ; sent = {}
mapg("explore all")
local d1 = sent[1]
check(fire_trigger("You cannot go"), "the refusal trigger exists")
check(said(d1 .. " from 1 is blocked ('You cannot go')"), "the server's refusal blocks the exit without waiting for the watchdog")
check(#sent == 2, "...and the sweep moves on at once")

-- 71b. During a REQUESTED walk the refusal stops the walk - a walk that failed is a
--      walk that failed, exactly as the watchdog would have said ten seconds later.
fresh_client() ; load_plugin()
room(X1) ; arrive_ok = true
cmd("e") ; room(X2) ; cmd("w") ; room(X1)
clear() ; sent = {}
mapg("go 2")
check(#sent == 1, "a requested walk steps out")
fire_trigger("You cannot go")
check(said("Walk stopped where you stand"), "...and the refusal stops it")
check(emitted_ev("map.walk.stopped") ~= nil, "...with the contract's stopped event")

-- 72. THE HP FLOOR. Default 50%: under it a sweep stops at the next decision, and a
--     sweep already walking stops on the tick the wound shows.
fresh_client() ; load_plugin()
state["char.vitals.hp"], state["char.vitals.maxhp"] = "300", "1000"
room(X1)
clear() ; sent = {}
mapg("explore all")
check(#sent == 0 and said("HP 30% is under the explore floor of 50%"), "a sweep does not start under the floor")
check(said("explore stopped"), "...and says it stopped")
state["char.vitals.hp"] = "800"
clear() ; sent = {}
mapg("explore all")
check(#sent == 1, "fit again, it sweeps")
state["char.vitals.hp"] = "400"           -- wounded mid-probe
tick()
check(said("HP 40% is under the explore floor of 50%") and said("stopped"), "a wound mid-walk stops the sweep on the tick")
arrive(X2)
check(#sent == 1, "...and nothing more is sent on arrival")

-- 72b. The floor is set, persisted, and can be turned off; MIP is the fallback feed.
fresh_client() ; load_plugin()
clear() ; mapg("explore hp")
check(said("now 50%"), "the default floor is 50%")
mapg("explore hp 20")
fresh_client(true) ; load_plugin()
clear() ; mapg("explore hp")
check(said("now 20%"), "the floor survives a reload")
state["character.health.current"], state["character.health.max"] = "10", "100"
room(X1) ; clear() ; sent = {}
mapg("explore all")
check(#sent == 0 and said("HP 10% is under the explore floor of 20%"), "MIP health is the fallback feed")
mapg("explore hp 0")
clear() ; sent = {}
mapg("explore all")
check(#sent == 1, "with the floor off, a 10% character sweeps (their call)")

-- 72c. No feed at all: the sweep runs and says the floor cannot be checked.
fresh_client() ; load_plugin()
room(X1) ; clear() ; sent = {}
mapg("explore all")
check(#sent == 1 and said("no HP feed has spoken yet"), "no feed: it sweeps and says the floor is unchecked")

-- =====================================================================
-- PHASE B (Plan-Map-Farmer-Round2 E2/E5/E6): the fence, the budget, the panel.

-- 73. THE FENCE. 'mapg explore area' probes every frontier of the area you stand in, and a
--     probe that lands in ANOTHER area steps straight back, marks the exit as an edge, and
--     never probes from the other side: room 3 (Woods) has its own unexplored exit and it
--     is left alone.
local A1 = '{ "num": 1, "name": "Gate",   "area": "Farm",  "exits": { "n": 0, "e": 2 } }'
local A2 = '{ "num": 2, "name": "Field",  "area": "Farm",  "exits": { "w": 1, "e": 3 } }'
local A3 = '{ "num": 3, "name": "Edge",   "area": "Woods", "exits": { "w": 2, "e": 0 } }'
local C9 = '{ "num": 9, "name": "Hole",   "area": "Cave",  "exits": { "s": 1 } }'
fresh_client() ; load_plugin()
room(A1)
clear() ; sent = {}
mapg("explore area")
check(said("exploring 'Farm' until"), "'explore area' names the fence")
check(#sent == 1 and sent[1] == "n", "(the first probe goes north)")
arrive(C9)
check(said("n from 1 leads into 'Cave' - the fence is here; stepping back s"), "a probe into another area is named and reversed")
check(sent[#sent] == "s", "...the step back is the opposite compass direction")
arrive(A1)
check(sent[#sent] == "e", "...and back inside, the sweep goes on to the next frontier")
arrive(A2)
check(sent[#sent] == "e", "(probing east from 2)")
arrive(A3)
check(sent[#sent] == "w", "the Woods is out of the fence: straight back west")
arrive(A2)
check(said("explore finished"), "with no Farm frontier left the sweep finishes")
check(said("2 edge(s) found"), "...and counts both edges")
local probed_from_3 = false
for i = 1, #sent do if sent[i] == "e" and i > 5 then probed_from_3 = true end end
check(not probed_from_3 and #sent == 5, "room 3's own unexplored exit was never probed: sent " .. table.concat(sent, " "))
check(state["plugin.3s-map-gmcp.explore"]:find("explore finished  -  +3 room(s), 0 blocked, 2 edge(s)", 1, true) ~= nil,
      "the panel carries the summary: " .. tostring(state["plugin.3s-map-gmcp.explore"]))

-- 73b. Edges are persisted with the room, shown by 'mapg room', and a later fenced sweep
--      skips them without a probe - while 'explore all' still ignores the fence entirely.
clear() ; mapg("room 1")
check(said("edge: n -> Cave"), "'mapg room' shows the edge and the area behind it")
mapg("save")
check(tostring(shared["rooms"] or store["rooms"] or ""):find('"edge"', 1, true) ~= nil, "edges are saved")
fresh_client(true) ; load_plugin()
room(A1)
clear() ; mapg("room 2")
check(said("edge: e -> Woods"), "...and the edge is still there after a reload")
clear() ; sent = {}
mapg("explore area")
check(#sent == 0 and said("explore finished"), "a fenced sweep from the gate has nothing left: edges are not frontier")
clear() ; sent = {}
mapg("explore all")
check(said("exploring everywhere until"), "'explore all' is the unfenced form")
check(said("walking to 3"), "...and it heads for the Woods' unexplored exit the fence kept out")

-- 73c. A non-compass door out of the fence has no way back: the exit is marked and the
--      sweep routes itself back inside through known links, then goes on.
local D1 = '{ "num": 1, "name": "Yard",   "area": "Farm", "exits": { "in": 0, "e": 2 } }'
local D2 = '{ "num": 2, "name": "Field",  "area": "Farm", "exits": { "w": 1, "e": 0 } }'
local D5 = '{ "num": 5, "name": "Shop",   "area": "Town", "exits": { "out": 1 } }'
local D6 = '{ "num": 6, "name": "Far",    "area": "Farm", "exits": { "w": 2 } }'
fresh_client() ; load_plugin()
room(D1) ; arrive_ok = true
cmd("e") ; room(D2) ; cmd("w") ; room(D1)   -- 1<->2 known; 1's 'in' and 2's e are frontier
clear() ; sent = {}
mapg("explore area")
check(sent[1] == "in", "(the only frontier underfoot is the door)")
arrive(D5)
check(said("in from 1 leads into 'Town' - the fence is here, and there is no compass way back; routing"),
      "a non-compass door out of the fence is marked, and the sweep re-routes")
check(said("walking to 2") and sent[#sent] == "out", "...back through the known link toward the fence's last frontier")
arrive(D1)
check(sent[#sent] == "e", "(the route continues)")
arrive(D2)
check(sent[#sent] == "e", "...and the probe goes out on arrival")
arrive(D6)
check(said("explore finished") and said("1 edge(s) found"), "finished, the door counted as an edge")

-- 74. THE BUDGET. 'explore all 2' stops after two new rooms and says so.
fresh_client() ; load_plugin()
room(X1)
clear() ; sent = {}
mapg("explore all 2")
check(said("or 2 new room(s) are learned"), "the budget is announced")
arrive(X2) ; arrive(X3)
check(#sent == 2 and said("budget of 2 new room(s) reached"), "two rooms learned: the sweep stops, nothing more is sent")
check(state["plugin.3s-map-gmcp.explore"]:find("explore stopped (budget of 2 new room(s) reached)", 1, true) ~= nil,
      "...and the panel says why")
fresh_client() ; load_plugin()
room(X1) ; clear() ; sent = {}
mapg("explore area 1")
arrive(X2)
check(#sent == 1 and said("budget of 1 new room(s) reached"), "'explore area N' takes a budget too")

-- 75. THE PANEL. A live line while sweeping, an Explore button that starts a fenced sweep,
--     and the Stop button ends it through the same summary.
fresh_client() ; load_plugin()
room(X1) ; clear() ; sent = {}
local function map_button(label)
  for _, w in ipairs(panel.tabs[1].widgets) do
    if w.type == "buttonrow" then
      for _, b in ipairs(w.buttons) do if b.text == label then return b.action end end
    end
  end
end
check(map_button("Explore") ~= nil, "an Explore button sits on the map tab")
map_button("Explore")()
check(said("exploring 'Sweep' until") and #sent == 1, "...and it starts a FENCED sweep of the area underfoot")
local line = state["plugin.3s-map-gmcp.explore"] or ""
check(line:find("EXPLORING Sweep", 1, true) ~= nil and line:find("+0 room(s)", 1, true) ~= nil,
      "while sweeping the panel line says what and how far: " .. line)
arrive(X2)
line = state["plugin.3s-map-gmcp.explore"] or ""
check(line:find("+1 room(s)", 1, true) ~= nil, "...and counts rooms as they come: " .. line)
check(line:find("1 unexplored", 1, true) ~= nil, "...with the frontier count (only 2's e is left; 3 is unknown): " .. line)
clear()
map_button("Stop")()
check(said("explore stopped: walk stopped"), "the Stop button ends the sweep through the summary")
check((state["plugin.3s-map-gmcp.explore"] or ""):find("explore stopped (walk stopped)", 1, true) ~= nil,
      "...which the panel shows")
local found = false
for _, w in ipairs(panel.tabs[1].widgets) do if w.type == "label" and w.bind == "plugin.3s-map-gmcp.explore" then found = true end end
check(found, "the explore line is its own label on the map tab")

-- 75b. The frontier count is cached - counted once per map change, not per draw - and a
--      map change recounts it: a new room with its own frontier raises it from 1 to 2.
local E1 = '{ "num": 1, "name": "Cross", "area": "Sweep", "exits": { "n": 0, "e": 0 } }'
local E7 = '{ "num": 7, "name": "North", "area": "Sweep", "exits": { "s": 1, "e": 0 } }'
fresh_client() ; load_plugin()
room(E1) ; sent = {}
mapg("explore area")
local before = state["plugin.3s-map-gmcp.explore"]
check(before:find("1 unexplored", 1, true) ~= nil, "(one room offers a frontier: " .. before .. ")")
for _ = 1, 3 do tick() end                 -- draws, no map change
check(state["plugin.3s-map-gmcp.explore"] == before, "(redraws leave the line as it was)")
arrive(E7)
check((state["plugin.3s-map-gmcp.explore"] or ""):find("2 unexplored", 1, true) ~= nil,
      "a new room with a frontier of its own is counted: " .. tostring(state["plugin.3s-map-gmcp.explore"]))

-- 75c. Blocked probes are counted in the summary too.
fresh_client() ; load_plugin()
room(B1) ; sent = {}
mapg("explore all") ; fire_timers()        -- n never lands
arrive(B2)
check(said("1 exit(s) blocked"), "a probe the watchdog gave up on is counted in the summary")

-- 76. 'Unknown' is warned about, not refused.
local U1 = '{ "num": 40, "name": "A Vortex", "area": "Unknown", "exits": { "s": 0 } }'
fresh_client() ; load_plugin()
room(U1) ; clear() ; sent = {}
mapg("explore area")
check(said("this area is 'Unknown'") and #sent == 1, "a fenced sweep of 'Unknown' warns and goes on")

-- 77. Help and a bad form.
clear() ; mapg("help")
check(said("mapg explore area [N]"), "help lists the fenced form")

-- ============================================================
-- 78. THE OVERLAYS (20 Sep). A route lights the rooms it runs through; Room.Map's sight
-- marks paint the rooms the server shows monsters or players in; the farmer's published
-- route lights its next leg. A line world: 1 -n- 2 -e- 3, with 5 east of 3 and 6 east of 5.
-- ============================================================
do
  local function line_world5()
    fresh_client() ; load_plugin()
    room('{ "num": 1, "name": "One",   "area": "Test", "exits": { "n": 2 } }')
    cmd("n") ; room('{ "num": 2, "name": "Two",   "area": "Test", "exits": { "s": 1, "e": 3 } }')
    cmd("e") ; room('{ "num": 3, "name": "Three", "area": "Test", "exits": { "w": 2, "e": 5 } }')
    cmd("e") ; room('{ "num": 5, "name": "Five",  "area": "Test", "exits": { "w": 3, "e": 6 } }')
    cmd("e") ; room('{ "num": 6, "name": "Six",   "area": "Test", "exits": { "w": 5 } }')
    cmd("w") ; room('{ "num": 5, "name": "Five",  "area": "Test", "exits": { "w": 3, "e": 6 } }')
    cmd("w") ; room('{ "num": 3, "name": "Three", "area": "Test", "exits": { "w": 2, "e": 5 } }')
    cmd("w") ; room('{ "num": 2, "name": "Two",   "area": "Test", "exits": { "s": 1, "e": 3 } }')
    cmd("s") ; room('{ "num": 1, "name": "One",   "area": "Test", "exits": { "n": 2 } }')
    sent = {}
  end
  local function tile(dr, dc)
    local mr, mc = find_char("@")
    return (grid_rows()[mr + dr] or ""):sub(mc + dc, mc + dc)
  end
  -- a shown route: 'mapg path 6' lights 2 (next step), 3 and 5 (the way), 6 (the end)
  line_world5()
  mapg("path 6")
  check(tile(-2, 0) == "s" and tile(-2, 2) == "o" and tile(-2, 4) == "o" and tile(-2, 6) == "t",
        "'mapg path' lights the route: next step, the way, the end")
  check(status_line():find("route: 4 step(s) to Six", 1, true) ~= nil, "...and the status line counts it")
  check((state["plugin.3s-map-gmcp.legend"] or ""):find("next step", 1, true) ~= nil, "the legend explains the route tiles")
  -- it is for where you stood: a move clears it
  cmd("n") ; room('{ "num": 2, "name": "Two", "area": "Test", "exits": { "s": 1, "e": 3 } }')
  check(tile(0, 2) == "#" and tile(0, 6) == "#", "a move clears a shown route")
  -- 'mapg stop' clears one too, without a move
  mapg("path 6") ; check(tile(0, 2) == "s", "lit again")
  clear() ; mapg("stop")
  check(tile(0, 2) == "#" and said("route cleared"), "'mapg stop' clears a shown route")

  -- a walk keeps the remaining route lit and moves the 's' along
  line_world5()
  mapg("go 6")
  check(tile(-2, 0) == "s" and tile(-2, 6) == "t", "a walk lights its route from the first step")
  check(status_line():find("walking: 4 step(s) to Six", 1, true) ~= nil, "...and the status says it is walking")
  cmd(sent[#sent]) ; room('{ "num": 2, "name": "Two", "area": "Test", "exits": { "s": 1, "e": 3 } }')
  check(tile(0, 2) == "s" and tile(0, 4) == "o" and tile(0, 6) == "t", "after one step the next room is the next step")
  check(status_line():find("walking: 3 step(s)", 1, true) ~= nil, "...and the count came down")
  cmd(sent[#sent]) ; room('{ "num": 3, "name": "Three", "area": "Test", "exits": { "w": 2, "e": 5 } }')
  cmd(sent[#sent]) ; room('{ "num": 5, "name": "Five",  "area": "Test", "exits": { "w": 3, "e": 6 } }')
  cmd(sent[#sent]) ; room('{ "num": 6, "name": "Six",   "area": "Test", "exits": { "w": 5 } }')
  check(tile(0, -2) == "#" and not status_line():find("walking", 1, true), "arrived: nothing lit, nothing counted")

  -- the sight marks: the server's grid, walked from '@' along the store's exits
  line_world5()
  local function room_map(rows)
    for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Map" then h.fn(rows) end end
  end
  room_map('{ "rows": [ "O-m-p", "|    ", "@    " ] }')
  check(tile(-2, 0) == "#" and tile(-2, 2) == "m" and tile(-2, 4) == "p",
        "Room.Map's 'm' and 'p' cells are drawn on the rooms the store names for them")
  check(tile(-2, 6) == "#", "a room the grid does not show is untouched")
  -- the marks beat a route on the same rooms: the fact the route was chosen for
  mapg("path 6")
  check(tile(-2, 2) == "m" and tile(-2, 4) == "p" and tile(-2, 6) == "t", "sight marks are drawn over a route")
  -- they are for the room you saw them from: a move clears them until the next grid
  cmd("n") ; room('{ "num": 2, "name": "Two", "area": "Test", "exits": { "s": 1, "e": 3 } }')
  check(tile(0, 2) == "#" and tile(0, 4) == "#", "a move clears the sight marks")
  -- a grid the store cannot walk (no '@') is ignored
  room_map('{ "rows": [ "m-m" ] }')
  check(tile(0, 2) == "#", "a grid without '@' paints nothing")
  -- and a hop is only taken over a link glyph: an 'm' with no line to it is not reached
  room_map('{ "rows": [ "@ m" ] }')
  check(tile(0, 2) == "#", "no link glyph between the cells, no hop - the 'm' beyond is not painted")
  room_map('{ "rows": [ "@-m" ] }')
  check(tile(0, 2) == "m", "...with the glyph it is")
  check((state["plugin.3s-map-gmcp.legend"] or ""):find("monsters in sight", 1, true) ~= nil, "the legend explains the sight marks")

  -- the farmer's route: the state it publishes lights its leg, and clears with it
  line_world5()
  state["plugin.3s-farmer.route"] = "2,3,5"
  for _, w in ipairs(watch_hooks) do if w.prefix == "plugin.3s-farmer.route" then w.fn("2,3,5", w.prefix) end end
  check(tile(-2, 0) == "s" and tile(-2, 2) == "o" and tile(-2, 4) == "t", "the farmer's published route is lit")
  check(status_line():find("farmer's route", 1, true) ~= nil, "...and the status says whose it is")
  state["plugin.3s-farmer.route"] = ""
  for _, w in ipairs(watch_hooks) do if w.prefix == "plugin.3s-farmer.route" then w.fn("", w.prefix) end end
  check(tile(-2, 0) == "#" and tile(-2, 4) == "#", "...and clears when the farmer clears it")
  -- a rest room the farmer publishes is drawn 'R'
  state["plugin.3s-farmer.roomrules"] = "3:rest"
  mapg("redraw")
  check(tile(-2, 2) == "R", "a farmer rest room is drawn 'R'")
  check((state["plugin.3s-map-gmcp.legend"] or ""):find("farmer: rests", 1, true) ~= nil, "...and the legend says so")
  state["plugin.3s-farmer.roomrules"] = nil
end

-- ============================================================
-- 79. THE DETAILS LINE (20 Sep). A click on any room composes it: what the room is, its
-- ways out, how often you stood in it, what Room.Contents last showed there, with links
-- that walk, light the route, or print the room. It follows the store (a redraw
-- recomposes it) and the roster (a new Room.Contents for that room updates it).
-- ============================================================
do
  fresh_client() ; load_plugin()
  room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2, "e": 0 } }')
  cmd("n") ; room('{ "num": 2, "name": "Two", "area": "Test", "exits": { "s": 1 } }')
  local function contents(json)
    for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Contents" then h.fn(json) end end
  end
  contents('{ "items": [ { "name": "Small cur", "type": "monster" }, { "name": "Small cur", "type": "monster" }, { "name": "Bob", "type": "player" } ] }')
  cmd("s") ; room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2, "e": 0 } }')
  local map_tab = panel.tabs[1].widgets[2]
  local mr, mc = find_char("@")
  check(map_tab.onClick ~= nil, "the grid answers clicks")
  clear()
  map_tab.onClick(mc - 1, mr - 3)              -- room 2, north of us
  local d = state["plugin.3s-map-gmcp.detail"] or ""
  check(d:find("@{accent,bold}2 Two@{}", 1, true) ~= nil, "the details line names the room clicked")
  check(d:find("exits s", 1, true) ~= nil and d:find("seen 1x", 1, true) ~= nil, "...its exits and how often you stood there")
  check(d:find("last there: @{#B36BFF}2 Small cur@{}, @{success}Bob@{}", 1, true) ~= nil,
        "...and what Room.Contents last showed there, mobs counted, players named")
  check(d:find("click=mapg go 2}[Walk]", 1, true) ~= nil and d:find("click=mapg path 2}[Route]", 1, true) ~= nil
        and d:find("click=mapg room 2}[Details]", 1, true) ~= nil, "...with links that walk, light the route, print the room")
  check(said("1 -> 2"), "the click still prints (and lights) the route")
  local dw = panel.tabs[1].widgets
  local has_text = false
  for _, w in ipairs(dw) do if w.type == "text" and w.bind == "plugin.3s-map-gmcp.detail" then has_text = true end end
  check(has_text, "the details line is a text widget (markup, so the links are real)")
  -- your own room: no walk/route links
  map_tab.onClick(mc - 1, mr - 1)
  d = state["plugin.3s-map-gmcp.detail"] or ""
  check(d:find("1 One", 1, true) ~= nil and not d:find("[Walk]", 1, true) and d:find("[Details]", 1, true) ~= nil,
        "the room you stand in gets no walk link, still the details one")
  check(d:find("unexplored e", 1, true) ~= nil, "...and its unexplored exit is named")
  -- a room with no roster seen says nothing about one
  check(not d:find("last there", 1, true), "no Room.Contents seen for a room, no 'last there' line")
  -- the roster line follows a new roster for the room shown
  contents('{ "items": [ ] }')
  d = state["plugin.3s-map-gmcp.detail"] or ""
  check(d:find("last there: nothing", 1, true) ~= nil, "an empty roster for the room shown updates its line")
  -- the '@' in a mob's name cannot be read as markup
  contents('{ "items": [ { "name": "a @{error}trick", "type": "monster" } ] }')
  d = state["plugin.3s-map-gmcp.detail"] or ""
  check(d:find("a @@{error}trick", 1, true) ~= nil, "a name is escaped in the markup")
  -- 23 Sep: items carry a count, and a crowded room arrives paged - later pages add
  contents('{ "items": [ { "type": "item", "count": 6, "name": "The corpse of Wiremouth" } ], "full": 1, "pages": 3, "page": 1 }')
  contents('{ "items": [ { "type": "monster", "count": 2, "name": "Wiremouth guard" }, { "type": "player", "count": 1, "name": "Bob" } ], "full": 1, "pages": 3, "page": 2 }')
  contents('{ "items": [ { "type": "item", "count": 1, "name": "A truss" } ], "full": 1, "pages": 3, "page": 3 }')
  d = state["plugin.3s-map-gmcp.detail"] or ""
  check(d:find("last there: @{#B36BFF}2 Wiremouth guard@{}, @{success}Bob@{}", 1, true) ~= nil,
        "a count of 2 is two, and page 2's roster survives page 3 (" .. d .. ")")
  contents('{ "items": [ { "type": "monster", "count": 1, "name": "Rat" } ], "entry": 1, "full": 1 }')
  d = state["plugin.3s-map-gmcp.detail"] or ""
  check(d:find("last there: @{#B36BFF}Rat@{}", 1, true) ~= nil, "an unpaged list starts the room afresh")
end

-- ============================================================
-- 80. PLACES (20 Sep). 'mapg place <name>' bookmarks the room you stand in (or '<name>
-- <n>' any room); 'mapg go <name>' and 'mapg path <name>' take the name; the Places tab
-- lists them (click walks, right-click offers the usual and 'Remove'); the map marks
-- them; they persist, shared like the store, and a forgotten room's place goes with it.
-- ============================================================
do
  fresh_client() ; load_plugin()
  room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2 } }')
  cmd("n") ; room('{ "num": 2, "name": "Bank", "area": "Test", "exits": { "s": 1, "n": 3 } }')
  cmd("n") ; room('{ "num": 3, "name": "Vault", "area": "Test", "exits": { "s": 2 } }')
  clear() ; mapg("place bank 2")
  check(said("place 'bank': 2 Bank"), "'mapg place <name> <n>' bookmarks that room")
  clear() ; mapg("place vault")
  check(said("place 'vault': 3 Vault"), "'mapg place <name>' bookmarks the room you stand in")
  clear() ; mapg("place 12")
  check(said("needs a name, not a number"), "a numeric name is refused (it would collide with room numbers)")
  clear() ; mapg("places")
  check(said("2 place(s)") and said("bank") and said("vault"), "'mapg places' lists them")
  -- the tab
  local tab = panel.tabs[5]
  check(tab and tab.title == "Places", "a Places tab")
  local list = state["plugin.3s-map-gmcp.placelist"] or ""
  check(list:find("bank\t2 Bank\t", 1, true) ~= nil and list:find("vault\t3 Vault\t", 1, true) ~= nil, "...listing name, room and map")
  local tbl = tab.widgets[3]
  local menu = tbl.onRowMenu(nil, 1)
  check(menu and menu[4][1] == "Remove place" and menu[4][2] == "mapg unplace bank", "a row's menu removes it by typed command")
  -- the map marks a place
  local mr, mc = find_char("@")
  check(grid_rows()[mr + 2]:sub(mc, mc) == "b", "a place is marked on the map")
  check((state["plugin.3s-map-gmcp.legend"] or ""):find("a place", 1, true) ~= nil, "...and the legend says so")
  -- hovering it names the place
  panel.tabs[1].widgets[2].onHover(mc - 1, mr + 1)
  check((state["plugin.3s-map-gmcp.peek"] or ""):find("place: bank", 1, true) ~= nil, "hovering a place names it")
  -- go / path by name
  cmd("s") ; room('{ "num": 2, "name": "Bank", "area": "Test", "exits": { "s": 1, "n": 3 } }')
  cmd("s") ; room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2 } }')
  clear() ; sent = {}
  mapg("path vault")
  check(said("1 -> 3") and said("2 step(s): n n"), "'mapg path <place>' routes by name")
  mapg("go bank")
  check(said("walking to 2 Bank (bank)") and sent[1] == "n", "'mapg go <place>' walks by name")
  mapg("stop")
  -- a row click walks there
  sent = {} ; clear()
  tbl.onRowClick(nil, 2)
  check(said("walking to") and sent[1] == "n", "a Places row click walks there")
  mapg("stop")
  -- the box names the room you stand in, through the typed command
  clear()
  tab.widgets[2].onSubmit("home")
  check(said("place 'home': 1 One"), "the box bookmarks the room you stand in under the name typed")
  -- renaming a place moves it; unplace drops it
  clear() ; mapg("place bank 3")
  check(said("place 'bank' moved to 3 Vault"), "a place named again moves to the new room")
  clear() ; mapg("unplace bank")
  check(said("place 'bank' removed") and not (state["plugin.3s-map-gmcp.placelist"] or ""):find("bank", 1, true), "'mapg unplace' drops it")
  clear() ; mapg("unplace nowhere")
  check(said("no place called 'nowhere'"), "...and an unknown name says so")
  -- persistence: the store carries the places; a reload brings them back
  mapg("save")
  local raw = store_get and store_get() or nil
  fresh_client(true) ; load_plugin()
  check((state["plugin.3s-map-gmcp.placelist"] or ""):find("vault", 1, true) ~= nil
        or (function() mapg("places") return said("vault") end)(), "places survive a reload")
  -- a forgotten room takes its place with it
  room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2 } }')
  clear() ; mapg("forget 3")
  mapg("places")
  check(not said("vault"), "a forgotten room's place is gone")
end

-- ============================================================
-- 81. FIND AND DOORS (20 Sep). 'mapg find <text>' lights every room named like it and
-- lists them on the Find tab (click walks); the Doors table lists every way off the map
-- you stand on, and a click walks through.
-- ============================================================
do
  fresh_client() ; load_plugin()
  room('{ "num": 1, "name": "Market Square", "area": "Town", "exits": { "n": 2, "e": 3 } }')
  cmd("n") ; room('{ "num": 2, "name": "Market Lane",  "area": "Town", "exits": { "s": 1 } }')
  cmd("s") ; room('{ "num": 1, "name": "Market Square", "area": "Town", "exits": { "n": 2, "e": 3 } }')
  cmd("e") ; room('{ "num": 3, "name": "Forest Gate",  "area": "Woods", "exits": { "w": 1, "e": 4 } }')
  cmd("e") ; room('{ "num": 4, "name": "Deep Market",  "area": "Woods", "exits": { "w": 3 } }')
  cmd("w") ; room('{ "num": 3, "name": "Forest Gate",  "area": "Woods", "exits": { "w": 1, "e": 4 } }')
  cmd("w") ; room('{ "num": 1, "name": "Market Square", "area": "Town", "exits": { "n": 2, "e": 3 } }')
  clear() ; mapg("find market")
  check(said("3 room(s) named like 'market'"), "'mapg find' counts the rooms named like the text")
  local tab = panel.tabs[4]
  check(tab and tab.title == "Find", "a Find tab")
  local list = state["plugin.3s-map-gmcp.findlist"] or ""
  check(list:find("4 Deep Market\tWoods", 1, true) ~= nil and list:find("2 Market Lane\tTown", 1, true) ~= nil,
        "...listing each hit with its map, across maps")
  check(list:find("1 Market Square\tTown\there", 1, true) ~= nil, "...and marks the one you stand in")
  local mr, mc = find_char("@")
  check(grid_rows()[mr - 2]:sub(mc, mc) == "f", "a hit is lit on the map")
  check((state["plugin.3s-map-gmcp.legend"] or ""):find("mapg find", 1, true) ~= nil, "...and the legend says so")
  -- a row click walks there (to the room, across maps)
  sent = {} ; clear()
  local tbl = tab.widgets[3]
  -- rows are alphabetical by name: Deep Market, Market Lane, Market Square
  tbl.onRowClick(nil, 1)
  check(said("walking to 4 Deep Market") and sent[1] == "e", "a Find row click walks to that room")
  mapg("stop")
  -- the box is the same search; blank clears
  tab.widgets[2].onSubmit("lane")
  check((state["plugin.3s-map-gmcp.findlist"] or ""):find("Market Lane", 1, true) ~= nil
        and not (state["plugin.3s-map-gmcp.findlist"] or ""):find("Deep Market", 1, true), "the box searches too")
  clear() ; tab.widgets[2].onSubmit("")
  check(said("find cleared") and grid_rows()[mr - 2]:sub(mc, mc) == "#", "a blank search clears the lights")
  clear() ; mapg("find zzz")
  check(said("no room named like 'zzz'"), "no hit says so")
  mapg("find")
  -- the Doors table: Town's one way out is east from Market Square into Forest Gate [Woods]
  local doors = state["plugin.3s-map-gmcp.doorlist"] or ""
  check(doors:find("Woods\tMarket Square e\tForest Gate", 1, true) ~= nil, "the Doors table lists the way off this map: to, from, lands in (" .. doors .. ")")
  sent = {} ; clear()
  tab.widgets[5].onRowClick(nil, 1)
  check(said("walking to 3 Forest Gate [Woods]") and sent[1] == "e", "a Doors row click walks through")
  mapg("stop")
  local menu = tab.widgets[5].onRowMenu(nil, 1)
  check(menu and menu[1][2] == "mapg go 3", "...and its menu offers the typed walk")
  -- from the Woods, the door is the way back
  cmd("e") ; room('{ "num": 3, "name": "Forest Gate",  "area": "Woods", "exits": { "w": 1, "e": 4 } }')
  doors = state["plugin.3s-map-gmcp.doorlist"] or ""
  check(doors:find("Town\tForest Gate w\tMarket Square", 1, true) ~= nil, "on the other map the same door reads the other way")
end

-- ============================================================
-- 82. A LINK IN A DIRECTION THE ROOM HAS NO EXIT FOR (23 Sep). Joakim's floor 30: the
-- elevator car (listing 's' only) had "walked" n into the floor-30 lobby - a mis-paired
-- move - which joined the car to the floor, seeded its layout from the car and pulled
-- it apart. Such a link is never learned, never read, and is dropped from a saved store.
-- ============================================================
do
  -- never learned: a move in a direction the room does not list is out of step
  fresh_client() ; load_plugin()
  room('{ "num": 1, "name": "Car", "area": "Tower", "exits": { "s": 0 } }')
  cmd("n")
  clear()
  room('{ "num": 2, "name": "Lobby", "area": "Tower", "exits": { "n": 1, "s": 3 } }')
  check(said("1 has no exit 'n'") and said("nothing learned"), "a move the room has no exit for learns nothing, and says so")
  mapg("save")
  local st = scrye.json.decode(shared["rooms"])
  local car
  for _, r in ipairs(st.rooms) do if r.num == 1 then car = r end end
  check(car and (car.walked == nil or car.walked.n == nil), "...nothing is written")
  -- a room with NO listing still learns (nothing to contradict)
  fresh_client() ; load_plugin()
  room('{ "num": 5, "name": "Dark", "area": "Cave", "exits": { } }')
  cmd("n")
  room('{ "num": 6, "name": "Darker", "area": "Cave", "exits": { "s": 5 } }')
  mapg("save")
  st = scrye.json.decode(shared["rooms"])
  local dark
  for _, r in ipairs(st.rooms) do if r.num == 5 then dark = r end end
  check(dark and dark.walked and dark.walked.n == 6, "a room with no listing keeps what it learned")
  -- dropped from a saved store on load, and never read: the car stays out of the floor
  fresh_client()
  shared["rooms"] = scrye.json.encode({ ver = 2, rooms = {
    { num = 1, name = "Car", area = "Tower", exits = { s = 0 }, walked = { n = 2 }, shift = { s = true }, visits = 3 },
    { num = 2, name = "Lobby", area = "Tower", exits = { n = 1, s = 3 }, walked = { n = 1, s = 3 }, visits = 3 },
    { num = 3, name = "Hall", area = "Tower", exits = { n = 2 }, walked = { n = 2 }, visits = 3 },
  } })
  clear()
  load_plugin()
  check(said("dropped 1 learned link(s)"), "a saved link the room has no exit for is dropped on load, and said once")
  room('{ "num": 3, "name": "Hall", "area": "Tower", "exits": { "n": 2 } }')
  check(status_line():find("2 room(s)", 1, true) ~= nil and not status_line():find("displaced", 1, true),
        "the car is its own map again: the floor is two rooms, nothing displaced (" .. status_line() .. ")")
  -- and never read: a link learned while the room listed that exit stops counting the
  -- moment its listing drops it (a door that closed for good)
  fresh_client() ; load_plugin()
  room('{ "num": 7, "name": "Gate", "area": "Keep", "exits": { "e": 0 } }')
  cmd("e")
  room('{ "num": 8, "name": "Yard", "area": "Keep", "exits": { "w": 0 } }')
  cmd("w")
  room('{ "num": 7, "name": "Gate", "area": "Keep", "exits": { "s": 9 } }')     -- 'e' no longer listed
  room('{ "num": 7, "name": "Gate", "area": "Keep", "exits": { "s": 9 } }')
  clear() ; mapg("path 8")
  local routed = said("step(s): e")
  check(not routed, "a learned link whose direction the listing no longer names is not routed through")
end


-- ================= export and import (1.12.0, host API 1.21) =================
do
  exports_files, exports_order = {}, {}
  fresh_client() ; load_plugin()
  room('{ "num": 101, "name": "Square", "area": "Midgard", "exits": { "e": 102, "u": 105 } }')
  cmd("e")
  room('{ "num": 102, "name": "Smithy", "area": "Midgard", "exits": { "w": 101, "in": 104 } }')
  cmd("w")
  room('{ "num": 101, "name": "Square", "area": "Midgard", "exits": { "e": 102, "u": 105 } }')
  mapg("place home 101")
  clear() ; mapg("export")
  local json_name, svg_name
  for _, n in ipairs(exports_order) do
    if n:match("^3s%-map%-all%-%d+%-%d+%.json$") then json_name = n end
    if n:match("^3s%-map%-midgard%-%d+%-%d+%.svg$") then svg_name = n end
  end
  check(json_name ~= nil, "export: the whole map as 3s-map-all-<stamp>.json")
  check(svg_name ~= nil, "export: a picture of this map as 3s-map-<map>-<stamp>.svg")
  check(said("2 room(s) exported") and said("mapg import " .. tostring(json_name)), "export: says how many and how to import it")
  local data = decode(exports_files[json_name or ""] or "")
  check(data and data.format == "scrye-map" and #data.rooms == 2, "export: the file is a scrye-map with both rooms")
  check(data and data.places and data.places[1] and data.places[1].name == "home", "export: places go with the rooms")
  local svg = exports_files[svg_name or ""] or ""
  check(svg:find("<svg", 1, true) and svg:find("</svg>", 1, true), "svg: a whole SVG document")
  check(svg:find("<title>101 Square</title>", 1, true) ~= nil, "svg: every room hovers with its number and name")
  check(svg:find('class="here', 1, true) ~= nil, "svg: where you stand is marked")
  check(select(2, svg:gsub('class="link"', "")) == 1, "svg: one line for the one link, not one per direction")
  check(svg:find('class="stair"', 1, true) and svg:find('class="door"', 1, true), "svg: stairs and doors are marked")
  check(svg:find(">home<", 1, true) ~= nil, "svg: places are labelled")

  -- an area on its own: data only
  local before = #exports_order
  clear() ; mapg("export midgard")
  check(#exports_order == before + 1 and exports_order[#exports_order]:match("^3s%-map%-midgard%-.*%.json$"),
        "export <area>: that area's rooms, no picture")
  clear() ; mapg("export Atlantis")
  check(said("no rooms carry the area 'Atlantis'") and #exports_order == before + 1, "export <area>: an unknown area writes nothing")
  clear() ; mapg("export svg")
  check(exports_order[#exports_order]:match("%.svg$") and not said("room(s) exported"), "export svg: the picture alone")

  -- import into someone else's map: add, never overwrite
  local shared_file = exports_files[json_name]
  fresh_client() ; load_plugin()
  room('{ "num": 102, "name": "Forge", "area": "Midgard", "exits": { "w": 101, "n": 103 } }')
  clear() ; mapg("import")
  check(said(json_name), "import with no file: lists the map files")
  clear() ; mapg("import " .. json_name)
  check(said("1 new room(s)"), "import: the room they had and I did not comes in (" .. last_line() .. ")")
  check(said("1 kept as you had them"), "import: the room I had is kept")
  mapg("save")
  local st = decode(shared["rooms"])
  local mine, theirs
  for _, r in ipairs(st.rooms) do if r.num == 102 then mine = r end ; if r.num == 101 then theirs = r end end
  check(mine and mine.name == "Forge" and mine.exits.n == 103, "import: my name and my exits for a room I have are untouched")
  check(mine and mine.walked and mine.walked.w == 101, "import: ...but it learns the link they walked, in a direction it lists")
  check(theirs and theirs.name == "Square" and theirs.visits == 0, "import: a new room arrives whole, unvisited")
  check(said("1 place(s)"), "import: their place comes in where I have none of that name")

  -- a file from outside is data: checked, clipped, and a bad entry skipped
  exports_files["bad.json"] = encode({ format = "scrye-map", ver = 1, rooms = {
    { num = "x" }, { num = -4 },
    { num = 900, name = "Evil\n\27[31mroom", area = "Nowhere", exits = { ["../up"] = 5, n = "nine", s = 901 } },
  } })
  clear() ; mapg("import bad.json")
  check(said("1 new room(s)") and said("2 unreadable entries skipped"), "import: bad entries are counted and skipped")
  mapg("save")
  st = decode(shared["rooms"])
  local evil
  for _, r in ipairs(st.rooms) do if r.num == 900 then evil = r end end
  check(evil and not evil.name:find("\n", 1, true) and not evil.name:find("\27", 1, true), "import: control characters never reach the store")
  check(evil and evil.exits.s == 901 and evil.exits.n == nil and evil.exits["../up"] == nil, "import: only well-formed exits are kept")

  exports_files["notes.json"] = '{ "hello": "world" }'
  clear() ; mapg("import notes.json")
  check(said("is not a map exported by 'mapg export'"), "import: a JSON file that is not a map is refused")
  clear() ; mapg("import missing.json")
  check(said("could not read missing.json"), "import: a missing file says so")
  exports_files["future.json"] = encode({ format = "scrye-map", ver = 9, rooms = {} })
  clear() ; mapg("import future.json")
  check(said("newer mapper"), "import: a newer format is refused, not half-read")

  -- a map name comes in only where I have none
  exports_files["named.json"] = encode({ format = "scrye-map", ver = 1,
    rooms = { { num = 300, name = "Hall", area = "Keep", exits = { e = 301 } },
              { num = 301, name = "Gate", area = "Keep", exits = { w = 300 } } },
    names = { { seed = 300, name = "Theirs" } } })
  fresh_client() ; load_plugin()
  room('{ "num": 300, "name": "Hall", "area": "Keep", "exits": { "e": 301 } }')
  mapg("name Mine")
  clear() ; mapg("import named.json")
  mapg("save")
  st = decode(shared["rooms"])
  check(st.names and st.names[1] and st.names[1].name == "Mine" and not said("map name(s)"),
        "import: my own name for a map is kept over theirs")
  fresh_client() ; load_plugin()
  room('{ "num": 300, "name": "Hall", "area": "Keep", "exits": { "e": 301 } }')
  clear() ; mapg("import named.json")
  check(said("1 map name(s)"), "import: ...and theirs comes in where I had none")

  -- an older Scrye: no scrye.exports at all
  fresh_client() ; scrye.exports = nil ; load_plugin()
  clear() ; mapg("export")
  check(said("needs plugin API 1.21"), "export on an older Scrye: says what is missing")
  clear() ; mapg("import x.json")
  check(said("needs plugin API 1.21"), "import on an older Scrye: the same")
  check(#sent == 0, "export and import send nothing to the MUD")
end

-- ============================================================
-- 6 Oct 2026: links added by hand. A door the room does not list ("climb rope") is never
-- learned by walking; 'mapg link' names it, and the router and walker use it.
do
  fresh_client() ; load_plugin()
  local A  = '{ "exits": { "e": 501 }, "area": "Idavallen", "name": "Rope foot", "num": 500 }'
  local B  = '{ "exits": { "w": 500 }, "area": "Idavallen", "name": "Lane", "num": 501 }'
  local C  = '{ "exits": { "n": 601 }, "area": "Angarboda", "name": "Cliff top", "num": 600 }'
  local D  = '{ "exits": { "s": 600 }, "area": "Angarboda", "name": "Hall", "num": 601 }'
  room(A) ; cmd("e") ; room(B) ; cmd("w") ; room(A)
  cmd("look") ; cmd("climb rope") ; room(C)
  cmd("n") ; room(D) ; cmd("s") ; room(C)
  -- before the link: no way from Idavallen up to the cliff
  clear() ; cmd("climb rope") ; room(C)       -- (stand at C again: the hop is C->C, no move)
  room(A)                                     -- teleported back to A by hand, nothing typed
  clear() ; mapg("path 600")
  check(said("no known route") and said("mapg link"), "no route through an unlisted door, and the hint names 'mapg link'")
  clear() ; mapg("link")
  check(said("nothing typed took you"), "'mapg link' after an arrival nothing typed caused says so")

  -- go through the door by hand, then link it
  cmd("climb rope") ; room(C)
  clear() ; mapg("link")
  check(said("linked: 500") and said("'climb rope'") and said("600"), "'mapg link' links the last move with the last command")
  room(A)                                     -- back (by some way we do not model)
  sent = {} ; clear() ; mapg("go 601")
  check(sent[1] == "climb rope", "the walker takes the hand-added door (" .. tostring(sent[1]) .. ")")
  cmd("climb rope")                           -- the walker's own command echoing back
  check(not said("you moved yourself"), "...and its own command does not stop it")
  room(C)
  check(sent[#sent] == "n", "...then walks on from the room the door leads to")
  cmd("n") ; room(D)
  check(said("arrived"), "...and arrives")

  -- a door of several commands, named by number, one way
  clear() ; mapg("link 601 500 say home; n")
  check(said("linked: 601"), "'mapg link <from> <to> <command>' links any two rooms")
  sent = {} ; clear() ; mapg("go 500")
  check(sent[1] == "say home" and sent[2] == "n", "a ';' door sends each part (" .. table.concat(sent, "|") .. ")")
  cmd("say home") ; cmd("n")
  check(not said("you moved yourself"), "...none of its parts stop the walk")
  room(B)                                     -- passes through a room on the way
  check(not said("Walk stopped"), "...a room on the way is waited through")
  room(A)
  check(said("arrived"), "...and it arrives where the link says")

  -- refused: client commands, and rooms the store does not know
  clear() ; mapg("link 500 600 /lua x()")
  check(said("not '/' or '.'"), "a link cannot send a client command")
  clear() ; mapg("link 500 999 jump")
  check(said("not in the store"), "a link to an unknown room is refused")

  -- listed, shown on the room, persisted
  clear() ; mapg("links")
  check(said("2 hand-added link(s)") and said("climb rope") and said("say home; n"), "'mapg links' lists them")
  clear() ; mapg("here")
  check(said("added by hand"), "'mapg here' marks a hand-added link")
  mapg("save")
  fresh_client(true) ; load_plugin()
  room(A)
  sent = {} ; mapg("go 600")
  check(sent[1] == "climb rope", "hand-added links survive a reload")

  -- the panel's button
  -- (a bound row since 1.18.0: its labels are state, so the fast-walk button can say on/off)
  local found = false
  for _, tab in ipairs(panel.tabs or {}) do
    for _, w in ipairs(tab.widgets or {}) do
      if w.type == "buttonrow" and w.bind then
        local first = tostring(state[w.bind] or ""):match("^([^\n]*)")
        if first == "Link last move" then found = true end
      end
    end
  end
  check(found, "the Map tab has a 'Link last move' button")

  -- export carries them; import takes them only when asked
  mapg("export")
  local file
  for i = #exports_order, 1, -1 do if exports_order[i]:match("%.json$") then file = exports_order[i] break end end
  fresh_client() ; load_plugin()
  room(A) ; cmd("e") ; room(B) ; cmd("w") ; room(A)   -- a store that knows nothing of the door
  clear() ; mapg("import " .. file)
  check(said("hand-added link(s) - commands the walker would send") and said("climb rope"),
        "import lists a file's hand-added links instead of taking them")
  clear() ; mapg("links")
  check(said("no hand-added links"), "...and has not taken them")
  clear() ; mapg("import " .. file .. " links")
  check(said("hand-added link(s) taken"), "'mapg import <file> links' takes them")
  sent = {} ; mapg("go 600")
  check(sent[1] == "climb rope", "...and the walker uses them")

  -- unlink
  clear() ; mapg("unlink 500 climb rope")
  check(said("unlinked 'climb rope'"), "'mapg unlink <room> <command>' drops one")
  clear() ; mapg("path 600")
  check(said("no known route"), "...and the route is gone with it")
end

-- ============================================================
-- SECTIONS (1.14.0, Joakim 26 Oct 2026: the Ant hole). One level can be several places that
-- do not join on that level: the west wing one floor down and the east wing one floor down,
-- each reached by its own stairs. A level shows only the part(s) nearest yours by stairs.
-- ============================================================
do
  local ANT = {
    [1] = '{ "num": 1, "name": "Ant hole entrance", "area": "Ant hole", "exits": { "w": 2, "e": 3 } }',
    [2] = '{ "num": 2, "name": "West wing",         "area": "Ant hole", "exits": { "e": 1, "d": 4 } }',
    [3] = '{ "num": 3, "name": "East wing",         "area": "Ant hole", "exits": { "w": 1, "d": 5 } }',
    [4] = '{ "num": 4, "name": "West tunnel",       "area": "Ant hole", "exits": { "u": 2, "n": 6, "d": 9 } }',
    [6] = '{ "num": 6, "name": "West chamber",      "area": "Ant hole", "exits": { "s": 4 } }',
    [5] = '{ "num": 5, "name": "East tunnel",       "area": "Ant hole", "exits": { "u": 3, "n": 7, "d": 8 } }',
    [7] = '{ "num": 7, "name": "East chamber",      "area": "Ant hole", "exits": { "s": 5 } }',
    [8] = '{ "num": 8, "name": "East nest",         "area": "Ant hole", "exits": { "u": 5 } }',
    [9] = '{ "num": 9, "name": "West nest",         "area": "Ant hole", "exits": { "u": 4 } }',
  }
  local function walk_ant(path) for _, n in ipairs(path) do room(ANT[n]) end end
  local function rooms_drawn() return select(2, grid():gsub("[#%^v%%>!@]", "")) end
  -- the whole hole explored: entrance, west wing down and down, back, east wing down and down
  local function explored()
    fresh_client() ; load_plugin()
    walk_ant({ 1, 2, 4, 6, 4, 9, 4, 2, 1, 3, 5, 8, 5, 7 })     -- ends in the east chamber
  end
  explored()
  check(rooms_drawn() == 2, "in the east wing's lower floor only the east wing is drawn (" .. rooms_drawn() .. " rooms)")
  check(status_line():find("1 other section(s) of this level hidden", 1, true) ~= nil,
        "...and the status says one part of the level is hidden (" .. status_line() .. ")")
  -- the west chamber would be two rooms west of us: nothing there to hover or click
  local map_tab = panel.tabs[1].widgets[2]
  local mr, mc = find_char("@")
  map_tab.onHover(mc - 1 - 4, mr - 1)
  check((state["plugin.3s-map-gmcp.peek"] or "") == "", "a hidden section cannot be hovered")
  -- every part, on request - and remembered
  clear() ; mapg("sections all")
  check(said("sections: all"), "'mapg sections all' says so")
  check(rooms_drawn() == 4, "...and draws the west wing's floor too (" .. rooms_drawn() .. " rooms)")
  check(not status_line():find("hidden", 1, true), "...with nothing hidden")
  map_tab.onHover(mc - 1 - 4, mr - 1)
  check((state["plugin.3s-map-gmcp.peek"] or ""):find("West chamber", 1, true) ~= nil, "...where the west chamber can be hovered")
  check(store["sections"] == "all", "...and the choice is kept")
  disconnect()                                           -- the map is saved with it
  fresh_client(true) ; load_plugin()
  walk_ant({ 7 })
  check(rooms_drawn() == 4, "...across a reload")
  clear() ; mapg("sections near")
  check(said("sections: near") and rooms_drawn() == 2, "'mapg sections near' goes back to the nearest part")
  clear() ; mapg("sections")
  check(said("sections: near"), "'mapg sections' says which it is")
  clear() ; mapg("sections sideways")
  check(said("mapg sections all|near"), "...and a wrong word gets the usage")
  -- from the entrance, the level below shows BOTH wings: one flight of stairs each
  explored()
  walk_ant({ 5, 3, 1 })
  check(rooms_drawn() == 3, "the entrance level is one section: all three rooms")
  mapg("level down")
  check(rooms_drawn() == 4 and not status_line():find("hidden", 1, true),
        "from the entrance, the floor below shows both wings (" .. rooms_drawn() .. " rooms)")
  -- from the east wing's floor, the floor below that shows the east nest, not the west one
  explored()
  walk_ant({ 5 })
  mapg("level down")
  check(rooms_drawn() == 1, "from the east wing, the floor below is the east nest alone (" .. rooms_drawn() .. ")")
  check(status_line():find("1 other section(s) of this level hidden", 1, true) ~= nil, "...the west nest hidden")
  -- A one-way trapdoor (the west wing's floor has no way back up) still makes the floors
  -- near - from either side - and a section that is only reached the long way round stays
  -- hidden. The east chamber has stairs up into a little loft of its own, on the entrance
  -- level but not joined to the entrance on it.
  ANT[4] = '{ "num": 4, "name": "West tunnel", "area": "Ant hole", "exits": { "n": 6, "d": 9 } }'
  ANT[7] = '{ "num": 7, "name": "East chamber", "area": "Ant hole", "exits": { "s": 5, "u": 10 } }'
  ANT[10] = '{ "num": 10, "name": "Loft", "area": "Ant hole", "exits": { "d": 7 } }'
  local function trapdoor()
    fresh_client() ; load_plugin()
    walk_ant({ 1, 2, 4, 6, 4, 9, 4 })                  -- down the trapdoor into the west wing
    walk_ant({ 1, 3, 5, 8, 5, 7, 10, 7, 5, 3, 1 })     -- (back up by other means) the east side
  end
  trapdoor()
  mapg("level down")
  check(rooms_drawn() == 4, "a one-way trapdoor down still puts the west wing one flight below the entrance (" .. rooms_drawn() .. ")")
  trapdoor()
  walk_ant({ 2, 4 })
  mapg("level up")
  check(rooms_drawn() == 3, "...and from below, the entrance one flight up (" .. rooms_drawn() .. ")")
  check(status_line():find("1 other section(s) of this level hidden", 1, true) ~= nil,
        "...with the loft, three flights round, hidden (" .. status_line() .. ")")
  -- help
  clear() ; mapg("help")
  check(said("mapg sections all|near"), "help lists 'mapg sections'")
end

-- ============================================================
-- NOTES (1.15.0): your own words about a room - typed, from the note box, or from a
-- room's right-click menu; shown on hover, in the details line, beside where you stand,
-- and marked 'n' on the map. Kept with the map; never exported.
-- ============================================================
do
  fresh_client() ; load_plugin()
  room('{ "num": 1, "name": "One", "area": "Test", "exits": { "n": 2 } }')
  cmd("n") ; room('{ "num": 2, "name": "Two", "area": "Test", "exits": { "s": 1, "n": 3 } }')
  cmd("n") ; room('{ "num": 3, "name": "Three", "area": "Test", "exits": { "s": 2 } }')
  cmd("s") ; room('{ "num": 2, "name": "Two", "area": "Test", "exits": { "s": 1, "n": 3 } }')
  local map_tab = panel.tabs[1].widgets[2]
  local box
  for _, w in ipairs(panel.tabs[1].widgets) do if w.bind == "plugin.3s-map-gmcp.notebox" then box = w end end
  check(box and box.type == "input" and box.onSubmit, "the Map tab has a note box")
  -- typed, on the room you stand in
  clear() ; mapg("note lever behind the throne: pull")
  check(said("note on 2 Two: lever behind the throne: pull"), "'mapg note <text>' writes on the room you stand in")
  check((state["plugin.3s-map-gmcp.where"] or ""):find("note: lever behind the throne", 1, true) ~= nil,
        "...and it shows beside where you stand")
  check(state["plugin.3s-map-gmcp.notebox"] == "lever behind the throne: pull", "...and in the note box")
  clear() ; mapg("note")
  check(said("note on 2 Two: lever"), "'mapg note' reads it back")
  -- any room, by number; marked 'n' on the map, named on hover
  clear() ; mapg("note #1 the guard hits hard")
  check(said("note on 1 One: the guard hits hard"), "'mapg note #<room> <text>' writes on any room")
  local rows = grid_rows()
  local mr, mc = find_char("@")
  check(rows[mr + 2]:sub(mc, mc) == "n", "a room with a note is drawn 'n'")
  map_tab.onHover(mc - 1, mr + 1)
  check((state["plugin.3s-map-gmcp.peek"] or ""):find("note: the guard hits hard", 1, true) ~= nil, "...and hovering it shows the note")
  check((state["plugin.3s-map-gmcp.legend"] or ""):find("has a note", 1, true) ~= nil, "the legend explains 'n'")
  -- the right-click menu aims the box at the room: a menu cannot ask for text
  local menu = map_tab.onRightClick(mc - 1, mr + 1)
  local edit
  for _, e in ipairs(menu) do if e[2] == "mapg note #1" then edit = e[1] end end
  check(edit == "Edit note...", "the menu offers to edit a note that exists")
  menu = map_tab.onRightClick(mc - 1, mr - 3)        -- room 3, no note
  local add
  for _, e in ipairs(menu) do if e[2] == "mapg note #3" then add = e[1] end end
  check(add == "Add note...", "...and to add one where there is none")
  clear() ; mapg("note #3")
  check(said("3 Three - no note yet; type in the note box"), "picking it says where to type")
  check((state["plugin.3s-map-gmcp.detail"] or ""):find("3 Three", 1, true) ~= nil, "...and the details line is about that room")
  check(state["plugin.3s-map-gmcp.notebox"] == "", "...with the box empty, ready")
  box.onSubmit("  chest under the bed  ")
  check(said("note on 3 Three: chest under the bed"), "the box writes on the room the details line is about")
  check((state["plugin.3s-map-gmcp.detail"] or ""):find("note:@{} chest under the bed", 1, true) ~= nil,
        "...and the details line shows it")
  -- the box is not wiped by a redraw while you type, only when the room it is about changes
  state["plugin.3s-map-gmcp.notebox"] = "half-typed"
  mapg("redraw")
  check(state["plugin.3s-map-gmcp.notebox"] == "half-typed", "a redraw leaves what you are typing alone")
  clear() ; mapg("note #1")
  check(state["plugin.3s-map-gmcp.notebox"] == "the guard hits hard", "aiming it at another room fills it with that room's note")
  -- an empty box removes the note; '-' typed does too
  box.onSubmit("")
  check(said("note removed from 1 One"), "an empty box removes the note")
  clear() ; mapg("note #1 -")
  check(said("no note on 1 One"), "...and there is then nothing to remove")
  clear() ; mapg("note #99 hello")
  check(said("room 99 is not in the store"), "a room the store does not have gets no note")
  -- a cap on length
  clear() ; mapg("note " .. string.rep("x", 250))
  check(said("(kept the first 200 characters)"), "a note longer than 200 characters is cut, saying so")
  clear() ; mapg("note #2")
  check(said("its note: " .. string.rep("x", 200)) and not said(string.rep("x", 201)), "...to exactly 200")
  check((shared["rooms"] or ""):find(string.rep("x", 200), 1, true) ~= nil, "a note is saved at once, not at the next flush")
  -- the list
  clear() ; mapg("notes")
  check(said("2 note(s):") and said("2 Two [Test]: xxxx") and said("3 Three [Test]: chest under the bed"),
        "'mapg notes' lists every room with a note")
  -- kept with the map, across a restart
  disconnect()
  fresh_client(true) ; load_plugin()
  room('{ "num": 2, "name": "Two", "area": "Test", "exits": { "s": 1, "n": 3 } }')
  check(state["plugin.3s-map-gmcp.notebox"] == string.rep("x", 200), "with no room clicked, the box is about the room you stand in")
  cmd("n") ; room('{ "num": 3, "name": "Three", "area": "Test", "exits": { "s": 2 } }')
  check(state["plugin.3s-map-gmcp.notebox"] == "chest under the bed", "...and follows you into the next")
  clear() ; mapg("note #3")
  check(said("its note: chest under the bed"), "notes are saved with the map")
  -- ...but never exported: they are yours, not the map's
  clear() ; mapg("export")
  local file = exports_files[exports_order[#exports_order - 1] or ""] or ""
  for _, name in ipairs(exports_order) do if name:match("%.json$") then file = exports_files[name] end end
  check(file:find("Three", 1, true) ~= nil, "(the export has the rooms)")
  check(file and not file:find("chest under the bed", 1, true), "an export carries no notes")
  -- a long list says how many more
  for i = 1, 45 do
    room('{ "num": ' .. (500 + i) .. ', "name": "Cell", "area": "Jail", "exits": { } }')
    mapg("note cell " .. i)
  end
  clear() ; mapg("notes")
  check(said("47 note(s):") and said("... and 7 more"), "'mapg notes' lists forty, then says how many more")
  -- help
  clear() ; mapg("help")
  check(said("mapg note <text>") and said("mapg notes"), "help lists the note commands")
end

-- ============================================================
-- THE GAP (1.16.0): no route? Where the map's knowledge breaks. Everything you can reach,
-- everything known to lead to the target, and the rooms between: a room on your side the
-- far side leads INTO (the way back exists, almost surely - it was never walked), and the
-- nearest unexplored exits on each side. Printed with links, lit, listed on the Find tab.
-- ============================================================
do
  -- The keep: carried from the tower down into the hall once; the hall's way north - the
  -- way back up - never walked.
  local function keep(hall)
    fresh_client() ; load_plugin()
    room('{ "num": 3, "name": "Tower", "area": "Keep", "exits": { "s": 2 } }')
    cmd("s") ; room(hall or '{ "num": 2, "name": "Hall", "area": "Keep", "exits": { "s": 1, "n": 0, "e": 0 } }')
    cmd("s") ; room('{ "num": 1, "name": "Gate", "area": "Keep", "exits": { "n": 2, "w": 0 } }')
    clear()
  end
  keep()
  mapg("go 3")
  check(said("no known route to 3 Tower - the gap"), "no route says it is showing the gap")
  check(said("you can reach 2 room(s) from here; no other known room leads to it"),
        "...how much of the map is on each side")
  check(said("likeliest break: @{accent,click=mapg go 2}2 Hall@{} - @{accent,click=mapg go 3}3 Tower@{}'s s leads into it, and its n has never been walked - walk n from 2 once"),
        "...the room where it breaks, why, and what to walk - with links that walk there")
  check(said("unexplored on your side, nearest first: @{accent,click=mapg go 1}1 Gate@{} (w), @{accent,click=mapg go 2}2 Hall@{} (n,e)"),
        "...and the unexplored exits nearest you")
  check(#sent == 0, "...and it walks nothing")
  check(not said("nothing the map knows leads INTO it"), "...and does not claim nothing leads in, with a break in hand")
  -- lit and listed
  check((state["plugin.3s-map-gmcp.findhint"] or ""):find("the gap to 3 Tower", 1, true) ~= nil,
        "the Find tab is about the gap")
  local list = state["plugin.3s-map-gmcp.findlist"] or ""
  check(list:find("1 Gate", 1, true) and list:find("2 Hall", 1, true) and not list:find("Tower", 1, true),
        "...listing the rooms to work from")
  local rows = grid_rows()
  local mr, mc = find_char("@")
  check(rows[mr - 2]:sub(mc, mc) == "f", "...lit on the map")
  local findtab
  for _, t in ipairs(panel.tabs) do if t.title == "Find" then findtab = t end end
  sent = {}
  findtab.widgets[3].onRowClick("2 Hall", 2)
  check(sent[1] == "n", "a click on a gap room walks there (" .. tostring(sent[1]) .. ")")
  mapg("stop")
  clear() ; mapg("find")
  check(not (state["plugin.3s-map-gmcp.findhint"] or ""):find("gap", 1, true), "'mapg find' clears the gap off the Find tab")

  -- a door the room does not show
  keep('{ "num": 2, "name": "Hall", "area": "Keep", "exits": { "s": 1 } }')
  mapg("path 3")
  check(said("but it lists no n - a door it does not show? go by hand to 3, then 'mapg link'"),
        "a room that lists no way back: a hidden door, and the cure")
  -- one way only
  keep('{ "num": 2, "name": "Hall", "area": "Keep", "exits": { "s": 1, "n": 4 } }')
  room('{ "num": 4, "name": "Chapel", "area": "Keep", "exits": { } }')
  room('{ "num": 1, "name": "Gate", "area": "Keep", "exits": { "n": 2, "w": 0 } }')
  clear() ; mapg("path 3")
  check(said("but its n is known to lead to 4, not 3 - one way only?"), "a way back that leads elsewhere: one way only")
  -- shifting, and shut
  keep()
  mapg("shift 2 n")
  clear() ; mapg("path 3")
  check(said("but its n is marked shifting - never routed through"), "a shifting way back says so")
  keep('{ "num": 2, "name": "Hall", "area": "Keep", "exits": { "s": 1, "n": 0 } }')
  room('{ "num": 2, "name": "Hall", "area": "Keep", "exits": { "s": 1, "n": 0 } }')
  sent = {} ; mapg("explore all")
  check(sent[1] == "n", "(the sweep probes the hall's n)")
  fire_trigger("You cannot go") ; mapg("stop")
  clear() ; mapg("path 3")
  check(said("but its n was found shut this session"), "a way back found shut this session says so")
  -- walked, and the listing names no exits at all: the walk is what the map knows
  fresh_client() ; load_plugin()
  room('{ "num": 3, "name": "Tower", "area": "Keep", "exits": { "s": 2 } }')
  cmd("s") ; room('{ "num": 2, "name": "Hall", "area": "Keep", "exits": { } }')
  cmd("n") ; room('{ "num": 4, "name": "Chapel", "area": "Keep", "exits": { "s": 2 } }')
  clear() ; mapg("path 3")
  check(said("but its n is known to lead to 4, not 3"), "a way back only ever walked counts as known")
  -- a way in that is not a compass direction
  fresh_client() ; load_plugin()
  room('{ "num": 3, "name": "Tower", "area": "Keep", "exits": { "out": 2 } }')
  cmd("out") ; room('{ "num": 2, "name": "Hall", "area": "Keep", "exits": { "s": 1 } }')
  cmd("s") ; room('{ "num": 1, "name": "Gate", "area": "Keep", "exits": { "n": 2 } }')
  clear() ; mapg("path 3")
  check(said("'s out leads into it, but the way back is not known"), "a way in like 'out' has no compass way back to name")
  -- nothing leads in at all: arrived by a way the map could not read
  fresh_client() ; load_plugin()
  room('{ "num": 1, "name": "Gate", "area": "Keep", "exits": { "n": 0 } }')
  room('{ "num": 9, "name": "Oubliette", "area": "Keep", "exits": { } }')          -- teleported in
  room('{ "num": 1, "name": "Gate", "area": "Keep", "exits": { "n": 0 } }')
  clear() ; mapg("path 9")
  check(said("no other known room leads to it"), "a room nothing leads to says so")
  check(said("nothing the map knows leads INTO it"), "...and what that means")
  -- unexplored on the far side
  fresh_client() ; load_plugin()
  room('{ "num": 1, "name": "Gate", "area": "Keep", "exits": { } }')
  room('{ "num": 5, "name": "Crypt", "area": "Keep", "exits": { "w": 6 } }')
  cmd("w") ; room('{ "num": 6, "name": "Tomb", "area": "Keep", "exits": { "e": 5, "up": 0 } }')
  room('{ "num": 1, "name": "Gate", "area": "Keep", "exits": { } }')
  clear() ; mapg("path 5")
  check(said("1 other known room(s) lead to it"), "rooms known to lead to the target are counted")
  check(not said("nothing the map knows leads INTO it"), "...so it does not say nothing leads in")
  check(said("unexplored on its side, nearest first: @{accent,click=mapg go 6}6 Tomb@{} (up)"),
        "the unexplored exits on the target's side are named too")
  check(said("nothing on either side") == false, "(there is something to try)")
  -- nothing to try anywhere
  fresh_client() ; load_plugin()
  room('{ "num": 1, "name": "Gate", "area": "Keep", "exits": { } }')
  room('{ "num": 7, "name": "Island", "area": "Keep", "exits": { } }')
  room('{ "num": 1, "name": "Gate", "area": "Keep", "exits": { } }')
  clear() ; mapg("path 7")
  check(said("nothing on either side is left") and said("'mapg link'"), "with nothing to try, it says so - and names 'mapg link'")

  -- A bigger map, to pin the order of things. Your side: a corridor 1-2-3-4 north, with
  -- unexplored w at 1 and 2 and e at 4. The far side, by how far each is from the target
  -- 20: 21 (1), 22 and 24 (2), 23 (3), 25 (4) - and the links from it into your side:
  -- 20 w>3 (cost 2), 22 w>2 and 24 w>2 (3 each), 25 w>1 and 21 w>4 (4 each).
  fresh_client() ; load_plugin()
  local W = {
    '{ "num": 20, "name": "Throne", "area": "Keep", "exits": { "w": 3 } }',
    '{ "num": 21, "name": "Antechamber", "area": "Keep", "exits": { "s": 20, "w": 4, "e": 0 } }',
    '{ "num": 22, "name": "Gallery", "area": "Keep", "exits": { "s": 21, "w": 2 } }',
    '{ "num": 24, "name": "Loggia", "area": "Keep", "exits": { "s": 21, "w": 2 } }',
    '{ "num": 23, "name": "Stair", "area": "Keep", "exits": { "s": 22 } }',
    '{ "num": 25, "name": "Garret", "area": "Keep", "exits": { "s": 23, "w": 1, "n": 0 } }',
    '{ "num": 4, "name": "Ward", "area": "Keep", "exits": { "s": 3, "e": 0 } }',
    '{ "num": 3, "name": "Yard", "area": "Keep", "exits": { "s": 2, "n": 4 } }',
    '{ "num": 2, "name": "Hall", "area": "Keep", "exits": { "s": 1, "n": 3, "w": 0 } }',
    '{ "num": 1, "name": "Gate", "area": "Keep", "exits": { "n": 2, "w": 0 } }',
  }
  for _, j in ipairs(W) do room(j) end
  clear() ; mapg("path 20")
  local breaks = {}
  for _, l in ipairs(printed) do
    local a = l:match("likeliest break: @{accent,click=mapg go (%d+)}")
    if a then breaks[#breaks + 1] = a end
  end
  check(table.concat(breaks, ",") == "3,2,1", "the breaks come cheapest first, each room once, three at most ("
        .. table.concat(breaks, ",") .. ")")
  check(said("@{accent,click=mapg go 20}20 Throne@{}'s w leads into it"), "...the cheapest named with the room that leads in")
  check(said("unexplored on your side, nearest first: @{accent,click=mapg go 1}1 Gate@{} (w), @{accent,click=mapg go 2}2 Hall@{} (w), @{accent,click=mapg go 4}4 Ward@{} (e)"),
        "your side's unexplored exits, nearest first")
  check(said("unexplored on its side, nearest first: @{accent,click=mapg go 21}21 Antechamber@{} (e), @{accent,click=mapg go 25}25 Garret@{} (n)"),
        "...and the target's side, nearest the target first")
  check(said("5 other known room(s) lead to it"), "...and the count of rooms leading to it")
  check((state["plugin.3s-map-gmcp.findlist"] or ""):find("3 Yard", 1, true) ~= nil,
        "a break with nothing unexplored in it is still listed to walk to")
  -- from the far end of the corridor, nearest first is the other way round
  room(W[7])                                             -- standing in 4 Ward
  clear() ; mapg("gap 20")
  check(said("unexplored on your side, nearest first: @{accent,click=mapg go 4}4 Ward@{} (e), @{accent,click=mapg go 2}2 Hall@{} (w), @{accent,click=mapg go 1}1 Gate@{} (w)"),
        "nearest first means nearest to where you stand")

  -- 'mapg gap', and a favourite that cannot be reached (the case it was asked for)
  keep()
  clear() ; mapg("gap 3")
  check(said("likeliest break"), "'mapg gap <room>' asks directly")
  clear() ; mapg("gap 2")
  check(said("there is a known route to 2 Hall"), "...and says so when there is a route")
  clear() ; mapg("gap nowhere")
  check(said("mapg gap <room | place>"), "...and gives the usage for a room it does not know")
  mapg("fav 3")
  local favtab
  for _, t in ipairs(panel.tabs) do if t.title == "Favs" then favtab = t end end
  clear()
  favtab.widgets[3].onRowClick("3 Tower", 1)
  check(said("likeliest break") and said("walk n from 2 once"), "clicking a favourite you cannot reach shows the gap")
  -- a whole map, from the Maps tab: every room of it is the target, so a room of that
  -- map leading to another is not "another room leading to it"
  fresh_client() ; load_plugin()
  room('{ "num": 20, "name": "Isle",  "area": "Aland", "exits": { "e": 0 } }')
  room('{ "num": 21, "name": "Cove",  "area": "Aland", "exits": { "s": 20 } }')
  room('{ "num": 30, "name": "Shore", "area": "Bland", "exits": { "w": 0 } }')
  clear()
  panel.tabs[2].widgets[3].onRowClick("Aland", 1)
  check(said("no known route to the map Aland"), "a map you cannot reach shows the gap to the whole map")
  check(said("no other known room leads to it"), "...the whole map is the target")
  check(said("(e)") and said("(w)"), "...with the unexplored exits on both sides")
  clear() ; mapg("help")
  check(said("mapg gap <n>"), "help lists 'mapg gap'")
end

-- 1.17.0. A walk you ASKED for keeps walking through a fight (Joakim, 27 Oct): stopping
--     in the room where something attacked you is the worst place to be. Wimpy dragging
--     you off the route re-plans from where it left you, once the dust settles; a fight
--     never teaches a wrong link; an explore still stops the moment a fight starts.
do
  local F1 = '{ "num": 1, "name": "West",   "area": "Fight", "exits": { "e": 2 } }'
  local F2 = '{ "num": 2, "name": "Mid",    "area": "Fight", "exits": { "w": 1, "e": 3, "n": 5 } }'
  local F3 = '{ "num": 3, "name": "Mid2",   "area": "Fight", "exits": { "w": 2, "e": 4, "n": 6 } }'
  local F4 = '{ "num": 4, "name": "East",   "area": "Fight", "exits": { "w": 3 } }'
  local F5 = '{ "num": 5, "name": "Nook",   "area": "Fight", "exits": { "s": 2, "e": 6 } }'
  local F6 = '{ "num": 6, "name": "Nook2",  "area": "Fight", "exits": { "w": 5, "s": 3 } }'
  local F7 = '{ "num": 7, "name": "Pit",    "area": "Fight", "exits": { } }'
  local HIT = '{ "attacker": "a goblin", "attacker_hp": "healthy", "rounds": 1, "target": "you" }'
  local function world()
    fresh_client() ; load_plugin()
    room(F6) ; room(F5) ; room(F4) ; room(F3) ; room(F2) ; room(F1)
    clear() ; sent = {} ; emitted = {}
  end

  -- a fight mid-walk: it says so once and walks on
  world()
  mapg("go 4")
  check(#sent == 1 and sent[1] == "e", "fight walk: the walk sets off (" .. table.concat(sent, ",") .. ")")
  check(said("a fight does not"), "the walk's opening line says a fight does not stop it")
  combat(HIT)
  check(said("something is attacking you - still walking"), "a fight starting mid-walk is announced, not obeyed")
  check(not said("combat - stopped"), "...the walk is not stopped")
  clear() ; combat(HIT)
  check(not said("still walking"), "...and the next combat round does not say it again")
  arrive(F2)
  check(#sent == 2 and sent[2] == "e", "...the next step goes out on arrival, mid-fight")
  arrive(F3) ; arrive(F4)
  check(said("arrived - 3 step(s), 4 East"), "...and it gets there")
  check(emitted_count("map.walk.arrived") == 1 and emitted_count("map.walk.stopped") == 0,
        "...a requester hears arrived, never stopped")

  -- wimpy drags us north out of 2 while the walker's 'e' is still in flight; that 'e' then
  -- lands from 5 (in 6). Nothing learned from either, and once settled it re-plans from 6.
  world()
  mapg("go 4")
  arrive(F2)
  check(sent[#sent] == "e", "(walking on from 2)")
  combat(HIT)
  check(fire_trigger("Your legs run away with you", "north"), "the wimpy trigger exists")
  clear()
  room(F5)                                   -- the retreat
  check(not said("Walk stopped") and not said("should have reached"), "a wimpy retreat does not stop the walk")
  check(not said("learned 2") and not said("DISAGREES"), "...and teaches nothing")
  local n = #sent
  room(F6)                                   -- the walker's old 'e', landing from 5
  check(#sent == n, "...nothing is sent while it settles")
  check(not said("DISAGREES") and not said("learned 5"), "...the stale step's arrival teaches nothing either")
  fire_timers()                              -- settled
  check(#sent == n + 1, "...and settling plans once, sending one step (" .. table.concat(sent, ",") .. ")")
  check(said("knocked off the route in the fight - now in 6 Nook2; 2 step(s) on to 4 East"),
        "once settled it plans again from where it actually is")
  check(sent[#sent] == "s", "...and walks it (" .. table.concat(sent, ",") .. ")")
  arrive(F3) ; arrive(F4)
  check(said("arrived") and said("4 East"), "...all the way there")
  -- the links are what they were: 2's e still leads to 3, 5's e to 6
  clear() ; mapg("path 3")
  check(said("4 -> 3"), "(path)")
  world() ; mapg("path 3")
  check(said("1 -> 3") and not said("Nook"), "2's 'e' was never rewritten to the room wimpy fled to")

  -- wimpy running ALONG the route: the room it lands in is the step's, but the walker's
  -- own 'e' is still in flight - sending another would put two steps on the wire
  world()
  mapg("go 4") ; arrive(F2)
  combat(HIT)
  fire_trigger("Your legs run away with you", "east")
  room(F3)                                   -- the retreat, east - where the step was going
  room(F4)                                   -- the walker's 'e', landing from 3
  fire_timers()
  check(#sent == 2, "a retreat along the route still settles first: no extra step (" .. table.concat(sent, ",") .. ")")
  check(said("arrived") and said("4 East"), "...and the stale step's landing on the target is the arrival")

  -- stopped while it settles: the settle timer does nothing
  world()
  mapg("go 4") ; arrive(F2)
  combat(HIT)
  fire_trigger("Your legs run away with you", "north")
  room(F5)
  mapg("stop")
  local m = #sent
  fire_timers()
  check(#sent == m and not said("knocked off"), "a walk stopped while settling stays stopped")
  -- ...and a NEW walk started before the old settle timer fires is not re-planned by it
  world()
  mapg("go 4") ; arrive(F2)
  combat(HIT)
  fire_trigger("Your legs run away with you", "north")
  room(F5)
  mapg("stop") ; mapg("go 1")
  clear() ; fire_timers()
  check(not said("knocked off"), "the old walk's settle timer leaves a new walk alone")

  -- the watchdog mid-fight says what is likeliest
  world()
  mapg("go 4")
  combat(HIT)
  clear() ; fire_timers()
  check(said("never landed") and said("you are in a fight"), "a step that never lands mid-fight blames the fight")

  -- wimpy with nothing walking: a move you typed and the retreat that beat it are not
  -- paired, even through an exit the server does not name (so no known end to compare)
  fresh_client() ; load_plugin()
  room('{ "num": 10, "name": "Gate", "area": "Fight", "exits": { "e": 0, "n": 0 } }')
  cmd("e")
  fire_trigger("Your legs run away with you", "north")
  clear()
  room('{ "num": 9, "name": "Yard", "area": "Fight", "exits": { "s": 10 } }')
  check(not said("learned 10 e> 9"), "a retreat is never paired with the move queued before it")

  -- an exit only ever WALKED (the server shows 0) is known too: a fight cannot rewrite it
  fresh_client() ; load_plugin()
  room('{ "num": 11, "name": "Hall", "area": "Fight", "exits": { "e": 0, "n": 13 } }')
  cmd("e") ; room('{ "num": 12, "name": "Hut", "area": "Fight", "exits": { "w": 11 } }')
  cmd("w") ; room('{ "num": 11, "name": "Hall", "area": "Fight", "exits": { "e": 0, "n": 13 } }')
  combat(HIT)
  clear() ; cmd("e") ; room('{ "num": 13, "name": "Loft", "area": "Fight", "exits": { "s": 11 } }')
  check(said("mid-fight: 11's 'e' is known to lead to 12, not here"), "a walked-only exit is known to a fight too")

  -- the retreat that lands on the target ends the walk there
  world()
  mapg("go 5")
  check(sent[#sent] == "e", "(walking to 5 by 2)")
  arrive(F2)
  check(sent[#sent] == "n", "(and north from 2)")
  combat(HIT)
  fire_trigger("Your legs run away with you", "north")
  room(F5) ; fire_timers()
  check(said("arrived") and said("5 Nook"), "a retreat that lands on the target is an arrival")

  -- 'You cannot go' while it settles is the old step refused - ignored
  world()
  mapg("go 4") ; arrive(F2)
  combat(HIT)
  fire_trigger("Your legs run away with you", "north")
  room(F5)
  clear() ; fire_trigger("You cannot go")
  check(not said("Walk stopped"), "'You cannot go' while settling is the stale step - ignored")
  fire_timers()
  check(said("knocked off the route - now in 5") or said("now in 5 Nook; 3 step(s) on to 4"),
        "...and the re-plan goes ahead from 5 (" .. table.concat(sent, ",") .. ")")
  clear() ; fire_trigger("You cannot go")
  check(said("route is out of date"), "...a refusal after the re-plan is a real one, and stops it")

  -- off the route with no way on: stop and say where
  world()
  mapg("go 4") ; arrive(F2)
  combat(HIT)
  fire_trigger("Your legs run away with you", "down")
  room(F7) ; clear() ; fire_timers()
  check(said("the fight put us in 7 Pit, and no known route goes on from there to 4"),
        "a retreat into a room with no known way on stops the walk and says so")
  check(emitted_count("map.walk.stopped") == 1, "...and a requester hears stopped")

  -- an off-route arrival mid-fight with no wimpy line re-plans too; a fight that keeps
  -- throwing us about stops after HL.REPLANS of them
  world()
  mapg("go 4") ; arrive(F2)
  combat(HIT)
  cmd("e") ; room(F5)                        -- landed in 5, not 3: a shove
  check(said("mid-fight: 2's 'e' is known to lead to 3, not here - nothing learned"),
        "mid-fight, an arrival that contradicts a known exit teaches nothing")
  check(not said("DISAGREES"), "...and is not reported as the exit disagreeing")
  fire_timers()
  check(said("knocked off the route"), "an off-route arrival mid-fight re-plans")
  for i = 1, 5 do
    if sent[#sent] == "s" then room(F6) else room(F5) end   -- never where it asked
    fire_timers()
    if i == 4 then
      check(not said("knocked us off the route 5 times"), "...the fifth re-plan still walks")
    end
  end
  check(said("the fight knocked us off the route 5 times - walk stopped"), "...the sixth knock stops it")

  -- the skipped pairing takes the rest of the queue with it: two moves typed mid-fight,
  -- the first shoved off course - the second's arrival is not "out of step" news
  world()
  room(F2) ; combat(HIT)
  cmd("e") ; cmd("e")
  room(F5) ; clear() ; room(F6)
  check(not said("out of step") and not said("learned"), "a fight's skipped pairing drops the queue behind it")

  -- without a fight an off-route arrival still stops the walk, as it always has
  world()
  mapg("go 4") ; arrive(F2)
  clear() ; cmd("e") ; room(F5)
  check(said("should have reached 3"), "no fight: an off-route arrival still stops the walk")
  check(said("DISAGREES"), "...and the exit's disagreement is reported as before")

  -- an explore still stops for a fight
  world()
  room('{ "num": 8, "name": "Edge", "area": "Fight", "exits": { "e": 0 } }')
  mapg("explore all")
  clear() ; combat(HIT)
  check(said("combat"), "an explore still stops the moment a fight starts")
end

-- 1.18.0. Fast walk: a walk keeps HL.fast_n steps on the wire at once instead of one per
--     round trip. Every arrival is still checked against its step, in order; each step is
--     queued from the room the ROUTE sends it from (the echoes all come back before the
--     first one lands), so links are learned exactly as one at a time; a hand-added door
--     is a barrier; a step that goes wrong lets the rest of the burst land before stopping.
do
  local function R(n, ex) return string.format('{ "num": %d, "name": "R%d", "area": "Fast", "exits": %s }', n, n, ex) end
  local L = {
    R(21, '{ "e": 22 }'), R(22, '{ "w": 21, "e": 23 }'), R(23, '{ "w": 22, "e": 24, "n": 29 }'),
    R(24, '{ "w": 23, "e": 25 }'), R(25, '{ "w": 24, "e": 26 }'), R(26, '{ "w": 25 }'),
  }
  local R29 = R(29, '{ "s": 23, "e": 30 }')
  local R30 = R(30, '{ "w": 29 }')
  local echoed = 0
  -- the host echoes every command that goes to the MUD, ours included
  local function echo() while echoed < #sent do echoed = echoed + 1 ; cmd(sent[echoed]) end end
  local function world(fast)
    fresh_client() ; load_plugin()
    room(R30) ; room(R29)
    for i = 6, 1, -1 do room(L[i]) end
    if fast then mapg("fast on") end
    clear() ; sent = {} ; echoed = 0 ; emitted = {}
  end

  world(true)
  mapg("go 26")
  check(#sent == 3 and sent[1] == "e" and sent[3] == "e", "fast walk: three steps go out at once (" .. table.concat(sent, ",") .. ")")
  check(said("fast walk: 3 steps at a time"), "...and the walk says so")
  echo()
  check(not said("you moved yourself"), "...their echoes are the walk's own, not you moving")
  room(L[2]) ; check(#sent == 4, "...each arrival tops the burst up by one")
  echo() ; room(L[3]) ; echo() ; room(L[4]) ; echo()
  check(#sent == 5, "...never past the end of the route")
  room(L[5]) ; room(L[6])
  check(said("arrived - 5 step(s), 26 R26"), "...and it arrives")
  clear() ; mapg("status")
  check(said("out-of-step 0"), "...with every arrival paired with its own step")
  check(said("links walked 5"), "...and every link learned, as one at a time would")

  -- one step lands wrong with two more behind it: they land too, then it stops
  world(true)
  mapg("go 26") ; echo()
  room(L[2]) ; echo()                        -- steps 2, 3, 4 on the wire
  clear() ; room(R29)                         -- step 2 went north, somehow
  check(not said("Walk stopped"), "a wrong arrival mid-burst waits for the rest of the burst")
  room(R30)                                   -- the burst's next 'e', from 29
  fire_timers()
  check(said("step 2 ('e') should have reached 23 R23, but we are in 29 R29. Walk stopped - the 2 step(s) already sent after it landed too: you are in 30 R30."),
        "...then stops, saying where the rest of it left you")
  check(emitted_count("map.walk.stopped") == 1, "...a requester hears stopped once")

  -- 'You cannot go' for the first of three: the other two were sent from here as well
  world(true)
  mapg("go 26") ; echo()
  clear() ; fire_trigger("You cannot go")
  check(not said("Walk stopped"), "a refusal mid-burst waits for the rest of it too")
  fire_trigger("You cannot go") ; fire_trigger("You cannot go")
  check(not said("Walk stopped"), "...and the rest refused as well is part of the same settling")
  fire_timers()
  check(said("'You cannot go' - step 1 ('e') was refused; the route is out of date. Walk stopped - the 2 step(s)"),
        "...then stops and says so")

  -- a move you type mid-burst is still yours, and still stops it
  world(true)
  mapg("go 26") ; echo()
  clear() ; cmd("n")
  check(said("you moved yourself"), "a move you type during a fast walk stops it")

  -- a hand-added door is a barrier: sent alone, and nothing after it until it lands
  world(true)
  mapg("link 22 29 climb rope")
  clear() ; sent = {} ; echoed = 0
  mapg("go 30")
  local route = table.concat(sent, ",")
  check(sent[1] == "e" and #sent == 1, "a hand-added door waits for the steps before it to land (" .. route .. ")")
  echo() ; room(L[2])
  check(#sent == 2 and sent[2] == "climb rope", "...goes out alone when they have (" .. table.concat(sent, ",") .. ")")
  echo() ; room(R29)
  check(#sent == 3 and sent[3] == "e", "...and the steps after it go out once it lands")
  echo() ; room(R30)
  check(said("arrived"), "...all the way")

  -- wimpy mid-burst: everything still on the wire lands, then it plans again - with a burst
  world(true)
  mapg("go 26") ; echo()
  room(L[2]) ; echo()                        -- steps 2, 3, 4 on the wire
  combat('{ "attacker": "a goblin", "attacker_hp": "healthy", "rounds": 1, "target": "you" }')
  fire_trigger("Your legs run away with you", "north")
  room(R29)                                   -- the retreat
  local longest = 0
  for id in pairs(timers) do longest = math.max(longest, timer_secs[id] or 0) end
  check(longest == 4, "...settling waits longer with a burst on the wire: 2 s + 1 per extra step (" .. longest .. ")")
  local n = #sent
  room(R30)                                   -- one of the burst's 'e's, from 29
  check(#sent == n, "a retreat mid-burst sends nothing while the burst lands")
  fire_timers()
  check(said("knocked off the route in the fight - now in 30 R30; 5 step(s) on to 26"), "...then plans again from where it all left us")
  check(#sent == n + 3, "...and the new route goes out three at a time (" .. table.concat(sent, ",", n + 1) .. ")")
  clear() ; mapg("status")
  local before = tonumber(table.concat(printed, "\n"):match("out%-of%-step (%d+)"))
  echo() ; room(R29) ; echo() ; room(L[3]) ; echo() ; room(L[4]) ; room(L[5]) ; room(L[6])
  check(said("arrived"), "...and arrives")
  clear() ; mapg("status")
  check(tonumber(table.concat(printed, "\n"):match("out%-of%-step (%d+)")) == before,
        "...its steps queued from the room the NEW route starts in (out-of-step unchanged)")

  -- off: one at a time again; the Map tab's button switches it and says which it is
  world(true)
  local row
  for _, tab in ipairs(panel.tabs or {}) do
    for _, w in ipairs(tab.widgets or {}) do
      if w.type == "buttonrow" and w.bind and tostring(state[w.bind] or ""):find("Fast walk", 1, true) then row = w end
    end
  end
  check(row and state[row.bind]:find("Fast walk: on", 1, true), "the Map tab has a Fast walk button that says it is on")
  row.action("Fast walk: on", 3)
  check(state[row.bind]:find("Fast walk: off", 1, true) and store["fast"] == "0", "pressing it turns fast walk off, and that is kept")
  mapg("go 26")
  check(#sent == 1, "with it off a walk sends one step at a time again")
  mapg("stop")
  row.action("Fast walk: off", 3)
  check(state[row.bind]:find("Fast walk: on", 1, true) and store["fast"] == "1", "...pressing it again turns it back on")
  clear() ; row.action("Link last move", 1)
  check(said("mapg link") and not said("fast walk"), "the row's first button is still Link last move")
  clear() ; row.action("Hand links", 2)
  check(said("no hand-added links"), "...and its second Hand links")

  -- 'mapg fast <n>': how many at once (1-6), and a reload keeps both
  world(true)
  mapg("fast 5")
  check(store["fast_n"] == "5", "'mapg fast 5' sets five at once")
  fresh_client(true) ; load_plugin()
  for i = 6, 1, -1 do room(L[i]) end
  sent = {} ; mapg("go 26")
  check(#sent == 5, "...and a reload keeps it (" .. #sent .. ")")
  mapg("stop")
  mapg("fast 99") ; check(store["fast_n"] == "6", "...capped at six")
  mapg("fast 1") ; check(store["fast"] == "0", "...and one at a time is off")
  mapg("fast on") ; check(store["fast_n"] == "3", "...so 'on' after it goes back to three")
  clear() ; mapg("fast sideways")
  check(said("mapg fast on|off|<steps 1-6>"), "...anything else gets the usage")
  clear() ; mapg("help")
  check(said("mapg fast on|off|<n>"), "help lists 'mapg fast'")

  -- an explore still goes one step at a time, fast walk or not
  fresh_client() ; load_plugin() ; mapg("fast on")
  room(X1) ; cmd("e") ; room(X2) ; cmd("e") ; room(X3) ; cmd("w") ; room(X2) ; cmd("w") ; room(X1)
  clear() ; sent = {}
  mapg("explore all")
  check(#sent == 1, "an explore's route goes one step at a time even with fast walk on")
end

-- 1.19.0. The 3D view: on a Scrye with plugin API 1.24 (scrye.api) the map is a scene3d
--     widget with the 2D grid as its stand-in; 'mapg view 3d' (or the Map tab's 3D button)
--     sends every level of the map as a scene - rooms, links, '?' stubs for unexplored
--     exits, '>' stubs for doors to other maps - while the grid is still drawn for the
--     phone. On an older Scrye nothing changes.
do
  local SP = "plugin.3s-map-gmcp.scene3d"
  local function scene() return decode(state[SP] or "") end
  local function load3d(keep)
    fresh_client(keep) ; scrye.api = "1.24" ; load_plugin()
  end
  -- 1 Hall, east to 2 Yard (whose north is unexplored, east leads into another area), up
  -- to 3 Loft; 9 Gate is in another area
  local H1 = '{ "num": 1, "name": "Hall", "area": "Keep", "exits": { "e": 2, "u": 3 } }'
  local H2 = '{ "num": 2, "name": "Yard", "area": "Keep", "exits": { "w": 1, "n": 0, "e": 9 } }'
  local H3 = '{ "num": 3, "name": "Loft", "area": "Keep", "exits": { "d": 1 } }'
  local H9 = '{ "num": 9, "name": "Gate", "area": "Moor", "exits": { "w": 2 } }'
  local function world()
    room(H9) ; cmd("w") ; room(H2) ; cmd("w") ; room(H1) ; cmd("u") ; room(H3) ; cmd("d") ; room(H1)
  end
  local function map_widget()
    for _, w in ipairs(panel.tabs[1].widgets) do
      if w.type == "scene3d" or w.type == "colorgrid" then return w end
    end
  end

  -- an older Scrye: the plain grid, and 'mapg view 3d' explains
  fresh_client() ; load_plugin() ; world()
  check(map_widget().type == "colorgrid", "with no scrye.api the map is the 2D grid it always was")
  clear() ; mapg("view 3d")
  check(said("needs a Scrye with plugin API 1.24"), "...and 'mapg view 3d' says what it needs")
  check(state[SP] == nil, "...sending nothing")
  check((state["plugin.3s-map-gmcp.help.scene3d"] or "") == "", "...and no 3D how-to on the Map tab")

  -- 1.23 is not enough: scrye.api older than 1.24 is still the plain grid
  fresh_client() ; scrye.api = "1.23" ; load_plugin() ; world()
  check(map_widget().type == "colorgrid", "an API older than 1.24 keeps the 2D grid")
  fresh_client() ; scrye.api = "2.0" ; load_plugin() ; world()
  check(map_widget().type == "scene3d", "...and a newer major has it")

  -- API 1.24: the grid inside a scene3d widget, 2D by default
  load3d() ; world()
  local w = map_widget()
  check(w.type == "scene3d" and w.bind == SP, "on API 1.24 the map is a scene3d widget bound to .scene3d")
  check(w.widgets and #w.widgets == 1 and w.widgets[1].type == "colorgrid" and w.widgets[1].weave,
        "...with the woven 2D grid as its stand-in")
  check(w.height == 300 and w.palette["#"] == "text" and w.palette["@"] == "accent", "...300 px tall, rooms in the text colour")
  check((state[SP] or "") == "", "2D by default: no scene is sent")
  local tools
  for _, x in ipairs(panel.tabs[1].widgets) do
    if x.type == "buttonrow" and x.bind and tostring(state[x.bind] or ""):find("3D:", 1, true) then tools = x end
  end
  check(tools and state[tools.bind]:find("3D: off", 1, true), "the Map tab's tools row has a 3D button that says off")

  -- the how-to (30 Oct): a text widget right above the note box, empty (so taking no room)
  -- while 3D is off, and bound where the phone - which shows the flat map - never gets it
  local HP = "plugin.3s-map-gmcp.help.scene3d"
  local ws, hi, ni = panel.tabs[1].widgets
  for i, x in ipairs(ws) do
    if x.bind == HP then hi = i end
    if x.type == "input" and x.bind == "plugin.3s-map-gmcp.notebox" then ni = i end
  end
  check(hi and ni and hi == ni - 1 and ws[hi].type == "text", "the 3D how-to sits right above the note box")
  check(HP:sub(-8) == ".scene3d", "...bound under .scene3d, which is kept off the phone")
  check((state[HP] or "") == "", "...and is empty while the view is 2D")

  -- on: the scene
  clear() ; mapg("view 3d")
  check(said("view: 3D"), "'mapg view 3d' switches it on and says how to drive it")
  check(state[tools.bind]:find("3D: on", 1, true) and store["view3d"] == "1", "...the button says on, and it is kept")
  do
    local h = state[HP] or ""
    check(h:find("drag: turn", 1, true) and h:find("pan", 1, true) and h:find("wheel: zoom", 1, true)
          and h:find("right-click a room", 1, true), "3D on: the how-to says how to turn, pan, zoom and use the rooms")
    local widest = 0
    for line in (h:gsub("@{[^}]*}", "") .. "\n"):gmatch("([^\n]*)\n") do widest = math.max(widest, #line) end
    check(widest <= 32, "...in lines short enough for the 280 px panel (widest " .. widest .. ")")
  end
  local sc = scene()
  check(sc and sc.nodes, "a scene is sent")
  local by = {}
  for i, n in ipairs(sc.nodes) do by[n[1]] = { i = i - 1, n = n } end
  check(by["1"] and by["2"] and by["3"], "every level of the map is in it: hall, yard and the loft above")
  check(by["9"] and by["9"].n[5] == ">" and by["9"].n[6] == 0.6, "the door into another area is a small '>' named for the room behind it")
  check(by["1"].n[5] == "@" and sc.focus == by["1"].i, "you are '@' and the focus")
  check(by["3"].n[4] == by["1"].n[4] + 1 and by["3"].n[2] == by["1"].n[2] and by["3"].n[3] == by["1"].n[3],
        "the loft is one level up, straight above the hall")
  check(by["2"].n[2] == by["1"].n[2] + 1, "the yard is one step east")
  local stub
  for _, n in ipairs(sc.nodes) do if n[5] == "?" then stub = n end end
  check(stub and stub[1] == "" and stub[6] == 0.4 and stub[3] > by["2"].n[3], "the yard's unexplored north is a small unclickable '?' north of it")
  local function linked(a, b, ch)
    for _, l in ipairs(sc.links) do
      if ((l[1] == a and l[2] == b) or (l[1] == b and l[2] == a)) and (not ch or l[3] == ch) then return true end
    end
    return false
  end
  check(linked(by["1"].i, by["2"].i, "-"), "the hall and the yard are linked, once")
  check(linked(by["1"].i, by["3"].i, "|"), "the stairs are a vertical '|' link")
  local n12 = 0
  for _, l in ipairs(sc.links) do
    if (l[1] == by["1"].i and l[2] == by["2"].i) or (l[1] == by["2"].i and l[2] == by["1"].i) then n12 = n12 + 1 end
  end
  check(n12 == 1, "...a two-way link is drawn once, not twice")
  check(sc.level == 0 and sc.zscale == 2.5, "the level you stand on is the bright one; levels 2.5 apart")
  check(grid() ~= "", "the 2D grid is still drawn - the phone shows it")

  -- the scene is re-sent only when it changed
  state[SP] = "UNTOUCHED"
  mapg("redraw")
  check(state[SP] == "UNTOUCHED", "a redraw with nothing changed sends nothing")
  cmd("e") ; room(H2)
  sc = scene()
  check(sc and sc.nodes, "moving sends the new scene")
  by = {}
  for i, n in ipairs(sc.nodes) do by[n[1]] = { i = i - 1, n = n } end
  check(by["2"].n[5] == "@" and sc.focus == by["2"].i and by["1"].n[5] ~= "@", "...with you where you now are")

  -- Up looks at the level above: the scene's bright level follows it
  local up
  for _, x in ipairs(panel.tabs[1].widgets) do
    if x.type == "buttonrow" and x.buttons then for _, b in ipairs(x.buttons) do if b.text == "Up" then up = b end end end
  end
  up.action()
  check(scene().level == 1, "Up brightens the level above in 3D")
  -- Center: your own level again, and the camera reset (the view token moves)
  local v0 = scene().view
  local center
  for _, x in ipairs(panel.tabs[1].widgets) do
    if x.type == "buttonrow" and x.buttons then for _, b in ipairs(x.buttons) do if b.text == "Center" then center = b end end end
  end
  center.action()
  check(scene().level == 0 and scene().view == v0 + 1, "Center comes back to your level and resets the camera")

  -- the 3D callbacks: hover names a room, click shows it, right-click is the same menu
  clear() ; w.onHover("3")
  check((state["plugin.3s-map-gmcp.peek"] or ""):find("3  Loft", 1, true), "hovering a room in 3D names it")
  w.onHover("9")
  check((state["plugin.3s-map-gmcp.peek"] or ""):find("another map", 1, true), "...the door's room is named as another map")
  w.onHover("")
  check((state["plugin.3s-map-gmcp.peek"] or "") == "", "...and moving off clears it")
  w.onClick("1")
  check((state["plugin.3s-map-gmcp.detail"] or ""):find("Hall", 1, true) and said("2 -> 1"), "clicking a room in 3D shows it and lights the route")
  local menu = w.onRightClick("3")
  check(menu and menu[1][1] == "Walk there" and menu[1][2] == "mapg go 3", "right-clicking a room in 3D gives the same menu")
  check(w.onRightClick("") == nil and w.onRightClick("nope") == nil, "...and nothing for no room")
  state["plugin.3s-map-gmcp.detail"] = "KEPT" ; clear()
  local ok = pcall(w.onClick, "")
  check(ok and state["plugin.3s-map-gmcp.detail"] == "KEPT" and #printed == 0, "a click on empty space does nothing")
  ok = pcall(w.widgets[1].onClick, 1, 1)
  check(ok and state["plugin.3s-map-gmcp.detail"] == "KEPT", "...nor does one on an empty cell of the 2D grid")

  -- sections: a level's hidden part stays out of the 3D view too. The ant hole: from the
  -- entrance hall (21) west to 22 and east to 23, each with stairs down to its own wing -
  -- 24 under the west, 25 under the east - and the wings never meet down there.
  do
    local A = '{ "num": 21, "name": "Mouth", "area": "Ants", "exits": { "w": 22, "e": 23 } }'
    local B = '{ "num": 22, "name": "West",  "area": "Ants", "exits": { "e": 21, "d": 24 } }'
    local C = '{ "num": 23, "name": "East",  "area": "Ants", "exits": { "w": 21, "d": 25 } }'
    local D = '{ "num": 24, "name": "WDeep", "area": "Ants", "exits": { "u": 22 } }'
    local F = '{ "num": 25, "name": "EDeep", "area": "Ants", "exits": { "u": 23 } }'
    room(A) ; cmd("w") ; room(B) ; cmd("d") ; room(D) ; cmd("u") ; room(B) ; cmd("e") ; room(A)
    cmd("e") ; room(C) ; cmd("d") ; room(F)
    local ids = {}
    for _, n in ipairs(scene().nodes) do ids[n[1]] = true end
    check(ids["25"] and ids["23"] and ids["21"] and ids["22"], "in the east wing the levels above are all there")
    check(not ids["24"], "...but not the west wing's lower floor: the same section rule as 2D")
    mapg("sections all")
    ids = {}
    for _, n in ipairs(scene().nodes) do ids[n[1]] = true end
    check(ids["24"], "'mapg sections all' puts it in")
    mapg("sections near")
    cmd("u") ; room(C) ; cmd("w") ; room(A) ; cmd("w") ; room(B) ; cmd("w")
    room(H1)                               -- (walked off; back in the keep for the rest)
  end

  -- the button switches it back; 2D clears the scene
  tools.action("3D: on", 4)
  check((state[SP] or "") == "" and state[tools.bind]:find("3D: off", 1, true) and store["view3d"] == "0",
        "the 3D button switches back to 2D, clearing the scene")
  check((state[HP] or "") == "", "...and hides the how-to again")
  tools.action("3D: off", 4)
  check(scene() and scene().nodes, "...and on again")

  -- kept across a reload
  mapg("save")
  load3d(true) ; room(H2)
  check(scene() and scene().nodes, "a reload keeps the 3D view on")
  check((state[HP] or ""):find("drag: turn", 1, true), "...and its how-to with it")
  clear() ; mapg("view sideways")
  check(said("mapg view 2d|3d"), "anything else gets the usage")
  clear() ; mapg("help")
  check(said("mapg view 2d|3d"), "help lists 'mapg view'")
  clear() ; mapg("view")
  check(said("view: 3D"), "'mapg view' says which it is")

  -- a map wiped from under the 3D view: the scene goes with it
  mapg("wipe") ; mapg("wipe yes")
  check((state[SP] or "") == "", "wiping the map clears the 3D scene")
  world()

  -- 'mapg draw off' stops the scene with the grid
  mapg("draw off")
  state[SP] = "UNTOUCHED" ; cmd("w") ; room(H1)
  check(state[SP] == "UNTOUCHED", "with the panel off no scene is sent either")
end

print(string.format("%d passed, %d failed", pass, fail))
os.exit(fail == 0 and 0 or 1)
