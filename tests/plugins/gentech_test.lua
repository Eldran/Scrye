-- Harness for 3s-gentech. Every payload below is VERBATIM from the capture
-- (_lab/captures/gmcp-fields-Sermi-gentech-20260901-0811.md, 1 Sep 2026), which
-- came from ANOTHER player's character - so this file is also the record of what
-- that capture actually contained, for whenever a Gentech player can tell us what
-- the unnamed fields mean.
--
-- Equivalent mutant, documented rather than faked: clearing the snapshot before
-- applying a completed burst (snap = {} in the assembler) changes nothing for
-- THIS plugin's packages - Guild.Progress is the only paged one here and its two
-- pages together carry every key it ever sends, so there is no unpaged-only key
-- for the clear to lose. The behaviour is still wrong in general and the Viking
-- harnesses kill it where it matters; contriving a key here that the server never
-- sends alone would be testing the fixture rather than the plugin.
--
-- What this file protects: the two-page Guild.Progress burst assembling into one
-- snapshot, the live countdowns rendering as time rather than as bare seconds, a
-- lapsed countdown reading as "--" rather than "0s", and - the point of the whole
-- design - that a field the capture does not explain is printed under the FEED'S
-- OWN NAME with a raw value instead of being given a label we invented.
--
--   lua5.4 gentech_test.lua      (from anywhere; tests/plugins/run.sh runs them all, as CI does)

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-gentech/main.lua"

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
    id = "3s-gentech",
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
local function st(k) return tostring(state["plugin.3s-gentech." .. k] or "") end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end


-- ---- capture payloads, verbatim ----
local STATE_FULL = '{ "bias_lock": 1, "g2n": 853877, "timeslides": 2, "tech_eff_pct": 100, "rush": 1, "pu_store_max": 5625, "cpc": 10000, "full": 1, "reset_pct": 64, "g2n_pct": 14, "glevel": 50, "guild": "gentech", "gen_eff_pct": 100, "illuminated": 0, "pu_store": 1685, "medkits_max": 999, "efield_mins": 171, "timeslides_max": 2, "res_creds": 43367239, "def_bias": 90, "atk_bias": 100, "gexp": 146123, "medkits": 999, "dgexp": 0, "pu_max": 11250, "com_timeslides": 1, "pu": 9750, "edna_secs": 482, "cpc_max": 10000 }'
local STATE_TICK = '{ "pu": 9748, "guild": "gentech", "pu_store": 1706, "edna_secs": 480 }'

local SYS_FULL = '{ "capsules": 0, "stabilize_secs": 9274, "hypergen": 0, "timescan_on": 0, "ddb_type": "Sonic", "edna_on": 1, "stabilize_artifact": 0, "full": 1, "timescan_audio": 0, "guild": "gentech", "tacshield_type": "Bladed", "life_support": 1, "efield": 2, "tactical_amount": "Max", "autorepair": 0, "hypergen_secs": 0, "synthorg_type": "Sonicator", "tacshield_adaptive": 1, "tactical_secs": 1328, "ddb_adaptive": 0, "synthorg_on": 1, "tactical_on": 1, "capsules_resupply": 1, "stabilize_on": 1 }'
local SYS_TICK = '{ "stabilize_secs": 9270, "guild": "gentech", "tactical_secs": 1324 }'

local INFO_FULL = '{ "co_title": "Chief Science Officer", "admin_title": "Gen-High Council", "echelon_pct": 65, "guild_age_secs": 31501804, "num_recruits": 0, "echelon_gexp": 1962, "echelon_insignia": "===*===", "division": "Defense", "full": 1, "rank_title": "Leader", "echelon": 25, "guild": "gentech", "echelon_title": "Terran Ascendent", "echelon_required": 3000, "class_name": "Science" }'

