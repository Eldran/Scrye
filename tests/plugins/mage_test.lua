-- Harness for 3s-mage. Every payload below is VERBATIM from the capture
-- (_lab/captures/gmcp-fields-Eldran-20260903-1842.md, 3 Sep 2026): Joakim's own
-- mage, a fresh login followed by ten minutes of fighting.
--
-- What this file protects: that a Guild.State delta merges into the snapshot
-- (a {umbra} round must not cost the bars their umbra_max), that PAGED packages
-- assemble across their pages and a delta after them keeps the paged lists, that
-- the seven spell packages merge into one book shown a category at a time with
-- the category remembered, that focuses sort by points and skills draw under
-- their parent with affordable rows called out, that contingencies sort by
-- priority, that the familiar and the actives read as words, and that the
-- unnamed fields are shown raw under the server's own names.
--
--   lua5.4 mage_test.lua      (from anywhere; tests/plugins/run.sh runs them all, as CI does)
--
-- Mutants killed (3 Sep): 26 (30 after the fields were named) - delta-replaces-snapshot, pages not appended, a
-- full burst merging over the old list, every sort reversed, the category
-- filter, the stored shelf, and each of the words the Status tab chooses.
-- One EQUIVALENT mutant, documented: the spellbook's dedupe by name. The
-- directory splits offensive and enhancing spells across packages BY LEVEL, so no
-- spell arrives twice in this capture; the guard is for a server that ever
-- overlaps them, and nothing in the capture can exercise it.

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-mage/main.lua"

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
local printed, gmcp_hooks, aliases, state, panel, timers, next_id, store

