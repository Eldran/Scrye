-- 3S Gentech -- the Gentech guild HUD, built on the Guild.* GMCP packages a
-- gentech character receives. 3Scapes only.
--
-- Built from ONE capture, and not from Joakim's own character: a Gentech player's
-- log (1 Sep 2026, 7,985 messages / 14 packages). Seven fields the capture did
-- not explain were first shipped under the feed's own names, and five of those
-- came back answered (1 Sep):
--   g2n / g2n_pct  gexp still owed before the next glevel, and the same as a
--                  percent. Guild levels stop at 50 and become echelons after.
--   gexp_split     earned gexp is routed between RESEARCH CREDITS and ECHELONS,
--                  and the player sets the ratio in game and can change it at
--                  any time: 100 sends all of it to res_creds, 0 all of it to
--                  echelons. Research credits are what phase experiments, which
--                  is where phase_rank comes from. Because that is one side of
--                  the split, echelon_gexp/echelon_required is the other -
--                  echelons HELD against echelons needed for the next ORDER,
--                  not progress inside an echelon, which is what 1.0.0 called
--                  it. Two versions, two wrong labels on this pair; the numbers
--                  were never the problem.
--   reset_pct      the timeslide refill clock - at 100% you get a fresh set
--   phase_rank     how far the experiments have been trained/phased
--   rush           a healing power, so it sits with the systems that toggle
-- Two are still unnamed - dgexp and illuminated - and those keep the original
-- treatment: shown UNDER THE FEED'S OWN NAME with the raw value, under "not yet
-- understood" on the Status tab, rather than given a friendly label that might
-- be a lie. They are listed in docs/Plan-Improvements.md.
--
-- Packages consumed:
--   Guild.State    the pools and the standing numbers: pu/pu_max, pu_store,
--                  cpc, glevel, gexp/g2n, medkits, timeslides + their refill
--                  clock, the biases, res_creds, and the rush heal switch
--   Guild.Systems  the implanted systems: on/off, their type or amount, and the
--                  countdowns that arrive live (~4 s apart in the capture)
--   Guild.Info     echelon and its progress, division, class, the rank titles
--   Guild.Progress the long-run record: quest points, kills, slots, the bonus
--                  factors, store credits, storage, subsidy  (PAGED, 2 pages)
--   Guild.Stats    the fight-by-fight numbers: rounds, exp rates, enemy classes
--   Guild.Config   the automation settings: autoguild order, pheal, hms, dna
--   Guild.Powers   how the 94 powers split across divisions and classes
--
-- As with the Cyborg, a Gentech's Guild.* packages share only their NAMES with a
-- Viking's or a Cyborg's, so this plugin reads the assembled snapshots directly.

local P = "plugin." .. scrye.id .. "."

-- ---------------------------------------------------------------- helpers
local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end
local function col(c, s) return "@{" .. c .. "}" .. esc(s) .. "@{}" end
local function S(x) return x == nil and "" or tostring(x) end
local function N(x) return tonumber(x) or 0 end
local function has(t, k) return t[k] ~= nil end