local PROG_P1 = '{ "donated": 500000, "best_kill_class": 576154880, "rested_secs": 77063, "class_bonus_pct": 70, "slots_total": 94, "pu_to_cpc_login": 12458, "quest_points": 607, "full": 1, "division_bonus_pct": 70, "pages": 2, "slots_filled": 94, "gexp_split": 100, "qp_factor_pct": 0, "guild": "gentech", "guild_age_factor_pct": 100, "pu_to_cpc": 107271001, "slots_free": 0, "quest_points_total": 808, "best_kill": "Bone Dragon", "combat_age_secs": 22067478, "num_died": 170, "phase_rank": 865, "staff_secs": 322404, "page": 1, "explorer_factor_pct": 100, "exp_earned": 189286103832 }'
local PROG_P2 = '{ "store_credits_max": 60, "store_credits": 60, "utilities_free": 3, "utilities": 5, "subsidy": 899689, "full": 1, "storage_in": 30161, "pages": 2, "total_kills": 60945, "page": 2, "guild": "gentech", "storage_out": 602 }'

local STATS_FULL = '{ "rounds_total": 0, "exp_per_kill": 0, "exp_min": 0, "rc_cs": 0, "gexp_cs": 0, "stating": 0, "stat_secs": 0, "pu_to_cpc_login": 12469, "cpc_used": 10208, "last_enemy_class": 0, "rc_min": 0, "rounds_enemy": 928, "full": 1, "exp_cs": 0, "best_enemy_class": 0, "guild": "gentech", "gexp_min": 0, "last_enemy": "", "best_enemy": "", "worst_enemy_class": 0, "rounds_fight": 0, "worst_enemy": "", "enemy_deaths": 0 }'

local CFG_FULL = '{ "hms_on": 1, "dna": 1, "pheal_hp": 2000, "autoguild": [ "meditate", "edna", "health", "conduit", "rush", "efield", "lsupp", "recharge", "ionbeam", "ddb", "tshift" ], "hms_cmd": "surge", "hms_val": 1300, "gentimer_autoreset": 1, "full": 1, "pheal_on": 1, "dna_source": "", "gentimer_secs": 0, "autocombat_on": 0, "autocombat": "", "guild": "gentech", "gentimer_on": 0, "pheal_percent": 0, "gentimer_audio": 1, "pheal": "surgekit", "dna_ready": 1, "hms_percent": 0, "dna_max": 2, "hms_combat_only": 0 }'

local POWERS_FULL = '{ "div_offense_pct": 48, "class_medical_pct": 32, "class_science": 20, "class_engineering_pct": 30, "class_science_pct": 37, "div_defense_pct": 51, "count": 94, "div_defense": 21, "full": 1, "div_offense": 20, "class_medical": 17, "class_engineering": 16, "guild": "gentech" }'

local function feed_all()
  gmcp("Guild.State", STATE_FULL)
  gmcp("Guild.Systems", SYS_FULL)
  gmcp("Guild.Info", INFO_FULL)
  gmcp("Guild.Progress", PROG_P1); gmcp("Guild.Progress", PROG_P2)
  gmcp("Guild.Stats", STATS_FULL)
  gmcp("Guild.Config", CFG_FULL)
  gmcp("Guild.Powers", POWERS_FULL)
  fire_timers()
end

