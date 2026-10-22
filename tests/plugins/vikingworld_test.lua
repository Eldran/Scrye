-- Harness for 3s-viking-world. The Guild.Voyage payloads are verbatim from the 27 Aug
-- 2026 capture (_lab/captures/gmcp-fields-Goran-20260827.md). The chart rows, the
-- queued path and the Guild.Map terrain rows are SYNTHETIC - the capture never
-- carried them (no chart was up, the map barely refreshed) - and are marked so;
-- their adapters carry matching VERIFY LIVE notes.
--
-- What this file protects: the voyage positional contract (adapter slot N <->
-- composer read N, and sea_nav's metric slots), the no-voyage fleet fallback, the
-- chart render + resolve options, sea-nav's tour (pick nearest, hold course,
-- arrive, resolve by preference with conditions, resume original course), the
-- travel bridge (clicks emit viking.travel / the towns list clicks vgo), vikloc,
-- and the panel vocabulary.
--
-- Equivalent mutants, documented rather than faked: (1) vset's no-change skip -
-- churn only, and live refreshes always differ (the tests mimic that); (2) the
-- MEE mask branch in build_map - no GMCP source writes mee rows yet, so the
-- branch is dormant until the server ships a fog-of-war mask.

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-viking-world/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

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

local function encode(v)
  local t = type(v)
  if t == "number" or t == "boolean" then return tostring(v) end
  if t == "nil" then return "null" end
  if t == "string" then return '"' .. v:gsub('[\\"]', '\\%0') .. '"' end
  local keys = {}
  for k in pairs(v) do keys[#keys + 1] = tostring(k) end
  table.sort(keys)
  local out = {}
  for _, k in ipairs(keys) do out[#out + 1] = encode(k) .. ":" .. encode(v[k]) end
  return "{" .. table.concat(out, ",") .. "}"
end

local printed, sent, emitted, gmcp_hooks, aliases, store, state, panel, bus_hooks, watches
local timers, repeats, next_id

local function fresh_client()
  printed, sent, emitted = {}, {}, {}
  gmcp_hooks, aliases, bus_hooks, watches = {}, {}, {}, {}
  store, state, panel = {}, {}, nil
  timers, repeats, next_id = {}, {}, 0
  scrye = {
    id = "3s-viking-world",
    print = function(x) printed[#printed + 1] = tostring(x) end,
    send = function(x) sent[#sent + 1] = tostring(x) end,
    notify = function() end,
    json = { decode = decode, encode = encode },
    store = {
      get = function(k) return store[k] end,
      set = function(k, v) store[k] = tostring(v) end,
      delete = function(k) store[k] = nil end,
    },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    on = function(k, fn) bus_hooks[#bus_hooks + 1] = { k = k, fn = fn } end,
    emit = function(k, v) emitted[#emitted + 1] = { k = k, v = v } end,
    onConnect = function() end,
    onDisconnect = function() end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addPanel = function(pn) panel = pn end,
    setState = function(k, v)
      state[k] = v
      for _, w in ipairs(watches) do if w.path == k then w.fn(v, k) end end
    end,
    watch = function(path, fn) watches[#watches + 1] = { path = path, fn = fn } end,
    getState = function(k) return state[k] end,
    after = function(d, fn) next_id = next_id + 1; timers[next_id] = { at = d, fn = fn }; return next_id end,
    every = function(_, fn) next_id = next_id + 1; repeats[next_id] = fn; return next_id end,
    cancel = function(id) if id then timers[id] = nil; repeats[id] = nil end end,
  }
end

local function gmcp(pkg, json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == pkg then h.fn(json) end end
end
local function fire_timers()
  for _ = 1, 6 do
    local due = {}
    for id, e in pairs(timers) do due[#due + 1] = { id = id, e = e } end
    if #due == 0 then return end
    table.sort(due, function(a, b) if a.e.at ~= b.e.at then return a.e.at < b.e.at end return a.id < b.id end)
    for _, d in ipairs(due) do timers[d.id] = nil; d.e.fn() end
  end
end
local function tick(n) for _ = 1, n or 1 do for _, fn in pairs(repeats) do fn() end end end
local function st(k) return tostring(state["plugin.3s-viking-world." .. k] or "") end
local function alias(pat, ...)
  for _, a in ipairs(aliases) do if a.pattern == pat then a.run(...) return true end end
  return false
end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end
local function sent_has(x)
  for _, c in ipairs(sent) do if c == x then return true end end
  return false
end
local function find_widget(want_type, bind)
  for _, tab in ipairs(panel and panel.tabs or {}) do
    local function walk(ws)
      for _, w in ipairs(ws or {}) do
        if w.type == want_type and (bind == nil or w.bind == bind) then return w end
        if w.widgets then local hit = walk(w.widgets); if hit then return hit end end
      end
    end
    local hit = walk(tab.widgets)
    if hit then return hit end
  end
end

-- ---- payloads ----
-- verbatim: the capture's live-voyage main example (Guild.Voyage, unpaged shape)
local VOYAGE_LIVE = '{ "pages": 6, "voyage": { "ship_identity": "whisper-prowed", "x": 0, "y": 11, "weather_key": 0, "danger": 9, "hull": 100, "morale": 100, "contract_type": "salvage", "threat_name": "reef maze", "state": "sailing", "ship_id": 5, "next_move_in": 2033, "threat_level": 2, "crew_alive": 60, "paused_type": "", "captain_style": "bold", "supplies": 92, "steps_sailed": 1, "hull_stress": 0, "contract_name": "Deep Salvage", "ship_name": "Vasa", "crew_max": 60, "height": 16, "threat_pressure": 0, "width": 16 }, "page": 6, "guild": "viking" }'
-- SYNTHETIC: ship at B02, island at B05 (distance 9), wreck at D02 (distance 4):
-- the wreck is STRICTLY nearest, so the tour order is deterministic
local CHART = '{ "pages": 1, "page": 1, "voyage_chart": { "height": 4, "chart_mode": "coastal", "width": 5 }, "voyage_chart_rows": [ "#####", " S  I", "     ", " W   " ], "guild": "viking" }'
-- verbatim: fleet ships page (pages renumbered to a complete 1-page burst)
local FLEET = '{ "pages": 1, "ships": [ { "convoy_bonus": 0, "durability": 100, "target": "Nidaros", "state": "raiding", "tier": 5, "convoy_size": 0, "secs": 13264, "saga_title": "the Undying", "convoy": 0, "held": 0, "saga_raids": 159, "crew": 60, "id": 16, "name": "Kronan" }, { "convoy_bonus": 0, "durability": 100, "target": "", "state": "docked", "tier": 5, "convoy_size": 0, "secs": 0, "saga_title": "the Iron-Prowed", "convoy": 0, "held": 0, "saga_raids": 61, "crew": 60, "id": 5, "name": "Vasa" } ], "page": 1, "guild": "viking" }'
-- verbatim: shplots from the Settlement capture
local SETTLE = '{ "shplots": { "h2": 12, "h5": 0, "h3": 4, "h4": 7, "h1": 37 }, "full": 1, "pages": 1, "page": 1, "sproj": [ ], "guild": "viking" }'
-- verbatim-shaped: goods/aids objects (page 7 of the Voyage full burst carried the keys, empty)
local CARGO = '{ "vaids": { "chart": 2 }, "vgoods": { "salvage": 14, "mead": 3 }, "vcurios": [ "sea glass" ], "vboons": { }, "full": 1, "pages": 1, "voyage": { "ship_identity": "whisper-prowed", "x": 0, "y": 11, "weather_key": 0, "danger": 9, "hull": 100, "morale": 100, "contract_type": "salvage", "threat_name": "reef maze", "state": "sailing", "ship_id": 5, "next_move_in": 2033, "threat_level": 2, "crew_alive": 60, "paused_type": "", "captain_style": "bold", "supplies": 92, "steps_sailed": 1, "hull_stress": 0, "contract_name": "Deep Salvage", "ship_name": "Vasa", "crew_max": 60, "height": 16, "threat_pressure": 0, "width": 16 }, "page": 1, "guild": "viking" }'
-- SYNTHETIC: Guild.Map terrain rows (shape per enc={terrain:"glyph"}; VERIFY LIVE)
local MAP_T = '{ "pages": 1, "page": 1, "full": 1, "terrain": [ "ppppffff", "ppSpffff", "ppppwwww", "hhhhwwww" ], "pos": { "x": 2, "y": 1 }, "guild": "viking" }'

-- =====================================================================
fresh_client(); dofile(PLUGIN); fire_timers()

-- honest before the feed
check(st("sea"):find("No voyage under way", 1, true), "sea: no voyage before the feed")
check(st("maphdr"):find("waiting for Guild.Map terrain", 1, true), "map: honest waiting line")

-- fleet fallback (no voyage yet)
gmcp("Guild.Fleet", FLEET); gmcp("Guild.Settlement", SETTLE); fire_timers()
local sea = st("sea")
check(sea:find("Kronan", 1, true) and sea:find("raiding", 1, true) and sea:find("Nidaros", 1, true),
  "sea: fleet fallback lists the raiding ship")
check(sea:find("h1 37 / h2 12", 1, true), "sea: ship plots line")

-- live voyage: the positional contract end to end
gmcp("Guild.Voyage", VOYAGE_LIVE); fire_timers()
sea = st("sea")
check(sea:find("Voyage: Vasa - Deep Salvage (salvage)", 1, true), "sea: voyage header")
check(sea:find("State:    sailing", 1, true), "sea: state slot")
check(sea:find("Next square: 2033s", 1, true), "sea: next_move_in slot")
check(sea:find("Position: L01", 1, true), "sea: x=0,y=11 renders as L01")
check(sea:find("Threat:   reef maze [2]", 1, true), "sea: threat name + level")
check(sea:find("Danger: 9", 1, true), "sea: danger slot")
check(sea:find("Captain:  bold", 1, true), "sea: captain slot")
check(sea:find("Identity: whisper-prowed", 1, true), "sea: identity slot")
check(sea:find("Hull 100%   Morale 100%   Supplies 92%   Stress 0%", 1, true), "sea: the four metrics")
check(sea:find("Crew 60/60", 1, true), "sea: crew")

-- chart render
gmcp("Guild.Voyage", CHART); fire_timers()
check(st("seachart"):find("#####", 1, true) and st("seachart"):find(" S  I", 1, true),
  "sea: chart rows render")
check(st("sea"):find("Chart 5x4", 1, true), "sea: chart header w x h")

-- voyage cargo tab
gmcp("Guild.Voyage", CARGO); fire_timers()
local voy = st("voyage")
check(voy:find("Salvage", 1, true) and voy:find("14", 1, true), "voyage: goods with counts")
check(voy:find("sea glass", 1, true), "voyage: curios listed")
check(voy:find("Chart", 1, true) and voy:find("2", 1, true), "voyage: aids with counts")

-- =====================================================================
-- sea-nav tour: nearest feature first (island at B04 beats wreck at D02? ship B02:
-- island d=(0)^2+(2)^2=4, wreck d=(2)^2+(0)^2=4 - tie; either is fine, course must be set)
gmcp("Guild.Voyage", CHART); fire_timers()
alias("^vnav (on|off)$", "on")
tick(3)                            -- give the pacing clock some seconds
-- a REAL voyage refresh always differs (the move clock ticks); an identical
-- payload is skipped by vset's no-change guard, exactly as live
gmcp("Guild.Voyage", VOYAGE_LIVE:gsub('"next_move_in": 2033', '"next_move_in": 2020'))
fire_timers()
check(sent_has("vvoyage clear"), "sea-nav: clears the queue before setting course")
local queued
for _, c in ipairs(sent) do queued = c:match("^vvoyage queue (%a%d%d)$") or queued end
check(queued == "D02", "sea-nav: NEAREST feature first (wreck D02, got " .. tostring(queued) .. ")")

-- arrival via the queued-path route: the course reaches D02, then the path
-- empties while the wreck STAYS charted - toured, not re-picked, tour advances
gmcp("Guild.Voyage", '{ "pages": 1, "page": 1, "vqpath": [ { "x": 2, "y": 3 } ], "guild": "viking" }')
tick(3)
gmcp("Guild.Voyage", VOYAGE_LIVE:gsub('"next_move_in": 2033', '"next_move_in": 2010')); fire_timers()
sent = {}
gmcp("Guild.Voyage", '{ "pages": 1, "page": 1, "vqpath": [ ], "guild": "viking" }')
tick(3)
gmcp("Guild.Voyage", VOYAGE_LIVE:gsub('"next_move_in": 2033', '"next_move_in": 2005')); fire_timers()
local queued2
for _, c in ipairs(sent) do queued2 = c:match("^vvoyage queue (%a%d%d)$") or queued2 end
check(queued2 == "B05",
  "sea-nav: arrival tours the wreck (still charted) and advances to the island (got " .. tostring(queued2) .. ")")

-- a NEW voyage (different ship/contract) clears the toured list: the wreck is
-- a candidate again and, being nearest, is picked first
sent = {}
local NEWVOY = VOYAGE_LIVE:gsub('"ship_id": 5', '"ship_id": 14'):gsub('"contract_name": "Deep Salvage"', '"contract_name": "Coastal Run"')
tick(3)
gmcp("Guild.Voyage", NEWVOY); fire_timers()
local queued3
for _, c in ipairs(sent) do queued3 = c:match("^vvoyage queue (%a%d%d)$") or queued3 end
check(queued3 == "D02", "sea-nav: a fresh voyage resets the tour (got " .. tostring(queued3) .. ")")

-- resolve preference: hull fine -> evade?hull<40 skipped, hunt picked
sent = {}
alias([[^vnav resolve (.+)$]], "evade?hull<40,hunt,plunder")
gmcp("Guild.Voyage", '{ "pages": 1, "page": 1, "vresolve": [ "hunt", "evade", "plunder" ], "guild": "viking" }')
tick(4)
gmcp("Guild.Voyage", VOYAGE_LIVE:gsub('"next_move_in": 2033', '"next_move_in": 1990')); fire_timers()
check(sent_has("vvoyage resolve hunt"), "sea-nav: condition skips evade at full hull, picks hunt")

-- hull low -> evade wins (hull slot 11 is what the metric reads)
fresh_client(); dofile(PLUGIN); fire_timers()
alias("^vnav (on|off)$", "on")
alias([[^vnav resolve (.+)$]], "evade?hull<40,hunt")
gmcp("Guild.Voyage", CHART)
local LOWHULL = VOYAGE_LIVE:gsub('"hull": 100', '"hull": 30')
tick(4)
gmcp("Guild.Voyage", '{ "pages": 1, "page": 1, "vresolve": [ "hunt", "evade" ], "guild": "viking" }')
tick(4)
gmcp("Guild.Voyage", LOWHULL); fire_timers()
check(sent_has("vvoyage resolve evade"), "sea-nav: hull 30 satisfies evade?hull<40")

-- resolve options are clickable in the panel text
check(st("resolveopts"):find("click=vvoyage resolve hunt", 1, true), "sea: resolve options clickable")
-- An encounter whose options are ALL unknown to the list must not stall in
-- silence. The option set depends on the encounter and we have seen exactly one
-- encounter's worth (["scout","plunder","resupply"], 31 Aug), so meeting a novel
-- one is expected rather than exotic.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  alias("^vnav (on|off)$", "on")
  alias([[^vnav resolve (.+)$]], "evade?hull<40,hunt")
  gmcp("Guild.Voyage", CHART)
  tick(4)
  sent = {}; printed = {}
  gmcp("Guild.Voyage", '{ "pages": 1, "page": 1, "vresolve": [ "scout", "parley" ], "guild": "viking" }')
  tick(4)
  gmcp("Guild.Voyage", VOYAGE_LIVE:gsub('"next_move_in": 2033', '"next_move_in": 1971')); fire_timers()
  local resolved = false
  for _, c in ipairs(sent) do if c:find("vvoyage resolve", 1, true) then resolved = true end end
  check(not resolved, "sea-nav: an unknown option set is NOT guessed at")
  check(said("none are in your resolve list"), "sea-nav: ...it says why it is holding")
  check(said("scout") and said("parley"), "sea-nav: ...and names what was offered")
  -- and only once per node. The live feed pushes a voyage burst every few
  -- seconds, so "once" has to survive more bursts, not just more clock.
  printed = {}
  gmcp("Guild.Voyage", VOYAGE_LIVE:gsub('"next_move_in": 2033', '"next_move_in": 1970')); fire_timers()
  tick(4)
  gmcp("Guild.Voyage", VOYAGE_LIVE:gsub('"next_move_in": 2033', '"next_move_in": 1968')); fire_timers()
  check(not said("none are in your resolve list"), "sea-nav: ...said once, not on every burst")

  -- "*" at the end of the list is the opt-in last resort
  alias([[^vnav resolve (.+)$]], "evade?hull<40,hunt,*")
  sent = {}
  gmcp("Guild.Voyage", VOYAGE_LIVE:gsub('"next_move_in": 2033', '"next_move_in": 1969')); fire_timers()
  check(sent_has("vvoyage resolve scout"),
    "sea-nav: a trailing * answers an unknown node with its first offer")
end


-- =====================================================================
-- map: synthetic terrain (VERIFY LIVE) renders with pos, unknown chars flagged
fresh_client(); dofile(PLUGIN); fire_timers()
gmcp("Guild.Map", MAP_T); fire_timers()
check(st("maphdr"):find("Territory 8x4", 1, true) and st("maphdr"):find("@ 2,1", 1, true),
  "map: header from terrain rows + pos")
check(st("map"):find("ppSpffff", 1, true), "map: terrain rows render")
check(st("maplocs"):find("Midgard", 1, true), "map: default locations listed")
-- position-only refresh moves the marker, keeps the terrain
gmcp("Guild.Map", '{ "pages": 1, "page": 1, "pos": { "x": 5, "y": 2 }, "guild": "viking" }')
fire_timers()
check(st("maphdr"):find("@ 5,2", 1, true) and st("map"):find("ppSpffff", 1, true),
  "map: pos-only burst moves the marker and keeps terrain")

-- vikloc names a coordinate and it shows in the list
alias([[^vikloc (\d+) (\d+)(?:\s+(.*))?$]], "2", "1", "Fish Camp")
fire_timers()
check(st("maplocs"):find("Fish Camp", 1, true), "vikloc: named location appears")
-- ...and it becomes a TRAVEL destination, because the engine plans by BFS now
-- and so needs a coordinate and a name rather than a hand-recorded route
check(st("towns"):find("click=vgo Fish Camp", 1, true),
  "vikloc: naming a place puts it in the travel list")
do
  local handed = false
  for _, e in ipairs(emitted) do
    if e.k == "viking.locnames" and tostring(e.v):find("Fish Camp", 1, true) then handed = true end
  end
  check(handed, "vikloc: the name is handed to the travel engine over the bus")
end
-- sites that always had coordinates but never had routes are listed too
check(st("towns"):find("click=vgo Jarl", 1, true),
  "travel list: Jarl is a destination now that no route needs recording")

-- the towns list clicks route through the command pipeline to vgo
check(st("towns"):find("click=vgo Midgard", 1, true), "travel: town list clicks run vgo")

-- The map colorgrid click WALKS. It used to emit "viking.travel" and wait for
-- 3s-viking-status-gmcp to answer; the engine moved into this plugin on 30 Aug,
-- so that was a round trip out and back, and the note it printed sent you
-- hunting a dependency that no longer exists. Assert the walk, not the message:
-- an emit landing is only evidence something was asked for.
local grid = find_widget("colorgrid", "plugin.3s-viking-world.map")
check(grid and grid.onClick, "map: colorgrid is clickable")
if grid and grid.onClick then
  -- the engine plans FROM somewhere, so tell it where we stand first; without
  -- this the click is answered with "can't tell where you are", which is also
  -- what the old emit-only assertion could not have noticed
  alias([[^vhere (.+)$]], "Nidaros")
  emitted = {}; sent = {}; printed = {}
  grid.onClick(35, 17, "M")     -- Midgard's coordinate
  check(#sent > 0, "map: clicking a town on the grid walks there without a second plugin")
  check(sent[1] == "leave" and sent[#sent] == "enter",
    "map: ...as a proper wrapped walk (got " .. tostring(sent[1]) .. " .. "
    .. tostring(sent[#sent]) .. ")")
  local said_dep = false
  for _, l in ipairs(printed) do
    if tostring(l):find("3s-viking-status-gmcp", 1, true) then said_dep = true end
  end
  check(not said_dep, "map: ...and says nothing about needing the status plugin")
end


-- =====================================================================
-- 28 Aug capture: vboons counters, crew memories, the M landmass tile
fresh_client(); dofile(PLUGIN); fire_timers()
-- all-zero boons = none active
gmcp("Guild.Voyage", '{ "pages": 1, "page": 1, "vboons": { "rigging_bonus": 0, "storm_charm_ready": 0, "favorable_current_steps": 0 }, "guild": "viking" }')
fire_timers()
check(st("voyage"):find("Boons", 1, true) and st("voyage"):find("none", 1, true),
  "vboons: all-zero counters render as none")
-- a live boon shows with its count; zeros stay hidden
gmcp("Guild.Voyage", '{ "pages": 1, "page": 1, "vboons": { "rigging_bonus": 0, "storm_charm_ready": 1 }, "guild": "viking" }')
fire_timers()
check(st("voyage"):find("Storm Charm Ready", 1, true) and not st("voyage"):find("Rigging Bonus", 1, true),
  "vboons: non-zero shown, zero hidden")
-- crew memories: full sentences, commas intact (verbatim from the capture)
gmcp("Guild.Voyage", '{ "pages": 1, "page": 1, "vmem": [ "The crew remembers the bitter stretch of rationed food." ], "guild": "viking" }')
fire_timers()
check(st("voyage"):find("Crew memory", 1, true)
  and st("voyage"):find("The crew remembers the bitter stretch of rationed food.", 1, true),
  "vmem: crew memories shown whole")
-- the M tile is charted (landmass), not an unmapped '?'
gmcp("Guild.Voyage", VOYAGE_LIVE)
gmcp("Guild.Voyage", '{ "pages": 1, "page": 1, "voyage_chart": { "height": 3, "chart_mode": "coastal", "width": 3 }, "voyage_chart_rows": [ "MMM", "MTM", "SOO" ], "guild": "viking" }')
fire_timers()
check(st("seachart"):find("MMM", 1, true), "chart: M landmass tiles render")
check(not st("sea"):find("unmapped chart chars", 1, true), "chart: M is a known tile now")

-- =====================================================================
-- panel vocabulary
local VALID = { value = true, label = true, text = true, buttonrow = true, input = true,
                button = true, colorgrid = true, barlist = true, row = true }
local widgets_ok = true
local function walk(ws)
  for _, w in ipairs(ws or {}) do
    if not VALID[w.type] then widgets_ok = false; print("bad widget: " .. tostring(w.type)) end
    if w.widgets then walk(w.widgets) end
  end
end
for _, tab in ipairs(panel and panel.tabs or {}) do walk(tab.widgets) end
check(panel and #panel.tabs == 6 and widgets_ok, "panel: six tabs, every widget a known type")
-- the Mission runner moved here with the travel engine it walks on
do
  local tset = {}
  for _, tab in ipairs(panel and panel.tabs or {}) do tset[tab.title] = true end
  check(tset["Mission"], "panel: the Mission tab lives here now, beside the engine it walks on")
end
-- the Plan tab moved here with the other grids: it binds ACROSS to the settlement
-- plugin's state (paths are global), and the art came with it because image paths
-- are sandboxed to the declaring plugin's own folder
do
  local plan
  for _, tab in ipairs(panel and panel.tabs or {}) do
    if tab.title == "Plan" then
      for _, wd in ipairs(tab.widgets or {}) do if wd.type == "colorgrid" then plan = wd end end
    end
  end
  check(plan ~= nil, "panel: the Plan tab lives here now")
  check(plan and plan.bind == "plugin.3s-viking-status-gmcp.plangrid",
    "Plan: bound across to the plugin that computes it")
  check(plan and type(plan.images) == "table" and plan.images["7"] == "tiles/tower.png",
    "Plan: the tower tile came along, declared relative to THIS plugin")
end


-- =====================================================================
-- THE GUILD MAP AS A GRAPH (2.15.0): Guild.Map's east/south EDGE grids drive
-- travel by BFS instead of the 28 hand-recorded ROUTES. Verified against the
-- 29 Aug capture before a line was written: replaying every recorded route as
-- coordinate arithmetic lands exactly on its town, and all 540 answerable edge
-- lookups say passable -- so the grids are the routes' own map. The recorded
-- table stays as the ORACLE: a grid that cannot reproduce it is not understood,
-- and travel falls back rather than walking you into the sea.
local function jrows(rows)
  local q = {}
  for i, r in ipairs(rows) do q[i] = '"' .. r .. '"' end
  return "[" .. table.concat(q, ",") .. "]"
end
local function open_grid()
  local east, south = {}, {}
  for y = 1, 35 do east[y] = string.rep("1", 69) end
  for y = 1, 34 do south[y] = string.rep("1", 70) end
  return east, south
end
local function feed_map(east, south)
  gmcp("Guild.Map", '{ "east": ' .. jrows(east) .. ', "w": 70, "h": 35, "guild": "viking" }')
  gmcp("Guild.Map", '{ "south": ' .. jrows(south) .. ', "guild": "viking" }')
  fire_timers()
end
-- an edge string with x blocked (0-based x, 1-based Lua string position x+1)
local function block(row, x) return row:sub(1, x) .. "0" .. row:sub(x + 2) end

do
  -- 1. an open map: the trip goes straight there instead of through Midgard.
  -- Nidaros (25,9) -> Lodbrok's Hold (17,9) is 8 west; the recorded table walks
  -- it in 64 moves, all the way to Midgard and back out again.
  fresh_client(); dofile(PLUGIN); fire_timers()
  local east, south = open_grid()
  feed_map(east, south)
  store["curtown"] = "Nid"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Lod")
  check(#sent == 10, "map travel: Nid->Lod is leave + 8 west + enter (got " .. #sent .. ")")
  check(sent[1] == "leave" and sent[#sent] == "enter", "map travel: the walk is wrapped in leave/enter")
  local wests = 0
  for _, c in ipairs(sent) do if c == "w" then wests = wests + 1 end end
  check(wests == 8, "map travel: exactly the 8 westward steps between them")
  check(said("(10 steps, map)"), "map travel: the line says the map planned it")

  -- 2. a wall on their shared row: BFS goes around, it does not give up
  fresh_client(); dofile(PLUGIN); fire_timers()
  east, south = open_grid()
  for x = 17, 24 do east[10] = block(east[10], x) end   -- row 9 (1-based 10) closed
  feed_map(east, south)
  store["curtown"] = "Nid"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Lod")
  local n_moves, s_moves = 0, 0
  for _, c in ipairs(sent) do
    if c == "n" then n_moves = n_moves + 1 elseif c == "s" then s_moves = s_moves + 1 end
  end
  check(#sent == 12, "walled map: the detour costs two extra steps (got " .. #sent .. ")")
  check(n_moves == 1 and s_moves == 1, "walled map: it steps off the blocked row and back onto it")
  check(said("steps, map)"), "walled map: still planned from the map")

  -- 3. THE ORACLE. A grid that contradicts a recorded route is a grid we do not
  -- understand: travel must fall back to the table, not improvise.
  fresh_client(); dofile(PLUGIN); fire_timers()
  east, south = open_grid()
  east[18] = block(east[18], 35)        -- row 17, the first step of Midgard->Holmgard
  feed_map(east, south)
  store["curtown"] = "Nid"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Lod")
  check(said("recorded route"), "oracle: a grid that fails the known routes is not trusted")
  check(#sent == 68, "oracle: and the recorded hub route is walked instead - 64 moves"
    .. " wrapped in two leave/enter pairs, against the map's 10")

  -- 4. an INCOMPLETE grid is refused the same way - which is exactly what the
  -- captures hold (their south half is truncated), so this is the real shape of
  -- a half-arrived burst rather than an invented one
  fresh_client(); dofile(PLUGIN); fire_timers()
  east, south = open_grid()
  local half = {}
  for y = 1, 15 do half[y] = south[y] end
  feed_map(east, half)
  store["curtown"] = "Nid"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Lod")
  check(said("recorded route"), "a half-arrived map is not trusted either")

  -- 5. east and south arrive in SEPARATE full=1 bursts, and a full burst clears
  -- the previous one's paged keys - so the adapter has to latch each as it lands
  fresh_client(); dofile(PLUGIN); fire_timers()
  east, south = open_grid()
  local function paged(key, rows)
    for p = 2, 8 do
      local chunk = {}
      for i = (p - 2) * 5 + 1, math.min((p - 2) * 5 + 5, #rows) do chunk[#chunk + 1] = rows[i] end
      gmcp("Guild.Map", string.format('{ "%s": %s, "full": 1, "pages": 8, "page": %d, "guild": "viking" }',
        key, jrows(chunk), p))
    end
  end
  gmcp("Guild.Map", '{ "full": 1, "pages": 8, "page": 1, "w": 70, "h": 35, "guild": "viking" }')
  paged("east", east)
  gmcp("Guild.Map", '{ "full": 1, "pages": 8, "page": 1, "guild": "viking" }')
  paged("south", south)
  fire_timers()
  store["curtown"] = "Nid"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Lod")
  check(said("steps, map)"), "paged: east survives the south burst's full-replace")
  check(#sent == 10, "paged: and the route is the short one")

  -- 6. Blot is the open-air site the recorded table already knew about: you
  -- neither leave nor enter it, and the wrapper learns that from the table
  fresh_client(); dofile(PLUGIN); fire_timers()
  east, south = open_grid()
  feed_map(east, south)
  store["curtown"] = "Blot"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Mid")
  check(sent[1] ~= "leave", "Blot: no 'leave' walking out of the open-air site")
  check(sent[#sent] == "enter", "Blot: but you do enter Midgard at the far end")
end

-- KNOWN EQUIVALENT MUTANT (documented, not tested): the oracle ends with
-- `gmap.trusted = checked > 0`, and forcing that to plain `true` kills no test.
-- Every constructible bad grid is rejected earlier, by the per-step passable()
-- check or the endpoint comparison; `checked > 0` only guards the degenerate
-- case of a grid present while NO recorded route can be replayed at all, which
-- needs the town-coordinate table itself to be empty. It stays as the defensive
-- line it is: a map validated against nothing has not been validated.

-- =====================================================================
-- POS EARNS ITS TRUST (2.15.0): Guild.Map's `pos` reads like the cell you stand
-- on and would retire `vhere` - but it read (49,17) in all three captures, taken
-- days apart from different places. So nothing trusts it until the towns whose
-- coordinates we already know have examined it.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  store["curtown"] = "Mid"                       -- Midgard is (35,17)
  printed = {}
  for _ = 1, 3 do gmcp("Guild.Map", '{ "pos": { "x": 49, "y": 17 }, "guild": "viking" }') end
  fire_timers()
  check(said("does not track you"), "pos: three disagreements and it is written off")

  -- one lucky agreement is not evidence: a static pos that happens to sit on a
  -- town would otherwise be promoted on its first reading
  fresh_client(); dofile(PLUGIN); fire_timers()
  store["curtown"] = "Mid"
  printed = {}
  gmcp("Guild.Map", '{ "pos": { "x": 35, "y": 17 }, "guild": "viking" }')
  fire_timers()
  check(not said("map position confirmed live"), "pos: one agreement is not yet trust")

  fresh_client(); dofile(PLUGIN); fire_timers()
  store["curtown"] = "Mid"
  printed = {}
  gmcp("Guild.Map", '{ "pos": { "x": 49, "y": 17 }, "guild": "viking" }')
  fire_timers()
  check(not said("does not track you"), "pos: one disagreement does not write it off either")

  fresh_client(); dofile(PLUGIN); fire_timers()
  store["curtown"] = "Mid"
  printed = {}
  for _ = 1, 3 do gmcp("Guild.Map", '{ "pos": { "x": 35, "y": 17 }, "guild": "viking" }') end
  fire_timers()
  check(said("map position confirmed live"), "pos: agreeing with a known town three times earns trust")

  -- once trusted, a town that has MOVED corrects itself rather than being wrong
  printed = {}
  store["curtown"] = "Upp"                       -- recorded at (33,27)
  gmcp("Guild.Map", '{ "pos": { "x": 34, "y": 27 }, "guild": "viking" }')
  fire_timers()
  check(said("has moved to 34,27"), "pos: a trusted position corrects a town that moved")
end

-- =====================================================================
-- EVERY NAMED PLACE IS A DESTINATION (2.16.0): with BFS planning the walk, a
-- site needs coordinates and a name -- not the pair of hand-recorded routes the
-- old engine demanded. Jarl and Berserker had coordinates in DEFAULT_LOCNAMES
-- all along and were unreachable purely for want of those routes.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  local east, south = open_grid()
  feed_map(east, south)

  -- Jarl sits at (50,27); from Midgard (35,17) that is 15 east and 10 south
  store["curtown"] = "Mid"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Jarl")
  check(said("steps, map)"), "Jarl: a site with coordinates but no recorded route is reachable")
  local e_moves, s_moves = 0, 0
  for _, c in ipairs(sent) do
    if c == "e" then e_moves = e_moves + 1 elseif c == "s" then s_moves = s_moves + 1 end
  end
  check(e_moves == 15 and s_moves == 10, "Jarl: 15 east and 10 south, straight there")
  check(said("-> Jarl"), "Jarl: named on the travel line, not shown as a coordinate")

  -- Berserker (51,16) likewise
  store["curtown"] = "Mid"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Berserker")
  check(said("steps, map)") and #sent > 0, "Berserker: reachable for the same reason")

  -- and the ones that were always travelable still are
  store["curtown"] = "Mid"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Uppsala")
  check(#sent == 14, "the recorded towns are untouched (leave + 12 + enter)")
end

-- The names you give places with `vikloc` live in the SEA plugin's store, which
-- this one cannot read - both scrye.store and scrye.shared are scoped per
-- plugin. They arrive over the event bus instead, and each one becomes a place
-- BFS can walk to.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  local east, south = open_grid()
  feed_map(east, south)

  store["curtown"] = "Mid"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Hidden Cove")
  check(said("no settlement matching"), "an unnamed place is not a destination")

  -- this plugin OWNS the names now: `vikloc` is right here, so naming a place
  -- and walking to it is one plugin's business end to end
  alias([[^vikloc (\d+) (\d+)(?:\s+(.*))?$]], "40", "17", "Hidden Cove")
  fire_timers()
  check(st("towns"):find("click=vgo Hidden Cove", 1, true),
    "vikloc: the named place joins the travel list in the same plugin")

  store["curtown"] = "Mid"
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Hidden Cove")
  check(said("steps, map)"), "naming a place makes it travelable at once")
  check(#sent == 7, "Hidden Cove: leave + 5 east + enter (35,17)->(40,17)")
end

-- =====================================================================
-- THE MISSION RUNNER (moved here with the travel engine it walks on). The
-- missions belong to the settlement feed, so 3s-viking-status-gmcp publishes
-- them raw and this plugin reads them across -- state paths are global.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  -- The feed lands AFTER load, which is the whole point: the first Guild.Trade
  -- burst reaches the settlement plugin long after this one has drawn its tab.
  -- Setting it beforehand is what let a dead tab pass its test once already --
  -- the load-time publish happened to catch it, and nothing else ever would.
  check(st("mission"):find("no missions", 1, true), "mission: empty until the feed speaks")
  scrye.setState("plugin.3s-viking-status-gmcp.missions_raw",
    "52782|fetch furs|35|120|900||Birka|furs:9,sunstone:4,grain:9")
  fire_timers()
local mission = st("mission")
check(mission:find("52782", 1, true) and mission:find("Birka", 1, true)
  and mission:find("furs9 sunstone4 grain9", 1, true) and mission:find("35", 1, true),
  "mission: id/town/goods/rep from Guild.Trade")
check(mission:find("click=vmgo 52782", 1, true), "mission: row is clickable (route to Birka known)")

-- mission runner: clicking runs travel + fulfill (curtown = Midgard set via vhere)
alias("^vhere (.+)$", "Midgard")
sent = {}
alias([[^vmgo (\d+)$]], "52782")
local fulfilled, walked = false, false
for _, c in ipairs(sent) do
  if c == "vmission fulfill 52782" then fulfilled = true end
  if c == "leave" or c == "enter" then walked = true end
end
check(fulfilled and walked, "mission click walks the route and fulfills")

-- the sea plugin's map click arrives over the event bus and walks there
sent = {}
for _, h in ipairs(bus_hooks) do
  if h.k == "viking.travel" then h.fn('{ "town": "Uppsala" }') end
end
local walked2 = false
for _, c in ipairs(sent) do if c == "leave" or c == "enter" then walked2 = true end end
check(walked2, "viking.travel event from 3s-viking-world walks to the town")
end


-- =====================================================================
-- NO REMEMBERED TOWN (1.4.1). Every travel test above sets curtown first, so the
-- fallback path -- current_town() asking live_town() -- was never executed, and a
-- forward reference in it (gv, defined further down this file than the function
-- that called it) survived the whole move to blow up on the first real `vgo`.
-- Load-time is no help here: the name only resolves when the line runs.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  local east, south = open_grid()
  feed_map(east, south)
  store["curtown"] = nil                       -- nothing remembered, as on a fresh profile
  sent = {}; printed = {}
  local ok = pcall(function() alias("^vgo (.+)$", "Borgarfjord") end)
  check(ok, "vgo with no remembered town does not error")
  check(said("can't tell where you are"), "...it asks you to set one with vhere")
  check(#sent == 0, "...and sends nothing rather than walking from a guess")

  -- pos does not stand in for a remembered town until it has earned trust: an
  -- untrusted position naming the wrong origin plans a good route from the wrong
  -- place, which is worse than declining to answer
  gmcp("Guild.Map", '{ "pos": { "x": 19, "y": 25 }, "guild": "viking" }')   -- Borgarfjord's cell
  fire_timers()
  sent = {}; printed = {}
  local ok2 = pcall(function() alias("^vgo (.+)$", "Midgard") end)
  check(ok2, "vgo with an untrusted pos does not error either")
  check(said("can't tell where you are"), "...and an unproven pos is still not an answer")

  -- ...and once it HAS earned trust, it answers: three agreements with a town we
  -- already know, and from then on the live position stands in for a remembered
  -- one. This is the whole payoff of the gate, so it is pinned rather than assumed.
  fresh_client(); dofile(PLUGIN); fire_timers()
  feed_map(open_grid())
  store["curtown"] = "Mid"
  for _ = 1, 3 do gmcp("Guild.Map", '{ "pos": { "x": 35, "y": 17 }, "guild": "viking" }') end
  fire_timers()
  check(said("map position confirmed live"), "pos: earns trust from the known towns")
  store["curtown"] = nil                       -- forget where we are; pos must carry it
  gmcp("Guild.Map", '{ "pos": { "x": 19, "y": 25 }, "guild": "viking" }')   -- Borgarfjord
  fire_timers()
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Midgard")
  check(#sent > 0 and not said("can't tell where you are"),
    "a TRUSTED pos stands in for the remembered town")
  check(said("Borgarfjord -> Midgard"), "...and names it as the origin it walked from")

  -- once vhere says where we are, travel works from there
  fresh_client(); dofile(PLUGIN); fire_timers()
  feed_map(open_grid())
  alias("^vhere (.+)$", "Midgard")
  sent = {}; printed = {}
  alias("^vgo (.+)$", "Borgarfjord")
  check(#sent > 0 and said("steps, map)"), "vhere restores travel from a known origin")
end

print(string.format("vikingworld_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
