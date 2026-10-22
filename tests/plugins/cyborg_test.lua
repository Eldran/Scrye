-- Harness for 3s-cyborg. Every payload below is VERBATIM from the capture
-- (_lab/captures/gmcp-fields-Lobo-20260901-1358.md, 1 Sep 2026) - the paged
-- Guild.State burst with its activated[] list, the 7-page Guild.Chassis burst
-- with slots split across pages, the 5-page Guild.Combat burst, and the small
-- unpaged deltas that follow. Renumbered only where the capture's sample was
-- partial, since the assembler only publishes complete bursts.
--
-- What this file protects: that a paged list is CONCATENATED across its pages
-- rather than replaced (the chassis slots arrive in three chunks and the
-- implants in two), that an unpaged delta updates without wiping the burst, and
-- that the four fields whose meaning the capture does not settle are shown as
-- raw numbers rather than dressed up as bars.
--
--   lua5.4 cyborg_test.lua      (from anywhere; tests/plugins/run.sh runs them all, as CI does)

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-cyborg/main.lua"

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
    id = "3s-cyborg",
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
local function st(k) return tostring(state["plugin.3s-cyborg." .. k] or "") end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end

-- ---- capture payloads, verbatim ----
local STATE_P1 = '{ "full": 1, "pages": 3, "activated": [ "adrenal pump", "weakness identification system", "ext. vampiric transfer array", "wired reflexes", "defensive synergy coprocessor", "hologram projection unit", "pain editor", "electrodampening grounder", "alpha ray mitigator", "emmer net", "telescopic riot shield", "mass deceleration field", "projectional force shield", "thermal balancing unit", "plasma supercooler", "frost dissipation unit", "thermal dispersion field" ], "guild": "cyborg", "page": 1 }'
local STATE_P2 = '{ "full": 1, "pages": 3, "activated": [ "sensory obtunder", "psiodampening insulator", "quark nullifier", "hadronic negation emitter", "toxin extractor", "caustic dampening unit", "intrinsic preservation proxy", "platelet booster", "anti-grav ion booster", "pathogenic attenuator" ], "guild": "cyborg", "page": 2 }'
local STATE_P3 = '{ "si_level": 107, "gexp_round": 0, "ammo": 0, "adrenaline_pct": 0, "full": 1, "stims": 670, "pages": 3, "guild": "cyborg", "stored_power": 7600, "power_max": 4912, "heat_pct": 0, "power_rgn": 0, "power": 4912, "painedit_pct": 0, "overheating": 0, "power_change": 4912, "credits": 275442628, "si_pct": 5478, "page": 3 }'

local CH_P1 = '{ "control_used": 10630, "power_rgn_base": 92, "hardpoint_used": 15, "hardpoint_max": 17, "power_grid_used": 18, "full": 1, "machine_pct": 61, "power_grid_max": 21, "pages": 7, "control_avail": 124, "page": 1, "guild": "cyborg", "power_rgn_max": 98 }'
local CH_P2 = '{ "slots_free": [ "right middle finger", "right foot", "left forearm", "left knee", "left middle finger", "left eye lens", "right calf", "left ring finger", "right chest", "left pinky finger", "right knee", "right forearm", "right index finger", "left elbow", "left ankle", "left foot", "right upper arm", "forehead", "waist", "face", "right eyebrow", "right ear", "right eye", "left ear", "right elbow", "left eye", "right pinky finger", "left upper arm" ], "full": 1, "pages": 7, "guild": "cyborg", "page": 2 }'
local CH_P3 = '{ "slots_free": [ "right eye lens", "throat", "right ring finger", "right ankle", "left wrist" ], "full": 1, "pages": 7, "guild": "cyborg", "page": 3 }'
local CH_P4 = '{ "full": 1, "pages": 7, "guild": "cyborg", "slots_used": [ "left chest", "skull", "left shoulder", "right shoulder exterior", "right thigh", "left chest exterior", "right upper back exterior", "left lower back", "right thumb", "left upper arm exterior", "right wrist", "left forearm exterior", "right calf exterior", "right hand", "left hand", "left hip", "right chest exterior", "left upper back", "left thumb", "right foot exterior", "right hip exterior", "left shoulder exterior" ], "page": 4 }'
local CH_P5 = '{ "full": 1, "pages": 7, "guild": "cyborg", "slots_used": [ "right lower back exterior", "left abdomen", "left eyebrow", "right lower back", "rifle", "left hip exterior", "right upper back", "left calf exterior", "left calf", "brain", "right abdomen exterior", "left upper back exterior", "left abdomen exterior", "left thigh", "mouth", "left index finger", "skull exterior", "left hand exterior", "right upper arm exterior", "left thigh exterior", "right abdomen", "right forearm exterior", "neck" ], "page": 5 }'
local CH_P6 = '{ "full": 1, "pages": 7, "guild": "cyborg", "slots_used": [ "right shoulder", "right hand exterior", "right thigh exterior", "left lower back exterior", "right hip" ], "page": 6 }'
local CH_P7 = '{ "full": 1, "pages": 7, "weapon_array_max": 13, "page": 7, "weapon_array_used": 6, "guild": "cyborg" }'

