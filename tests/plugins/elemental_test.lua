-- Harness for 3s-elemental. The payloads are the plugin's real input: every one is taken
-- from Keyser's two captures of 25 Nov 2026 (gmcp-fields-Keyser-20261125-135644.md and
-- -141337.md, the second with a wave going up), the gentech
-- ones included - the character arrived as a gentech and gswapped, which is the case the
-- guild check exists for.
--
--   lua5.4 elemental_test.lua      (from anywhere; tests/plugins/run.sh runs them all, as CI does)

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-elemental/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

-- ---- a small JSON reader ----
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

-- ---- the client, with a clock the test owns ----
local printed, gmcp_hooks, aliases, state, panel, timers, next_id
local clock = 1000000
local real_time = os.time
os.time = function(t) if t then return real_time(t) end return clock end

local function fresh_client()
  printed, gmcp_hooks, aliases = {}, {}, {}
  state, panel, timers, next_id = {}, nil, {}, 0
  clock = 1000000
  scrye = {
    id = "3s-elemental",
    print = function(x) printed[#printed + 1] = tostring(x) end,
    json = { decode = decode },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addPanel = function(pn) panel = pn end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    after = function(d, fn) next_id = next_id + 1; timers[next_id] = fn; return next_id end,
    every = function() return 0 end,
    cancel = function() end,
  }
end

local function gmcp(pkg, json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == pkg then h.fn(json) end end
end
local function fire_timers()
  for _ = 1, 4 do
    local due = {}
    for id, fn in pairs(timers) do due[#due + 1] = { id = id, fn = fn } end
    if #due == 0 then return end
    table.sort(due, function(a, b) return a.id < b.id end)
    for _, d in ipairs(due) do timers[d.id] = nil; d.fn() end
  end
end
local function st(k) return tostring(state["plugin.3s-elemental." .. k] or "") end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end
local function line_of(text, needle)
  for l in text:gmatch("[^\n]+") do if l:find(needle, 1, true) then return l end end
  return ""
end
local function at(text, needle, from) return text:find(needle, from or 1, true) end
-- the bar: how many cells are lit and how many are not
local FULL, EMPTY = "\u{2588}", "\u{2591}"
local function lit(line) return select(2, line:gsub(FULL, "")) end
local function unlit(line) return select(2, line:gsub(EMPTY, "")) end

-- ---- payloads, verbatim from the capture ----
local GT_INFO  = '{ "co_title": "Chief Science Officer", "admin_title": "Gen-High Council", "echelon_max": 0, "echelon_pct": 13, "guild_age_secs": 4417364, "num_recruits": 0, "echelon_gexp": 16, "echelon_insignia": "<*>", "division": "Offense", "full": 1, "rank_title": "Leader", "echelon": 2, "guild": "gentech", "echelon_title": "Speaker for the Barren Wastes", "echelon_required": 120, "class_name": "Science" }'
local GT_STATE = '{ "pu": 7611, "guild": "gentech", "edna_secs": 2598 }'
local INFO     = '{ "reset": "60.4", "lifetime_energy": 473136584, "energy": 1561, "link_energy": 442065125, "max_energy": 1576, "gtnl": 30542398, "full": 1, "energy_to_spend": 9510296, "used_energy": 463776221, "guild": "elemental" }'
local INFO_D1  = '{ "reset": "60.7", "guild": "elemental", "energy": 1562 }'
local INFO_END = '{ "reset": "65.8", "gtnl": 30529438, "lifetime_energy": 473149544, "used_energy": 463789181, "energy_to_spend": 9523256, "guild": "elemental", "link_energy": 442078085, "energy": 1524 }'
local STATE    = '{ "defs": { "dissipated": "", "wave": "", "eshield_rounds": 0, "barrier": "", "engulfed": "", "siphon": "", "diffused": "", "negation": " RA IC AC FI CR TO SH MA SL ME" }, "guild_title": "the time elemental", "form": "Time", "vortex_cost": 182500, "full": 1, "current_vortex": 52, "max_vortex": 52, "size": 1550, "guild": "elemental" }'
local EXTRA    = '{ "resolve_type": "mental", "engulfs": 14, "panic_action": "evoke rebuild", "emit": 16, "consistency": 100, "resolve_efficiency": 100, "blasts": 5, "equipollent": "Off", "shrouds": 5, "eshield_remaining": 2, "full": 1, "panic_hp": 3110, "waves": 7, "prismatic_emission": "mental", "guild": "elemental", "natural_convergence": 2 }'
-- Guild.Skills as the fourth capture caught it at login: four pages, then a one-key tick
local SK1 = '{ "full": 1, "pages": 4, "energy_to_spend": 9746552, "guild": "elemental", "page": 1 }'
local SK2 = '{ "full": 1, "pages": 4, "skills": [ { "total": 7, "current": 7, "name": "Manifestation", "cost": -1, "id": "manifestation" }, { "total": 234, "current": 234, "name": "Continuance", "cost": -1, "id": "continuance" }, { "total": 234, "current": 234, "name": "Control", "cost": -1, "id": "control" }, { "total": 68, "current": 67, "name": "Cohesion", "cost": 4493500, "id": "cohesion" }, { "total": 132, "current": 130, "name": "Resistance", "cost": 595000, "id": "resistance" }, { "total": 52, "current": 52, "name": "Vortex", "cost": -1, "id": "vortex" } ], "guild": "elemental", "page": 2 }'
local SK3 = '{ "full": 1, "pages": 4, "skills": [ { "total": 89, "current": 88, "name": "Resolve", "cost": 1131000, "id": "resolve" }, { "total": 103, "current": 103, "name": "Intensity", "cost": -1, "id": "intensity" }, { "total": 15, "current": 15, "name": "Efficacy", "cost": -1, "id": "efficacy" }, { "total": 52, "current": 52, "name": "Blast", "cost": -1, "id": "blast" }, { "total": 12, "current": 12, "name": "Elucidation", "cost": -1, "id": "elucidation" }, { "total": 12, "current": 12, "name": "Vinculum", "cost": -1, "id": "vinculum" } ], "guild": "elemental", "page": 3 }'
local SK4 = '{ "full": 1, "pages": 4, "skills": [ { "total": 59, "current": 57, "name": "Equipollence", "cost": 1029750, "id": "equipollence" }, { "total": 87, "current": 59, "name": "Focus", "cost": 13858111, "id": "focus" }, { "total": 20, "current": 20, "name": "Finesse", "cost": -1, "id": "finesse" }, { "total": 51, "current": 48, "name": "Manipulation", "cost": 1167000, "id": "manipulation" } ], "guild": "elemental", "page": 4 }'
local SK_TICK = '{ "guild": "elemental", "energy_to_spend": 9746600 }'
local EXTRA_WV = '{ "guild": "elemental", "waves": 6 }'
local STATE_WV = '{ "guild": "elemental", "defs": { "dissipated": "", "wave": "WV", "eshield_rounds": 0, "barrier": "", "engulfed": "", "siphon": "", "diffused": "", "negation": " RA IC AC FI CR TO SH MA SL ME" } }'

-- 1. nothing yet
fresh_client(); dofile(PLUGIN)
check(at(st("status"), "waiting for Guild.State") and at(st("energy_tab"), "waiting for Guild.Info")
      and at(st("setup"), "waiting for Guild.Extra") and at(st("skills"), "waiting for Guild.Skills"), "empty: every tab waits")
check(at(st("summary"), "waiting") ~= nil, "empty: summary waits")
check(panel ~= nil and panel.title == "Elemental" and #panel.tabs == 4 and panel.tabs[3].title == "Skills", "the panel has four tabs")
do
  local known = { value = 1, text = 1, label = 1, gauge = 1, list = 1, table = 1, buttonrow = 1, input = 1, row = 1 }
  local bad, binds = 0, {}
  for _, t in ipairs(panel.tabs) do for _, w in ipairs(t.widgets) do
    if not known[w.type] or not w.bind then bad = bad + 1 end
    binds[w.bind or "?"] = true
  end end
  check(bad == 0, "every widget has a type the host knows and a bind")
  local unset = 0
  for b in pairs(binds) do if state[b] == nil then unset = unset + 1 end end
  check(unset == 0, "every bind is a state key the plugin writes")
end

-- 2. a gentech's packages are not an elemental's
gmcp("Guild.Info", GT_INFO); gmcp("Guild.State", GT_STATE); fire_timers()
check(at(st("status"), "waiting for Guild.State") and at(st("status"), "in the gentech guild"), "gentech messages are ignored, and the wait says why")
check(st("summary") == "in the gentech guild", "summary names the other guild (" .. st("summary") .. ")")
check(not at(st("status"), "Chief Science") and not at(st("energy_tab"), "echelon"), "nothing of the gentech's leaks in")

-- 3. the gswap: Guild.Info, State and Extra as an elemental
gmcp("Guild.Info", INFO); gmcp("Guild.State", STATE); gmcp("Guild.Extra", EXTRA); fire_timers()
local s = st("status")
check(not at(s, "gentech"), "after the swap the other-guild note is gone")
check(at(s, "-- the time elemental --") and at(s, "Form         Time") and at(s, "Size         1,550"), "title, form and size")
check(lit(line_of(s, "Energy")) == 10 and unlit(line_of(s, "Energy")) == 0 and at(line_of(s, "Energy"), "@{success}1,561@{}@{#3DB8FF} / 1,576@{}"),
      "energy 1,561 of 1,576 is a full bar (" .. line_of(s, "Energy") .. ")")
check(at(line_of(s, "Energy"), "Energy       @{#1B3A8C}" .. FULL .. "@{}") and at(line_of(s, "Energy"), "@{#9FD8FF}" .. FULL .. "@{}  "),
      "the bar runs from dark blue to light blue")
do
  local prev, rising, n = -1, true, 0
  for hex in line_of(s, "Energy"):gmatch("@{#(%x%x%x%x%x%x)}" .. FULL) do
    local v = tonumber(hex:sub(1, 2), 16) + tonumber(hex:sub(3, 4), 16) + tonumber(hex:sub(5, 6), 16)
    if v <= prev then rising = false end
    prev, n = v, n + 1
  end
  check(n == 10 and rising, "every cell is lighter than the one before it")
end
check(not at(s, "#####") and not at(s, "...."), "no # or . bars are left")
check(at(line_of(s, "Vortex"), "@{success}52@{}@{#3DB8FF} / 52@{}") and not at(s, "cost") and not at(s, "182,500"), "vortexes, the maximum in bright blue and no cost")
check(at(line_of(s, "Reset"), "60.4%") and lit(line_of(s, "Reset")) == 6 and unlit(line_of(s, "Reset")) == 4
      and at(line_of(s, "Reset"), "@{dim}" .. string.rep(EMPTY, 4) .. "@{}") and not at(line_of(s, "Reset"), "#9FD8FF"), "reset clock: the string 60.4 is six cells, none of them the lightest (" .. line_of(s, "Reset") .. ")")
check(line_of(s, "Reset"):match("60%.4%%$") ~= nil, "reset clock: no time left until it has been watched")
check(at(line_of(s, "Negation"), "@{success}RA IC AC FI CR TO SH MA SL ME@{}"), "negation lists its damage types, the leading space trimmed (" .. line_of(s, "Negation") .. ")")
check(at(line_of(s, "Barrier"), "@{dim}--@{}") and at(line_of(s, "Dissipated"), "@{dim}--@{}"), "an empty defence is down")
check(at(s, "Negation") < at(s, "Barrier") and at(s, "Barrier") < at(s, "Wave") and at(s, "Diffused") < at(s, "Dissipated"), "defences in reading order")
check(at(line_of(s, "E-shield"), "down") and at(line_of(s, "E-shield"), "2 remaining"), "e-shield down, two remaining (" .. line_of(s, "E-shield") .. ")")
check(not at(s, "Eshield rounds"), "eshield_rounds is not listed a second time")
check(at(line_of(s, "Fires"), "below 3,110 hp") and at(line_of(s, "Fires"), "evoke rebuild") and not at(line_of(s, "Fires"), "of max"), "panic line, no share without Char.Vitals")
check(at(line_of(s, "Blasts"), "Blasts       @{text}5@{}") and at(line_of(s, "Waves"), "@{text}7@{}") and at(line_of(s, "Convergence"), "@{text}2@{}")
      and at(line_of(s, "Engulfs"), "@{text}14@{}") and at(line_of(s, "Shrouds"), "@{text}5@{}"), "the five charges")
check(at(s, "Convergence") < at(s, "given back when the reset clock turns") and at(s, "given back when the reset clock turns") < at(s, "Engulfs"),
      "blasts, waves and convergence sit above the reset note, engulfs and shrouds below it")
check(not at(line_of(s, "given back"), ", in ~"), "the note carries no time until the clock has been watched")
check(at(s, "Shrouds") < at(s, "given back at the daily reset") and at(s, "given back at the daily reset") < at(s, "-- Panic --"), "engulfs and shrouds carry the daily-reset note")
check(not at(s, "Emit"), "emit is a setting, not a charge: not on Status")
check(st("summary") == "Time   Energy 1,561/1,576   Vortex 52/52   Reset 60%", "summary (" .. st("summary") .. ")")
check(st("energy") == "1561" and st("energy_max") == "1576", "the pool is published as numbers")

local e = st("energy_tab")
check(at(e, "Still owed   30,542,398 gexp") and at(line_of(e, "At this pace"), "watching"), "next level: owed, pace not yet known")
check(at(line_of(e, "Lifetime"), "473,136,584") and at(line_of(e, "Used"), "463,776,221")
      and at(line_of(e, "To spend"), "9,510,296") and at(line_of(e, "Link"), "442,065,125"), "the four totals")
check(at(line_of(e, "Gained"), "Gained       0") , "session: nothing gained yet")

local u = st("setup")
check(at(line_of(u, "Fires"), "below 3,110 hp") and at(u, "Type         mental") and at(line_of(u, "Efficiency"), "@{success}100%@{}"), "setup: panic and resolve")
check(at(u, "Prismatic    mental") and at(line_of(u, "Equipollent"), "@{dim}Off@{}") and at(line_of(u, "Consistency"), "100%"), "setup: emission block")
check(at(line_of(u, "Emit"), "Emit         16") and at(line_of(u, "Emit"), "power output") and at(u, "Emit") < at(u, "Prismatic"), "setup: emit is the power output (" .. line_of(u, "Emit") .. ")")
check(not at(u, "Blasts") and not at(u, "Engulfs") and not at(u, "Convergence") and not at(u, "Counts"), "setup: the charges are not repeated here")
check(not at(u, "Also sent"), "setup: nothing left over in this capture")

-- 3b. a wave goes up, as the second capture caught it: one charge fewer, the defence on
gmcp("Guild.Extra", EXTRA_WV); gmcp("Guild.State", STATE_WV); fire_timers()
s = st("status")
check(at(line_of(s, "Waves"), "@{text}6@{}") and at(line_of(s, "Blasts"), "@{text}5@{}") and at(line_of(s, "Engulfs"), "@{text}14@{}"), "a spent wave: 7 becomes 6, the other charges stay")
check(at(line_of(s, "Wave  "), "@{success}WV@{}") and at(line_of(s, "Negation"), "RA IC AC"), "the wave defence is up (" .. line_of(s, "Wave  ") .. ")")
check(at(s, "-- the time elemental --") and at(s, "Form         Time") and at(line_of(s, "Vortex"), "52"), "a Guild.State without full keeps title, form and vortexes")
check(at(line_of(st("setup"), "Emit"), "16") and at(st("setup"), "evoke rebuild"), "a one-key Guild.Extra keeps the settings")

-- 4. deltas merge; max_energy survives a message that does not carry it
clock = clock + 2
gmcp("Guild.Info", INFO_D1); fire_timers()
check(at(line_of(st("status"), "Energy"), "1,562@{}@{#3DB8FF} / 1,576"), "delta: energy moves, the max stays")
check(at(line_of(st("energy_tab"), "Lifetime"), "473,136,584"), "delta: totals it did not carry stay")

-- 5. the reset clock's time left: 0.3 a tick, 2 s a tick -> from 66.4 that is 224 s
fresh_client(); dofile(PLUGIN)
gmcp("Guild.Info", INFO)
for i = 1, 20 do
  clock = clock + 2
  gmcp("Guild.Info", string.format('{ "reset": "%.1f", "guild": "elemental", "energy": 1576 }', 60.4 + 0.3 * i))
end
fire_timers()
check(at(line_of(st("status"), "Reset"), "66.4%") and at(line_of(st("status"), "Reset"), "66.4%   @{#3DB8FF}3m 44s@{}") and not at(line_of(st("status"), "Reset"), "charges"), "reset clock: only the timer after the percent, in bright blue (" .. line_of(st("status"), "Reset") .. ")")
gmcp("Guild.Extra", EXTRA); fire_timers()
check(at(line_of(st("status"), "given back"), "turns, in ~3m 44s"), "the charges note carries the same time (" .. line_of(st("status"), "given back") .. ")")
-- it turns: lower than last time, so the measuring starts over
clock = clock + 2
gmcp("Guild.Info", '{ "reset": "0.2", "guild": "elemental" }'); fire_timers()
check(lit(line_of(st("status"), "Reset")) == 0 and unlit(line_of(st("status"), "Reset")) == 10, "reset clock: just turned, an empty bar")
check(at(line_of(st("status"), "Reset"), "0.2%") and line_of(st("status"), "Reset"):match("0%.2%%$") ~= nil
      and not at(line_of(st("status"), "given back"), ", in ~"), "reset clock: after turning, no stale time left")
for i = 1, 15 do
  clock = clock + 2
  gmcp("Guild.Info", string.format('{ "reset": "%.1f", "guild": "elemental" }', 0.2 + 0.3 * i))
end
fire_timers()
check(at(line_of(st("status"), "Reset"), "4.7%") and at(line_of(st("status"), "Reset"), "@{#3DB8FF}10m 35s@{}"), "reset clock: measured afresh from the turn (" .. line_of(st("status"), "Reset") .. ")")

-- 6. pace and the next level: 48 energy every 2 s is 86,400 an hour
fresh_client(); dofile(PLUGIN)
gmcp("Guild.Info", INFO)
for i = 1, 30 do
  clock = clock + 2
  gmcp("Guild.Info", string.format('{ "guild": "elemental", "lifetime_energy": %d, "gtnl": %d }', 473136584 + 48 * i, 30542398 - 48 * i))
end
fire_timers()
e = st("energy_tab")
check(at(line_of(e, "Gained"), "1,440") and at(line_of(e, "Gained"), "in 1m 00s"), "session: gained 1,440 in a minute (" .. line_of(e, "Gained") .. ")")
check(at(line_of(e, "Pace"), "86,400 / hour"), "pace per hour (" .. line_of(e, "Pace") .. ")")
check(at(e, "Still owed   30,540,958 gexp") and at(line_of(e, "At this pace"), "~14d 17h"), "next level at this pace (" .. line_of(e, "At this pace") .. ")")
-- then it stops coming in: twelve idle minutes later the pace is nothing, not the old figure
for i = 1, 24 do clock = clock + 30; gmcp("Guild.Info", '{ "guild": "elemental", "energy": 1576 }') end
fire_timers()
e = st("energy_tab")
check(at(line_of(e, "Pace"), "Pace         0 / hour"), "idle: the pace falls to nothing (" .. line_of(e, "Pace") .. ")")
check(at(line_of(e, "At this pace"), "no gexp coming in"), "idle: no date for the next level (" .. line_of(e, "At this pace") .. ")")
check(at(line_of(e, "Gained"), "1,440"), "idle: what the session gained is kept")

-- 7. the last message of the capture, and the panic share once Char.Vitals has a max
fresh_client(); dofile(PLUGIN)
state["char.vitals.maxhp"] = 3534
gmcp("Guild.Info", INFO); gmcp("Guild.State", STATE); gmcp("Guild.Extra", EXTRA); gmcp("Guild.Info", INFO_END); fire_timers()
s = st("status")
check(at(line_of(s, "Energy"), "1,524@{}@{#3DB8FF} / 1,576"), "end of capture: energy 1,524 of the 1,576 the first message set")
check(at(line_of(s, "Fires"), "(88% of max)"), "panic at 3,110 of 3,534 is 88% (" .. line_of(s, "Fires") .. ")")
check(at(st("energy_tab"), "Still owed   30,529,438 gexp") and at(line_of(st("energy_tab"), "Gained"), "12,960"), "end of capture: owed and gained")

-- 8. a defence going up, an e-shield running, and a defence this plugin has never heard of
gmcp("Guild.State", '{ "defs": { "wave": " FI IC", "eshield_rounds": 7, "barrier": "", "negation": "", "mirror": "ME" }, "form": "Fire", "full": 1, "guild": "elemental" }'); fire_timers()
s = st("status")
check(at(line_of(s, "Wave"), "@{success}FI IC@{}") and at(line_of(s, "Negation"), "@{dim}--@{}"), "wave up, negation down")
check(at(line_of(s, "E-shield"), "@{success}7 rounds@{}"), "e-shield: rounds running (" .. line_of(s, "E-shield") .. ")")
check(at(line_of(s, "Mirror"), "@{success}ME@{}") and at(s, "Dissipated") == nil, "an unknown defence is still shown; one not sent is not")
check(at(s, "Form         Fire") and at(s, "-- Elemental --") and not at(s, "Vortex"), "full:1 starts the snapshot over: no stale title or vortex")
gmcp("Guild.Extra", '{ "full": 1, "guild": "elemental", "panic_hp": 0, "panic_action": "", "emit": 3, "brand_new": "yes", "equipollent": "On", "blasts": 0, "shrouds": 4 }'); fire_timers()
u = st("setup")
s = st("status")
check(at(line_of(s, "Blasts"), "@{error}0@{}") and at(line_of(s, "Shrouds"), "@{text}4@{}"), "a spent charge is red")
check(not at(s, "Waves ") and not at(s, "Engulfs") and at(s, "given back when the reset clock turns") and at(s, "daily reset"), "charges not sent are not shown; each note stays for the one that is")
gmcp("Guild.Extra", '{ "guild": "elemental", "blasts": 5 }'); fire_timers()
check(at(line_of(st("status"), "Blasts"), "@{text}5@{}") and at(line_of(st("setup"), "Emit"), "3"), "a Guild.Extra delta moves one charge and keeps the rest")
check(at(u, "-- Also sent --") and at(line_of(u, "brand_new"), "yes"), "setup: a key never seen is listed under its own name")
check(not at(u, "-- Panic --") and not at(st("status"), "-- Panic --"), "no panic block when nothing is set")
check(at(line_of(u, "Equipollent"), "@{success}On@{}") and not at(u, "Resolve"), "setup: equipollent on, no resolve block without resolve")

-- 9. gswap away: the panel keeps its figures and says they are old
gmcp("Guild.State", GT_STATE); fire_timers()
check(at(st("status"), "now in the gentech guild") and at(st("status"), "Form         Fire"), "after swapping away the status says so and keeps its last figures")
check(at(st("summary"), "(now gentech)") and at(st("energy_tab"), "now in the gentech guild") and at(st("setup"), "now in the gentech guild"), "every tab and the summary say so")
gmcp("Guild.Info", INFO_D1); fire_timers()
check(not at(st("status"), "gentech") and not at(st("summary"), "gentech"), "and back again clears it")

-- 9b. Guild.Skills: four pages make one list, in the server's order
fresh_client(); dofile(PLUGIN)
gmcp("Guild.Skills", SK1); gmcp("Guild.Skills", SK2); gmcp("Guild.Skills", SK3); fire_timers()
check(at(st("skills"), "waiting for Guild.Skills"), "skills: nothing is shown until the last page lands")
gmcp("Guild.Skills", SK4); fire_timers()
local k = st("skills")
check(at(line_of(k, "To spend"), "@{#3DB8FF}9,746,552@{}"), "skills: energy to spend (" .. line_of(k, "To spend") .. ")")
check(line_of(k, "Manifestation") == "@{#8FE38F,click=info manifestation}Manifestation@{}  @{#8FE38F}   7 / 7   @{}" and not at(k, "at its total"),
      "skills: one at its total is light green, its name and level and nothing else (" .. line_of(k, "Manifestation") .. "|)")
check(at(line_of(k, "Cohesion"), "@{#FFD84A}  67 / 68  @{}") and at(line_of(k, "Cohesion"), "@{#FFD84A}4,493,500@{}") and line_of(k, "Cohesion"):match("4,493,500@{}$") ~= nil and not at(k, "can afford")
      and not at(line_of(k, "Cohesion"), "#FF9A3D"), "skills: an affordable level is yellow (" .. line_of(k, "Cohesion") .. ")")
check(at(line_of(k, "Focus"), "@{#FF9A3D}  59 / 87  @{}") and at(line_of(k, "Focus"), "@{#FF9A3D}13,858,111@{}@{dim}   short 4,111,559@{}") and at(line_of(k, "Focus"), "@{#FF9A3D,menu=")
      and not at(line_of(k, "Focus"), "#FFD84A"), "skills: one out of reach is orange and says by how much (" .. line_of(k, "Focus") .. ")")
check(at(line_of(k, "Cohesion"), "@{#FFD84A,menu=Enhance Cohesion|enhance cohesion,rclick=enhance cohesion,click=info cohesion}Cohesion@{}       @{#FFD84A}  67 / 68"),
      "skills: left click is info, right click enhances, and the columns still line up (" .. line_of(k, "Cohesion") .. ")")
check(at(line_of(k, "Focus"), "menu=Enhance Focus|enhance focus,") ~= nil, "skills: one that is out of reach can still be asked for")
check(not at(line_of(k, "Manifestation"), "enhance") and at(line_of(k, "Vortex"), "@{#8FE38F,click=info vortex}Vortex@{}") and not at(line_of(k, "Vortex"), "menu="), "skills: a skill at its total has info on it and nothing to enhance")
check(at(k, "Manifestation") < at(k, "Vortex") and at(k, "Vortex") < at(k, "Resolve") and at(k, "Vinculum") < at(k, "Equipollence") and at(k, "Focus") < at(k, "Manipulation"), "skills: all three pages of them, in the order sent")
check(at(k, "16 skills, 10 at their total") and at(k, "5 affordable now"), "skills: the tally (" .. line_of(k, "16 skills") .. ")")
gmcp("Guild.Skills", SK_TICK); fire_timers()
k = st("skills")
check(at(line_of(k, "To spend"), "9,746,600") and at(line_of(k, "Focus"), "short 4,111,511") and at(k, "16 skills"), "skills: a tick moves the energy and keeps the list")
-- a level bought: the list comes again whole, and must replace the old one, not join it
gmcp("Guild.Skills", '{ "full": 1, "pages": 2, "energy_to_spend": 100, "guild": "elemental", "page": 1 }')
gmcp("Guild.Skills", '{ "full": 1, "pages": 2, "skills": [ { "total": 68, "current": 68, "name": "Cohesion", "cost": -1, "id": "cohesion" }, { "total": 9, "current": 2, "cost": 500, "id": "new_thing" }, { "total": 5, "current": 3, "name": "Odd", "cost": -1, "id": "odd" } ], "guild": "elemental", "page": 2 }')
fire_timers()
k = st("skills")
check(at(k, "3 skills, 1 at their total") and not at(k, "Focus") and at(line_of(k, "Cohesion"), "@{#8FE38F}  68 / 68  @{}") and not at(line_of(k, "Cohesion"), "enhance"), "skills: a fresh burst replaces the list (" .. line_of(k, "skills,") .. ")")
check(at(line_of(k, "New thing"), "menu=Enhance New thing|enhance new_thing,rclick=enhance new_thing,click=info new_thing}") and not at(line_of(k, "Odd"), "menu=")
      and at(line_of(k, "Odd"), "@{text,click=info odd}Odd@{}"), "skills: the command uses the id; no cost, no menu")
check(at(line_of(k, "New thing"), "   2 / 9") and at(line_of(k, "New thing"), "@{#FF9A3D}500@{}@{dim}   short 400@{}") and not at(k, "affordable now"), "skills: a skill with no name shows its id; nothing affordable, no green tally")
check(at(line_of(k, "Odd"), "   3 / 5") and at(line_of(k, "Odd"), "@{dim}  --@{}") and not at(line_of(k, "Odd"), "#"), "skills: no cost and not at its total is not called finished (" .. line_of(k, "Odd") .. ")")
gmcp("Guild.Skills", '{ "guild": "gentech", "full": 1, "skills": [ { "id": "splice", "name": "Splice", "current": 1, "total": 2, "cost": 5 } ] }'); fire_timers()
check(not at(st("skills"), "Splice") and at(st("skills"), "now in the gentech guild"), "skills: another guild's Guild.Skills is not ours")

-- a whole burst with no list in it: the old list does not linger
gmcp("Guild.Skills", '{ "full": 1, "pages": 2, "energy_to_spend": 100, "guild": "elemental", "page": 1 }')
gmcp("Guild.Skills", '{ "full": 1, "pages": 2, "guild": "elemental", "page": 2 }'); fire_timers()
check(at(st("skills"), "waiting for Guild.Skills") and not at(st("skills"), "Cohesion"), "skills: a full burst without a list clears it")

-- an id or a name that would break the markup gets no menu, and its @ is still escaped
gmcp("Guild.Skills", '{ "full": 1, "guild": "elemental", "energy_to_spend": 9, "skills": [ { "id": "a,click=quit", "name": "Bad", "current": 1, "total": 2, "cost": 5 }, { "id": "ok", "name": "x|y@{error}", "current": 1, "total": 2, "cost": 5 }, { "id": "m;x", "name": "Full", "current": 2, "total": 2, "cost": -1 } ] }'); fire_timers()
k = st("skills")
check(not at(k, "menu=") and not at(k, "click=") and at(k, "@{#FFD84A}x|y@@{error}@{}") and at(k, "@{#FFD84A}Bad@{}") and at(k, "@{#8FE38F}Full@{}"), "skills: an unsafe id or name keeps its colour but gets no command")
gmcp("Guild.Skills", '{ "guild": "elemental", "energy_to_spend": 5 }'); fire_timers()
check(at(st("skills"), "@{#FFD84A}Bad@{}") and at(st("skills"), "2 affordable now"), "skills: exactly enough energy is enough")
gmcp("Guild.Skills", '{ "guild": "elemental", "energy_to_spend": 4 }'); fire_timers()
check(at(st("skills"), "@{#FF9A3D}Bad@{}") and not at(st("skills"), "#FFD84A"), "skills: yellow turns orange the moment the energy no longer covers it")

-- 10. elhud
fresh_client(); dofile(PLUGIN)
gmcp("Guild.Info", INFO); gmcp("Guild.State", STATE); fire_timers()
printed = {}
for _, a in ipairs(aliases) do if a.pattern == "^elhud$" then a.run() end end
check(said("[elemental]") and said("Form         Time") and said("Negation"), "elhud: prints the status page")

-- 11. the markup never lets a server @ through as a colour code; junk does not throw
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", '{ "guild": "elemental", "full": 1, "guild_title": "a@{error}bad", "form": "x@y", "defs": { "negation": "@{error}Z" } }')
gmcp("Guild.Extra", '{ "guild": "elemental", "full": 1, "panic_action": "p@{error}q", "panic_hp": 5 }')
gmcp("Guild.Info", "not json"); gmcp("Guild.Info", "[1,2]"); gmcp("Guild.State", '{ "guild": "elemental", "defs": 3 }')
fire_timers()
check(at(st("status"), "a@@{error}bad") and at(st("status"), "x@@y") and at(st("status"), "p@@{error}q"), "@ in server text is escaped")
check(at(st("status"), "-- Defences --") == nil, "defs that is not a table shows no defences block, and nothing throws")

print(string.format("elemental_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
