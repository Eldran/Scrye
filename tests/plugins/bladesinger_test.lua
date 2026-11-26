-- Harness for 3s-bladesinger. The payloads are the plugin's real input: every one is taken
-- from Erebseth's capture of 26 Nov 2026 (gmcp-fields-erebseth-20261126-083200.md) - rune
-- inscribing in the guild hall, then a Dungeon of Shame run. The gentech pair is from
-- Keyser's capture the day before: these package names are shared by every guild, and a
-- character can gswap.
--
--   lua5.4 bladesinger_test.lua    (from anywhere; tests/plugins/run.sh runs them all, as CI does)

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-bladesinger/main.lua"

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
    id = "3s-bladesinger",
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
local function st(k) return tostring(state["plugin.3s-bladesinger." .. k] or "") end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end
local function line_of(text, needle)
  for l in text:gmatch("[^\n]+") do if l:find(needle, 1, true) then return l end end
  return ""
end
local function last_line_of(text, needle)
  local hit = ""
  for l in text:gmatch("[^\n]+") do if l:find(needle, 1, true) then hit = l end end
  return hit
end
local function at(text, needle, from) return text:find(needle, from or 1, true) end
-- the bar: how many cells are lit and how many are not
local FULL, EMPTY = "\u{2588}", "\u{2591}"
local function lit(line) return select(2, line:gsub(FULL, "")) end
local function unlit(line) return select(2, line:gsub(EMPTY, "")) end

-- ---- payloads, verbatim from the capture ----
local GT_INFO  = '{ "co_title": "Chief Science Officer", "echelon": 2, "full": 1, "guild": "gentech", "class_name": "Science" }'
local GT_STATE = '{ "pu": 7611, "guild": "gentech", "edna_secs": 2598 }'
local STATE    = '{ "reset": 49.629629629629626, "gxp_last_round": 0, "evade_cmd": "", "evade2_var": 0, "sokra_max": 3700, "evade2_cmd": "", "evade_hp": 0, "gxp": 8855067, "nekra_max": 5100, "gtnl": 29994933, "full": 1, "nekra": 5100, "sokra": 3700, "portal_reset": 41.5, "evade2_level": 0, "guild": "bladesinger" }'
local STATE_D1 = '{ "portal_reset": 41.75, "reset": 49.703703703703702, "guild": "bladesinger" }'
local STATE_F1 = '{ "reset": 78.81481481481481, "gtnl": 29084505, "nekra": 4470, "portal_reset": 77.5, "guild": "bladesinger", "gxp": 9765495 }'
local STATE_F2 = '{ "reset": 78.888888888888886, "gtnl": 29083238, "nekra": 4444, "portal_reset": 77.75, "guild": "bladesinger", "gxp": 9766762 }'
local INFO     = '{ "nekra_regen": 0, "sp_regen": 0, "sokra_max_regen": 10, "runes_prepared": 1201, "sokra_regen": 0, "hp_max_regen": 300, "runes_inscribed": 6570, "hp_regen": 0, "full": 1, "nekra_max_regen": 10, "glevel": 50, "guild": "bladesinger", "sp_max_regen": 30 }'
local INFO_D1  = '{ "guild": "bladesinger", "runes_prepared": 1218 }'
local INFO_D2  = '{ "guild": "bladesinger", "runes_inscribed": 6714 }'
local INFO_D3  = '{ "hp_regen": 120, "nekra_max_regen": 18, "sokra_max_regen": 18, "sp_regen": 8, "guild": "bladesinger" }'
local INFO_D4  = '{ "guild": "bladesinger", "sokra_regen": 23, "sp_regen": 10 }'
local EXTRA    = '{ "runes": { "fluidity": 0, "starfire": 218, "venom": 176, "reckoning": 0, "health": 517, "winter": 200, "eternity": 160, "fortitude": 45, "spirit": 485, "thought": 209, "blight": 9, "retaliation": 0, "resolution": 20, "corrosion": 133, "knowledge": 60, "power": 676, "brawn": 1, "sending": 117, "charm": 0, "stone": 267, "spikes": 12, "battle": 242, "alacrity": 60, "force": 0, "storms": 164, "decay": 186, "rejuvenation": 433, "strategem": 12, "revalrie": 432, "understanding": 0, "enspellment": 48, "balefire": 248, "egress": 0 }, "full": 1, "guild": "bladesinger" }'
local EXTRA_END = '{ "guild": "bladesinger", "runes": { "fluidity": 0, "starfire": 224, "venom": 183, "reckoning": 0, "health": 522, "winter": 204, "eternity": 160, "fortitude": 49, "spirit": 490, "thought": 213, "blight": 10, "retaliation": 0, "resolution": 22, "corrosion": 139, "knowledge": 66, "power": 692, "brawn": 1, "sending": 117, "charm": 0, "stone": 275, "spikes": 12, "battle": 247, "alacrity": 66, "force": 0, "storms": 166, "decay": 187, "rejuvenation": 434, "strategem": 13, "revalrie": 432, "understanding": 4, "enspellment": 52, "balefire": 251, "egress": 0 } }'