local function fresh_client(keep_store)
  printed, gmcp_hooks, aliases = {}, {}, {}
  state, panel, timers, next_id = {}, nil, {}, 0
  if not keep_store then store = {} end
  scrye = {
    id = "3s-mage",
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
    store = { get = function(k) return store[k] end, set = function(k, v) store[k] = v end },
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
-- panel text is read with its markup stripped: the checks are about the words
local function st(k)
  return (tostring(state["plugin.3s-mage." .. k] or ""):gsub("@{[^}]*}", ""):gsub("@@", "@"))
end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end
local function mage(arg) aliases[1].run(arg) end
local function lines(k)
  local out = {}
  for line in (st(k) .. "\n"):gmatch("([^\n]*)\n") do if line ~= "" then out[#out + 1] = line end end
  return out
end
local function pos(k, needle)
  for i, l in ipairs(lines(k)) do if l:find(needle, 1, true) then return i end end
  return nil
end
local function button(tab, label)
  for _, t in ipairs(panel.tabs) do
    if t.title == tab then
      for _, w in ipairs(t.widgets) do
        if w.type == "buttonrow" then
          for _, b in ipairs(w.buttons) do if b.text == label then return b.action end end
        end
      end
    end
  end
end

-- ---- capture payloads, verbatim ----
local ST_FULL = '{ "bridge_reset_pct": 28, "imbues": 6, "concentration_max": 18880, "umbra_max": 19725, "gxp_to_next": 1451348, "gem_pct": 2, "rifts": 3, "school_casts": 2, "magical_reset_pct": 77, "full": 1, "concentration_pct": 0, "guild": "mage", "imbue_reset_pct": 22, "gxp_last_round": 89483320, "bridges": 5, "concentration": 0, "level_pct": 85, "umbra_pct": 99, "umbra": 19700 }'
local ST_D1   = '{ "gxp_last_round": 0, "guild": "mage", "umbra": 19644, "gem_pct": 90 }'
local ST_D2   = '{ "guild": "mage", "umbra": 19590 }'
local ST_D3   = '{ "umbra_pct": 98, "guild": "mage", "umbra": 19517 }'
local ST_D4   = '{ "gxp_last_round": 31, "bridge_reset_pct": 21, "guild": "mage", "umbra": 9763, "gxp_to_next": 1446574, "imbue_reset_pct": 71 }'
local PROG_P1 = '{ "corpse_donation_label": "You have embraced a few of your foes.", "gxp": 89483320, "item_net_count": 1980, "full": 1, "item_total_value": 3032304, "pages": 2, "explorer_bonus_label": "You have a small bonus from explored rooms.", "guild": "mage", "guild_level": 163, "coins_donated": 38346402, "gxp_per_hour": 41198.581952117864, "guild_play_seconds": 7820004, "combat_seconds": 3121450, "gxp_to_spend": 536707, "clarity_pct": 16.800000000000001, "channel_minutes": 49, "guild_quest_complete": 1, "page": 1, "item_average_value": 1531 }'
local PROG_P2 = '{ "quest_bonus_label": "You have no bonus from quest points.", "full": 1, "pages": 2, "sp_burnt": 145241910, "page": 2, "guild": "mage" }'
local PROG_D1 = '{ "guild_play_seconds": 7820020, "sp_burnt": 145242380, "guild": "mage" }'
local INFO    = '{ "status": "Guardian", "title": "the Guardian of the Veil of the Ascendant Astral", "joined_at": 1604431594, "recruiter": "", "school_short": "AA", "full": 1, "school": "Ascendant Astral", "school_joined_at": 1688407168, "guild": "mage" }'
local ASSETS  = '{ "staff": { "charge_pct": 0, "shelter_pct": 0, "attuned": 0, "active": 0 }, "full": 1, "summons_total": 0, "familiar": { "level": 71, "type": "cat", "status": "", "hp_pct": 100, "name": "beep", "active": 1 }, "summons": [], "guild": "mage", "summons_truncated": 0 }'
local ACT_0   = '{ "astral_bonuses": [ ], "full": 1, "guild": "mage", "effects": [ ] }'
local ACT_2   = '{ "guild": "mage", "effects": [ "aura", "shroud" ] }'
local ACT_4   = '{ "guild": "mage", "effects": [ "aura", "shroud", "strength_of_oceans", "perform" ] }'
local CFG_P1  = '{ "contingencies": [ { "action": "cast blink", "threshold": 0, "when": "end of round", "priority": 7, "direction": "failed", "trigger": "blinking", "trigger_label": "blink" }, { "action": "cast aura of light", "threshold": 0, "when": "end of round", "priority": 9, "direction": "failed", "trigger": "aura", "trigger_label": "aura" } ], "full": 1, "pages": 5, "guild": "mage", "page": 1 }'
local CFG_P2  = '{ "contingencies": [ { "action": "cast protection from good", "threshold": 0, "when": "end of round", "priority": 3, "direction": "failed", "trigger": "prot_good", "trigger_label": "prot_good" }, { "action": "cast shroud of storms", "threshold": 0, "when": "end of round", "priority": 2, "direction": "failed", "trigger": "shroud", "trigger_label": "shroud" } ], "full": 1, "pages": 5, "guild": "mage", "page": 2 }'
local CFG_P3  = '{ "contingencies": [ { "action": "cast protection from evil", "threshold": 0, "when": "end of round", "priority": 4, "direction": "failed", "trigger": "prot_evil", "trigger_label": "prot_evil" }, { "action": "cast prismatic aura", "threshold": 0, "when": "end of round", "priority": 6, "direction": "failed", "trigger": "prismatic_aura", "trigger_label": "prismatic aura" } ], "full": 1, "pages": 5, "guild": "mage", "page": 3 }'
local CFG_P4  = '{ "contingencies": [ { "action": "cast greater shield", "threshold": 0, "when": "end of round", "priority": 5, "direction": "failed", "trigger": "shield", "trigger_label": "shield" } ], "full": 1, "pages": 5, "guild": "mage", "page": 4 }'
local CFG_P5  = '{ "perform": { "action": "", "sp_threshold": 0, "mode": "inactive" }, "full": 1, "pages": 5, "page": 5, "contingency_limit": 11, "guild": "mage" }'
local CFG_D1  = '{ "guild": "mage", "perform": { "action": "cast shoal", "sp_threshold": 400, "mode": "combat_active" } }'
local FOC_P1  = '{ "allowed": 11, "full": 1, "pages": 4, "guild": "mage", "page": 1 }'
local FOC_P2  = '{ "full": 1, "pages": 4, "guild": "mage", "focuses": [ { "points": 23141, "cost_reduction_pct": 23, "spell": "blink", "standing": "Obsessed" }, { "points": 286102, "cost_reduction_pct": 39, "spell": "greater shield", "standing": "Claimed Ownership" }, { "points": 110924, "cost_reduction_pct": 35, "spell": "major globe", "standing": "Taken to new levels" }, { "points": 39888, "cost_reduction_pct": 28, "spell": "prismatic aura", "standing": "Mastered" }, { "points": 1447433, "cost_reduction_pct": 48, "spell": "shoal", "standing": "Redesigned" } ], "page": 2 }'
local FOC_P3  = '{ "full": 1, "pages": 4, "guild": "mage", "focuses": [ { "points": 56818, "cost_reduction_pct": 32, "spell": "shroud of storms", "standing": "Taken to new levels" }, { "points": 505510, "cost_reduction_pct": 42, "spell": "sunspear", "standing": "Redesigned" }, { "points": 316355, "cost_reduction_pct": 39, "spell": "twilight note", "standing": "Claimed Ownership" } ], "page": 3 }'
local FOC_P4  = '{ "used": 8, "over_limit": 0, "full": 1, "pages": 4, "page": 4, "guild": "mage" }'
-- the later, two-page delta form: no full, the list re-sent with fresher points
local FOC_D1  = '{ "pages": 2, "page": 1, "focuses": [ { "points": 23142, "cost_reduction_pct": 23, "spell": "blink", "standing": "Obsessed" }, { "points": 286103, "cost_reduction_pct": 39, "spell": "greater shield", "standing": "Claimed Ownership" }, { "points": 110924, "cost_reduction_pct": 35, "spell": "major globe", "standing": "Taken to new levels" }, { "points": 39889, "cost_reduction_pct": 28, "spell": "prismatic aura", "standing": "Mastered" }, { "points": 1447433, "cost_reduction_pct": 48, "spell": "shoal", "standing": "Redesigned" } ], "guild": "mage" }'
local FOC_D2  = '{ "pages": 2, "page": 2, "focuses": [ { "points": 56818, "cost_reduction_pct": 32, "spell": "shroud of storms", "standing": "Taken to new levels" }, { "points": 505510, "cost_reduction_pct": 42, "spell": "sunspear", "standing": "Redesigned" }, { "points": 316355, "cost_reduction_pct": 39, "spell": "twilight note", "standing": "Claimed Ownership" } ], "guild": "mage" }'
local SK_P1   = '{ "full": 1, "pages": 5, "gxp_available": 536707, "guild": "mage", "page": 1 }'
local SK_P2   = '{ "full": 1, "pages": 5, "skills": [ { "next_cost": 0, "current": 115, "at_max": 1, "affordable": 0, "maximum": 115, "parent": "", "name": "axiomatics" }, { "next_cost": 0, "current": 95, "at_max": 1, "affordable": 0, "maximum": 95, "parent": "", "name": "statics" }, { "next_cost": 0, "current": 50, "at_max": 1, "affordable": 0, "maximum": 50, "parent": "axiomatics", "name": "scope" }, { "next_cost": 0, "current": 15, "at_max": 1, "affordable": 0, "maximum": 15, "parent": "statics", "name": "balance" } ], "guild": "mage", "page": 2 }'
local SK_P3   = '{ "full": 1, "pages": 5, "skills": [ { "next_cost": 0, "current": 75, "at_max": 1, "affordable": 0, "maximum": 75, "parent": "axiomatics", "name": "potence" }, { "next_cost": 0, "current": 90, "at_max": 1, "affordable": 0, "maximum": 90, "parent": "statics", "name": "intensity" }, { "next_cost": 0, "current": 0, "at_max": 1, "affordable": 0, "maximum": 0, "parent": "", "name": "poetics" }, { "next_cost": 0, "current": 103, "at_max": 1, "affordable": 0, "maximum": 103, "parent": "", "name": "fortitude" } ], "guild": "mage", "page": 3 }'
local SK_P4   = '{ "full": 1, "pages": 5, "skills": [ { "next_cost": 50, "current": 0, "at_max": 0, "affordable": 1, "maximum": 10, "parent": "poetics", "name": "force" }, { "next_cost": 0, "current": 238, "at_max": 1, "affordable": 0, "maximum": 238, "parent": "", "name": "potential" }, { "next_cost": 50, "current": 0, "at_max": 0, "affordable": 1, "maximum": 10, "parent": "poetics", "name": "meter" }, { "next_cost": 0, "current": 141, "at_max": 1, "affordable": 0, "maximum": 141, "parent": "", "name": "integrity" } ], "guild": "mage", "page": 4 }'
local SK_P5   = '{ "full": 1, "pages": 5, "skills": [ { "next_cost": 605000, "current": 37, "at_max": 0, "affordable": 0, "maximum": 81, "parent": "", "name": "construction" } ], "guild": "mage", "page": 5 }'
local SK_D1   = '{ "guild": "mage", "gxp_available": 541261 }'
local SPD_P1  = '{ "categories": [ { "category": "O", "max_level": 100, "package": "Guild.SpellsOffense", "label": "offensive, level 1-100" }, { "category": "O", "min_level": 101, "package": "Guild.SpellsAdvanced", "label": "offensive, level 101+" }, { "category": "D", "package": "Guild.SpellsDefense", "label": "defensive" }, { "category": "E", "max_level": 50, "package": "Guild.SpellsEnhanceLo", "label": "enhancing, level 1-50" } ], "full": 1, "pages": 3, "guild": "mage", "page": 1 }'
local OFF_P1  = '{ "full": 1, "pages": 2, "spells": [ { "level": 1, "focus_label": "Redesigned", "current_cost": 1, "learned": 1, "base_cost": 1, "category": "O", "online": 1, "name": "ghost fire" }, { "level": 7, "focus_label": "Taken to new levels", "current_cost": 2, "learned": 1, "base_cost": 3, "category": "O", "online": 1, "name": "spell wind" }, { "learned": 1, "level": 12, "current_cost": 6, "base_cost": 6, "category": "O", "online": 1, "name": "moon shadow" } ], "guild": "mage", "page": 1 }'
local OFF_P2  = '{ "full": 1, "pages": 2, "spells": [ { "level": 90, "focus_label": "Redesigned", "current_cost": 18, "learned": 1, "base_cost": 42, "category": "O", "online": 1, "name": "nest of serpents" }, { "level": 100, "focus_label": "Redesigned", "current_cost": 22, "learned": 1, "base_cost": 45, "category": "O", "online": 1, "name": "daystar" } ], "guild": "mage", "page": 2 }'
local UTIL_P1 = '{ "full": 1, "pages": 2, "spells": [ { "learned": 1, "level": 6, "current_cost": 0, "base_cost": 0, "category": "I", "online": 1, "name": "vocalize" }, { "learned": 1, "level": 6, "current_cost": 26, "base_cost": 30, "category": "I", "online": 1, "name": "comprehend" }, { "learned": 1, "level": 14, "current_cost": 0, "base_cost": 0, "category": "I", "online": 1, "name": "monitor" }, { "learned": 1, "level": 19, "current_cost": 48, "base_cost": 55, "category": "I", "online": 1, "name": "identify" } ], "guild": "mage", "page": 1 }'
local UTIL_P2 = '{ "full": 1, "pages": 2, "spells": [ { "learned": 1, "level": 63, "current_cost": 97, "base_cost": 110, "category": "I", "online": 1, "name": "memetic mists" }, { "learned": 1, "level": 75, "current_cost": 223, "base_cost": 250, "category": "I", "online": 1, "name": "augur" } ], "guild": "mage", "page": 2 }'
-- SpellsAdvanced carries BOTH offensive 101+ and enhancing 51-75 (Guild.Spells says so)
local ADV_P1  = '{ "full": 1, "pages": 1, "spells": [ { "level": 54, "focus_label": "Claimed Ownership", "current_cost": 37, "learned": 1, "base_cost": 75, "category": "E", "online": 1, "name": "vine fetters" }, { "learned": 0, "level": 110, "current_cost": 300, "base_cost": 300, "category": "O", "online": 0, "name": "sunspear test" } ], "guild": "mage", "page": 1 }'

-- =====================================================================
-- 1. honest before the feed
fresh_client(); dofile(PLUGIN); fire_timers()
check(panel ~= nil and panel.title == "Mage", "the panel is registered")
local titles = {}
for _, t in ipairs(panel.tabs) do titles[#titles + 1] = t.title end
check(table.concat(titles, ",") == "Status,Spells,Focus,Skills,Config,Progress",
  "six tabs in order (got " .. table.concat(titles, ",") .. ")")
check(st("status"):find("waiting for Guild.State", 1, true) ~= nil, "before the feed the Status tab says what it waits for")
check(st("spells"):find("waiting for the Guild.Spells", 1, true) ~= nil, "...and Spells")
check(st("focus"):find("waiting for Guild.Focus", 1, true) ~= nil, "...and Focus")
check(st("skills"):find("waiting for Guild.Skills", 1, true) ~= nil, "...and Skills")
check(st("config"):find("waiting for Guild.Config", 1, true) ~= nil, "...and Config")
check(st("progress"):find("waiting for Guild.Progress", 1, true) ~= nil, "...and Progress")
check(st("summary"):find("waiting", 1, true) ~= nil, "...and the summary line")
check(st("umbra") == "" and st("umbra_max") == "", "gauges are empty")

local pkgs = {}
for _, h in ipairs(gmcp_hooks) do pkgs[h.pkg] = true end
for _, p in ipairs({ "Guild.State", "Guild.Progress", "Guild.Info", "Guild.Assets", "Guild.Actives",
                     "Guild.Config", "Guild.Focus", "Guild.Skills", "Guild.Spells",
                     "Guild.SpellsOffense", "Guild.SpellsAdvanced", "Guild.SpellsDefense",
                     "Guild.SpellsEnhanceLo", "Guild.SpellsEnhanceHi", "Guild.SpellsSummon", "Guild.SpellsUtility" }) do
  check(pkgs[p], "subscribes to " .. p)
end
local n = 0; for _ in pairs(pkgs) do n = n + 1 end
check(n == 16, "...and nothing else (" .. n .. ")")

-- 2. the login burst
gmcp("Guild.State", ST_FULL)
gmcp("Guild.Progress", PROG_P1); gmcp("Guild.Progress", PROG_P2)
gmcp("Guild.Info", INFO); gmcp("Guild.Assets", ASSETS); gmcp("Guild.Actives", ACT_0)
fire_timers()
check(st("status"):find("Umbra          19,700/19,725", 1, true) ~= nil, "status: umbra against max, thousands separated")
check(st("status"):find("Concentration  0/18,880", 1, true) ~= nil, "status: concentration")
check(st("status"):find("imbues   6", 1, true) ~= nil and st("status"):find("bridges  5", 1, true) ~= nil
      and st("status"):find("rifts    3", 1, true) ~= nil, "status: the workings")
check(st("status"):find("imbues 22%", 1, true) ~= nil and st("status"):find("bridges 28%", 1, true) ~= nil, "status: the clocks toward the next imbues and bridges")
check(st("status"):find("School reset   77%", 1, true) ~= nil, "status: the school-spell reset clock")
check(st("status"):find("Gem            2% left", 1, true) ~= nil and st("status"):find("about to break", 1, true) ~= nil, "status: a gem at 2% is about to break")
check(st("status"):find("Guild level    163", 1, true) ~= nil and st("status"):find("85% of the way to the next", 1, true) ~= nil, "status: level progress beside the level")
check(st("status"):find("Guild level    163", 1, true) ~= nil, "status: guild level comes from Progress")
check(st("status"):find("To next level  1,451,348 gxp", 1, true) ~= nil, "status: gxp to next")
check(st("status"):find("last round", 1, true) == nil, "status: the login snapshot's gxp_last_round (the total, a server quirk) is not shown as a round")
check(st("status"):find("Gxp to spend   536,707", 1, true) ~= nil, "status: gxp to spend")
check(st("status"):find("School         Ascendant Astral", 1, true) ~= nil and st("status"):find("(AA)", 1, true) ~= nil, "status: school and its short form")
check(st("status"):find("Guardian of the Veil", 1, true) ~= nil, "status: the title")
check(st("status"):find("beep the cat, level 71", 1, true) ~= nil and st("status"):find("100% hp", 1, true) ~= nil, "status: the familiar reads as a sentence")
check(st("status"):find("Staff          none active", 1, true) ~= nil, "status: an inactive staff says so")
check(st("status"):find("Summons        none", 1, true) ~= nil, "status: no summons")
check(st("status"):find("nothing up", 1, true) ~= nil, "status: no actives at login")
check(st("status"):find("Not yet understood", 1, true) ~= nil and st("status"):find("school_casts       2", 1, true) ~= nil,
      "status: the one unnamed field is shown raw")
check(st("status"):find("gem_pct", 1, true) == nil and st("status"):find("clarity_pct", 1, true) == nil, "...and the named ones are not in the raw block")
check(st("umbra") == "19700" and st("umbra_max") == "19725" and st("conc_max") == "18880", "gauges: published from the snapshot")
check(st("summary"):find("Umbra 19,700/19,725", 1, true) ~= nil and st("summary"):find("6 imbue 5 bridge 3 rift", 1, true) ~= nil, "summary: pools and workings")

-- 3. THE DELTA MERGES: a round's {umbra} must not cost umbra_max
gmcp("Guild.State", ST_D1); gmcp("Guild.State", ST_D2); gmcp("Guild.State", ST_D3); fire_timers()
check(st("umbra") == "19517" and st("umbra_max") == "19725", "delta: umbra moves, the max survives")
check(st("status"):find("Umbra          19,517/19,725", 1, true) ~= nil, "...and the text agrees")
check(st("status"):find("Gem            90% left", 1, true) ~= nil and st("status"):find("about to break", 1, true) == nil,
      "delta: a new gem reads 90% left and is no longer about to break")
gmcp("Guild.State", ST_D4); fire_timers()
check(st("status"):find("last round +31", 1, true) ~= nil, "delta: a real per-round gxp figure IS shown")
check(st("status"):find("imbues 71%", 1, true) ~= nil and st("status"):find("bridges 21%", 1, true) ~= nil, "delta: the clocks move")
check(st("status"):find("To next level  1,446,574", 1, true) ~= nil, "delta: gxp to next moves")
check(st("summary"):find("Umbra 9,763/19,725", 1, true) ~= nil, "summary follows the delta")

-- 3b. a Progress delta (no pages) keeps the paged page-1 fields
gmcp("Guild.Progress", PROG_D1); fire_timers()
check(st("status"):find("Guild level    163", 1, true) ~= nil, "progress delta: guild level from page 1 survives")
check(st("progress"):find("SP burnt       145,242,380", 1, true) ~= nil, "progress delta: the delta's field updates")

-- 4. actives
gmcp("Guild.Actives", ACT_2); fire_timers()
check(st("status"):find("aura  shroud", 1, true) ~= nil, "actives: listed in the server's names")
gmcp("Guild.Actives", ACT_4); fire_timers()
check(st("status"):find("strength_of_oceans  perform", 1, true) ~= nil, "actives: the list grows")

-- 5. progress tab
check(st("progress"):find("Total          89,483,320", 1, true) ~= nil, "progress: total gxp")
check(st("progress"):find("Per hour       41,198", 1, true) ~= nil, "progress: gxp/hour, floored")
check(st("progress"):find("In the guild   90d", 1, true) ~= nil, "progress: play time as days")
check(st("progress"):find("In combat      36d", 1, true) ~= nil and st("progress"):find("39% of it", 1, true) ~= nil, "progress: combat time and its share")
check(st("progress"):find("Joined         2020-11-0", 1, true) ~= nil, "progress: the join date from the epoch")
check(st("progress"):find("Clarity        16.8%", 1, true) ~= nil and st("progress"):find("climbs to 100", 1, true) ~= nil, "progress: clarity, with where it stops")
check(st("progress"):find("Guild quest    solved", 1, true) ~= nil, "progress: the guild quest is solved")
check(st("progress"):find("Channelling    49m 00s", 1, true) ~= nil, "progress: channelling time from minutes")
check(st("progress"):find("Coins          38,346,402", 1, true) ~= nil, "progress: coins donated")
check(st("progress"):find("Items          1,980 worth 3,032,304", 1, true) ~= nil and st("progress"):find("avg 1,531", 1, true) ~= nil, "progress: items donated")
check(st("progress"):find("embraced a few of your foes", 1, true) ~= nil and st("progress"):find("no bonus from quest points", 1, true) ~= nil,
      "progress: the server's prose from BOTH pages")

-- 6. config: five pages assemble, contingencies sort by priority, a delta keeps them
for _, p in ipairs({ CFG_P1, CFG_P2, CFG_P3, CFG_P4, CFG_P5 }) do gmcp("Guild.Config", p) end
fire_timers()
check(st("config"):find("Mode           inactive", 1, true) ~= nil, "config: perform inactive at login")
check(st("config"):find("Contingencies (7 of 11)", 1, true) ~= nil, "config: seven contingencies against the limit")
local c = lines("config")
local i2, i3, i9 = pos("config", "cast shroud of storms"), pos("config", "cast protection from good"), pos("config", "cast aura of light")
check(i2 and i3 and i9 and i2 < i3 and i3 < i9, "config: sorted by priority (2 before 3 before 9)")
check(st("config"):find("  2  end of round, failed  shroud", 1, true) ~= nil and st("config"):find("shroud           cast shroud of storms", 1, true) ~= nil,
      "config: priority, when+direction, trigger label, action")
gmcp("Guild.Config", CFG_D1); fire_timers()
check(st("config"):find("Mode           combat_active", 1, true) ~= nil and st("config"):find("Action         cast shoal", 1, true) ~= nil
      and st("config"):find("SP threshold   400", 1, true) ~= nil, "config: the perform delta lands")
check(st("config"):find("Contingencies (7 of 11)", 1, true) ~= nil, "...and the paged contingencies survive the delta")

-- 7. focus: four pages, sorted by points, then the two-page re-send replaces the list
for _, p in ipairs({ FOC_P1, FOC_P2, FOC_P3, FOC_P4 }) do gmcp("Guild.Focus", p) end
fire_timers()
check(st("focus"):find("Focuses        8 of 11 allowed", 1, true) ~= nil, "focus: used against allowed")
check(st("focus"):find("OVER", 1, true) == nil, "focus: not over the limit")
local f1, f2, f3 = pos("focus", "shoal"), pos("focus", "sunspear"), pos("focus", "blink")
check(f1 and f2 and f3 and f1 < f2 and f2 < f3, "focus: sorted by points, most first")
check(st("focus"):find("shoal                   Redesigned", 1, true) ~= nil and st("focus"):find("1,447,433   48%", 1, true) ~= nil,
      "focus: spell, standing, points, cost reduction")
check(#lines("focus") == 2 + 8, "focus: eight rows (" .. #lines("focus") .. ")")
gmcp("Guild.Focus", FOC_D1); gmcp("Guild.Focus", FOC_D2); fire_timers()
check(#lines("focus") == 2 + 8, "focus: the two-page re-send replaces the list rather than doubling it")
check(st("focus"):find("23,142", 1, true) ~= nil, "...with the fresher points")
check(st("focus"):find("Focuses        8 of 11", 1, true) ~= nil, "...and the header fields from the full burst survive")

-- 7b. a FULL re-send that carries no list at all (every focus dropped) empties it:
--     a full burst replaces the paged keys, it does not merge over them
gmcp("Guild.Focus", '{ "allowed": 11, "full": 1, "pages": 2, "guild": "mage", "page": 1 }')
gmcp("Guild.Focus", '{ "used": 0, "over_limit": 0, "full": 1, "pages": 2, "page": 2, "guild": "mage" }')
fire_timers()
check(st("focus"):find("no spells focused", 1, true) ~= nil and pos("focus", "shoal") == nil,
      "focus: a full burst without the list clears it")
for _, p in ipairs({ FOC_P1, FOC_P2, FOC_P3, FOC_P4 }) do gmcp("Guild.Focus", p) end
fire_timers()

-- 8. skills: the tree
for _, p in ipairs({ SK_P1, SK_P2, SK_P3, SK_P4, SK_P5 }) do gmcp("Guild.Skills", p) end
fire_timers()
check(st("skills"):find("Gxp available  536,707", 1, true) ~= nil, "skills: gxp available")
local ax, sc, po = pos("skills", "axiomatics"), pos("skills", "  scope"), pos("skills", "  potence")
check(ax and sc and po and sc == ax + 1 and po == ax + 2, "skills: children drawn under their parent, indented")
check(st("skills"):find("115/115  at max", 1, true) ~= nil, "skills: at max says so")
check(st("skills"):find("50  affordable", 1, true) ~= nil, "skills: an affordable skill is called out")
check(st("skills"):find("construction", 1, true) ~= nil and st("skills"):find("37/81", 1, true) ~= nil and st("skills"):find("605,000", 1, true) ~= nil,
      "skills: a plain row shows have/max and the next cost")
gmcp("Guild.Skills", SK_D1); fire_timers()
check(st("skills"):find("Gxp available  541,261", 1, true) ~= nil, "skills: the gxp delta lands")
check(pos("skills", "construction") ~= nil, "...and the paged tree survives it")

-- 9. spells: the book merges across packages, one category at a time
gmcp("Guild.Spells", SPD_P1)
gmcp("Guild.SpellsOffense", OFF_P1); gmcp("Guild.SpellsOffense", OFF_P2)
gmcp("Guild.SpellsUtility", UTIL_P1); gmcp("Guild.SpellsUtility", UTIL_P2)
gmcp("Guild.SpellsAdvanced", ADV_P1)
fire_timers()
check(st("spells_head"):find("offensive: 6 spell(s), 5 learned", 1, true) ~= nil,
      "spells: offensive by default, counted across Offense AND Advanced (" .. st("spells_head") .. ")")
local g, d, s = pos("spells", "ghost fire"), pos("spells", "daystar"), pos("spells", "sunspear test")
check(g and d and s and g < d and d < s, "spells: sorted by level")
check(st("spells"):find("  1  ghost fire", 1, true) ~= nil, "spells: level then name")
check(st("spells"):find("18 (42)", 1, true) ~= nil and st("spells"):find("Redesigned", 1, true) ~= nil, "spells: current cost, base in brackets, focus standing")
check(st("spells"):find("moon shadow", 1, true) ~= nil and st("spells"):find("6 (6)", 1, true) == nil, "spells: no bracket when the cost is not reduced")
check(st("spells"):find("(not learned)", 1, true) ~= nil and st("spells"):find("offline", 1, true) ~= nil, "spells: unlearned and offline are marked")
check(st("spells"):find("vine fetters", 1, true) == nil, "spells: the enhancing spell in Advanced is NOT on the offensive shelf")
button("Spells", "Infor")()
check(st("spells_head"):find("informational: 6 spell(s)", 1, true) ~= nil and pos("spells", "augur") ~= nil, "spells: the category button switches the shelf")
check(pos("spells", "ghost fire") == nil, "...and the other shelf is gone")
button("Spells", "Enhan")()
check(pos("spells", "vine fetters") ~= nil, "spells: enhancing finds the one in Advanced")
check(store["cat"] == "E", "spells: the choice is stored")

-- 10. the alias
printed = {}
mage("")
check(said("[mage]") and said("Umbra          9,763/19,725"), "'mage' prints the status page")
printed = {}
mage("spells off")
check(said("offensive: 6 spell(s)") and said("ghost fire"), "'mage spells off' picks by prefix and prints")
printed = {}
mage("spells xyz")
check(said("categories:"), "an unknown category lists the categories")
printed = {}
mage("focus")
check(said("shoal"), "'mage focus' prints the focus page")
printed = {}
mage("bogus")
check(said("mage spells"), "an unknown verb prints help")

-- 11. the stored category survives a reload
button("Spells", "Enhan")()
fresh_client(true); dofile(PLUGIN)
gmcp("Guild.SpellsUtility", UTIL_P1); gmcp("Guild.SpellsUtility", UTIL_P2)
gmcp("Guild.SpellsAdvanced", ADV_P1); fire_timers()
check(st("spells_head"):find("enhancing", 1, true) ~= nil, "reload: the shelf comes back as it was left")

-- 12. a reconnect starts over: a fresh Guild.State full replaces the snapshot
fresh_client(); dofile(PLUGIN)
gmcp("Guild.State", ST_D2); fire_timers()
check(st("umbra") == "19590" and st("umbra_max") == "", "a delta before any full is shown for what it is: umbra, no max")
gmcp("Guild.State", ST_FULL); fire_timers()
check(st("umbra_max") == "19725", "...and the full fills it in")

print(string.format("%d passed, %d failed", pass, fail))
os.exit(fail == 0 and 0 or 1)
