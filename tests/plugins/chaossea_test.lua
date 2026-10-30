-- Harness for 3s-chaossea-gmcp. Every Room.Info payload is verbatim from Joakim's
-- captures of 23 Aug 2026 (a .gmcp fields report and two .gmcp raw walks) rather than
-- invented, so what passes here is what the server actually sends.
--
-- The properties this fork exists to keep are at the bottom: it never asks the MUD to
-- re-describe a room, and it never needs the '=S=' display markers to know where it is.
--
-- Equivalent mutants of the locator (2.4.0), documented rather than hunted. (Its odds of a
-- WRONG place are measured separately, on simulated mazes - see the comment on LOC_BITS.)
--   * the teleport line emptying what we saw: Room.Info empties it a moment later, and on a
--     Room.Map-only arrival the picture and then the header replace both halves before the
--     first look at them.
--   * cs_advance's leave_room(): a step's arrival either sends Room.Info, which starts afresh,
--     or does not - and then its exits are the same as the room before (that is WHY nothing
--     was sent), so the picture that replaces the old one is all that changes.

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
-- This file sits in tests/plugins/ (CI runs it), not beside the plugin: everything in the plugin folder is copied
-- into the build output, and a test file has no business shipping with the client.
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-chaossea/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

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
local printed, sent, every_sent
local gmcp_hooks, aliases, timers, repeats, triggers, command_hooks
local prompt_hooks, idle_hooks, disconnect_hooks, emitted
local store, state, vars, next_timer, panel

