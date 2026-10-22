-- Harness for 3s-changeling. Every payload below is VERBATIM from the capture
-- gmcp-fields-Kuben-20260903-185223.md (3 Sep 2026): the full Guild.State and Guild.Info
-- messages and the unpaged deltas that follow them.
--
--   lua5.4 changeling_test.lua      (from anywhere; tests/plugins/run.sh runs them all, as CI does)

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-changeling/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

-- ---- a small JSON reader (the payloads below are the plugin's real input) ----
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

-- ---- the client ----
local printed, gmcp_hooks, aliases, state, panel, timers, next_id

local function fresh_client()
  printed, gmcp_hooks, aliases = {}, {}, {}
  state, panel, timers, next_id = {}, nil, {}, 0
  scrye = {
    id = "3s-changeling",
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
local function st(k) return tostring(state["plugin.3s-changeling." .. k] or "") end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end

-- ---- capture payloads, verbatim ----
local STATE_FULL = '{ "reset": 377, "dissipate": "Off", "instinct_act": "", "stamina": 58.886367025546917, "full": 1, "thermotaxis": "Off", "protoplasm": 100.0, "guild": "changeling", "bioplasts": 0, "instinct2_act": "", "instinct2_which": "", "instinct2_gp": 0, "adrenalize": "Off", "instinct_hp": 0, "super": 209, "hyperproliferate": "Off", "kills": 19012, "relinquishes": 3 }'
local STATE_D1 = '{ "guild": "changeling", "stamina": 63.343701478950372, "super": 208 }'
local STATE_D2 = '{ "guild": "changeling", "stamina": 100.0, "super": 199 }'
local STATE_D3 = '{ "guild": "changeling", "super": 23 }'
local INFO_FULL = '{ "perform": "morph", "best_kill_class": 8404735, "form_initimacy": 0, "form_time": 130098, "last_form_cost": 0, "full": 1, "arch_foe": 0, "next_form_cost": 10800, "guild": "changeling", "fam": "", "current_form": "slime", "form_points": 229, "guild_age": 1852228, "ingested": 559096, "arch_foe_class": 0, "best_kill": "a growing evolved creature {wildly chaotic}", "complex": 185, "form_group": "other", "combat_age": 1640224 }'
local INFO_D = '{ "guild": "changeling", "guild_age": 1852600 }'

-- 1. nothing yet
fresh_client(); dofile(PLUGIN)
check(st("status"):find("waiting for Guild.State", 1, true) ~= nil, "empty: status waits")
check(st("form"):find("waiting for Guild.Info", 1, true) ~= nil, "empty: form waits")
check(st("summary"):find("waiting", 1, true) ~= nil, "empty: summary waits")
check(panel ~= nil and panel.title == "Changeling" and #panel.tabs == 3, "the panel has three tabs")

-- 2. the full messages
gmcp("Guild.State", STATE_FULL); gmcp("Guild.Info", INFO_FULL)
fire_timers()
local s = st("status")
check(s:find("Protoplasm", 1, true) ~= nil and s:find("@{success}100%@{}", 1, true) ~= nil, "protoplasm 100% is green")
check(s:find("@{warning}59%@{}", 1, true) ~= nil, "stamina 59% is amber (" .. s .. ")")
check(s:find("Super        209", 1, true) ~= nil and s:find("6m", 1, true) ~= nil, "super and its time at 2 s a step")
check(s:find("dissipate", 1, true) ~= nil and s:find("@{dim}off@{}", 1, true) ~= nil, "the toggles show off")
check(s:find("Instinct     @{dim}none set@{}", 1, true) ~= nil, "an empty instinct says so")
check(s:find("slime", 1, true) ~= nil and s:find("(other)", 1, true) ~= nil, "the form and its group")
check(s:find("In form      1d 12h", 1, true) ~= nil, "time in form")
local f = st("form")
check(f:find("Form points  229", 1, true) ~= nil and f:find("Next form    10,800", 1, true) ~= nil, "form points and next cost")
check(not f:find("affordable", 1, true), "229 points cannot afford 10,800")
check(f:find("Ingested     559,096", 1, true) ~= nil and f:find("Complex      185", 1, true) ~= nil, "ingested and complex")
check(f:find("Perform      morph", 1, true) ~= nil, "perform")
check(not f:find("Familiar", 1, true), "no familiar, no line")
local r = st("record")
check(r:find("Kills        19,012", 1, true) ~= nil and r:find("Relinquishes 3", 1, true) ~= nil, "kills and relinquishes")
check(r:find("a growing evolved creature", 1, true) ~= nil and r:find("class 8,404,735", 1, true) ~= nil, "best kill with its class")
check(r:find("Arch foe     @{dim}none@{}", 1, true) ~= nil, "no arch foe")
check(r:find("Guild age    21d 10h", 1, true) ~= nil and r:find("89% of it", 1, true) ~= nil, "guild age and the combat share")
check(st("summary"):find("slime", 1, true) ~= nil and st("summary"):find("Proto 100%", 1, true) ~= nil
      and st("summary"):find("Super 209", 1, true) ~= nil, "the summary line")

-- 3. deltas keep the rest
gmcp("Guild.State", STATE_D1); gmcp("Guild.State", STATE_D2); gmcp("Guild.State", STATE_D3); gmcp("Guild.Info", INFO_D)
fire_timers()
s = st("status")
check(s:find("Super        23", 1, true) ~= nil and s:find("@{success}100%@{}", 1, true) ~= nil, "super counts down, stamina full")
check(s:find("Protoplasm", 1, true) ~= nil and st("record"):find("19,012", 1, true) ~= nil, "a delta keeps the rest")
check(st("record"):find("21d 10h", 1, true) ~= nil and st("form"):find("slime", 1, true) ~= nil, "guild age moves, the form stays")

-- 4. toggles, instincts, a familiar, an affordable form, an arch foe
gmcp("Guild.State", '{ "guild": "changeling", "adrenalize": "On", "protoplasm": 30.0, "instinct_act": "flee", "instinct_hp": 400, "instinct2_act": "absorb", "instinct2_which": "slime", "instinct2_gp": 50 }')
gmcp("Guild.Info", '{ "guild": "changeling", "fam": "a blob", "form_points": 11000, "arch_foe": "a troll", "arch_foe_class": 5000 }')
fire_timers()
s = st("status")
check(s:find("@{success}On@{}", 1, true) ~= nil and st("summary"):find("ADRENALIZE", 1, true) ~= nil, "a toggle on shows, and in the summary")
check(s:find("@{error}30%@{}", 1, true) ~= nil, "low protoplasm is red")
check(s:find("flee", 1, true) ~= nil and s:find("at hp 400", 1, true) ~= nil, "instinct 1")
check(s:find("absorb", 1, true) ~= nil and s:find("slime at gp 50", 1, true) ~= nil, "instinct 2")
check(st("form"):find("Familiar     a blob", 1, true) ~= nil and st("form"):find("affordable", 1, true) ~= nil, "familiar and an affordable form")
check(st("record"):find("Arch foe     a troll", 1, true) ~= nil and st("record"):find("class 5,000", 1, true) ~= nil, "an arch foe")

-- 5. a new full message replaces the snapshot
gmcp("Guild.State", STATE_FULL); fire_timers()
check(not st("status"):find("flee", 1, true) and st("status"):find("@{dim}off@{}", 1, true) ~= nil, "a full message starts over")
gmcp("Guild.State", '{ "full": 1, "guild": "changeling", "protoplasm": 50.0 }'); fire_timers()
check(not st("status"):find("Stamina", 1, true), "...and what it does not carry is gone")

-- 6. another guild's same-named packages are not ours
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", '{ "power": 4912, "power_max": 4912, "heat_pct": 0, "guild": "cyborg", "full": 1 }')
gmcp("Guild.Info", '{ "clan": "x", "guild": "juggernaut", "full": 1, "guild_age": 5 }')
fire_timers()
check(st("status"):find("waiting for Guild.State", 1, true) ~= nil and st("record"):find("waiting", 1, true) ~= nil, "another guild is ignored")

-- 7. the alias
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", STATE_FULL); fire_timers()
printed = {}
for _, a in ipairs(aliases) do if a.pattern == "^clhud$" then a.run() end end
check(said("[cling]") and said("Protoplasm"), "clhud: prints the status page")

-- 8. the markup never lets a server @ through as a colour code
fresh_client(); dofile(PLUGIN)
gmcp("Guild.Info", '{ "guild": "changeling", "full": 1, "current_form": "slime", "best_kill": "a@{error}bad", "guild_age": 1 }')
fire_timers()
check(st("record"):find("a@@{error}bad", 1, true) ~= nil, "@ in server text is escaped")

print(string.format("changeling_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
