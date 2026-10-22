-- Harness for 3s-juggernaut. Every payload below is VERBATIM from the capture
-- gmcp-fields-icewind-20260925-070327.md (25 Sep 2026): the paged Guild.Suits and
-- Guild.Tech bursts, the Guild.Session burst with its casualties list, the full
-- Guild.State / Combat / Info / Chassis / Skills messages and the unpaged deltas
-- that follow them.
--
--   lua5.4 juggernaut_test.lua      (from anywhere; tests/plugins/run.sh runs them all, as CI does)

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-juggernaut/main.lua"

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
    id = "3s-juggernaut",
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
local function st(k) return tostring(state["plugin.3s-juggernaut." .. k] or "") end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end

-- ---- capture payloads, verbatim ----
local STATE_FULL = '{ "clan_powers_max": 2, "glevel_pct": 83.953987213391954, "ammo": "23800", "full": 1, "follower_stim": 0, "battery": 704, "reset_pct": 82, "low_light": 0, "guild": "juggernaut", "clan_powers": 2, "heat_pct": 42, "missiles": 2, "hits_last_round": 0, "jump_jets": 0, "missiles_max": 2, "stim_pct": 56, "credits": 117 }'
local STATE_D1 = '{ "glevel_pct": 83.962937798153519, "ammo": "23780", "reset_pct": 83, "heat_pct": 45, "guild": "juggernaut" }'
local STATE_D2 = '{ "glevel_pct": 84.052443645769003, "ammo": "23580", "stim_pct": 53, "heat_pct": 88, "guild": "juggernaut" }'
local COMBAT_FULL = '{ "target_condition": "Death", "full": 1, "hits_last_round": 0, "frenzy_active": 0, "guild": "juggernaut", "target_rounds": 134 }'
local COMBAT_D = '{ "guild": "juggernaut", "hits_last_round": 1, "target_rounds": 136 }'
local SUITS_P1 = '{ "current": "salamander", "full": 1, "pages": 3, "guild": "juggernaut", "page": 1 }'
local SUITS_P2 = '{ "suits": [ { "level": 65, "current": 0, "valour": 0, "gxp_pct": 63.137354257925438, "name": "gnome", "has_valour": 0 }, { "level": 5, "current": 0, "valour": 0, "gxp_pct": 37.639543230652919, "name": "undine", "has_valour": 1 }, { "level": 20, "current": 0, "valour": 0, "gxp_pct": 1.7914251740654723, "name": "elemental", "has_valour": 1 }, { "level": 1, "current": 0, "valour": 0, "gxp_pct": 0.0, "name": "sylph", "has_valour": 1 } ], "full": 1, "pages": 3, "guild": "juggernaut", "page": 2 }'
local SUITS_P3 = '{ "suits": [ { "level": 63, "current": 1, "valour": 2, "gxp_pct": 83.953987213391954, "name": "salamander", "has_valour": 1 } ], "full": 1, "pages": 3, "guild": "juggernaut", "page": 3 }'
local SUITS_U1 = '{ "pages": 2, "suits": [ { "level": 65, "current": 0, "valour": 0, "gxp_pct": 63.137354257925438, "name": "gnome", "has_valour": 0 }, { "level": 5, "current": 0, "valour": 0, "gxp_pct": 37.639543230652919, "name": "undine", "has_valour": 1 }, { "level": 20, "current": 0, "valour": 0, "gxp_pct": 1.7914251740654723, "name": "elemental", "has_valour": 1 }, { "level": 1, "current": 0, "valour": 0, "gxp_pct": 0.0, "name": "sylph", "has_valour": 1 } ], "page": 1, "guild": "juggernaut" }'
local SUITS_U2 = '{ "pages": 2, "suits": [ { "level": 63, "current": 1, "valour": 2, "gxp_pct": 84.007690721961254, "name": "salamander", "has_valour": 1 } ], "page": 2, "guild": "juggernaut" }'
local SESS_P1 = '{ "full": 1, "pages": 2, "guild": "juggernaut", "casualties": [ { "damage": 1, "rounds": 2, "name": "Leonardo", "class": 4580 }, { "damage": 13, "rounds": 2, "name": "Donatello", "class": 4865 }, { "damage": 1964, "rounds": 147, "name": "An archer", "class": 151830 }, { "damage": 9080, "rounds": 137, "name": "A manticore", "class": 176360 }, { "damage": 9731, "rounds": 146, "name": "A goblin", "class": 149160 }, { "damage": 9907, "rounds": 144, "name": "A knight", "class": 154140 }, { "damage": 7970, "rounds": 137, "name": "A Huge Fire Elemental", "class": 79190 } ], "page": 1 }'
local SESS_P2 = '{ "reset": { "seconds": 7338, "rounds": 860, "kills": 7 }, "full": 1, "login": { "best_kill_class": 176360, "last_kill": "An Elemental", "cpowers_donated": 0, "missiles_wasted": 14, "seconds": 7338, "worst_kill_class": 4580, "rounds": 860, "last_kill_class": 79190, "best_kill": "Manticore", "missiles_used": 0, "best_kill_rounds": 137, "cpowers_used": 0, "cpowers_wasted": 14, "missiles_donated": 0, "kills": 7, "worst_kill": "Leonardo" }, "pages": 2, "lifetime": { "combat_rounds": 884924, "combat_pct": 69, "combat_time": 1769848 }, "page": 2, "guild": "juggernaut" }'
local SESS_U1 = '{ "pages": 3, "page": 1, "casualties": [ { "damage": 1, "rounds": 2, "name": "Leonardo", "class": 4580 }, { "damage": 13, "rounds": 2, "name": "Donatello", "class": 4865 }, { "damage": 1964, "rounds": 147, "name": "An archer", "class": 151830 }, { "damage": 9080, "rounds": 137, "name": "A manticore", "class": 176360 }, { "damage": 9731, "rounds": 146, "name": "A goblin", "class": 149160 }, { "damage": 9907, "rounds": 144, "name": "A knight", "class": 154140 }, { "damage": 7970, "rounds": 137, "name": "A Huge Fire Elemental", "class": 79190 } ], "guild": "juggernaut" }'
local SESS_U2 = '{ "pages": 3, "page": 2, "casualties": [ { "damage": 12236, "rounds": 149, "name": "A knight", "class": 166675 } ], "guild": "juggernaut" }'
local SESS_U3 = '{ "reset": { "seconds": 7374, "rounds": 875, "kills": 8 }, "login": { "best_kill_class": 176360, "last_kill": "Knight", "cpowers_donated": 0, "missiles_wasted": 14, "seconds": 7374, "worst_kill_class": 4580, "rounds": 875, "last_kill_class": 166675, "best_kill": "Manticore", "missiles_used": 0, "best_kill_rounds": 137, "cpowers_used": 0, "cpowers_wasted": 14, "missiles_donated": 0, "kills": 8, "worst_kill": "Leonardo" }, "pages": 3, "lifetime": { "combat_rounds": 884939, "combat_pct": 69, "combat_time": 1769878 }, "page": 3, "guild": "juggernaut" }'
local INFO_FULL = '{ "joined": 1568045848, "rank": "Member", "depot_status": "None|Wheeled Tank 14%", "recruits": 0, "title": "the Clan Wolf Juggernaut", "vault": 718908.0, "full": 1, "clan_honor": "Low", "guild": "juggernaut", "storage": 378.0, "house_leader": 0, "suit_kills": 144591, "guild_age": 2538752, "sponsor": "Nobody", "clan": "Wolf", "current_suit": "salamander", "recruiter": "none", "eval_level": 6.6200000000000001, "oathmaster": 0, "kills": 252819 }'
local INFO_D = '{ "suit_kills": 144641, "guild": "juggernaut", "guild_age": 2538812, "kills": 252869 }'
local CHASSIS_FULL = '{ "full": 1, "guild": "juggernaut", "mounts": [ { "ammo": 23760, "slot": 1, "weapon": "Heavy Lrm 20", "enabled": 1, "ammo_type": "Lrm Ammo" }, { "ammo": 0, "slot": 2, "enabled": 1, "weapon": "Lrm 20 STRUCTURE" }, { "ammo": 0, "slot": 3, "enabled": 1, "weapon": "Lrm 20 STRUCTURE" } ], "mounts_available": 0 }'
local CHASSIS_D = '{ "guild": "juggernaut", "mounts": [ { "ammo": 21540, "slot": 1, "weapon": "Heavy Lrm 20", "enabled": 1, "ammo_type": "Lrm Ammo" }, { "ammo": 0, "slot": 2, "enabled": 1, "weapon": "Lrm 20 STRUCTURE" }, { "ammo": 0, "slot": 3, "enabled": 1, "weapon": "Lrm 20 STRUCTURE" } ] }'
local SKILLS_FULL = '{ "full": 1, "gunnery": 7, "skill_points": 20120, "skills": [ { "value": 25, "cost": 26391, "max": 80, "name": "critical_hit" }, { "value": 43, "cost": 68033, "max": 80, "name": "jump_jet" }, { "value": 22, "cost": 21164, "max": 80, "name": "death_from_above" }, { "value": 20, "cost": 17968, "max": 80, "name": "kick" }, { "value": 16, "cost": 12283, "max": 80, "name": "punch" }, { "value": 40, "cost": 59912, "max": 80, "name": "heat_management" }, { "value": 29, "cost": 34144, "max": 80, "name": "transpose" } ], "skill_max": 10, "guild": "juggernaut", "piloting": 1 }'
local SKILLS_D = '{ "guild": "juggernaut", "skill_points": 20580 }'
local TECH_P1 = '{ "full": 1, "pages": 3, "credits": 117, "guild": "juggernaut", "page": 1 }'
local TECH_P2 = '{ "tech": [ { "value": 0, "max": 0, "name": "salvage_efficiency" }, { "value": 5, "max": 5, "name": "point_defense" }, { "value": 2, "max": 2, "name": "blow_absorption" }, { "value": 2, "max": 5, "name": "cargo_truck" }, { "value": 4, "max": 12, "name": "heat_capacity" }, { "value": 1, "max": 1, "name": "stim_regen" }, { "value": 4, "max": 7, "name": "internal_structure" }, { "value": 1, "max": 1, "name": "stim_efficiency" }, { "value": 0, "max": 6, "name": "targeting_computer" }, { "value": 5, "max": 6, "name": "missile_guidance" } ], "full": 1, "pages": 3, "guild": "juggernaut", "page": 2 }'
local TECH_P3 = '{ "tech": [ { "value": 0, "max": 7, "name": "energy_weapons" }, { "value": 4, "max": 10, "name": "heat_sinks" }, { "value": 0, "max": 7, "name": "projectile_weapons" }, { "value": 7, "max": 7, "name": "missile_weapons" }, { "value": 0, "max": 4, "name": "projectile_ammo" }, { "value": 1, "max": 4, "name": "missile_ammo" }, { "value": 0, "max": 7, "name": "plasma_focusing" }, { "value": 8, "max": 50, "name": "module_interfacing" } ], "full": 1, "pages": 3, "guild": "juggernaut", "page": 3 }'

