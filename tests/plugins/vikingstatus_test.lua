-- Harness for 3s-viking-status-gmcp. Feed payloads are verbatim from the 27 Aug 2026
-- capture (_lab/captures/gmcp-fields-Goran-20260827.md) - pages of real bursts,
-- renumbered into complete bursts where the capture's sample was partial (the
-- assembler only publishes complete bursts, which is the point of it).
--
-- What this file protects: the adapter->composer contract per tab (each string
-- format has its writer in the adapters and its reader in a composer; these tests
-- pin that both sides agree), the wstock feed-gap guards (warehouse note, planner
-- note, AUTO-TRADER REFUSING TO ARM), the Feeds tab's package staleness, the
-- mission runner riding the Guild.Trade missions list, and the panel vocabulary.
--
-- Equivalent mutants, documented rather than faked: (00) ws_auto_scan's
-- ws_feed_live gate - vstock_scan carries the same gate, so removing the outer
-- one changes nothing observable (each gate's own path is killed separately);
-- (000) the release path checking at_yard_provisional rather than releasing any
-- hold: a refusal always empties the pending list, so a timeout during a
-- refusal-set hold needs two dispatches in flight and self-heals (one doomed
-- probe, corrected by the next refusal) - near-equivalent, kept for the design;
-- (0) ws_finish resetting
-- ws_warned - once a scan succeeds the goods string persists for the session, so
-- the no-stock guard can never re-fire and the reset is hygiene, not behaviour;
-- (1) removing vset's no-change
-- skip only costs redundant flush scheduling - flush coalesces, so nothing
-- observable changes; (2) gasm keeping "full"/"guild" as snapshot data keys - no
-- adapter iterates snapshot keys, each reads named fields (same note as the raid
-- and fx harnesses).

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-viking-status-gmcp/main.lua"

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
local prompt_hooks, cmd_hooks, conn_hooks, disc_hooks, watch_hooks, notifies, idle_hooks

local function fresh_client()
  printed, sent = {}, {}
  gmcp_hooks, aliases, line_hooks, triggers, bus_hooks = {}, {}, {}, {}, {}
  prompt_hooks, cmd_hooks, conn_hooks, disc_hooks, watch_hooks, notifies = {}, {}, {}, {}, {}, {}
  idle_hooks = {}
  store, state, vars, panel = {}, {}, {}, nil
  timers, repeats, next_id = {}, {}, 0
  scrye = {
    id = "3s-viking-status-gmcp",
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
    onIdle       = function(fn) idle_hooks[#idle_hooks + 1] = fn end,
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
local function st(k) return tostring(state["plugin.3s-viking-status-gmcp." .. k] or "") end
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

os = os or {}
os.time = os.time or function() return 0 end

-- ---- capture payloads (verbatim; page numbering adjusted only where noted) ----
local STATE_P1 = '{ "god": { "expires_at": 1787849225, "focus": "rite", "name": "Freyja" }, "chain": { "bsdepth": 0, "chain": 0 }, "gline2": "G[...]", "bars": { "gp1_max": 7152, "gp2_max": 8082, "gp1": 5852, "gp2": 8082 }, "daler": 943976, "encounter": { "rounds": 0, "active": 0 }, "full": 1, "pages": 3, "gline1": "H[...]", "page": 1, "guild": "viking", "fx": { "stfx": "[valg:588 veth:201 gro:259]", "queue": "", "chan": "" } }'
local STATE_P2 = '{ "ledung": { "max": 4, "reset_pct": 0, "charges": 4 }, "missions_reg": 5, "gxp": { "vitka": 68498 }, "hp": { "mthrek": 9499, "cur": 6312, "threk": 0, "delta": 0, "max": 6333 }, "full": 1, "pages": 3, "missions_newbie": 3, "guild": "viking", "page": 2 }'
local STATE_P3 = '{ "points": { "mvitka": 7152, "mviga": 8082, "mfury": 333, "vitka": 5852, "mdrotta": 6964, "mbuandi": 6963, "fury": 0, "buandi": 714, "drotta": 6964, "viga": 8082 }, "full": 1, "sp": { "cur": 4885, "max": 53 }, "target": { "hp_status": "", "name5": "None", "name": "None" }, "pages": 3, "tox": { "cur": 0, "max": 18 }, "page": 3, "guild": "viking" }'
local INFO_FULL = '{ "rank": 3, "age": 4710328, "rank_name": "Vikingr Fraegr", "title": "hinn Vikingr Fraegr af Sigurdsson, fylgjandi Tyrs", "subguild": "Berserkr", "disabled": 0, "gp2_name": "Vig", "renown": 1792, "gp1_name": "Seid", "full": 1, "glvl": 196, "guild": "viking", "lineage": "Sigurdsson" }'
local CITY_UNPAGED = '{ "blot": { "total": 9, "filled": 9, "state": "rest", "reset_in": 5040 }, "heat": [ 0, 0, 59, 0, 0, 0, 27, 0, 19, 0, 62, 29, 26 ], "patrol": { "remaining": 10382, "count": 15 }, "nexttick": 10197, "guild": "viking", "dcycle": { "secs": 276188, "name": "Calm Season" } }'
-- buildings pages (verbatim page 2 of the capture's full:1 pages:8 City burst, renumbered 1/2)
local CITY_P1 = '{ "buildings": [ { "tier": 4, "id": "mine" }, { "tier": 3, "id": "hiring_hall" }, { "tier": 5, "id": "well" }, { "tier": 5, "id": "farm" }, { "tier": 4, "id": "mead_cellar" }, { "tier": 5, "id": "lumber_yard" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }'
local CITY_P2 = '{ "buildings": [ { "tier": 2, "id": "shadow_house" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" }, { "tier": 4, "id": "smithy" } ], "full": 1, "cdtime": 0, "pages": 2, "cityplan": { "dim": 12, "coast_side": 1, "placed": 35, "gate": 6, "cap": 56, "mood_delta": 0, "moat": 1, "wall": 1, "margin": 4, "enabled": 1 }, "guild": "viking", "page": 2, "builds": [ { "total": 16761, "mats": 130, "secs": 1415, "detail": "sunstone:9\\/9,iron:40\\/40,timber:81\\/81", "done": 130, "tier": 3, "id": "armoury" }, { "total": 16761, "mats": 130, "secs": 1385, "detail": "sunstone:9\\/9,iron:40\\/40,timber:81\\/81", "done": 130, "tier": 3, "id": "weaponry" } ] }'
local SETTLE_P1 = '{ "sconsume": { "fish": 116, "spoils": 12, "food": 233, "livestock_grain": 8, "eggs": 0, "mead": 24, "water": 77, "grain": 117 }, "full": 1, "pages": 2, "page": 1, "settlers": { "water": 400, "tax_rate": 3, "mood": 100, "fert": 0, "settlers": 660 }, "guild": "viking" }'
local SETTLE_P2 = '{ "settlerx": { "housing_upkeep": 332, "net": 3207, "dignity": 100, "sentiment": 17, "tax_income": 7920, "comm_upkeep": 742, "employed": 613, "housing_cap": 660, "sustenance": 100, "jobs": 613, "mult_pct": 120 }, "full": 1, "pages": 2, "scivics": [ { "count": 3, "id": "shipwrights_yard" }, { "count": 4, "id": "law_stone" }, { "count": 4, "id": "granary_mill" } ], "guild": "viking", "page": 2 }'
local TRADE_P1 = '{ "carts": [ { "durability": 95, "escort": 5, "horses": 6, "amount": 228, "half_in": 0, "cart_id": 2, "cap": 313, "tier": 5, "good": "bread", "mode": "sell", "quality_pct": 100, "secs": 1320, "refit": "speed", "village": "Vestergotland" }, { "durability": 100, "escort": 5, "horses": 6, "amount": 193, "half_in": 0, "cart_id": 3, "cap": 313, "tier": 5, "good": "cloth", "mode": "sell", "quality_pct": 116, "secs": 4372, "refit": "speed", "village": "Nidaros" } ], "full": 1, "pages": 3, "guild": "viking", "page": 1 }'
local TRADE_P2 = '{ "full": 1, "pages": 3, "guild": "viking", "missions": [ { "goods": "furs:9,sunstone:4,grain:9", "reward": 865, "town": "Birka", "origin": "", "secs": 86352, "rep": 35, "label": "Olof the Scribe bids you bring 9 furs, 9 grain and 4 sunstone.", "id": 52782 } ], "refinery": [ { "bldg": "salting_house", "stock": 250, "cap": 250, "tier": 3 }, { "bldg": "bakehouse", "stock": 250, "cap": 250, "tier": 3 } ], "page": 2 }'
local TRADE_P3 = '{ "wstock_cap": 8369, "full": 1, "pages": 3, "guild": "viking", "page": 3, "refinery_grades": [ { "bldg": "salting_house", "pct": 90, "qty": 23, "grade": "cured" }, { "bldg": "salting_house", "pct": 100, "qty": 227, "grade": "hard-cured" }, { "bldg": "bakehouse", "pct": 45, "qty": 248, "grade": "proofed" } ] }'
local FLEET_SHIPS = '{ "pages": 1, "ships": [ { "convoy_bonus": 0, "durability": 100, "target": "Nidaros", "state": "raiding", "tier": 5, "convoy_size": 0, "secs": 13264, "saga_title": "the Undying", "convoy": 0, "held": 0, "saga_raids": 159, "crew": 60, "id": 16, "name": "Kronan" }, { "convoy_bonus": 0, "durability": 100, "target": "", "state": "docked", "tier": 5, "convoy_size": 0, "secs": 0, "saga_title": "the Iron-Prowed", "convoy": 0, "held": 0, "saga_raids": 61, "crew": 60, "id": 5, "name": "Vasa" } ], "page": 1, "guild": "viking" }'
local ROSTER_P1 = '{ "full": 1, "pages": 3, "bonds": [ { "a": 1, "tier": 3, "ticks": 1312394, "b": 6 }, { "a": 4, "tier": 3, "ticks": 1584438, "b": 5 } ], "guild": "viking", "page": 1 }'
local ROSTER_P2 = '{ "full": 1, "pages": 3, "courier_tier": 1, "gneeds": { "on_duty": 0, "hird_cap": 18, "city_pool": 6, "stationed": 41, "hird_count": 6, "garrisoned": 0, "wounded": 0, "garrison_cap": 41 }, "page": 2, "courier": [ ], "guild": "viking" }'
local ROSTER_P3 = '{ "full": 1, "pages": 3, "hird": [ { "level": 50, "age": "veteran", "atk": 8, "status": "city_pool", "champ": 0, "hired": 1781676778, "mode": "neutral", "wpn": 0, "def": 10, "arm": 0, "loyalty": 3, "name": "Thyra Ivarardottir", "id": 5 }, { "level": 50, "age": "veteran", "atk": 10, "status": "city_pool", "champ": 0, "hired": 1782582760, "mode": "neutral", "wpn": 0, "def": 10, "arm": 0, "loyalty": 4, "name": "Roar Svensson", "id": 6 } ], "thralls": { "mine": 4, "total": 30, "farm": 5 }, "guild": "viking", "page": 3 }'
local KINGDOM_P1 = '{ "pages": 2, "vrep": [ { "rank": 4, "lin": 0, "rep": 5305, "next_at": 10000, "start_at": 5000, "name": "Midgard" }, { "rank": 4, "lin": 1, "rep": 6614, "next_at": 10000, "start_at": 5000, "name": "Lodbrok" } ], "page": 1, "guild": "viking" }'
local KINGDOM_P2 = '{ "pages": 2, "standings": [ { "lin": 1, "score": 53, "own": 0, "label": "Allied", "name": "Lodbrok" }, { "lin": 3, "score": -100, "own": 0, "label": "Feud", "name": "Ui Imair" } ], "page": 2, "guild": "viking" }'

local function feed_all()
  gmcp("Guild.State", STATE_P1); gmcp("Guild.State", STATE_P2); gmcp("Guild.State", STATE_P3)
  gmcp("Guild.Info", INFO_FULL)
  gmcp("Guild.City", CITY_UNPAGED); gmcp("Guild.City", CITY_P1); gmcp("Guild.City", CITY_P2)
  gmcp("Guild.Settlement", SETTLE_P1); gmcp("Guild.Settlement", SETTLE_P2)
  gmcp("Guild.Trade", TRADE_P1); gmcp("Guild.Trade", TRADE_P2); gmcp("Guild.Trade", TRADE_P3)
  gmcp("Guild.Fleet", FLEET_SHIPS)
  gmcp("Guild.Roster", ROSTER_P1); gmcp("Guild.Roster", ROSTER_P2); gmcp("Guild.Roster", ROSTER_P3)
  gmcp("Guild.Kingdom", KINGDOM_P1); gmcp("Guild.Kingdom", KINGDOM_P2)
  fire_timers()
end

-- =====================================================================
fresh_client(); dofile(PLUGIN)
fire_timers()

-- honest before the feed
check(st("feeds"):find("no Guild.* burst yet", 1, true), "Feeds tab says it is waiting")

feed_all()

-- Stats tab
local stats = st("stats")
check(stats:find("God Freyja > rite", 1, true), "stats: god + focus (no bogus countdown)")
check(stats:find("Daler 943976", 1, true), "stats: daler from Guild.State")
check(stats:find("Sigurdsson", 1, true) and stats:find("GLvl 196", 1, true), "stats: lineage + glvl from Guild.Info")
check(stats:find("Rank Vikingr Fraegr", 1, true) and stats:find("Renown 1792", 1, true), "stats: rank + renown")
check(stats:find("Missions: new 3 reg 5", 1, true), "stats: mission counts")
check(stats:find("Cycle Calm Season", 1, true) and stats:find("76h left", 1, true), "stats: day cycle + hours")
check(stats:find("Threk 0 / 9499", 1, true), "stats: threk pool")
check(stats:find("Ledung 4 / 4 uses", 1, true), "stats: ledung charges")
check(stats:find("Hirdmadrs 15", 1, true) and stats:find("173m left", 1, true), "stats: patrol count + minutes")
check(stats:find("Blot 9/9 rest, reset 84m", 1, true), "stats: blot line")

-- the Vitals line: pools decoded from Guild.State points, the mapping gline1
-- proves (S=vitka/Seid, V=viga/Vigor, R=drotta/Rad, H=hp) - values must land on
-- the right labels or the whole line is misinformation
check(stats:find("HP", 1, true) and stats:find("6312/6333", 1, true), "vitals: HP from the hp block")
check(stats:match("Seid @{[^}]+}5852/7152") ~= nil, "vitals: Seid = points.vitka/mvitka")
check(stats:match("Vig @{[^}]+}8082/8082") ~= nil, "vitals: Vig = points.viga/mviga")
check(stats:match("Rad @{[^}]+}6964/6964") ~= nil, "vitals: Rad = points.drotta/mdrotta")

-- City tab
local city = st("city")
check(city:find("Kronan", 1, true) and city:find("Nidaros", 1, true) and city:find("221m", 1, true),
  "city: raiding ship with destination + eta")
check(city:find("Vasa", 1, true), "city: docked ship listed")
check(city:find("sell", 1, true) and city:find("bread", 1, true) and city:find("Vestergotland", 1, true)
  and city:find("22m", 1, true) and city:find("x228", 1, true), "city: cart row (mode good town eta amount)")
-- refinery barlist
local refy = st("refinery")
check(refy:find("Salting House\tT3 250/250", 1, true), "refinery: name + tier + fill")
check(refy:find("23,90", 1, true) and refy:find("227,100", 1, true), "refinery: stages carry qty,pct")
-- 17 Sep 2026: the server moved the refinery off Guild.Trade into its own Guild.Refinery
-- (3 pages); Guild.Trade is carts only. The same adapter reads both.
gmcp("Guild.Refinery", '{ "pages": 3, "page": 1, "guild": "viking", "refinery": [ { "stock": 120, "bldg": "smokehouse", "cap": 300, "tier": 4 } ] }')
gmcp("Guild.Refinery", '{ "pages": 3, "page": 2, "guild": "viking", "refinery": [ { "stock": 0, "bldg": "bakehouse", "cap": 250, "tier": 3 } ] }')
gmcp("Guild.Refinery", '{ "pages": 3, "page": 3, "guild": "viking", "refinery_grades": [ { "bldg": "smokehouse", "pct": 60, "qty": 120, "grade": "smoked" } ] }')
fire_timers()
refy = st("refinery")
check(refy:find("Smokehouse\tT4 120/300", 1, true) ~= nil and refy:find("120,60", 1, true) ~= nil,
      "refinery: Guild.Refinery feeds the same bars - buildings across pages, grades on the last")
check(refy:find("Bakehouse\tT3 0/250", 1, true) ~= nil, "refinery: ...an empty building is still a bar")
check(refy:find("Salting House", 1, true) == nil, "refinery: ...and the old Guild.Trade copy is replaced, not merged")

-- Builds tab (planner): daler affordable, resources unknown -> gap note
local builds = st("builds")
check(builds:find("Daler 943,976", 1, true), "builds: daler in header")
check(builds:find("no stock data", 1, true), "builds: wstock gap note shown")
check(builds:find("constr: armoury", 1, true) and builds:find("23m left", 1, true), "builds: construction row from Guild.City")

-- Production tab: warehouse gap note, no fake emptiness
local prod = st("production")
check(prod:find("no stock data", 1, true), "production: warehouse gap note")
check(prod:find("Warehouse  0 / 8369", 1, true), "production: effective capacity from wstock_cap")

-- People tab
local people = st("people")
check(people:find("Thralls 30", 1, true), "people: thrall total")
check(people:find("Garrison 0/41", 1, true), "people: garrison from gneeds")
check(people:find("Hird Guard (6/18)", 1, true), "people: hird count/cap header")
check(people:find("Thyra Ivarardottir", 1, true) and people:find("8/10/3/50", 1, true),
  "people: hird row atk/def/loyalty/level")
check(people:find("Roar", 1, true), "people: bond matrix names resolve via hird ids")

-- Settlers tab
local settlers = st("settlers")
check(settlers:find("Population 660", 1, true), "settlers: population")
check(settlers:find("100/100", 1, true), "settlers: mood")
check(settlers:find("Sentiment", 1, true) and settlers:find("17", 1, true), "settlers: sentiment")
check(settlers:find("Income  +7920 d/tick", 1, true), "settlers: tax income")
check(settlers:find("Upkeep  1074 d/tick", 1, true) and settlers:find("Net +3207", 1, true),
  "settlers: upkeep = comm+housing, net from the feed")
check(settlers:find("Law Stone T4", 1, true), "settlers: civics with tiers")
check(settlers:find("fish 116", 1, true), "settlers: consumption")

-- Holds tab
local holds = st("holds")
check(holds:find("Score", 1, true), "holds: header says Score (not the MIP Bond)")
check(holds:find("Midgard", 1, true) and holds:find("5305", 1, true), "holds: vrep row")
check(holds:find("Imaird", 1, true) and holds:find("Feud", 1, true), "holds: standing word from Guild.Kingdom")
check(holds:find("Blot 9/9 rest", 1, true), "holds: blot line")

-- the missions the moved runner reads: this plugin still publishes them raw
check(st("missions_raw"):find("52782", 1, true) and st("missions_raw"):find("Birka", 1, true),
  "missions are published raw for 3s-viking-world's runner to read")

-- Feeds tab
local feeds = st("feeds")
check(feeds:find("Guild.Trade", 1, true) and feeds:find("bursts", 1, true), "feeds: package rows")
check(feeds:find("daler", 1, true), "feeds: translated keys listed")

-- =====================================================================
-- auto-trader with no stock data: kicks a quiet scan and WAITS armed
printed = {}
sent = {}
alias("^atrade (.+)$", "on")
fire_timers()
check(said("auto-trade waiting: no warehouse stock yet"), "no stock: the trader says it is waiting")
check(st("atstatus"):find("Auto-trade: @{success}ON@{}", 1, true), "no stock: the trader STAYS armed")
local sent_scan = false
for _, c in ipairs(sent) do if c == "vtrade stock" then sent_scan = true end end
check(sent_scan, "no stock: a quiet vtrade stock goes out")

-- said once, and the scan attempt is throttled: another pass inside 15 s neither
-- repeats the warning nor sends a second scan
printed = {}
sent = {}
gmcp("Guild.Trade", '{ "wstock_cap": 8369, "pages": 1, "page": 1, "guild": "viking" }')
fire_timers()
check(not said("auto-trade waiting"), "the waiting note is said once")
local resent = false
for _, c in ipairs(sent) do if c == "vtrade stock" then resent = true end end
check(not resent, "auto scans are throttled (one attempt per 15 s)")
-- a real second pass (re-arming re-runs the tick) must not repeat the note either
alias("^atrade (.+)$", "on"); fire_timers()
local warns = 0
for _, l in ipairs(printed) do if l:find("auto-trade waiting", 1, true) then warns = warns + 1 end end
check(warns == 0, "a second guard pass does not repeat the waiting note")
alias("^atrade (.+)$", "off"); fire_timers()

-- keep-last across bursts: a State refresh without daler keeps daler
gmcp("Guild.State", '{ "guild": "viking", "fx": { "stfx": "[gro:100]", "queue": "", "chan": "" } }')
fire_timers()
check(st("stats"):find("Daler 943976", 1, true), "unpaged fx refresh keeps daler")

-- rndz fight-length tracking rides the encounter block
gmcp("Guild.State", '{ "guild": "viking", "encounter": { "rounds": 12, "active": 1 } }')
gmcp("Guild.State", '{ "guild": "viking", "encounter": { "rounds": 0, "active": 0 } }')
fire_timers()
check(st("stats"):find("Last fight 12", 1, true), "encounter.rounds drop records the fight length")



-- =====================================================================
-- the vtrade stock scan - the FALLBACK path, not a stopgap: Guild.Warehouse has
-- carried per-good stock since 28 Aug pm and owns it wherever both exist. This
-- parser covers the case where the package has not arrived, and a hand-typed
-- `vtrade stock`. (This header said "STOPGAP until GMCP carries per-good stock"
-- for days after that stopped being true, and was believed over the code.)
--
-- The report below is VERBATIM from Joakim's game session of 28 Aug 2026 - do
-- not hand-edit it to suit a test. Nine phases share it and several assert on
-- sums drawn from it; a phase that needs particular stock should send its own
-- Guild.Warehouse burst, the way the ore reserve phase at the end does.
local STOCK_REPORT = [==[
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*   Warehouse Stock Tier 5   6138/8369 stored           *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*  Timber           271 units                           *~-
-~*  Iron             256 units total                     *~-
-~*                   256 units  seax-grade               *~-
-~*  Furs             200 units (cap 200)                 *~-
-~*  Fish             300 units total (cap 300)           *~-
-~*                    99 units  fresh                    *~-
-~*                   201 units  slightly stale           *~-
-~*  Grain            270 units total                     *~-
-~*                   270 units  fresh                    *~-
-~*  Mead             184 units total                     *~-
-~*                   184 units  aged                     *~-
-~*  Sunstone         204 units                           *~-
-~*  Runestones        24 units                           *~-
-~*  Spoils          1221 units                           *~-
-~*  Salted Fish      300 units total (cap 300)           *~-
-~*                   300 units  hard-cured               *~-
-~*  Bread            466 units total                     *~-
-~*                   466 units  hardtack                 *~-
-~*  Fine Furs        200 units total (cap 200)           *~-
-~*                   200 units  trimmed                  *~-
-~*  Tools            301 units total                     *~-
-~*                   301 units  masterwork               *~-
-~*  Gemstones         32 units                           *~-
-~*  Finery            52 units total                     *~-
-~*                    52 units  regalia                  *~-
-~*  Eggs             300 units total (cap 300)           *~-
-~*                    27 units  slightly stale           *~-
-~*                    41 units  stale                    *~-
-~*                    40 units  old                      *~-
-~*                   192 units  very old                 *~-
-~*  Milk              16 units total                     *~-
-~*                    12 units  fresh                    *~-
-~*                     4 units  slightly stale           *~-
-~*  Cloth            520 units total                     *~-
-~*                   520 units  broadcloth               *~-
-~*  Smoked Meat      618 units total                     *~-
-~*                   618 units  hard-cured               *~-
-~*  Cheese           400 units total (cap 400)           *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*  (next shift: 1h 20m)                                 *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*  Daler: 1,082,927                                     *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
-~*  'vtrade stock unblocked' show stock with blocked     *~-
-~*  subtracted                                           *~-
-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-
]==]

-- deliver the report the way the client does: every ^-~\* trigger fires with the
-- captured remainder, and every onLine hook sees the raw line
local function feed_stock_report()
  for line in STOCK_REPORT:gmatch("[^\n]+") do
    local cap = line:match("^%-~%*(.*)$")
    if cap then
      for _, t in ipairs(triggers) do
        if t.pattern == [[^-~\*(.*)]] then t.run(cap) end
      end
    end
    for _, h in ipairs(line_hooks) do h(line) end
  end
end

-- the Production tab's button sends the command and gags the report
sent = {}
local prod_btn
for _, tab in ipairs(panel.tabs) do
  if tab.title == "Production" then
    for _, w in ipairs(tab.widgets) do
      if w.type == "button" then prod_btn = w end
    end
  end
end
check(prod_btn and prod_btn.text:find("Refresh stock", 1, true), "production: the Refresh stock button exists")
prod_btn.action()
check(sent[1] == "vtrade stock", "the button sends vtrade stock")
-- gagged while the button scan is in flight...
local gagged = false
for _, h in ipairs(line_hooks) do
  if h("-~*  Timber           271 units                           *~-") == false then gagged = true end
end
check(gagged, "framed report lines are gagged during a button scan")

feed_stock_report()

-- ...and the hint FOOTER closes the gag window - checked before any timer fires,
-- so the after(8) safety close cannot mask a footer that never closed it
local still_gagged = false
for _, h in ipairs(line_hooks) do
  if h("-~*  some later framed line  *~-") == false then still_gagged = true end
end
check(not still_gagged, "the hint footer closes the gag window (not the safety timer)")
fire_timers()

-- the warehouse fills: summed per good, capacity from the report header.
-- (the report header says 6138 stored; the listed goods sum to 6135 - the game's
-- own 3-unit discrepancy, shown as computed so the numbers always match the rows)
local prod2 = st("production")
check(prod2:find("Warehouse  6135 / 8369", 1, true), "production: warehouse total/capacity from the scan")
check(not prod2:find("no stock data", 1, true), "production: gap note gone after the scan")
check(prod2:find("timber", 1, true) and prod2:find("271", 1, true), "production: single-grade good row")
check(prod2:find("192 very old", 1, true) and prod2:find("27 slightly stale", 1, true),
  "production: egg grades listed by name")
check(prod2:find("@{warning}192 very old@{}", 1, true), "production: stale-ish grades wear warning")
check(prod2:find("@{dim}256 seax-grade@{}", 1, true), "production: fine grades stay dim")
check(prod2:find("1221", 1, true), "production: spoils amount")

-- the build planner can finally judge resources: smithy T4>5 needs 460,000d +
-- 140 iron + 240 timber - all covered by the scan + State daler
alias("^build refresh$"); fire_timers()
local builds3 = st("builds")
check(not builds3:find("no stock data", 1, true), "builds: gap note gone after the scan")
check(builds3:find("OK", 1, true) and builds3:find("Smithy", 1, true), "builds: an affordable building shows OK")

-- the auto-trader arms on scanned stock... and treats the scan as FRESH:
-- no re-scan goes out (a scan that never marks itself fresh would loop on vtrade stock)
printed = {}
sent = {}
alias("^atrade (.+)$", "on"); fire_timers()
check(not said("auto-trade idle"), "auto-trader: no refusal with scanned stock")
check(st("atstatus"):find("Auto-trade: @{success}ON@{}", 1, true), "auto-trader: armed")
local rescan_fresh = false
for _, c in ipairs(sent) do if c == "vtrade stock" then rescan_fresh = true end end
check(not rescan_fresh, "auto-trader: a fresh scan is not re-scanned")
alias("^atrade (.+)$", "off"); fire_timers()

-- ...but not on a STALE scan: past 90 s it re-scans quietly instead of dispatching
tick(100)
sent = {}
alias("^atrade (.+)$", "on"); fire_timers()
local rescanned = false
for _, c in ipairs(sent) do if c == "vtrade stock" then rescanned = true end end
check(rescanned, "auto-trader: stale scan triggers a quiet re-scan, not a dispatch")
alias("^atrade (.+)$", "off"); fire_timers()

-- a later Guild.Trade burst updates the capacity but keeps the scanned goods
gmcp("Guild.Trade", '{ "wstock_cap": 9000, "pages": 1, "page": 1, "guild": "viking" }')
fire_timers()
local prod3 = st("production")
check(prod3:find("/ 9000", 1, true) and prod3:find("192 very old", 1, true),
  "a fresh wstock_cap updates capacity without losing the scanned goods")

-- typing `vtrade stock` yourself (no gag window) still refreshes the panel
gmcp("Guild.Trade", '{ "wstock_cap": 8369, "pages": 1, "page": 1, "guild": "viking" }')
fire_timers()
local before = st("production")
feed_stock_report()          -- as if the report scrolled by un-gagged
fire_timers()
check(st("production"):find("Warehouse  6135 / 8369", 1, true),
  "a hand-typed vtrade stock report is parsed passively")


-- Honey is a tradeable raw good: the market refresh scans it, and LAST - the
-- early-finish marker follows the final RES entry, so honey must close the scan
sent = {}
alias("^mkref$")
fire_timers()
local goods_cmds = {}
for _, c in ipairs(sent) do
  local g = c:match("^vtrade goods (%S+)$")
  if g then goods_cmds[#goods_cmds + 1] = g end
end
local has_honey = false
for _, g in ipairs(goods_cmds) do if g == "honey" then has_honey = true end end
check(has_honey, "mkref scans honey")
check(goods_cmds[#goods_cmds] == "honey", "honey is scanned last (the early-finish marker)")


-- Honey is RAW: the auto-buyer restocks it. End to end: scan the market for
-- honey (text lines through the same parser the game feeds), re-scan the stock
-- (no honey in the warehouse), give the city a trading post, arm the restocker -
-- the one dispatch that goes out is a BUY of honey.
alias("^mkref$")           -- opens the scan window
for _, l in ipairs({
  "-~*   Honey - Market Overview                       *~-",
  "-~*  Lodbrok's Hol    10  500 avail export++ Vinur  *~-",
  "-~*  Nidaros          25   80 wants import Vinur    *~-",
}) do
  for _, h in ipairs(line_hooks) do h(l) end
end
fire_timers()              -- settle + finalize the market scan
feed_stock_report()        -- fresh stock (honey absent -> have 0 < Raw> buffer)
fire_timers()
-- (a buildings burst carries the WHOLE list - the earlier buildings ride along)
gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 4, "id": "mine" }, { "tier": 3, "id": "hiring_hall" }, { "tier": 5, "id": "well" }, { "tier": 5, "id": "farm" }, { "tier": 4, "id": "mead_cellar" }, { "tier": 5, "id": "lumber_yard" }, { "tier": 2, "id": "shadow_house" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" }, { "tier": 4, "id": "smithy" } ], "guild": "viking" }')
fire_timers()
sent = {}
alias("^atrade (.+)$", "restock on")
alias("^atrade (.+)$", "on")
fire_timers()
local honey_buy = false
for _, c in ipairs(sent) do
  if c:match("^vtrade dispatch buy %d+ honey lodbrok") then honey_buy = true end
end
check(honey_buy, "restock: the auto-buyer sends a honey buy cart (honey is RAWBUILD)")
alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "restock off"); fire_timers()



-- 28 Aug capture: Guild.Trade now ships town ROUTES and real-shaped CIDLE
gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "routes": [ { "fort_maint": 100, "road_maint": 100, "fort_name": "No Fort", "road_name": "Paved Road", "road_tier": 2, "fort_tier": 0, "name": "Eiriksby", "village": "2" }, { "fort_maint": 100, "road_maint": 100, "fort_name": "Wood Fort", "road_name": "No Road", "road_tier": 0, "fort_tier": 1, "name": "Uppsala", "village": "6" } ], "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 310, "refit": "speed", "tier": 5 }, { "durability": 95, "horses": 6, "slot": 2, "cap": 313, "refit": "speed", "tier": 5 } ], "guild": "viking" }')
fire_timers()
local prodr = st("production")
check(not prodr:find("-- Routes --@{}\nnone", 1, true), "routes: section no longer empty")
check(prodr:match("Eiriksby%s+@{[^}]+}%[road%]") ~= nil,
  "routes: paved road + no fort renders [road] ON Eiriksby's row")
check(prodr:match("Uppsala%s+@{[^}]+}%[fort%]") ~= nil,
  "routes: no road + wood fort renders [fort] ON Uppsala's row")
-- cidle contract pin: at_capacity reads field 4 as cap - via the Feeds key dump
check(st("feeds"):find("1|5|speed|310", 1, true), "cidle: slot|tier|refit|CAP field order")


-- =====================================================================
-- the cart-event refresh: a cart leaving or docking re-scans the stock
-- (this is what keeps the auto-trader honest between staleness checks)
tick(20)                     -- clear the auto-scan throttle window
sent = {}
-- baseline cart set is ids 2,3 (from TRADE_P1 earlier); a secs-only refresh of
-- the SAME carts must NOT scan - the id set is the trigger, not the string
gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "durability": 95, "escort": 5, "horses": 6, "amount": 228, "half_in": 0, "cart_id": 2, "cap": 313, "tier": 5, "good": "bread", "mode": "sell", "quality_pct": 100, "secs": 900, "refit": "speed", "village": "Vestergotland" }, { "durability": 100, "escort": 5, "horses": 6, "amount": 193, "half_in": 0, "cart_id": 3, "cap": 313, "tier": 5, "good": "cloth", "mode": "sell", "quality_pct": 116, "secs": 4000, "refit": "speed", "village": "Nidaros" } ], "guild": "viking" }')
fire_timers()
local scanned = false
for _, c in ipairs(sent) do if c == "vtrade stock" then scanned = true end end
check(not scanned, "cart refresh with the same ids (secs ticking) does not re-scan")

-- cart 3 comes HOME (drops out of the list) -> the warehouse changed -> scan
sent = {}
gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "durability": 95, "escort": 5, "horses": 6, "amount": 228, "half_in": 0, "cart_id": 2, "cap": 313, "tier": 5, "good": "bread", "mode": "sell", "quality_pct": 100, "secs": 880, "refit": "speed", "village": "Vestergotland" } ], "guild": "viking" }')
fire_timers()
scanned = false
for _, c in ipairs(sent) do if c == "vtrade stock" then scanned = true end end
check(scanned, "a cart coming home triggers a stock re-scan")

