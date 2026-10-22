
-- 3s-raid-gmcp harness: the feed layer after Guild.Fleet went (17 Sep 2026) -
-- ships off Guild.Voyage, home towns off Guild.Kingdom grudges + Guild.City heat,
-- foreign towns off the `vlongship targets` listing. Run: lua5.4 raid_test.lua

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-raid-gmcp/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

local function encode(v)
  local t = type(v)
  if t == "table" then
    local out = {}
    if v[1] ~= nil or next(v) == nil then
      for _, x in ipairs(v) do out[#out + 1] = encode(x) end
      return "[" .. table.concat(out, ",") .. "]"
    end
    local keys = {}
    for k in pairs(v) do keys[#keys + 1] = tostring(k) end
    table.sort(keys)
    for _, k in ipairs(keys) do out[#out + 1] = '"' .. k .. '":' .. encode(v[k]) end
    return "{" .. table.concat(out, ",") .. "}"
  elseif t == "string" then return '"' .. v:gsub('\\', '\\\\'):gsub('"', '\\"') .. '"'
  elseif t == "boolean" then return v and "true" or "false"
  elseif v == nil then return "null"
  else return tostring(v) end
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

-- ---- the client, as far as the plugin can tell ----
local printed, sent, gmcp_hooks, aliases, store, state, vars, panel
local timers, repeats, next_id, line_hooks, triggers, bus_hooks
local prompt_hooks, cmd_hooks, conn_hooks, disc_hooks, watch_hooks, notifies

local function fresh_client()
  printed, sent = {}, {}
  gmcp_hooks, aliases, line_hooks, triggers, bus_hooks = {}, {}, {}, {}, {}
  prompt_hooks, cmd_hooks, conn_hooks, disc_hooks, watch_hooks, notifies = {}, {}, {}, {}, {}, {}
  store, state, vars, panel = {}, {}, {}, nil
  timers, repeats, next_id = {}, {}, 0
  scrye = {
    id = "3s-raid-gmcp",
    print = function(x) printed[#printed + 1] = tostring(x) end,
    send = function(x)
      sent[#sent + 1] = tostring(x)
      for _, f in ipairs(cmd_hooks) do f(tostring(x)) end
    end,
    notify = function(t) notifies[#notifies + 1] = tostring(t) end,
    log = function() end,
    -- encode as well as decode: the Skills block round-trips its scan through
    -- scrye.json to persist it, and a stub that only decodes made "the scan is
    -- saved" silently untestable (pcall swallows the missing function).
    json = { decode = decode, encode = encode },
    store = {
      get = function(k) return store[k] end,
      set = function(k, v) store[k] = tostring(v) end,
      delete = function(k) store[k] = nil end,
    },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    on = function(k, fn) bus_hooks[#bus_hooks + 1] = { k = k, fn = fn } end,
    emit = function() end,
    onLine = function(fn) line_hooks[#line_hooks + 1] = fn end,
    onConnect    = function(fn) conn_hooks[#conn_hooks + 1] = fn end,
    onDisconnect = function(fn) disc_hooks[#disc_hooks + 1] = fn end,
    onPrompt     = function(fn) prompt_hooks[#prompt_hooks + 1] = fn end,
    onCommand    = function(fn) cmd_hooks[#cmd_hooks + 1] = fn end,
    watch        = function(prefix, fn) watch_hooks[#watch_hooks + 1] = { prefix = prefix, fn = fn } end,
    addTrigger = function(t) triggers[#triggers + 1] = t end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addPanel = function(pn) panel = pn end,
    setState = function(k, v)
      state[k] = v
      for _, w in ipairs(watch_hooks) do
        if k:sub(1, #w.prefix) == w.prefix then w.fn(v, k) end
      end
    end,
    getState = function(k) return state[k] end,
    setVariable = function(k, v) vars[k] = v end,
    getVariable = function(k) return vars[k] end,
    after = function(d, fn) next_id = next_id + 1; timers[next_id] = { at = d, fn = fn }; return next_id end,
    every = function(_, fn) next_id = next_id + 1; repeats[next_id] = fn; return next_id end,
    cancel = function(id) if id then timers[id] = nil; repeats[id] = nil end end,
  }
end

local function gmcp(pkg, json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == pkg then h.fn(json) end end
end
-- flush is scheduled via after(1, ...): run every due timer (order-scheduled)
local function fire_timers()
  for _ = 1, 6 do            -- flushes can schedule flushes; settle in a few rounds
    local due = {}
    for id, e in pairs(timers) do due[#due + 1] = { id = id, e = e } end
    if #due == 0 then return end
    table.sort(due, function(a, b) if a.e.at ~= b.e.at then return a.e.at < b.e.at end return a.id < b.id end)
    for _, d in ipairs(due) do timers[d.id] = nil; d.e.fn() end
  end
end
local function tick(n) for _ = 1, n or 1 do for _, fn in pairs(repeats) do fn() end end end
local function st(k) return tostring(state["plugin.3s-raid-gmcp." .. k] or "") end
local function alias(cmd, rest)
  for _, a in ipairs(aliases) do
    if a.pattern == cmd then a.run(rest); return true end
  end
  return false
end

local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end
local function sent_any(pat)
  for _, l in ipairs(sent) do if l:find(pat, 1, true) then return true end end
  return false
end
-- feed a MUD line to every trigger whose (Lua-translated) pattern matches
local function line(l)
  for _, t in ipairs(triggers) do
    -- the plugin's one trigger: frame lines; hand it the inner capture
    local inner = l:match("^%-~%*(.*)%*~%-%s*$")
    if inner and t.pattern:find("~") then t.run(inner) end
  end
end

-- verbatim payloads from gmcp-fields-Goran-20260917 (pages renumbered)
local VOY_P1 = '{ "longship": [ { "target": "Deep Salvage", "state": "voyaging", "tier": 5, "secs": 0, "crew": 60, "name": "Vasa", "id": 5 }, { "target": "", "state": "docked", "tier": 5, "secs": 0, "crew": 60, "name": "Svarten", "id": 14 } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }'
local VOY_P2 = '{ "longship": [ { "target": "", "state": "docked", "tier": 5, "secs": 0, "crew": 60, "name": "Draken", "id": 15 }, { "target": "Lejre", "state": "raiding", "tier": 5, "secs": 534, "crew": 60, "name": "Kronan", "id": 16 } ], "full": 1, "pages": 2, "guild": "viking", "page": 2 }'
local CITY = '{ "heat": [ 80, 8, 24, 0, 0, 0, 25, 0, 0, 18, 13, 37, 0 ], "buildings": [ { "tier": 5, "id": "dock" } ], "full": 1, "pages": 1, "guild": "viking", "page": 1 }'
local KING = '{ "full": 1, "pages": 1, "guild": "viking", "grudges": [ { "town": "Lejre", "secs": 131688, "lineage": 12 }, { "town": "Eiriksby", "secs": 166414, "lineage": 2 }, { "town": "Lodbrok\'s Hold", "secs": 179206, "lineage": 1 } ], "page": 1 }'
local LISTING = [[
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*  Raid Targets                                             *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*  Historical Targets (free, halved rewards)                *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*  Paris              grain        & sunstone               *~-
-~*  Lindisfarne        timber       & wool                   *~-
-~*  New Hamwic         furs         & pork                   *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*  Lineage Cities (full rewards; raids raise heat)          *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*  Lodbrok's Hold     bread        & fine furs     Calm     *~-
-~*  Eiriksby           salted fish  & cloth         Calm     *~-
-~*  Imaird             mead         & cheese        Warm     *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*  Runestones are rare - any raid may find one by chance.   *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
]]

-- =====================================================================
fresh_client(); dofile(PLUGIN)
check(said("loaded - OFF"), "loads disarmed")

-- 1. the town table stands on the defaults before any feed: both pools present
local heat = st("heat")
check(heat:find("Lodbrok's Hold", 1, true) and heat:find("Nidaros", 1, true), "defaults: the 13 lineage cities")
check(heat:find("Paris", 1, true) and heat:find("Utrecht", 1, true), "defaults: the 24 historical towns")
check(not heat:find("no target data", 1, true), "defaults: no 'waiting on Guild.Fleet' line")

-- 2. ships off Guild.Voyage: two docked, one raiding, one voyaging; dock tier 5 -> 10
gmcp("Guild.Voyage", VOY_P1); gmcp("Guild.Voyage", VOY_P2); gmcp("Guild.City", CITY)
check(st("docked") == "2 / 10", "voyage: docked count from longship[], capacity from the dock tier (" .. st("docked") .. ")")

local function heat_row(t)
  return st("heat"):match("target " .. t:gsub("%p", "%%%0") .. "}[^\n]*")
end
local function heat_of(t)
  local r = heat_row(t); return r and tonumber(r:match("@{}%s+(%-?%d+)"))
end
-- 3. heat pairs by lineage index: Lodbrok's Hold = 80 (lin 1), Eiriksby = 8, Holmgard = 0
heat = st("heat")
check(heat_of("Lodbrok's Hold") == 80, "heat: lineage 1 wears heat[1]")
check(heat_of("Holmgard") == 0 and heat_row("Holmgard"):find("calm", 1, true), "heat: a zero-heat town is in the calm pool")
check(not heat_row("Lodbrok's Hold"):find("calm", 1, true), "heat: an 80-heat town is not")

-- 4. grudges overlay the home list by lineage number (a renamed town heals)
gmcp("Guild.Kingdom", (KING:gsub("Eiriksby", "Eiriksborg")))
heat = st("heat")
check(heat_of("Eiriksborg") == 8, "grudges: lineage 2's town renamed by the feed, heat[2] follows it")
check(not heat:find("Eiriksby", 1, true), "grudges: the default name for that lineage is gone")
check(heat_of("Imaird") == 24, "grudges: a lineage the grudges skip keeps its default town")

-- 5. the auto-raid pass dispatches docked longships at a calm home town
alias("^araid (.+)$", "auto on"); alias("^araid (.+)$", "convoy off")
alias("^araid (.+)$", "on")
sent = {}
tick(31)                       -- past the 30 s pass interval
for _, fn in pairs(repeats) do fn() end
check(#sent == 2 and sent[1]:match("^vlongship raid Svarten ") and sent[2]:match("^vlongship raid Draken "),
  "pass: both docked longships from Guild.Voyage are sent (" .. table.concat(sent, "; ") .. ")")
check(sent[1]:match("raid Svarten (%S+)$") and not sent[1]:find("lodbrok", 1, true),
  "pass: the target is a calm town, never the 80-heat one")
alias("^araid (.+)$", "off")

-- 6. foreign pool: random among the historical list
alias("^araid (.+)$", "pool foreign"); alias("^araid (.+)$", "on")
sent = {}; tick(31); for _, fn in pairs(repeats) do fn() end
local FOREIGN = { Paris=1, Lindisfarne=1, Hamwic=1, Dorestad=1, Rouen=1, Nantes=1, Quentovic=1, Repton=1, Seville=1, Hedeby=1, Jorvik=1, Dublin=1, Noirmoutier=1, Poitiers=1, Sandwich=1, Groningen=1, Antwerp=1, Kaupang=1, Ribe=1, Waterford=1, Winchester=1, Trondheim=1, Bordeaux=1, Utrecht=1 }
local town = sent[1] and sent[1]:match("raid Svarten (.+)$")
check(town and FOREIGN[town], "pass: the foreign pool picks a historical town (" .. tostring(town) .. ")")
alias("^araid (.+)$", "off"); alias("^araid (.+)$", "pool home")

-- 7. `araid targets` lists both pools and asks the MUD for the listing
printed, sent = {}, {}
alias("^araid targets$")
check(said("Home: Lodbrok's Hold, Eiriksborg") and said("Foreign: Paris, Lindisfarne"), "targets: both lists printed")
check(said("built-in default") and sent[1] == "vlongship targets", "targets: says the foreign list is the default and sends the listing command")

-- 8. the listing scan replaces both lists (a new foreign town, a renamed one, a shorter home list)
printed = {}
for l in LISTING:gmatch("[^\n]+") do line(l) end
check(said("targets read: 3 home, 3 foreign"), "scan: reads the framed listing")
heat = st("heat")
check(heat:find("New Hamwic", 1, true) and not heat:find("Utrecht", 1, true), "scan: the foreign list is the listing's, whole")
check(store["towns_foreign"] == "Paris\nLindisfarne\nNew Hamwic", "scan: the foreign list persists")
check(heat_of("Imaird") == 24 and not heat:find("Nidaros", 1, true), "scan: the home list is the listing's, still paired with heat[] by position")
check(heat_of("Eiriksborg") == 8, "scan: the grudges' name still overlays the scanned home list")
printed = {}; alias("^araid targets$")
check(said("from the last listing scan"), "targets: says the foreign list came from a scan")

-- 9. a listing cut short applies nothing
fresh_client(); dofile(PLUGIN)
line("-~*  Historical Targets (free, halved rewards)                *~-")
line("-~*  Ghent              grain        & wool                   *~-")
gmcp("Guild.City", CITY)       -- a republish: whatever the scan holds shows now
check(store["towns_foreign"] == nil and st("heat"):find("Utrecht", 1, true) and not st("heat"):find("Ghent", 1, true),
  "scan: no Runestones line, nothing applied and nothing persisted")

-- 10. the persisted lists come back on reload
fresh_client(); store["towns_foreign"] = "Ghent\nBruges"; dofile(PLUGIN)
check(st("heat"):find("Bruges", 1, true) and not st("heat"):find("Paris", 1, true), "reload: a scanned foreign list survives a restart")

-- 11. a server still sending Guild.Fleet is read as before
fresh_client(); dofile(PLUGIN)
gmcp("Guild.Fleet", '{ "pages": 1, "page": 1, "ships": [ { "state": "docked", "name": "Regal", "id": 1 } ], "guild": "viking" }')
gmcp("Guild.City", CITY)
check(st("docked") == "1 / 10", "fleet: Guild.Fleet ships[] still counts when it is what arrives")

-- 12. THE RAIDS TAB (20 Sep): Guild.Fleet's raidlog, its goods joined by idx, a total
--     line, folded by town (best paying first) and by ship; town rows target. Verbatim
--     records from the 20 Sep capture.
fresh_client(); dofile(PLUGIN)
gmcp("Guild.Fleet", '{ "full": 1, "pages": 3, "raidlog": [ { "ship": "Draken", "target": "Eiriksby", "thralls": 0, "lost": 0, "idx": 0, "daler": 849 }, { "ship": "Kronan", "target": "Lodbrok", "thralls": 0, "lost": 1, "idx": 1, "daler": 684 }, { "ship": "Svarten", "target": "Lejre", "thralls": 2, "lost": 0, "idx": 2, "daler": 659 } ], "guild": "viking", "page": 1 }')
gmcp("Guild.Fleet", '{ "full": 1, "pages": 3, "raidlog_goods": [ { "idx": 0, "amount": 116, "good": "cloth" }, { "idx": 0, "amount": 124, "good": "salted_fish" }, { "idx": 2, "amount": 93, "good": "mead" } ], "guild": "viking", "page": 2 }')
gmcp("Guild.Fleet", '{ "full": 1, "pages": 3, "ships": [ { "state": "docked", "name": "Regal", "id": 1 } ], "guild": "viking", "page": 3 }')
local tab
for _, t in ipairs(panel.tabs) do if t.title == "Raids" then tab = t end end
check(tab ~= nil, "a Raids tab")
check(st("raidsum") == "3 raid(s): 2192 daler (730 avg), 2 thrall(s), 1 ship(s) lost", "the total line (" .. st("raidsum") .. ")")
local log = st("raidlog")
check(log:find("Draken\tEiriksby\t849\t116 cloth, 124 salted fish", 1, true) ~= nil, "a log row: ship, town, daler, the goods joined by idx")
check(log:find("Kronan\tLodbrok\t684\t\n", 1, true) ~= nil, "...a raid that brought nothing lists nothing")
check(log:find("Svarten\tLejre\t659\t93 mead", 1, true) ~= nil, "...and each raid gets its own goods, by idx")
check(st("raidtowns"):match("^Eiriksby\t1\t849\t849\nLodbrok\t1\t684\t684\nLejre\t1\t659\t659") ~= nil, "by town, best paying first (" .. st("raidtowns"):gsub("\n", "|") .. ")")
check(st("raidships"):find("Draken\t1\t849\t849", 1, true) ~= nil, "by ship")
tab.widgets[4].onRowClick(nil, 3)
check(st("target") == "Lejre", "a town row click targets that town")
tab.widgets[2].onRowClick(nil, 1)
check(st("target") == "Eiriksby", "a log row click targets its town")
-- a Fleet burst without the log (a page set of ships only) leaves the tab empty-honest
fresh_client(); dofile(PLUGIN)
check(st("raidlog"):find("no raids logged", 1, true) ~= nil and st("raidsum") == "no raids logged", "no log yet says so")

-- 13. GUILD.FLEET IS BACK (23 Sep 15:34 capture): rtargets_lineage on the full burst,
--     rtargets_historical (two pages) on the delta burst. Each is kept as it comes -
--     the full burst replacing the paged keys must not forget the foreign list - and
--     the feed's list wins over a scan and the default.
fresh_client(); store["towns_foreign"] = "Ghent\nBruges"; dofile(PLUGIN)
gmcp("Guild.Fleet", '{ "pages": 2, "rtargets_historical": [ "Paris:grain:sunstone", "Lindisfarne:timber:wool", "Hamwic:furs:pork" ], "page": 1, "guild": "viking" }')
gmcp("Guild.Fleet", '{ "pages": 2, "rtargets_historical": [ "Ribe:grain:fish", "Aachen:timber:iron" ], "page": 2, "guild": "viking" }')
check(st("heat"):find("Aachen", 1, true) and st("heat"):find("Paris", 1, true) and not st("heat"):find("Bruges", 1, true),
      "fleet: rtargets_historical, both pages, is the foreign list over the scan")
gmcp("Guild.Fleet", '{ "full": 1, "pages": 2, "raidlog": [ ], "guild": "viking", "page": 1 }')
gmcp("Guild.Fleet", '{ "full": 1, "pages": 2, "rtargets_lineage": [ "Lodbrok\'s Hold:bread:fine_furs", "Eiriksby:salted_fish:cloth", "New Imaird:mead:cheese" ], "guild": "viking", "page": 2 }')
check(st("heat"):find("Aachen", 1, true) ~= nil, "fleet: a full burst without the foreign list does not forget it")
check(st("heat"):find("New Imaird", 1, true) ~= nil, "fleet: rtargets_lineage names the home towns, goods stripped")
gmcp("Guild.Kingdom", '{ "pages": 1, "page": 1, "grudges": [ { "town": "Eiriksborg", "secs": 5, "lineage": 2 } ], "guild": "viking" }')
check(st("heat"):find("Eiriksborg", 1, true) ~= nil and not st("heat"):find("Eiriksby", 1, true), "fleet: grudges still overlay the home list by lineage")
printed, sent = {}, {}
alias("^araid targets$")
check(said("Foreign: Paris, Lindisfarne, Hamwic, Ribe, Aachen") and said("foreign list from Guild.Fleet"), "targets: says the foreign list came from Guild.Fleet")

print(string.format("raid_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
