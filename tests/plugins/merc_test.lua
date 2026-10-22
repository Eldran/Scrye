-- Harness for 3s-merc. Every payload below is VERBATIM from the capture
-- (_lab/captures/gmcp-fields-Goran-20260902-1526.md, 2 Sep 2026) - the first
-- session subscribed with "Merc 1": one full snapshot per package, then the
-- per-round deltas that follow it.
--
-- What this file protects: that a DELTA merges into the snapshot rather than
-- replacing it (a {stam, target_hp} round must not cost the bars their hp_max),
-- that the gauges read from the merged snapshot, that an empty target zeroes the
-- target bar instead of showing the last mob's health, that talents sort by their
-- level gate and skills trained-first, that a reconnect starts over, and that the
-- five fields nobody has named are shown raw rather than dressed up.
--
--   lua5.4 merc_test.lua      (from anywhere; tests/plugins/run.sh runs them all, as CI does)
--
-- Mutants killed (2 Sep): replace-instead-of-merge (6 checks), a target bar that keeps
-- the last mob's health (2), the talent sort reversed (3), the level gate removed (1).
-- One EQUIVALENT mutant, documented rather than chased: keeping the "full" key in a
-- snapshot changes nothing observable, since nothing reads it.

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-merc/main.lua"

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
local printed, gmcp_hooks, aliases, state, panel, timers, next_id, connect_hooks