-- a cart LEAVING (new id appears) also scans - but not within the throttle window
sent = {}
gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "durability": 95, "escort": 5, "horses": 6, "amount": 228, "half_in": 0, "cart_id": 2, "cap": 313, "tier": 5, "good": "bread", "mode": "sell", "quality_pct": 100, "secs": 860, "refit": "speed", "village": "Vestergotland" }, { "durability": 100, "escort": 5, "horses": 4, "amount": 200, "half_in": 0, "cart_id": 5, "cap": 308, "tier": 5, "good": "mead", "mode": "sell", "quality_pct": 90, "secs": 3000, "refit": "speed", "village": "Lejre" } ], "guild": "viking" }')
fire_timers()
scanned = false
for _, c in ipairs(sent) do if c == "vtrade stock" then scanned = true end end
check(not scanned, "a departure inside the 15 s throttle waits its turn")
tick(20)
sent = {}
gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "durability": 95, "escort": 5, "horses": 6, "amount": 228, "half_in": 0, "cart_id": 2, "cap": 313, "tier": 5, "good": "bread", "mode": "sell", "quality_pct": 100, "secs": 840, "refit": "speed", "village": "Vestergotland" }, { "durability": 100, "escort": 5, "horses": 4, "amount": 200, "half_in": 0, "cart_id": 5, "cap": 308, "tier": 5, "good": "mead", "mode": "sell", "quality_pct": 90, "secs": 2980, "refit": "speed", "village": "Lejre" }, { "durability": 100, "escort": 5, "horses": 4, "amount": 150, "half_in": 0, "cart_id": 7, "cap": 308, "tier": 5, "good": "furs", "mode": "sell", "quality_pct": 90, "secs": 5000, "refit": "speed", "village": "Birka" } ], "guild": "viking" }')
fire_timers()
scanned = false
for _, c in ipairs(sent) do if c == "vtrade stock" then scanned = true end end
check(scanned, "a cart leaving (new id) triggers a stock re-scan once the throttle clears")

-- the auto-scan GATE: a session where nobody scanned and the trader is off
-- must never send vtrade stock on cart traffic
fresh_client(); dofile(PLUGIN); fire_timers()
sent = {}
gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "cart_id": 2, "mode": "sell", "good": "bread", "village": "Lejre", "secs": 100, "cap": 313 } ], "guild": "viking" }')
tick(20)
gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ ], "guild": "viking" }')
fire_timers()
local idle_scan = false
for _, c in ipairs(sent) do if c == "vtrade stock" then idle_scan = true end end
check(not idle_scan, "no scan this session + trader off = cart traffic never auto-scans")

-- ...and after a scan, the FIRST carts burst is a baseline, not an event
-- (a genuinely fresh client: the previous part already baselined this one)
fresh_client(); dofile(PLUGIN); fire_timers()
feed_stock_report()
tick(20)
sent = {}
gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "cart_id": 9, "mode": "sell", "good": "mead", "village": "Birka", "secs": 500, "cap": 300 } ], "guild": "viking" }')
fire_timers()
idle_scan = false
for _, c in ipairs(sent) do if c == "vtrade stock" then idle_scan = true end end
check(not idle_scan, "the first carts burst only baselines the id set")
fresh_client(); dofile(PLUGIN); fire_timers()
feed_all()
feed_stock_report()
alias("^atrade (.+)$", "off")   -- redraws the Auto tab from the fresh feed
fire_timers()

-- =====================================================================
-- contract pins (each kills a specific adapter mutant)

-- Builds planner sees tiers from BOTH buildings pages (concat, not overwrite)
local builds1 = st("builds")
check(builds1:find("Mine", 1, true) and builds1:match("Mine%s*@{[^}]+}T4>5"),
  "builds: Mine T4>5 - page-1 buildings survive the page-2 merge")
-- ...including page 1's LAST entry (a clobbered concat loses exactly that one)
alias("^build all$")
fire_timers()
local builds2 = st("builds")
check(builds2:match("Lumber Yard%s*@{[^}]+}T5"),
  "builds: Lumber Yard (last of page 1) still T5 with maxed shown")
alias("^build all$")
fire_timers()

-- a FULL City burst without builds drops the construction rows (full replaces)
gmcp("Guild.City", '{ "full": 1, "pages": 1, "page": 1, "buildings": [ { "tier": 4, "id": "mine" }, { "tier": 5, "id": "lumber_yard" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" }, { "tier": 4, "id": "smithy" } ], "guild": "viking" }')
fire_timers()
check(not st("builds"):find("constr: armoury", 1, true),
  "builds: a full City burst without builds clears the construction rows")

-- cart row order is mode THEN good (at_cart_sigs and the City tab both key on it)
check(city:find("sell bread", 1, true), "city: cart row starts mode good, in that order")

-- the ships contract (f3 = state) is pinned via the Feeds tab key dump:
-- this plugin never reads f3, but 3s-viking-world will - the string IS the contract
check(st("feeds"):find("Kronan|5|raiding|Nidaros", 1, true),
  "feeds: ships string carries name|tier|state|target")

-- the auto-trader reads the EFFECTIVE warehouse capacity from wstock's lead field
check(st("atstatus"):find("8,369", 1, true),
  "trade auto: capacity is the feed's 8,369, not the tier table")

-- =====================================================================
-- panel: land tabs only, every widget a type the host knows
local VALID = { value = true, label = true, text = true, buttonrow = true, input = true,
                button = true, colorgrid = true, barlist = true, row = true, table = true }