local function padesc(s, n)
  s = tostring(s or "")
  return esc(s .. string.rep(" ", math.max(0, n - #s)))
end

local function comma(n)
  local s = tostring(math.floor(N(n)))
  while true do
    local a, b = s:gsub("^(%-?%d+)(%d%d%d)", "%1,%2")
    s = a; if b == 0 then break end
  end
  return s
end

local function fmt_span(secs)
  secs = N(secs)
  if secs >= 86400 then return string.format("%dd %dh", math.floor(secs / 86400), math.floor((secs % 86400) / 3600)) end
  if secs >= 3600  then return string.format("%dh %dm", math.floor(secs / 3600), math.floor((secs % 3600) / 60)) end
  if secs >= 60    then return string.format("%dm %02ds", math.floor(secs / 60), secs % 60) end
  return secs .. "s"
end

local function pctcol(v, good_high)
  v = N(v)
  if good_high then
    if v >= 75 then return "success" end
    if v >= 40 then return "warning" end
    return "error"
  end
  if v >= 75 then return "error" end
  if v >= 40 then return "warning" end
  return "success"
end

local function ratio(cur, max, good_high)
  if not max or N(max) <= 0 then return esc(comma(cur)) end
  local pct = math.floor(N(cur) * 100 / N(max))
  return esc(comma(cur) .. "/" .. comma(max) .. "  ") .. col(pctcol(pct, good_high ~= false), pct .. "%")
end

local function onoff(v)
  return N(v) > 0 and col("success", "on") or col("dim", "off")
end

local function two_col(items, width, add)
  for i = 1, #items, 2 do
    add(padesc(items[i], width) .. (items[i + 1] and esc(items[i + 1]) or ""))
  end
end

-- ------------------------------------------------------------- snapshots
local ST, SYS, INFO, PROG, STATS, CFG, POWERS = {}, {}, {}, {}, {}, {}, {}
local function T(t, k) return type(t[k]) == "table" and t[k] or {} end

local dirty = {}
local flush_pending = false
local flush

local function schedule_flush()
  if flush_pending then return end
  flush_pending = true
  scrye.after(1, function() flush() end)
end

local function mkadd(L)
  return function(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
end

-- Fields the capture shows but does not explain. Printed under the feed's own
-- name so a Gentech player can tell us what they are, rather than under a label
-- we made up. Each entry: { feed key, which snapshot, how to render }.
local UNKNOWNS = {
  { "dgexp",      "ST", "count" }, { "illuminated", "ST", "flag" },
}

-- ------------------------------------------------------------- Status tab
local function build_status()
  local L = {}
  local add = mkadd(L)
  if not has(ST, "pu") and not has(ST, "glevel") then
    add("waiting for Guild.State...")
    scrye.setState(P .. "status", table.concat(L, "\n"))
    return
  end

  add("-- Pools --")
  if has(ST, "pu") then add("PU           " .. ratio(ST.pu, ST.pu_max, true)) end
  if has(ST, "pu_store") then add("PU store     " .. ratio(ST.pu_store, ST.pu_store_max, true)) end
  if has(ST, "cpc") then add("CPC          " .. ratio(ST.cpc, ST.cpc_max, true)) end

  add("")
  add("-- Supplies --")
  if has(ST, "medkits") then add("Medkits      " .. ratio(ST.medkits, ST.medkits_max, true)) end
  if has(ST, "timeslides") then
    add("Timeslides   " .. ratio(ST.timeslides, ST.timeslides_max, true)
      .. (has(ST, "com_timeslides") and col("dim", esc("   com " .. comma(ST.com_timeslides))) or ""))
    -- the refill clock: at 100% the server hands out a fresh set
    if has(ST, "reset_pct") then
      add("  refill     " .. col(pctcol(ST.reset_pct, true), N(ST.reset_pct) .. "%")
        .. col("dim", "   new timeslides at 100%"))
    end
  end
  if has(SYS, "capsules") then
    add("Capsules     " .. esc(comma(SYS.capsules))
      .. (N(SYS.capsules_resupply) > 0 and col("dim", "   resupply on") or ""))
  end

  add("")
  add("-- Standing --")
  if has(ST, "glevel") then
    add("Guild level  " .. esc(S(ST.glevel))
      .. (N(ST.glevel) >= 50 and col("dim", "   capped - echelons from here") or ""))
  end
  -- gexp counts up, g2n counts what is still owed, so the two of them name the
  -- threshold the server never sends on its own. In the capture they summed to
  -- a round 1,000,000 and gexp/threshold floored to exactly the g2n_pct the
  -- feed sent, so the feed's own percent is printed beside it as a live check.
  -- gexp counts up and g2n counts what is still owed, so the two of them name a
  -- threshold the server never sends outright.
  if has(ST, "gexp") and has(ST, "g2n") then
    local goal = N(ST.gexp) + N(ST.g2n)
    add("Guild exp    " .. ratio(ST.gexp, goal, true)
      .. (has(ST, "g2n_pct") and col("dim", esc("   feed says " .. N(ST.g2n_pct) .. "%")) or ""))
    add("  to next    " .. col(N(ST.g2n) > 0 and "text" or "success",
      esc(N(ST.g2n) > 0 and (comma(ST.g2n) .. " gexp") or "ready")))
  elseif has(ST, "gexp") then
    add("Guild exp    " .. esc(comma(ST.gexp)))
  end
  if has(INFO, "echelon") then
    add("Echelon      " .. esc(S(INFO.echelon) .. "  " .. S(INFO.echelon_title))
      .. (S(INFO.echelon_insignia) ~= "" and col("dim", esc("  " .. S(INFO.echelon_insignia))) or ""))
    -- echelon_gexp / echelon_required is NOT progress inside this echelon: it is
    -- echelons HELD against echelons NEEDED for the next Order. Same arithmetic,
    -- a different thing being counted, so it gets its own label.
    if has(INFO, "echelon_gexp") then
      add("Echelons     " .. ratio(INFO.echelon_gexp, INFO.echelon_required, true)
        .. (has(INFO, "echelon_pct") and col("dim", esc("   feed says " .. N(INFO.echelon_pct) .. "%")) or ""))
      local short = N(INFO.echelon_required) - N(INFO.echelon_gexp)
      add("  to Order   " .. col(short > 0 and "text" or "success",
        esc(short > 0 and (comma(short) .. " more") or "ready")))
    end
  end
  if has(ST, "res_creds") then add("Res credits  " .. esc(comma(ST.res_creds))) end
  -- the split routes earned gexp between the two destinations named just above -
  -- research credits and echelons. 100% sends all of it to res_creds, 0% sends
  -- all of it to echelons, so the remainder is computed rather than echoed. It
  -- is set in game and can be changed at will, so it is a live control, not one
  -- of the passive multipliers it used to be filed under on the Progress tab.
  if has(PROG, "gexp_split") then
    local v = N(PROG.gexp_split)
    add("Gexp split   " .. esc(v .. "% to research credits")
      .. col("dim", esc("   " .. (100 - v) .. "% to echelons")))
  end

  add("")
  add("-- Combat bias --")
  if has(ST, "atk_bias") then
    add("Attack       " .. esc(S(ST.atk_bias)) .. "        Defence  " .. esc(S(ST.def_bias))
      .. (N(ST.bias_lock) > 0 and col("dim", "   locked") or ""))
  end
  if has(ST, "tech_eff_pct") then
    add("Efficiency   " .. col(pctcol(ST.tech_eff_pct, true), "tech " .. N(ST.tech_eff_pct) .. "%")
      .. "  " .. col(pctcol(ST.gen_eff_pct, true), "gen " .. N(ST.gen_eff_pct) .. "%"))
  end

  -- the honest tail: fields the feed sends and this plugin cannot name
  local raw = {}
  for _, u in ipairs(UNKNOWNS) do
    local src = (u[2] == "ST" and ST) or (u[2] == "PROG" and PROG) or {}
    if has(src, u[1]) then
      local v = src[u[1]]
      if u[3] == "flag" then v = (N(v) > 0 and "yes" or "no")
      elseif u[3] == "pct" then v = N(v) .. "%"
      else v = comma(v) end
      raw[#raw + 1] = string.format("%-14s %s", u[1], v)
    end
  end
  if #raw > 0 then
    add("")
    add("-- Not yet understood --")
    add(col("dim", "the feed sends these and this capture does not say what they mean;"))
    add(col("dim", "shown under the server's own field names, raw"))
    two_col(raw, 30, add)
  end

  scrye.setState(P .. "status", table.concat(L, "\n"))
end

-- ------------------------------------------------------------ Systems tab
-- One row per system: is it on, what type or amount is it set to, and how long
-- until it needs attention. The countdowns arrive live (about every 4 s in the
-- capture), so nothing is ticked locally - a number here is one the server sent.
local SYSROWS = {
  -- label,        on-key,          secs-key,        secs-from,  extra-key,      extra-label
  { "HyperGen",    "hypergen",      "hypergen_secs",  "SYS" },
  { "Stabilize",   "stabilize_on",  "stabilize_secs", "SYS",     "stabilize_artifact", "artifact" },
  { "Tactical",    "tactical_on",   "tactical_secs",  "SYS",     "tactical_amount",    nil },
  { "eDNA",        "edna_on",       "edna_secs",      "ST" },
  { "Timescan",    "timescan_on",   nil,              nil,       "timescan_audio",     "audio" },
  { "Life support","life_support",  nil,              nil },
  { "Autorepair",  "autorepair",    nil,              nil },
  -- rush heals, and it switches on and off like the rest of these, but the
  -- server keeps its flag on Guild.State instead of Guild.Systems. Field 7
  -- says where to read the switch from; everything else defaults to SYS.
  { "Rush heal",   "rush",          nil,              nil,       nil, nil,        "ST" },
}

local function build_systems()
  local L = {}
  local add = mkadd(L)
  if not has(SYS, "stabilize_on") and not has(SYS, "ddb_type") then
    add("waiting for Guild.Systems...")
    scrye.setState(P .. "systems", table.concat(L, "\n"))
    return
  end

  add("-- Systems --")
  add(col("dim", string.format("%-13s %-5s %-12s %s", "", "state", "setting", "time left")))
  for _, r in ipairs(SYSROWS) do
    local label, onkey, secskey, secsfrom, extrakey, extralabel = r[1], r[2], r[3], r[4], r[5], r[6]
    local src = (secsfrom == "ST") and ST or SYS
    local osrc = (r[7] == "ST") and ST or SYS
    if has(osrc, onkey) or (secskey and has(src, secskey)) then
      local setting = ""
      if extrakey then
        local v = SYS[extrakey]
        if v ~= nil then
          if extralabel then setting = (N(v) > 0 and extralabel or "")
          else setting = S(v) end
        end
      end
      local left = ""
      if secskey and has(src, secskey) then
        local secs = N(src[secskey])
        -- a countdown at zero is not "0s left", it is ready or lapsed - say which
        -- rather than printing a zero that reads like a broken clock
        left = secs > 0 and col(secs < 60 and "warning" or "text", fmt_span(secs))
                         or col("dim", "--")
      end
      add(padesc(label, 13) .. " " .. padesc("", 0)
        .. (has(osrc, onkey) and onoff(osrc[onkey]) or col("dim", "?"))
        .. string.rep(" ", math.max(1, 6 - #(N(osrc[onkey]) > 0 and "on" or "off")))
        .. padesc(setting, 12) .. " " .. left)
    end
  end

  -- efield is a LEVEL, not a switch: it read 2 in the capture, with its own
  -- minutes counter on Guild.State. Shown as what it is rather than as on/off.
  if has(SYS, "efield") then
    add("")
    add("-- Energy field --")
    add("Level        " .. esc(S(SYS.efield))
      .. (has(ST, "efield_mins") and esc("   " .. N(ST.efield_mins) .. " min left") or ""))
  end

  add("")
  add("-- Loadout --")
  local kit = {}
  if has(SYS, "ddb_type") then
    kit[#kit + 1] = "DDB " .. S(SYS.ddb_type) .. (N(SYS.ddb_adaptive) > 0 and " (adaptive)" or "")
  end
  if has(SYS, "tacshield_type") then
    kit[#kit + 1] = "Shield " .. S(SYS.tacshield_type) .. (N(SYS.tacshield_adaptive) > 0 and " (adaptive)" or "")
  end
  if has(SYS, "synthorg_type") then
    kit[#kit + 1] = "Synthorg " .. S(SYS.synthorg_type) .. (N(SYS.synthorg_on) > 0 and "" or " (off)")
  end
  if #kit == 0 then add(col("dim", "nothing listed")) else
    for _, k in ipairs(kit) do add(esc(k)) end
  end

  scrye.setState(P .. "systems", table.concat(L, "\n"))
end

-- ----------------------------------------------------------- Progress tab
local function build_progress()
  local L = {}
  local add = mkadd(L)
  if not has(PROG, "total_kills") and not has(INFO, "division") then
    add("waiting for Guild.Progress...")
    scrye.setState(P .. "progress", table.concat(L, "\n"))
    return
  end

  add("-- Service --")
  if has(INFO, "division") then
    add("Division     " .. esc(S(INFO.division)) .. "        Class    " .. esc(S(INFO.class_name)))
  end
  if has(INFO, "rank_title") then
    add("Rank         " .. esc(S(INFO.rank_title))
      .. (S(INFO.co_title) ~= "" and esc("   " .. S(INFO.co_title)) or ""))
  end
  if has(INFO, "guild_age_secs") then add("In the guild " .. esc(fmt_span(INFO.guild_age_secs))) end
  if has(PROG, "combat_age_secs") then add("In combat    " .. esc(fmt_span(PROG.combat_age_secs))) end
  if has(PROG, "rested_secs") then add("Rested       " .. esc(fmt_span(PROG.rested_secs))) end
  if has(PROG, "staff_secs") then add("Staff time   " .. esc(fmt_span(PROG.staff_secs))) end

  add("")
  add("-- Record --")
  if has(PROG, "total_kills") then
    add("Kills        " .. esc(comma(PROG.total_kills))
      .. (has(PROG, "num_died") and esc("   died " .. comma(PROG.num_died)) or ""))
  end
  if S(PROG.best_kill) ~= "" then add("Best kill    " .. esc(S(PROG.best_kill))) end
  if has(PROG, "exp_earned") then add("Exp earned   " .. esc(comma(PROG.exp_earned))) end
  if has(PROG, "phase_rank") then
    add("Phase rank   " .. esc(comma(PROG.phase_rank))
      .. col("dim", "   how far the experiments are phased"))
  end
  if has(PROG, "quest_points") then
    add("Quest points " .. esc(comma(PROG.quest_points) .. " of " .. comma(PROG.quest_points_total) .. " total"))
  end
  if has(PROG, "slots_total") then
    add("Power slots  " .. ratio(PROG.slots_filled, PROG.slots_total, true)
      .. (N(PROG.slots_free) > 0 and col("success", esc("   " .. comma(PROG.slots_free) .. " free"))
                                  or col("dim", "   none free")))
  end

  add("")
  add("-- Bonus factors --")
  local f = {}
  local FACTORS = {
    { "class_bonus_pct", "class" }, { "division_bonus_pct", "division" },
    { "qp_factor_pct", "quest points" }, { "guild_age_factor_pct", "guild age" },
    { "explorer_factor_pct", "explorer" },
  }
  for _, x in ipairs(FACTORS) do
    if has(PROG, x[1]) then f[#f + 1] = string.format("%-14s %d%%", x[2], N(PROG[x[1]])) end
  end
  if #f > 0 then two_col(f, 26, add) else add(col("dim", "none listed")) end

  add("")
  add("-- Quartermaster --")
  if has(PROG, "store_credits") then
    add("Store credit " .. ratio(PROG.store_credits, PROG.store_credits_max, true))
  end
  if has(PROG, "utilities") then
    add("Utilities    " .. esc(comma(PROG.utilities))
      .. (has(PROG, "utilities_free") and esc("   " .. comma(PROG.utilities_free) .. " free") or ""))
  end
  if has(PROG, "subsidy") then add("Subsidy      " .. esc(comma(PROG.subsidy))) end
  if has(PROG, "donated") then add("Donated      " .. esc(comma(PROG.donated))) end
  if has(PROG, "storage_in") then
    add("Storage      " .. esc("in " .. comma(PROG.storage_in) .. "   out " .. comma(PROG.storage_out)))
  end
  if has(PROG, "pu_to_cpc") then
    add("PU -> CPC    " .. esc(comma(PROG.pu_to_cpc))
      .. (has(PROG, "pu_to_cpc_login") and esc("   this login " .. comma(PROG.pu_to_cpc_login)) or ""))
  end

  -- the powers split, when Guild.Powers has spoken
  if has(POWERS, "count") then
    add("")
    add("-- Powers (" .. N(POWERS.count) .. ") --")
    local rows = {}
    for _, x in ipairs({ { "div_offense", "offense" }, { "div_defense", "defence" },
                         { "class_science", "science" }, { "class_medical", "medical" },
                         { "class_engineering", "engineering" } }) do
      if has(POWERS, x[1]) then
        rows[#rows + 1] = string.format("%-12s %3d  %d%%", x[2], N(POWERS[x[1]]), N(POWERS[x[1] .. "_pct"]))
      end
    end
    two_col(rows, 26, add)
  end

  scrye.setState(P .. "progress", table.concat(L, "\n"))
end

-- -------------------------------------------------------------- Stats tab
local function build_stats()
  local L = {}
  local add = mkadd(L)
  if not has(STATS, "rounds_total") and not has(STATS, "cpc_used") then
    add("waiting for Guild.Stats...")
    scrye.setState(P .. "stats", table.concat(L, "\n"))
    return
  end

  add("-- This fight --")
  if has(STATS, "rounds_fight") then add("Rounds       " .. esc(comma(STATS.rounds_fight))) end
  if S(STATS.last_enemy) ~= "" then
    add("Last enemy   " .. esc(S(STATS.last_enemy))
      .. col("dim", esc("   class " .. comma(STATS.last_enemy_class))))
  end
  if has(STATS, "rounds_enemy") then add("  vs enemy   " .. esc(comma(STATS.rounds_enemy))) end

  add("")
  add("-- Rates --")
  local rates = {}
  for _, x in ipairs({ { "exp_per_kill", "exp/kill" }, { "exp_min", "exp/min" },
                       { "gexp_min", "gexp/min" }, { "rc_min", "rc/min" },
                       { "exp_cs", "exp cs" }, { "gexp_cs", "gexp cs" }, { "rc_cs", "rc cs" } }) do
    if has(STATS, x[1]) then rates[#rates + 1] = string.format("%-10s %s", x[2], comma(STATS[x[1]])) end
  end
  if #rates > 0 then two_col(rates, 24, add) else add(col("dim", "nothing measured yet")) end
  -- "cs" is the feed's own suffix and the capture never says what it stands for;
  -- the rows are labelled with it rather than with a guess.

  add("")
  add("-- Totals --")
  if has(STATS, "rounds_total") then add("Rounds       " .. esc(comma(STATS.rounds_total))) end
  if has(STATS, "cpc_used") then add("CPC used     " .. esc(comma(STATS.cpc_used))) end
  if has(STATS, "enemy_deaths") then add("Enemy deaths " .. esc(comma(STATS.enemy_deaths))) end
  if S(STATS.best_enemy) ~= "" then
    add("Best enemy   " .. esc(S(STATS.best_enemy)) .. col("dim", esc("   class " .. comma(STATS.best_enemy_class))))
  end
  if S(STATS.worst_enemy) ~= "" then
    add("Worst enemy  " .. esc(S(STATS.worst_enemy)) .. col("dim", esc("   class " .. comma(STATS.worst_enemy_class))))
  end
  if N(STATS.stating) > 0 then
    add("")
    add(col("info", "stat run in progress" .. (has(STATS, "stat_secs") and (" - " .. fmt_span(STATS.stat_secs)) or "")))
  end

  scrye.setState(P .. "stats", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Config tab
local function build_config()
  local L = {}
  local add = mkadd(L)
  if not has(CFG, "autoguild") and not has(CFG, "pheal") then
    add("waiting for Guild.Config...")
    scrye.setState(P .. "config", table.concat(L, "\n"))
    return
  end

  local ag = T(CFG, "autoguild")
  add("-- Autoguild order --")
  if #ag == 0 then add(col("dim", "nothing queued"))
  else
    local parts = {}
    for i, s in ipairs(ag) do parts[#parts + 1] = i .. ". " .. S(s) end
    two_col(parts, 22, add)
  end

  add("")
  add("-- Panic heal --")
  add("Enabled      " .. onoff(CFG.pheal_on))
  if S(CFG.pheal) ~= "" then add("Uses         " .. esc(S(CFG.pheal))) end
  if has(CFG, "pheal_hp") then
    add("Trigger      " .. esc("below " .. comma(CFG.pheal_hp) .. " hp")
      .. (N(CFG.pheal_percent) > 0 and esc(" or " .. N(CFG.pheal_percent) .. "%") or ""))
  end

  add("")
  add("-- HMS --")
  add("Enabled      " .. onoff(CFG.hms_on)
    .. (N(CFG.hms_combat_only) > 0 and col("dim", "   in combat only") or ""))
  if S(CFG.hms_cmd) ~= "" then
    add("Command      " .. esc(S(CFG.hms_cmd))
      .. (has(CFG, "hms_val") and esc("   at " .. comma(CFG.hms_val)) or "")
      .. (N(CFG.hms_percent) > 0 and esc(" or " .. N(CFG.hms_percent) .. "%") or ""))
  end

  add("")
  add("-- DNA --")
  add("Stored       " .. ratio(CFG.dna, CFG.dna_max, true)
    .. (N(CFG.dna_ready) > 0 and col("success", "   ready") or col("dim", "   not ready")))
  if S(CFG.dna_source) ~= "" then add("Source       " .. esc(S(CFG.dna_source)))
  else add("Source       " .. col("dim", "none set")) end

  add("")
  add("-- Gen timer --")
  add("Enabled      " .. onoff(CFG.gentimer_on)
    .. (N(CFG.gentimer_audio) > 0 and col("dim", "   audio") or "")
    .. (N(CFG.gentimer_autoreset) > 0 and col("dim", "   auto-reset") or ""))
  if N(CFG.gentimer_secs) > 0 then add("Set to       " .. esc(fmt_span(CFG.gentimer_secs))) end

  if has(CFG, "autocombat_on") then
    add("")
    add("-- Autocombat --")
    add("Enabled      " .. onoff(CFG.autocombat_on)
      .. (S(CFG.autocombat) ~= "" and esc("   " .. S(CFG.autocombat)) or col("dim", "   nothing set")))
  end

  scrye.setState(P .. "config", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- one-liner
local function summary()
  if not has(ST, "pu") then return "waiting for the gentech feed" end
  local bits = { "PU " .. comma(ST.pu) .. "/" .. comma(ST.pu_max) }
  if has(ST, "cpc") then bits[#bits + 1] = "CPC " .. comma(ST.cpc) .. "/" .. comma(ST.cpc_max) end
  if has(ST, "medkits") then bits[#bits + 1] = "Medkits " .. comma(ST.medkits) end
  -- the soonest countdown is the thing you actually want on one line
  local soon, soon_name
  for _, r in ipairs(SYSROWS) do
    local src = (r[4] == "ST") and ST or SYS
    if r[3] and has(src, r[3]) and N(src[r[3]]) > 0 then
      if not soon or N(src[r[3]]) < soon then soon, soon_name = N(src[r[3]]), r[1] end
    end
  end
  if soon then bits[#bits + 1] = soon_name .. " " .. fmt_span(soon) end
  return table.concat(bits, "   ")
end

local BUILDERS = {
  status = build_status, systems = build_systems, progress = build_progress,
  stats = build_stats, config = build_config,
}

flush = function()
  flush_pending = false
  for sec in pairs(dirty) do
    local b = BUILDERS[sec]
    if b then pcall(b) end
  end
  scrye.setState(P .. "summary", summary())
  dirty = {}
end

-- ---------- Guild.* page assembler (shared snippet; docs/Plan-Viking-GMCP.md 3) ----------
local function gasm(pkg, on_snap)
  local snap, burst, bfull, expect, last_page = {}, nil, false, nil, 0
  local paged_keys = {}
  local function is_list(v) return type(v) == "table" and v[1] ~= nil end
  scrye.onGmcp(pkg, function(json)
    local ok, t = pcall(scrye.json.decode, json)
    if not ok or type(t) ~= "table" then return end
    local page, pages = tonumber(t.page), tonumber(t.pages)
    if not pages then
      for k, v in pairs(t) do if k ~= "guild" and k ~= "full" then snap[k] = v end end
      pcall(on_snap, snap)
      return
    end
    if not burst or pages ~= expect or (page or 0) <= last_page then
      burst, bfull, expect = {}, false, pages
    end
    last_page = page or 0
    if tonumber(t.full) == 1 then bfull = true end
    for k, v in pairs(t) do
      if k ~= "page" and k ~= "pages" and k ~= "full" and k ~= "guild" then
        if is_list(v) and is_list(burst[k]) then
          for _, e in ipairs(v) do burst[k][#burst[k] + 1] = e end
        else
          burst[k] = v
        end
      end
    end
    if page == pages then
      if bfull then
        local keep = {}
        for k, v in pairs(snap) do if not paged_keys[k] then keep[k] = v end end
        snap = keep
      end
      for k, v in pairs(burst) do snap[k] = v; paged_keys[k] = true end
      burst, bfull, expect, last_page = nil, false, nil, 0
      pcall(on_snap, snap)
    end
  end)
end

gasm("Guild.State", function(snap)
  ST = snap
  dirty.status = true; dirty.systems = true   -- edna_secs and efield_mins show there
  schedule_flush()
end)

gasm("Guild.Systems", function(snap)
  SYS = snap
  dirty.systems = true; dirty.status = true   -- capsules show on Status
  schedule_flush()
end)

gasm("Guild.Info", function(snap)
  INFO = snap
  dirty.status = true; dirty.progress = true
  schedule_flush()
end)

gasm("Guild.Progress", function(snap)
  PROG = snap
  dirty.progress = true; dirty.status = true  -- phase_rank sits in the raw tail
  schedule_flush()
end)

gasm("Guild.Stats", function(snap)
  STATS = snap
  dirty.stats = true
  schedule_flush()
end)

gasm("Guild.Config", function(snap)
  CFG = snap
  dirty.config = true
  schedule_flush()
end)

gasm("Guild.Powers", function(snap)
  POWERS = snap
  dirty.progress = true
  schedule_flush()
end)

-- --------------------------------------------------------------- aliases
-- gt: the status page in the output window, for a glance without the panel
scrye.addAlias{ pattern = "^gt$", regex = true, run = function()
  dirty.status = true; flush()
  for line in (scrye.getState(P .. "status") or ""):gmatch("[^\n]+") do
    scrye.print("@{#7ED957,bold}[gentech]@{} " .. line)
  end
end }

-- gtsys: the systems page, which is the one with the countdowns on it
scrye.addAlias{ pattern = "^gtsys$", regex = true, run = function()
  dirty.systems = true; flush()
  for line in (scrye.getState(P .. "systems") or ""):gmatch("[^\n]+") do
    scrye.print("@{#7ED957,bold}[gentech]@{} " .. line)
  end
end }

-- --------------------------------------------------------------- panel
scrye.addPanel{
  title = "Gentech",
  width = 430,
  accent = "#7ED957",          -- signature: gene-splice green
  tabs = {
    { title = "Status",   widgets = {
        { type = "value", text = "", bind = P .. "summary", color = "info" },
        { type = "text",  bind = P .. "status" },
    } },
    { title = "Systems",  widgets = { { type = "text", bind = P .. "systems" } } },
    { title = "Progress", widgets = { { type = "text", bind = P .. "progress" } } },
    { title = "Stats",    widgets = { { type = "text", bind = P .. "stats" } } },
    { title = "Config",   widgets = { { type = "text", bind = P .. "config" } } },
  },
}

-- ------------------------------------------------------------------ init
for _, s in ipairs({ "status", "systems", "progress", "stats", "config" }) do dirty[s] = true end
flush()