local function fresh_client()
  printed, gmcp_hooks, aliases, connect_hooks = {}, {}, {}, {}
  state, panel, timers, next_id = {}, nil, {}, 0
  scrye = {
    id = "3s-merc",
    print = function(x) printed[#printed + 1] = tostring(x) end,
    json = { decode = decode },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    onConnect = function(fn) connect_hooks[#connect_hooks + 1] = fn end,
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
local function st(k) return tostring(state["plugin.3s-merc." .. k] or "") end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end
local function rows(k)
  local out = {}
  for line in (st(k) .. "\n"):gmatch("([^\n]*)\n") do if line ~= "" then out[#out + 1] = line end end
  return out
end

-- ---- capture payloads, verbatim ----
local INFO_FULL = '{ "dtype": "Edged", "follow": 1, "theme": "entropy_herald", "status": 1, "perm_cap": 150, "eff_level": 75, "inst_level": 1, "class": "offensive", "cost": 8, "gender": 2, "full": 1, "perm_level": 75, "merc": "Stabby", "inst_cap": 30 }'
local INFO_D2   = '{ "merc": "Stabby", "inst_level": 2 }'
local VIT_FULL  = '{ "stam_max": 264, "dormant": 0, "hp_max": 19000, "stam": 264, "target": "", "stam_regen": 13, "ap_regen": 10, "full": 1, "target_hp": 0, "hp": 19000, "merc": "Stabby", "ap_max": 125, "abils": "", "ap": 125 }'
local VIT_D1    = '{ "merc": "Stabby", "stam": 230, "target_hp": 91, "target": "Wiremouth" }'
local VIT_D2    = '{ "merc": "Stabby", "stam": 196, "target_hp": 90 }'
local VIT_D3    = '{ "merc": "Stabby", "stam": 162 }'
local STATS_FULL = '{ "healing": 0, "dmg_out": 0, "spent_spec": 2036650, "abilities": 0, "inst_xp": 0, "full": 1, "dmg_in": 0, "life_healing": 0, "merc": "Stabby", "life_dmg_out": 302411929, "spent_total": 28119084, "perm_xp_next": 76243, "skill_points": 42, "spent_boot": 21133479, "life_rounds": 1497368, "rounds": 0, "inst_xp_next": 64, "spent_skills": 4948955, "perm_xp": 55780, "fund": 0, "life_abilities": 9949, "life_dmg_in": 1762 }'
local STATS_D1  = '{ "dmg_out": 48, "life_rounds": 1497370, "rounds": 2, "spent_boot": 21133495, "inst_xp": 2, "perm_xp": 55782, "merc": "Stabby", "life_dmg_out": 302411977, "spent_total": 28119100 }'
local TAL_FULL  = '{ "points": 5, "rend": { "points": 0, "min_level": 90, "eff": 0 }, "bandage": { "points": 0, "min_level": 1, "eff": 0 }, "frenzy": { "points": 0, "min_level": 120, "eff": 0 }, "critical": { "points": 10, "min_level": 30, "eff": 10 }, "combo": { "points": 0, "min_level": 60, "eff": 0 }, "next_cost": 640090, "full": 1, "allocs": 10, "merc": "Stabby" }'
local SKL_FULL  = '{ "cost_reduction": { "eff": 10, "raw": 10 }, "max_stamina": { "eff": 0, "raw": 0 }, "stamina_regen": { "eff": 8, "raw": 8 }, "ability_boost": { "eff": 0, "raw": 0 }, "hp_bonus": { "eff": 0, "raw": 0 }, "bury": { "eff": 0, "raw": 0 }, "next_cost": 456620, "full": 1, "max_ap": { "eff": 0, "raw": 0 }, "merc": "Stabby", "attacks": { "eff": 10, "raw": 10 }, "weapon_use": { "eff": 0, "raw": 0 }, "points": 42, "damage_types": { "eff": 0, "raw": 0 }, "armor_use": { "eff": 0, "raw": 0 }, "ap_regen": { "eff": 5, "raw": 5 }, "hp_regen": { "eff": 0, "raw": 0 }, "allocs": 33 }'

-- =====================================================================
-- 1. honest before the feed
fresh_client(); dofile(PLUGIN); fire_timers()
check(panel ~= nil and panel.title == "Mercenary", "the panel is registered")
local titles = {}
for _, t in ipairs(panel.tabs) do titles[#titles + 1] = t.title end
check(table.concat(titles, ",") == "Status,Stats,Talents,Skills",
  "four tabs in order (got " .. table.concat(titles, ",") .. ")")
check(st("status"):find("waiting for Merc.Info", 1, true) ~= nil, "before the feed the Status tab says what it waits for")
check(st("stats"):find("waiting for Merc.Stats", 1, true) ~= nil, "...and Stats")
check(st("talents_head"):find("waiting", 1, true) ~= nil and st("talents") == "", "...and Talents, with no rows")
check(st("summary"):find("waiting", 1, true) ~= nil, "...and the summary line")
check(st("hp") == "" and st("target_hp") == "0", "gauges are empty, the target bar zero")

-- the plugin only ever reads the five Merc packages
local pkgs = {}
for _, h in ipairs(gmcp_hooks) do pkgs[h.pkg] = true end
for _, p in ipairs({ "Merc.Info", "Merc.Vitals", "Merc.Stats", "Merc.Talents", "Merc.Skills" }) do
  check(pkgs[p], "subscribes to " .. p)
end
local n = 0; for _ in pairs(pkgs) do n = n + 1 end
check(n == 5, "...and nothing else")

-- 2. the full snapshots
gmcp("Merc.Info", INFO_FULL); gmcp("Merc.Vitals", VIT_FULL); gmcp("Merc.Stats", STATS_FULL)
gmcp("Merc.Talents", TAL_FULL); gmcp("Merc.Skills", SKL_FULL)
fire_timers()

check(st("status"):find("-- Stabby --", 1, true) ~= nil, "status: named after the merc")
check(st("status"):find("offensive", 1, true) ~= nil and st("status"):find("Edged", 1, true) ~= nil, "status: class and damage type")
check(st("status"):find("75/150", 1, true) ~= nil and st("status"):find("1/30", 1, true) ~= nil, "status: permanent and instance level against their caps")
check(st("status"):find("19,000/19,000", 1, true) ~= nil, "status: HP against max, thousands separated")
check(st("status"):find("+13/round", 1, true) ~= nil and st("status"):find("+10/round", 1, true) ~= nil, "status: both regens")
check(st("status"):find("following", 1, true) ~= nil and st("status"):find("awake", 1, true) ~= nil, "status: follow and dormant flags read as words")
check(st("status"):find("not yet understood", 1, true) ~= nil and st("status"):find("theme=entropy_herald", 1, true) ~= nil,
  "status: the unnamed fields are shown raw under their own keys")
check(st("status"):find("abils=", 1, true) == nil, "status: an EMPTY abils is not worth a line")
check(st("hp") == "19000" and st("hp_max") == "19000" and st("stam") == "264" and st("ap_max") == "125", "gauges: published from the snapshot")
check(st("target") == "" and st("target_hp") == "0", "gauges: no target, target bar at zero")
check(st("summary"):find("Stabby", 1, true) ~= nil and st("summary"):find("HP 19,000/19,000", 1, true) ~= nil, "summary: name and HP")

-- 3. THE DELTA MERGES. A round's {stam, target_hp, target} must not cost hp_max.
gmcp("Merc.Vitals", VIT_D1); fire_timers()
check(st("hp_max") == "19000" and st("hp") == "19000", "delta: hp and hp_max survive a round that did not mention them")
check(st("stam") == "230", "delta: stam moves")
check(st("target") == "Wiremouth" and st("target_hp") == "91", "delta: the target and its health appear")
check(st("status"):find("Wiremouth", 1, true) ~= nil and st("status"):find("91%", 1, true) ~= nil, "delta: the Status page shows the fight")
check(st("summary"):find("-> Wiremouth 91%", 1, true) ~= nil, "delta: ...and so does the summary")
gmcp("Merc.Vitals", VIT_D2); gmcp("Merc.Vitals", VIT_D3); fire_timers()
check(st("stam") == "162" and st("target_hp") == "90" and st("target") == "Wiremouth",
  "delta: a round that carries only stam keeps the target and its last health")
-- the fight ends: the server sends an empty target. Here WITHOUT a target_hp, so
-- the last mob's 90% is still in the snapshot - the bar must read zero anyway.
gmcp("Merc.Vitals", '{ "merc": "Stabby", "target": "" }'); fire_timers()
check(st("target") == "" and st("target_hp") == "0", "delta: an empty target zeroes the target bar even with a stale target_hp")
check(st("status"):find("Target       @{dim}none", 1, true) ~= nil, "delta: ...and the page says none")

-- 4. Info delta: the instance level climbs, everything else stays
gmcp("Merc.Info", INFO_D2); fire_timers()
check(st("status"):find("2/30", 1, true) ~= nil, "info delta: instance level 2")
check(st("status"):find("75/150", 1, true) ~= nil and st("status"):find("Edged", 1, true) ~= nil, "info delta: the rest of the snapshot stands")

-- 5. Stats
check(st("stats"):find("55,780", 1, true) ~= nil and st("stats"):find("next 76,243", 1, true) ~= nil, "stats: permanent xp and the next level's cost")
check(st("stats"):find("next 64", 1, true) ~= nil, "stats: instance xp next")
check(st("stats"):find("42", 1, true) ~= nil and st("stats"):find("unspent", 1, true) ~= nil, "stats: unspent skill points")
check(st("stats"):find("302,411,929", 1, true) ~= nil, "stats: lifetime damage beside the session figure")
check(st("stats"):find("28,119,084", 1, true) ~= nil and st("stats"):find("2,036,650", 1, true) ~= nil, "stats: the spend and its parts")
gmcp("Merc.Stats", STATS_D1); fire_timers()
check(st("stats"):find("Damage out   48", 1, true) ~= nil and st("stats"):find("/ 302,411,977", 1, true) ~= nil, "stats delta: session and lifetime both move")
check(st("stats"):find("1,762", 1, true) ~= nil, "stats delta: a field the delta did not carry (life_dmg_in) survives")

-- 6. Talents: sorted by level gate, trained/locked/open decided against the level
local tr = rows("talents")
check(#tr == 5, "talents: five rows (got " .. #tr .. ")")
check(tr[1]:match("^bandage\t") ~= nil and tr[5]:match("^frenzy\t") ~= nil, "talents: sorted by min_level, bandage first, frenzy last")
check(tr[2] == "critical\t10\t10\t30\ttrained", "talents: critical is trained (" .. tr[2] .. ")")
check(tr[3] == "combo\t0\t0\t60\topen", "talents: combo is open at level 75 (" .. tr[3] .. ")")
check(tr[4] == "rend\t0\t0\t90\tlocked (lvl 90)", "talents: rend is locked below 90 (" .. tr[4] .. ")")
check(st("talents_head"):find("5 free", 1, true) ~= nil and st("talents_head"):find("next point 640,090", 1, true) ~= nil, "talents: the header says what is free and what the next costs")

-- 7. Skills: trained first, highest effective up, then the rest by name
local sr = rows("skills")
check(#sr == 13, "skills: thirteen rows (got " .. #sr .. ")")
check(sr[1] == "attacks\t10\t10" and sr[2] == "cost reduction\t10\t10", "skills: the two 10s first, by name (" .. sr[1] .. " / " .. sr[2] .. ")")
check(sr[3] == "stamina regen\t8\t8" and sr[4] == "ap regen\t5\t5", "skills: then 8 and 5")
check(sr[5]:match("^ability boost\t0\t0") ~= nil, "skills: the untrained follow alphabetically")
check(st("skills_head"):find("42 free", 1, true) ~= nil and st("skills_head"):find("33 allocated", 1, true) ~= nil, "skills: header")

-- 8. the alias prints the status page
printed = {}
for _, a in ipairs(aliases) do if a.pattern == "^mhud$" then a.run() end end
check(said("[merc]"), "mhud: prints into the output window")
check(said("Stabby"), "mhud: ...the status page")
local claims_merc = false
for _, a in ipairs(aliases) do if a.pattern:find("merc", 1, true) then claims_merc = true end end
check(not claims_merc, "mhud: the game's own 'merc' command is left alone")

-- 9. a reconnect starts over: no merc this time means no bars from the last one
for _, fn in ipairs(connect_hooks) do fn() end
fire_timers()
check(st("hp") == "" and st("summary"):find("waiting", 1, true) ~= nil, "reconnect: the snapshots are cleared")
check(st("talents") == "", "reconnect: ...the tables too")

-- 10. a bad payload is ignored, not fatal
gmcp("Merc.Vitals", "not json"); fire_timers()
check(st("hp") == "", "garbage: ignored")

-- 11. THE PROPERTY: nothing here ever sends a command
check(scrye.send == nil, "the client stub has no send, and the plugin never asked for one")

print(string.format("merc_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
