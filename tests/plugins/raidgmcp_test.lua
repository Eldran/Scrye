-- Harness for 3s-raid-gmcp. Feed payloads are verbatim from the 27 Aug 2026 capture
-- (_lab/captures/gmcp-fields-Goran-20260827.md) wherever a complete burst exists; the
-- ships/rtargets burst is assembled from verbatim PAGE payloads with only the page
-- numbering adjusted (the capture's first-12 samples don't include every page of any
-- single Fleet burst, and the assembler only publishes complete bursts - which is the
-- point of it).
--
-- What this file protects: the page assembler's burst semantics (concat, full-replace,
-- keep-last, stale-burst abandonment), the heat<->town index pairing, and the ported
-- dispatch rules (throttle, keep/reserve/convoy, auto-target lock + hold).

--
-- Equivalent mutant, documented rather than faked: gasm stripping only page/pages
-- (letting "full"/"guild" sit in the snapshot as data keys) survives - no consumer
-- in this plugin reads those names, so no published state changes. It matters the
-- day a consumer iterates snapshot keys (the viking-status Feeds tab will); the
-- shared-snippet contract in the plan doc §3 is the guard until then.

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-raid-gmcp/main.lua"

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
local printed, sent, notified
local gmcp_hooks, aliases, repeats, store, state, panel, idle_hooks