-- =====================================================================
-- 1. honest before the feed
fresh_client(); dofile(PLUGIN); fire_timers()
check(panel ~= nil and panel.title == "Juggernaut", "the panel is registered")
local titles = {}
for _, t in ipairs(panel.tabs) do titles[#titles + 1] = t.title end
check(table.concat(titles, ",") == "Status,Suits,Loadout,Session,Clan", "five tabs (" .. table.concat(titles, ",") .. ")")
check(st("status"):find("waiting for Guild.State", 1, true) ~= nil, "status: waits for its feed")
check(st("suits"):find("waiting for Guild.Suits", 1, true) ~= nil, "suits: waits for its feed")
check(st("session"):find("waiting for Guild.Session", 1, true) ~= nil, "session: waits for its feed")
check(st("summary"):find("waiting", 1, true) ~= nil, "summary: waits")

-- 2. the full bursts
gmcp("Guild.State", STATE_FULL); gmcp("Guild.Combat", COMBAT_FULL)
gmcp("Guild.Suits", SUITS_P1); gmcp("Guild.Suits", SUITS_P2); gmcp("Guild.Suits", SUITS_P3)
gmcp("Guild.Session", SESS_P1); gmcp("Guild.Session", SESS_P2)
gmcp("Guild.Info", INFO_FULL); gmcp("Guild.Chassis", CHASSIS_FULL); gmcp("Guild.Skills", SKILLS_FULL)
gmcp("Guild.Tech", TECH_P1); gmcp("Guild.Tech", TECH_P2); gmcp("Guild.Tech", TECH_P3)
fire_timers()
local s = st("status")
check(s:find("42%", 1, true) ~= nil and s:find("Heat", 1, true) ~= nil, "status: heat percent")
check(s:find("56%", 1, true) ~= nil, "status: stims percent")
check(s:find("23,800", 1, true) ~= nil, "status: the ammo string read as a number")
check(s:find("704", 1, true) ~= nil, "status: battery")
check(s:find("2/2", 1, true) ~= nil and s:find("Clan powers", 1, true) ~= nil, "status: missiles and clan powers against their max")
check(s:find("Death", 1, true) ~= nil and s:find("134", 1, true) ~= nil, "status: the target's condition and rounds")
check(s:find("salamander", 1, true) ~= nil and s:find("level 63", 1, true) ~= nil and s:find("83.95%", 1, true) ~= nil,
      "status: the suit worn, its level and guild xp")
check(s:find("Valour       2", 1, true) ~= nil, "status: valour for a suit that has it")
check(s:find("jump jets", 1, true) == nil and s:find("FRENZY", 1, true) == nil, "status: flags that are off say nothing")
check(st("summary"):find("Heat 42%", 1, true) ~= nil and st("summary"):find("Msl 2/2", 1, true) ~= nil, "summary line")

local su = st("suits")
check(su:find("gnome", 1, true) ~= nil and su:find("sylph", 1, true) ~= nil and su:find("salamander", 1, true) ~= nil,
      "suits: all five, across the burst's pages")
check(su:find("gnome", 1, true) < su:find("salamander", 1, true) and su:find("salamander", 1, true) < su:find("elemental", 1, true),
      "suits: highest level first")
check(su:find("@{accent}>@{}", 1, true) ~= nil, "suits: the worn suit is marked")
check(su:find("144,591", 1, true) ~= nil, "suits: kills in this suit from Guild.Info")

local lo = st("loadout")
check(lo:find("Heavy Lrm 20", 1, true) ~= nil and lo:find("23,760", 1, true) ~= nil and lo:find("Lrm Ammo", 1, true) ~= nil,
      "loadout: a mount with its ammo")
check(lo:find("Lrm 20 STRUCTURE", 1, true) ~= nil, "loadout: every mount")
check(lo:find("gunnery 7", 1, true) ~= nil and lo:find("20,120 skill points", 1, true) ~= nil, "loadout: skill header")
check(lo:find("heat management", 1, true) ~= nil and lo:find("40/80", 1, true) ~= nil, "loadout: a skill against its max")
check(lo:find("next 12,283", 1, true) ~= nil, "loadout: a skill's next cost")
check(lo:find("missile weapons", 1, true) ~= nil and lo:find("point defense", 1, true) ~= nil, "loadout: tech across both pages")
check(lo:find("salvage efficiency", 1, true) == nil, "loadout: a tech with max 0 is not a tech you can raise")
check(lo:find("117 credits", 1, true) ~= nil, "loadout: tech credits")

local se = st("session")
check(se:find("Kills 7 in 2h 2m", 1, true) ~= nil, "session: login kills and time")
check(se:find("3.4/h", 1, true) ~= nil, "session: kills per hour")
check(se:find("Manticore", 1, true) ~= nil and se:find("class 176,360", 1, true) ~= nil, "session: the best kill")
check(se:find("wasted @{warning}14@{}", 1, true) ~= nil, "session: wasted missiles flagged")
check(se:find("A Huge Fire Elemental", 1, true) ~= nil and se:find("9,907", 1, true) ~= nil, "session: the casualties list")
do
  local list = se:sub((se:find("Kills this login", 1, true)))
  check(list:find("A Huge Fire Elemental", 1, true) < list:find("Leonardo", 1, true), "session: newest kill first")
end
check(se:find("20d", 1, true) ~= nil and se:find("69%", 1, true) ~= nil, "session: lifetime")

local cl = st("clan")
check(cl:find("the Clan Wolf Juggernaut", 1, true) ~= nil and cl:find("Wolf", 1, true) ~= nil, "clan: title and clan")
check(cl:find("honour Low", 1, true) ~= nil and cl:find("6.62", 1, true) ~= nil, "clan: honour and evaluation")
check(cl:find("Sponsor", 1, true) == nil and cl:find("Recruiter", 1, true) == nil, "clan: 'Nobody' and 'none' are not data")
check(cl:find("718,908", 1, true) ~= nil and cl:find("378", 1, true) ~= nil, "clan: vault and storage")
check(cl:find("Wheeled Tank 14%", 1, true) ~= nil and cl:find("building", 1, true) ~= nil, "clan: the depot string split in two")
check(cl:find("252,819", 1, true) ~= nil, "clan: kills")

-- 3. the deltas
gmcp("Guild.State", STATE_D1); gmcp("Guild.State", STATE_D2); gmcp("Guild.Combat", COMBAT_D)
gmcp("Guild.Suits", SUITS_U1); gmcp("Guild.Suits", SUITS_U2)
gmcp("Guild.Session", SESS_U1); gmcp("Guild.Session", SESS_U2); gmcp("Guild.Session", SESS_U3)
gmcp("Guild.Info", INFO_D); gmcp("Guild.Chassis", CHASSIS_D); gmcp("Guild.Skills", SKILLS_D)
fire_timers()
s = st("status")
check(s:find("@{error}88%@{}", 1, true) ~= nil, "delta: heat 88% is red")
check(s:find("23,580", 1, true) ~= nil, "delta: ammo moves")
check(s:find("Battery      704", 1, true) ~= nil and s:find("Clan powers", 1, true) ~= nil, "delta: a delta keeps the rest")
check(s:find("84.05%", 1, true) ~= nil, "delta: glevel_pct follows")
check(s:find("136", 1, true) ~= nil and s:find("Hits last    1", 1, true) ~= nil, "delta: combat rounds and hits")
check(st("suits"):find("84.01%", 1, true) ~= nil and st("suits"):find("gnome", 1, true) ~= nil, "delta: a 2-page suits burst replaces the 3-page one")
check(st("session"):find("Kills 8", 1, true) ~= nil and st("session"):find("12,236", 1, true) ~= nil, "delta: the next kill joins the session")
check(st("clan"):find("252,869", 1, true) ~= nil and st("clan"):find("Wolf", 1, true) ~= nil, "delta: info kills move, the rest stays")
check(st("loadout"):find("21,540", 1, true) ~= nil and st("loadout"):find("20,580 skill points", 1, true) ~= nil
      and st("loadout"):find("heat management", 1, true) ~= nil, "delta: mount ammo and skill points move")

-- 4. frenzy and flags
gmcp("Guild.Combat", '{ "guild": "juggernaut", "frenzy_active": 1 }')
gmcp("Guild.State", '{ "guild": "juggernaut", "jump_jets": 1, "low_light": 1, "follower_stim": 1 }')
fire_timers()
check(st("status"):find("FRENZY", 1, true) ~= nil and st("summary"):find("FRENZY", 1, true) ~= nil, "frenzy shows")
check(st("status"):find("jump jets", 1, true) ~= nil and st("status"):find("low light", 1, true) ~= nil
      and st("status"):find("follower stim on", 1, true) ~= nil, "the 0/1 flags show when on")

-- 5. a cyborg's Guild.State is not ours
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", '{ "power": 4912, "power_max": 4912, "heat_pct": 0, "guild": "cyborg" }')
fire_timers()
check(st("status"):find("waiting for Guild.State", 1, true) ~= nil, "another guild's same-named package is ignored")

-- 6. the alias
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", STATE_FULL); fire_timers()
printed = {}
for _, a in ipairs(aliases) do if a.pattern == "^jug$" then a.run() end end
check(said("[jugg]") and said("Heat"), "jug: prints the status page")

print(string.format("juggernaut_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