local CO_P1 = '{ "combat_rounds": 4795922, "kills_total": 31904, "kills_login": 0, "login_bil": 0, "login_xp": 0, "online_time": 2, "full": 1, "pages": 5, "page": 1, "guild": "cyborg" }'
local CO_P2 = '{ "full": 1, "pages": 5, "guild": "cyborg", "patterns": [ "none", "multispectral strobe cannon [0]/multispectral strobe cannon [1]/" ], "page": 2 }'
local CO_P3 = '{ "full": 1, "pages": 5, "guild": "cyborg", "patterns": [ "multispectral strobe cannon [10]/multispectral strobe cannon [11]/" ], "page": 3 }'
local CO_P4 = '{ "full": 1, "pages": 5, "guild": "cyborg", "patterns": [ "multispectral strobe cannon [5]/multispectral strobe cannon [6]/" ], "page": 4 }'
local CO_P5 = '{ "si_gained_pct": 0, "full": 1, "pages": 5, "strategy": [ 1, 2, 3, 4, 5, 6 ], "page": 5, "guild": "cyborg" }'

local PROG = '{ "item_donates": 20220804, "gexp": 1826061145, "power_scaled": 0, "gexp_cost": 3333000000, "coin_donate_label": "You have donated EPIC amounts of coins.", "item_donate_label": "You have donated EPIC amounts of items.", "full": 1, "corpse_donate_label": "Error! The C.R.C. lost track of what was sent.", "guild": "cyborg", "coin_donates": 20003000 }'
local INFO = '{ "enrollees": [ ], "joined": 1632211786, "guild_age": 5302159, "liaison": "None", "rank_name": "ronin", "guild_quest": 1, "full": 1, "affiliation": "Chummer", "guild": "cyborg" }'
local SYS1 = '{ "stims_max": 785, "full": 1, "auto_thresh": 45, "nanite_thresh": 95, "dissipate_thresh": 60, "guild": "cyborg" }'
local SYS2 = '{ "ammo_rounds": 10000, "guild": "cyborg", "ammo_type": "u-232" }'

local function feed_all()
  gmcp("Guild.State", STATE_P1); gmcp("Guild.State", STATE_P2); gmcp("Guild.State", STATE_P3)
  gmcp("Guild.Chassis", CH_P1); gmcp("Guild.Chassis", CH_P2); gmcp("Guild.Chassis", CH_P3)
  gmcp("Guild.Chassis", CH_P4); gmcp("Guild.Chassis", CH_P5); gmcp("Guild.Chassis", CH_P6)
  gmcp("Guild.Chassis", CH_P7)
  gmcp("Guild.Combat", CO_P1); gmcp("Guild.Combat", CO_P2); gmcp("Guild.Combat", CO_P3)
  gmcp("Guild.Combat", CO_P4); gmcp("Guild.Combat", CO_P5)
  gmcp("Guild.Progress", PROG)
  gmcp("Guild.Info", INFO)
  gmcp("Guild.Systems", SYS1); gmcp("Guild.Systems", SYS2)
  fire_timers()
end