-- =====================================================================
-- 1. honest before the feed
fresh_client(); dofile(PLUGIN); fire_timers()
check(panel ~= nil and panel.title == "Gentech", "the panel is registered")
local titles = {}
for _, t in ipairs(panel.tabs) do titles[#titles + 1] = t.title end
check(table.concat(titles, ",") == "Status,Systems,Progress,Stats,Config",
  "five tabs in order (got " .. table.concat(titles, ",") .. ")")
check(st("status"):find("waiting for Guild.State", 1, true) ~= nil, "Status says what it waits for")
check(st("systems"):find("waiting for Guild.Systems", 1, true) ~= nil, "Systems too")
check(st("config"):find("waiting for Guild.Config", 1, true) ~= nil, "Config too")

-- 2. the feed
feed_all()

-- pools
check(st("status"):find("9,750/11,250", 1, true) ~= nil, "status: PU against its max")
check(st("status"):find("1,685/5,625", 1, true) ~= nil, "status: the PU store has its own max")
check(st("status"):find("10,000/10,000", 1, true) ~= nil, "status: CPC")
check(st("status"):find("999/999", 1, true) ~= nil, "status: medkits")
check(st("status"):find("43,367,239", 1, true) ~= nil, "status: res credits")
check(st("status"):find("Terran Ascendent", 1, true) ~= nil, "status: the echelon title")
check(st("status"):find("1,962/3,000", 1, true) ~= nil,
  "status: echelon progress computed from gexp and required, not just the feed's percent")
-- and the reading is CORROBORATED: 1962/3000 is 65%, which is exactly what the
-- feed's own echelon_pct says. Two independent fields agreeing is the strongest
-- evidence available from a capture that the interpretation is right, so the
-- panel keeps showing both and this pins the agreement.
do
  local line
  for l in (st("status") .. "\n"):gmatch("([^\n]*)\n") do
    if l:find("Echelons", 1, true) then line = l end
  end
  local computed = line and line:match("(%d+)%%")
  local fed = line and line:match("feed says (%d+)%%")
  check(computed ~= nil and computed == fed,
    "status: ...and it agrees with the feed's own echelon_pct (" .. tostring(computed)
    .. " vs " .. tostring(fed) .. ")")
end

-- 3. THE DESIGN POINT: a field the capture cannot explain keeps the feed's own
--    name. Five of the original seven were answered by a Gentech player on
--    1 Sep and have moved out of this block into rows of their own; the two
--    that are still unnamed must stay in it, and the five must not come back.
check(st("status"):find("-- Not yet understood --", 1, true) ~= nil,
  "unknowns: they get their own block rather than being scattered")
check(st("status"):find("dgexp", 1, true) ~= nil,
  "unknowns: dgexp is still unnamed, so it keeps the server's field name")
check(st("status"):find("illuminated", 1, true) ~= nil, "unknowns: flags are shown as yes/no under their own name")
do
  -- the block ends at the blank line after it, or at the end of the tab
  local body, inside = {}, false
  for line in (st("status") .. "\n"):gmatch("([^\n]*)\n") do
    if inside then
      if line == "" then inside = false else body[#body + 1] = line end
    elseif line:find("Not yet understood", 1, true) then inside = true end
  end
  local blob = table.concat(body, "\n")
  for _, k in ipairs({ "g2n", "reset_pct", "phase_rank", "rush" }) do
    check(blob:find(k, 1, true) == nil,
      "unknowns: " .. k .. " is named now, so it is no longer in the raw block")
  end
end

-- 3b. gexp + g2n names the threshold the server never sends on its own, and the
--     percent that falls out of it has to match the g2n_pct the feed sent.
do
  local line
  for l in (st("status") .. "\n"):gmatch("([^\n]*)\n") do
    if l:find("Guild exp", 1, true) then line = l end
  end
  check(line ~= nil, "gexp: the guild-exp track has a row")
  check(line and line:find("146,123/1,000,000", 1, true) ~= nil,
    "gexp: 146,123 + 853,877 gives the 1,000,000 threshold")
  local computed = line and line:match("(%d+)%%")
  local fed = line and line:match("feed says (%d+)%%")
  check(computed == "14" and computed == fed,
    "gexp: ...and the computed percent equals the feed's g2n_pct (" .. tostring(computed)
    .. " vs " .. tostring(fed) .. ")")
  local togo
  for l in (st("status") .. "\n"):gmatch("([^\n]*)\n") do
    if l:find("to next", 1, true) then togo = l end
  end
  check(togo and togo:find("853,877 gexp", 1, true) ~= nil,
    "gexp: g2n is spelled out as what is still owed")
end
-- glevel 50 is the cap; echelons take over from there
check(st("status"):find("echelons from here", 1, true) ~= nil,
  "gexp: a glevel at the 50 cap says so")

-- 3c. reset_pct is the timeslide refill clock, so it rides with the timeslides
do
  local ts, rf
  local lines = {}
  for l in (st("status") .. "\n"):gmatch("([^\n]*)\n") do lines[#lines + 1] = l end
  for i, l in ipairs(lines) do
    if l:find("Timeslides", 1, true) then ts = i end
    if l:find("refill", 1, true) then rf = i end
  end
  check(ts ~= nil and rf == ts + 1, "reset_pct: the refill clock is the line under Timeslides")
  check(lines[rf] and lines[rf]:find("64%", 1, true) ~= nil, "reset_pct: ...showing the feed's 64%")
  check(lines[rf] and lines[rf]:find("new timeslides at 100%", 1, true) ~= nil,
    "reset_pct: ...and says what happens when it fills")
end

-- 3c2. echelon_gexp/echelon_required counts ECHELONS HELD against echelons
--      needed for the next ORDER. 1.0.0 called it "progress" inside the current
--      echelon, which was wrong; the label must say what is actually counted,
--      and the shortfall must be spelled out rather than left to subtraction.
do
  local ech, ord
  local lines = {}
  for l in (st("status") .. "\n"):gmatch("([^\n]*)\n") do lines[#lines + 1] = l end
  for i, l in ipairs(lines) do
    if l:find("Echelons", 1, true) then ech = i end
    if l:find("to Order", 1, true) then ord = i end
  end
  check(ech ~= nil, "order: the echelon count has a row of its own")
  check(ech ~= nil and ord == ech + 1, "order: ...with the shortfall to the next Order under it")
  check(lines[ord] and lines[ord]:find("1,038 more", 1, true) ~= nil,
    "order: 3,000 - 1,962 is spelled out, not left to the reader")
  check(st("status"):find("  progress   1,962", 1, true) == nil,
    "order: ...and the old 'progress' label is gone")
end

-- 3c3. gexp_split routes earned gexp between RESEARCH CREDITS and ECHELONS -
--      not between "a pool" and echelons, which is what 1.1.0's first draft
--      said. Both destinations must be named, and both halves computed, so the
--      line cannot be read as pointing at whichever number sits nearest it.
do
  local split, res
  local lines = {}
  for l in (st("status") .. "\n"):gmatch("([^\n]*)\n") do lines[#lines + 1] = l end
  for i, l in ipairs(lines) do
    if l:find("Gexp split", 1, true) then split = i end
    if l:find("Res credits", 1, true) then res = i end
  end
  check(split ~= nil, "split: gexp_split is on Status, where the destinations are")
  check(res ~= nil and split == res + 1, "split: ...directly under res credits, one of the two")
  local line = lines[split] or ""
  check(line:find("100% to research credits", 1, true) ~= nil,
    "split: the side the number names is spelled out, not left to the reader")
  check(line:find(" 0% to echelons", 1, true) ~= nil and line:find("100% to echelons", 1, true) == nil,
    "split: ...and the remainder is COMPUTED as 100-v, not echoed as v")
  check(st("status"):find("Gexp pool", 1, true) == nil,
    "split: gexp is not called a pool - research credits are what phase experiments")
end
check(st("progress"):find("gexp split", 1, true) == nil,
  "split: ...and it is no longer listed as a passive bonus factor")

-- 3d. phase_rank is how far the experiments are phased - it belongs to the record
check(st("progress"):find("Phase rank   865", 1, true) ~= nil,
  "phase_rank: shown on Progress under a real label")
check(st("status"):find("Phase rank", 1, true) == nil,
  "phase_rank: ...and not left behind on Status")

-- 3e. rush heals and it toggles, so it is a system row - even though the server
--     keeps its flag on Guild.State rather than on Guild.Systems
do
  local line
  for l in (st("systems") .. "\n"):gmatch("([^\n]*)\n") do
    if l:find("Rush heal", 1, true) then line = l end
  end
  check(line ~= nil, "rush: it has a row on the Systems tab")
  check(line and line:find("on", 1, true) ~= nil and line:find("?", 1, true) == nil,
    "rush: ...reading its switch from Guild.State, not the missing Guild.Systems key")
end

-- 4. the countdowns: rendered as time, and a lapsed one is NOT "0s"
check(st("systems"):find("2h 34m", 1, true) ~= nil,
  "systems: stabilize_secs 9274 renders as time, not as a bare number")
check(st("systems"):find("22m 08s", 1, true) ~= nil, "systems: tactical_secs 1328 likewise")
check(st("systems"):find("8m 02s", 1, true) ~= nil, "systems: edna_secs comes from Guild.State")
do
  -- hypergen_secs is 0 in the capture: a zero countdown means lapsed/ready, and
  -- printing "0s" would read like a clock that had stopped
  local hyper
  for line in (st("systems") .. "\n"):gmatch("([^\n]*)\n") do
    if line:find("HyperGen", 1, true) then hyper = line end
  end
  check(hyper ~= nil, "systems: HyperGen has a row")
  check(hyper and hyper:find("0s", 1, true) == nil, "systems: ...and a lapsed countdown is not shown as 0s")
end
check(st("systems"):find("Sonic", 1, true) ~= nil and st("systems"):find("Bladed", 1, true) ~= nil,
  "systems: the loadout types are listed")
check(st("systems"):find("Level        2", 1, true) ~= nil,
  "systems: efield is a LEVEL, not an on/off switch")
check(st("systems"):find("171 min left", 1, true) ~= nil, "systems: ...with its own minutes counter")

-- 5. the paged Guild.Progress burst assembles across both pages
check(st("progress"):find("60,945", 1, true) ~= nil, "progress: total_kills from page 2")
check(st("progress"):find("Bone Dragon", 1, true) ~= nil, "progress: best kill from page 1")
check(st("progress"):find("94/94", 1, true) ~= nil, "progress: power slots, all filled")
check(st("progress"):find("none free", 1, true) ~= nil, "progress: ...and zero free says so in words")
check(st("progress"):find("60/60", 1, true) ~= nil, "progress: store credits from page 2")
check(st("progress"):find("Defense", 1, true) ~= nil and st("progress"):find("Science", 1, true) ~= nil,
  "progress: division and class from Guild.Info")
check(st("progress"):find("engineering", 1, true) ~= nil, "progress: the Guild.Powers split rides along")

-- 6. config
check(st("config"):find("1. meditate", 1, true) ~= nil, "config: the autoguild order is numbered")
check(st("config"):find("11. tshift", 1, true) ~= nil, "config: ...all eleven of them")
check(st("config"):find("surgekit", 1, true) ~= nil, "config: the panic-heal item")
check(st("config"):find("below 2,000 hp", 1, true) ~= nil, "config: ...and its trigger")
check(st("config"):find("1/2", 1, true) ~= nil, "config: DNA against its max")
check(st("config"):find("none set", 1, true) ~= nil, "config: an empty dna_source says so rather than blank")

-- 7. an unpaged tick updates without wiping the burst
gmcp("Guild.State", STATE_TICK); gmcp("Guild.Systems", SYS_TICK); fire_timers()
check(st("status"):find("9,748/11,250", 1, true) ~= nil, "tick: PU moves")
check(st("status"):find("43,367,239", 1, true) ~= nil, "tick: ...without losing the rest of the burst")
check(st("systems"):find("Sonic", 1, true) ~= nil, "tick: ...nor the systems loadout")

-- 7b. g2n at zero means the level is earned, not that there is nothing to show:
--     "0 gexp" would read like a bug, so that case says "ready" instead
gmcp("Guild.State", '{ "guild": "gentech", "g2n": 0, "g2n_pct": 100 }'); fire_timers()
do
  local togo
  for l in (st("status") .. "\n"):gmatch("([^\n]*)\n") do
    if l:find("to next", 1, true) then togo = l end
  end
  check(togo ~= nil and togo:find("ready", 1, true) ~= nil,
    "gexp: g2n at 0 reads as ready")
  check(togo and togo:find("0 gexp", 1, true) == nil, "gexp: ...not as a bare 0")
end
gmcp("Guild.State", '{ "guild": "gentech", "g2n": 853877, "g2n_pct": 14 }'); fire_timers()

-- 8. the summary line carries the soonest countdown
check(st("summary"):find("PU 9,748", 1, true) ~= nil, "summary: the pool")
check(st("summary"):find("eDNA", 1, true) ~= nil,
  "summary: the SOONEST countdown is the one worth one line (eDNA at 8m beats tactical at 22m)")
-- ...and it really is the soonest, not just the last one checked: push eDNA out
-- past tactical and the line must change hands
gmcp("Guild.State", '{ "guild": "gentech", "edna_secs": 3600 }')
gmcp("Guild.Systems", '{ "guild": "gentech", "tactical_secs": 90 }')
fire_timers()
check(st("summary"):find("Tactical", 1, true) ~= nil and st("summary"):find("eDNA", 1, true) == nil,
  "summary: ...and it follows whichever is actually soonest")

-- 9. the aliases
printed = {}
for _, a in ipairs(aliases) do if a.pattern == "^gt$" then a.run() end end
check(said("[gentech]"), "gt: prints into the output window")
printed = {}
for _, a in ipairs(aliases) do if a.pattern == "^gtsys$" then a.run() end end
check(said("Stabilize"), "gtsys: prints the systems page")

print(string.format("gentech_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