local function fresh_client()
  printed, sent, every_sent = {}, {}, {}
  gmcp_hooks, aliases, timers, repeats, triggers = {}, {}, {}, {}, {}
  command_hooks, prompt_hooks, idle_hooks, disconnect_hooks = {}, {}, {}, {}
  emitted, store, state, vars = {}, {}, {}, {}
  next_timer, panel = 0, nil
  scrye = {
    id = "3s-chaossea",
    print = function(s) printed[#printed + 1] = tostring(s) end,
    send = function(s) sent[#sent + 1] = tostring(s); every_sent[#every_sent + 1] = tostring(s) end,
    notify = function() end,
    json = { decode = decode, encode = encode },
    store = {
      get = function(k) return store[k] end,
      set = function(k, v) store[k] = v end,
      delete = function(k) store[k] = nil end,
      setMany = function(t) for k, v in pairs(t) do store[k] = v end end,
    },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    onCommand = function(fn) command_hooks[#command_hooks + 1] = fn end,
    onPrompt = function(fn) prompt_hooks[#prompt_hooks + 1] = fn end,
    onIdle = function(fn) idle_hooks[#idle_hooks + 1] = fn end,
    onDisconnect = function(fn) disconnect_hooks[#disconnect_hooks + 1] = fn end,
    on = function() end,
    emit = function(k, v) emitted[#emitted + 1] = { k = k, v = v } end,
    addTrigger = function(t) triggers[#triggers + 1] = t end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addPanel = function(p) panel = p end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    setVariable = function(k, v) vars[k] = v end,
    getVariable = function(k) return vars[k] end,
    after = function(d, fn) next_timer = next_timer + 1; timers[next_timer] = { at = d, fn = fn }; return next_timer end,
    every = function(_, fn) next_timer = next_timer + 1; repeats[next_timer] = fn; return next_timer end,
    cancel = function(id) if id then timers[id] = nil; repeats[id] = nil end end,
  }
end

-- ---- driving it the way the client does ----
local function room(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Info" then h.fn(json) end end
end
local function roommap(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Map" then h.fn(json) end end
end
local function contents(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Contents" then h.fn(json) end end
end
local function arm()
  -- A real arrival always ENDS with Room.Map, and a step may not go out until it has.
  -- Tests that only deliver Room.Info are simulating half a burst; this is the other half.
  -- With no step outstanding it arms and nothing else -- exits are left exactly as told.
  roommap('{ "rows": [ "        @        " ], "w": 17, "h": 1, "kind": "los" }')
end
local function sent_has(x)
  for _, c in ipairs(sent) do if c == x then return true end end
  return false
end
local function cmd(text) for _, h in ipairs(command_hooks) do h(text) end end
local function prompt() for _, h in ipairs(prompt_hooks) do h() end end
local function csg(arg) aliases[1].run(arg) end
local function tick() for _, fn in pairs(repeats) do fn() end end
-- Delays matter for the New Sea swap, which sends its commands seconds apart and changes
-- room twice while doing it. Fire in (delay, order-scheduled) order, once each.
local function run_until(t)
  local due = {}
  for id, e in pairs(timers) do if e.at <= t then due[#due + 1] = { id = id, e = e } end end
  table.sort(due, function(a, b)
    if a.e.at ~= b.e.at then return a.e.at < b.e.at end
    return a.id < b.id
  end)
  for _, d in ipairs(due) do timers[d.id] = nil; d.e.fn() end
end
local function fire_timers() run_until(math.huge) end
local function find_button(node, want)
  if type(node) ~= "table" then return nil end
  if node.text == want and node.action then return node end
  for _, v in pairs(node) do
    local hit = find_button(v, want)
    if hit then return hit end
  end
  return nil
end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end
local function clear() printed = {} end
local function fire_trigger(needle, ...)
  for _, t in ipairs(triggers) do
    if t.pattern:find(needle, 1, true) then t.run(...) return true end
  end
  return false
end

-- ---- the captures (verbatim) ----
local L3_NS  = '{ "exits": { "n": 60494, "s": 60494 }, "area": "Unknown", "name": "Layer three of the Sea of Chaos", "num": 60494 }'
local L3_NSE = '{ "exits": { "n": 60494, "s": 60494, "e": 60494 }, "area": "Unknown", "name": "Layer three of the Sea of Chaos", "num": 60494 }'
local L2_NS  = '{ "exits": { "n": 60494, "s": 60494 }, "area": "Unknown", "name": "Layer two of the Sea of Chaos", "num": 60494 }'
local L4_NS  = '{ "exits": { "n": 60494, "s": 60494 }, "area": "Unknown", "name": "Layer four of the Sea of Chaos", "num": 60494 }'
local L1_ZERO = '{ "exits": { "s": 0, "n": 60494 }, "area": "Unknown", "name": "Layer one of the Sea of Chaos", "num": 60494 }'

local DIRS = { n=1, s=1, e=1, w=1, u=1, d=1, ne=1, nw=1, se=1, sw=1 }

-- ============================================================
fresh_client()
dofile(PLUGIN)

-- 1. It subscribes to Room.Info, and that is the arrival signal.
local pkgs = {}
for _, h in ipairs(gmcp_hooks) do pkgs[h.pkg] = true end
check(#gmcp_hooks == 5, "five GMCP hooks registered")
check(pkgs["Room.Info"], "...Room.Info, the room when it has something new to say")
check(pkgs["Room.Contents"], "...Room.Contents, what is in the room")
check(pkgs["Room.Map"], "...Room.Map, which is the only one sent for EVERY move")
check(pkgs["Char.Combat"], "...Char.Combat, the combat truth now that MIP is off")
check(pkgs["Guild.State"], "...and Guild.State, which carries Seid now that vik.* is dead")

-- 2. The whole point: the '=S=' room-header marker is no longer needed, so the MUD
--    display setting can be switched off.
-- The distinction is TOLERATES versus REQUIRES. Nothing may need the marker to be switched
-- on -- that was the whole point -- but the room header trigger has to survive it still
-- being on, because the marker is a PREFIX and would otherwise stop the line matching.
-- So: any pattern that mentions '=S=' must make it optional.
local requires_S = false
for _, tr in ipairs(triggers) do
  if tr.pattern:find("=S=", 1, true) and not tr.pattern:find("(?:=S=)?", 1, true) then
    requires_S = true
  end
end
check(not requires_S, "no trigger REQUIRES the =S= room-header marker")

-- 3. The alias is 'csg', so it can run beside the shipped bot without stealing 'cs'.
-- The alias is 'cs': this IS the chaos-sea bot now, not a fork running beside one.
-- (The harness helper below is still named csg for historical reasons -- it drives
--  aliases[1] directly and never looks at the pattern, so the name is cosmetic.)
check(aliases[1] and aliases[1].pattern:find("cs", 1, true) ~= nil, "the alias is registered")
check(aliases[1] and aliases[1].pattern:find("csg", 1, true) == nil, "...as 'cs', not 'csg'")

-- 4. Exits come from the payload KEYS. The values are all 60494 or 0 and say nothing.
fresh_client(); dofile(PLUGIN)
csg("enable"); clear()
room(L3_NSE)
csg("rooms")
check(said("3 exits") or said("n,s,e") or said("e,n,s") or said("3"), "exits are read from the payload")

-- 5. A destination of 0 is still an exit -- the frontier a mapper wants.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L1_ZERO); arm()
clear(); sent = {}
csg("step")
check(#sent == 1, "a zero-destination exit is walkable: exactly one step goes out")
check(DIRS[sent[1]] ~= nil, "...and it is a bare direction")

-- 6. One confirmed move at a time: nothing further is sent until Room.Info says we arrived.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); arm()
sent = {}
csg("step")
check(#sent == 1, "exactly one step is sent")
local first = sent[1]
prompt(); prompt()
check(#sent == 1, "no second step while the first is unconfirmed")
room(L3_NSE)
check(#sent == 1, "arriving does not itself send anything")

-- 7. The layer the server names corrects dead reckoning, and says so.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)            -- anchors: this layer is z = 0
clear()
room(L4_NS)            -- a layer deeper, without the bot having walked 'd'
check(said("z corrected"), "a layer that disagrees with dead reckoning corrects z out loud")
clear()
room(L3_NS)            -- back up again
check(said("z corrected"), "...and corrects it back")

-- 8. An unrecognised name leaves z alone rather than guessing.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
clear()
room('{ "exits": { "n": 1 }, "area": "Midgard", "name": "The gatehouse of Midgard", "num": 50942 }')
check(not said("z corrected"), "a room that is not a sea layer does not move z")

-- 9. Rubbish in a payload is survived, not thrown.
fresh_client(); dofile(PLUGIN)
csg("enable"); clear()
room("not json at all")
check(said("did not decode"), "a payload that is not an object is reported, not fatal")
room('{ "exits": "n", "name": "Layer three of the Sea of Chaos" }')
check(true, "exits of the wrong type does not throw")

-- 10. The watchdog no longer glances: an unconfirmed step is treated as one that
--     never landed, and replanned -- never re-sent, which would double-move us.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); arm()
csg("step")
local before = #sent
clear()
tick(); tick(); tick()
check(said("the step is unconfirmed"), "an unconfirmed step is called out after 3s, not 15")
check(#sent == before, "...and nothing is re-sent for it")

-- ...and the exit it failed on is dropped, so the router cannot keep walking into it.
-- constructed: a sea room with a single exit, so "did it drop that exit?" has one answer.
SINGLE_EXIT = '{ "exits": { "n": 60494 }, "area": "Unknown", "name": "Layer one of the Sea of Chaos", "num": 60494 }'
fresh_client(); dofile(PLUGIN)
csg("enable")
room(SINGLE_EXIT); arm()
sent = {}
csg("step")
check(#sent == 1 and sent[1] == "n", "the one exit is stepped")
tick(); tick(); tick()
sent = {}
csg("step")
-- The exit is KEPT. A wall says so ("You cannot go north.") and cs_blocked removes it with
-- evidence; a silent timeout has an innocent cause -- a corridor of identical rooms sends no
-- GMCP at all, because nothing in the payload changed. Dropping an exit here deleted the
-- corridor out from under the bot until the room had none left.
check(#sent == 1 and sent[1] == "n", "the exit survives an unconfirmed step - silence is not a wall")

-- A pause freezes the timeout too. Pausing mid-step is how you take the controls back, and
-- a bot that went on dropping exits from a room you have since walked out of would quietly
-- vandalise the map while you thought it was stopped.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
csg("step")
csg("pause")
clear()
tick(); tick(); tick(); tick(); tick()
check(not said("the step is unconfirmed"), "the step timeout does not run while paused")

-- 12. THE NEW SEA SWAP. 'enter sea' is a room change, so Room.Info seeds the new map by
--     itself -- but only if the reset has already happened. Resetting AFTER the arrival
--     wipes the one room that mattered and leaves the bot in a sea it cannot map.
--     'retreat from the sea' changes room too, to somewhere outside the sea entirely, and
--     mapping THAT would seed the new maze with a phantom room and unreachable frontier.
local ENTER_SEA = '{ "exits": { "s": 0, "w": 0, "out": 266 }, "area": "Unknown", "name": "Layer one of the Sea of Chaos", "num": 60494 }'
-- constructed, NOT captured: we have no capture of the room 'retreat' lands in. Its exits
-- are deliberately disjoint from the sea room's, so a leak is visible as a step nobody could
-- legitimately take. 266 is real -- it is what the sea's 'out' exit points at.
-- The 'd' is what makes ignoring it OBSERVABLE. Mapping a room with a down exit shouts
-- "DOWN exit here!" -- and the reset seconds later would clean the map but not un-say it.
local OUTSIDE = '{ "exits": { "n": 267, "d": 268 }, "area": "Midgard", "name": "Somewhere outside", "num": 266 }'

fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
csg("pause")
local newsea = find_button(panel, "New Sea")
check(newsea ~= nil, "the New Sea button is there to press")
newsea.action()
run_until(2)                       -- ...retreat from the sea
clear(); sent = {}
room(OUTSIDE)                      -- the room outside: must not be mapped
check(not said("DOWN exit here"), "a room change mid-swap is not mapped - it is not in the sea")
run_until(5)                       -- reset, then 'enter sea'
local entered = false
for _, c in ipairs(sent) do if c == "enter sea" then entered = true end end
check(entered, "the swap sends 'enter sea'")
room(ENTER_SEA)                    -- THE arrival that matters
arm()
run_until(7)
sent = {}
csg("step")
check(#sent == 1, "the new sea's arrival room survived the reset - a step can be planned")
check(sent[1] == "s" or sent[1] == "w", "...and it steps an exit of the SEA room, not the room outside")

-- 13. 'out' is the only exit in the sea with a real destination -- it is the way OUT --
--     and it is never explored. Tested with a room that has nothing else, so a bot that
--     queued it would have to send it: with a second exit available it could pick that one
--     and pass while still holding 'out' in the frontier.
-- constructed, not captured: a sea room whose only exit is 'out'.
ONLY_OUT_ROOM = '{ "exits": { "out": 266 }, "area": "Unknown", "name": "Layer one of the Sea of Chaos", "num": 60494 }'
fresh_client(); dofile(PLUGIN)
csg("enable")
room(ONLY_OUT_ROOM)
sent = {}
csg("step")
check(#sent == 0, "'out' is never walked as an exploration step, even as the only exit")

-- 14. Room.Contents replaces the mob / player / item markers.
-- verbatim captures, 23 Aug
local MOB     = '{ "full": 1, "items": [ { "type": "monster", "count": 1, "name": "A enormous misfigured abomination {chaotic}" } ] }'
local WARBAND = '{ "full": 1, "items": [ { "type": "monster", "count": 1, "name": "A warband in service to Goran [Legendary] [Grey-bearded] [6]" }, { "type": "monster", "count": 1, "name": "Stabby, the Entropy Herald {Offensive}" } ] }'
local CLOAK   = '{ "full": 1, "items": [ { "type": "item", "count": 1, "name": "A strong chaotic cloak" } ] }'
-- verbatim, 23 Aug: the 'player' type is real, and a player's own guards arrive typed as
-- monsters beside them -- which is exactly why the attack has to be held.
local PLAYER  = '{ "full": 1, "items": [ { "type": "monster", "count": 1, "name": "A household guard in service to Walker [Legendary] [Grizzled] [4]" }, { "type": "monster", "count": 1, "name": "Thrain, Shackled Fighter" }, { "type": "player", "count": 1, "name": "Jarl of Ein, Walker hinn Hersir af Sigurdsson, fylgjandi Baldrs (righteous)" }, { "type": "item", "count": 61, "name": "A cube of raw chaos" } ] }'
-- constructed: a cask, to exercise the goal match against a typed item rather than a line.
local CASK    = '{ "full": 1, "items": [ { "type": "item", "count": 1, "name": "a battered cask" } ] }'

fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); contents(MOB)
sent = {}
prompt()
check(sent_has("kill mutant"), "a monster in Room.Contents is attacked at the prompt")

fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); contents(PLAYER)
sent = {}; clear()
prompt()
check(not sent_has("kill mutant"), "a player in the room stops the attack")
check(said("player in the room"), "...and says why")

fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); contents(CASK)
clear()
prompt()
check(said("CASK found"), "a goal item in Room.Contents pauses the bot")

fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); contents(CLOAK)
clear()
prompt()
check(not said("CASK found"), "...an ordinary item does not")

-- 15. Once Room.Contents has spoken for a room, the marker triggers stand down: the MUD
--     prints the same creatures as text moments later and they must not count twice.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); contents(WARBAND)
clear()
check(fire_trigger("=P=", "Bob"), "the =P= trigger is still registered as a fallback")
prompt()
check(sent_has("kill mutant"), "a player line AFTER Room.Contents is ignored - it is stale text")

-- 16. ...but with the markers still on and no Room.Contents for the room, they work
--     exactly as before. This is what lets the markers come off one at a time.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)                      -- an empty room sends no Room.Contents at all
check(fire_trigger("=M=", "a mutant"), "the =M= trigger is still registered")
sent = {}
prompt()
check(sent_has("kill mutant"), "with nothing from GMCP, the marker line still arms the attack")

-- 17. The stand-down is per ROOM, not for ever.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); contents(MOB)
room(L3_NSE)                     -- next room: empty, so no Room.Contents
fire_trigger("=M=", "a mutant")
sent = {}
prompt()
check(sent_has("kill mutant"), "the next room's marker line is heard again")

-- 18. A malformed payload is survived.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); clear()
contents("}}not json{{")
check(said("did not decode"), "a Room.Contents that is not an object is reported, not fatal")
contents('{ "full": 1, "items": "a cloak" }')
check(true, "items of the wrong type does not throw")

-- 19. A party member's long title still matches the whitelist by substring, so the bot is
--     not held up by its own side. 'walker' is buried deep inside the Jarl's full title.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("party walker")
room(L3_NS); contents(PLAYER)
sent = {}
prompt()
check(sent_has("kill mutant"), "a party member in the room does not stop the attack")

-- 20. The sea moves you without being asked. The Room.Info that follows is NOT the step we
--     sent, and confirming it as one would advance pos by a direction never travelled.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
csg("step")
clear()
check(fire_trigger("Suddenly you find yourself", nil), "the teleport line is watched for")
room(L3_NSE)                     -- a real maze room, but not where the step was going
check(said("teleported inside the maze"), "a teleport within the maze says pos is unknown")
check(said("cs reset"), "...and tells you how to recover: reset here")
sent = {}
prompt()
check(#sent == 0, "...and the bot stops rather than walking on from a position it invented")
-- The half-walked plan has to GO, not merely be held. Pausing makes it inert, but the
-- moment you unpause, a step still marked "awaiting" times out and deletes an exit from
-- whichever room the bot thinks it is in -- which after a teleport is the wrong one.
csg("pause")                     -- back to walking
clear()
tick(); tick(); tick()
check(not said("the step is unconfirmed"),
      "the abandoned step does not survive the pause and vandalise a room on unpause")

-- 21. Leaving the maze entirely. Verbatim capture of the sea's entrance room.
local SWIRL = '{ "exits": { "out": 267 }, "area": "The Sea of Chaos", "name": "A swirling Sea of Chaos", "num": 266 }'
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
csg("step")
clear()
room(SWIRL)
check(said("left the maze"), "a room that names no layer stops the bot")
sent = {}
prompt()
check(#sent == 0, "...and it does not keep walking")

-- 21b. Thrown OUT of the maze by the sea. You cannot leave it by accident -- 'out' is
--      never walked, and 'retreat from the sea' / 'enter portal' are deliberate -- so being
--      outside it means going back IN, not resetting a map about to be replaced.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
csg("step")
clear()
fire_trigger("Suddenly you find yourself", nil)
room(SWIRL)
check(said("enter sea"), "thrown out of the maze: told to go back in, not to reset")
check(not said("cs reset"), "...and NOT told to reset, which would be the wrong advice there")

-- 22. ...but an ordinary layer arrival is still confirmed, teleport flag or no.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
csg("step")
clear()
room(L3_NSE)
check(not said("teleported"), "a normal arrival is not mistaken for a teleport")
check(not said("left the maze"), "...nor for leaving the maze")

-- 23. A refusal must be recognised INSTANTLY, not left to the step timeout. The pattern
--      is not end-anchored, because a single trailing space on the MUD's line used to cost
--      the whole timeout before the bot worked out for itself that it had not moved.
fresh_client(); dofile(PLUGIN)
local blocked_pat
for _, tr in ipairs(triggers) do
  if tr.pattern:find("cannot go", 1, true) then blocked_pat = tr.pattern end
end
check(blocked_pat ~= nil, "the refusal line is watched for")
check(blocked_pat and not blocked_pat:find("$", 1, true),
      "...with no end anchor, so trailing whitespace cannot defeat it")

-- ...and it does the same thing the timeout does, three seconds sooner.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(SINGLE_EXIT); arm()
sent = {}
csg("step")
check(#sent == 1 and sent[1] == "n", "the one exit is stepped")
clear()
check(fire_trigger("cannot go", "n"), "the refusal fires")
check(said("blocked going"), "...and is called out at once")
sent = {}
csg("step")
check(#sent == 0, "the refused exit is dropped, not walked into again")

-- 24. An EMPTY Room.Contents is a statement, not silence: the room has nothing in it, and
--     the marker triggers must stand down for it exactly as for a room full of mobs.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
contents('{ "full": 1, "items": [ ] }')     -- verbatim, 23 Aug
fire_trigger("=M=", "a mutant")
sent = {}
prompt()
check(#sent == 0, "a stale =M= line after an empty Room.Contents is ignored")

-- 25. Room.Info is NOT sent for every move: an identical payload is suppressed, so stepping
--     between two rooms with the same exits produces nothing. Room.Map still arrives, and
--     the links touching '@' are the exits. Every map below is a verbatim capture (rows
--     only; the legend is constant and unused).
--
--     Each is paired with the Room.Info the server sent for the SAME room, so the parser is
--     checked against the server's own answer rather than against my reading of a picture.
local function themap(rows) return '{ "rows": [ ' .. rows .. ' ], "h": 9, "w": 17, "kind": "los" }' end
--            Room.Info said { "w": 60494 }
local MAP_W  = themap('"      |          ", "    #-O-@        "')
--            Room.Info said { "e": 60494, "w": 60494 }
local MAP_EW = themap('"      O-@-O-#    ", "      |          "')
--            room header said (s,n)
local MAP_NS = themap('"        #        ", "        |        ", "        m        ", "        |        ", "        @        ", "        |        ", "        O        ", "        |        ", "        #        "')

-- The stall itself: a step lands, Room.Map comes, Room.Info does not.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); arm()
csg("step")
clear()
roommap(MAP_W)
check(not said("the step is unconfirmed"), "a Room.Map-only arrival confirms the step")
prompt()                         -- the prompt is what retires a finished plan
tick(); tick(); tick()
check(not said("the step is unconfirmed"), "...and the step timeout never fires for it")

-- ...and the exits it read are the server's own answer for that room: w, and only w.
sent = {}
csg("step")
check(#sent == 1 and sent[1] == "w", "the single exit the map shows is the one walked")

fresh_client(); dofile(PLUGIN)
csg("enable"); room(L3_NS); arm(); csg("step"); roommap(MAP_EW); prompt()
sent = {}
csg("step")
check(#sent == 1 and (sent[1] == "e" or sent[1] == "w"), "an east-west map yields east or west")
check(sent[1] ~= "n" and sent[1] ~= "s", "...and never a direction the map does not draw")

fresh_client(); dofile(PLUGIN)
csg("enable"); room(L3_NSE); arm(); csg("step"); roommap(MAP_NS); prompt()
sent = {}
csg("step")
check(#sent == 1 and (sent[1] == "n" or sent[1] == "s"), "a north-south map yields north or south")
check(sent[1] ~= "e" and sent[1] ~= "w", "...and never east, which that map does not draw")

-- North and south have to be told apart, not merely both found. A symmetric map cannot
-- catch them being swapped, so this one is deliberately one-sided.
-- constructed, from the verbatim shapes above: a room whose only link runs south.
local MAP_S_ONLY = themap('"        @        ", "        |        ", "        O        "')
fresh_client(); dofile(PLUGIN)
csg("enable"); room(L3_NS); arm(); csg("step"); roommap(MAP_S_ONLY); prompt()
sent = {}
csg("step")
check(#sent == 1 and sent[1] == "s", "a link drawn BELOW '@' is south, not north")

-- 26. Room.Contents lands BEFORE Room.Map in the burst, so by the time the arrival is
--     confirmed the new room may already have been described. Confirming must not throw
--     that away -- which is why the per-room flags are cleared when the step is SENT.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
csg("step")
contents(MOB)                    -- the new room's monster, before anything confirms arrival
roommap(MAP_NS)
sent = {}
prompt()
check(sent_has("kill mutant"), "a monster reported before the arrival is confirmed still gets attacked")

-- 26b. When YOU walk rather than the bot, nothing was sent, so the clearing that happens on
--      send never ran. The arrival still has to forget the room behind it: a mob you left
--      standing one room back is not a mob to attack here.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
fire_trigger("=M=", "a mutant")  -- a monster in THIS room
room(L3_NSE)                     -- ...and you walk out of it yourself
sent = {}
prompt()
check(not sent_has("kill mutant"), "a monster left behind in the last room is not attacked in this one")

-- 27. A teleport with no Room.Info is still a teleport.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); arm()
csg("step")
clear()
fire_trigger("Suddenly you find yourself", nil)
roommap(MAP_NS)
check(said("teleported"), "a teleport confirmed only by Room.Map is not mistaken for our step")
sent = {}
prompt()
check(#sent == 0, "...and the bot stops")

-- 28. Room.Map with no step outstanding is ignored: it also fires when a monster moves in
--     sight, and that is not an arrival.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)
clear()
roommap(MAP_W)
check(not said("z corrected"), "a Room.Map with nothing outstanding moves nothing")
prompt()
sent = {}
csg("step")
check(#sent == 1 and (sent[1] == "n" or sent[1] == "s"),
      "...and the room still has the exits Room.Info gave it, not the map's")

-- 29. THE BURST RACE. Room.Info is the FIRST thing in an arrival burst; Room.Map is the
--     last. Stepping on Room.Info sends the next move while Room.Contents and Room.Map for
--     the room being left are still in flight -- and Room.Map confirms an outstanding step,
--     so the old burst's tail confirms the step that just went out. The real arrival then
--     finds nothing awaiting it, and mid-fight that reads as a wimpy retreat.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")                   -- nothing known yet, so this only arms auto
room(L3_NS)                      -- Room.Info: the burst has STARTED
sent = {}
prompt()
check(#sent == 0, "no step goes out between Room.Info and Room.Map")
roommap(MAP_NS)                  -- ...and now the burst has finished
prompt()
check(#sent == 1, "the step goes out once the room is fully described")
check(DIRS[sent[1]] ~= nil, "...and it is a bare direction")

-- 29b. ...and when Room.Info is suppressed entirely, the confirming Room.Map has to arm as
--      well, or the bot walks exactly one room and then waits for ever.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NS)
roommap(MAP_NS)
prompt()                         -- first step goes out
sent = {}
roommap(MAP_NS)                  -- arrival with NO Room.Info: confirms, and ends the burst
prompt()
check(#sent == 1, "a Room.Map-only arrival arms the next step as well as confirming the last")
check(#sent == 1, "...and it goes out on THAT prompt: the burst had already ended")

-- 29c. THE SAME RACE, THROUGH THE OTHER DOOR. When a plan FINISHES, cs_advance used to call
--      cs_step() straight away -- bypassing `armed` entirely. That runs on the prompt, which
--      can beat Room.Map, so the next step went out mid-burst and the burst's own Room.Map
--      confirmed it. Gating the idle path was not enough; this path had to be gated too.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NS)
roommap(MAP_NS)
prompt()                         -- first step goes out
sent = {}
room(L3_NSE)                     -- Room.Info for the room we arrived in: burst has STARTED
prompt()                         -- the plan finishes here...
check(#sent == 0, "a finished plan does not step on before Room.Map ends the burst")
roommap(MAP_NS)                  -- ...and now it has ended
prompt()
check(#sent == 1, "...and the next step goes out on the prompt after it")

-- 29d. THE REGRESSION THAT CAME WITH 29c. The prompt that retires a plan after a fight is
--      the one AFTER the kill -- Room.Map is long past by then. Waiting for yet another
--      prompt meant the bot sat over every corpse until the watchdog nudged it, twenty
--      seconds a kill. `armed` is the burst-ended signal, so a finished plan may step on
--      whenever it is set.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NS); roommap(MAP_NS)
prompt()                         -- steps out
room(L3_NSE); contents(MOB); roommap(MAP_NS)   -- arrive on a monster; burst ends
sent = {}
prompt()
check(sent_has("kill mutant"), "the monster is attacked on arrival")
fire_trigger("There is no", "mutant")          -- the fight ends
sent = {}
fire_timers()                                  -- cs_no_mob's one-second resume
check(#sent == 1 and DIRS[sent[1]] ~= nil, "the walk resumes straight after a kill, unprompted")

-- 30. The consequence in full: a fight, and no phantom wimpy.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NS)
roommap(MAP_NS)
prompt()                         -- steps
state["enemy.name"] = "a horrific anathemation {chaotic}"   -- the mob engages us
clear()
room(L3_NSE)                     -- the real arrival, mid-fight
check(not said("WIMPY"), "an arrival that IS our own step is not called a wimpy retreat")

-- 31. A room with a down exit draws 'v' where '@' would be -- the marker REPLACES you.
--     Reconstructed from the client's rendered map, not from a raw GMCP payload: the two
--     have matched character-for-character in every capture where both were shown.
local MAP_DOWN = themap('"    #-O-v        ", "        |        ", "        m        "')
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); arm()
csg("step")
roommap(MAP_DOWN)
prompt()
sent = {}
csg("step")
check(#sent == 1, "a room whose '@' is replaced by 'v' is still readable - not a dead end")
check(sent[1] == "s" or sent[1] == "w", "...and its exits are the ones the map draws")

-- (A map with nobody in it at all returns nil rather than an empty exit list, so an
--  unreadable map cannot be written over a room as "dead end". No behavioural test pins
--  that: a room just arrived in has no exits recorded either way, so the two are
--  indistinguishable from outside. Defensive, not verified.)

-- 33. A REAL retreat announces itself and names its direction, so position survives it.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NS); roommap(MAP_NS)     -- [0 0 0], armed and ready to walk
clear()
check(fire_trigger("Your legs run away", "east"), "the retreat line is watched for")
room(L3_NSE)
check(said("ran away with you east"), "the retreat is reported with its direction")
check(said("[1 0 0]"), "...and position is APPLIED, not declared lost")
check(not said("csg set"), "...so there is nothing to guess at")
sent = {}
prompt()
check(#sent == 0, "...and the bot is paused, though it was armed and would otherwise walk")

-- 34. Its ABSENCE is what matters. Mid-fight, nothing ordered and nothing announced: the
--     maze rearranged itself. That is not a retreat and must not stop the bot -- this is
--     what was pausing it mid-kill and leaving the monster it had walked up to unattacked.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NS); roommap(MAP_NS)
prompt()                                       -- steps
room(L3_NS); contents(MOB); roommap(MAP_NS)    -- arrive on a monster
prompt()                                       -- attacks it: fighting is now true
clear()
room(L3_NSE)                                   -- the maze shifts: new exits, same room
check(not said("WIMPY"), "a shift mid-fight is not called a retreat")
check(not said("PAUSED"), "...and does not stop the bot")
-- ...and the new exits were taken from the payload, not ignored.
state["enemy.name"] = ""
fire_trigger("There is no", "mutant")
sent = {}
fire_timers()
check(#sent == 1 and (sent[1] == "n" or sent[1] == "s" or sent[1] == "e"),
      "the shifted room's exits are the ones it walks")

-- 34b. GMCP-only combat truth (MIP coexists on 3Scapes - verified 29 Aug - but a no-MIP
--      character has only GMCP, where enemy.name stopped clearing at the kill - every
--      recovery path gates on in_combat(), so the bot stood over the corpse for good).
--      Combat truth now comes
--      from the RAW Char.Combat stream: attacker + live rounds = fighting, and activity
--      has a shelf life, so no terminal snapshot is needed and any shape is accepted.
local function combat(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Char.Combat" then h.fn(json) end end
end
local CC_HIT    = '{ "target": "you", "rounds": 3, "attacker": "a horrific anathemation" }'
local CC_END    = '{ "target": "", "rounds": 0, "attacker": "" }'
local CC_ZOMBIE = '{ "target": "you", "rounds": 0, "attacker": "a horrific anathemation" }'
local function into_fight()
  fresh_client(); dofile(PLUGIN)
  csg("enable")
  csg("auto on")
  room(L3_NS); roommap(MAP_NS)
  prompt()                                     -- steps out
  room(L3_NSE); contents(MOB); roommap(MAP_NS) -- arrive on a monster
  prompt()                                     -- attacks: fighting = true
  combat(CC_HIT)                               -- the GMCP fight heartbeat
  fire_trigger("killing blow", "a chaos mutant")
  sent = {}
end

-- a clean terminal snapshot ends it
into_fight()
combat(CC_END)
fire_timers()                                  -- kill_delay, then the resume chain
fire_trigger("There is no", "mutant")
sent = {}
fire_timers()
check(#sent == 1 and DIRS[sent[1]] ~= nil,
      "a terminal Char.Combat ends the fight and the walk resumes - no MIP involved")

-- a ZOMBIE terminal - attacker still named, rounds zeroed - is a report, not a fight
into_fight()
combat(CC_ZOMBIE)
fire_timers()
fire_trigger("There is no", "mutant")
sent = {}
fire_timers()
check(#sent == 1 and DIRS[sent[1]] ~= nil,
      "a terminal that keeps the attacker's name but zeroes the rounds still ends it")

-- NO terminal at all: the shelf life ends it
into_fight()
for _ = 1, 9 do tick() end                     -- nine silent seconds outlive CC_STALE
fire_timers()
fire_trigger("There is no", "mutant")
sent = {}
fire_timers()
check(#sent == 1 and DIRS[sent[1]] ~= nil,
      "total silence at the kill cannot wedge the bot: staleness ends the fight")

-- ...but a LIVE fight is not ended by impatience: heartbeats keep it alive, and the
-- GMCP truth OUTRANKS the state fallback (which is empty here - MIP is off)
into_fight()
sent = {}
for _ = 1, 10 do tick() ; combat(CC_HIT) end   -- a LONG fight: rounds keep arriving
                                               -- well past CC_STALE, each refreshing it
fire_timers()                                  -- cs_after_kill checks, keeps waiting
local rekilled = false
for _, c in ipairs(sent) do if c:find("^kill ") then rekilled = true end end
check(not rekilled, "while rounds keep arriving the bot keeps waiting, not re-killing")
prompt()
check(#sent == 0, "...and the prompt walks nowhere either")

-- 34c. SEID OVER GMCP (Guild.State, live 27 Aug). vik.* is MIP-fed and dead on a GMCP
--      world; Guild.State's points.vitka is the S[cur|max] number, and once seen it
--      outranks the vik.* reads - even a stale MIP value left behind.
local function guild(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Guild.State" then h.fn(json) end end
end
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("rest 100 30")
room(L3_NS); roommap(MAP_NS)
guild('{ "guild": "viking", "page": 3, "pages": 3, "points": { "vitka": 40, "mvitka": 7152 } }')
state["vik.seid"] = "500"                      -- a stale MIP leftover says we are fine
clear() ; sent = {}
csg("step")
check(said("Seid low (40 < 100)"), "Guild.State's vitka drives the rest floor, over stale vik.*")
check(#sent == 0, "...and no step goes out")
guild('{ "guild": "viking", "page": 3, "pages": 3, "points": { "vitka": 150, "mvitka": 7152 } }')
fire_timers()                                  -- the rest timer: recovered now
clear() ; sent = {}
csg("step")
check(not said("Seid low"), "recovery over GMCP lifts the floor")
check(#sent == 1, "...and the step goes out")

-- 35. You walking out of a fight yourself still stops it -- you have taken the controls.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NS); roommap(MAP_NS)
prompt()
room(L3_NS); contents(MOB); roommap(MAP_NS)
prompt()
clear()
cmd("n")                                       -- YOU typed a direction
room(L3_NSE)
check(said("You walked n during a fight"), "a move you made yourself is still called out")
check(said("PAUSED"), "...and still stops the bot")

-- 36. AN EMPTY FRONTIER IS NOT AN EXPLORED SEA. Walk into a room whose only exit leads
--     back where you came from and the map has nothing unexplored left in it -- but the sea
--     is barely touched. Stopping there is a statement about the map, not about the sea, so
--     it blind-steps to re-orient instead.
-- constructed, from the verbatim shapes: a room whose only link runs north.
local MAP_N_ONLY = themap('"        O        ", "        |        ", "        @        "')
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(SINGLE_EXIT)                -- [0 0 0], one exit north
roommap(MAP_N_ONLY)
prompt()                         -- steps n
roommap(MAP_S_ONLY)              -- [0 1 0], one exit south - straight back to a known room
clear(); sent = {}
prompt()
check(said("blind step"), "an empty frontier re-orients rather than declaring the sea explored")
check(#sent == 1 and DIRS[sent[1]] ~= nil, "...by walking a real exit of the room it is in")

-- ...but it does not re-orient for ever. Twenty is the cap -- and then, because a map that
-- routes nowhere is a fact about the MAP and not about the sea, it forgets the map and
-- relearns from the room it is standing in, keeping that room's own exits. Three relearns a
-- sea; a two-room world that can never produce a frontier burns all three and then stops.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(SINGLE_EXIT); roommap(MAP_N_ONLY)
prompt()
for i = 1, 130 do
  roommap(i % 2 == 1 and MAP_S_ONLY or MAP_N_ONLY)   -- bouncing between the same two rooms
  prompt()
end
check(said("relearning from this room"), "twenty blind steps is the cap, and then the map is relearned")
check(said("(3/3)"), "...three times at most")
check(said("still stuck after 3 relearns"), "...and a world that defeats all three stops honestly")

local function layer3(e) return '{ "exits": ' .. e .. ', "area": "Unknown", "name": "Layer three of the Sea of Chaos", "num": 60494 }' end
local function layer4(e) return '{ "exits": ' .. e .. ', "area": "Unknown", "name": "Layer four of the Sea of Chaos", "num": 60494 }' end
local function header(ex) return fire_trigger("Layer", ex) end

-- 36b. A blind step never takes a DOWN exit. A descent is a one-way door -- pathing never
--      goes back up -- so a recovery walk that stumbles down a hole strands every queued
--      exit on the layer above for the rest of the sea. Constructed so the choice is
--      deterministic: the room has 'd' and the way we came, nothing else. The way back is
--      only reached by the second pass (it is OPP of the last step), so a bot willing to
--      blind-step down would do it in the first pass and send 'd'.
fresh_client(); dofile(PLUGIN)
csg("enable"); csg("auto on")
csg("set 0 0 0")
room(layer3('{ "n": 0 }'))
arm()
sent = {}
csg("step")
check(sent[1] == "n", "walks n into the trap room")
-- the trap room: a down exit and the way back, both leading to "known" places
csg("set 0 1 -1")
room(layer4('{ "n": 60494 }'))   -- pre-know the room below the trap, so no dive candidate
arm()
csg("set 0 1 0")
room(layer3('{ "s": 60494, "d": 60494 }'))
arm()
sent = {}
csg("step")
check(#sent == 1 and sent[1] == "s",
      "the blind step takes the way back, never the hole in the floor (sent "
      .. tostring(sent[1]) .. ")")

-- 36c. The relearn keeps what the current room's header just said -- exits AND their
--      walked/unwalked state -- so the step after a relearn goes somewhere real. Build a
--      stall by hand: one unreachable queued exit far away, and a room whose every
--      neighbour is already mapped.
fresh_client(); dofile(PLUGIN)
csg("enable"); csg("auto on")
csg("set 9 9 0")
room(layer3('{ "w": 0 }'))               -- far away: queues [9 9 0]w, soon unreachable
arm()
csg("set 0 1 0")
room(layer3('{ "s": 60494 }'))           -- pre-map both neighbours of the stall room
arm()
csg("set 0 -1 0")
room(layer3('{ "n": 60494 }'))
arm()
csg("set 0 0 0")
room(layer3('{ "n": 0, "s": 60494 }'))   -- the stall room: all targets known
arm()
clear()
for _ = 1, 25 do                          -- exhaust the blind budget bouncing n/s
  sent = {}
  csg("step")
  local mv = sent[1]
  if said("relearning from this room") then break end
  if mv then header(mv == "n" and "s" or "n") end   -- arrive; way back only
end
check(said("relearning from this room"), "an unreachable frontier ends in a relearn, not a stop")
-- the relearn re-enters cs_step itself: the step out of the relearned room is already on the
-- wire in the same breath as the note
check(#sent == 1 and DIRS[sent[1]] ~= nil,
      "...and the step out of the relearned room goes in the same breath (sent "
      .. tostring(sent[1]) .. ")")
header(sent[1] == "n" and "s,e" or "n,e")   -- arrive somewhere with a fresh exit
clear(); sent = {}
csg("step")
check(#sent == 1 and DIRS[sent[1]] ~= nil, "...and exploration carries on from there")

-- 36c2. The relearn WIPES the old rooms, and that is load-bearing, not hygiene. The kept
--      exits are queued because their targets are unknown -- which they only are once the
--      old map is gone. A relearn that kept the rooms would find every kept exit "already
--      explored" and go straight back to being stuck. Constructed in a header-only world,
--      where exits carry no server state and the coordinate check is all there is.
fresh_client(); dofile(PLUGIN)
csg("enable"); csg("auto on")
csg("set 9 9 0")
room(layer3('{ "w": 0 }'))               -- the far unreachable entry again
arm()
csg("set 0 1 0")
room(layer3('{ "s": 60494 }'))           -- pre-map both neighbours
arm()
csg("set 0 -1 0")
room(layer3('{ "n": 60494 }'))
arm()
csg("set 0 0 0")
header("n,s")                            -- the stall room is known from its HEADER alone
clear()
for _ = 1, 30 do
  sent = {}
  csg("step")
  local mv = sent[1]
  if said("relearning from this room") then break end
  if mv then header(mv == "n" and "s" or "n") end
end
check(said("relearning from this room"), "a header-only stall relearns too")
-- The discriminator is the ROUTE note. Every pre-relearn step in this world is blind (the
-- only queued entry is unreachable), so "[..] -> [..]: N steps" can only have been printed
-- by the post-relearn selection -- and it is only printed if the wipe made the kept exits'
-- targets unknown again. A relearn that kept the rooms would blind-step here instead.
check(said("] -> ["),
      "...and the step out is a PLANNED route, possible only because the wipe re-opened"
      .. " the kept exits")

-- 36c3. The bottom of the last resort: a relearn whose current room has NO exits left in
--      the map (a desync makes this real - every refusal deletes a genuine exit from the
--      wrong room, until the room the bot is standing in reads as exitless). The one
--      describer left is the room itself: the bot sends 'look', the header that comes back
--      re-seeds the map, and the walk resumes. The only place it ever asks the MUD to
--      re-describe a room.
fresh_client(); dofile(PLUGIN)
csg("enable"); csg("auto on")
csg("set 0 0 0")
header("n")                              -- the room is known to have one exit...
sent = {}
csg("step")
check(sent[1] == "n", "the one exit is planned")
fire_trigger("You cannot go")            -- ...which the MUD refuses: the map was wrong
clear(); sent = {}
csg("step")
check(said("asking it to describe itself"), "an exitless relearn says what it is doing")
check(sent_has("look"), "...and sends look, the one re-describe the bot ever asks for")
header("n,e")                            -- the room answers with its real exits
clear(); sent = {}
prompt()
check(#sent == 1 and DIRS[sent[1]] ~= nil,
      "...and the walk resumes from the room's real exits (sent " .. tostring(sent[1]) .. ")")

-- 36d. 'cs reset' restores the relearn budget: it is three per SEA, not three per ever.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(SINGLE_EXIT); roommap(MAP_N_ONLY)
prompt()
for i = 1, 130 do
  roommap(i % 2 == 1 and MAP_S_ONLY or MAP_N_ONLY)
  prompt()
end
check(said("still stuck after 3 relearns"), "(the budget is spent)")
csg("reset")
clear()
csg("auto on")
room(SINGLE_EXIT); roommap(MAP_N_ONLY)
prompt()
for i = 1, 30 do
  roommap(i % 2 == 1 and MAP_S_ONLY or MAP_N_ONLY)
  prompt()
end
check(said("relearning from this room"), "after 'cs reset' the relearn budget is back")

-- ...and a room with genuinely nothing to walk no longer stops on the spot: the bot cannot
-- tell "this room has no exits" from "a desync ate this room's record" (both read as an
-- empty exit set), so it spends its relearn budget asking the room to describe itself.
-- Three unanswered looks and it stops honestly. It never blind-steps into thin air.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(ONLY_OUT_ROOM)              -- the only exit is 'out', which is never stored
sent = {}; clear()
csg("step")
check(sent_has("look") and not DIRS[sent[1] or ""],
      "a room with no walkable exit asks to be described rather than stepping into thin air")
for _ = 1, 4 do prompt() end     -- the look goes unanswered (no header): budget burns down
check(said("still stuck after 3 relearns"), "...three unanswered looks and it stops honestly")

-- 36. THE GATE ITSELF, on the path that was never gated: the CONTINUATION of a multi-step
--     route. It goes out from cs_advance on the prompt, and the prompt can beat Room.Map --
--     so the burst for the room being left would confirm the step that had just gone out,
--     pos would advance twice for one move, and the router would start planning through
--     exits that are not there. Single-step plans were fine; long routes drifted.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NSE); arm()
prompt()                                   -- step 1 of the route goes out
local after_first = #sent
room(L3_NS)                                -- Room.Info: the burst has STARTED, not ended
prompt()                                   -- ...and the prompt beats Room.Map
check(#sent == after_first, "the next step of a route waits for Room.Map, not the prompt")
roommap(MAP_NS)                            -- the burst ends
prompt()
check(#sent == after_first + 1, "...and goes out once the burst has")

-- 37. ...and a step that is REFUSED must re-arm, or nothing ever would: no Room.Map follows
--     a move that did not happen, and the bot would sit in the room for ever.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NSE); arm()
prompt()
sent = {}
fire_trigger("cannot go", "n")             -- the MUD refuses it
fire_timers()                              -- cs_blocked's one-second replan
check(#sent >= 1, "a refused step re-arms, so the replan can actually walk")
check(DIRS[sent[1]] ~= nil, "...with a bare direction")

-- 38. Same for a step we concluded never landed.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NSE); arm()
prompt()
clear()
tick(); tick(); tick()                     -- the 3s timeout
check(said("the step is unconfirmed"), "the timeout fires")
sent = {}
csg("step")
check(#sent == 1, "...and the bot is armed again rather than stuck")
check(DIRS[sent[1]] ~= nil, "...with a bare direction")

-- 39. THE CORRIDOR. Captured 23 Aug: walking between two identical rooms sends NOTHING --
--     Room.Info, Room.Map and Room.Contents are all suppressed when the payload has not
--     changed. Two of four steps along one corridor produced no Room.Map at all. The MUD's
--     own room header is the only thing that arrives every time, and it carries the exits.
local function header(ex) return fire_trigger("Layer", ex) end

fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NS); arm()
prompt()                                   -- step 1 goes out
local n1 = #sent
header("s,n")                              -- the ONLY thing the corridor sends
prompt()
check(#sent == n1 + 1, "a room described only by its header still confirms and arms")

-- ...and it keeps going, step after step, with no GMCP whatsoever.
local n2 = #sent
header("s,n")
prompt()
check(#sent == n2 + 1, "...step after step, with no GMCP at all")

-- 40. The exits come from the header when nothing else supplied them.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); arm()
csg("step")
header("w")                                -- the room we land in has one exit, west
prompt()
sent = {}
csg("step")
check(#sent == 1 and sent[1] == "w", "the header's exit list is the one walked")

-- 41. It arms even with nothing outstanding -- it is the end of the room's output either
--     way -- but it must not invent a move when no step is in flight.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); arm()
clear()
header("s,n")
check(not said("z corrected"), "a header with no step outstanding moves nothing")

-- 41b. When Room.Map DID confirm, the header arrives behind it in the same burst and must
--      not confirm a second time -- that would advance pos twice for one move, which is the
--      exact fault this whole evening has been about.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); arm()
csg("step")                                -- steps n or s from [0 0 0]
roommap(MAP_NS)                            -- Room.Map confirms it: pos is now one room away
header("s,n")                              -- ...and the header follows it in the same burst
prompt()
clear()
csg("step")
check(said("[0 1 0] ->") or said("[0 -1 0] ->"),
      "the header does not confirm a second time when Room.Map already did")
check(not (said("[0 2 0] ->") or said("[0 -2 0] ->")),
      "...so pos advances once per move, not twice")

-- 41c. The pattern must not be end-anchored: the client renders the map on the same line,
--      so anything after the exits would defeat it. (Structural -- the harness has no regex
--      engine, so this checks the pattern rather than matching a line with it.)
local hpat
for _, tr in ipairs(triggers) do
  if tr.pattern:find("Sea of Chaos", 1, true) then hpat = tr.pattern end
end
check(hpat ~= nil, "the header trigger is registered")
check(hpat and not hpat:find("$", 1, true), "...and is not end-anchored")
-- ...and it must survive the '=S=' marker still being switched on. The marker is a PREFIX,
-- so with it enabled the line reads "=S=Layer three of the Sea of Chaos (s,n)   =S=" and a
-- pattern anchored on "Layer" matches none of them -- handing the corridor stall straight
-- back to anyone who had not turned the markers off. (Structural: the harness has no regex
-- engine, so this checks the pattern rather than matching a line with it.)
check(hpat and hpat:find("=S=", 1, true) ~= nil,
      "...and tolerates a leading =S= for players who still have the markers on")

-- 42. A teleport confirmed only by the header is still a teleport.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); arm()
csg("step")
clear()
fire_trigger("Suddenly you find yourself", nil)
header("s,n")
check(said("teleported"), "a teleport is caught on the header path too")

-- 43. The panel line: the layer the server named, rooms explored, mobs killed.
local function stats() return tostring(state["plugin.3s-chaossea.stats"] or "") end

fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); arm()               -- "Layer three ...", so this room IS layer three
check(stats():find("^3 ") ~= nil, "the layer shown is the one the server named")

-- ...and it stays right as the bot descends, because it is anchored rather than assumed.
-- The old line computed 1 - pos.z, which read 4 on a sea whose header said "Layer five".
room(L4_NS); arm()
check(stats():find("^4 ") ~= nil, "...and follows the server down a layer")

-- 44. Kills are counted, and counted once per killing blow.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NS); roommap(MAP_NS)
room(L3_NS); contents(MOB); roommap(MAP_NS)
prompt()                                     -- attacks: fighting is now true
fire_trigger("killing blow", "a cave troll")
check(stats():find("kills 1") ~= nil, "a killing blow is counted")
fire_trigger("There is no", "mutant")        -- fight over
check(stats():find("kills 1") ~= nil, "...once, not again when the fight ends")

-- 45. A reset zeroes it, so the count reads as "this sea" rather than for ever.
csg("reset")
check(stats():find("kills 0") ~= nil, "a reset starts the count again")

-- 46. THE EXPLORATION BIT. Confirmed by Joakim: in exits, 0 means you have NOT taken that
--     exit and a number means you have. The destination is worthless (every maze room is
--     60494) -- the whole signal is zero versus not-zero, and it is the SERVER keeping the
--     record, so it survives the coordinate collisions dead reckoning cannot.
--
--     Every case below is built so the two answers DISAGREE. The bot's own test is "is there
--     a room at the coordinate this exit leads to?", so the room north is put on the map
--     FIRST -- after which coordinates say "explored" for a north exit no matter what. Only
--     the server can tell the two apart, which is the entire point.
local function layer3(exits)
  return '{ "exits": ' .. exits .. ', "area": "Unknown", "name": "Layer three of the Sea of Chaos", "num": 60494 }'
end

-- 46a. Server says NOT walked; coordinates say explored. The server wins.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("set 0 1 0")                          -- put a room on the map to the north...
room(layer3('{ "s": 60494 }')); arm()
csg("set 0 0 0")                          -- ...then stand south of it
room(layer3('{ "n": 0 }')); arm()         -- north: never walked, says the server
sent = {}
csg("step")
check(#sent == 1 and sent[1] == "n",
      "an exit the server calls unwalked is explored even though we know the room beyond")

-- 46b. And the other way: an exit the server later reports as WALKED must stop being
--      offered, even though a frontier entry for it is already queued. Without this, 46a
--      would pass just as well on a bot that offers everything it can see.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("set 0 0 0")
room(layer3('{ "n": 0 }')); arm()         -- never walked: this QUEUES a frontier entry
csg("set 0 1 0")
room(layer3('{ "s": 60494 }')); arm()     -- now the room north is on the map too
csg("set 0 0 0")
room(layer3('{ "n": 60494 }')); arm()     -- ...and the server now says north IS walked
sent = {}
csg("step")
check(#sent == 0, "a queued exit the server later calls walked is dropped, not walked again")

-- (The room header carries exit names and NO destinations, so set_exits keeps the previous
--  per-exit state rather than blanking it. NOT covered by a test: the header only touches
--  exits while CONFIRMING a step, and only into a room we have seen before -- the harness
--  cannot stage that without a multi-step plan it has no way to force. Defensive, and
--  correct on inspection; not verified.)

-- 48. A DOWN exit the server has never seen walked is announced even when our map already
--     has something at that coordinate.
-- The lower room must be named for the layer it is ON. The anchor ties z to the layer the
-- server reports, so a room at z -1 calling itself "Layer three" makes every later "Layer
-- three" drag z back down -- and the down exit would then point at an unknown square, which
-- is the case this test exists to exclude.
local function layer4(exits)
  return '{ "exits": ' .. exits .. ', "area": "Unknown", "name": "Layer four of the Sea of Chaos", "num": 60494 }'
end
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("set 0 0 -1")                         -- a room already known one layer down
room(layer4('{ "n": 60494 }')); arm()
csg("set 0 0 0")
clear()
room(layer3('{ "d": 0 }'))
check(said("DOWN exit here"), "an unwalked down exit is called out despite the known room")

-- 50. A step YOU asked for goes out even when nothing has ended a burst -- which is the
--     state you are in the moment you enable the bot on a map restored from the store.
--     Silently doing nothing there is what broke ChaosSeaDelegationTests, and it would have
--     done the same to you at the keyboard.
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS)                      -- Room.Info only: a burst has started and not ended
sent = {}
csg("step")
check(#sent == 1, "'cs step' walks even with nothing having armed the bot")
check(DIRS[sent[1]] ~= nil, "...with a bare direction")

-- 51. ...but the AUTOMATIC paths stay gated. This is the whole point of the distinction:
--     they are the ones that fire in the middle of arriving output.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("auto on")
room(L3_NS)                      -- again: burst started, not ended
sent = {}
prompt()
check(#sent == 0, "the automatic path still waits for the burst to end")
roommap(MAP_NS)
prompt()
check(#sent == 1, "...and goes once it has")

-- ---------------------------------------------------------------------------------------
-- 52-57. THE LOS PICTURE MAY ADD AN EXIT AND MAY NEVER TAKE ONE AWAY.
--
-- Room.Map arrives just BEFORE the room header and confirms the step too, so whichever of
-- the two got there first used to decide the room's exits -- and the picture is a drawing
-- cropped to what is in sight, with its rows right-trimmed, so a link one column past the
-- end of a shorter row simply is not there to read. A room that lost an exit that way had
-- no edge for BFS and no frontier entry to walk to; the bot re-oriented ("maze shifted -
-- blind step") until it gave up with "N unexplored exits but no path".

local function disconnect() for _, h in ipairs(disconnect_hooks) do h() end end

-- 52. A step is outstanding, the picture that confirms it can read no link at all, and the
--     header that follows names two exits. The room ends up with both.
--
--     Note what this does and does not prove. exits_from_map now returns nil rather than an
--     empty list, and the picture MERGES rather than replacing -- but the header applies its
--     own list a line later either way, so neither of those two shows up here. They are the
--     fallback for a room the header does not describe (its trigger not firing, or its
--     exit list absent), and the suite does NOT isolate them: 54 is the load-bearing test.
fresh_client(); dofile(PLUGIN)
csg("enable"); csg("auto on")
csg("set 0 0 0")
room(layer3('{ "n": 0 }'))
arm()
sent = {}
csg("step")
check(sent[1] == "n", "the step goes out")
roommap('{ "rows": [ "        @        " ], "w": 17, "h": 1, "kind": "los" }')
header("s,e")
sent = {}
csg("step")
check(#sent == 1 and sent[1] == "e",
      "a room whose picture showed nothing still has the exits the header named (sent "
      .. tostring(sent[1]) .. ")")

-- 54. The header has the last word. Room.Map confirms the step first and describes the room
--     from a picture showing only the way we came; the header that follows names three.
fresh_client(); dofile(PLUGIN)
csg("enable"); csg("auto on")
csg("set 0 0 0")
room(layer3('{ "n": 0, "s": 0 }'))
arm()
sent = {}
csg("step")
check(#sent == 1, "a step is outstanding")
roommap('{ "rows": [ "        @-O      " ], "w": 17, "h": 1, "kind": "los" }')
header("e,n,s")
sent = {}
csg("step")
check(#sent == 1 and (sent[1] == "n" or sent[1] == "s"),
      "the header's exit list survives a Room.Map that confirmed the step first")

-- 55. An exit we have walked is not queued again when the room is described afresh.
--     Room.Info for the room we left is suppressed whenever its payload has not changed, so
--     the server's own 0-means-unwalked bit can be a long time reaching us -- or never. The
--     bot walked the exit; that is enough to know.
fresh_client(); dofile(PLUGIN)
csg("enable"); csg("auto on")
csg("set 0 0 0")
room(layer3('{ "n": 0, "e": 0 }'))
arm()
sent = {}
csg("step")
local first = sent[1]
check(first == "n", "the newest unwalked exit is taken first")
header("s")                                        -- arrived: a dead end but for the way back
csg("set 0 0 0")                                   -- stand back on the first room...
header("n,e")                                      -- ...and let it describe itself again
sent = {}
csg("step")
check(#sent == 1 and sent[1] == "e",
      "describing the room again does not re-offer the exit we walked (sent "
      .. tostring(sent[1]) .. ")")

-- 56. Nor does describing it again queue the same unwalked exit twice.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("set 0 0 0")
room(layer3('{ "n": 0, "e": 0, "s": 0 }'))
arm()
header("n,e,s")
header("n,e,s")
disconnect()                                       -- flushes the debounced write-through
local saved = tostring(store["frontier"] or "")
check(saved ~= "", "the frontier was actually written (otherwise this test proves nothing)")
local n = 0 ; for _ in saved:gmatch("[^\n]+") do n = n + 1 end
check(n == 3, "three unwalked exits make three frontier entries however often the room is"
      .. " described (got " .. n .. ")")

-- 57. A source that lists a room's exits in full may still REMOVE one. Room.Info is such a
--     source, so a room it re-describes without an exit no longer has it.
fresh_client(); dofile(PLUGIN)
csg("enable")
csg("set 0 0 0")
room(layer3('{ "n": 60494, "s": 0 }'))
arm()
room(layer3('{ "n": 60494 }'))                     -- south is gone
arm()
sent = {}
csg("step")
check(#sent == 1 and sent[1] == "n",
      "an exit dropped from a full exit list is dropped from the room (sent "
      .. tostring(sent[1]) .. ")")

-- 58. Loop (2.3.0): at the cask the bot clears the room, THEN runs New Sea by itself. The
--     user's rule: it must wait for the last monster to die before it opens the cask.
local MOB_CASK = '{ "full": 1, "items": [ { "type": "monster", "count": 1, "name": "A enormous misfigured abomination {chaotic}" }, { "type": "item", "count": 1, "name": "a battered cask" } ] }'
local function opened() return sent_has("open cask") end
local function ticks(n) for _ = 1, n do tick() end end
local function loop_at_cask(json)
  fresh_client(); dofile(PLUGIN)
  csg("enable"); csg("auto on"); csg("loop on")   -- Loop is meant to run with Auto
  room(L3_NS); contents(json)
  sent = {}; clear()
  prompt()
end

-- a monster beside the cask: attacked, and nothing opens while the fight goes on
loop_at_cask(MOB_CASK)
check(said("CASK found - Loop"), "loop: the cask is announced as a loop stop")
check(sent_has("kill mutant"), "loop: the cask room's monster is attacked, not left standing")
for _ = 1, 12 do tick(); combat(CC_HIT) end      -- a long fight, rounds keep coming
check(not opened(), "loop: no cask while the monster is still hitting us")
fire_trigger("killing blow", "a chaos mutant")
combat(CC_END)
fire_timers()                                    -- kill_delay, then the re-kill
check(sent[#sent] == "kill mutant", "loop: after the kill the room is checked for another")
ticks(5)
check(not opened(), "loop: the room is not called clear until the re-kill answers")
fire_trigger("There is no", "mutant")
ticks(3)
check(opened(), "loop: the last monster dead and the room quiet -> New Sea opens the cask")
fire_timers()                                    -- incl. anything the kill chain queued
check(not said("paused - unpause first"), "loop: the cleared room does not nag about the pause")

-- a monster that walks in and attacks during the wait restarts the clock and is killed
loop_at_cask(CASK)
check(not sent_has("kill mutant"), "loop: an empty cask room starts no fight")
ticks(2)
combat(CC_HIT)                                   -- something attacks us at the cask
ticks(1)
check(not opened(), "loop: combat during the wait holds the swap")
fire_trigger("killing blow", "a chaos mutant")   -- the chain knows it is fighting now
combat(CC_END)
fire_timers()
check(sent[#sent] == "kill mutant", "loop: an attacker at the cask is fought through the kill chain")
fire_trigger("There is no", "mutant")
ticks(2)
check(not opened(), "loop: quiet must last a few seconds first")
ticks(1)
check(opened(), "loop: ...then the new sea goes")

-- a missed 'There is no X here' cannot wedge the loop
loop_at_cask(MOB_CASK)
combat(CC_HIT); fire_trigger("killing blow", "a chaos mutant"); combat(CC_END)
fire_timers()
ticks(9)
check(not opened(), "loop: a fight flag is trusted for a while")
ticks(1)
check(opened(), "loop: ...but not for ever - a missed line does not stop the loop")

-- the full swap runs and the new sea comes up unpaused
fire_timers()
check(sent_has("enter sea"), "loop: the swap enters the new sea")
check(said("new sea #1 ready"), "loop: and says it is ready")

-- a player in the room: never attacked, but the loop still goes once it is quiet
loop_at_cask('{ "full": 1, "items": [ { "type": "monster", "count": 1, "name": "A household guard in service to Walker [Legendary] [Grizzled] [4]" }, { "type": "player", "count": 1, "name": "Jarl of Ein, Walker" }, { "type": "item", "count": 1, "name": "a battered cask" } ] }')
check(not sent_has("kill mutant"), "loop: a player in the cask room means no attack")
ticks(3)
check(opened(), "loop: ...and the loop still goes")

-- Loop off: the old behaviour, the bot waits at the cask for you
fresh_client(); dofile(PLUGIN)
csg("enable")
room(L3_NS); contents(MOB_CASK)
sent = {}; prompt(); ticks(30)
check(not opened() and not sent_has("kill mutant"), "loop off: paused at the cask, nothing sent")

-- unpausing yourself takes the cask back
loop_at_cask(CASK)
csg("pause")                                     -- toggles it off: you are going on
ticks(10)
check(not opened(), "loop: unpausing at the cask cancels the swap")
check(not said("New Sea only works"), "loop: ...quietly - it does not try and get refused")
csg("pause")                                     -- ...and pausing later, anywhere, is just a pause
ticks(10)
check(not opened(), "loop: a cancelled swap does not come back on the next pause")

-- switching Loop off at the cask cancels it too
loop_at_cask(CASK)
csg("loop off")
ticks(10)
check(not opened(), "loop: 'cs loop off' at the cask stops it there")

-- a portal is not a cask: Loop never swaps there
fresh_client(); dofile(PLUGIN)
csg("enable"); csg("loop on")
room(L3_NS); contents('{ "full": 1, "items": [ { "type": "item", "count": 1, "name": "a shimmering portal" } ] }')
sent = {}; prompt(); ticks(10)
check(said("PORTAL found") and not opened(), "loop: a portal pauses as before, no swap")

-- a sea this session started is waited out to 60 minutes, then loops
loop_at_cask(CASK)
ticks(3); fire_timers()                           -- first swap: the clock starts now
room(ENTER_SEA); arm()
contents(CASK); sent = {}; clear()
prompt()                                          -- the next cask, minutes later
ticks(10)
check(not opened() and said("sea only 0m old"), "loop: a young sea is waited out, and says so")
local announced = 0
for _, l in ipairs(printed) do if l:find("sea only", 1, true) then announced = announced + 1 end end
ticks(3500)
check(not opened(), "loop: still waiting before the hour")
check(announced == 1, "loop: the wait is announced once, not every second")
ticks(100)
check(opened(), "loop: at 60 minutes the next sea goes")

-- the button, and the setting survives a reload
fresh_client(); dofile(PLUGIN)
local lb = find_button(panel, "Loop")
check(lb ~= nil, "loop: a Loop button on the panel")
lb.action()
check(find_button(panel, "Loop ON") ~= nil, "loop: it shows ON once pressed")
check(store["loop"] == "1", "loop: the setting is kept")
local kept = store
fresh_client(); store = kept; dofile(PLUGIN)
check(find_button(panel, "Loop ON") ~= nil, "loop: ...and is still on after a reload")

-- ---------------------------------------------------------------------------------------
-- 60. FINDING OURSELVES ON THE SAVED MAP (2.4.0, Joakim 26 Oct 2026: "it sometimes gets
--     lost and has to reset and start over"). The sea is static once made, so the map the
--     bot built stays true when its idea of WHERE it is does not. A room cannot be told by
--     its number (all 60494), but it can by its shape: the Room.Map picture is a patch of
--     the maze, and it fits the saved map in one place.
--
--     The maze below is drawn in Room.Map's own notation (rooms every 2 columns and rows,
--     links between), so every picture the bot is shown is a window cut from it - the
--     format of the verbatim captures above, at any crop. The whole maze is the "saved
--     map", written into the store the way save_state writes it.
local MAZE = {
  "O-O-O-O O-O-O-O-O",
  "|     | |     |  ",
  "O O-O O-O O-O O-O",
  "| | | |   |X  |  ",
  "O-O O O-O-O O-O O",
  "  |   |     |\\  |",
  "O-O-O O O-O-O O-O",
  "|     | |   | |  ",
  "O O-O-O O O O-O-O",
  "| |     | | |/   ",
  "O-O O-O-O O-O-O-O",
}
local function mz_ch(r, c)
  local l = MAZE[r]
  if not l or c < 1 or c > #l then return " " end
  return l:sub(c, c)
end
local function mz_is_room(r, c) return mz_ch(r, c) == "O" end
-- the maze is well formed: every link joins two rooms
do
  local ok = true
  for r = 1, #MAZE do
    for c = 1, #MAZE[r] do
      local ch = mz_ch(r, c)
      if ch == "-" then ok = ok and mz_is_room(r, c - 1) and mz_is_room(r, c + 1)
      elseif ch == "|" then ok = ok and mz_is_room(r - 1, c) and mz_is_room(r + 1, c)
      elseif ch == "\\" then ok = ok and mz_is_room(r - 1, c - 1) and mz_is_room(r + 1, c + 1)
      elseif ch == "/" then ok = ok and mz_is_room(r + 1, c - 1) and mz_is_room(r - 1, c + 1)
      elseif ch == "X" then ok = ok and mz_is_room(r - 1, c - 1) and mz_is_room(r + 1, c + 1)
                                    and mz_is_room(r + 1, c - 1) and mz_is_room(r - 1, c + 1) end
    end
  end
  check(ok, "(the test maze is well formed)")
end
local MZ_STEP = { n = { -2, 0 }, s = { 2, 0 }, e = { 0, 2 }, w = { 0, -2 },
                  ne = { -2, 2 }, nw = { -2, -2 }, se = { 2, 2 }, sw = { 2, -2 } }
local function mz_exits(r, c)
  local ex = {}
  if mz_ch(r, c + 1) == "-" then ex[#ex + 1] = "e" end
  if mz_ch(r, c - 1) == "-" then ex[#ex + 1] = "w" end
  if mz_ch(r - 1, c) == "|" then ex[#ex + 1] = "n" end
  if mz_ch(r + 1, c) == "|" then ex[#ex + 1] = "s" end
  -- 'X' is both diagonals crossing
  local function diag(ch, want) return ch == want or ch == "X" end
  if diag(mz_ch(r - 1, c + 1), "/") then ex[#ex + 1] = "ne" end
  if diag(mz_ch(r + 1, c - 1), "/") then ex[#ex + 1] = "sw" end
  if diag(mz_ch(r + 1, c + 1), "\\") then ex[#ex + 1] = "se" end
  if diag(mz_ch(r - 1, c - 1), "\\") then ex[#ex + 1] = "nw" end
  table.sort(ex)
  return ex
end
-- drawing (r, c) <-> map (x, y): x east, y north, the top-left room at 0 0
local function mz_xy(r, c) return (c - 1) // 2, -((r - 1) // 2) end
local function mz_rooms()
  local out = {}
  for r = 1, #MAZE, 2 do
    for c = 1, #MAZE[r], 2 do
      if mz_is_room(r, c) then out[#out + 1] = { r, c } end
    end
  end
  return out
end
-- the saved map, as save_state writes it; `keep(r, c)` picks the rooms that were explored
local function mz_store(keep, z)
  local lines = {}
  for _, rc in ipairs(mz_rooms()) do
    if not keep or keep(rc[1], rc[2]) then
      local x, y = mz_xy(rc[1], rc[2])
      lines[#lines + 1] = string.format("%d|%d|%d|%s", z or 0, x, y, table.concat(mz_exits(rc[1], rc[2]), ","))
    end
  end
  return table.concat(lines, "\n")
end
-- The Room.Map picture standing at (r, c): the 17x9 window, rooms within `k` rooms drawn
-- and the links one step past them (a link to a room out of sight is drawn - MAP_W), rows
-- right-trimmed. k = nil draws the whole window.
local function mz_window(r, c, k)
  local rows = {}
  for dr = -4, 4 do
    local line = {}
    for dc = -8, 8 do
      local ch = mz_ch(r + dr, c + dc)
      local room_cell = dr % 2 == 0 and dc % 2 == 0
      local lim = room_cell and 2 * k or (k and 2 * k + 1)
      if k and (math.abs(dr) > lim or math.abs(dc) > lim) then ch = " " end
      if dr == 0 and dc == 0 then ch = "@" end
      line[#line + 1] = ch
    end
    local s = table.concat(line):gsub("%s+$", ""):gsub("\\", "\\\\")
    rows[#rows + 1] = '"' .. s .. '"'
  end
  return '{ "rows": [ ' .. table.concat(rows, ", ") .. ' ], "h": 9, "w": 17, "kind": "los" }'
end
local function mz_info(r, c)
  local parts = {}
  for _, d in ipairs(mz_exits(r, c)) do parts[#parts + 1] = '"' .. d .. '": 60494' end
  return layer3("{ " .. table.concat(parts, ", ") .. " }")
end
-- One arrival at (r, c), the whole burst as the server sends it, and the prompt.
local function mz_arrive(r, c, k)
  room(mz_info(r, c))
  roommap(mz_window(r, c, k))
  header(table.concat(mz_exits(r, c), ","))
  prompt()
end
local function found_at()
  for _, l in ipairs(printed) do
    local x, y, z = l:match("this room is at %[(%-?%d+) (%-?%d+) (%-?%d+)%] on the saved map")
    if x then return tonumber(x), tonumber(y), tonumber(z) end
  end
  return nil
end
-- Standing in (sr, sc) with the map loaded; `walking` puts the bot on Auto, and the first
-- prompt then has it step out (a blind step: the saved map has no frontier).
local NOTES = {}
local function mz_start(store_map, sr, sc, walking, k, z)
  fresh_client()
  NOTES = {}
  local sx, sy = mz_xy(sr, sc)
  store.map = store_map
  store.pos = sx .. "|" .. sy .. "|" .. (z or 0)
  dofile(PLUGIN)
  scrye.notify = function(m) NOTES[#NOTES + 1] = m end
  csg("enable")
  if walking then csg("auto on") end
  mz_arrive(sr, sc, k or 2)                -- standing in the start room: anchors the layer
end

-- 60a. Thrown across the maze, the bot finds the room on the map - by one picture or by
--      walking a few rooms - and goes on from the RIGHT place. Every room of the maze as
--      the landing spot, twice over (pictures two rooms deep, and one room deep), with the
--      walk's choices seeded: wherever it says it is, it is. (The odds of a WRONG place are
--      what the evidence rule is tuned against - on simulated mazes of up to 1500 mapped
--      rooms, corridor-heavy ones the hardest; this suite pins the behaviour, not the odds.)
local START_R, START_C = 5, 9
local wrong, found_n, runs = {}, 0, 0
local function teleport_run(tr, tc, k, seed, store_map)
  math.randomseed(seed)
  mz_start(store_map, START_R, START_C, true)
  check(#sent == 1, "(setup) the bot is walking when the sea takes it")
  sent = {}; clear()
  fire_trigger("Suddenly you find yourself", nil)
  local r, c = tr, tc
  mz_arrive(r, c, k)
  for _ = 1, 40 do
    if found_at() or said("could not place them") then break end
    local d = sent[#sent]
    local mv = d and MZ_STEP[d]
    if not mv then break end
    check(mz_ch(r + mv[1] / 2, c + mv[2] / 2) ~= " ", "(the lost walk only takes real exits)")
    r, c = r + mv[1], c + mv[2]
    sent = {}
    mz_arrive(r, c, k)
  end
  return r, c
end
for _, k in ipairs({ 2, 1 }) do
  for i, rc in ipairs(mz_rooms()) do
    runs = runs + 1
    local r, c = teleport_run(rc[1], rc[2], k, i * 7 + k, mz_store())
    local fx, fy = found_at()
    if fx then
      found_n = found_n + 1
      local tx, ty = mz_xy(r, c)
      if fx ~= tx or fy ~= ty then wrong[#wrong + 1] = string.format("k%d %d,%d: said %d %d, truly %d %d", k, rc[1], rc[2], fx, fy, tx, ty) end
    end
  end
end
check(#wrong == 0, "a teleport is never placed at the WRONG room (" .. table.concat(wrong, "; ") .. ")")
check(found_n >= runs * 0.8, string.format("...and nearly always found (%d of %d)", found_n, runs))

-- 60b. One teleport in detail: the bot says what happened, finds the room, and carries on
--      exploring from where it really is. (7,11) is a room in the thick of the maze: one
--      picture of it is enough.
local T_R, T_C = 7, 11                             -- map 5 -3, exits e,w
local T_INFO = mz_info(T_R, T_C)
local T_PIC = mz_window(T_R, T_C, 2)
local T_HDR = table.concat(mz_exits(T_R, T_C), ",")
math.randomseed(3)
mz_start(mz_store(), START_R, START_C, true)
sent = {}; clear()
fire_trigger("Suddenly you find yourself", nil)
room(T_INFO)
check(said("teleported inside the maze") and said("looking for this room on the saved map"),
      "a teleport says the position is unknown and that it is looking")
check(tostring(state["plugin.3s-chaossea.status"]):find("LOST", 1, true) ~= nil, "the panel says LOST meanwhile")
roommap(T_PIC)
header(T_HDR)
local fx, fy = found_at()
check(fx == 5 and fy == -3, "a distinctive room is found from its first picture (" .. tostring(fx) .. " " .. tostring(fy) .. ")")
check(said("bits of evidence"), "...and says how sure it is")
check(said("carrying on exploring"), "...and the bot carries on by itself")
sent = {}
prompt()
check(sent[1] == "e" or sent[1] == "w", "...its next step is a real exit of the room it is really in (" .. tostring(sent[1]) .. ")")

-- 60c. Landed somewhere the map has never seen: the walk keeps looking, never "finds" a
--      room it has not mapped, and gives up honestly - paused, with the old advice.
math.randomseed(5)
local left_only = function(_, c) return c <= 9 end           -- only the west half explored
local unk_wrong, unk_found = 0, 0
for i, rc in ipairs(mz_rooms()) do
  if rc[2] >= 11 then                                        -- land in the unexplored east
    local r, c = teleport_run(rc[1], rc[2], 2, 100 + i, mz_store(left_only))
    local fx2, fy2 = found_at()
    if fx2 then
      unk_found = unk_found + 1
      local tx, ty = mz_xy(r, c)
      if fx2 ~= tx or fy2 ~= ty or c > 9 then unk_wrong = unk_wrong + 1 end
    end
  end
end
check(unk_wrong == 0, "landing in unexplored rooms never produces a wrong position (" .. unk_wrong .. ")")
check(unk_found > 0, "...and walking into the mapped part, it does find itself there (" .. unk_found .. ")")
-- an island it can never walk out of: a dead-end room, the map holding none of it
mz_start(mz_store(left_only), START_R, START_C, true)
sent = {}; clear()
fire_trigger("Suddenly you find yourself", nil)
local ISLAND = '{ "exits": { "e": 60494 }, "area": "Unknown", "name": "Layer three of the Sea of Chaos", "num": 60494 }'
local ISLAND_MAP = themap('"        @-O      "')
for _ = 1, 40 do
  room(ISLAND); roommap(ISLAND_MAP); header("e"); prompt()
  if said("could not place them") then break end
end
check(said("walking until it is sure"), "an unknown room starts the search walk")
check(said("could not place them") and said("cs reset"), "...which gives up after its budget, pointing at 'cs reset'")
check(said("walked 30 room(s)"), "...and the budget is thirty rooms")
check(#NOTES == 1 and NOTES[1]:find("position unknown", 1, true) ~= nil, "...and tells the phone, once")
check(not found_at(), "...without ever claiming a place")
sent = {}; prompt()
check(#sent == 0, "...and the bot stays paused")

-- 60d. With Auto off nobody walks for it: it says what it found and waits; you walking
--      narrows it down and gathers the evidence. (11,1) is the maze's corner: one look is
--      far from enough, the walk out of it is what decides.
mz_start(mz_store(), START_R, START_C)
sent = {}; clear()
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(11, 1, 1)
check(not found_at(), "a picture without enough behind it is not taken")
check(said("not yet surely") or said("places on the saved map"), "...and it says so")
check(said("walk a step and it looks again"), "...and that walking helps")
check(#sent == 0, "...and with Auto off it does not walk by itself")
do
  local r, c = 11, 1
  for _, d in ipairs({ "e", "n", "e", "e", "n", "n", "e", "e", "s" }) do
    if found_at() then break end
    cmd(d)
    r, c = r + MZ_STEP[d][1], c + MZ_STEP[d][2]
    mz_arrive(r, c, 1)
  end
  local fx3, fy3 = found_at()
  local tx3, ty3 = mz_xy(r, c)
  check(fx3 == tx3 and fy3 == ty3, "walking it yourself narrows it to the right room ("
        .. tostring(fx3) .. " " .. tostring(fy3) .. " vs " .. tx3 .. " " .. ty3 .. ")")
end
check(said("after walking"), "...found on the trail, not from one picture")
check(said("'cs step' or 'cs auto on'"), "...and tells you how to go on")

-- 60e. A refused move while lost changes nothing about where it might be.
mz_start(mz_store(), START_R, START_C)
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(11, 1, 1)                          -- the corner: not enough to be sure
cmd("w")
fire_trigger("You cannot go", "west")       -- there is no west there
clear()
csg("locate")
check(said("not yet surely") or said("places on the saved map"),
      "after a refused move the room is still the one it was, and still matched")

-- 60f. 'cs locate' on demand, and its honest answers.
mz_start(mz_store(), START_R, START_C)
clear(); csg("locate")
check(said("exactly where we think we are"), "locate: the start room is where the bot thinks it is")
csg("set 0 0 0")                              -- tell it something wrong
clear(); csg("locate")
local lx, ly = found_at()
local sx0, sy0 = mz_xy(START_R, START_C)
check(lx == sx0 and ly == sy0, "locate: a wrong position is corrected from the room's shape")
fresh_client(); store.map = mz_store(); dofile(PLUGIN); csg("enable")
clear(); csg("locate")
check(said("nothing to match yet"), "locate: before any picture it says it has nothing to match")
mz_start(mz_store(left_only), START_R, START_C)
room(mz_info(1, 15)); roommap(mz_window(1, 15, 2)); header(table.concat(mz_exits(1, 15), ","))
clear(); csg("locate")
check(said("does not fit anywhere"), "locate: a room the map does not have fits nowhere")

-- 60g. A stuck route checks where it is before re-orienting. The bot believes it is in
--      the start room; it is really somewhere else (a missed step, say). Nothing on the
--      frontier: the old answer was a blind step from the wrong place.
mz_start(mz_store(), START_R, START_C)
room(T_INFO); roommap(T_PIC); header(T_HDR)
csg("auto on")
sent = {}; clear()
csg("step")
local sx, sy = mz_xy(START_R, START_C)
check(said(string.format("fits the saved map at [5 -3 0], not [%d %d 0] - position corrected", sx, sy)),
      "a stuck route finds the room on the map and corrects the position")
check(said("from [5 -3 0]"), "...and re-orients from where it really is")

-- 60h. The fit is strict. A stored neighbour at the end of a drawn link that LACKS the
--      exit rules the place out: with the map's copy of a neighbour damaged, the right
--      place no longer fits, and nothing else is taken instead.
do
  -- the '\\' link between (6,-2) and (7,-3) gone from BOTH rooms' records
  local dmg = mz_store():gsub("\n0|6|%-2|[^\n]*", "\n0|6|-2|e,s"):gsub("\n0|7|%-3|[^\n]*", "\n0|7|-3|e,s")
  mz_start(dmg, START_R, START_C)
  fire_trigger("Suddenly you find yourself", nil)
  mz_arrive(7, 13, 2)                         -- (6,-3), beside both damaged rooms
  local hx, hy = found_at()
  check(not hx, "a neighbour on the map without the drawn link rules the place out")
end

-- 60i. THE FIT, rule by rule. Each probe stands the bot somewhere off the map and PAUSED,
--      so what it is shown is only looked at, never written - then asks 'cs locate'.
local function probe(store_map, info, pic, hdr)
  fresh_client()
  store.map = store_map
  store.pos = "50|50|0"
  dofile(PLUGIN)
  csg("enable"); csg("pause")
  room(info); roommap(pic); header(hdr)
  clear(); csg("locate")
end
probe(mz_store(), T_INFO, T_PIC, T_HDR)
local px, py = found_at()
check(px == 5 and py == -3, "probe: the room is placed (control)")
-- the room's own record must hold every exit the room has...
probe((mz_store():gsub("\n0|5|%-3|[^\n]*", "\n0|5|-3|e")), T_INFO, T_PIC, T_HDR)
px, py = found_at()
check(not (px == 5 and py == -3), "fit: a mapped room missing one of this room's exits is not this room")
-- ...but a neighbour whose record lacks the way back still agrees (one end is enough)
probe((mz_store():gsub("\n0|6|%-3|[^\n]*", "\n0|6|-3|n,s")), T_INFO, T_PIC, T_HDR)
px, py = found_at()
check(px == 5 and py == -3, "fit: a link known from one end only still counts")
-- two rooms drawn side by side with nothing between them: the map may not link them
probe((mz_store():gsub("\n0|4|%-4|[^\n]*", "\n0|4|-4|n,s,e"):gsub("\n0|5|%-4|[^\n]*", "\n0|5|-4|s,w")),
      T_INFO, T_PIC, T_HDR)                    -- (4,-4)-(5,-4): drawn apart, mapped together
px, py = found_at()
check(not (px == 5 and py == -3), "fit: a link the map has between two rooms drawn apart rules the place out")
-- the evidence a place needs grows with the map: 20 bits plus log2 of the rooms mapped
-- on the layer - 54 here, so 25.8, shown rounded
probe(mz_store(), mz_info(9, 9), mz_window(9, 9, 2), table.concat(mz_exits(9, 9), ","))
check(said("not yet surely (25.0 of 25.8 bits)"), "a fit a bit short of the evidence is not taken, and says by how much")
probe(mz_store(), mz_info(5, 13), mz_window(5, 13, 2), table.concat(mz_exits(5, 13), ","))
px, py = found_at()
check(px == 6 and py == -2, "...and one past it is")
-- a picture and exits that are not of the same room are not matched at all
probe(mz_store(), layer3('{ "n": 60494 }'), themap('"        @-O      "'), "n")
check(said("nothing to match yet"), "a link drawn from us that is not one of our exits: no match attempted")
probe(mz_store(), layer3('{ "n": 60494 }'), themap('"      O-@        "'), "n")
check(said("nothing to match yet"), "...nor a link drawn TO us")
probe(mz_store(), layer3('{ "n": 60494 }'), themap('"         /       ", "        @        "'), "n")
check(said("nothing to match yet"), "...nor a diagonal one ('/' up and to the right is ne)")

-- 60j. The layer the server names picks the map layer to search. Two identical layers on
--      the map, the bot anchored on z -1 = layer three: the room is found on THAT layer.
local TWO_LAYERS = mz_store(nil, -1) .. "\n" .. mz_store(nil, 0)
mz_start(TWO_LAYERS, START_R, START_C, false, 2, -1)
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(T_R, T_C, 2)
local lx2, ly2, lz2 = found_at()
check(lx2 == 5 and ly2 == -3 and lz2 == -1, "the named layer decides which copy fits ("
      .. tostring(lx2) .. " " .. tostring(ly2) .. " " .. tostring(lz2) .. ")")
mz_start(TWO_LAYERS, START_R, START_C, false, 2, -1)
csg("pause")
room(mz_info(9, 9)); roommap(mz_window(9, 9, 2)); header(table.concat(mz_exits(9, 9), ","))
clear(); csg("locate")
check(said("[4 -4 -1], not yet surely (24.9 of 25.8 bits)"),
      "'cs locate' searches the named layer, and counts only ITS rooms toward the evidence needed")

-- 60k. Found before the layer was ever anchored (the teleport was the first room seen):
--      the find anchors it, so the panel's layer number is the server's.
fresh_client(); store.map = mz_store(); store.pos = "4|-2|0"; dofile(PLUGIN)
csg("enable")
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(T_R, T_C, 2)
check(found_at() ~= nil, "(found)")
check(tostring(state["plugin.3s-chaossea.stats"]):match("^3 ") ~= nil,
      "a find anchors the layer: the panel says layer 3 (" .. tostring(state["plugin.3s-chaossea.stats"]) .. ")")

-- 60l. The prompt can beat Room.Map. Found when the picture lands after it - and since no
--      prompt follows, the bot steps on by itself.
mz_start(mz_store(), START_R, START_C, true)
sent = {}; clear()
fire_trigger("Suddenly you find yourself", nil)
room(T_INFO); header(T_HDR); prompt()
roommap(T_PIC)
px, py = found_at()
check(px == 5 and py == -3, "a picture arriving after the prompt is still matched at once")
sent = {}
fire_timers()
check(sent[1] == "e" or sent[1] == "w", "...and the bot steps on without waiting for a prompt ("
      .. tostring(sent[1]) .. ")")

-- 60m. A teleport seen only by Room.Map (no Room.Info: the payload repeated) gets its
--      exits from the header that follows, and is found from those.
mz_start(mz_store(), START_R, START_C, true)
fire_trigger("Suddenly you find yourself", nil)
roommap(T_PIC)
header(T_HDR)
px, py = found_at()
check(px == 5 and py == -3, "a Room.Map-only teleport is found once the header names the exits")

-- 60m2. Room.Info and Room.Map are enough: found before the header has even printed.
mz_start(mz_store(), START_R, START_C)
fire_trigger("Suddenly you find yourself", nil)
room(T_INFO); roommap(T_PIC)
px, py = found_at()
check(px == 5 and py == -3, "found from Room.Info's exits and the picture, header or no header")

-- 60m3. A teleport seen only by Room.Map that is NOT found: the step that was in flight is
--       dropped with it, so unpausing does not time it out against the wrong room.
mz_start(mz_store(left_only), START_R, START_C, true)
fire_trigger("Suddenly you find yourself", nil)
roommap(mz_window(1, 15, 2)); header(table.concat(mz_exits(1, 15), ","))
check(not found_at(), "(not found: unmapped)")
csg("pause")                                   -- you take over
clear(); tick(); tick(); tick(); tick()
check(not said("the step is unconfirmed"), "the abandoned step does not outlive the teleport")

-- 60m4. The phone: told when a teleport stays unresolved for ten seconds - and told
--       again when it is resolved after all.
mz_start(mz_store(), START_R, START_C, true)
fire_trigger("Suddenly you find yourself", nil)
room(T_INFO)                                   -- and no picture, for a while
for _ = 1, 11 do tick() end
check(#NOTES == 1 and NOTES[1]:find("position unknown", 1, true) ~= nil, "a teleport unresolved for 10 s reaches the phone")
roommap(T_PIC)
check(found_at() ~= nil and #NOTES == 2 and NOTES[2]:find("found itself", 1, true) ~= nil,
      "...and so does finding itself after that")

-- 60m5. A refused step while lost is not counted as walked. (9,9) is just short of sure;
--       north of it, (7,9), settles it.
mz_start(mz_store(), START_R, START_C)
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(9, 9, 2)
check(not found_at(), "(just short)")
cmd("e"); fire_trigger("You cannot go", "east")
cmd("n"); mz_arrive(7, 9, 2)
px, py = found_at()
check(px == 4 and py == -3, "(found one room on)")
check(said("after walking 1 room(s)"), "a refused step does not count as a room walked")

local SAVED_MAZE = MAZE
-- 60m6. The corridor: GMCP sends nothing that has not changed, so the next room's Room.Map
--       may never come. Two seconds after its header the last picture stands for it, and
--       the room is judged - the walk does not stall there.
MAZE = { "O-O-O-O-O-O-O-O-O-O-O-O-O-O-O-O-O" }
math.randomseed(11)
mz_start(mz_store(), 1, 1, true)
sent = {}; clear()
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(1, 15, 2)
local d1 = sent[#sent]
check(d1 == "e" or d1 == "w", "(the search walk steps)")
sent = {}
header(table.concat(mz_exits(1, 15 + (d1 == "e" and 2 or -2)), ",")); prompt()   -- no Room.Info, no Room.Map
for _ = 1, 5 do tick() end
check(#sent == 1, "a room whose picture never came is judged on the last one, and the walk goes on")
-- and a step nothing answers at all - no room, no refusal we know the words of - is given
-- up after ten seconds and another way tried
local d2 = sent[1]
sent = {}
for _ = 1, 10 do tick() end
check(#sent == 1 and sent[1] ~= d2, "a step nothing answered is taken back, and another way is tried ("
      .. tostring(d2) .. " then " .. tostring(sent[1]) .. ")")
MAZE = SAVED_MAZE

-- 60n. The trail goes cold and starts again: lost in the unexplored east, you walk west
--      into mapped rooms, and it finds you there.
mz_start(mz_store(left_only), START_R, START_C)
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(5, 11, 2)                          -- unmapped
check(not found_at(), "(not found in unmapped rooms)")
do
  local r, c = 5, 11
  for _, d in ipairs({ "w", "w", "n", "n", "w", "s" }) do
    if found_at() then break end
    cmd(d)
    r, c = r + MZ_STEP[d][1], c + MZ_STEP[d][2]
    mz_arrive(r, c, 2)
  end
  local nx, ny = found_at()
  local tx, ty = mz_xy(r, c)
  check(nx == tx and ny == ty, "after rooms that fit nowhere, the search starts again and finds you ("
        .. tostring(nx) .. " " .. tostring(ny) .. " vs " .. tx .. " " .. ty .. ")")
end

-- 60o. The search walk, in a corridor seen one room deep - where all rooms look alike, and
--      the walk keeps its direction, waits out fights, and never claims a place.
MAZE = { "O-O-O-O-O-O-O-O-O" }
local function corridor_teleport(seed, enemy)
  math.randomseed(seed)
  mz_start(mz_store(), 1, 1, true)
  sent = {}; clear()
  fire_trigger("Suddenly you find yourself", nil)
  room(mz_info(1, 7)); roommap(mz_window(1, 7, 1)); header(table.concat(mz_exits(1, 7), ","))
  if enemy then state["enemy.name"] = "mutant" end
  prompt()
end
corridor_teleport(11)
local c, reversals, steps, from = 7, 0, {}, {}
for _ = 1, 40 do
  local d = sent[#sent]
  if found_at() or not (d == "e" or d == "w") then break end
  steps[#steps + 1] = d
  from[#from + 1] = c
  c = c + (d == "e" and 2 or -2)
  sent = {}
  mz_arrive(1, c, 1)
end
for i = 2, #steps do
  -- turning round where the corridor went on (an end room leaves only the way back)
  if steps[i] ~= steps[i - 1] and from[i] ~= 1 and from[i] ~= 17 then reversals = reversals + 1 end
end
check(#steps >= 10, "the search walk walks the corridor (" .. #steps .. " steps)")
check(reversals == 0, "...does not double back while the corridor goes on (" .. table.concat(steps, ",") .. ")")
check(not found_at(), "...and never claims a place among rooms that all look alike")
-- a fight holds the walk
corridor_teleport(12, true)
check(#sent == 0, "the search walk waits while something is hitting us")
state["enemy.name"] = nil
prompt()
check(#sent == 1, "...and walks once it is over")
-- a refused step is not tried again from the same room
corridor_teleport(13)
local first = sent[#sent]
sent = {}
fire_trigger("You cannot go", first)
prompt()
check(#sent == 1 and sent[1] ~= first, "a refused way is not tried again from the same room ("
      .. tostring(first) .. " then " .. tostring(sent[1]) .. ")")
-- the watchdog judges a room whose prompt came before its picture
math.randomseed(14)
mz_start(mz_store(), 1, 1, true)
sent = {}; clear()
fire_trigger("Suddenly you find yourself", nil)
room(mz_info(1, 7)); header(table.concat(mz_exits(1, 7), ",")); prompt()
roommap(mz_window(1, 7, 1))
check(#sent == 0, "(nothing judged the room yet)")
for _ = 1, 5 do tick() end
check(#sent == 1, "the watchdog judges it, and the walk starts")
MAZE = SAVED_MAZE

-- 60p. No floor or ceiling in the search walk: a room whose only way on is down is where
--      it stops - a descent is a one-way door.
mz_start(mz_store(), START_R, START_C, true)
sent = {}; clear()
fire_trigger("Suddenly you find yourself", nil)
room(layer3('{ "d": 60494 }')); roommap(themap('"        v        "')); header("d"); prompt()
check(not sent_has("d"), "the search walk never takes a way down")
check(said("walked 0 room(s)"), "...and with nothing else open it stops and says so")

-- 60q. Where the bot already is: a stuck route that fits right here corrects nothing.
mz_start(mz_store(), START_R, START_C, true)
check(not said("position corrected"), "a stuck route at the right position is not 'corrected'")

-- 60r. Taking it over yourself ends the search: unpausing, setting the position, or
--      switching the bot off and on. Unpausing on Auto also walks on: paused arrivals arm
--      nothing, and the teleported step had used the arming up.
local function lost_here(walking)
  mz_start(mz_store(), START_R, START_C, walking)
  fire_trigger("Suddenly you find yourself", nil)
  room(mz_info(11, 1)); roommap(mz_window(11, 1, 1)); header(table.concat(mz_exits(11, 1), ","))
  return tostring(state["plugin.3s-chaossea.status"]):find("LOST", 1, true) ~= nil
end
check(lost_here(), "(lost)")
clear(); csg("pause")
check(said("no longer looking") and not tostring(state["plugin.3s-chaossea.status"]):find("LOST", 1, true),
      "unpausing by hand ends the search")
lost_here(); csg("set 0 0 0")
check(not tostring(state["plugin.3s-chaossea.status"]):find("LOST", 1, true), "'cs set' ends the search")
lost_here(); csg("disable"); csg("enable")
check(not tostring(state["plugin.3s-chaossea.status"]):find("LOST", 1, true), "switching off and on ends it")
lost_here(true)
sent = {}
csg("pause"); fire_timers()
check(#sent == 1, "unpausing a lost bot on Auto walks on from where the position says")

-- 60s. While lost, a picture that is not of the room the exits came from is not judged.
mz_start(mz_store(), START_R, START_C)
fire_trigger("Suddenly you find yourself", nil)
clear()
room(layer3('{ "n": 60494 }')); roommap(themap('"        @-O      "')); prompt()
check(not said("saved map -") and not said("places on the saved map") and not said("not yet surely"),
      "a picture disagreeing with the exits gets no verdict")

-- 60t. A wimpy while lost moves the search, never the map: nothing is written where the
--      bot only thought it was.
mz_start(mz_store(), START_R, START_C)
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(9, 9, 2)                             -- just short of sure
fire_trigger("Your legs run away with you", "north")
mz_arrive(7, 9, 2)
px, py = found_at()
check(px == 4 and py == -3, "a wimpy while lost carries the search along with it (" .. tostring(px) .. " " .. tostring(py) .. ")")
check(not said("WIMPY"), "...and is not handled as a retreat from a known room")
-- the map's (4,-1) - north of where the bot thought it was - keeps its own exits, n and w
csg("debug_all")
local rec41
for _, l in ipairs(printed) do rec41 = rec41 or l:match("%[ 4 %-1 %] : ([%a, ]*)") end
check(rec41 and rec41:find("n", 1, true) and rec41:find("w", 1, true) and not rec41:find("e", 1, true),
      "...and the room north of the old position is not overwritten (" .. tostring(rec41) .. ")")

-- 60u. The evidence, to the tenth of a bit. A change to how it is counted shows here first.
probe(mz_store(), T_INFO, T_PIC, T_HDR)
check(said("(32.0 bits of evidence)"), "one picture of (7,11): 32.0 bits")
-- the room's exits beyond what it is known to have: walked in from the south, (9,9)'s
-- n,s says only that it goes on north
mz_start(mz_store(), START_R, START_C)
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(9, 9, 2)
cmd("n"); mz_arrive(7, 9, 2)
check(said("[4 -3 0] on the saved map (32.2 bits of evidence, after walking 1 room(s))"),
      "a room walked into adds what its exits say beyond the way back, and the trail keeps the rest")

-- 60v. More of the fit.
-- a mapped room with an exit this room does not have is not this room either
probe((mz_store():gsub("\n0|5|%-3|[^\n]*", "\n0|5|-3|e,n,w")), T_INFO, T_PIC, T_HDR)
px, py = found_at()
check(not (px == 5 and py == -3), "fit: a mapped room with an exit this room lacks is not this room")
-- a link known only from the end it runs TO (the west neighbour's record lost its east)
probe((mz_store():gsub("\n0|4|%-3|[^\n]*", "\n0|4|-3|s")), T_INFO, T_PIC, T_HDR)
px, py = found_at()
check(px == 5 and py == -3, "fit: a link known only from its far end still counts")
-- a drawn link whose one mapped end lacks it rules the place out, the other end unmapped
do
  local m = mz_store():gsub("\n0|6|%-2|[^\n]*", ""):gsub("\n0|6|%-3|[^\n]*", "\n0|6|-3|s,w")
  probe(m, T_INFO, T_PIC, T_HDR)
  px, py = found_at()
  check(not (px == 5 and py == -3), "fit: a mapped end without the drawn link rules it out, unmapped other end or not")
end
-- a gap (rooms drawn apart) linked in only ONE room's record still rules it out - either one
probe((mz_store():gsub("\n0|4|%-4|[^\n]*", "\n0|4|-4|e,n,s")), T_INFO, T_PIC, T_HDR)
px, py = found_at()
check(not (px == 5 and py == -3), "fit: a gap the map links from its west room rules it out")
probe((mz_store():gsub("\n0|5|%-4|[^\n]*", "\n0|5|-4|s,w")), T_INFO, T_PIC, T_HDR)
px, py = found_at()
check(not (px == 5 and py == -3), "fit: ...and from its east room")
-- a diagonal gap too: (5,-2) and (6,-3) are drawn with nothing between them
probe((mz_store():gsub("\n0|5|%-2|[^\n]*", "\n0|5|-2|n,ne,se,w")), T_INFO, T_PIC, T_HDR)
px, py = found_at()
check(not (px == 5 and py == -3), "fit: ...and a diagonal one")
-- two places fitting with all the evidence in the world are still two places
probe(TWO_LAYERS, T_INFO, T_PIC, T_HDR)        -- no layer anchored: both copies fit
check(said("fits 2 places"), "two places that both fit are not one")

-- 60w. Off the map. (4,-3) unmapped: walking north into it from (9,9), the only candidate
--      leaves the map - where nothing can be checked, so nothing is certain, however much
--      the picture there agrees.
do
  local m = mz_store():gsub("\n0|4|%-3|[^\n]*", "")
  mz_start(m, START_R, START_C)
  fire_trigger("Suddenly you find yourself", nil)
  mz_arrive(9, 9, 2)
  clear()
  cmd("n"); mz_arrive(7, 9, 2)
  check(not found_at(), "a trail that walks off the map is not certain there")
  check(said("it may have walked off the saved map"), "...and says why")
end

-- 60x. A wimpy while lost is a step of the trail: (9,9) is short, dragged south to (11,9),
--      which alone is shorter still - together they are enough.
mz_start(mz_store(), START_R, START_C)
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(9, 9, 2)
fire_trigger("Your legs run away with you", "south")
mz_arrive(11, 9, 2)
px, py = found_at()
check(px == 4 and py == -5, "a wimpy while lost carries the trail along (" .. tostring(px) .. " " .. tostring(py) .. ")")
check(said("after walking 1 room(s)"), "...as a room walked")

-- 60y. The client shows a plugin its own sends (onCommand). The search walk's step coming
--      back that way must change nothing: the same landing walks and finds the same.
local function echo_run(echo)
  math.randomseed(77)
  mz_start(mz_store(), START_R, START_C, true)
  if echo then
    local raw = scrye.send
    scrye.send = function(t) raw(t) ; cmd(t) end
  end
  sent = {}; clear()
  fire_trigger("Suddenly you find yourself", nil)
  local r, c = 11, 1
  mz_arrive(r, c, 1)
  for _ = 1, 40 do
    if found_at() or said("could not place them") then break end
    local d = sent[#sent]
    if not (d and MZ_STEP[d]) then break end
    r, c = r + MZ_STEP[d][1], c + MZ_STEP[d][2]
    sent = {}
    mz_arrive(r, c, 1)
  end
  for _, l in ipairs(printed) do if l:find("this room is at", 1, true) then return l end end
  return "not found"
end
local plain, echoed = echo_run(false), echo_run(true)
check(plain:find("after walking", 1, true) ~= nil, "(the corner landing is found by walking)")
check(echoed == plain, "the walk's own step echoed back changes nothing (" .. echoed .. ")")

-- 60z. After the search: nothing of it lingers.
-- a teleport while lost, its Room.Info suppressed, does not outlive the find
mz_start(mz_store(), START_R, START_C, true)
fire_trigger("Suddenly you find yourself", nil)
mz_arrive(9, 9, 2)                            -- short: the walk steps
fire_trigger("Suddenly you find yourself", nil)
roommap(T_PIC); header(T_HDR)                 -- thrown to (7,11): found
check(found_at() ~= nil, "(found after a second teleport)")
clear(); sent = {}
prompt()
local d3 = sent[1]
check(d3 == "e" or d3 == "w", "(steps on)")
roommap(mz_window(T_R, T_C + (d3 == "e" and 2 or -2), 2))
check(not said("teleported"), "the next step is not taken for a teleport")
-- a fight flag from before the teleport does not hold the bot after the find
mz_start(mz_store(), START_R, START_C)
contents('{ "full": 1, "items": [ { "name": "a mutant", "type": "monster" } ] }')
prompt()                                      -- 'kill mutant': fighting
check(sent_has("kill mutant"), "(fighting)")
csg("auto on")
fire_trigger("Suddenly you find yourself", nil)
room(T_INFO); roommap(T_PIC); header(T_HDR)
check(found_at() ~= nil, "(found)")
sent = {}; prompt()
check(sent[1] == "e" or sent[1] == "w", "a fight from before the teleport does not hold the found bot (" .. tostring(sent[1]) .. ")")
-- a teleport while PAUSED is forgotten on unpausing, like everything a pause ignores
mz_start(mz_store(), START_R, START_C, true)
csg("pause")
fire_trigger("Suddenly you find yourself", nil)
roommap(T_PIC); header(T_HDR)
clear(); sent = {}
csg("pause")
for _ = 1, 4 do tick() end ; fire_timers()   -- the step in flight when paused times out first
local d4 = sent[#sent]
check(d4 ~= nil, "(walks on after the pause)")
if d4 and MZ_STEP[d4] then roommap(mz_window(START_R + MZ_STEP[d4][1], START_C + MZ_STEP[d4][2], 2)) end
check(not said("teleported"), "a teleport seen while paused does not ambush the first step after it")

-- 60zz. A plan in hand when the position is corrected keeps its target. The bot is on its
--       way to an unexplored exit when 'cs locate' moves it: the exit is still to explore.
do
  local m = mz_store():gsub("\n0|8|%-4|[^\n]*", "")          -- (8,-4) unexplored...
  fresh_client(); store.map = m; store.pos = "4|-2|0"
  store.frontier = "0|7|-4|e"                                -- ...through (7,-4)'s east exit
  dofile(PLUGIN)
  csg("enable")
  mz_arrive(START_R, START_C, 2)
  csg("auto on")
  local d = sent[#sent]
  check(d and MZ_STEP[d], "(on its way: " .. tostring(d) .. ")")
  local r, c = START_R + MZ_STEP[d][1], START_C + MZ_STEP[d][2]
  room(mz_info(r, c)); roommap(mz_window(r, c, 2)); header(table.concat(mz_exits(r, c), ","))
  csg("set 0 0 0")                                           -- the position goes wrong
  clear(); csg("locate")
  check(found_at() ~= nil, "(located)")
  clear(); csg("step")
  check(said("-> [8 -4 0]"), "the target the plan was walking to is still the target")
end

-- 60zz2. Odds and ends of the fit and the trail.
-- an exit the map has for this room, toward a room the picture does not draw: still not
-- this room (the map's own record is what the room must match, both ways)
probe(mz_store(), T_INFO, themap('"      O-@-O      "'), T_HDR)
check(said("fits 9 places"), "(a small picture of (7,11) fits nine places)")
probe((mz_store():gsub("\n0|5|%-3|[^\n]*", "\n0|5|-3|e,n,w")), T_INFO, themap('"      O-@-O      "'), T_HDR)
check(said("fits 8 places"), "...and eight once the map's record of it has an exit it lacks")
-- a room passed through unjudged (no picture ever came) hands nothing on: the fits of
-- the room before it are a step behind, and the next room is judged afresh
MAZE = { "O-O-O-O-O-O-O-O-O-O-O-O-O-O-O-O-O" }
local function corridor_counts(judge_middle)
  mz_start(mz_store(), 1, 1)
  fire_trigger("Suddenly you find yourself", nil)
  mz_arrive(1, 15, 1)
  cmd("e")
  if judge_middle then mz_arrive(1, 17, 1) else header(table.concat(mz_exits(1, 17), ",")); prompt() end
  clear()
  cmd("e"); mz_arrive(1, 19, 1)
  return printed[1] or ""
end
local fresh = corridor_counts(false)
fresh_client(); store.map = mz_store(); store.pos = "50|50|0"; dofile(PLUGIN); csg("enable"); csg("pause")
room(mz_info(1, 19)); roommap(mz_window(1, 19, 1)); header(table.concat(mz_exits(1, 19), ","))
clear(); csg("locate")
local alone = (printed[1] or ""):match("fits (%d+) places")
check(fresh:match("fits (%d+) places") == alone, "after an unjudged room the next is judged on its own ("
      .. tostring(fresh:match("fits (%d+) places")) .. " vs " .. tostring(alone) .. ")")
MAZE = SAVED_MAZE
-- a mob seen in the room the sea threw us out of is not attacked in the room we land in
mz_start(mz_store(), START_R, START_C)
csg("auto on")
contents('{ "full": 1, "items": [ { "name": "a mutant", "type": "monster" } ] }')   -- seen, not yet fought
fire_trigger("Suddenly you find yourself", nil)
room(T_INFO); roommap(T_PIC); header(T_HDR)
check(found_at() ~= nil, "(found)")
sent = {}; prompt()
check(not sent_has("kill mutant"), "a mob from before the teleport is not attacked after it")
-- the exit-set odds are the LAYER's: another layer full of corridors changes nothing here
do
  local corr = {}
  for i = 0, 29 do corr[#corr + 1] = string.format("0|%d|10|e,w", i) end
  mz_start(mz_store(nil, -1) .. "\n" .. table.concat(corr, "\n"), START_R, START_C, false, 2, -1)
  csg("pause")
  room(mz_info(9, 9)); roommap(mz_window(9, 9, 2)); header(table.concat(mz_exits(9, 9), ","))
  clear(); csg("locate")
  check(said("[4 -4 -1], not yet surely (24.9 of 25.8 bits)"), "the odds come from the layer searched")
  mz_start(mz_store(nil, -1) .. "\n" .. table.concat(corr, "\n"), START_R, START_C, false, 2, -1)
  fire_trigger("Suddenly you find yourself", nil)
  clear()
  mz_arrive(9, 9, 2)
  check(said("[4 -4 -1], not yet surely (24.9 of 25.8 bits)"), "...in the search as in 'cs locate'")
end

-- 2.5.0. The 3D view: on a Scrye with plugin API 1.24 the sea map is a scene3d widget with
--     the 2D grid as its stand-in, and 'cs view 3d' (or the 3D button) sends every layer at
--     once - rooms, links (a way down drawn '|'), the frontier targets, start and you. The
--     2D grid is drawn regardless: the phone shows that one. Older Scrye: nothing changes.
do
  local SP = "plugin.3s-chaossea.scene3d"
  local function scene() return decode(state[SP] or "") end
  local function find_type(node, want)
    if type(node) ~= "table" then return nil end
    if node.type == want then return node end
    for _, v in pairs(node) do
      local hit = find_type(v, want)
      if hit then return hit end
    end
    return nil
  end

  fresh_client() ; dofile(PLUGIN)
  check(find_type(panel, "scene3d") == nil and find_type(panel, "colorgrid") ~= nil,
        "3D: with no scrye.api the sea map is the 2D grid it always was")
  check(find_button(panel, "3D") == nil, "...and there is no 3D button")
  clear() ; csg("view 3d")
  check(said("needs a Scrye with plugin API 1.24"), "...'cs view 3d' says what it needs")
  fresh_client() ; scrye.api = "1.23" ; dofile(PLUGIN)
  check(find_type(panel, "scene3d") == nil, "...and 1.23 is not enough")

  fresh_client() ; scrye.api = "1.24" ; dofile(PLUGIN)
  local w = find_type(panel, "scene3d")
  check(w and w.bind == SP and w.height == 280, "3D: on API 1.24 the sea map is a scene3d widget")
  check(w and w.widgets and w.widgets[1].type == "colorgrid" and w.widgets[1].bind == "plugin.3s-chaossea.map",
        "...with the 2D sea map as its stand-in")
  check(w.palette["@"] == "#6BEF75" and w.palette["|"] == "#3BAECE", "...the map's colours, a way down in the down-exit cyan")
  check(find_button(panel, "3D") and find_button(panel, "Reset view"), "...and a 3D button with a Reset view beside it")
  check(find_button(panel, "New Sea") and find_button(panel, "Loop"), "...the buttons before it all still there")

  csg("enable")
  room(L3_NS) ; arm()
  check((state[SP] or "") == "", "3D: 2D by default - no scene sent")
  find_button(panel, "3D").action()
  check(store["view3d"] == "1" and find_button(panel, "3D ON") ~= nil, "the 3D button turns it on, kept, and lights up")
  local sc = scene()
  check(sc and sc.nodes and #sc.nodes >= 1, "a scene is sent")
  local you, fronts = nil, 0
  for i, n in ipairs(sc.nodes) do
    if n[5] == "@" then you = i - 1 end
    if n[5] == "f" then fronts = fronts + 1 ; check(n[6] == 0.6, "a frontier target is a small point") end
  end
  check(you == sc.focus and sc.level == 0, "you are the focus, your layer the bright one")
  check(fronts == 2, "both unexplored ways (n and s) are frontier targets (" .. fronts .. ")")
  check(#sc.links == 2, "...each linked to the room it is reached from")

  csg("step") ; room(L3_NS) ; arm()
  sc = scene()
  local at = {}
  for i, n in ipairs(sc.nodes) do at[n[5]] = at[n[5]] or { i = i - 1, n = n } end
  check(at["S"] and at["@"] and at["@"].n[3] == -1, "after a step: the start room and you, one south")
  check(sc.focus == at["@"].i, "...the focus follows you")
  local linked = false
  for _, l in ipairs(sc.links) do
    if (l[1] == at["S"].i and l[2] == at["@"].i) or (l[2] == at["S"].i and l[1] == at["@"].i) then linked = (l[3] == "-") end
  end
  check(linked, "...linked flat to where you came from")
  check(state["plugin.3s-chaossea.map"] ~= nil and state["plugin.3s-chaossea.map"]:find("@", 1, true), "the 2D map is still drawn")

  -- unchanged: not re-sent
  local v0 = sc.view
  state[SP] = "UNTOUCHED"
  csg("view 3d")
  check(state[SP] == "UNTOUCHED", "an unchanged sea is not sent again")
  -- Reset view: the camera token moves, so the scene goes out again
  find_button(panel, "Reset view").action()
  check(scene() and scene().view == v0 + 1, "Reset view resets the camera")

  -- down a layer: a vertical link
  fresh_client() ; scrye.api = "1.24" ; store["view3d"] = "1" ; dofile(PLUGIN)
  csg("enable")
  room('{ "exits": { "d": 0 }, "area": "Unknown", "name": "Layer three of the Sea of Chaos", "num": 60494 }') ; arm()
  csg("step")
  check(sent_has("d"), "(the only way is down)")
  room('{ "exits": { "u": 60494 }, "area": "Unknown", "name": "Layer four of the Sea of Chaos", "num": 60494 }') ; arm()
  sc = scene()
  check(sc and sc.level == -1, "a reload kept 3D on; down a layer, the bright layer follows you")
  local vert = false
  for _, l in ipairs(sc and sc.links or {}) do if l[3] == "|" then vert = true end end
  check(vert, "...and the way down is a vertical '|' link")
  local zs = {}
  for _, n in ipairs(sc and sc.nodes or {}) do zs[n[4]] = true end
  check(zs[0] and zs[-1], "...with both layers in the scene")

  -- placed rooms (the 46a trick): a down exit is 'v'; two frontier exits aiming at the
  -- same square make one target; a frontier exit aiming at a square that HAS a room makes
  -- none there (the server says unwalked, but the point would sit on the room)
  fresh_client() ; scrye.api = "1.24" ; store["view3d"] = "1" ; dofile(PLUGIN)
  csg("enable")
  local function L3(ex) return '{ "exits": ' .. ex .. ', "area": "Unknown", "name": "Layer three of the Sea of Chaos", "num": 60494 }' end
  csg("set 5 5 0") ; room(L3('{ "d": 0 }')) ; arm()
  csg("set 0 1 0") ; room(L3('{ "s": 60494 }')) ; arm()
  csg("set 0 0 0") ; room(L3('{ "n": 0, "se": 0 }')) ; arm()
  csg("set 0 -1 0") ; room(L3('{ "e": 0 }')) ; arm()
  csg("set 9 9 0") ; room(L3('{ "w": 60494 }')) ; arm()
  local here_at = {}
  for _, n in ipairs(scene().nodes) do
    local k = n[2] .. "," .. n[3]
    here_at[k] = here_at[k] or {}
    table.insert(here_at[k], n[5])
  end
  check(here_at["5,5"] and here_at["5,5"][1] == "v", "a room with a way down is 'v' in 3D too")
  check(here_at["1,-1"] and #here_at["1,-1"] == 1 and here_at["1,-1"][1] == "f",
        "two frontier exits aiming at one square make one target")
  check(#here_at["0,1"] == 1 and here_at["0,1"][1] == "#", "a frontier exit aiming at a known room puts no target on it")
  local between = 0
  local idx = {}
  for i, n in ipairs(scene().nodes) do idx[n[2] .. "," .. n[3] .. "," .. n[5]] = i - 1 end
  for _, l in ipairs(scene().links) do
    local a, b = idx["0,0,S"], idx["0,1,#"]
    if (l[1] == a and l[2] == b) or (l[1] == b and l[2] == a) then between = between + 1 end
  end
  check(between == 1, "a two-way link is drawn once (" .. between .. ")")
  find_button(panel, "3D ON").action()
  check(find_button(panel, "3D") ~= nil and (state[SP] or "") == "", "the 3D button, pressed while on, turns it off")
  find_button(panel, "3D").action()

  -- off again
  clear() ; csg("view 2d")
  check((state[SP] or "") == "" and store["view3d"] == "0" and find_button(panel, "3D") ~= nil,
        "'cs view 2d' clears the scene and the button goes back")
  check(said("view: 2D"), "...and says so")
  clear() ; csg("view upside")
  check(said("cs view 2d|3d"), "anything else gets the usage")
  clear() ; csg("")
  check(said("cs view 2d|3d"), "the help lists 'cs view'")
end

-- 11. THE PROPERTY. Across the whole suite, the bot never asks the MUD to re-describe
--     a room: '!glance' produces no Room.Info, so sending it would be asking for silence.
local glanced = false
for _, c in ipairs(every_sent) do if c:find("glance", 1, true) then glanced = true end end
check(not glanced, "!glance is never sent, all suite long")

print(string.format("\n%d passed, %d failed", pass, fail))
os.exit(fail == 0 and 0 or 1)