local titles, widgets_ok = {}, true
for _, tab in ipairs(panel and panel.tabs or {}) do
  titles[#titles + 1] = tab.title
  for _, w in ipairs(tab.widgets or {}) do
    if not VALID[w.type] then widgets_ok = false; print("bad widget: " .. tostring(w.type)) end
  end
end
local tset = {}
for _, t in ipairs(titles) do tset[t] = true end
check(widgets_ok, "panel: every widget a known type")
-- the plan itself is still computed HERE (Guild.City feeds it, `vplan` edits it);
-- only the picture moved, so the state the moved tab binds to must still be set
check(st("plangrid") ~= "" or st("planlist") ~= "",
  "the plan is still computed here for 3s-viking-world's tab to show")
check(tset["Stats"] and tset["Trade Auto"] and tset["Feeds"], "panel: settlement tabs present")
check(not tset["Plan"], "panel: the Plan TAB moved to 3s-viking-world with the other grids")
check(not (tset["Sea"] or tset["Voyage"] or tset["Map"] or tset["Travel"]),
  "panel: sea half is gone (lives in 3s-viking-world)")

-- =====================================================================
-- GUILD.WAREHOUSE (server-side 28 Aug pm): the per-good stock feed the scan was
-- standing in for. Pages verbatim from the 17:25 capture (rows trimmed, values
-- untouched). From its first burst the FEED owns WSTOCK: real per-grade pcts in
-- field 3, and every scan path - button, alias, cart-event, staleness - is gated
-- off (the feed pushes changes itself).
do
  local WH_P1 = '{ "wstock": [ { "pct": 100, "amount": 1, "grade": "warplate", "good": "armour" }, { "amount": 1130, "pct": 100, "good": "spoils" }, { "pct": 90, "amount": 33, "grade": "slightly stale", "good": "fish" }, { "pct": 78, "amount": 251, "grade": "stale", "good": "fish" } ], "full": 1, "pages": 3, "guild": "viking", "page": 1 }'
  local WH_P2 = '{ "wstock": [ { "pct": 62, "amount": 42, "grade": "old", "good": "eggs" }, { "pct": 100, "amount": 300, "grade": "hard-cured", "good": "salted_fish" }, { "pct": 100, "amount": 630, "grade": "aged", "good": "mead" } ], "full": 1, "pages": 3, "guild": "viking", "page": 2 }'
  local WH_P3 = '{ "wstock_cap": 8369, "full": 1, "pages": 3, "guild": "viking", "page": 3 }'

  fresh_client(); dofile(PLUGIN); fire_timers()
  gmcp("Guild.Warehouse", WH_P1); gmcp("Guild.Warehouse", WH_P2); gmcp("Guild.Warehouse", WH_P3)
  fire_timers()
  local prod = st("production")
  check(prod:find("Warehouse  2387 / 8369", 1, true),
    "warehouse feed: total and capacity straight from Guild.Warehouse")
  check(prod:find("@{warning}251 stale 78%@{}", 1, true) and prod:find("@{dim}630 aged@{}", 1, true),
    "warehouse feed: real freshness pcts ride the grade rows (stale fish at 78%)")
  check(prod:find("@{warning}33 slightly stale 90%@{}", 1, true),
    "warehouse feed: every under-100 grade wears its pct and warning")
  check(prod:find("salted fish", 1, true), "warehouse feed: key-form goods display with spaces")
  check(not st("builds"):find("no stock data", 1, true), "warehouse feed: the builds gap note is gone")

  -- the scan paths stand down: the manual alias says so and sends nothing...
  printed = {}; sent = {}
  alias("^vstock$")
  fire_timers()
  check(said("warehouse feed (Guild.Warehouse) is live"), "feed live: vstock says the feed owns stock")
  local scanned = false
  for _, c in ipairs(sent) do if c == "vtrade stock" then scanned = true end end
  check(not scanned, "feed live: the manual scan sends nothing")

  -- ...a hand-typed report that scrolls by is NOT allowed to clobber the feed's pcts...
  feed_stock_report()
  fire_timers()
  check(st("production"):find("Warehouse  2387 / 8369", 1, true),
    "feed live: a passing vtrade stock report does not overwrite the feed")

  -- ...and cart traffic no longer triggers scans (the feed pushes the change itself)
  sent = {}
  gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "cart_id": 2, "mode": "sell", "good": "bread", "village": "Lejre", "secs": 100, "cap": 313 } ], "guild": "viking" }')
  tick(20)
  gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ ], "guild": "viking" }')
  fire_timers()
  scanned = false
  for _, c in ipairs(sent) do if c == "vtrade stock" then scanned = true end end
  check(not scanned, "feed live: a cart docking does not send vtrade stock")

  -- the auto-trader arms on feed stock without the waiting note
  printed = {}; sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  check(not said("auto-trade waiting"), "feed live: the trader arms without waiting")
  alias("^atrade (.+)$", "off"); fire_timers()
  -- (the staleness exemption gets its own scenario below - a second arm here would
  -- trip over the market scan this first arm started)

  -- a full burst is the whole warehouse: goods it no longer lists are gone
  gmcp("Guild.Warehouse", '{ "wstock": [ { "pct": 100, "amount": 1, "grade": "warplate", "good": "armour" }, { "amount": 1130, "pct": 100, "good": "spoils" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
  gmcp("Guild.Warehouse", '{ "wstock_cap": 8369, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
  fire_timers()
  prod = st("production")
  check(not prod:find("630 aged", 1, true) and prod:find("Warehouse  1131 / 8369", 1, true),
    "warehouse feed: a full burst replaces the stock (sold-out goods vanish)")
end

-- =====================================================================
-- Feed stock never goes 'stale': with Guild.Warehouse live, the 90 s hold that
-- protects text-scan snapshots must NOT hold the trader (the re-scan it would wait
-- for is gated off and can never come - the trader would stall for good). The
-- proof it got PAST the hold: the dispatch pass reaches the market refresh, whose
-- first send goes out synchronously.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  gmcp("Guild.Warehouse", '{ "wstock": [ { "pct": 100, "amount": 630, "grade": "aged", "good": "mead" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
  gmcp("Guild.Warehouse", '{ "wstock_cap": 8369, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
  fire_timers()
  tick(100)                       -- well past the text-scan staleness window
  printed = {}; sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  check(not said("auto-trade waiting"), "feed stock: armed straight away after 100 s")
  local goods_scan, stock_scan = false, false
  for _, c in ipairs(sent) do
    if c == "vtrade goods timber" then goods_scan = true end
    if c == "vtrade stock" then stock_scan = true end
  end
  check(goods_scan, "feed stock: the dispatch pass gets PAST the staleness hold (market refresh starts)")
  check(not stock_scan, "feed stock: and never falls back to vtrade stock")
  alias("^atrade (.+)$", "off"); fire_timers()
end

-- =====================================================================
-- GUILD.CITY raid + bdmg (server-side 28 Aug pm): the raid alarm on Stats' War
-- section, battle scars on the Builds tab. Raid payload verbatim from the capture.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  gmcp("Guild.City", '{ "blot": { "total": 9, "filled": 9, "state": "rest", "reset_in": 4230 }, "patrol": { "remaining": 10050, "count": 15 }, "nexttick": 9315, "cdtime": 0, "raid": { "strength": "Overwhelming", "faction": "sigurdsson", "secs": 8671 }, "guild": "viking", "dcycle": { "secs": 188906, "name": "Calm Season" } }')
  fire_timers()
  check(st("stats"):find("@{error,bold}RAID Overwhelming (sigurdsson) in 2h24m@{}", 1, true),
    "raid: the alarm shows who, how hard, and when - in error red")

  -- the countdown follows the feed
  gmcp("Guild.City", '{ "raid": { "strength": "Overwhelming", "faction": "sigurdsson", "secs": 500 }, "guild": "viking" }')
  fire_timers()
  check(st("stats"):find("in 8m", 1, true), "raid: the countdown tracks the bursts")

  -- while bursts keep arriving the alarm STAYS up through the heartbeat: each
  -- burst refreshes the shelf-life clock (a dead clock would clear it in seconds)
  tick(300)
  gmcp("Guild.City", '{ "raid": { "strength": "Overwhelming", "faction": "sigurdsson", "secs": 8000 }, "guild": "viking" }')
  tick(300)
  fire_timers()
  check(st("stats"):find("RAID", 1, true), "raid: bursts keep the alarm alive across the heartbeat")

  -- shelf life: raid{} rides the unpaged stream (its keys are never removed), so
  -- ten minutes of silence clears the alarm rather than crying wolf forever
  tick(601)
  fire_timers()
  check(not st("stats"):find("RAID", 1, true), "raid: the alarm ages out after 10 min of silence")

  -- building damage arrives on the paged burst; a later full burst without it clears
  gmcp("Guild.City", '{ "full": 1, "pages": 1, "bdmg": [ { "pct": 12, "id": "settler_plots" } ], "page": 1, "guild": "viking" }')
  fire_timers()
  check(st("builds"):find("Damaged: settler plots 12%", 1, true),
    "bdmg: damaged buildings named on the Builds tab")
  gmcp("Guild.City", '{ "full": 1, "pages": 1, "page": 1, "guild": "viking" }')
  fire_timers()
  check(not st("builds"):find("Damaged:", 1, true), "bdmg: a full burst without damage clears the line")
end

-- =====================================================================
-- PER-GOOD STOCK FLOORS (2.7.0): 'atrade floor bread 400' keeps at least 400
-- bread whatever the category reserve says. Warehouse from the canned stock
-- report (Bread 466, Fish 300); market scanned through the real parser - Bread
-- has a seller's market, Fish a buyer's, and the Honey header closes the scan
-- early exactly as live.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  alias("^atrade (.+)$", "yard 0")   -- the cartyard clock has its own scenario below;
                                     -- these phases test the floor arithmetic in isolation
  gmcp("Guild.State", '{ "daler": 900000, "guild": "viking" }')   -- budget for the restocker
  -- Each dispatch now DEBITS the local market (2.11.0), so every phase below
  -- re-feeds the scan first - the server's numbers restoring a debit is exactly
  -- the live contract, and it keeps these phases testing floors, not demand.
  local function feed_market()
    alias("^mkref$")
    for _, l in ipairs({
      "-~*   Bread - Market Overview                       *~-",
      "-~*  Nidaros          25  800 wants import Vinur    *~-",
      "-~*   Fish - Market Overview                        *~-",
      "-~*  Lodbrok's Hol    10  500 avail export++ Vinur  *~-",
      "-~*  Nidaros           8   60 wants import Vinur    *~-",
      "-~*   Honey - Market Overview                       *~-",
    }) do for _, h in ipairs(line_hooks) do h(l) end end
    fire_timers()           -- settle + finalize the scan
  end
  feed_market()
  feed_stock_report()       -- Bread 466 / Fish 300 in the warehouse
  fire_timers()
  gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
  gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 310, "refit": "speed", "tier": 5 } ], "guild": "viking" }')
  fire_timers()

  -- baseline: no floor - bread sells a full cart (466 - keep 20 = 446, cart-capped 310)
  sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  local qty0
  for _, c in ipairs(sent) do qty0 = qty0 or tonumber(c:match("vtrade dispatch sell (%d+) bread")) end
  check(qty0 == 310, "no floor: bread sells a full cart (got " .. tostring(qty0) .. ")")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- The pending-cart counter only resets when a tick SEES the carts string change,
  -- so each phase below arms first, then walks CARTS non-empty -> empty across two
  -- tick rounds: the second tick has a free cart and fresh pending.
  local function next_pass(secs)
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "cart_id": 9, "mode": "sell", "good": "bread", "village": "Nidaros", "secs": ' .. secs .. ', "cap": 310 } ], "guild": "viking" }')
    fire_timers()
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ ], "guild": "viking" }')
    fire_timers()
  end

  -- floor 400: only the surplus above the floor may go (466 - 400 = 66)
  feed_market()             -- restore the demand the baseline carts debited
  alias("^atrade (.+)$", "floor bread 400")
  check(store["at_floors"] == "bread=400", "the floor persists in the store")
  sent = {}
  alias("^atrade (.+)$", "on"); next_pass(100)
  local qty1
  for _, c in ipairs(sent) do qty1 = qty1 or tonumber(c:match("vtrade dispatch sell (%d+) bread")) end
  check(qty1 == 66, "floor 400: exactly the surplus sells (got " .. tostring(qty1) .. ")")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- floor 450: surplus 16 is under the dispatch minimum - bread stays home
  feed_market()
  alias("^atrade (.+)$", "floor bread 450")
  sent = {}
  alias("^atrade (.+)$", "on"); next_pass(95)
  local sold_bread = false
  for _, c in ipairs(sent) do if c:match("vtrade dispatch sell %d+ bread") then sold_bread = true end end
  check(not sold_bread, "floor 450: surplus under the game minimum - nothing sells")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- the Trade tab: floored names wear info-blue; held (amber) wins over floored.
  -- The good's name now carries THREE generations of right-button in one spec (1.19):
  -- menu= first (named hold/floor/clear entries, this good's state in the labels),
  -- then rclick= for a pre-1.19 host, then click= for a pre-1.16 one.
  local rep = tostring(state["plugin.3s-viking-status-gmcp.report"])
  check(rep:find("@{info,menu=Hold (never trade)|atrade exempt bread;Never sell|atrade nosell bread;Set floor 500|atrade floorset bread"
      .. ";Clear floor (450)|atrade floor bread 0,rclick=atrade floorset bread,click=atrade exempt bread}", 1, true),
    "floored bread is blue and its menu offers hold, never-sell, set-floor and clear-floor")
  check(rep:find("menu=Hold (never trade)|atrade exempt bread", 1, true)
    and rep:find(",rclick=atrade floorset bread,click=", 1, true),
    "menu rides FIRST, rclick and click behind it - three host generations, one string")
  alias("^atrade (.+)$", "exempt bread")
  rep = tostring(state["plugin.3s-viking-status-gmcp.report"])
  check(rep:find("@{warning,menu=Release from hold|atrade exempt bread", 1, true),
    "held beats floored: amber wins, and the menu's first entry flips to Release")
  alias("^atrade (.+)$", "exempt bread")   -- release again

  -- the Trade Auto tab lists the floors beside the held list
  check(tostring(state["plugin.3s-viking-status-gmcp.atstatus"]):find("floors (never sold below): bread=450", 1, true),
    "the auto tab shows the floor list")

  -- restock: a floored RAW tops up to the floor, not just to Raw>. Fish holds 300
  -- (= Raw>, so the baseline restocker leaves it) - floor it to 500 and it buys.
  sent = {}
  alias("^atrade (.+)$", "restock on")
  alias("^atrade (.+)$", "on"); next_pass(90)
  local fish_buy = false
  for _, c in ipairs(sent) do if c:match("vtrade dispatch buy %d+ fish") then fish_buy = true end end
  check(not fish_buy, "no floor: fish at Raw> is not restocked")
  alias("^atrade (.+)$", "off"); fire_timers()
  alias("^atrade (.+)$", "floor fish 500")
  sent = {}
  alias("^atrade (.+)$", "on"); next_pass(80)
  fish_buy = false
  for _, c in ipairs(sent) do if c:match("vtrade dispatch buy %d+ fish lodbrok") then fish_buy = true end end
  check(fish_buy, "floor 500: the restocker tops fish up toward its floor")
  alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "restock off"); fire_timers()

  -- a floor BELOW the category reserve changes nothing: it raises, never lowers
  -- (fish is raw: Raw> 300 already protects all 300 held, floor 100 must not
  -- open 200 of it for sale)
  alias("^atrade (.+)$", "floor fish 100")
  sent = {}
  alias("^atrade (.+)$", "on"); next_pass(70)
  local sold_fish = false
  for _, c in ipairs(sent) do if c:match("vtrade dispatch sell %d+ fish") then sold_fish = true end end
  check(not sold_fish, "a floor under the raw reserve does not lower it")
  alias("^atrade (.+)$", "off"); fire_timers()
  alias("^atrade (.+)$", "floor fish 500")   -- back where the earlier checks left it

  -- the manual Dispatch tab respects floors too: bread (floored 450, surplus 16)
  -- must not appear among its candidates
  check(not tostring(state["plugin.3s-viking-status-gmcp.carts"]):find("bread", 1, true)
    and not tostring(state["plugin.3s-viking-status-gmcp.carts"]):find("Bread", 1, true),
    "manual dispatch offers no floored-down bread cart")

  -- display names normalise like the held list: 'Salted Fish' floors as 'salted'
  alias("^atrade (.+)$", "floor salted fish 200")
  check(tostring(store["at_floors"]):find("salted=200", 1, true),
    "a display-name floor lands on the command word")
  alias("^atrade (.+)$", "floor salted off")

  -- 'off' clears one floor; a restart honours what the store still holds
  alias("^atrade (.+)$", "floor bread off")
  check(store["at_floors"] == "fish=500", "clearing one floor leaves the others")
  fresh_client(); store["at_floors"] = "grain=750"; dofile(PLUGIN)
  printed = {}
  alias("^atrade (.+)$", "floor")
  check(said("grain=750"), "a restart loads the stored floors")
end

-- =====================================================================
-- NEVER SELL (2.29.0): a floor alone still sells the surplus above it, so a floored raw
-- that restocked a whole cart shipped it straight back out, dipped under and bought
-- again (Joakim, 13 Oct). 'atrade nosell <good>' stops the selling - and the scalping,
-- which only buys to resell - while the restocker still holds the floor.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  alias("^atrade (.+)$", "yard 0")
  gmcp("Guild.State", '{ "daler": 900000, "guild": "viking" }')
  local function feed_market(fish_sell)
    alias("^mkref$")
    for _, l in ipairs({
      "-~*   Bread - Market Overview                       *~-",
      "-~*  Nidaros          25  800 wants import Vinur    *~-",
      "-~*   Fish - Market Overview                        *~-",
      "-~*  Lodbrok's Hol    10  500 avail export++ Vinur  *~-",
      fish_sell or "-~*  Nidaros           8   60 wants import Vinur    *~-",
      "-~*   Honey - Market Overview                       *~-",
    }) do for _, h in ipairs(line_hooks) do h(l) end end
    fire_timers()
  end
  feed_market()
  feed_stock_report()       -- Bread 466 / Fish 300
  fire_timers()
  gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
  gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 310, "refit": "speed", "tier": 5 } ], "guild": "viking" }')
  fire_timers()
  local function next_pass(secs)
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "cart_id": 9, "mode": "sell", "good": "bread", "village": "Nidaros", "secs": ' .. secs .. ', "cap": 310 } ], "guild": "viking" }')
    fire_timers()
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ ], "guild": "viking" }')
    fire_timers()
  end
  local function sent_match(pat)
    for _, c in ipairs(sent) do if c:match(pat) then return true end end
    return false
  end
  local function carts_tab() return tostring(state["plugin.3s-viking-status-gmcp.carts"] or ""):lower() end

  -- the user's case: bread floored at 400 sells its 66 surplus...
  alias("^atrade (.+)$", "floor bread 400")
  sent = {}
  alias("^atrade (.+)$", "on"); next_pass(100)
  check(sent_match("vtrade dispatch sell %d+ bread"), "never sell: baseline - floored bread still sells its surplus")
  alias("^atrade (.+)$", "off"); fire_timers()
  check(carts_tab():find("bread", 1, true) ~= nil, "never sell: baseline - Quick dispatch offers a bread cart")

  -- ...and with never-sell on, nothing of it goes
  feed_market()
  alias("^atrade (.+)$", "nosell bread")
  check(store["at_nosell"] == "bread", "never sell: the flag persists in the store")
  check(said("never sell: bread - restock still keeps it at 400 or more"), "never sell: says what it does, floor included")
  sent = {}
  alias("^atrade (.+)$", "on"); next_pass(95)
  check(not sent_match("vtrade dispatch sell %d+ bread"), "never sell: a never-sell good is not sold above its floor")
  alias("^atrade (.+)$", "off"); fire_timers()
  check(carts_tab():find("bread", 1, true) == nil, "never sell: Quick dispatch offers no cart of it either")

  -- the floor still RESTOCKS: fish floored 500 and never-sell still buys back up
  alias("^atrade (.+)$", "floor fish 500")
  alias("^atrade (.+)$", "nosell fish")
  alias("^atrade (.+)$", "restock on")
  sent = {}
  alias("^atrade (.+)$", "on"); next_pass(90)
  check(sent_match("vtrade dispatch buy %d+ fish lodbrok"), "never sell: a floored never-sell raw is still restocked")
  alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "restock off"); fire_timers()
  alias("^atrade (.+)$", "floor fish off")

  -- the scalper leaves it alone: fish buys at 10 and sells at 30 - a scalp, until never-sell
  alias("^atrade (.+)$", "nosell fish")      -- toggle off first: the baseline
  feed_market("-~*  Nidaros          30  400 wants import Vinur    *~-")
  alias("^atrade (.+)$", "soft 85")          -- the canned warehouse is 73% full: no pressure
  alias("^atrade (.+)$", "scalp on")
  sent = {}
  alias("^atrade (.+)$", "on"); next_pass(85)
  check(sent_match("vtrade dispatch buy %d+ fish"), "never sell: baseline - the scalper buys fish to resell")
  alias("^atrade (.+)$", "off"); fire_timers()
  feed_market("-~*  Nidaros          30  400 wants import Vinur    *~-")
  alias("^atrade (.+)$", "nosell fish")
  sent = {}
  alias("^atrade (.+)$", "on"); next_pass(80)
  check(not sent_match("vtrade dispatch buy %d+ fish"), "never sell: a never-sell good is never scalped")
  alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "scalp off"); fire_timers()

  -- the Trade tab: violet, and the menu entry flips; held amber still wins
  local rep = tostring(state["plugin.3s-viking-status-gmcp.report"])
  check(rep:find("@{#B48CFF,menu=Hold (never trade)|atrade exempt bread;Allow selling|atrade nosell bread;", 1, true),
    "never sell: the name is violet and the menu offers to allow selling again")
  alias("^atrade (.+)$", "exempt bread")
  rep = tostring(state["plugin.3s-viking-status-gmcp.report"])
  check(rep:find("@{warning,menu=Release from hold|atrade exempt bread;Allow selling", 1, true),
    "never sell: held amber still wins the colour")
  alias("^atrade (.+)$", "exempt bread")

  -- the Trade Auto tab lists it
  check(tostring(state["plugin.3s-viking-status-gmcp.atstatus"]):find("never sell (still restocked): bread, fish", 1, true),
    "never sell: the auto tab lists the never-sell goods")

  -- toggling off sells the surplus again
  printed = {}
  alias("^atrade (.+)$", "nosell bread")
  check(said("bread sells its surplus again"), "never sell: toggling off says so")
  feed_market()
  sent = {}
  alias("^atrade (.+)$", "on"); next_pass(75)
  check(sent_match("vtrade dispatch sell %d+ bread"), "never sell: off again, the surplus sells")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- display names normalise; a restart loads the list; clear empties it
  alias("^atrade (.+)$", "nosell salted fish")
  check(store["at_nosell"] == "fish,salted", "never sell: a display name lands on the command word ("
    .. tostring(store["at_nosell"]) .. ")")
  fresh_client(); store["at_nosell"] = "grain,mead"; dofile(PLUGIN)
  printed = {}
  alias("^atrade (.+)$", "nosell")
  check(said("never sell: grain, mead"), "never sell: a restart loads the stored list")
  alias("^atrade (.+)$", "nosell clear")
  check(store["at_nosell"] == "", "never sell: 'nosell clear' empties it")
end