-- =====================================================================
-- 1. honest before the feed
fresh_client(); dofile(PLUGIN); fire_timers()
check(panel ~= nil and panel.title == "Cyborg", "the panel is registered")
local titles = {}
for _, t in ipairs(panel.tabs) do titles[#titles + 1] = t.title end
check(table.concat(titles, ",") == "Status,Implants,Chassis,Combat",
  "four tabs in order (got " .. table.concat(titles, ",") .. ")")
check(st("status"):find("waiting for Guild.State", 1, true) ~= nil,
  "before the feed the Status tab says what it is waiting for")
check(st("chassis"):find("waiting for Guild.Chassis", 1, true) ~= nil,
  "...and so does Chassis")
check(st("summary"):find("waiting", 1, true) ~= nil, "...and the summary line")

-- 2. the feed
feed_all()

check(st("status"):find("4,912/4,912", 1, true) ~= nil, "status: power against its max, thousands separated")
check(st("status"):find("Reserve", 1, true) ~= nil and st("status"):find("7,600", 1, true) ~= nil,
  "status: the reserve pool is shown as a figure (no ceiling is known for it)")
check(st("status"):find("chassis base 92", 1, true) ~= nil,
  "status: regen names the chassis figures beside it")
check(st("status"):find("u-232", 1, true) ~= nil, "status: the loaded ammo type shows")
check(st("status"):find("670/785", 1, true) ~= nil, "status: stims read against Guild.Systems' max")
check(st("status"):find("ronin", 1, true) ~= nil and st("status"):find("Chummer", 1, true) ~= nil,
  "status: rank and affiliation from Guild.Info")
check(st("status"):find("275,442,628", 1, true) ~= nil, "status: credits")
check(st("status"):find("Liaison", 1, true) == nil,
  "status: the feed's literal \"None\" liaison is dropped, not printed as a name")

-- 3. THE PAGED LISTS. activated[] arrives on pages 1 and 2 and must CONCATENATE:
-- 17 + 10 = 27. A replace would leave 10 and nobody would notice from a glance.
check(st("implants"):find("27 running", 1, true) ~= nil,
  "implants: the two pages concatenate to 27, not replace to 10")
check(st("implants"):find("adrenal pump", 1, true) ~= nil
  and st("implants"):find("pathogenic attenuator", 1, true) ~= nil,
  "implants: entries from BOTH pages are listed")

-- chassis slots: free 28+5 = 33, used 22+23+5 = 50
check(st("chassis"):find("33 free", 1, true) ~= nil, "chassis: free slots concatenate across pages 2-3")
check(st("chassis"):find("50 used", 1, true) ~= nil, "chassis: used slots concatenate across pages 4-6")
check(st("chassis"):find("brain", 1, true) ~= nil and st("chassis"):find("right hip", 1, true) ~= nil,
  "chassis: entries from the middle and last used-pages both survive")
check(st("chassis"):find("15/17", 1, true) ~= nil, "chassis: hardpoints from page 1")
check(st("chassis"):find("6/13", 1, true) ~= nil, "chassis: the weapon array from page 7")

-- combat: patterns concatenate across pages 2-4
check(st("combat"):find("31,904", 1, true) ~= nil, "combat: kills total")
check(st("combat"):find("1 > 2 > 3 > 4 > 5 > 6", 1, true) ~= nil, "combat: the strategy order")
local pat_rows = 0
for line in (st("combat") .. "\n"):gmatch("([^\n]*)\n") do
  if line:find("multispectral", 1, true) then pat_rows = pat_rows + 1 end
end
check(pat_rows >= 3, "combat: patterns from all three pages are listed (got " .. pat_rows .. ")")

-- 4. THE FOUR FIGURES THE CAPTURE COULD NOT SETTLE, now that Joakim has (1 Sep).
-- Each of these was a raw labelled number in 1.0.0 and is now the thing it is.

-- si_pct is hundredths of a percent toward the NEXT level: 5478 -> 54.78%, 107 -> 108
check(st("status"):find("54.78%", 1, true) ~= nil,
  "si: si_pct 5478 renders as 54.78% (hundredths of a percent, not a raw count)")
check(st("status"):find("107 -> 108", 1, true) ~= nil,
  "si: ...toward the next level, named")
check(st("status"):find("5,478", 1, true) == nil,
  "si: ...and the raw hundredths are no longer shown as a number")

-- control_used + control_avail IS the capacity; the feed sends no total
check(st("chassis"):find("10,630/10,754", 1, true) ~= nil,
  "control: used against used+available, because the feed sends no total")
check(st("chassis"):find("124 free", 1, true) ~= nil,
  "control: ...with the headroom spelled out rather than left to subtract")

-- ammo (magazine) and ammo_rounds (case) are two stores, never a ratio
-- the burst's own ammo is 0 (the capture's full burst really did read 0); a delta
-- brings the magazine up to the 4,109 the summary table showed, which is also the
-- case that matters: magazine and case moving independently
gmcp("Guild.State", '{ "guild": "cyborg", "ammo": 4109 }')
fire_timers()
check(st("status"):find("4,109", 1, true) ~= nil and st("status"):find("loaded", 1, true) ~= nil,
  "ammo: the magazine is labelled loaded")
check(st("status"):find("in the case", 1, true) ~= nil,
  "ammo: ...and the case is named as a separate store")
check(st("status"):find("10,000", 1, true) ~= nil,
  "ammo: ...the case keeps its own figure while the magazine moves")
check(st("status"):find("4,109/10,000", 1, true) == nil,
  "ammo: ...and the two are NEVER divided into a ratio")

-- 5. an unpaged delta updates without wiping the burst
gmcp("Guild.State", '{ "guild": "cyborg", "power": 4825, "heat_pct": 82, "overheating": 1 }')
fire_timers()
check(st("status"):find("4,825/4,912", 1, true) ~= nil, "delta: power moves")
check(st("status"):find("OVERHEATING", 1, true) ~= nil, "delta: overheating is called out")
check(st("implants"):find("27 running", 1, true) ~= nil,
  "delta: an unpaged delta does not wipe the paged activated list")
check(st("status"):find("275,442,628", 1, true) ~= nil, "delta: ...nor the rest of the burst")
check(st("summary"):find("Heat 82%", 1, true) ~= nil, "delta: the summary line follows")

-- 6. the alias prints the status page
printed = {}
for _, a in ipairs(aliases) do if a.pattern == "^cyb$" then a.run() end end
check(said("[cyborg]"), "cyb: prints into the output window")
check(said("Power"), "cyb: ...the status page")

-- 7. a guild xp bar only when both halves are known
check(st("status"):find("1,826,061,145/3,333,000,000", 1, true) ~= nil,
  "progress: gexp reads against gexp_cost")

print(string.format("cyborg_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
