-- Harness for 3s-stepper's GMCP wiring.
--
-- SCOPE: this covers the room/contents feed and the marker fallback -- the part that was
-- just changed -- and NOT the other 800-odd lines (the recorder, the return walk, stacking,
-- hardmode). Those were untested before and still are; nothing here should be read as
-- cover for them.
--
-- The area is a small made-up one rather than anything out of areas.json, so a route change
-- in the real data cannot quietly break these.

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
-- This file sits in tests/plugins/ (CI runs it), not beside the plugin: everything in the plugin folder is
-- copied into the build output, and a test file has no business shipping with the client.
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-stepper/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

-- ---- a JSON decoder, for the Room.Contents payloads ----
local decode
do
  local src, pos
  local function ws() while true do local c = src:sub(pos, pos)
      if c == " " or c == "\n" or c == "\t" or c == "\r" then pos = pos + 1 else return end end end
  local function value()
    ws()
    local c = src:sub(pos, pos)
    if c == "{" then
      local t = {} ; pos = pos + 1 ; ws()
      if src:sub(pos, pos) == "}" then pos = pos + 1 ; return t end
      while true do
        ws()
        local k = value() ; ws() ; pos = pos + 1
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
      lit = src:match("^%a+", pos) ; pos = pos + #(lit or "")
      if lit == "true" then return true elseif lit == "false" then return false end
      return nil
    end
  end
  decode = function(x)
    if type(x) ~= "string" then return nil end
    src, pos = x, 1
    local ok, v = pcall(value)
    return ok and v or nil
  end
end

-- ---- the client, as far as the plugin can tell ----
local printed, sent, store, state, vars
local gmcp_hooks, triggers, aliases, prompt_hooks, idle_hooks, disconnect_hooks, timers
local next_timer

-- One small invented area: three steps, one mob that maps to the kill word "troll".
local AREAS = { testarea = { p = "n e s", mobs = { ["A cave troll"] = "troll" } } }