-- =====================================================================
-- THE FLOOR BOX + RIGHT-CLICK (2.8.0): the Trade tab input above Units holds a
-- value; 'atrade floorset <good>' (what the report rows' rclick= runs) applies
-- it as that good's floor, toggles it off at the same value, and with the box
-- at 0 the right button only ever clears.
do
  fresh_client(); dofile(PLUGIN); fire_timers()

  -- the input sits directly above Units on the Trade tab, seeded from the store default
  local widgets
  for _, tab in ipairs(panel.tabs) do if tab.title == "Trade" then widgets = tab.widgets end end
  local fl_i, un_i, fl_w
  for i, w in ipairs(widgets or {}) do
    if w.type == "input" and tostring(w.bind):find("v_floorset") then fl_i = i; fl_w = w end
    if w.type == "input" and tostring(w.bind):find("v_units") then un_i = i end
  end
  check(fl_w ~= nil and un_i ~= nil and fl_i == un_i - 1, "the Floor box sits just above Units")
  check(tostring(state["plugin.3s-viking-status-gmcp.v_floorset"]) == "500",
    "the box is seeded with the default floor value")

  -- right-click applies the box's value...
  alias("^atrade (.+)$", "floorset bread")
  check(store["at_floors"] == "bread=500", "floorset applies the box value as the floor")
  -- ...the same value again toggles it off...
  alias("^atrade (.+)$", "floorset bread")
  check((store["at_floors"] or "") == "", "the same right-click again clears it")
  -- ...and a changed box value re-floors at the new level (submitting via the widget)
  fl_w.onSubmit("750")
  check(store["mk_floorset"] == "750" and tostring(state["plugin.3s-viking-status-gmcp.v_floorset"]) == "750",
    "the box persists and re-seeds its state")
  alias("^atrade (.+)$", "floorset bread")
  check(store["at_floors"] == "bread=750", "floorset uses the new value")
  -- a floored good at a DIFFERENT box value moves to the new value, no toggle
  fl_w.onSubmit("600")
  alias("^atrade (.+)$", "floorset bread")
  check(store["at_floors"] == "bread=600", "a different value re-floors rather than clearing")

  -- box at 0: the right button is an eraser
  fl_w.onSubmit("0")
  alias("^atrade (.+)$", "floorset bread")
  check((store["at_floors"] or "") == "", "box at 0: right-click clears the floor")
  printed = {}
  alias("^atrade (.+)$", "floorset bread")
  check(said("right-click only clears"), "box at 0 on an unfloored good: says what to do, sets nothing")
  check((store["at_floors"] or "") == "", "...and no floor appears")

  -- display names normalise on this path too
  fl_w.onSubmit("300")
  alias("^atrade (.+)$", "floorset salted fish")
  check(tostring(store["at_floors"]):find("salted=300", 1, true), "floorset normalises display names")
  alias("^atrade (.+)$", "floorset salted fish")
  check(not tostring(store["at_floors"] or ""):find("salted", 1, true),
    "...and the toggle matches through the display name (second right-click clears)")

  -- a restart reads the box back from the store
  fresh_client(); store["mk_floorset"] = "42"; dofile(PLUGIN); fire_timers()
  check(tostring(state["plugin.3s-viking-status-gmcp.v_floorset"]) == "42",
    "a restart re-seeds the box from the store")
end

-- =====================================================================
-- THE CARTYARD CLOCK (2.9.0): one caravan per ~3 min in game. A dispatch starts a
-- hold; the yard's framed "Ready in" refusal corrects it; a never-left timeout
-- releases a hold that was only guessed. Framed lines verbatim from the live soak.
do
  local function yard_world()
    fresh_client(); dofile(PLUGIN); fire_timers()
    gmcp("Guild.State", '{ "daler": 900000, "guild": "viking" }')
    alias("^mkref$")
    for _, l in ipairs({
      "-~*   Bread - Market Overview                       *~-",
      "-~*  Nidaros          25  800 wants import Vinur    *~-",
      "-~*   Honey - Market Overview                       *~-",
    }) do for _, h in ipairs(line_hooks) do h(l) end end
    fire_timers()
    -- stock from the WAREHOUSE FEED, not the scan: these scenarios tick far past the
    -- text-scan staleness window, and feed-owned stock is exempt from it (2.6.0)
    gmcp("Guild.Warehouse", '{ "wstock": [ { "pct": 100, "amount": 466, "grade": "hardtack", "good": "bread" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
    gmcp("Guild.Warehouse", '{ "wstock_cap": 8369, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
    fire_timers()
    gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 310, "refit": "speed", "tier": 5 } ], "guild": "viking" }')
    fire_timers()
  end
  local function edge(secs)   -- a carts edge, like the live feed after a dispatch window
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "cart_id": 9, "mode": "sell", "good": "bread", "village": "Nidaros", "secs": ' .. secs .. ', "cap": 310 } ], "guild": "viking" }')
    fire_timers()
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ ], "guild": "viking" }')
    fire_timers()
  end
  local function sells() local n = 0; for _, c in ipairs(sent) do if c:match("vtrade dispatch sell") then n = n + 1 end end; return n end
  local function refuse(txt)
    for _, t in ipairs(triggers) do
      if tostring(t.pattern):find("cartyard is preparing", 1, true) then t.run() end
    end
    for _, t in ipairs(triggers) do
      if tostring(t.pattern):find("Ready in", 1, true) then t.run(txt) end
    end
  end

  -- 1. the provisional hold: one dispatch, then feed edges bring NO second cart
  yard_world()
  sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  check(sells() == 1, "yard: the first cart goes out")
  edge(100); edge(90)
  check(sells() == 1, "yard: the hold stops a second dispatch for the whole cooldown")
  check(tostring(state["plugin.3s-viking-status-gmcp.atstatus"]):find("cartyard: next cart in", 1, true),
    "yard: the Trade Auto tab shows the countdown")
  -- ...and it expires: 181 ticks later the next edge dispatches
  tick(181)
  edge(80)
  check(sells() == 2, "yard: the hold expires and the trader resumes")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- 2. the refusal corrects the clock and takes back the refused dispatch quietly
  yard_world()
  alias("^atrade (.+)$", "yard 5")           -- a too-short guess: the yard will say otherwise
  sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  check(sells() == 1, "refusal: first cart out")
  tick(6)                                     -- the 5 s guess expires
  printed = {}
  edge(70)                                    -- doomed probe goes out...
  check(sells() == 2, "refusal: the too-short guess lets one doomed probe out")
  refuse("2m 40s                                     *~-")   -- ...and the yard answers
  check(said("cartyard busy - next cart in 160s"), "refusal: the yard's number is read (2m 40s = 160)")
  printed = {}
  tick(13)
  gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ ], "guild": "viking" }')  -- a settle tick
  fire_timers()
  check(not said("never left"), "refusal: the refused dispatch is taken back quietly, no timeout noise")
  edge(60); edge(50)
  check(sells() == 2, "refusal: the corrected clock holds where the guess would have expired")
  tick(165)
  edge(40)
  check(sells() == 3, "refusal: the yard opens on the yard's own schedule")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- 3. a never-left WITHOUT a refusal releases a guessed hold: the yard was not taken
  yard_world()
  sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  check(sells() == 1, "release: first dispatch out")
  printed = {}
  tick(13)                                    -- no cart ever appears, no refusal either
  -- settle runs on feed ticks (the confirm timer fires in real seconds, which the
  -- harness cannot wait out): an EMPTY carts burst is a feed tick that confirms nothing
  gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ ], "guild": "viking" }')
  fire_timers()
  check(said("never left"), "release: the confirm timeout still reports it")
  edge(30)
  check(sells() == 2, "release: the guessed hold is released - the trader tries again")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- 4. 'atrade yard 0' turns the guess off (refusals still rule): two edges, two sends
  yard_world()
  alias("^atrade (.+)$", "yard 0")
  check(store["at_yard"] == "0", "yard: the setting persists")
  sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  edge(20)
  check(sells() >= 2, "yard 0: no provisional hold between dispatches (old behaviour)")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- 5. a lone "Ready in" line with no cartyard line before it is ignored
  yard_world()
  sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  tick(181)
  for _, t in ipairs(triggers) do
    if tostring(t.pattern):find("Ready in", 1, true) then t.run("9m 59s") end
  end
  edge(15)
  check(sells() == 2, "a bare 'Ready in' with no cartyard line before it sets no hold")
  alias("^atrade (.+)$", "off"); fire_timers()
end

-- =====================================================================
-- GUILD.TRADEGOODS (2.10.0): the market straight off the feed, through the cracked
-- code map. lin-0 rows verbatim from the 23:09 capture; the mapping they must land
-- under was solved from the overview symbol grid + the midgard price paste (two
-- independent matchings, zero mismatches).
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  -- one burst: five Midgard rows (t/i share prices, differ in supply - the pair the
  -- symbols could not split; the numbers pin t=timber i=iron)
  gmcp("Guild.TradeGoods", '{ "goods": [ { "buy": 31, "lin": 0, "dem": 246, "sell": 37, "score": 0, "sup": 196, "good": "t" }, { "buy": 31, "lin": 0, "dem": 144, "sell": 37, "score": 0, "sup": 104, "good": "i" }, { "buy": 11, "lin": 0, "dem": 279, "sell": 14, "score": 3, "sup": 124, "good": "f" }, { "buy": 5, "lin": 0, "dem": 394, "sell": 6, "score": 2, "sup": 282, "good": "h" }, { "buy": 7, "lin": 0, "dem": 47, "sell": 8, "score": -2, "sup": 354, "good": "y" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
  gmcp("Guild.TradeGoods", '{ "lin": 0, "lin_count": 14, "full": 1, "pages": 2, "page": 2, "guild": "viking" }')
  fire_timers()
  check(said("Guild.TradeGoods is live"), "tradegoods: the feed announces itself once")
  local rep = tostring(state["plugin.3s-viking-status-gmcp.report"])
  check(rep:match("Timber[^\n]*196") ~= nil, "tradegoods: code t is TIMBER (its supply rides the row)")
  check(rep:match("Iron[^\n]*104") ~= nil, "tradegoods: code i is IRON - the pair the symbols could not split")
  check(rep:match("Timber[^\n]*Midgard") ~= nil, "tradegoods: lin 0 is Midgard")
  check(rep:find("Furs", 1, true) and rep:find("Honey", 1, true), "tradegoods: f=furs, y=honey render too")
  check(tostring(state["plugin.3s-viking-status-gmcp.status"]):find("live from the feed", 1, true),
    "tradegoods: the status label says the report is feed-fed")
  check(tostring(store["market"] or ""):find("timber", 1, true), "tradegoods: the feed market persists")
  check(tostring(store["market"] or ""):find("fish\tMidgard\t5\t282\t6\t394\tin demand", 1, true),
    "tradegoods: a serialized row carries the affinity word on the game's own scale")
  check(tostring(store["market"] or ""):find("high demand", 1, true),
    "tradegoods: score 3 serializes as high demand (furs)")

  -- village names ride Guild.Fleet's lineage list (lin i = lineage[i]) - and the
  -- LIVE list beats the baked-in fallback order, so a reshuffled server heals itself
  -- (this lineage deliberately disagrees with TG_ORDER: Eiriksby first)
  gmcp("Guild.Fleet", '{ "pages": 1, "page": 1, "rtargets_lineage": [ "Eiriksby:salted_fish:cloth", "Lodbrok\'s Hold:bread:fine_furs" ], "guild": "viking" }')
  gmcp("Guild.TradeGoods", '{ "goods": [ { "buy": 60, "lin": 1, "dem": 400, "sell": 43, "score": 2, "sup": 99, "good": "b" } ], "full": 1, "pages": 1, "page": 1, "guild": "viking" }')
  fire_timers()
  rep = tostring(state["plugin.3s-viking-status-gmcp.report"])
  check(rep:match("Bread[^\n]*Eiriksby") ~= nil,
    "tradegoods: lin 1 wears the LIVE lineage's first town, not the baked-in order")

  -- mkref stands down while the feed owns the market
  printed = {}; sent = {}
  alias("^mkref$"); fire_timers()
  check(said("market feed (Guild.TradeGoods) is live"), "tradegoods: mkref says the feed owns prices")
  local scanned = false
  for _, c in ipairs(sent) do if c:match("^vtrade goods") then scanned = true end end
  check(not scanned, "tradegoods: mkref sends no scan burst")

  -- end to end: the auto-trader dispatches off feed prices, no text scan ever sent
  gmcp("Guild.Warehouse", '{ "wstock": [ { "pct": 100, "amount": 466, "grade": "hardtack", "good": "bread" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
  gmcp("Guild.Warehouse", '{ "wstock_cap": 8369, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
  gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
  gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 310, "refit": "speed", "tier": 5 } ], "guild": "viking" }')
  fire_timers()
  sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  local sold, scanned2 = false, false
  for _, c in ipairs(sent) do
    if c:match("^vtrade dispatch sell %d+ bread eiriksby") then sold = true end
    if c:match("^vtrade goods") then scanned2 = true end
  end
  check(sold, "tradegoods: the trader sells bread to Eiriksby off feed prices alone")
  check(not scanned2, "tradegoods: and no vtrade goods command ever went out")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- fallback order covers a village before the fleet has spoken
  fresh_client(); dofile(PLUGIN); fire_timers()
  gmcp("Guild.TradeGoods", '{ "goods": [ { "buy": 15, "lin": 2, "dem": 300, "sell": 15, "score": 0, "sup": 256, "good": "g" } ], "full": 1, "pages": 1, "page": 1, "guild": "viking" }')
  fire_timers()
  check(tostring(state["plugin.3s-viking-status-gmcp.report"]):match("Grain[^\n]*Eiriksby") ~= nil,
    "tradegoods: without the fleet, the overview's village order stands in")
end

-- =====================================================================
-- THE FEED-QUIET BRICK (2.10.1): Guild.TradeGoods pushes only when a price CHANGES,
-- so a quiet hour is normal - but the auto-trader's 60 s staleness clock (a text-scan
-- concept) still ran, called mk_refresh, and mk_refresh under a live feed deliberately
-- does nothing. Every pass bailed forever: market rendered perfectly, zero carts
-- (Joakim, 29 Aug live). Feed-owned prices never age; the clock now gates the text
-- scan only. os.time is swapped here because that is the clock the bug lived on -
-- every earlier test ran inside the 60 s window and could not see it.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  gmcp("Guild.Fleet", '{ "pages": 1, "page": 1, "rtargets_lineage": [ "Eiriksby:salted_fish:cloth" ], "guild": "viking" }')
  gmcp("Guild.TradeGoods", '{ "goods": [ { "buy": 60, "lin": 1, "dem": 400, "sell": 43, "score": 2, "sup": 99, "good": "b" } ], "full": 1, "pages": 1, "page": 1, "guild": "viking" }')
  gmcp("Guild.Warehouse", '{ "wstock": [ { "pct": 100, "amount": 466, "grade": "hardtack", "good": "bread" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
  gmcp("Guild.Warehouse", '{ "wstock_cap": 8369, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
  gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
  gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 310, "refit": "speed", "tier": 5 } ], "guild": "viking" }')
  fire_timers()

  -- two minutes with nothing changing on the market: no burst, no clock reset
  local real_time = os.time
  os.time = function() return real_time() + 120 end

  sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  local sold, scanned = false, false
  for _, c in ipairs(sent) do
    if c:match("^vtrade dispatch sell %d+ bread eiriksby") then sold = true end
    if c:match("^vtrade goods") then scanned = true end
  end
  check(sold, "feed-quiet: two silent minutes on a live feed do not brick the trader")
  check(not scanned, "feed-quiet: and the staleness clock triggers no text scan while the feed owns prices")

  os.time = real_time
  alias("^atrade (.+)$", "off"); fire_timers()
end
-- =====================================================================
-- DEMAND IS AN ANSWER (2.11.0): dem 0 means the town won't buy - it must get no
-- cart (the old code read 0 as "unknown" and shipped a FULL cart at cap into the
-- satisfied town, 29 Aug live). And between feed bursts the trader itself is the
-- thing moving the market: a dispatched cart debits the local dem/sup so the next
-- pass doesn't stack a second cart into demand the first one already claimed.
do
  -- stage 1: the best-PRICED town has dem 0 -> the cart goes to the next town
  fresh_client(); dofile(PLUGIN); fire_timers()
  gmcp("Guild.Fleet", '{ "pages": 1, "page": 1, "rtargets_lineage": [ "Eiriksby:salted_fish:cloth", "Lodbrok\'s Hold:bread:fine_furs" ], "guild": "viking" }')
  gmcp("Guild.TradeGoods", '{ "goods": [ { "buy": 55, "lin": 1, "dem": 0, "sell": 60, "score": -2, "sup": 900, "good": "b" }, { "buy": 40, "lin": 2, "dem": 310, "sell": 43, "score": 2, "sup": 99, "good": "b" } ], "full": 1, "pages": 1, "page": 1, "guild": "viking" }')
  gmcp("Guild.Warehouse", '{ "wstock": [ { "pct": 100, "amount": 1000, "grade": "hardtack", "good": "bread" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
  gmcp("Guild.Warehouse", '{ "wstock_cap": 8369, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
  gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
  gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 310, "refit": "speed", "tier": 5 } ], "guild": "viking" }')
  fire_timers()
  sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  local to_dead, to_live = false, false
  for _, c in ipairs(sent) do
    if c:match("^vtrade dispatch sell %d+ bread eiriksby") then to_dead = true end
    if c:match("^vtrade dispatch sell %d+ bread lodbrok") then to_live = true end
  end
  check(not to_dead, "demand: a town at dem 0 gets no cart even at the best price")
  check(to_live, "demand: the cart goes to the runner-up town that still wants bread")

  -- stage 2: that 310-cart claimed ALL of Lodbrok's 310 demand - the debit stops a
  -- second cart while the feed is silent. (Cart 42 on the road resets the pending
  -- counter; the yard hold is ticked off; trading post t5 leaves free cart slots.)
  gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ { "durability": 100, "escort": 5, "horses": 4, "amount": 310, "half_in": 0, "cart_id": 42, "cap": 310, "tier": 5, "good": "bread", "mode": "sell", "quality_pct": 100, "secs": 900, "refit": "speed", "village": "Lodbrok\'s Hold" } ], "guild": "viking" }')
  fire_timers()
  tick(181)
  sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  local resent = false
  for _, c in ipairs(sent) do if c:match("^vtrade dispatch sell %d+ bread") then resent = true end end
  check(not resent, "demand: the dispatch debited the town's dem - no second cart into it while the feed is quiet")

  -- stage 3 (vacuity guard for stage 2, and the burst-drives-dispatch trigger): the
  -- server speaks again - Lodbrok's demand is back - and that burst ALONE restarts
  -- the trader, no warehouse or cart event needed
  sent = {}
  gmcp("Guild.TradeGoods", '{ "goods": [ { "buy": 40, "lin": 2, "dem": 500, "sell": 43, "score": 2, "sup": 99, "good": "b" } ], "full": 1, "pages": 1, "page": 1, "guild": "viking" }')
  fire_timers()
  local again = false
  for _, c in ipairs(sent) do if c:match("^vtrade dispatch sell %d+ bread lodbrok") then again = true end end
  check(again, "demand: a fresh feed row restores the town and the burst itself re-runs the trader")
  alias("^atrade (.+)$", "off"); fire_timers()
end

-- =====================================================================
-- GXP TRACKS (2.12.0): Guild.State.gxp carries four guild-xp tracks - cur, the
-- advance threshold (_max), the last tick's gain (_last). Values verbatim from
-- the 17:25 capture. Buandi runs far PAST its threshold (a topped path), so the
-- section must colour by >=100% as done, not clamp or hide it.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  gmcp("Guild.State", '{ "gxp": { "buandi_last": 8, "vitka_max": 59100, "drotta_last": 3, "vitka": 32790, "drotta_max": 59100, "viga_max": 59100, "buandi": 1621768, "buandi_max": 59100, "drotta": 53146, "viga_last": 2, "vitka_last": 0, "viga": 20544 }, "full": 1, "pages": 1, "page": 1, "guild": "viking" }')
  fire_timers()
  local stats = tostring(state["plugin.3s-viking-status-gmcp.stats"])
  check(stats:find("-- GXP --", 1, true) ~= nil, "gxp: the Stats tab grows a GXP section")
  check(stats:match("Buandi%s+@{success}1,621,768/59,100@{}") ~= nil,
    "gxp: buandi past its threshold wears success, thousands separated")
  check(stats:match("Buandi[^\n]*@{info}%+8@{}") ~= nil, "gxp: the last tick's gain rides the line")
  check(stats:match("Viga%s+@{warning}20,544/59,100@{}") ~= nil,
    "gxp: a mid track wears the percent colour (34%% = warning)")
  check(stats:match("Vitka[^\n]*%+0") == nil, "gxp: a zero last-gain is not shown")
  check(stats:find("Buandi", 1, true) < stats:find("Drotta", 1, true)
    and stats:find("Drotta", 1, true) < stats:find("Viga", 1, true)
    and stats:find("Viga", 1, true) < stats:find("Vitka", 1, true),
    "gxp: the four tracks keep a fixed order")

  -- a gxp-ONLY burst must re-render stats by itself (the keymap carries the key;
  -- nothing else in this burst changes, so no other key can mask a missing entry)
  gmcp("Guild.State", '{ "gxp": { "buandi_last": 8, "vitka_max": 59100, "drotta_last": 3, "vitka": 32790, "drotta_max": 59100, "viga_max": 59100, "buandi": 1621768, "buandi_max": 59100, "drotta": 53146, "viga_last": 5, "vitka_last": 0, "viga": 25000 }, "full": 1, "pages": 1, "page": 1, "guild": "viking" }')
  fire_timers()
  stats = tostring(state["plugin.3s-viking-status-gmcp.stats"])
  check(stats:match("Viga%s+@{warning}25,000/59,100@{}[^\n]*%+5") ~= nil,
    "gxp: a gxp-only burst re-renders the tab on its own")
end