local function fresh_client()
  printed, sent, notified = {}, {}, {}
  gmcp_hooks, aliases, repeats = {}, {}, {}
  idle_hooks = {}
  store, state, panel = {}, {}, nil
  scrye = {
    id = "3s-raid-gmcp",
    print = function(s) printed[#printed + 1] = tostring(s) end,
    send = function(s) sent[#sent + 1] = tostring(s) end,
    notify = function(s) notified[#notified + 1] = tostring(s) end,
    json = { decode = decode, encode = encode },
    store = {
      get = function(k) return store[k] end,
      set = function(k, v) store[k] = v end,
      delete = function(k) store[k] = nil end,
    },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    onConnect = function() end,
    onDisconnect = function() end,
    onIdle = function(fn) idle_hooks[#idle_hooks + 1] = fn end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addTrigger = function() end,
    addPanel = function(p) panel = p end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    every = function(_, fn) repeats[#repeats + 1] = fn; return #repeats end,
    after = function(_, fn) return 0 end,
    cancel = function() end,
  }
end

local function gmcp(pkg, json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == pkg then h.fn(json) end end
end
local function tick(n) for _ = 1, n or 1 do for _, fn in ipairs(repeats) do fn() end end end
local function araid(rest)
  for _, a in ipairs(aliases) do
    if rest == nil and a.pattern == "^araid$" then a.run() return end
    if rest ~= nil and a.pattern == "^araid (.+)$" then a.run(rest) return end
  end
end
local function sent_has(x)
  for _, c in ipairs(sent) do if c == x then return true end end
  return false
end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end

-- ---- feed payloads ----
-- Ship pairs verbatim from the capture's Fleet pages (a pages:7 burst, pages 3-4);
-- here renumbered into a complete 4-page non-full burst with the (verbatim) rtargets
-- lists as pages 3-4.
local FLEET_P1 = '{ "pages": 4, "ships": [ { "convoy_bonus": 0, "durability": 100, "target": "Nidaros", "state": "raiding", "tier": 5, "convoy_size": 0, "secs": 13264, "saga_title": "the Undying", "convoy": 0, "held": 0, "saga_raids": 159, "crew": 60, "id": 16, "name": "Kronan" }, { "convoy_bonus": 0, "durability": 100, "target": "", "state": "docked", "tier": 5, "convoy_size": 0, "secs": 0, "saga_title": "the Iron-Prowed", "convoy": 0, "held": 0, "saga_raids": 61, "crew": 60, "id": 5, "name": "Vasa" } ], "page": 1, "guild": "viking" }'
local FLEET_P2 = '{ "pages": 4, "ships": [ { "convoy_bonus": 0, "durability": 100, "target": "Borgarfjord", "state": "raiding", "tier": 5, "convoy_size": 0, "secs": 13707, "saga_title": "the Legendary", "convoy": 0, "held": 0, "saga_raids": 186, "crew": 60, "id": 14, "name": "Svarten" }, { "convoy_bonus": 0, "durability": 100, "target": "", "state": "docked", "tier": 5, "convoy_size": 0, "secs": 0, "saga_title": "the Deathless", "convoy": 0, "held": 0, "saga_raids": 291, "crew": 60, "id": 1, "name": "Regal" } ], "page": 2, "guild": "viking" }'
local FLEET_P3 = '{ "rtargets_lineage": [ "Lodbrok\'s Hold:bread:fine_furs", "Eiriksby:salted_fish:cloth", "Imaird:mead:cheese", "Holmgard:bread:smoked_meat", "Hafrfjord:salted_fish:mead", "Uppsala:fine_furs:cheese", "Borgarfjord:cloth:smoked_meat", "Vestergotland:bread:cloth", "Sverkersby:fine_furs:smoked_meat", "Ericsgard:mead:bread", "Birka:salted_fish:cheese", "Lejre:cloth:mead", "Nidaros:smoked_meat:cheese" ], "pages": 4, "page": 3, "guild": "viking" }'
local FLEET_P4 = '{ "rtargets_historical": [ "Paris:grain:wool", "Lindisfarne:timber:pork", "Hamwic:furs:mutton" ], "pages": 4, "page": 4, "guild": "viking" }'

-- Guild.City: heat arrives on UNPAGED messages (verbatim main-example shape), the
-- dock tier on a paged buildings burst (buildings page verbatim, dock on page 2).
local CITY_HEAT = '{ "blot": { "total": 9, "filled": 9, "state": "rest", "reset_in": 5040 }, "heat": [ 0, 0, 59, 0, 0, 0, 27, 0, 19, 0, 62, 29, 26 ], "patrol": { "remaining": 10382, "count": 15 }, "nexttick": 10197, "guild": "viking", "dcycle": { "secs": 276188, "name": "Calm Season" } }'
local CITY_P1 = '{ "blot": { "total": 9, "filled": 9, "state": "rest", "reset_in": 9380 }, "full": 1, "pages": 2, "bdmg": [ ], "page": 1, "guild": "viking" }'
local CITY_P2 = '{ "buildings": [ { "tier": 2, "id": "shadow_house" }, { "tier": 5, "id": "dock" }, { "tier": 4, "id": "tannery" } ], "full": 1, "pages": 2, "guild": "viking", "page": 2 }'

local function feed_all()
  gmcp("Guild.Fleet", FLEET_P1); gmcp("Guild.Fleet", FLEET_P2)
  gmcp("Guild.Fleet", FLEET_P3); gmcp("Guild.Fleet", FLEET_P4)
  gmcp("Guild.City", CITY_P1); gmcp("Guild.City", CITY_P2)
  gmcp("Guild.City", CITY_HEAT)
end

-- =====================================================================
-- before the feed: the table lists the baked-in towns (HOME_DEFAULT / FOREIGN_DEFAULT,
-- plugin 2.3.1 - "the scan and the default are the fallbacks"). This check used to expect
-- a "waiting on Guild.Fleet" line; it went stale unnoticed while the harness lived outside
-- CI (found and updated 21 Oct 2026, when the harnesses moved to tests/plugins).
fresh_client(); dofile(PLUGIN)
do
  local heat = tostring(state["plugin.3s-raid-gmcp.heat"])
  check(heat:find("Lodbrok's Hold", 1, true) and heat:find("Foreign", 1, true)
        and heat:find("Paris", 1, true),
    "before any burst the heat table lists the default home and foreign towns")
end
check(state["plugin.3s-raid-gmcp.docked"] == "0 / 2",
  "no feed: 0 docked over the default dock capacity")

-- assembler: ships concatenate across the burst's pages; docked/raiding split right
feed_all()
check(state["plugin.3s-raid-gmcp.docked"] == "2 / 10",
  "4 ships over 2 pages -> 2 docked; dock tier 5 -> capacity 10 (got " .. tostring(state["plugin.3s-raid-gmcp.docked"]) .. ")")

-- heat pairing: index i of City heat[] belongs to town i of rtargets_lineage
local heat = state["plugin.3s-raid-gmcp.heat"]
check(heat:find("Imaird", 1, true) and heat:match("Imaird%s*@{}%s*59"), "Imaird carries heat 59 (index 3)")
check(heat:match("Birka%s*@{}%s*62"), "Birka carries heat 62 (index 11)")
check(heat:match("Nidaros%s*@{}%s*26"), "Nidaros carries heat 26 (index 13)")
check(heat:match("Lodbrok's Hold%s*@{}%s*0"), "Lodbrok's Hold carries heat 0 (index 1)")
-- the calm pool: heat 0 towns marked calm, hot towns not
check(heat:find("calm", 1, true), "zero-heat towns are marked calm")

-- keep-last: an unpaged City refresh must not erase the buildings from the paged burst
gmcp("Guild.City", CITY_HEAT)
check(state["plugin.3s-raid-gmcp.docked"] == "2 / 10",
  "unpaged heat refresh keeps the dock tier from the earlier burst")

-- full-replace: a full City burst WITHOUT buildings drops them (dock back to default)
gmcp("Guild.City", '{ "full": 1, "pages": 1, "page": 1, "heat": [ 0, 0, 59, 0, 0, 0, 27, 0, 19, 0, 62, 29, 26 ], "guild": "viking" }')
check(state["plugin.3s-raid-gmcp.docked"] == "2 / 2",
  "a full burst replaces the snapshot: no buildings -> default capacity")

-- stale-burst abandonment: an unfinished burst's pages never leak into the next one
fresh_client(); dofile(PLUGIN)
gmcp("Guild.Fleet", '{ "pages": 8, "ships": [ { "state": "docked", "name": "Ghost", "id": 99 } ], "page": 1, "guild": "viking" }')
feed_all()
check(state["plugin.3s-raid-gmcp.docked"] == "2 / 10",
  "abandoned 8-page burst leaks nothing: still 2 docked, no Ghost")

-- =====================================================================
-- dispatch rules (feed as above: Vasa + Regal docked)

-- the 30 s throttle: arming does not dispatch before AR_INTERVAL has elapsed
araid("target Lejre"); araid("on")
sent = {}
tick(6)              -- 6 s of clock; every driver pass inside the throttle window
check(#sent == 0, "no dispatch inside the 30 s throttle")
tick(30)
check(sent_has("vlongship raid Vasa Lejre") and sent_has("vlongship raid Regal Lejre"),
  "after the throttle: ships 2 -> both docked ships raid Lejre")

-- keep: with keep 1 only one ship may leave
fresh_client(); dofile(PLUGIN); feed_all()
araid("target Lejre"); araid("keep 1"); araid("on"); sent = {}
tick(36)
check(#sent == 1 and sent[1]:match("^vlongship raid %a+ Lejre$"),
  "keep 1 -> exactly one of two docked ships dispatched")

-- reserve: the named voyage ship never raids
fresh_client(); dofile(PLUGIN); feed_all()
araid("target Lejre"); araid("reserve Vasa"); araid("on"); sent = {}
tick(36)
check(sent_has("vlongship raid Regal Lejre") and not sent_has("vlongship raid Vasa Lejre"),
  "reserve Vasa -> only Regal raids")

-- convoy: one command, count not names
fresh_client(); dofile(PLUGIN); feed_all()
araid("target Lejre"); araid("convoy on"); araid("on"); sent = {}
tick(36)
check(sent_has("vlongship convoy 2 Lejre") and #sent == 1, "convoy on -> one convoy command for 2 ships")

-- multi-word town: vlongship gets the short word, display keeps the real name
fresh_client(); dofile(PLUGIN); feed_all()
araid("target Lodbrok's Hold"); araid("on"); sent = {}
tick(36)
check(sent_has("vlongship raid Vasa lodbrok"), "Lodbrok's Hold raids as 'lodbrok'")

-- auto-target: picks from the calm pool (heat <= min+2) and locks for the hold
fresh_client(); dofile(PLUGIN); feed_all()
araid("auto on"); araid("hold 600"); araid("on"); sent = {}
tick(36)
check(#sent == 2, "auto-target dispatched both ships")
local town1 = sent[1] and sent[1]:match("^vlongship raid %a+ (.+)$")
local calm = { ["Lodbrok's Hold"] = true, lodbrok = true, Eiriksby = true, Holmgard = true,
               Hafrfjord = true, Uppsala = true, Vestergotland = true, Ericsgard = true }
check(town1 and calm[town1], "auto pick is a calm town (got " .. tostring(town1) .. ")")
-- lock: a second pass inside the hold keeps the same town
gmcp("Guild.Fleet", FLEET_P1); gmcp("Guild.Fleet", FLEET_P2)
gmcp("Guild.Fleet", FLEET_P3); gmcp("Guild.Fleet", FLEET_P4)
sent = {}
tick(36)
local town2 = sent[1] and sent[1]:match("^vlongship raid %a+ (.+)$")
check(town2 == town1, "inside the hold the locked town is kept (" .. tostring(town1) .. " vs " .. tostring(town2) .. ")")

-- fleet-return notify: docked count rising fires the phone only when enabled
fresh_client(); dofile(PLUGIN)
araid("notify fleet on")
gmcp("Guild.Fleet", FLEET_P3); gmcp("Guild.Fleet", FLEET_P4)  -- rtargets only: 0 ships
gmcp("Guild.City", CITY_HEAT)
notified = {}
feed_all()   -- now 2 docked: an increase over the 0 baseline
check(#notified >= 1 and notified[#notified]:find("back in dock", 1, true),
  "docked count rising fires the fleet-return notification")

-- pool membership is exact: only state=="docked" raids. The capture only ever showed
-- docked/raiding, but a ship in any third state (out on a voyage, say) must not be
-- dispatched - this ship is SYNTHETIC, marked so, to pin that judgement down.
fresh_client(); dofile(PLUGIN)
gmcp("Guild.Fleet", '{ "pages": 2, "ships": [ { "state": "docked", "name": "Vasa", "id": 5 }, { "state": "at sea", "name": "Wasa", "id": 2 } ], "page": 1, "guild": "viking" }')
gmcp("Guild.Fleet", FLEET_P3:gsub('"pages": 4, "page": 3', '"pages": 2, "page": 2'))
araid("target Lejre"); araid("on"); sent = {}
tick(36)
check(sent_has("vlongship raid Vasa Lejre") and not sent_has("vlongship raid Wasa Lejre"),
  "a ship in a state that is not 'docked' never raids")
fresh_client(); dofile(PLUGIN); feed_all()

-- targets listing splits home and foreign
printed = {}
araid("targets")
check(said("Home: Lodbrok's Hold, Eiriksby") and said("Foreign: Paris, Lindisfarne, Hamwic"),
  "araid targets lists lineage as Home and historical as Foreign")

-- =====================================================================
-- BOTH TARGET GROUPS (2.1.0): the foreign/historical towns join the town table,
-- and 'araid pool home|foreign' chooses which group the auto-target raids.

-- the display: home section carries heat and the auto-pool marker by default;
-- foreign rows are clickable, carry '-' for heat, and are never 'calm'
local heat2 = tostring(state["plugin.3s-raid-gmcp.heat"])
check(heat2:find("@{warning,bold}Home", 1, true) and heat2:find("<- auto pool", 1, true),
  "home section wears the auto-pool marker by default")
check(heat2:find("Foreign", 1, true) and heat2:find("@{accent,click=araid target Paris}", 1, true),
  "foreign towns are listed and clickable")
check(heat2:match("Paris%s*@{}%s*%-") ~= nil, "foreign heat column is '-' (the feed carries none)")
check(not heat2:match("Paris[^\n]*calm"), "a foreign town is never marked calm")

-- clicking a foreign row targets it like any other town
fresh_client(); dofile(PLUGIN); feed_all()
araid("target Paris"); araid("on"); sent = {}
tick(36)
check(sent_has("vlongship raid Vasa Paris") and sent_has("vlongship raid Regal Paris"),
  "a foreign town works as a manual target")

-- pool foreign: persisted, shown in the status line, section marker moves
fresh_client(); dofile(PLUGIN); feed_all()
araid("pool foreign")
check(store["pool"] == "foreign", "the pool choice persists")
check(tostring(state["plugin.3s-raid-gmcp.status"]):find("pool foreign", 1, true),
  "the status line names the pool")
local heat3 = tostring(state["plugin.3s-raid-gmcp.heat"])
check(heat3:find("@{warning,bold}Foreign", 1, true) and not heat3:find("@{warning,bold}Home", 1, true),
  "pool foreign moves the auto-pool marker to the foreign section")

-- auto-target over the foreign pool dispatches to a foreign town and locks it
araid("auto on"); araid("hold 600"); araid("on"); sent = {}
tick(36)
local FOREIGN = { Paris = true, Lindisfarne = true, Hamwic = true }
local ftown = sent[1] and sent[1]:match("^vlongship raid %a+ (.+)$")
check(ftown and FOREIGN[ftown], "auto pool foreign picks a foreign town (got " .. tostring(ftown) .. ")")
check(said("(auto foreign)"), "the dispatch note says it was the foreign auto pool")
gmcp("Guild.Fleet", FLEET_P1); gmcp("Guild.Fleet", FLEET_P2)
gmcp("Guild.Fleet", FLEET_P3); gmcp("Guild.Fleet", FLEET_P4)
sent = {}
tick(36)
local ftown2 = sent[1] and sent[1]:match("^vlongship raid %a+ (.+)$")
check(ftown2 == ftown, "the hold locks a foreign pick exactly as a home one")

-- switching pools drops the lock: the very next pass raids the OTHER group
araid("pool home")
gmcp("Guild.Fleet", FLEET_P1); gmcp("Guild.Fleet", FLEET_P2)
gmcp("Guild.Fleet", FLEET_P3); gmcp("Guild.Fleet", FLEET_P4)
sent = {}
tick(36)
local htown = sent[1] and sent[1]:match("^vlongship raid %a+ (.+)$")
check(htown and not FOREIGN[htown],
  "switching pools drops the lock - next pass raids home (got " .. tostring(htown) .. ")")

-- a restart honours the stored pool
fresh_client(); store["pool"] = "foreign"; dofile(PLUGIN); feed_all()
check(tostring(state["plugin.3s-raid-gmcp.status"]):find("pool foreign", 1, true),
  "restart: the stored pool still holds")

-- pool foreign with no historical list yet: armed, but nothing is sent
fresh_client(); store["pool"] = "foreign"; dofile(PLUGIN)
gmcp("Guild.Fleet", FLEET_P1:gsub('"pages": 4', '"pages": 2'))
gmcp("Guild.Fleet", FLEET_P2:gsub('"pages": 4, "ships"', '"pages": 2, "ships"'):gsub('"page": 2', '"page": 2'))
araid("auto on"); araid("on"); sent = {}
tick(36)
-- No foreign list from the feed or a scan yet: the baked-in FOREIGN_DEFAULT is the pool
-- (2.3.1). Every raid must go to one of THOSE towns - read from the plugin itself, so the
-- check follows the list rather than a copy of it.
do
  local src = assert(io.open(PLUGIN)):read("a")
  local block = assert(src:match("local FOREIGN_DEFAULT = (%b{})"), "FOREIGN_DEFAULT not found in the plugin")
  local default = {}
  for town in block:gmatch('"([^"]+)"') do default[town] = true end
  local all_default = #sent > 0
  for _, cmd in ipairs(sent) do
    local town = cmd:match("^vlongship raid %S+ (.+)$")
    if not (town and default[town]) then all_default = false end
  end
  check(all_default, "no foreign list yet: raids go to the baked-in foreign towns only (got "
    .. table.concat(sent, " | ") .. ")")
end

-- =====================================================================
-- panel: only widget types the host renders (the farmer's empty-panel lesson)
-- 'table' joined the host's widget set before the Raids tab (2.3.0) used it.
local VALID = { value = true, label = true, text = true, buttonrow = true, input = true, table = true }
local widgets_ok = true
for _, tab in ipairs(panel and panel.tabs or {}) do
  for _, w in ipairs(tab.widgets or {}) do
    if not VALID[w.type] then widgets_ok = false end
  end
end
check(panel and panel.tabs and #panel.tabs == 3 and widgets_ok,
  "panel: three tabs (Raid, Raids, Settings), every widget a type the host knows")

-- =====================================================================
-- 5 Oct: Scrye's idle guard disarms it, and nothing goes out after
fresh_client(); dofile(PLUGIN); feed_all()
araid("target Lejre"); araid("on"); sent = {}
check(#idle_hooks == 1, "the plugin listens for the idle guard")
printed = {}
for _, f in ipairs(idle_hooks) do f() end
check(said("auto-raid disarmed"), "the idle guard disarms auto-raid")
check(state["plugin.3s-raid-gmcp.armed"] == "OFF",
  "...and the panel shows it OFF")
check(#notified == 0, "...no phone buzz while dispatch notify is off")
tick(72)
check(#sent == 0, "no raid after the guard fired")
printed = {}
for _, f in ipairs(idle_hooks) do f() end
check(#printed == 0, "a disarmed bot is left alone")
araid("notify send on"); araid("on"); notified = {}
for _, f in ipairs(idle_hooks) do f() end
check(#notified == 1, "dispatch notify on: the phone hears about it")

print(string.format("raidgmcp_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