local function fresh_client()
  printed, sent, store, state, vars = {}, {}, {}, {}, {}
  gmcp_hooks, triggers, aliases = {}, {}, {}
  prompt_hooks, idle_hooks, disconnect_hooks, timers = {}, {}, {}, {}
  next_timer = 0
  scrye = {
    id = "3s-stepper",
    print = function(x) printed[#printed + 1] = tostring(x) end,
    send  = function(x) sent[#sent + 1] = tostring(x) end,
    sound = function() end,
    notify = function() end,
    json = { decode = decode, encode = function() return "{}" end },
    data = { areas = AREAS },
    store = {
      get = function(k) return store[k] end,
      set = function(k, v) store[k] = v end,
      delete = function(k) store[k] = nil end,
      setMany = function(t) for k, v in pairs(t) do store[k] = v end end,
    },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    onPrompt = function(fn) prompt_hooks[#prompt_hooks + 1] = fn end,
    onIdle = function(fn) idle_hooks[#idle_hooks + 1] = fn end,
    onDisconnect = function(fn) disconnect_hooks[#disconnect_hooks + 1] = fn end,
    addTrigger = function(t) triggers[#triggers + 1] = t end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addPanel = function() end,
    setState = function(k, v) state[k] = v end,
    setVariable = function(k, v) vars[k] = v end,
    after = function(_, fn) next_timer = next_timer + 1 ; timers[next_timer] = fn ; return next_timer end,
    cancel = function(id) if id then timers[id] = nil end end,
  }
end

-- ---- driving it ----
local function roominfo(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Info" then h.fn(json or "{}") end end
end
local function contents(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Room.Contents" then h.fn(json) end end
end
local function prompt() for _, f in ipairs(prompt_hooks) do f() end end
local function fire(needle, ...)
  for _, t in ipairs(triggers) do
    if t.pattern:find(needle, 1, true) then t.run(...) return true end
  end
  return false
end
local function alias(needle, ...)
  for _, a in ipairs(aliases) do
    if a.pattern:find(needle, 1, true) then a.run(...) return true end
  end
  return false
end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end
local function sent_has(x) for _, c in ipairs(sent) do if c == x then return true end end return false end
local function clear() printed = {} end

local TROLL  = '{ "full": 1, "items": [ { "type": "monster", "count": 1, "name": "A cave troll {angry}" } ] }'
local PLAYER = '{ "full": 1, "items": [ { "type": "player", "count": 1, "name": "Bob the Bold" }, { "type": "monster", "count": 1, "name": "A cave troll {angry}" } ] }'
local EMPTY  = '{ "full": 1, "items": [ ] }'

local function start_bot()
  fresh_client() ; dofile(PLUGIN)
  alias("walker ", "testarea")
  sent = {}
end

-- ============================================================
fresh_client() ; dofile(PLUGIN)

-- 1. It subscribes to the two packages that carry what the markers carried.
local pkgs = {}
for _, h in ipairs(gmcp_hooks) do pkgs[h.pkg] = true end
check(pkgs["Room.Info"], "subscribed to Room.Info - the room you are in")
check(pkgs["Room.Contents"], "subscribed to Room.Contents - what is in it")

-- 2. The markers are still registered. They are the fallback, not deleted.
check(fire("=S=") ~= false, "the =S= trigger is still registered")
check(fire("MONSTAR", "A cave troll") ~= false, "the =M= trigger is still registered")

-- 3. Room.Info arms the next step, exactly as '=S=' did.
start_bot()
roominfo()
prompt()
check(sent_has("n"), "Room.Info arms the step, and the prompt walks it")

-- 4. A monster in Room.Contents arms the kill, matched against the area's mob table.
start_bot()
roominfo() ; contents(TROLL)
prompt()
check(sent_has("kill troll"), "a monster in Room.Contents is killed by its mapped word")
check(not sent_has("n"), "...instead of walking on")

-- 5. A player in the room skips the mob, exactly as '=P=' did.
start_bot()
roominfo() ; contents(PLAYER)
clear()
prompt()
check(said("Found player"), "a player in Room.Contents is noticed")
check(sent_has("n"), "...and the bot walks on rather than killing")

-- 6. THE ONE THAT BITES. on_room() clears a pending kill, so a '=S=' arriving AFTER
--    Room.Contents has armed one would drop the mob the bot was about to attack. The
--    marker has to stand down for a room GMCP already spoke for.
start_bot()
roominfo() ; contents(TROLL)
fire("=S=")                      -- the marker line, arriving after the GMCP burst
prompt()
check(sent_has("kill troll"), "a =S= line after Room.Contents does not drop the pending kill")

-- 7. And a '=M=' after Room.Contents is stale text, not a second monster.
start_bot()
roominfo() ; contents(TROLL)
fire("MONSTAR", "A cave troll {angry}")
prompt()
check(sent_has("kill troll"), "the kill still goes out once")
local kills = 0
for _, c in ipairs(sent) do if c == "kill troll" then kills = kills + 1 end end
check(kills == 1, "...once, not twice")

-- 8. With no GMCP at all, the markers still drive it. This is what lets the display
--    settings be switched off one at a time, and what keeps a GMCP-less character working.
start_bot()
fire("=S=")
prompt()
check(sent_has("n"), "with no GMCP, =S= still arms the step")
start_bot()
fire("=S=")
fire("MONSTAR", "A cave troll {angry}")
prompt()
check(sent_has("kill troll"), "...and =M= still arms the kill")

-- 9. The stand-down is per ROOM. If GMCP went quiet mid-session the markers must be heard
--    again, not suppressed for ever by one old Room.Info.
start_bot()
roominfo()
prompt()                         -- walks; the prompt ends the room
clear() ; sent = {}
fire("=S=")                      -- next room, described only by the marker
prompt()
check(sent_has("e"), "a later =S= is heard again once the prompt has ended the room")

-- 10. An empty Room.Contents is still Room.Contents: it has spoken for the room.
start_bot()
roominfo() ; contents(EMPTY)
fire("MONSTAR", "A cave troll {angry}")
prompt()
check(not sent_has("kill troll"), "a stale =M= after an empty Room.Contents is ignored")
check(sent_has("n"), "...and the bot walks on")

-- 11. The collapse recovery no longer asks for a room GMCP has already promised. With the
--     markers off, '!glance' produces nothing this plugin can read.
start_bot()
roominfo()
sent = {}
fire("collapses")
check(not sent_has("!glance"), "no glance is sent when GMCP is feeding us")
start_bot()
fire("collapses")
check(sent_has("!glance"), "...but it still is when nothing has come over GMCP")

-- 12. A malformed payload is survived.
start_bot()
roominfo()
contents("}}not json{{")
prompt()
check(sent_has("n"), "a Room.Contents that is not an object does not stop the bot")


-- 8. THE SILENT START (Joakim live, 25 Aug: '- megacity', the =S= text, a prompt, and no
--    move at all). On a GMCP character standing still, the start's glance re-describes a
--    room whose payloads have not changed - so GMCP sends NOTHING - and the marker line
--    prints indented, which the old anchored pattern never matched. Nothing armed; the
--    bot sat. Starting or resuming now arms directly: the person saying 'walk' is the
--    authority that we are in a room.
start_bot()                      -- nothing arrives at all: no Room.Info, no marker match
prompt()
check(sent_has("n"), "a start with a completely silent feed still takes the first step")

-- 8b. ...and a mob the glance DOES reveal still wins over the step: direct arming did not
--     jump the queue - the prompt still checks the kill first.
start_bot()
contents(TROLL)                  -- the glance's answer names a mob
prompt()
check(sent_has("kill troll"), "a mob revealed by the glance is fought before the step")
check(not sent_has("n"), "...and the step waits")

-- 8c. THE PATTERNS MATCH THE REAL LINES. The harness drives triggers by run(), which is
--     blind to the pattern - this checks the pattern itself against a line as 3Scapes
--     actually prints it, INDENTED. (The client regex \s* is Lua %s* for these shapes;
--     the translation below is by hand and only valid for these three patterns.)
do
  local live = "  =S=Floor 10   Elevator Lobby (e,w,n,s,sw,se)  =S="
  local s_pat
  for _, t in ipairs(triggers) do
    if t.pattern:find("=S=", 1, true) then s_pat = t.pattern break end
  end
  check(s_pat == [[^\s*=S=(.*)=S=]], "the =S= pattern tolerates the indent 3Scapes prints")
  check(live:match("^%s*=S=(.*)=S=") ~= nil, "...and matches the live line verbatim")
  local m_ok, p_ok = false, false
  for _, t in ipairs(triggers) do
    if t.pattern:find("MONSTAR", 1, true) then m_ok = t.pattern:sub(1, 4) == [[^\s*]] end
    if t.pattern:find("PLAYAR", 1, true) then p_ok = t.pattern:sub(1, 4) == [[^\s*]] end
  end
  check(m_ok and p_ok, "the =M= and =P= patterns tolerate it too")
end

-- 9. THE IDLE GUARD STOPS A BOT IN MID-FIGHT. It used to skip a bot paused on a mob, and
--    the fight is exactly where the bot keeps itself going: the killing blow re-attacks,
--    and "There is no X here." walks on.
start_bot()
roominfo() ; contents(TROLL)
prompt()
check(sent_has("kill troll"), "(setup) the bot is fighting")
clear() ; sent = {} ; timers = {}
for _, f in ipairs(idle_hooks) do f() end
check(said("idle guard fired"), "the idle guard stops a bot that is mid-fight")
fire("dealt the killing blow", "A cave troll")
fire("There is no", "troll")
for _, fn in pairs(timers) do fn() end
check(#sent == 0, "...and after the fight nothing is re-attacked or walked")
prompt()
check(#sent == 0, "...nor on the next prompt")

-- 9b. .stop by hand in mid-fight is the same stop.
start_bot()
roominfo() ; contents(TROLL)
prompt()
sent = {} ; timers = {}
alias("stop$")
fire("dealt the killing blow", "A cave troll")
fire("There is no", "troll")
for _, fn in pairs(timers) do fn() end
prompt()
check(#sent == 0, ".stop mid-fight holds after the fight too")
alias("resume$")
check(sent_has("!glance"), "...and .resume picks it up again")

-- 9c. An already stopped bot is not stopped twice.
clear()
alias("stop$")
clear()
for _, f in ipairs(idle_hooks) do f() end
check(not said("idle guard fired"), "a stopped bot is left alone by the idle guard")

print(string.format("\n%d passed, %d failed", pass, fail))
os.exit(fail == 0 and 0 or 1)