-- =====================================================================
-- Skills (merged in from lab-skillwatch, 30 Aug).
--
-- Ported from _lab/skillwatch_test.lua, with the half that changed rewritten:
-- the pool totals and daler no longer come from the MIP-era vik.* state keys
-- (which this plugin never set, so under GMCP that half was dead) but from this
-- plugin's own Guild.State feed - daler directly, the pools through a pairing
-- LEARNED by value-matching the listing's totals against the four gxp tracks.
-- Nothing here assumes Visindi is vitka; the test asserts the pairing is
-- discovered, and asserts equally that a pool whose number matches no track
-- stays unmapped rather than being guessed at.
--
-- The listings below are the real vskills format, captured in game.
do
  local FRAME = "-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-~*~-"
  local function fr(s) return "-~*  " .. s .. string.rep(" ", math.max(0, 62 - #s)) .. "  *~-" end
  local function L(t) for _, h in ipairs(line_hooks) do h(t) end end
  local function prompt() for _, f in ipairs(prompt_hooks) do f() end end
  local function connect() for _, f in ipairs(conn_hooks) do f() end end
  local function command(t) for _, f in ipairs(cmd_hooks) do f(t) end end
  local function vsk(rest)
    for _, a in ipairs(aliases) do
      if a.pattern == [[^vsk(?:\s+(.*))?$]] then a.run(rest); return true end
    end
    return false
  end
  local function rows()    return tostring(state["plugin.3s-viking-status-gmcp.swrows"] or "") end
  local function summary() return tostring(state["plugin.3s-viking-status-gmcp.swsummary"] or "") end

  local function feed_vitka()
    L(FRAME)
    L(fr("Vegr Vitka"))
    L(fr("Visindi: 57,117 points"))
    L(FRAME)
    L(fr("Name                        Saga                  Daler"))
    L(FRAME)
    L(fr("Runakostur ................ [20 (   max)]"))
    L(fr(" + Seidkostnadr ........... [20 (   max)]"))
    L(fr(" + Hugarvit ............... [10 ( 33000)]       [1,571]"))
    L(fr(" + Seidgerd ............... [18 ( 76000)]       [7,238]"))
    L(fr(" + Seidkyn ................ [ 3 (  4000)]      [10,285]"))
    L(fr("   + Voluvakunnustadr ...... [ 0 (  1750)]       [1,142]"))
    L(FRAME)
  end
  local function feed_berserkja()
    L(fr("Vegr Berserkja"))
    L(fr("Kappi: 8,392 points"))
    L(FRAME)
    L(fr("Name                        Saga                  Daler"))
    L(FRAME)
    L(fr("Berserksgangr ............. [20 (   max)]"))
    L(fr(" + Styrjold ............... [15 ( 60800)]       [6,095]"))
    L(fr(" + Eggmod ................. [15 ( 60800)]      [41,142]"))
    L(FRAME)
  end
  local function feed_footer()
    L(fr("Use vtrain <skill name> to train a skill."))
    L(fr("Trees: vitka | berserkja | hersins | buandi"))
    L(FRAME)
    prompt()
  end
  local function feed_full()
    L("> vskills")
    L(FRAME)
    L(fr("Viking Skills"))
    L(fr("Daler: 1,238,996"))
    L(FRAME)
    feed_vitka()
    feed_berserkja()
    feed_footer()
  end

  -- the gxp burst whose numbers the listing's pools will be matched against:
  -- vitka carries Visindi's 57,117 and viga carries Kappi's 8,392, while drotta
  -- and buandi carry numbers no listed pool has.
  local GXP_MATCHING = '{ "gxp": { "buandi": 2931336, "buandi_max": 59100, "buandi_last": 0, '
    .. '"drotta": 61731, "drotta_max": 59100, "drotta_last": 0, '
    .. '"viga": 8392, "viga_max": 59100, "viga_last": 0, '
    .. '"vitka": 57117, "vitka_max": 59100, "vitka_last": 0 }, '
    .. '"daler": 2222222, "full": 1, "pages": 1, "page": 1, "guild": "viking" }'

  fresh_client(); dofile(PLUGIN); fire_timers()

  -- 1. the tab is part of THIS panel now, not a panel of its own
  local titles = {}
  for _, t in ipairs(panel.tabs) do titles[#titles + 1] = t.title end
  local joined = table.concat(titles, ",")
  check(joined:find("Skills", 1, true) ~= nil, "skills: the merge lands as a tab on the Viking Status panel")
  check(#aliases > 0 and vsk ~= nil, "skills: the vsk alias came along")

  -- 2. the login rescan waits for the feed, then fires
  connect()
  prompt(); prompt()
  check(#sent == 0, "skills: no login scan before the feed is alive")
  gmcp("Guild.State", GXP_MATCHING); fire_timers()
  sent = {}
  prompt()
  check(sent[1] == "vskills", "skills: the login scan fires once the feed speaks (got "
    .. tostring(sent[1]) .. ")")

  -- 3. the listing parses, and the pools pair themselves to the gxp tracks
  printed = {}
  feed_full()
  check(said("scan: 9 skills"), "skills: the rows are counted")
  check(said("pools matched to the feed"), "skills: the pairing is announced, not assumed")
  check(said("visindi = vitka"), "skills: Visindi's 57,117 is found on the vitka track")
  check(said("kappi = viga"), "skills: Kappi's 8,392 is found on the viga track")
  check((store["sw_pooltrack"] or ""):find("visindi=vitka", 1, true) ~= nil,
    "skills: the pairing is persisted (got " .. tostring(store["sw_pooltrack"]) .. ")")
  check((store["sw_pooltrack"] or ""):find("soemd=", 1, true) == nil,
    "skills: a pool that was never listed is not paired")

  -- 4. what the rows say, priced off the feed
  check(rows():find("Visindi 57,117", 1, true) ~= nil, "skills: the tree header carries the live pool")
  check(rows():find("need 18,883 visindi", 1, true) ~= nil,
    "skills: Seidgerd is short exactly 76,000-57,117")
  check(rows():find("need 52,408 kappi", 1, true) ~= nil,
    "skills: Eggmod is short 60,800-8,392")
  check(rows():find("Runakostur", 1, true) == nil, "skills: maxed skills are not listed")

  -- every row leads with an INFO cell carrying both actions, whether the skill
  -- is affordable or not - that is the whole point of it being a column
  do
    local afford, short = nil, nil
    for line in (rows() .. "\n"):gmatch("([^\n]*)\n") do
      if line:find("Hugarvit", 1, true) then afford = line end
      if line:find("Seidgerd", 1, true) then short = line end
    end
    check(afford ~= nil and short ~= nil, "skills: both an affordable and a short row are on show")
    for what, line in pairs({ ["an affordable row"] = afford, ["a short row"] = short }) do
      local name = line:find("Hugarvit", 1, true) and "Hugarvit" or "Seidgerd"
      check(line:find("}INFO", 1, true) ~= nil, "skills: " .. what .. " leads with an INFO cell")
      check(line:find(",click=vhelp " .. name .. "}", 1, true) ~= nil,
        "skills: " .. what .. "'s left click is vhelp, never vtrain")
      check(line:find(",menu=Info|vhelp " .. name .. ";Train|vtrain " .. name, 1, true) ~= nil,
        "skills: " .. what .. " offers Info and Train on the right button")
      check(line:find(",rclick=vhelp " .. name, 1, true) ~= nil,
        "skills: " .. what .. " keeps an rclick= fallback for a pre-1.19 host")
    end
    -- vtrain is reachable ONLY through the menu: it spends points and daler and
    -- must not sit under a stray left click
    check(short:find("click=vtrain", 1, true) == nil and afford:find("click=vtrain", 1, true) == nil,
      "skills: vtrain is never bound to a mouse button on its own")
    -- the menu value must stay comma-free, or a pre-1.19 host reads half of it
    -- as colour flags instead of ignoring the whole verb
    local mval = afford:match(",menu=([^,}]*)")
    check(mval ~= nil and mval:find(",", 1, true) == nil,
      "skills: the menu value carries no comma (pre-1.19 hosts parse specs on commas)")
    -- and the affordable row's shortfall column is empty, not the old word
    check(afford:find("TRAIN", 1, true) == nil, "skills: the TRAIN word is gone")
  end

  -- a name too long for the column is truncated for DISPLAY, but the command
  -- must carry it whole or vhelp is sent a name the game does not know
  do
    local long = nil
    for line in (rows() .. "\n"):gmatch("([^\n]*)\n") do
      if line:find("Voluvakunnustadr", 1, true) then long = line end
    end
    check(long ~= nil, "skills: the long-named skill is listed")
    check(long:find("Voluvakunnusta…", 1, true) ~= nil,
      "skills: ...with its name cut to the column width")
    check(long:find("click=vhelp Voluvakunnustadr}", 1, true) ~= nil,
      "skills: ...while the command carries the whole name (got "
      .. tostring(long and long:match("click=vhelp [^}]*")) .. ")")

    -- and the columns still line up. The ellipsis is one column but three bytes,
    -- so a byte-counting pad would silently shove "lvl" left on exactly the rows
    -- that were truncated - invisible to every other assertion here.
    local function strip(t) return (t:gsub("@%b{}", ""):gsub("@@", "@")) end
    local normal = nil
    for line in (rows() .. "\n"):gmatch("([^\n]*)\n") do
      if line:find("Seidgerd", 1, true) then normal = line end
    end
    -- measured in COLUMNS, not bytes: the ellipsis is three bytes wide and one
    -- column wide, and it is precisely that gap the padding has to close
    local function lvlcol(line)
      local t = strip(line)
      local i = t:find("lvl", 1, true)
      return i and utf8.len(t, 1, i - 1) or nil
    end
    -- ...and there is still a gap. Alignment alone cannot see this: drop the
    -- separator and BOTH rows crowd equally, which is what the split into cells
    -- actually did the first time round.
    check(strip(long):find("… +lvl") ~= nil,
      "skills: a truncated name keeps a space before the lvl column")
    check(normal ~= nil and lvlcol(long) == lvlcol(normal),
      "skills: a truncated name does not shift the lvl column (long "
      .. tostring(lvlcol(long)) .. " vs normal " .. tostring(normal and lvlcol(normal)) .. ")")
  end
  -- the listing's own header says 1,238,996 and the feed says 2,222,222: the
  -- feed has to win, or this assertion could not tell the two sources apart
  check(summary():find("2,222,222", 1, true) ~= nil, "skills: daler comes from Guild.State.daler")
  check(summary():find("1,238,996", 1, true) == nil, "skills: ...not from the listing header")

  -- 5. THE POINT OF THE MERGE: a gxp burst moves a row with no rescan at all.
  gmcp("Guild.State", (GXP_MATCHING:gsub('"vitka": 57117', '"vitka": 100000')))
  fire_timers()
  check(rows():find("need 18,883", 1, true) == nil,
    "skills: 100k on the vitka track clears Seidgerd without a rescan")
  check(rows():find("Visindi 100,000", 1, true) ~= nil,
    "skills: ...and the header follows the track it was paired to")

  -- 6. alerts fire on the transition into affordable, not out of it
  vsk("alert on")
  notifies = {}
  gmcp("Guild.State", (GXP_MATCHING:gsub('"viga": 8392', '"viga": 100')))
  fire_timers()
  check(#notifies == 0, "skills: losing affordability does not notify")
  gmcp("Guild.State", (GXP_MATCHING:gsub('"viga": 8392', '"viga": 100000')))
  fire_timers()
  check(#notifies == 1 and notifies[1]:find("Eggmod", 1, true) ~= nil,
    "skills: a skill becoming trainable notifies once (got " .. #notifies .. ")")
  vsk("alert off")

  -- 7. `vsk feed` reports the REAL source per pool, including the unpaired ones
  printed = {}
  vsk("feed")
  check(said("Guild.State.daler"), "skills: feed names the daler source")
  check(said("gxp.vitka"), "skills: feed names the paired track as the source")

  -- 8. a rescan splices one tree back without wiping the others
  printed = {}
  command("vskills berserkja")
  feed_berserkja(); feed_footer()
  check(rows():find("Vegr Vitka", 1, true) ~= nil, "skills: a one-tree rescan keeps the other trees")
  -- a pairing is made once and kept: a rescan of an already-paired tree must not
  -- announce it again (and must not re-derive it - a pool total that has drifted
  -- into some other track's number would otherwise silently re-point the pool)
  check(not said("pools matched to the feed"),
    "skills: a rescan of a paired tree does not re-pair or re-announce")

  -- 9. a listing that parses to nothing keeps the last one
  printed = {}
  scrye.send("vskills")
  L("What?")
  prompt()
  check(said("nothing in it parsed"), "skills: an unparseable reply says so")
  check(rows():find("Hugarvit", 1, true) ~= nil, "skills: ...and keeps the previous scan")

  -- what a restart will be handed, taken before the two throwaway clients below
  local saved = {}
  for k, v in pairs(store) do saved[k] = v end

  -- 10. an AMBIGUOUS number pairs nothing. Two tracks holding the pool's total
  -- is exactly the case where a "close enough" or "first match wins" pairing
  -- would invent a wrong answer, and a wrong pairing is worse than none: the
  -- pool would then price every row off another path's points.
  do
    fresh_client(); dofile(PLUGIN); fire_timers()
    gmcp("Guild.State", '{ "gxp": { "buandi": 57117, "buandi_max": 59100, '
      .. '"drotta": 57117, "drotta_max": 59100, '
      .. '"viga": 8392, "viga_max": 59100, '
      .. '"vitka": 57117, "vitka_max": 59100 }, '
      .. '"daler": 2222222, "full": 1, "pages": 1, "page": 1, "guild": "viking" }')
    fire_timers()
    printed = {}
    command("vskills")            -- the capture opens on the command, not the echo
    feed_full()
    check((store["sw_pooltrack"] or ""):find("visindi=", 1, true) == nil,
      "skills: three tracks carry Visindi's total, so it pairs with none of them")
    check((store["sw_pooltrack"] or ""):find("kappi=viga", 1, true) ~= nil,
      "skills: ...while the unambiguous pool in the same scan still pairs")
    check(rows():find("Visindi 57,117", 1, true) ~= nil,
      "skills: an unpaired pool falls back to the number the listing printed")
  end

  -- 11. a paired track beats the MIP-era vik.* key. Both are present here and
  -- they disagree; under GMCP the feed is the truth, and the old path is only a
  -- fallback for a host that still speaks MIP.
  do
    fresh_client(); dofile(PLUGIN); fire_timers()
    gmcp("Guild.State", GXP_MATCHING); fire_timers()
    command("vskills")
    feed_full()
    scrye.setState("vik.vis", "5")
    check(rows():find("Visindi 57,117", 1, true) ~= nil,
      "skills: the paired gxp track wins over a stale vik.* key")
  end

  -- 12. persistence across a restart, and the pairing survives it
  fresh_client(); for k, v in pairs(saved) do store[k] = v end
  printed = {}
  dofile(PLUGIN); fire_timers()
  check(rows():find("Hugarvit", 1, true) ~= nil, "skills: a restart reloads the last scan")
  gmcp("Guild.State", GXP_MATCHING); fire_timers()
  check(rows():find("Visindi 57,117", 1, true) ~= nil,
    "skills: the learned pairing survived the restart (no rescan needed)")
end

-- =====================================================================
-- Guild.Map belongs to 3s-viking-world. When the travel engine moved out on
-- 30 Aug the adapter was left behind here, still writing into a gmap table that
-- had gone with it - so every Guild.Map burst raised a rule error in this
-- plugin. Nothing observable in the tabs changed, which is exactly why no test
-- caught it; this one watches the subscription itself.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  local subscribed = false
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Guild.Map" then subscribed = true end end
  check(not subscribed, "map: the settlement half does not subscribe to Guild.Map")
  -- and if one arrives anyway (a stale build, a re-registration), nothing throws
  local ok = pcall(gmcp, "Guild.Map",
    '{ "east": [ "111", "101" ], "w": 4, "h": 3, "pos": { "x": 1, "y": 1 }, "full": 1, "pages": 1, "page": 1, "guild": "viking" }')
  check(ok, "map: a Guild.Map burst is inert here rather than an error")
end

-- =====================================================================
-- The trade ledger's TIMING. A sell is not money until its cart docks - the game
-- pays on arrival, and confirmed live 30 Aug a cart cannot come home part sold, so
-- there is exactly one payment per cart and it happens at the dock. A buy pays at
-- the yard, so it still logs on dispatch. Nothing here asserted the log at all
-- before (the cartyard tests count SENDS), which is how the whole ledger stayed
-- untested while its timing was wrong.
do
  local function world()
    fresh_client(); dofile(PLUGIN); fire_timers()
    gmcp("Guild.State", '{ "daler": 900000, "guild": "viking" }')
    alias("^mkref$")
    for _, l in ipairs({
      "-~*   Bread - Market Overview                       *~-",
      "-~*  Nidaros          25  800 wants import Vinur    *~-",
      "-~*   Honey - Market Overview                       *~-",
    }) do for _, h in ipairs(line_hooks) do h(l) end end
    fire_timers()
    gmcp("Guild.Warehouse", '{ "wstock": [ { "pct": 100, "amount": 466, "grade": "hardtack", "good": "bread" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
    gmcp("Guild.Warehouse", '{ "wstock_cap": 8369, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
    fire_timers()
    gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 310, "refit": "speed", "tier": 5 } ], "guild": "viking" }')
    fire_timers()
  end
  local function carts(json)
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "carts": [ ' .. json .. ' ], "guild": "viking" }')
    fire_timers()
  end
  local CART9 = '{ "cart_id": 9, "mode": "sell", "good": "bread", "village": "Nidaros", "secs": 900, "cap": 310 }'
  local function log() return tostring(state["plugin.3s-viking-status-gmcp.atlog"] or "") end
  local function sold_rows()
    local n = 0
    for line in (log() .. "\n"):gmatch("([^\n]*)\n") do
      if line:find("SELL", 1, true) and line:find("~", 1, true) then n = n + 1 end
    end
    return n
  end

  world()
  alias("^atrade (.+)$", "on"); fire_timers()
  carts("")                       -- baseline: nothing out
  carts(CART9)                    -- the dispatched cart appears, so the send is confirmed

  check(sold_rows() == 0, "ledger: a sell on the road is NOT in the log yet")
  check(log():find("on the road: 1 sell cart", 1, true) ~= nil,
    "ledger: ...it is shown as on the road, so it cannot look forgotten")
  check(log():find("sold 0", 1, true) ~= nil,
    "ledger: ...and the session total has not counted it")
  check(store["at_road"] ~= nil and store["at_road"] ~= "",
    "ledger: the cart in flight is persisted, so a restart does not lose the sale")

  -- the cart docks
  carts("")
  check(sold_rows() == 1, "ledger: the sale is logged when the cart docks")
  check(log():find("on the road", 1, true) == nil, "ledger: ...and leaves the road list")
  check(log():find("sold 1", 1, true) ~= nil, "ledger: ...and the daler lands in the session total")
  check((store["at_road"] or "") == "", "ledger: ...and the persisted road is cleared")

  -- a BUY still logs at dispatch: the reserve is spent at the yard, not on return.
  -- Honey is raw and absent from the warehouse, so the restocker buys it - the same
  -- setup the restock test above uses, because contriving a buy any other way would
  -- test the fixture rather than the trader.
  do
    world()
    alias("^mkref$")
    for _, l in ipairs({
      "-~*   Honey - Market Overview                       *~-",
      "-~*  Lodbrok's Hol    10  500 avail export++ Vinur  *~-",
      "-~*  Nidaros          25   80 wants import Vinur    *~-",
    }) do for _, h in ipairs(line_hooks) do h(l) end end
    fire_timers()
    sent = {}
    alias("^atrade (.+)$", "restock on")
    alias("^atrade (.+)$", "on"); fire_timers()
    local sent_buy = false
    for _, c in ipairs(sent) do if c:match("^vtrade dispatch buy") then sent_buy = true end end
    check(sent_buy, "ledger: (the restocker sends a buy cart to log)")
    carts("")
    carts('{ "cart_id": 11, "mode": "buy", "good": "honey", "village": "Lodbrok", "secs": 900, "cap": 310 }')
    local buys = 0
    for line in (log() .. "\n"):gmatch("([^\n]*)\n") do
      if line:find("BUY", 1, true) then buys = buys + 1 end
    end
    check(buys >= 1, "ledger: a buy is logged the moment its cart leaves (got " .. buys .. ")")
    check(log():find("on the road", 1, true) == nil,
      "ledger: ...and a buy never sits on the road waiting to be paid")
  end

  -- a cart still out when the plugin stops is picked up again on the next load,
  -- and settled by the first carts burst rather than dropped
  do
    world()
    alias("^atrade (.+)$", "on"); fire_timers()
    carts(""); carts(CART9)
    check(sold_rows() == 0, "restart: the sale is parked before the restart")
    local saved = {}
    for k, v in pairs(store) do saved[k] = v end

    -- reload with the cart STILL out: it must keep waiting, not log twice
    fresh_client(); for k, v in pairs(saved) do store[k] = v end
    printed = {}
    dofile(PLUGIN); fire_timers()
    check(said("still out last time"), "restart: the in-flight cart is announced")
    carts(CART9)
    check(sold_rows() == 0, "restart: a cart still on the road is not logged on sight")
    carts("")
    check(sold_rows() == 1, "restart: it is logged when it finally docks")

    -- reload with the cart already GONE: it docked while we were away
    fresh_client(); for k, v in pairs(saved) do store[k] = v end
    dofile(PLUGIN); fire_timers()
    carts("")
    check(sold_rows() == 1, "away: a cart that docked while Scrye was closed is still logged")
    check(log():find("docked while away", 1, true) ~= nil,
      "away: ...and marked, because we cannot know when it landed")
  end
end



-- =====================================================================
-- ORE IS A RAW MATERIAL (2.22.0)
--
-- Ore was raw only by OMISSION. Absent from REFINED and SPECIAL it already kept
-- the raw reserve when SELLING, which made it look classified; absent from
-- RAWBUILD it was never bought back. Nothing failed - the restocker quietly did
-- nothing - so the assertion has to be the BUY, not the reserve.
--
-- Its own fixture rather than a second good in the honey scenario: the
-- restocker sends one buy per pass, so two raws sharing a pass let either one
-- hide the other. (Tried it both ways round; each time the good that lost the
-- slot failed and the test said nothing useful about the one that won.)
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  alias("^atrade (.+)$", "yard 0")
  gmcp("Guild.State", '{ "daler": 900000, "guild": "viking" }')
  alias("^mkref$")
  for _, l in ipairs({
    "-~*   Ore - Market Overview                         *~-",
    "-~*  Lodbrok's Hol    12  400 avail export++ Vinur  *~-",
    "-~*  Nidaros          30   60 wants import Vinur    *~-",
    "-~*   Honey - Market Overview                       *~-",
  }) do for _, h in ipairs(line_hooks) do h(l) end end
  fire_timers()
  feed_stock_report()          -- no ore in the warehouse -> have 0 < Raw>
  fire_timers()
  gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 4, "id": "mine" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
  fire_timers()
  sent = {}
  alias("^atrade (.+)$", "restock on")
  alias("^atrade (.+)$", "on")
  fire_timers()
  local ore_buy = false
  for _, c in ipairs(sent) do
    if c:match("^vtrade dispatch buy %d+ ore lodbrok") then ore_buy = true end
  end
  check(ore_buy, "restock: the auto-buyer sends an ore buy cart (ore is RAWBUILD)")
  alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "restock off"); fire_timers()
end


-- ...and the SELL side of the same claim, which the buy above is blind to:
-- RAWBUILD is consulted independently of the reserve, so classing ore as
-- REFINED or as SPECIAL leaves the buy test perfectly green while changing what
-- the trader HOLDS BACK. Both of those mutants survived until this existed.
--
-- Stock comes from Guild.Warehouse, not the text scan - one burst, no shared
-- fixture touched. (The first attempt went hunting for a way to add ore to the
-- canned STOCK_REPORT and concluded that fixture had to be split per-phase
-- first, on the strength of its own header calling the scan a stopgap. The
-- header was months stale. Joakim caught it.)
--
-- reserve = max(keep 20, Raw> 300) for a raw material, but only keep 20 for a
-- refined or special one - and since 2.24.0 a RAW also needs a whole cart of
-- surplus before it sells at all. So 605 units of ore (305 above Raw>, under the
-- 310 cart) sells NOTHING if ore is raw, and a full cart if it is not: the cart
-- going out or not is the discriminator. 620 (a cart and a bit above Raw>) sells
-- exactly the cart, leaving 310 - still above the buffer.
do
  local function ore_sells(units)
    fresh_client(); dofile(PLUGIN); fire_timers()
    alias("^atrade (.+)$", "yard 0")
    gmcp("Guild.State", '{ "daler": 900000, "guild": "viking" }')
    alias("^mkref$")
    for _, l in ipairs({
      "-~*   Ore - Market Overview                         *~-",
      "-~*  Nidaros          30  800 wants import Vinur    *~-",
      "-~*   Honey - Market Overview                       *~-",
    }) do for _, h in ipairs(line_hooks) do h(l) end end
    fire_timers()
    gmcp("Guild.Warehouse", string.format(
      '{ "wstock": [ { "pct": 100, "amount": %d, "grade": "raw", "good": "ore" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }', units))
    gmcp("Guild.Warehouse", '{ "wstock_cap": 8369, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
    gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 310, "refit": "speed", "tier": 5 } ], "guild": "viking" }')
    fire_timers()
    sent = {}
    alias("^atrade (.+)$", "restock off")   -- sells only: no buy cart can be mistaken for one
    alias("^atrade (.+)$", "on")
    fire_timers()
    -- the FIRST ore cart: the fixture never lists a sent cart back, so later passes
    -- repeat against a warehouse that has not moved (the market demand does debit)
    local qty
    for _, c in ipairs(sent) do
      local q = c:match("^vtrade dispatch sell (%d+) ore ")
      if q and not qty then qty = tonumber(q) end
    end
    alias("^atrade (.+)$", "off"); fire_timers()
    return qty
  end

  local qty = ore_sells(605)
  check(qty == nil,
    "reserve: 605 ore is 305 above Raw>, under a cart - a raw sells nothing (a refined good "
    .. "would have shipped a cart; got " .. tostring(qty) .. ")")
  qty = ore_sells(620)
  check(qty == 310,
    "reserve: 620 ore sells exactly one cart, the pile stays above Raw> (got " .. tostring(qty) .. ")")
end

-- =====================================================================
-- RAWS MOVE IN WHOLE CARTS (2.24.0, Joakim 3 Sep). Live, 2 Sep: ore 187 in the
-- warehouse, Raw> 300, the restocker bought a FULL cart (313) - then the very next
-- pass sold the 200 it had overshot by to another town. 2.23.1 answered by buying
-- the deficit, which made restocks one-unit carts for 22 minutes of yard time each.
-- Now the minimum is a TRIGGER: under it, a whole cart is bought (capped by the
-- town's supply, the budget and space - a partial cart still goes); and the sell
-- side is where the churn is stopped: a raw is sold only when a whole cart of
-- surplus sits above the minimum, so the cart just bought is never shipped back.
-- A raw with a buy cart on the road is still not sold while it is inbound.
do
  local function setup(units, carts)
    fresh_client(); dofile(PLUGIN); fire_timers()
    alias("^atrade (.+)$", "yard 0")
    gmcp("Guild.State", '{ "daler": 900000, "guild": "viking" }')
    alias("^mkref$")
    for _, l in ipairs({
      "-~*   Ore - Market Overview                         *~-",
      "-~*  Lodbrok's Hol    12  400 avail export++ Vinur  *~-",
      "-~*  Nidaros          30  800 wants import Vinur    *~-",
      "-~*   Honey - Market Overview                       *~-",
    }) do for _, h in ipairs(line_hooks) do h(l) end end
    fire_timers()
    gmcp("Guild.Warehouse", string.format(
      '{ "wstock": [ { "pct": 100, "amount": %d, "grade": "raw", "good": "ore" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }', units))
    gmcp("Guild.Warehouse", '{ "wstock_cap": 8369, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
    gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 313, "refit": "speed", "tier": 5 } ]'
      .. (carts and (', "carts": ' .. carts) or "") .. ', "guild": "viking" }')
    alias("^atrade (.+)$", "scalp off")     -- the scalper would buy ore too (12 -> 30); this is about restock
    fire_timers()
    sent = {}
  end
  -- the FIRST ore dispatch of a kind: the fixture never lists a sent cart back in
  -- Guild.Trade, so later passes cannot see it inbound and repeat - live, the feed
  -- carries the cart and the inbound guard holds. The first decision is the one under test.
  local function dispatches(kind)
    for _, c in ipairs(sent) do
      local n = c:match("^vtrade dispatch " .. kind .. " (%d+) ore ")
      if n then return tonumber(n) end
    end
    return nil
  end

  -- 187 in stock, Raw> 300: a whole cart (313), not the 113 gap
  setup(187)
  alias("^atrade (.+)$", "restock on"); alias("^atrade (.+)$", "on"); fire_timers()
  check(dispatches("buy") == 313, "restock buys a WHOLE cart (313), not the deficit (got " .. tostring(dispatches("buy")) .. ")")
  check(said("a whole cart: lands at 500"), "...and says where the pile lands")
  alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "restock off"); fire_timers()

  -- one unit under the minimum earns a whole cart; AT the minimum, nothing
  setup(299)
  alias("^atrade (.+)$", "restock on"); alias("^atrade (.+)$", "on"); fire_timers()
  check(dispatches("buy") == 313, "one unit under Raw> is a whole cart (got " .. tostring(dispatches("buy")) .. ")")
  alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "restock off"); fire_timers()
  setup(300)
  alias("^atrade (.+)$", "restock on"); alias("^atrade (.+)$", "on"); fire_timers()
  check(dispatches("buy") == nil, "at Raw> nothing is bought: the minimum is a trigger, not a target")
  alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "restock off"); fire_timers()

  -- the town only has 180: the partial cart still goes (Joakim, 3 Sep)
  setup(187)
  alias("^mkref$")
  for _, l in ipairs({
    "-~*   Ore - Market Overview                         *~-",
    "-~*  Lodbrok's Hol    12  180 avail export++ Vinur  *~-",
    "-~*  Nidaros          30  800 wants import Vinur    *~-",
    "-~*   Honey - Market Overview                       *~-",
  }) do for _, h in ipairs(line_hooks) do h(l) end end
  fire_timers(); sent = {}
  alias("^atrade (.+)$", "restock on"); alias("^atrade (.+)$", "on"); fire_timers()
  check(dispatches("buy") == 180, "a town with 180 to sell gets a 180 cart - partial beats empty (got " .. tostring(dispatches("buy")) .. ")")
  alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "restock off"); fire_timers()

  -- ore with a BUY cart inbound is not sold, however much is above the buffer
  setup(500, '[ { "durability": 100, "escort": 5, "horses": 6, "amount": 313, "half_in": 0, "cart_id": 9, "cap": 313, "tier": 5, "good": "ore", "mode": "buy", "quality_pct": 100, "secs": 1000, "refit": "speed", "village": "Lodbrok\'s Hold" } ]')
  alias("^atrade (.+)$", "on"); fire_timers()
  check(dispatches("sell") == nil, "a raw being restocked is not sold while the buy cart is on the road")
  alias("^atrade (.+)$", "off"); fire_timers()
  -- ...and with no cart inbound, 200 above the buffer is UNDER a cart: the pile a
  -- restock just made (187 + 313 = 500) is left alone - this is where the churn ends
  setup(500)
  alias("^atrade (.+)$", "on"); fire_timers()
  check(dispatches("sell") == nil, "500 ore (200 above Raw>) is not sold: under a whole cart of surplus (got " .. tostring(dispatches("sell")) .. ")")
  alias("^atrade (.+)$", "off"); fire_timers()
  -- the band is [min, min + cart): 612 stays, 613 ships one cart and lands on 300
  setup(612)
  alias("^atrade (.+)$", "on"); fire_timers()
  check(dispatches("sell") == nil, "612 ore (312 above, one short of the 313 cart) stays")
  alias("^atrade (.+)$", "off"); fire_timers()
  setup(613)
  alias("^atrade (.+)$", "on"); fire_timers()
  check(dispatches("sell") == 313, "613 ore sells one whole cart and lands exactly on Raw> (got " .. tostring(dispatches("sell")) .. ")")
  alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "scalp on"); fire_timers()