-- 1. nothing yet
fresh_client(); dofile(PLUGIN)
check(at(st("status"), "waiting for Guild.State") and at(st("guild_tab"), "waiting for Guild.State")
      and at(st("runes"), "waiting for Guild.Extra") and at(st("setup"), "waiting for Guild.State"), "empty: every tab waits")
check(at(st("summary"), "waiting") ~= nil, "empty: summary waits")
check(panel ~= nil and panel.title == "Bladesinger" and #panel.tabs == 4 and panel.tabs[3].title == "Runes", "the panel has four tabs")
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

-- 2. another guild's packages are not a bladesinger's
gmcp("Guild.Info", GT_INFO); gmcp("Guild.State", GT_STATE); fire_timers()
check(at(st("status"), "waiting for Guild.State") and at(st("status"), "in the gentech guild"), "gentech messages are ignored, and the wait says why")
check(st("summary") == "in the gentech guild", "summary names the other guild (" .. st("summary") .. ")")
check(not at(st("status"), "Chief Science") and not at(st("setup"), "edna_secs") and not at(st("setup"), "pu "), "nothing of the gentech's leaks in")

-- 3. login as a bladesinger: the whole messages
gmcp("Guild.State", STATE); gmcp("Guild.Info", INFO); gmcp("Guild.Extra", EXTRA); fire_timers()
local s = st("status")
check(not at(s, "gentech"), "after the swap the other-guild note is gone")
check(at(s, "-- Bladesinger --") and at(s, "Guild level  50"), "heading and guild level")
check(lit(line_of(s, "Nekra  ")) == 10 and at(line_of(s, "Nekra  "), "@{success}5,100@{}@{#D6E2F2} / 5,100@{}"),
      "nekra 5,100 of 5,100 is a full bar (" .. line_of(s, "Nekra  ") .. ")")
check(lit(line_of(s, "Sokra  ")) == 10 and at(line_of(s, "Sokra  "), "3,700@{}@{#D6E2F2} / 3,700"), "sokra 3,700 of 3,700")
check(at(line_of(s, "Nekra  "), "@{#4A1F7A}" .. FULL) and at(line_of(s, "Sokra  "), "@{#7A5612}" .. FULL)
      and not at(line_of(s, "Sokra  "), "#4A1F7A"), "the two pools have their own colours")
check(lit(line_of(s, "Blur reset")) == 5 and unlit(line_of(s, "Blur reset")) == 5 and at(line_of(s, "Blur reset"), "49.6%"), "reset 49.6% is half a bar")
check(lit(line_of(s, "Portal reset")) == 4 and at(line_of(s, "Portal reset"), "41.5%"), "portal 41.5%")
check(not at(line_of(s, "Blur reset"), "m ") and not at(line_of(s, "Blur reset"), "#D6E2F2"), "no time left on a clock that has only just been seen")
check(at(s, "-- Regeneration --") and at(line_of(s, "HP   "), "@{dim}      0@{}     300@{#D6E2F2}        0@{}") and not at(s, "over the max")
      and at(s, "  runes     max   in use"),
      "regen: nothing from runes yet, against a max of 300 (" .. line_of(s, "HP   ") .. ")")
check(not at(s, "-- Evade --"), "no evade block while nothing is set")
check(st("summary") == "Nekra 5,100/5,100   Sokra 3,700/3,700   Blur 50%   Portal 42%", "summary (" .. st("summary") .. ")")

-- 4. the guild level: 8,855,067 into a level of 38,850,000
local g = st("guild_tab")
check(at(g, "-- Guild level 50 --") and at(g, "Earned       8,855,067 gxp") and at(g, "Still owed   29,994,933 gxp"), "guild: earned and owed")
check(at(line_of(g, "Progress"), "22.8%") and lit(line_of(g, "Progress")) == 2, "guild: 22.8% of the level (" .. line_of(g, "Progress") .. ")")
check(at(g, "Last round   0") and at(line_of(g, "At this pace"), "watching..."), "guild: last round, and no pace yet")
check(at(g, "Gained       0"), "guild: nothing gained at first sight")

-- 5. deltas merge: the clocks move, the maxima stay
clock = clock + 2
gmcp("Guild.State", STATE_D1); fire_timers()
s = st("status")
check(at(line_of(s, "Blur reset"), "49.7%") and (at(line_of(s, "Portal reset"), "41.8%") or at(line_of(s, "Portal reset"), "41.7%")), "a delta moves the clocks")
check(at(line_of(s, "Nekra  "), "5,100@{}@{#D6E2F2} / 5,100") and at(s, "Guild level  50"), "a delta leaves the pools and their maxima alone")
check(not at(line_of(s, "Blur reset"), "#D6E2F2") and not at(line_of(s, "Portal reset"), "#D6E2F2"), "two seconds of watching is not enough to time a clock")

-- 6. the clocks are timed from what the server sends: 2/27 and 0.25 a 2 s tick
do
  local r, p = 49.703703703703702, 41.75
  for _ = 1, 15 do
    clock = clock + 2; r = r + 2 / 27; p = p + 0.25
    gmcp("Guild.State", string.format('{ "portal_reset": %.4f, "reset": %.12f, "guild": "bladesinger" }', p, r))
  end
  fire_timers()
  s = st("status")
  -- reset: (100 - 50.81) / (2/27 per 2 s) = 1,328 s = 22m 08s; portal: (100 - 45.5) / 0.125 = 436 s = 7m 16s
  check(at(line_of(s, "Blur reset"), "@{#D6E2F2}22m 08s@{}"), "reset: time until it turns (" .. line_of(s, "Blur reset") .. ")")
  check(at(line_of(s, "Portal reset"), "@{#D6E2F2}7m 16s@{}"), "portal: time until it turns (" .. line_of(s, "Portal reset") .. ")")
  -- the portal clock turns: the pace measured before is kept, so the time is there at once
  clock = clock + 2
  gmcp("Guild.State", '{ "portal_reset": 0.25, "reset": 50.9, "guild": "bladesinger" }'); fire_timers()
  s = st("status")
  check((at(line_of(s, "Portal reset"), "  0.3%") or at(line_of(s, "Portal reset"), "  0.2%")) ~= nil, "portal: it has turned")
  check(lit(line_of(s, "Portal reset")) == 0 and at(line_of(s, "Portal reset"), "@{#D6E2F2}13m 18s@{}"), "portal: a turned clock keeps its pace (" .. line_of(s, "Portal reset") .. ")")
  check(at(line_of(s, "Blur reset"), "m "), "reset: untouched by the other clock turning")
  -- and it is timed afresh from the turn, not from where it stood before it: 199 ticks on,
  -- at 50%, there are (100 - 50) / 0.125 = 400 s left
  do
    local p2 = 0.25
    for _ = 1, 199 do
      clock = clock + 2; p2 = p2 + 0.25
      gmcp("Guild.State", string.format('{ "portal_reset": %.2f, "guild": "bladesinger" }', p2))
    end
    fire_timers()
    s = st("status")
    check(at(line_of(s, "Portal reset"), "50.0%") and at(line_of(s, "Portal reset"), "@{#D6E2F2}6m 40s@{}"), "portal: timed from the turn (" .. line_of(s, "Portal reset") .. ")")
  end
end

-- a relog: the clock is next seen much further on, after a gap nobody watched. That gap
-- colours the pace for two minutes at most.
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", STATE)
do
  local p = 41.5
  for _ = 1, 15 do clock = clock + 2; p = p + 0.25; gmcp("Guild.State", string.format('{ "portal_reset": %.2f, "guild": "bladesinger" }', p)) end
  clock = clock + 3600                       -- an hour away, and the clock stood still
  p = 60
  for _ = 1, 70 do clock = clock + 2; p = p + 0.25; gmcp("Guild.State", string.format('{ "portal_reset": %.2f, "guild": "bladesinger" }', p)) end
  fire_timers()
  -- at 77.5%: (100 - 77.5) / 0.125 = 180 s
  check(at(line_of(st("status"), "Portal reset"), "77.5%") and at(line_of(st("status"), "Portal reset"), "@{#D6E2F2}3m 00s@{}"), "portal: an unwatched gap drops out of the pace (" .. line_of(st("status"), "Portal reset") .. ")")
end

-- 7. a fight: nekra drains, gxp comes in and gtnl falls by the same
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", STATE); gmcp("Guild.Info", INFO)
do
  local gxp, gtnl, nekra = 8855067, 29994933, 5100
  for _ = 1, 20 do
    clock = clock + 2; gxp = gxp + 1267; gtnl = gtnl - 1267; nekra = nekra - 26
    gmcp("Guild.State", string.format('{ "gtnl": %d, "nekra": %d, "guild": "bladesinger", "gxp": %d }', gtnl, nekra, gxp))
  end
  fire_timers()
  s, g = st("status"), st("guild_tab")
  check(at(line_of(s, "Nekra  "), "@{success}4,580@{}@{#D6E2F2} / 5,100") and lit(line_of(s, "Nekra  ")) == 9, "fight: nekra 4,580 of 5,100 (" .. line_of(s, "Nekra  ") .. ")")
  check(at(g, "Earned       8,880,407 gxp") and at(g, "Still owed   29,969,593 gxp"), "fight: gxp and gtnl follow")
  check(at(g, "Gained       25,340") and at(g, "in 40s"), "fight: the session's gain (" .. line_of(g, "Gained") .. ")")
  -- 25,340 in 40 s = 633.5 a second = 2,280,600 an hour; 29,969,593 / 633.5 = 47,308 s = 13h 8m
  check(at(line_of(g, "Pace"), "2,280,600 / hour") and at(line_of(g, "Pace"), "over the last 40s"), "fight: pace (" .. line_of(g, "Pace") .. ")")
  check(at(line_of(g, "At this pace"), "~13h 8m"), "fight: time to the next level (" .. line_of(g, "At this pace") .. ")")
  -- a new guild level: gxp starts over, and that is not a loss
  clock = clock + 2
  gmcp("Guild.State", '{ "gtnl": 40000000, "guild": "bladesinger", "gxp": 500 }'); fire_timers()
  g = st("guild_tab")
  check(at(g, "Gained       25,340") and at(g, "Earned       500 gxp") and at(line_of(g, "Progress"), "0.0%"), "a new level starts gxp over without undoing the session's gain")
  -- idle for ten minutes: the pace runs down to nothing
  for _ = 1, 31 do
    clock = clock + 20
    gmcp("Guild.State", '{ "reset": 10, "guild": "bladesinger" }')
  end
  fire_timers()
  g = st("guild_tab")
  check(at(line_of(g, "At this pace"), "no gxp coming in") and at(line_of(g, "Pace"), "0 / hour"), "idle: the pace runs out (" .. line_of(g, "At this pace") .. ")")
end
-- the capture's last two ticks, as they came
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", STATE); gmcp("Guild.State", STATE_F1); clock = clock + 2; gmcp("Guild.State", STATE_F2); fire_timers()
check(at(line_of(st("status"), "Nekra  "), "4,444@{}@{#D6E2F2} / 5,100") and at(st("guild_tab"), "Still owed   29,083,238 gxp")
      and at(line_of(st("guild_tab"), "Progress"), "25.1%"), "the capture's last tick: nekra 4,444, 25.1% of the level")
check(at(line_of(st("status"), "Blur reset"), "78.9%") and lit(line_of(st("status"), "Blur reset")) == 8, "the capture's last tick: reset 78.9%")

-- 8. Guild.Info deltas: rune totals and regeneration
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", STATE); gmcp("Guild.Info", INFO); gmcp("Guild.Extra", EXTRA); fire_timers()
local r = st("runes")
check(at(r, "Prepared     1,201") and at(r, "Inscribed    6,570"), "runes: the two totals")
-- 5,130 held across the 26 kinds that are not zero
check(at(r, "Held         5,130") and at(r, "of 26 kinds"), "runes: held (" .. line_of(r, "Held") .. ")")
check(at(r, "@{text}power            676@{}") and at(r, "@{dim}charm              0@{}"), "runes: a count, and a rune with none is dim")
check(not at(r, "+"), "runes: nothing has changed yet")
do
  -- 33 runes in two columns: 17 rows, by name, the first row alacrity beside reckoning
  local rows, first = 0, nil
  for l in r:gmatch("[^\n]+") do
    if l:find("^@{%a+}%l") then rows = rows + 1; first = first or l end
  end
  check(rows == 17, "runes: 33 runes make 17 rows (" .. rows .. ")")
  check(first and at(first, "alacrity") and at(first, "reckoning") and at(first, "alacrity") < at(first, "reckoning"), "runes: by name, down then across")
end
gmcp("Guild.Info", INFO_D1); gmcp("Guild.Info", INFO_D2); gmcp("Guild.Info", INFO_D3); gmcp("Guild.Info", INFO_D4)
gmcp("Guild.Extra", EXTRA_END); fire_timers()
r, s = st("runes"), st("status")
check(at(r, "Prepared     1,218") and at(r, "Inscribed    6,714"), "runes: totals follow the deltas")
check(at(r, "@{text}power            692@{}@{success} +16  @{}"), "runes: power went up 16 (" .. line_of(r, "power") .. ")")
check(at(r, "@{text}understanding      4@{}@{success} +4   @{}") and at(r, "Held         5,231") and at(r, "of 27 kinds"), "runes: a rune that was at none, and the new total")
check(at(r, "@{text}eternity         160@{}      "), "runes: an unchanged rune says nothing")
check(at(line_of(s, "HP   "), "@{success}    120@{}     300@{#D6E2F2}      120@{}") and at(line_of(s, "SP   "), "@{success}     10@{}      30@{#D6E2F2}       10@{}"),
      "regen: under the max, what the runes give is what is in use (" .. line_of(s, "SP   ") .. ")")
check(not at(line_of(s, "HP   "), "over") and not at(line_of(s, "SP   "), "over"), "regen: nothing said while under the max")
check(at(last_line_of(s, "Sokra    "), "@{warning}     23@{}      18@{#D6E2F2}       18@{}@{warning}   5 over the max@{}"),
      "regen: sokra runes give 23, 18 can be used, 5 are over (" .. last_line_of(s, "Sokra    ") .. ")")
check(at(last_line_of(s, "Nekra    "), "@{dim}      0@{}      18@{#D6E2F2}        0@{}"), "regen: nekra has no runes on it")
gmcp("Guild.Info", '{ "guild": "bladesinger", "sokra_regen": 18 }'); fire_timers()
check(at(last_line_of(st("status"), "Sokra    "), "@{success}     18@{}      18@{#D6E2F2}       18@{}") and not at(st("status"), "over the max"), "regen: exactly at the max is not over it")
check(at(s, "Guild level  50"), "a delta leaves the guild level alone")
-- runes spent: the count falls and says so
gmcp("Guild.Extra", '{ "guild": "bladesinger", "runes": { "power": 670, "health": 522 } }'); fire_timers()
r = st("runes")
check(at(r, "@{text}power            670@{}@{warning} -6   @{}") and at(r, "@{text}health           522@{}@{success} +5   @{}"), "runes: a count that fell (" .. line_of(r, "power") .. ")")

-- 9. setup: the evade settings, and a field nobody has named
local u = st("setup")
check(at(u, "-- Evade --") and at(line_of(u, "Fires"), "not set") and at(line_of(u, "Command"), "not set") and at(u, "Var          0") and at(u, "Level        0"), "setup: nothing set")
check(not at(u, "Also sent"), "setup: every field of the capture has a row somewhere")
state["char.vitals.maxhp"] = 1634
gmcp("Guild.State", '{ "guild": "bladesinger", "evade_cmd": "portal home", "evade_hp": 400, "evade2_cmd": "flee", "evade2_var": 3, "evade2_level": 2, "mystery": 7 }')
gmcp("Guild.Info", '{ "guild": "bladesinger", "new_thing": "x" }'); fire_timers()
u, s = st("setup"), st("status")
check(at(line_of(u, "Fires"), "below 400 hp") and at(line_of(u, "Fires"), "(24% of max)") and at(line_of(u, "Fires"), "@{info}portal home@{}"), "setup: evade (" .. line_of(u, "Fires") .. ")")
check(at(line_of(u, "Command"), "@{info}flee@{}") and at(u, "Var          3") and at(u, "Level        2"), "setup: evade 2")
check(at(u, "-- Also sent --") and at(u, "mystery                7") and at(u, "new_thing              x"), "setup: unknown fields are listed under their own names")
check(at(s, "-- Evade --") and at(line_of(s, "Fires"), "portal home"), "status: a set evade shows there too")

-- 10. a gswap away and back: the panel keeps the last figures and says so
gmcp("Guild.State", GT_STATE); fire_timers()
s = st("status")
check(at(s, "now in the gentech guild") and at(s, "last time as a bladesinger") and at(s, "Nekra  "), "gswap: the note, over the last figures")
check(at(st("summary"), "(now gentech)") == 1 and at(st("runes"), "now in the gentech guild") and not at(st("setup"), "edna"), "gswap: every tab says it, and nothing leaks")
gmcp("Guild.State", STATE); fire_timers()
check(not at(st("status"), "gentech") and at(line_of(st("setup"), "Fires"), "not set") and not at(st("setup"), "mystery"), "gswap back: a whole message starts the snapshot over")

-- 11. bshud
printed = {}
for _, a in ipairs(aliases) do if a.pattern == "^bshud$" then a.run() end end
check(said("[bladesinger]") and said("Guild level  50") and said("Blur reset") and said("Portal reset"), "bshud: prints the status page")

-- 12. the markup never lets a server @ through as a colour code; junk does not throw
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", '{ "guild": "bladesinger", "full": 1, "evade_cmd": "a@{error}b", "evade_hp": 5, "evade2_cmd": "x@y", "nekra": "junk", "nekra_max": 0 }')
gmcp("Guild.Extra", '{ "guild": "bladesinger", "full": 1, "runes": { "p@{error}q": 3, "nested": { "a": 1 } } }')
gmcp("Guild.Info", "not json"); gmcp("Guild.Info", "[1,2]"); gmcp("Guild.Extra", '{ "guild": "bladesinger", "runes": 3 }')
fire_timers()
check(at(st("status"), "a@@{error}b") and at(st("setup"), "x@@y"), "@ in server text is escaped")
check(at(line_of(st("status"), "Nekra  "), "Nekra        0"), "a pool with no maximum is just its number")
gmcp("Guild.Extra", '{ "guild": "bladesinger", "full": 1, "runes": { "p@{error}q": 3, "nested": { "a": 1 } } }'); fire_timers()
check(at(st("runes"), "p@@{error}q") and not at(st("runes"), "nested"), "runes: a name is escaped, a table is not a count")

print(string.format("bladesinger_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