end

-- =====================================================================
-- THE 40-ORE CART (2.23.1, from the 2 Sep 23:27 capture: ore sells at 5-6 daler, finery
-- at 301 with 196 wanted in Midgard). Ore at 500 read as a FLUSH pile (>= 500) and jumped
-- the queue past the value floor, though only 200 of it was sellable above Raw>. Flush is
-- measured on the sellable surplus now; and under warehouse PRESSURE the biggest cart
-- wins, as the status line always claimed.
do
  local function setup(fill)
    fresh_client(); dofile(PLUGIN); fire_timers()
    alias("^atrade (.+)$", "yard 0")
    alias("^atrade (.+)$", "scalp off"); alias("^atrade (.+)$", "restock off")
    gmcp("Guild.State", '{ "daler": 900000, "guild": "viking" }')
    -- the feed's own rows: ore (o) and finery (n), Midgard and Lodbrok, dem as captured
    gmcp("Guild.TradeGoods", '{ "pages": 1, "page": 1, "lin": 0, "guild": "viking", "goods": [ { "buy": 4, "lin": 0, "dem": 61, "sell": 5, "score": -2, "sup": 0, "good": "o" }, { "buy": 250, "lin": 0, "dem": 196, "sell": 301, "score": 1, "sup": 0, "good": "n" } ] }')
    gmcp("Guild.TradeGoods", '{ "pages": 1, "page": 1, "lin": 1, "guild": "viking", "goods": [ { "buy": 5, "lin": 1, "dem": 140, "sell": 6, "score": -2, "sup": 0, "good": "o" }, { "buy": 230, "lin": 1, "dem": 143, "sell": 273, "score": 1, "sup": 0, "good": "n" } ] }')
    fire_timers()
    -- ore 500 (200 above Raw>), finery 100 (80 above keep); the fill decides the mode
    local wcap = 8440
    gmcp("Guild.Warehouse", '{ "wstock": [ { "pct": 100, "amount": 500, "grade": "raw", "good": "ore" }, { "pct": 100, "amount": 100, "grade": "regalia", "good": "finery" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
    gmcp("Guild.Warehouse", '{ "wstock_cap": ' .. wcap .. ', "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
    gmcp("Guild.City", '{ "pages": 1, "page": 1, "buildings": [ { "tier": 5, "id": "trading_post" }, { "tier": 5, "id": "warehouse" }, { "tier": 5, "id": "dock" } ], "guild": "viking" }')
    gmcp("Guild.Trade", '{ "pages": 1, "page": 1, "cidle": [ { "durability": 100, "horses": 4, "slot": 1, "cap": 313, "refit": "speed", "tier": 5 } ], "guild": "viking" }')
    fire_timers()
    sent = {}
  end
  local function first_sell()
    for _, c in ipairs(sent) do
      local q, g = c:match("^vtrade dispatch sell (%d+) (%a+) ")
      if q then return g, tonumber(q) end
    end
    return nil
  end

  -- normal mode: finery's cart is worth ~50x the ore's; ore's 200 surplus is no flush
  -- pile, so the value floor refuses it and finery goes first
  setup()
  alias("^atrade (.+)$", "on"); fire_timers()
  local g, q = first_sell()
  check(g == "finery", "flush reads the sellable surplus: 500 ore with 300 kept is not a flush pile, finery (80 x 301) goes first (got " .. tostring(g) .. " " .. tostring(q) .. ")")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- ...but a real surplus of 500 still flushes: 800 ore has 500 to sell
  setup()
  gmcp("Guild.Warehouse", '{ "wstock": [ { "pct": 100, "amount": 800, "grade": "raw", "good": "ore" }, { "pct": 100, "amount": 100, "grade": "regalia", "good": "finery" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
  gmcp("Guild.Warehouse", '{ "wstock_cap": 8440, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
  fire_timers(); sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  g, q = first_sell()
  check(g == "ore" and q == 140, "a 500-unit SURPLUS flushes: ore first, to the town wanting most (got " .. tostring(g) .. " " .. tostring(q) .. ")")
  alias("^atrade (.+)$", "off"); fire_timers()

  -- under pressure (84% full, as live) the biggest cart wins: finery 80 vs ore 140 -> ore,
  -- and that is the point of pressure - space, not daler
  setup()
  gmcp("Guild.Warehouse", '{ "wstock_cap": 700, "full": 1, "pages": 2, "guild": "viking", "page": 2 }')   -- 600/700 = 85%
  fire_timers(); sent = {}
  alias("^atrade (.+)$", "on"); fire_timers()
  g, q = first_sell()
  check(g == "ore" and q == 140, "under pressure the biggest cart goes first, whatever it is worth (got " .. tostring(g) .. " " .. tostring(q) .. ")")
  alias("^atrade (.+)$", "off"); fire_timers()
end

-- =====================================================================
-- LIVESTOCK TAB (2.23.0). Payloads verbatim from the 2 Sep 2026 capture: an 8-page
-- full burst (queue on page 1, herds over pages 2-3, feed + empty finds on 4, the
-- lineage-1 market over 5-8), then a 6-page UNPAGED burst that brings the queue
-- again and a second lineage's market (lineage 10). Pages renumbered only where the
-- report's sample was partial, since the assembler publishes complete bursts only.
--
-- What this protects: herds CONCATENATE across their two pages (four buildings,
-- 89 head, not two); the market keeps BOTH lineages after the unpaged burst; a
-- market stat is marked green only when it beats your best herd of that species;
-- the breeding queue reads slot order and its countdown; and a later FULL burst
-- drops a lineage it no longer carries.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  check(st("livestock"):find("waiting for Guild.Livestock", 1, true) ~= nil, "livestock: honest before the feed")
  local titles = {}
  for _, t in ipairs(panel.tabs) do titles[#titles + 1] = t.title end
  check(table.concat(titles, ","):find("Holds,Livestock,Trade", 1, true) ~= nil,
    "livestock: the tab sits between Holds and Trade (" .. table.concat(titles, ",") .. ")")

  local LS_P1 = '{ "bqueue_max": 2, "full": 1, "pages": 8, "bqueue": [ { "trait": "purebred", "secs": 1840, "meat": "poultry", "qty": 1, "slot": 2, "species": "chicken" }, { "trait": "bountiful", "secs": 1840, "meat": "pork", "qty": 1, "slot": 1, "species": "pig" } ], "bqueue_used": 2, "page": 1, "guild": "viking" }'
  local LS_P2 = '{ "full": 1, "pages": 8, "guild": "viking", "herds": [ { "trait": "bountiful", "age_ticks": 59, "con": 51, "quality": 45, "yield": 30, "breed": "gotland_russ", "vigor": 60, "hv": 1, "bldg": "stable", "hard": 59, "sterile": 0, "gen": 10, "head": 22, "fert": 26 }, { "trait": "bountiful", "age_ticks": 40, "con": 50, "quality": 40, "yield": 41, "breed": "spelsau", "vigor": 26, "hv": 1, "bldg": "sheepfold", "hard": 47, "sterile": 0, "gen": 8, "head": 19, "fert": 39 } ], "page": 2 }'
  local LS_P3 = '{ "full": 1, "pages": 8, "guild": "viking", "herds": [ { "trait": "bountiful", "age_ticks": 51, "con": 53, "quality": 47, "yield": 51, "breed": "norse_landrace", "vigor": 32, "hv": 0, "bldg": "piggery", "hard": 55, "sterile": 0, "gen": 10, "head": 16, "fert": 44 }, { "trait": "purebred", "age_ticks": 52, "con": 58, "quality": 43, "yield": 25, "breed": "orust", "vigor": 39, "hv": 1, "bldg": "henhouse", "hard": 40, "sterile": 0, "gen": 15, "head": 32, "fert": 57 } ], "page": 3 }'
  local LS_P4 = '{ "lfind_posts": [ ], "full": 1, "lfind_offers": [ ], "pages": 8, "lfind_auctions": [ ], "page": 4, "guild": "viking", "lfeed": { "head": 89, "water": 8, "grain": 8 } }'
  local LS_P5 = '{ "full": 1, "pages": 8, "guild": "viking", "lmarket_1": [ { "lin": 1, "trait": "0", "con": 34, "breed": "landnam", "yield": 24, "count": 3, "vigor": 29, "hard": 31, "price": 165, "idx": 0, "fert": 40, "species": "chicken" }, { "lin": 1, "trait": "0", "con": 34, "breed": "landnam", "yield": 46, "count": 2, "vigor": 36, "hard": 27, "price": 156, "idx": 1, "fert": 55, "species": "chicken" }, { "lin": 1, "trait": "0", "con": 37, "breed": "landnam", "yield": 20, "count": 2, "vigor": 30, "hard": 32, "price": 124, "idx": 2, "fert": 38, "species": "chicken" } ], "page": 5 }'
  local LS_P6 = '{ "full": 1, "pages": 8, "guild": "viking", "lmarket_1": [ { "lin": 1, "trait": "0", "con": 27, "breed": "landnam", "yield": 23, "count": 3, "vigor": 36, "hard": 28, "price": 165, "idx": 3, "fert": 45, "species": "chicken" }, { "lin": 1, "trait": "0", "con": 35, "breed": "landnam", "yield": 17, "count": 1, "vigor": 37, "hard": 33, "price": 66, "idx": 4, "fert": 43, "species": "chicken" }, { "lin": 1, "trait": "0", "con": 41, "breed": "landnam", "yield": 19, "count": 2, "vigor": 29, "hard": 30, "price": 132, "idx": 5, "fert": 48, "species": "chicken" } ], "page": 6 }'
  local LS_P7 = '{ "full": 1, "pages": 8, "guild": "viking", "lmarket_1": [ { "lin": 1, "trait": "prolific", "con": 36, "breed": "landnam", "yield": 20, "count": 1, "vigor": 20, "hard": 30, "price": 78, "idx": 6, "fert": 37, "species": "chicken" }, { "lin": 1, "trait": "prolific", "con": 43, "breed": "landnam", "yield": 19, "count": 3, "vigor": 38, "hard": 47, "price": 300, "idx": 7, "fert": 55, "species": "chicken" }, { "lin": 1, "trait": "0", "con": 63, "breed": "spelsau", "yield": 37, "count": 2, "vigor": 27, "hard": 43, "price": 296, "idx": 8, "fert": 15, "species": "sheep" } ], "page": 7 }'
  local LS_P8 = '{ "full": 1, "pages": 8, "guild": "viking", "lmarket_1": [ { "lin": 1, "trait": "0", "con": 41, "breed": "spelsau", "yield": 35, "count": 1, "vigor": 13, "hard": 39, "price": 120, "idx": 9, "fert": 26, "species": "sheep" } ], "page": 8 }'
  for _, pg in ipairs({ LS_P1, LS_P2, LS_P3, LS_P4, LS_P5, LS_P6, LS_P7, LS_P8 }) do gmcp("Guild.Livestock", pg) end
  fire_timers()
  local ls = st("livestock")

  -- herds concatenate: 22+19+16+32 = 89 head in FOUR buildings, not the last page's two
  check(ls:find("89 head in 4 building(s)", 1, true) ~= nil, "livestock: herds concatenate across pages 2-3 (89 head, 4 buildings)")
  check(ls:find("stable", 1, true) ~= nil and ls:find("henhouse", 1, true) ~= nil, "livestock: buildings from both pages are listed")
  check(ls:find("gotland russ", 1, true) ~= nil, "livestock: breed underscores read as spaces")
  check(ls:find("harvest", 1, true) ~= nil, "livestock: hv=1 shows as harvest")
  check(ls:find("STERILE", 1, true) == nil, "livestock: no herd is sterile, so the word is absent")
  check(ls:find("45%", 1, true) ~= nil and ls:find("47%", 1, true) ~= nil, "livestock: quality as a percent")
  check(ls:find("Feed 8", 1, true) ~= nil and ls:find("(89 head)", 1, true) ~= nil, "livestock: feed line from lfeed")

  -- the breeding queue, in slot order, with the countdown
  local s1 = ls:find("slot 1  pig x1", 1, true)
  local s2 = ls:find("slot 2  chicken x1", 1, true)
  check(s1 and s2 and s1 < s2, "livestock: queue rows in slot order, slot 1 (pig) before slot 2 (chicken)")
  check(ls:find("2 of 2 slot(s)", 1, true) ~= nil, "livestock: queue slots used/max")
  check(ls:find("30m 40s", 1, true) ~= nil, "livestock: 1840 s reads as 30m 40s")
  check(ls:find("(pork)", 1, true) ~= nil, "livestock: the meat a queued animal gives")

  -- the market: lineage 1 is Lodbrok's Hold, ten lots, versus-your-herd colouring
  check(ls:find("Market: Lodbrok's Hold (10 lots)", 1, true) ~= nil, "livestock: lineage 1 named and counted")
  -- lot idx 1 (chicken, fert 55) beats the henhouse's fert 57? no - 55 < 57 stays dim;
  -- lot idx 8 (sheep, con 63) beats the sheepfold's con 50 -> green
  local sheep_row
  for line in (ls .. "\n"):gmatch("([^\n]*)\n") do if line:find("spelsau", 1, true) and line:find("296", 1, true) then sheep_row = line end end
  check(sheep_row ~= nil and sheep_row:find("@{success} 63@{}", 1, true) ~= nil, "livestock: a lot's con 63 beats your sheep's 50 and is green")
  check(sheep_row ~= nil and sheep_row:find("@{dim} 15@{}", 1, true) ~= nil, "livestock: ...its fert 15 does not beat 39 and is dim")
  check(ls:find("green: better than your best herd", 1, true) ~= nil, "livestock: the legend line")
  check(ls:find("find(s)", 1, true) == nil, "livestock: empty finds are not mentioned")

  -- an UNPAGED burst adds a second lineage's market and keeps the first
  local LS_U1 = '{ "pages": 3, "bqueue": [ { "trait": "purebred", "secs": 1820, "meat": "poultry", "qty": 1, "slot": 2, "species": "chicken" }, { "trait": "bountiful", "secs": 1820, "meat": "pork", "qty": 1, "slot": 1, "species": "pig" } ], "page": 1, "guild": "viking" }'
  local LS_U2 = '{ "pages": 3, "page": 2, "lmarket_10": [ { "lin": 10, "trait": "0", "con": 53, "breed": "icelandic_horse", "yield": 24, "count": 2, "vigor": 58, "hard": 47, "price": 1400, "idx": 0, "fert": 30, "species": "horse" }, { "lin": 10, "trait": "0", "con": 28, "breed": "manx", "yield": 48, "count": 1, "vigor": 24, "hard": 46, "price": 136, "idx": 1, "fert": 26, "species": "sheep" } ], "guild": "viking" }'
  local LS_U3 = '{ "pages": 3, "page": 3, "lmarket_10": [ { "lin": 10, "trait": "0", "con": 38, "breed": "manx", "yield": 43, "count": 2, "vigor": 27, "hard": 37, "price": 328, "idx": 3, "fert": 61, "species": "sheep" }, { "lin": 10, "trait": "0", "con": 50, "breed": "manx", "yield": 41, "count": 1, "vigor": 26, "hard": 47, "price": 100, "idx": 4, "fert": 39, "species": "sheep" } ], "guild": "viking" }'
  -- (the idx-4 lot is the capture's shape with every stat set EQUAL to the sheepfold's:
  -- equal is not better, so none of its numbers may be green)
  gmcp("Guild.Livestock", LS_U1); gmcp("Guild.Livestock", LS_U2); gmcp("Guild.Livestock", LS_U3)
  fire_timers()
  ls = st("livestock")
  check(ls:find("Market: Lodbrok's Hold (10 lots)", 1, true) ~= nil, "livestock: lineage 1 survives the unpaged burst")
  check(ls:find("Market: Ericsgard (4 lots)", 1, true) ~= nil, "livestock: lineage 10 (Ericsgard) added by it")
  local eq_row
  for line in (ls .. "\n"):gmatch("([^\n]*)\n") do if line:find("manx", 1, true) and line:find("  100  ", 1, true) then eq_row = line end end
  check(eq_row ~= nil and eq_row:find("success", 1, true) == nil, "livestock: a lot EQUAL to your best is not green (equal is not better)")
  check(ls:find("icelandic horse", 1, true) ~= nil and ls:find("1400", 1, true) ~= nil, "livestock: the horse lot")
  check(ls:find("30m 20s", 1, true) ~= nil and ls:find("30m 40s", 1, true) == nil, "livestock: the queue countdown moved on")
  check(ls:find("89 head in 4 building(s)", 1, true) ~= nil, "livestock: herds untouched by an unpaged burst")

  -- a later FULL burst carrying only lineage 1 drops lineage 10
  for _, pg in ipairs({ LS_P1, LS_P2, LS_P3, LS_P4, LS_P5, LS_P6, LS_P7, LS_P8 }) do gmcp("Guild.Livestock", pg) end
  fire_timers()
  ls = st("livestock")
  check(ls:find("Ericsgard", 1, true) == nil, "livestock: a full burst without lineage 10 drops its market")
  check(ls:find("Lodbrok's Hold (10 lots)", 1, true) ~= nil, "livestock: ...and still lists lineage 1")

  -- the Feeds tab lists the summary key
  check(st("feeds"):find("lhead", 1, true) ~= nil, "livestock: lhead reaches the Feeds tab")

  -- 17 Sep 2026 (Eldran, live): the server now sends the herds and the market as TWO
  -- back-to-back full bursts of 7 pages each. Without sections the second (all market)
  -- wiped the first (all herds) and the tab said "waiting" with 240 messages received.
  local N = {
    '{ "bqueue_max": 2, "full": 1, "pages": 7, "bqueue": [ ], "bqueue_used": 0, "page": 1, "guild": "viking" }',
    '{ "full": 1, "pages": 7, "guild": "viking", "herds": [ { "trait": "bountiful", "age_ticks": 33, "con": 68, "quality": 59, "yield": 59, "breed": "spelsau", "bldg": "sheepfold", "management": "50;0;47;0;3;1;3356;1200;6550,5750,5950,4450,6850,5910", "sterile": 0, "breeds": "soay,spelsau", "head": 3, "vigor": 44, "hv": 1, "hard": 65, "gen": 12, "fert": 57 } ], "page": 2 }',
    '{ "full": 1, "pages": 7, "guild": "viking", "herds": [ { "trait": "purebred", "age_ticks": 70, "con": 90, "quality": 76, "yield": 57, "breed": "orust", "bldg": "henhouse", "management": "56;0;54;0;2;1;7000;1600;7200,9100,5700,7300,9000,7600", "sterile": 0, "breeds": "orust,landnam", "head": 2, "vigor": 73, "hv": 1, "hard": 72, "gen": 16, "fert": 91 } ], "page": 3 }',
    '{ "full": 1, "pages": 7, "guild": "viking", "herds": [ { "trait": "bountiful", "age_ticks": 94, "con": 67, "quality": 61, "yield": 65, "breed": "norse_landrace", "bldg": "piggery", "management": "50;0;48;0;2;1;9400;1000;6900,5800,6500,4600,6700,6100", "sterile": 0, "breeds": "norse_landrace", "head": 2, "vigor": 46, "hv": 0, "hard": 69, "gen": 10, "fert": 58 } ], "page": 4 }',
    '{ "full": 1, "pages": 7, "guild": "viking", "herds": [ { "trait": "0", "age_ticks": 0, "con": 0, "quality": 0, "yield": 0, "breed": "", "bldg": "byre", "management": "36;0;36;0;0;1;0;100;0,0,0,0,0,0", "sterile": 0, "breeds": "", "head": 0, "vigor": 0, "hv": 0, "hard": 0, "gen": 1, "fert": 0 } ], "page": 5 }',
    '{ "full": 1, "pages": 7, "guild": "viking", "herds": [ { "trait": "bountiful", "age_ticks": 103, "con": 56, "quality": 50, "yield": 34, "breed": "gotland_russ", "bldg": "stable", "management": "60;0;37;23;0;1;10394;1400;6704,3004,3404,6604,5696,5082", "sterile": 0, "breeds": "gotland_russ,icelandic_horse", "head": 23, "vigor": 66, "hv": 1, "hard": 67, "gen": 14, "fert": 30 } ], "page": 6 }',
    '{ "lneeds": [ { "current": 3, "cap": 50, "species": "sheep" }, { "current": 2, "cap": 56, "species": "chicken" }, { "current": 2, "cap": 50, "species": "pig" }, { "current": 0, "cap": 36, "species": "cow" }, { "current": 23, "cap": 60, "species": "horse" } ], "lpending": [ ], "full": 1, "pages": 7, "page": 7, "lfeed": { "head": 30, "water": 5, "grain": 5 }, "guild": "viking" }',
  }
  local M = {
    '{ "lfind_posts": [ ], "full": 1, "lfind_offers": [ ], "pages": 7, "lfind_auctions": [ ], "page": 1, "guild": "viking" }',
    '{ "full": 1, "pages": 7, "lmarket_5": [ { "lin": 5, "trait": "hardy", "con": 45, "breed": "gotland_russ", "yield": 27, "token": "dac7ea7a52c426e672ba8779b007a627", "vigor": 87, "unit_price": 1227, "count": 3, "available": 3, "hard": 37, "price": 3681, "idx": 0, "fert": 33, "species": "horse" }, { "lin": 5, "trait": "hardy", "con": 49, "breed": "gotland_russ", "yield": 26, "token": "a057fdfcff1c2432c276707ba2772415", "vigor": 56, "unit_price": 910, "count": 2, "available": 2, "hard": 43, "price": 1820, "idx": 1, "fert": 24, "species": "horse" } ], "guild": "viking", "page": 2 }',
    '{ "full": 1, "pages": 7, "lmarket_5": [ { "lin": 5, "trait": "0", "con": 41, "breed": "fjord_cattle", "yield": 31, "count": 3, "vigor": 26, "hard": 54, "price": 1068, "idx": 2, "fert": 19, "species": "cow" }, { "lin": 5, "trait": "0", "con": 41, "breed": "gotland_russ", "yield": 18, "count": 3, "vigor": 51, "hard": 44, "price": 1527, "idx": 3, "fert": 20, "species": "horse" } ], "guild": "viking", "page": 3 }',
    '{ "full": 1, "pages": 7, "lmarket_5": [ { "lin": 5, "trait": "prolific", "con": 37, "breed": "gotland_russ", "yield": 66, "token": "f841d3f6160e6f294bf9485842b77ec3", "vigor": 51, "unit_price": 1026, "count": 1, "available": 1, "hard": 43, "price": 1026, "idx": 4, "fert": 24, "species": "horse" }, { "lin": 5, "trait": "bountiful", "con": 49, "breed": "fjord_cattle", "yield": 39, "count": 1, "vigor": 38, "hard": 55, "price": 652, "idx": 5, "fert": 23, "species": "cow" } ], "guild": "viking", "page": 4 }',
    '{ "full": 1, "pages": 7, "lmarket_5": [ { "lin": 5, "trait": "0", "con": 33, "breed": "gotland_russ", "yield": 41, "count": 1, "vigor": 43, "hard": 40, "price": 583, "idx": 6, "fert": 18, "species": "horse" }, { "lin": 5, "trait": "0", "con": 50, "breed": "fjord_cattle", "yield": 38, "count": 2, "vigor": 79, "hard": 52, "price": 1514, "idx": 7, "fert": 31, "species": "cow" } ], "guild": "viking", "page": 5 }',
    '{ "full": 1, "pages": 7, "lmarket_5": [ { "lin": 5, "trait": "0", "con": 47, "breed": "gotland_russ", "yield": 18, "token": "f9d7456f38a39d8be93847c0b6deb0d3", "vigor": 55, "unit_price": 554, "count": 3, "available": 3, "hard": 45, "price": 1662, "idx": 8, "fert": 24, "species": "horse" }, { "lin": 5, "trait": "0", "con": 40, "breed": "gotland_russ", "yield": 21, "token": "622f4f3dcd55d10e71f048abd4136521", "vigor": 61, "unit_price": 583, "count": 2, "available": 2, "hard": 37, "price": 1166, "idx": 9, "fert": 19, "species": "horse" } ], "guild": "viking", "page": 6 }',
    '{ "lmarket_partial": 1, "full": 1, "pages": 7, "guild": "viking", "page": 7 }',
  }
  for _, pg in ipairs(N) do gmcp("Guild.Livestock", pg) end
  for _, pg in ipairs(M) do gmcp("Guild.Livestock", pg) end
  fire_timers()
  ls = st("livestock")
  check(ls:find("waiting for", 1, true) == nil, "livestock 17 Sep: two full bursts, herds then market - the tab is not 'waiting'")
  check(ls:find("30 head in 5 building(s)", 1, true) ~= nil, "livestock 17 Sep: the herds burst survives the market burst that follows it")
  check(ls:find("Market: Hafrfjord (10 lots, partial)", 1, true) ~= nil, "livestock 17 Sep: the market burst is there too, flagged partial")
  check(ls:find("Room ", 1, true) ~= nil and ls:find("horse", 1, true) and ls:find("23/60", 1, true) ~= nil,
        "livestock 17 Sep: lneeds shows head against capacity per species")
  check(ls:find("Ericsgard", 1, true) == nil and ls:find("Lodbrok's Hold", 1, true) == nil,
        "livestock 17 Sep: a full market burst still replaces the market it does not carry")
  -- Each and Lot (24 Sep): unit_price per head, price for the lot; a lot without unit_price
  -- (the same market mixes both) works it out of price/count
  local function row_with(...)
    local need = { ... }
    for line in (ls .. "\n"):gmatch("([^\n]*)\n") do
      local ok = true
      for _, n in ipairs(need) do if not line:find(n, 1, true) then ok = false end end
      if ok then return line end
    end
  end
  check(row_with("gotland russ", " 1227   3681") ~= nil, "livestock: a lot's price per head and for the lot")
  check(row_with("fjord cattle", "  356   1068") ~= nil, "livestock: no unit_price - price / count per head (1068 / 3)")
  check(ls:find("Each    Lot", 1, true) ~= nil, "livestock: the header names both prices")
  -- and the other way round: a herds burst alone leaves the market as it was
  for _, pg in ipairs(N) do gmcp("Guild.Livestock", pg) end
  fire_timers()
  ls = st("livestock")
  check(ls:find("Market: Hafrfjord (10 lots, partial)", 1, true) ~= nil, "livestock 17 Sep: a herds burst leaves the market section alone")
  gmcp("Guild.Livestock", '{ "lmarket_5": [ { "lin": 5, "trait": "hardy", "con": 45, "breed": "gotland_russ", "yield": 27, "token": "x", "vigor": 87, "unit_price": 1227, "count": 3, "available": 1, "hard": 37, "price": 3681, "idx": 0, "fert": 33, "species": "horse" } ], "guild": "viking" }')
  fire_timers()
  ls = st("livestock")
  check(row_with("gotland russ", "  1/3  ") ~= nil, "livestock: part of a lot sold shows left/lot")
end

-- =====================================================================
-- 17 SEP FIELD MOVES (gmcp-fields-Goran-20260917 report): the hird comes as
-- hird_0/hird_1 slices, varang_in/out moved from Guild.Kingdom to Guild.Roster,
-- cityplan_buildings moved from Guild.City to its own Guild.CityBuildings (records
-- lost their name and carry w/h only when bigger than 1x1), cdtime moved from
-- Guild.Trade to Guild.City.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  feed_all()
  -- a Roster burst in the new shape (pages renumbered to 4; slice pages verbatim)
  gmcp("Guild.Roster", '{ "full": 1, "pages": 4, "guild": "viking", "hird_0": [ { "level": 50, "age": "elder", "atk": 10, "status": "personal_guard", "champ": 2, "hired": 1780752193, "mode": "neutral", "wpn": 5, "def": 10, "arm": 5, "loyalty": 2, "name": "Njord Brandsson", "id": 1 }, { "level": 50, "age": "elder", "atk": 10, "status": "personal_guard", "champ": 0, "hired": 1781676706, "mode": "neutral", "wpn": 5, "def": 10, "arm": 5, "loyalty": 4, "name": "Vakr Haldorsson", "id": 2 } ], "page": 1 }')
  gmcp("Guild.Roster", '{ "hird_total": 3, "gneeds": { "on_duty": 7, "hird_cap": 24, "city_pool": 0, "stationed": 58, "hird_count": 7, "garrisoned": 0, "wounded": 0, "garrison_cap": 58 }, "hird_slices": 2, "hird_1": [ { "level": 34, "age": "young", "atk": 8, "status": "personal_guard", "champ": 0, "hired": 1789376482, "mode": "neutral", "wpn": 5, "def": 10, "arm": 5, "loyalty": 3, "name": "Roar Bjornsson", "id": 7 } ], "courier": [ ], "full": 1, "pages": 4, "courier_tier": 2, "page": 2, "guild": "viking" }')
  gmcp("Guild.Roster", '{ "full": 1, "pages": 4, "bonds": [ { "a": 1, "tier": 3, "ticks": 1312394, "b": 7 } ], "guild": "viking", "page": 3 }')
  gmcp("Guild.Roster", '{ "train": { "secs": 0, "trained": 0, "name": "", "stat": "", "tier": 5 }, "full": 1, "varang_out": [ { "name": "Ulf" } ], "thralls": { "mine": 5, "total": 60 }, "pages": 4, "varang_in": [ ], "page": 4, "guild": "viking" }')
  fire_timers()
  local people = st("people")
  check(people:find("Hird Guard (7/24)", 1, true), "17 Sep roster: hird header from gneeds")
  check(people:find("Njord Brandsson", 1, true) and people:find("Vakr Haldorsson", 1, true)
    and people:find("Roar Bjornsson", 1, true), "17 Sep roster: hird_0 and hird_1 slices are one hird")
  check(people:find("Njord Brandsson", 1, true) < people:find("Roar Bjornsson", 1, true),
    "17 Sep roster: slices merge in slice order")
  check(not people:find("Thyra Ivarardottir", 1, true), "17 Sep roster: a full burst replaces the old hird")
  check(people:find("Roar Bjornsson", 1, true) and people:find("Njord", 1, true),
    "17 Sep roster: bond names resolve through the merged hird")
  check(st("holds"):find("Varangians in 0, out 1", 1, true), "17 Sep roster: varangians read from Guild.Roster")
  gmcp("Guild.Kingdom", KINGDOM_P1); gmcp("Guild.Kingdom", KINGDOM_P2); fire_timers()
  check(st("holds"):find("Varangians in 0, out 1", 1, true), "17 Sep roster: a Kingdom burst without varang keys leaves the figure alone")

  -- Guild.CityBuildings feeds the plan (three pages of the capture, renumbered)
  scrye.store.set("cp_placed", "")
  gmcp("Guild.CityBuildings", '{ "cityplan_buildings": [ { "x": 0, "glyph": "F", "y": 0, "pal": "p", "id": "farm" }, { "x": 0, "glyph": "M", "y": 3, "pal": "k", "id": "mine" } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
  gmcp("Guild.CityBuildings", '{ "cityplan_buildings": [ { "x": 3, "glyph": "d", "y": 5, "h": 2, "w": 2, "pal": "c", "id": "mead_hall" }, { "x": 10, "glyph": "t", "y": 0, "pal": "v", "id": "timberward_yard" } ], "full": 1, "pages": 2, "guild": "viking", "page": 2 }')
  fire_timers()
  local placed = scrye.store.get("cp_placed") or ""
  check(placed:find("0|0|1|1|F|Farm", 1, true), "17 Sep citybuildings: a 1x1 record without w/h reads as 1x1")
  check(placed:find("3|5|2|2|d|", 1, true), "17 Sep citybuildings: a 2x2 record keeps its footprint")
  check(placed:find("|t|Timberward yd", 1, true), "17 Sep citybuildings: the name comes from the palette by glyph")
  local grid = {}
  for row in st("plangrid"):gmatch("[^\n]+") do grid[#grid + 1] = row end
  check(grid[1] and grid[1]:sub(1, 1) ~= "." and grid[4] and grid[4]:sub(1, 1) ~= ".",
    "17 Sep citybuildings: the plan grid places the farm at 0,0 and the mine at 0,3")
  check(grid[6] and grid[6]:sub(4, 5):match("^[^.][^.]$") and grid[7] and grid[7]:sub(4, 5):match("^[^.][^.]$"),
    "17 Sep citybuildings: the mead hall covers its 2x2 footprint")

  -- cdtime: Guild.City carries it now; a Trade burst without it must not wipe it
  gmcp("Guild.City", CITY_UNPAGED); gmcp("Guild.City", CITY_P1)
  gmcp("Guild.City", (CITY_P2:gsub('"cdtime": 0', '"cdtime": 77')))
  gmcp("Guild.Trade", TRADE_P1); gmcp("Guild.Trade", TRADE_P2); gmcp("Guild.Trade", TRADE_P3)
  fire_timers()
  check(st("feeds"):find("cdtime         77", 1, true), "17 Sep cdtime: read from Guild.City and kept across a Trade burst")

  -- the fleet rides Guild.Voyage as `longship` now (no Guild.Fleet in the capture)
  gmcp("Guild.Voyage", '{ "longship": [ { "target": "Deep Salvage", "state": "voyaging", "captain_style": "bold", "tier": 5, "safe": 0, "voyage_identity": "whisper-prowed", "secs": 0, "saga_title": "the Terrible", "saga_raids": 92, "hired_crew": 0, "crew": 60, "name": "Vasa", "id": 5 }, { "target": "Lejre", "state": "raiding", "captain_style": 0, "tier": 5, "safe": 0, "voyage_identity": 0, "secs": 679, "saga_title": "the Legendary", "saga_raids": 276, "hired_crew": 0, "crew": 60, "name": "Svarten", "id": 14 } ], "full": 1, "pages": 2, "guild": "viking", "page": 1 }')
  gmcp("Guild.Voyage", '{ "vmem": [ ], "voyage_chart": { "height": 16, "chart_mode": "advanced", "width": 16 }, "full": 1, "pages": 2, "voffers": [ ], "page": 2, "guild": "viking" }')
  fire_timers()
  check(st("feeds"):find("Vasa|5|voyaging|Deep Salvage|0;Svarten", 1, true), "17 Sep voyage: longship feeds the ships contract")
  check(st("city"):find("Svarten", 1, true) and st("city"):find("Lejre", 1, true), "17 Sep voyage: the City tab lists the longships")
  check(not st("feeds"):find("Kronan", 1, true), "17 Sep voyage: the old Guild.Fleet list is replaced")

  -- the market's village order learns from Guild.Kingdom grudges (town + lineage);
  -- this one deliberately disagrees with TG_ORDER for lineage 1 and says nothing
  -- about lineage 2, which must keep its default
  gmcp("Guild.Kingdom", '{ "full": 1, "pages": 1, "guild": "viking", "grudges": [ { "town": "Lejre", "secs": 131688, "lineage": 1 } ], "page": 1 }')
  gmcp("Guild.TradeGoods", '{ "goods": [ { "buy": 60, "lin": 1, "dem": 400, "sell": 43, "score": 2, "sup": 99, "good": "b" }, { "buy": 60, "lin": 2, "dem": 400, "sell": 43, "score": 2, "sup": 99, "good": "b" } ], "full": 1, "pages": 2, "page": 1, "guild": "viking" }')
  gmcp("Guild.TradeGoods", '{ "lin": 1, "lin_count": 14, "full": 1, "pages": 2, "page": 2, "guild": "viking" }')
  fire_timers()
  local mk = tostring(store["market"] or "")
  check(mk:find("bread\tLejre\t", 1, true), "17 Sep kingdom: grudges name the market's lineage 1 town")
  check(mk:find("bread\tEiriksby\t", 1, true), "17 Sep kingdom: a lineage the grudges skip keeps its default town")
  -- a later, shorter grudges list must not forget what an earlier one taught
  gmcp("Guild.Kingdom", '{ "full": 1, "pages": 1, "guild": "viking", "grudges": [ { "town": "Holmgard", "secs": 1, "lineage": 2 } ], "page": 1 }')
  gmcp("Guild.Kingdom", '{ "full": 1, "pages": 1, "guild": "viking", "grudges": [ { "town": "Lejre", "secs": 1, "lineage": 1 } ], "page": 1 }')
  gmcp("Guild.TradeGoods", '{ "goods": [ { "buy": 61, "lin": 2, "dem": 400, "sell": 43, "score": 2, "sup": 99, "good": "b" } ], "full": 1, "pages": 2, "page": 1, "guild": "viking" }')
  gmcp("Guild.TradeGoods", '{ "lin": 2, "lin_count": 14, "full": 1, "pages": 2, "page": 2, "guild": "viking" }')
  fire_timers()
  mk = tostring(store["market"] or "")
  check(mk:find("bread\tHolmgard\t61", 1, true), "17 Sep kingdom: grudges overlay - a town learned earlier survives a shorter list")
end

-- =====================================================================
-- PRODUCTION PER TICK (server-side 19 Sep 2026: `production` on Guild.City). The
-- shape is unconfirmed until a capture, so all three the adapter accepts are pinned.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  feed_all()
  check(st("production"):find("no data", 1, true), "production: honest before the field arrives")
  gmcp("Guild.City", '{ "production": { "grain": 12, "salted_fish": -3, "iron": 4 }, "guild": "viking" }')
  fire_timers()
  local pt = st("production")
  check(pt:match("Grain%s+@{success}%+12") ~= nil, "production: an object - a producing good is green with a plus")
  check(pt:match("Salted fish%s+@{error}%-3") ~= nil, "production: a key-form good reads as words, a drain is red")
  check(not pt:find("no data", 1, true), "production: the placeholder is gone")
  gmcp("Guild.City", '{ "production": [ { "good": "timber", "amount": 7 }, { "good": "mead", "amount": -1 } ], "guild": "viking" }')
  fire_timers()
  pt = st("production")
  check(pt:match("Timber%s+@{success}%+7") ~= nil and pt:match("Mead%s+@{error}%-1") ~= nil and not pt:find("Grain", 1, true),
    "production: a list of records replaces the last figure")
  gmcp("Guild.City", '{ "production": "wool:2,honey:0", "full": 1, "pages": 2, "page": 1, "guild": "viking" }')
  gmcp("Guild.City", '{ "full": 1, "pages": 2, "page": 2, "guild": "viking" }')
  fire_timers()
  pt = st("production")
  check(pt:match("Wool%s+@{success}%+2") ~= nil and pt:match("Honey%s+@{success}%+0") ~= nil, "production: the MIP-era string passes through")
  gmcp("Guild.City", CITY_P1); gmcp("Guild.City", CITY_P2)     -- a full paged burst without the key
  fire_timers()
  check(st("production"):find("Wool", 1, true), "production: a burst without the key leaves the figure alone")
end

-- =====================================================================
-- 20 SEP: the refinery rides Guild.Settlement now (Guild.Refinery gone again); the
-- records carry in/out/wstock besides what the tab reads. Verbatim page 1 of 8, and a
-- grades page, renumbered.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  feed_all()
  gmcp("Guild.Settlement", '{ "full": 1, "pages": 2, "guild": "viking", "refinery": [ { "stock": 250, "bldg": "salting_house", "out": "salted_fish", "wstock": 132, "in": "fish", "cap": 250, "tier": 3 }, { "stock": 250, "bldg": "bakehouse", "out": "bread", "wstock": 164, "in": "grain", "cap": 250, "tier": 3 } ], "page": 1 }')
  gmcp("Guild.Settlement", '{ "refinery_grades": [ { "bldg": "salting_house", "pct": 78, "qty": 171, "grade": "brined" }, { "bldg": "salting_house", "pct": 100, "qty": 79, "grade": "hard-cured" } ], "full": 1, "pages": 2, "page": 2, "guild": "viking" }')
  fire_timers()
  check(st("feeds"):find("refinery       salting_house:3:250:250:brined,171,78", 1, true) ~= nil
        or st("feeds"):find("refinery       bakehouse:3:250:250", 1, true) ~= nil,
        "20 Sep: the refinery feed is filled from Guild.Settlement (" .. tostring(st("feeds"):match("refinery[^\n]*")) .. ")")
  check(st("refinery"):find("Salting House\tT3 250/250", 1, true) ~= nil, "...and the refinery bars show it")
  -- a later Settlement burst without the key (its usual unpaged settlerx) leaves it alone
  gmcp("Guild.Settlement", '{ "guild": "viking", "settlerx": { "net": 1 } }')
  fire_timers()
  check(st("feeds"):find("refinery       salting_house", 1, true) ~= nil or st("feeds"):find("refinery       bakehouse", 1, true) ~= nil,
        "20 Sep: a Settlement burst without the refinery keeps it")
end

-- 5 Oct: Scrye's idle guard. The trader disarms and the 5-minute 'l' keepalive stops -
-- the keepalive is what kept a botting character from ever going idle on the MUD.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  alias("^vtick (on|off)$", "on")
  alias("^atrade (.+)$", "on"); fire_timers()
  sent = {}
  tick(1)
  local l_before = false
  for _, c in ipairs(sent) do if c == "l" then l_before = true end end
  check(l_before, "(setup) the keepalive sends 'l'")
  check(#idle_hooks == 1, "the plugin listens for the idle guard")
  printed, notifies = {}, {}
  for _, f in ipairs(idle_hooks) do f() end
  check(said("auto-trade disarmed"), "the idle guard disarms the auto-trader")
  check(said("keepalive 'l' stopped"), "...and stops the keepalive")
  check(#notifies == 0, "...with no phone buzz while trade notify is off")
  sent = {}
  tick(3); fire_timers()
  local l_after = false
  for _, c in ipairs(sent) do if c == "l" then l_after = true end end
  check(not l_after, "no 'l' after the guard fired")
  check(store["tick"] == "1", "the saved vtick setting is left as it was")
  printed = {}
  for _, f in ipairs(idle_hooks) do f() end
  check(#printed == 0, "a second firing with nothing running says nothing")
  -- the notify half
  alias("^atrade (.+)$", "notify on"); alias("^atrade (.+)$", "on"); fire_timers()
  notifies = {}
  for _, f in ipairs(idle_hooks) do f() end
  check(#notifies == 1 and notifies[1]:find("idle guard", 1, true) ~= nil, "trade notify on: the phone hears about it")
end

-- 6 Oct: the Trade Auto switches read at a glance - each in its state's colour - and the
-- settings boxes are 10 characters wide (API 1.23 input width)
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  alias("^atrade (.+)$", "off"); alias("^atrade (.+)$", "scalp on"); fire_timers()
  local s = st("atstatus")
  check(s:find("Auto-trade: @{error}OFF@{}", 1, true) ~= nil, "auto-trade off shows red (" .. s:match("^[^\n]*") .. ")")
  check(s:find("Scalp: @{success}on@{}", 1, true) ~= nil, "a switch that is on shows green")
  local inputs, sized = 0, 0
  for _, tab in ipairs(panel.tabs or {}) do
    if tab.title == "Trade Auto" then
      for _, w in ipairs(tab.widgets) do
        if w.type == "input" then inputs = inputs + 1; if w.width == 10 then sized = sized + 1 end end
      end
    end
  end
  check(inputs > 0 and sized == inputs, "every Trade Auto setting box is 10 characters wide (" .. sized .. "/" .. inputs .. ")")
end

-- 6 Oct: Staffing on the People tab. Guild.Roster streams staff and hird as windows
-- (X_from + X_page) and the wanted posts as ~128-char pieces (rneeds_c); thralls come per
-- building. Shapes from the 6 Oct capture; staff entries carry the remembered field names.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  -- burst A: hird window from 0, then the summary page with a needs piece cut mid-entry
  gmcp("Guild.Roster", '{ "full": 1, "pages": 4, "courier_tier": 5, "hird_from": 0, "page": 1, "guild": "viking" }')
  gmcp("Guild.Roster", '{ "hird_page": [ { "level": 50, "status": "personal_guard", "age": "elder", "atk": 10, "mode": "neutral", "def": 10, "loyalty": 2, "name": "Njord Brandsson", "id": 1 } ], "full": 1, "pages": 4, "guild": "viking", "page": 2 }')
  gmcp("Guild.Roster", '{ "hird_total": 2, "staff_from": 0, "staff_total": 3, "staff_page": [ { "name": "Astrid Eiriksdottir", "assigned": "smithy", "best_stat": "craft", "stats": { "combat": 1, "trade": 2, "craft": 9, "sea": 1, "wild": 1, "land": 2, "charm": 3 }, "trait": "taskmaster", "loyalty": 4, "id": 31 }, { "name": "Leif Haraldsson", "assigned": "ship_24", "best_stat": "sea", "stats": { "sea": 8 }, "trait": "sea_dog", "loyalty": 3, "id": 32 } ], "full": 1, "rneeds_c": "shadow_house:charm:spymaster;courier_post:trade:silver_tongue;beacon:sea:old_salt;training_yard:combat:taskmaster;moat:combat:ir", "pages": 4, "page": 3, "guild": "viking" }')
  gmcp("Guild.Roster", '{ "full": 1, "thralls": { "mine": 5, "smithy": 4, "total": 9 }, "pages": 4, "page": 4, "guild": "viking" }')
  fire_timers()
  local tb = st("staffing")
  -- the staffing block's line for a building, or ""
  local function line(text, name)
    for l in (text .. "\n"):gmatch("([^\n]*)\n") do
      if l:find("}" .. name .. " ", 1, true) then return l end
    end
    return ""
  end
  local sm = line(tb, "Smithy")
  check(sm:find("^@{success}Smithy") and sm:find("Astrid (craft)", 1, true) and sm:find("4 thralls", 1, true),
        "staffing: a staffed building is green, with its staff and thralls (" .. tb .. ")")
  check(line(tb, "Ship #24"):find("Leif (sea)", 1, true) ~= nil, "staffing: a ship post shows as a ship")
  local mi = line(tb, "Mine")
  check(mi:find("^@{warning}Mine") and mi:find("vacant", 1, true) and mi:find("5 thralls", 1, true),
        "staffing: a building with nobody on it is orange and says vacant")
  check(tb:find("Total", 1, true) == nil, "staffing: the thrall total is not a building")
  local be = line(tb, "Beacon")
  check(be:find("^@{warning}Beacon") and be:find("wants sea/old salt", 1, true),
        "staffing: a vacant building says what it wants (" .. be .. ")")
  check(tb:find("Moat", 1, true) == nil, "staffing: the entry the piece cut in half is not guessed at")
  -- burst B: the next windows - hird from 1, staff from 2 (unassigned)
  gmcp("Guild.Roster", '{ "pages": 2, "hird_from": 1, "hird_page": [ { "name": "Ivar Ketilssonsson", "id": 9, "level": 50 } ], "page": 1, "guild": "viking" }')
  gmcp("Guild.Roster", '{ "pages": 2, "staff_from": 2, "staff_page": [ { "name": "Gyda Toresdottir", "assigned": "", "best_stat": "charm", "stats": { "charm": 7 }, "id": 33 } ], "page": 2, "guild": "viking" }')
  fire_timers()
  tb = st("staffing")
  check(tb:find("(unassigned)", 1, true) and tb:find("Gyda (charm)", 1, true) and tb:find("Gyda") > tb:find("Smithy"),
        "staffing: an unassigned staff member lists last as (unassigned)")
  check(line(tb, "Smithy"):find("Astrid", 1, true) ~= nil, "staffing: an earlier window is kept when the next one arrives")
  check(st("people"):find("Njord Brandsson", 1, true) ~= nil and st("people"):find("Ivar Ketilssonsson", 1, true) ~= nil,
        "People: the hird is read from its windows too")
  local pp = st("people")
  check(not pp:find("-- Staff --", 1, true) and not pp:find("Astrid", 1, true),
        "People: the Staff section is gone - Staffing shows the staff")
  check(not pp:find("personal", 1, true) and not pp:find("Threk", 1, true),
        "People: no personal_guard on hird rows, no Threk under Forces")
  local c1, c2 = pp:match("\n(Njord Brandsson%s+)%S"), pp:match("\n(Ivar Ketilssonsson%s+)%S")
  check(c1 and c2 and #c1 == #c2, "People: the hird's stat column lines up whatever the name length")
  local _, tabs = nil, panel and panel.tabs or {}
  local has_table = false
  for _, tab in ipairs(tabs) do
    if tab.title == "People" then
      for _, w in ipairs(tab.widgets) do if w.type == "text" and w.bind and w.bind:find("staffing", 1, true) then has_table = true end end
    end
  end
  check(has_table, "the People tab carries the Staffing block")
  -- 6 Oct: the unassigned wrap three to a line
  gmcp("Guild.Roster", '{ "pages": 1, "staff_from": 3, "staff_total": 8, "staff_page": [ { "name": "Bo A", "assigned": "", "best_stat": "land", "id": 41 }, { "name": "Ca B", "assigned": "", "best_stat": "land", "id": 42 }, { "name": "Di C", "assigned": "", "best_stat": "land", "id": 43 }, { "name": "Ed D", "assigned": "", "best_stat": "land", "id": 44 } ], "page": 1, "guild": "viking" }')
  fire_timers()
  local un = st("staffing"):match("%(unassigned%)[^\n]*\n([^\n]*)") or ""
  local first = st("staffing"):match("%(unassigned%)%s*@{} ([^\n]*)") or ""
  local n1 = select(2, first:gsub("%(", ""))
  check(n1 == 3 and un:find("Ed (", 1, true) ~= nil and un:match("^%s+") ~= nil,
        "staffing: the unassigned wrap three to a line, indented under the first (" .. first .. " / " .. un .. ")")
  -- burst C: the staff window comes round to 0 again and Astrid has moved to the armoury
  gmcp("Guild.Roster", '{ "pages": 1, "staff_from": 0, "staff_total": 3, "staff_page": [ { "name": "Astrid Eiriksdottir", "assigned": "armoury", "best_stat": "craft", "id": 31 } ], "page": 1, "guild": "viking" }')
  fire_timers()
  tb = st("staffing")
  check(line(tb, "Armoury"):find("Astrid (craft)", 1, true) ~= nil and line(tb, "Smithy"):find("Astrid", 1, true) == nil,
        "staffing: a window that comes round again replaces those entries (" .. tb .. ")")
end

-- 6 Oct: the livestock market moved to Guild.LivestockMarket (one burst per lineage
-- looked at), and Guild.Kingdom's war_incoming warns of a host on its way.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  gmcp("Guild.Livestock", '{ "full": 1, "pages": 2, "guild": "viking", "herds": [ { "trait": "bountiful", "age_ticks": 45, "con": 53, "quality": 46, "yield": 49, "breed": "spelsau", "bldg": "sheepfold", "sterile": 0, "head": 65, "vigor": 36, "hv": 3, "hard": 53, "gen": 9, "fert": 40 } ], "page": 1 }')
  gmcp("Guild.Livestock", '{ "lneeds": [ ], "lpending": [ ], "full": 1, "pages": 2, "page": 2, "lfeed": { "head": 65, "water": 8, "grain": 8 }, "guild": "viking" }')
  -- lineage 1's market, then lineage 3's: the second burst must not wipe the first
  gmcp("Guild.LivestockMarket", '{ "lfind_posts": [ ], "full": 1, "lfind_offers": [ ], "pages": 3, "lfind_auctions": [ ], "page": 1, "guild": "viking" }')
  gmcp("Guild.LivestockMarket", '{ "full": 1, "pages": 3, "guild": "viking", "lmarket_1": [ { "lin": 1, "trait": "bountiful", "con": 46, "breed": "spelsau", "yield": 35, "count": 2, "vigor": 26, "hard": 49, "price": 392, "idx": 0, "fert": 43, "species": "sheep" } ], "page": 2 }')
  gmcp("Guild.LivestockMarket", '{ "lmarket_partial": 1, "full": 1, "pages": 3, "guild": "viking", "page": 3 }')
  gmcp("Guild.LivestockMarket", '{ "lmarket_3": [ { "lin": 3, "trait": "0", "con": 49, "breed": "soay", "yield": 32, "count": 2, "vigor": 26, "hard": 36, "price": 256, "idx": 0, "fert": 41, "species": "sheep" } ], "full": 1, "pages": 1, "guild": "viking", "page": 1 }')
  fire_timers()
  local ls = st("livestock")
  check(ls:find("Market:", 1, true) ~= nil, "livestock: the market is read from Guild.LivestockMarket (" .. ls:sub(1, 200) .. ")")
  local markets = select(2, ls:gsub("%-%- Market:", ""))
  check(markets == 2, "livestock: one lineage's market burst does not wipe another's (" .. markets .. ")")
  check(ls:find("sheepfold", 1, true) ~= nil, "livestock: the herds still come from Guild.Livestock")
  -- a later herds burst keeps the market
  gmcp("Guild.Livestock", '{ "full": 1, "pages": 1, "guild": "viking", "herds": [ { "breed": "spelsau", "bldg": "sheepfold", "head": 66, "quality": 46 } ], "page": 1 }')
  fire_timers()
  check(select(2, st("livestock"):gsub("%-%- Market:", "")) == 2, "livestock: a herds burst keeps the market")

  gmcp("Guild.Kingdom", '{ "war_cb": [ ], "full": 1, "pages": 1, "war_camp": [ ], "war_incoming": { "strength": 116, "days": 2, "town": "Rouen" }, "page": 1, "guild": "viking" }')
  fire_timers()
  check(st("stats"):find("WAR Rouen marches on you - strength 116, 2 days left", 1, true) ~= nil,
        "stats: an incoming war host is called out")
  gmcp("Guild.Kingdom", '{ "war_cb": [ ], "full": 1, "pages": 1, "war_camp": [ ], "page": 1, "guild": "viking" }')
  fire_timers()
  check(st("stats"):find("marches on you", 1, true) == nil, "stats: the warning goes when the server stops sending it")
end

-- 11 Oct: a server that accepts the socket and drops it again had auto-reconnect cycling
-- every few seconds, and every onConnect booked its own 'vbuild list' scan 20s out - 100+
-- went out live. One pending scan at most; a disconnect drops it.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  local function connect()    for _, f in ipairs(conn_hooks) do f() end end
  local function disconnect() for _, f in ipairs(disc_hooks) do f() end end
  local function scans()
    local n = 0
    for _, c in ipairs(sent) do if c == "vbuild list" then n = n + 1 end end
    return n
  end
  for _ = 1, 50 do connect(); disconnect() end
  fire_timers()
  check(scans() == 0, "build scan: 50 connects that each dropped send nothing (" .. scans() .. ")")
  connect(); connect()
  fire_timers()
  check(scans() == 1, "build scan: two connects in a row book one scan (" .. scans() .. ")")
  connect(); fire_timers()
  check(scans() == 2, "build scan: a later connection that stays up still scans (" .. scans() .. ")")
end

-- 11 Oct: a right-click on a market lot buys from it - 'vlivestock buy <town> <row> <qty>',
-- town the hold's city name (Lodbrok's Hold -> lodbrok), row the lot's idx + 1.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  gmcp("Guild.Livestock", '{ "full": 1, "pages": 1, "guild": "viking", "page": 1, "herds": [ { "breed": "spelsau", "bldg": "sheepfold", "head": 66, "quality": 46 } ] }')
  gmcp("Guild.LivestockMarket", '{ "full": 1, "pages": 1, "guild": "viking", "page": 1, "lmarket_1": [ '
    .. '{ "lin": 1, "trait": "hardy", "con": 40, "breed": "landnam", "yield": 28, "count": 2, "vigor": 35, "hard": 34, "price": 190, "unit_price": 95, "idx": 1, "fert": 57, "species": "chicken" }, '
    .. '{ "lin": 1, "trait": "0", "con": 49, "breed": "landnam", "yield": 27, "count": 2, "vigor": 29, "hard": 45, "price": 140, "unit_price": 70, "idx": 0, "fert": 47, "species": "chicken" }, '
    .. '{ "lin": 1, "trait": "0", "con": 40, "breed": "spelsau", "yield": 30, "count": 12, "available": 10, "vigor": 30, "hard": 30, "price": 1200, "unit_price": 100, "idx": 2, "fert": 30, "species": "sheep" } ] }')
  gmcp("Guild.LivestockMarket", '{ "full": 1, "pages": 1, "guild": "viking", "page": 1, "lmarket_3": [ '
    .. '{ "lin": 3, "trait": "0", "con": 49, "breed": "soay", "yield": 32, "count": 1, "vigor": 26, "hard": 36, "price": 256, "idx": 4, "fert": 41, "species": "sheep" } ] }')
  gmcp("Guild.LivestockMarket", '{ "full": 1, "pages": 1, "guild": "viking", "page": 1, "lmarket_99": [ '
    .. '{ "lin": 99, "trait": "0", "breed": "x", "count": 1, "price": 5, "idx": 0, "species": "pig" } ] }')
  fire_timers()
  local ls = st("livestock")
  local function rowof(pat) for ln in ls:gmatch("[^\n]+") do if ln:find(pat, 1, true) then return ln end end return "" end
  local first = rowof("Lot 1: chicken landnam")
  check(first:find("Buy 1 for 70|vlivestock buy lodbrok 1 1;Buy 2 for 140|vlivestock buy lodbrok 1 2", 1, true) ~= nil,
        "lmarket buy: row 1 offers 1..qty to lodbrok (" .. first .. ")")
  check(first:find("vlivestock buy lodbrok 1 3", 1, true) == nil, "lmarket buy: never more than the lot holds")
  check(rowof("Lot 2: chicken landnam"):find("hardy", 1, true) ~= nil, "lmarket buy: row number is idx + 1, drawn in idx order")
  local big = rowof("Lot 3: sheep spelsau")
  check(big:find("Buy 6 for 600|", 1, true) ~= nil and big:find("Buy 7", 1, true) == nil
        and big:find("Buy all 10 for 1000|vlivestock buy lodbrok 3 10", 1, true) ~= nil,
        "lmarket buy: available, not count, caps the menu; 6 steps then all (" .. big .. ")")
  check(rowof("Lot 5: sheep soay"):find("vlivestock buy imaird 5 1", 1, true) ~= nil,
        "lmarket buy: another hold uses its city name, and the row is idx + 1 even across a gap")
  check(ls:find("vlivestock buy %a+ 1 1", 1) and rowof("pig"):find("menu=", 1, true) == nil, "lmarket buy: an unknown hold gets no menu")
  check(first:find("@{accent,underline,menu=", 1, true) ~= nil and first:find("}chicken@{} ", 1, true) ~= nil,
        "lmarket buy: a buyable lot looks clickable, and only the words are underlined")
  check(rowof("pig"):find("accent", 1, true) == nil, "lmarket buy: a lot with no menu does not look clickable")
  check(not ls:find("rclick=vlivestock", 1, true) and not ls:find("click=vlivestock", 1, true), "lmarket buy: a buy is never under a plain click")
  -- every spec fits the host's 512-char markup cap
  local longest = 0
  for spec in ls:gmatch("@{(.-)}") do longest = math.max(longest, #spec) end
  check(longest <= 512, "lmarket buy: markup specs fit 512 chars (" .. longest .. ")")
end

-- 15 Oct: 'vbuild list' calls it Levy Grounds, Guild.City calls it "herbyrgi" - the planner
-- read a T5 Levy Grounds as T0 and every building requiring it as locked.
do
  fresh_client(); dofile(PLUGIN); fire_timers()
  -- two triggers share the framed-line pattern (this scan and the stock report): feed both
  local function L(x)
    for _, t in ipairs(triggers) do if t.pattern == [[^-~\*(.*)]] then t.run(x) end end
  end
  alias("^build scan$")
  for _, x in ipairs({
    "  Available Buildings                                  *~-",
    "  Levy Grounds          Req: none                      *~-",
    "  Tier 1: 1,000 daler                                  *~-",
    "  + 10 Timber                                          *~-",
    "  Tier 2: 2,000 daler                                  *~-",
    "  + 20 Timber                                          *~-",
    "  Siege Workshop        Req: Levy Grounds tier 3       *~-",
    "  Tier 3: 3,000 daler                                  *~-",
    "  + 30 Timber                                          *~-",
    "  Commands: 'vbuild start <building>'                  *~-",
  }) do L(x) end
  check(said("scanned 2 buildings from vbuild list"), "levy grounds: the scan read both buildings")
  gmcp("Guild.State", '{ "daler": 900000, "guild": "viking" }')
  gmcp("Guild.City", '{ "pages": 1, "page": 1, "guild": "viking", "buildings": [ { "tier": 5, "id": "herbyrgi" }, { "tier": 2, "id": "siege_workshop" } ] }')
  fire_timers()
  local b = st("builds")
  check(not b:find("Levy Grounds", 1, true), "levy grounds: a T5 herbyrgi is maxed, so it is hidden - not offered as T0>1 ("
    .. (b:match("[^\n]*Levy Grounds[^\n]*") or "") .. ")")
  local sw = b:match("[^\n]*Siege Workshop[^\n]*") or ""
  check(sw:find("T2>3", 1, true) ~= nil and not sw:find("req", 1, true),
    "levy grounds: the building that requires it is not locked behind it (" .. sw .. ")")
  alias("^build all$")
  fire_timers()                                -- the tab redraws through its 1s flush
  check((st("builds"):match("[^\n]*Levy Grounds[^\n]*") or ""):find("MAX (T5)", 1, true) ~= nil,
    "levy grounds: shown with maxed buildings, it reads MAX (T5)")
end

print(string.format("vikingstatus_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
