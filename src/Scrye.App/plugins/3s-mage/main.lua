-- 3S Mage -- the Mage guild HUD, built on the Guild.* GMCP packages a mage
-- receives. 3Scapes only.
--
-- Built from one capture of Joakim's own mage (Eldran, 3 Sep 2026: 1,578 messages
-- across 28 packages, a fresh login followed by ten minutes of fighting), so most
-- of what the feed sends has a name a mage would use. What the capture does NOT
-- establish is shown under the server's own field name with its raw value, in a
-- "Not yet understood" block on the Status tab, rather than under a label that
-- might be a lie - the same rule the Gentech HUD follows. Those fields are listed
-- at the bottom of this header and in docs/Plan-Improvements.md.
--
-- Packages consumed:
--   Guild.State     the pools and the clocks: umbra and concentration against
--                   their maxima, imbues / bridges / rifts, the three reset
--                   percentages, gxp to the next level. Snapshot/delta: one
--                   full:1 at login, then a delta per round.
--   Guild.Progress  the long-run record: gxp and gxp/hour, gxp to spend, time in
--                   the guild and in combat, coins and items donated, the bonus
--                   labels the server writes in prose. PAGED (2) then deltas.
--   Guild.Info      school and title, guild status, when you joined.
--   Guild.Assets    the familiar (name, type, level, hp) and the staff.
--   Guild.Actives   the effects currently up, as the server names them.
--   Guild.Config    the contingencies (what is cast when something drops) and
--                   the perform setting. PAGED (5).
--   Guild.Focus     the spells you have focused on: standing, points, and the
--                   cost reduction each has earned. PAGED (2 or 4).
--   Guild.Skills    the skill tree: current/maximum, next cost, affordable, and
--                   the gxp available to spend. PAGED (5).
--   Guild.Spells    the directory of spell packages, and the seven Guild.Spells*
--                   packages it names - 122 spells in the capture, each with its
--                   level, base and current cost, category and focus standing.
--                   The Spells tab shows one category at a time.
--
-- Named by Joakim (3 Sep), so they have rows: gem_pct is what is LEFT of the
-- gem before it breaks (rises when a new one is bought); imbue/bridge_reset_pct
-- count toward the next imbues/bridges being handed out; magical_reset_pct
-- counts toward the school spells resetting; level_pct is progress toward the
-- next guild level; clarity_pct climbs to 100 and stops there; channel_minutes
-- is time spent channelling in the guild; sp_burnt is SP spent since joining;
-- guild_quest_complete says whether the guild quest is solved.
-- Still not understood (raw on the Status tab): school_casts.

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

local function two_col(items, width, add)
  for i = 1, #items, 2 do
    add(padesc(items[i], width) .. (items[i + 1] and esc(items[i + 1]) or ""))
  end
end

-- ------------------------------------------------------------- snapshots
local ST, PROG, INFO, ASSETS, ACT, CFG, FOCUS, SKILLS, DIR = {}, {}, {}, {}, {}, {}, {}, {}, {}
local BOOK = {}             -- package name -> that package's spell list (merged below)
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
-- name so a mage can say what they are. Each entry: { feed key, snapshot, how }.
local UNKNOWNS = {
  { "school_casts", "ST", "count" },
}

-- ------------------------------------------------------------- Status tab
local function build_status()
  local L = {}
  local add = mkadd(L)
  if not has(ST, "umbra") and not has(ST, "imbues") then
    add("waiting for Guild.State...")
    scrye.setState(P .. "status", table.concat(L, "\n"))
    return
  end

  add("-- Pools --")
  if has(ST, "umbra") then add("Umbra          " .. ratio(ST.umbra, ST.umbra_max, true)) end
  if has(ST, "concentration") then add("Concentration  " .. ratio(ST.concentration, ST.concentration_max, true)) end

  add("")
  add("-- Workings --")
  local w = {}
  if has(ST, "imbues") then w[#w + 1] = "imbues   " .. comma(ST.imbues) end
  if has(ST, "bridges") then w[#w + 1] = "bridges  " .. comma(ST.bridges) end
  if has(ST, "rifts") then w[#w + 1] = "rifts    " .. comma(ST.rifts) end
  if #w > 0 then two_col(w, 16, add) end
  -- the clocks: at 100% the next imbues / bridges are handed out, and the
  -- school spells reset
  if has(ST, "imbue_reset_pct") or has(ST, "bridge_reset_pct") then
    add("Next batch     "
      .. (has(ST, "imbue_reset_pct") and ("imbues " .. col(pctcol(ST.imbue_reset_pct, true), N(ST.imbue_reset_pct) .. "%") .. "   ") or "")
      .. (has(ST, "bridge_reset_pct") and ("bridges " .. col(pctcol(ST.bridge_reset_pct, true), N(ST.bridge_reset_pct) .. "%")) or "")
      .. col("dim", "   at 100% the next ones arrive"))
  end
  if has(ST, "magical_reset_pct") then
    add("School reset   " .. col(pctcol(ST.magical_reset_pct, true), N(ST.magical_reset_pct) .. "%")
      .. col("dim", "   at 100% the school spells reset"))
  end
  -- the gem: what is LEFT before it breaks, so low is the warning
  if has(ST, "gem_pct") then
    add("Gem            " .. col(pctcol(ST.gem_pct, true), N(ST.gem_pct) .. "% left")
      .. (N(ST.gem_pct) < 25 and col("error", "   about to break") or col("dim", "   before it breaks")))
  end

  add("")
  add("-- Standing --")
  if has(PROG, "guild_level") then
    add("Guild level    " .. esc(S(PROG.guild_level))
      .. (has(ST, "level_pct") and col("dim", esc("   " .. N(ST.level_pct) .. "% of the way to the next")) or ""))
  elseif has(ST, "level_pct") then
    add("Guild level    " .. col("dim", esc(N(ST.level_pct) .. "% of the way to the next")))
  end
  if has(ST, "gxp_to_next") then
    add("To next level  " .. esc(comma(ST.gxp_to_next) .. " gxp")
      .. (has(ST, "gxp_last_round") and N(ST.gxp_last_round) < 100000
          and col("dim", esc("   last round +" .. comma(ST.gxp_last_round))) or ""))
  end
  if has(PROG, "gxp_to_spend") then add("Gxp to spend   " .. esc(comma(PROG.gxp_to_spend))) end
  if has(INFO, "school") then
    add("School         " .. esc(S(INFO.school))
      .. (S(INFO.school_short) ~= "" and col("dim", esc("  (" .. S(INFO.school_short) .. ")")) or ""))
  end
  if has(INFO, "title") then add("Title          " .. esc(S(INFO.title))) end
  if has(INFO, "status") then add("Status         " .. esc(S(INFO.status))) end

  -- the familiar and the staff
  local fam = T(ASSETS, "familiar")
  local staff = T(ASSETS, "staff")
  if has(fam, "name") or has(staff, "active") then
    add("")
    add("-- Companions --")
    if has(fam, "name") then
      local hp = N(fam.hp_pct)
      add("Familiar       " .. esc(S(fam.name) .. " the " .. S(fam.type) .. ", level " .. S(fam.level))
        .. "  " .. (N(fam.active) > 0 and col(pctcol(hp, true), hp .. "% hp") or col("dim", "not out"))
        .. (S(fam.status) ~= "" and col("dim", esc("  " .. S(fam.status))) or ""))
    end
    if has(staff, "active") then
      if N(staff.active) > 0 then
        add("Staff          " .. col(pctcol(staff.charge_pct, true), "charge " .. N(staff.charge_pct) .. "%")
          .. "  " .. col(pctcol(staff.shelter_pct, true), "shelter " .. N(staff.shelter_pct) .. "%")
          .. (N(staff.attuned) > 0 and col("dim", "  attuned") or ""))
      else
        add("Staff          " .. col("dim", "none active"))
      end
    end
    if has(ASSETS, "summons_total") then
      add("Summons        " .. (N(ASSETS.summons_total) > 0 and esc(comma(ASSETS.summons_total)) or col("dim", "none")))
    end
  end

  -- what is up right now, in the server's own names
  local eff = T(ACT, "effects")
  add("")
  add("-- Active effects --")
  if #eff == 0 then add(col("dim", "nothing up"))
  else
    local names = {}
    for _, e in ipairs(eff) do names[#names + 1] = S(e) end
    add(col("success", table.concat(names, "  ")))
  end

  -- the honest tail
  local raw = {}
  for _, u in ipairs(UNKNOWNS) do
    local src = (u[2] == "ST" and ST) or (u[2] == "PROG" and PROG) or {}
    if has(src, u[1]) then
      local v = src[u[1]]
      if u[3] == "flag" then v = (N(v) > 0 and "yes" or "no")
      elseif u[3] == "pct" then v = N(v) .. "%"
      else v = comma(v) end
      raw[#raw + 1] = string.format("%-18s %s", u[1], v)
    end
  end
  if #raw > 0 then
    add("")
    add("-- Not yet understood --")
    add(col("dim", "the feed sends these and the capture does not say what they mean;"))
    add(col("dim", "shown under the server's own field names, raw"))
    two_col(raw, 32, add)
  end

  scrye.setState(P .. "status", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Spells tab
-- One category at a time: 122 spells is too many for a panel, and a mage
-- looking for a cost or a focus standing knows which shelf it is on. The
-- directory (Guild.Spells) says which package holds which category, but the
-- spells carry their own category letter, so the view keys on the letter and
-- the directory is only consulted for the labels.
local CATS = {
  { "O", "offensive" }, { "D", "defensive" }, { "E", "enhancing" },
  { "S", "summoning" }, { "I", "informational" }, { "N", "neutral" }, { "F", "fun" },
}
local CAT_OF = {}
for _, c in ipairs(CATS) do CAT_OF[c[1]] = c[2] end
local shown_cat = scrye.store.get("cat") or "O"

local function spellbook()
  local all, seen = {}, {}
  for _, pkg in ipairs({ "Guild.SpellsOffense", "Guild.SpellsAdvanced", "Guild.SpellsDefense",
                         "Guild.SpellsEnhanceLo", "Guild.SpellsEnhanceHi", "Guild.SpellsSummon",
                         "Guild.SpellsUtility" }) do
    for _, sp in ipairs(T(BOOK[pkg] or {}, "spells")) do
      local key = S(sp.name):lower()
      if key ~= "" and not seen[key] then seen[key] = true ; all[#all + 1] = sp end
    end
  end
  table.sort(all, function(a, b)
    if N(a.level) ~= N(b.level) then return N(a.level) < N(b.level) end
    return S(a.name) < S(b.name)
  end)
  return all
end

local function build_spells()
  local L = {}
  local add = mkadd(L)
  local all = spellbook()
  if #all == 0 then
    add("waiting for the Guild.Spells* packages...")
    scrye.setState(P .. "spells", table.concat(L, "\n"))
    scrye.setState(P .. "spells_head", "")
    return
  end
  local rows, learned, total = {}, 0, 0
  for _, sp in ipairs(all) do
    if S(sp.category) == shown_cat then
      total = total + 1
      if N(sp.learned) > 0 then learned = learned + 1 end
      local cost
      if N(sp.base_cost) > 0 and N(sp.current_cost) < N(sp.base_cost) then
        cost = comma(sp.current_cost) .. col("dim", esc(" (" .. comma(sp.base_cost) .. ")"))
      else
        cost = comma(sp.current_cost)
      end
      local line = string.format("%3d  ", N(sp.level)) .. padesc(S(sp.name), 26) .. " " .. padesc("", 0)
        .. string.rep(" ", math.max(0, 6 - #comma(sp.current_cost))) .. cost
        .. (S(sp.focus_label) ~= "" and col("info", esc("   " .. S(sp.focus_label))) or "")
      if N(sp.learned) == 0 then line = col("dim", S(sp.name) .. "  (not learned)") end
      if N(sp.online) == 0 then line = line .. col("error", "   offline") end
      rows[#rows + 1] = line
    end
  end
  scrye.setState(P .. "spells_head", string.format("%s: %d spell(s), %d learned",
    CAT_OF[shown_cat] or shown_cat, total, learned))
  add(col("dim", "lvl  name                        cost (base)  focus"))
  for _, r in ipairs(rows) do add(r) end
  if #rows == 0 then add(col("dim", "nothing in this category")) end
  scrye.setState(P .. "spells", table.concat(L, "\n"))
end

-- -------------------------------------------------------------- Focus tab
local function build_focus()
  local L = {}
  local add = mkadd(L)
  local list = T(FOCUS, "focuses")
  if #list == 0 and not has(FOCUS, "allowed") then
    add("waiting for Guild.Focus...")
    scrye.setState(P .. "focus", table.concat(L, "\n"))
    return
  end
  if has(FOCUS, "allowed") then
    add("Focuses        " .. esc(S(FOCUS.used or #list) .. " of " .. S(FOCUS.allowed) .. " allowed")
      .. (N(FOCUS.over_limit) > 0 and col("error", "   OVER THE LIMIT") or ""))
    add("")
  end
  local sorted = {}
  for _, f in ipairs(list) do sorted[#sorted + 1] = f end
  table.sort(sorted, function(a, b) return N(a.points) > N(b.points) end)
  add(col("dim", "spell                   standing              points      cost -"))
  for _, f in ipairs(sorted) do
    add(padesc(S(f.spell), 24) .. padesc(S(f.standing), 22)
      .. string.rep(" ", math.max(0, 9 - #comma(f.points))) .. esc(comma(f.points))
      .. "   " .. col(pctcol(f.cost_reduction_pct, true), N(f.cost_reduction_pct) .. "%"))
  end
  if #sorted == 0 then add(col("dim", "no spells focused")) end
  scrye.setState(P .. "focus", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Skills tab
-- The tree as the server sends it: a skill with a parent is drawn under it.
-- Affordable and not at max is the row worth noticing, so it is the one
-- coloured; at max is dim, the rest are plain.
local function build_skills()
  local L = {}
  local add = mkadd(L)
  local list = T(SKILLS, "skills")
  if #list == 0 and not has(SKILLS, "gxp_available") then
    add("waiting for Guild.Skills...")
    scrye.setState(P .. "skills", table.concat(L, "\n"))
    return
  end
  if has(SKILLS, "gxp_available") then
    add("Gxp available  " .. esc(comma(SKILLS.gxp_available)))
    add("")
  end
  add(col("dim", "skill               have/max    next cost"))
  local byparent = {}
  local roots = {}
  for _, s in ipairs(list) do
    local p = S(s.parent)
    if p == "" then roots[#roots + 1] = s
    else byparent[p] = byparent[p] or {} ; table.insert(byparent[p], s) end
  end
  local function row(s, depth)
    local name = string.rep("  ", depth) .. S(s.name)
    local have = comma(s.current) .. "/" .. comma(s.maximum)
    local line = padesc(name, 20) .. padesc(have, 12)
    if N(s.at_max) > 0 then
      line = col("dim", name .. string.rep(" ", math.max(0, 20 - #name)) .. have .. "  at max")
    elseif N(s.affordable) > 0 then
      line = line .. col("success", comma(s.next_cost) .. "  affordable")
    else
      line = line .. esc(comma(s.next_cost))
    end
    add(line)
    for _, c in ipairs(byparent[S(s.name)] or {}) do row(c, depth + 1) end
  end
  for _, s in ipairs(roots) do row(s, 0) end
  -- a child whose parent never arrived still gets a row, rather than vanishing
  local placed = {}
  for _, s in ipairs(roots) do placed[S(s.name)] = true end
  for p, kids in pairs(byparent) do
    if not placed[p] then
      local known = false
      for _, s in ipairs(list) do if S(s.name) == p then known = true end end
      if not known then for _, c in ipairs(kids) do row(c, 0) end end
    end
  end
  scrye.setState(P .. "skills", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Config tab
local function build_config()
  local L = {}
  local add = mkadd(L)
  local cont = T(CFG, "contingencies")
  local perf = T(CFG, "perform")
  if #cont == 0 and not has(perf, "mode") then
    add("waiting for Guild.Config...")
    scrye.setState(P .. "config", table.concat(L, "\n"))
    return
  end

  add("-- Perform --")
  if has(perf, "mode") then
    local mode = S(perf.mode)
    add("Mode           " .. (mode == "inactive" and col("dim", mode) or col("success", mode)))
    if S(perf.action) ~= "" then add("Action         " .. esc(S(perf.action))) end
    if N(perf.sp_threshold) > 0 then add("SP threshold   " .. esc(comma(perf.sp_threshold))) end
  else
    add(col("dim", "not sent yet"))
  end

  add("")
  add("-- Contingencies" .. (has(CFG, "contingency_limit") and (" (" .. #cont .. " of " .. N(CFG.contingency_limit) .. ")") or "") .. " --")
  if #cont == 0 then add(col("dim", "none set"))
  else
    local sorted = {}
    for _, c in ipairs(cont) do sorted[#sorted + 1] = c end
    table.sort(sorted, function(a, b) return N(a.priority) < N(b.priority) end)
    add(col("dim", "pri  when                  trigger          action"))
    for _, c in ipairs(sorted) do
      local when = S(c.when) .. (S(c.direction) ~= "" and (", " .. S(c.direction)) or "")
      if N(c.threshold) > 0 then when = when .. " " .. comma(c.threshold) end
      add(string.format("%3d  ", N(c.priority)) .. padesc(when, 22) .. padesc(S(c.trigger_label), 17) .. esc(S(c.action)))
    end
  end
  scrye.setState(P .. "config", table.concat(L, "\n"))
end

-- ----------------------------------------------------------- Progress tab
local function build_progress()
  local L = {}
  local add = mkadd(L)
  if not has(PROG, "gxp") and not has(PROG, "guild_play_seconds") then
    add("waiting for Guild.Progress...")
    scrye.setState(P .. "progress", table.concat(L, "\n"))
    return
  end

  add("-- Guild experience --")
  if has(PROG, "gxp") then add("Total          " .. esc(comma(PROG.gxp))) end
  if has(PROG, "gxp_per_hour") then add("Per hour       " .. esc(comma(PROG.gxp_per_hour))) end
  if has(PROG, "gxp_to_spend") then add("To spend       " .. esc(comma(PROG.gxp_to_spend))) end

  if has(PROG, "sp_burnt") then add("SP burnt       " .. esc(comma(PROG.sp_burnt)) .. col("dim", "   since joining the guild")) end
  if has(PROG, "clarity_pct") then
    add("Clarity        " .. col(pctcol(PROG.clarity_pct, true), string.format("%.1f%%", N(PROG.clarity_pct)))
      .. (N(PROG.clarity_pct) >= 100 and col("success", "   full") or col("dim", "   climbs to 100 and stops")))
  end
  if has(PROG, "guild_quest_complete") then
    add("Guild quest    " .. (N(PROG.guild_quest_complete) > 0 and col("success", "solved") or col("warning", "not yet solved")))
  end

  add("")
  add("-- Time --")
  if has(PROG, "guild_play_seconds") then add("In the guild   " .. esc(fmt_span(PROG.guild_play_seconds))) end
  if has(PROG, "combat_seconds") then
    add("In combat      " .. esc(fmt_span(PROG.combat_seconds))
      .. (N(PROG.guild_play_seconds) > 0
          and col("dim", esc("   " .. math.floor(N(PROG.combat_seconds) * 100 / N(PROG.guild_play_seconds)) .. "% of it")) or ""))
  end
  if has(PROG, "channel_minutes") then add("Channelling    " .. esc(fmt_span(N(PROG.channel_minutes) * 60))) end
  if has(INFO, "joined_at") and N(INFO.joined_at) > 0 then
    local ok, d = pcall(os.date, "%Y-%m-%d", N(INFO.joined_at))
    if ok then add("Joined         " .. esc(d)) end
  end
  if has(INFO, "school_joined_at") and N(INFO.school_joined_at) > 0 then
    local ok, d = pcall(os.date, "%Y-%m-%d", N(INFO.school_joined_at))
    if ok then add("School since   " .. esc(d)) end
  end

  add("")
  add("-- Donations --")
  if has(PROG, "coins_donated") then add("Coins          " .. esc(comma(PROG.coins_donated))) end
  if has(PROG, "item_net_count") then
    add("Items          " .. esc(comma(PROG.item_net_count) .. " worth " .. comma(PROG.item_total_value))
      .. col("dim", esc("   avg " .. comma(PROG.item_average_value))))
  end
  -- the server writes these bonuses in prose; the prose IS the information
  for _, k in ipairs({ "corpse_donation_label", "explorer_bonus_label", "quest_bonus_label" }) do
    if S(PROG[k]) ~= "" then add(col("dim", S(PROG[k]))) end
  end
  scrye.setState(P .. "progress", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- one-liner
local function summary()
  if not has(ST, "umbra") then return "waiting for the mage feed" end
  local bits = { "Umbra " .. comma(ST.umbra) .. "/" .. comma(ST.umbra_max) }
  if has(ST, "concentration") then bits[#bits + 1] = "Conc " .. comma(ST.concentration) .. "/" .. comma(ST.concentration_max) end
  local w = {}
  if has(ST, "imbues") then w[#w + 1] = comma(ST.imbues) .. " imbue" end
  if has(ST, "bridges") then w[#w + 1] = comma(ST.bridges) .. " bridge" end
  if has(ST, "rifts") then w[#w + 1] = comma(ST.rifts) .. " rift" end
  if #w > 0 then bits[#bits + 1] = table.concat(w, " ") end
  return table.concat(bits, "   ")
end

-- gauges for the panel (and for any plugin that wants a mage's pools)
local function publish_gauges()
  scrye.setState(P .. "umbra", S(ST.umbra))
  scrye.setState(P .. "umbra_max", S(ST.umbra_max))
  scrye.setState(P .. "conc", S(ST.concentration))
  scrye.setState(P .. "conc_max", S(ST.concentration_max))
end

local BUILDERS = {
  status = build_status, spells = build_spells, focus = build_focus,
  skills = build_skills, config = build_config, progress = build_progress,
}

flush = function()
  flush_pending = false
  for sec in pairs(dirty) do
    local b = BUILDERS[sec]
    if b then pcall(b) end
  end
  scrye.setState(P .. "summary", summary())
  publish_gauges()
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
  dirty.status = true
  schedule_flush()
end)

gasm("Guild.Progress", function(snap)
  PROG = snap
  dirty.progress = true; dirty.status = true   -- guild level and gxp to spend sit on Status
  schedule_flush()
end)

gasm("Guild.Info", function(snap)
  INFO = snap
  dirty.status = true; dirty.progress = true
  schedule_flush()
end)

gasm("Guild.Assets", function(snap)
  ASSETS = snap
  dirty.status = true
  schedule_flush()
end)

gasm("Guild.Actives", function(snap)
  ACT = snap
  dirty.status = true
  schedule_flush()
end)

gasm("Guild.Config", function(snap)
  CFG = snap
  dirty.config = true
  schedule_flush()
end)

gasm("Guild.Focus", function(snap)
  FOCUS = snap
  dirty.focus = true
  schedule_flush()
end)

gasm("Guild.Skills", function(snap)
  SKILLS = snap
  dirty.skills = true
  schedule_flush()
end)

gasm("Guild.Spells", function(snap)
  DIR = snap
  dirty.spells = true
  schedule_flush()
end)

for _, pkg in ipairs({ "Guild.SpellsOffense", "Guild.SpellsAdvanced", "Guild.SpellsDefense",
                       "Guild.SpellsEnhanceLo", "Guild.SpellsEnhanceHi", "Guild.SpellsSummon",
                       "Guild.SpellsUtility" }) do
  gasm(pkg, function(snap)
    BOOK[pkg] = snap
    dirty.spells = true
    schedule_flush()
  end)
end

-- --------------------------------------------------------------- aliases
local function print_section(sec)
  dirty[sec] = true; flush()
  if sec == "spells" then scrye.print("@{#9B7BFF,bold}[mage]@{} " .. esc(scrye.getState(P .. "spells_head") or "")) end
  for line in (scrye.getState(P .. sec) or ""):gmatch("[^\n]+") do
    scrye.print("@{#9B7BFF,bold}[mage]@{} " .. line)
  end
end

local function show_cat(letter)
  shown_cat = letter
  scrye.store.set("cat", letter)
  dirty.spells = true
  flush()
end

-- mage                 the Status page in the output window
-- mage spells [cat]    the spell list; a category name or letter picks the shelf
-- mage focus|skills|config|progress
scrye.addAlias{ pattern = "^mage(?:\\s+(.*))?$", regex = true, run = function(args)
  args = tostring(args or ""):gsub("^%s+", ""):gsub("%s+$", ""):lower()
  local verb, rest = args:match("^(%S+)%s*(.*)$")
  verb = verb or ""
  if verb == "" or verb == "status" then print_section("status")
  elseif verb == "spells" then
    if rest ~= "" then
      local pick
      for _, c in ipairs(CATS) do
        if rest == c[1]:lower() or c[2]:find(rest, 1, true) == 1 then pick = c[1] break end
      end
      if not pick then
        scrye.print("[mage] categories: " .. (function()
          local t = {} ; for _, c in ipairs(CATS) do t[#t + 1] = c[2] end ; return table.concat(t, ", ") end)())
        return
      end
      show_cat(pick)
    end
    print_section("spells")
  elseif BUILDERS[verb] then print_section(verb)
  else
    scrye.print("[mage] mage | mage spells [offensive|defensive|enhancing|summoning|informational|neutral|fun] | mage focus | mage skills | mage config | mage progress")
  end
end }

-- --------------------------------------------------------------- panel
do
  local catbuttons = {}
  for _, c in ipairs(CATS) do
    local letter = c[1]
    catbuttons[#catbuttons + 1] = { text = c[2]:sub(1, 1):upper() .. c[2]:sub(2, 5), action = function() show_cat(letter) end }
  end
  scrye.addPanel{
    title = "Mage",
    width = 460,
    accent = "#9B7BFF",          -- signature: umbral violet
    tabs = {
      { title = "Status",   widgets = {
          { type = "value", text = "", bind = P .. "summary", color = "info" },
          { type = "gauge", text = "Umbra", value = P .. "umbra", max = P .. "umbra_max" },
          { type = "gauge", text = "Conc",  value = P .. "conc",  max = P .. "conc_max" },
          { type = "text",  bind = P .. "status" },
      } },
      { title = "Spells",   widgets = {
          { type = "buttonrow", buttons = catbuttons },
          { type = "label", bind = P .. "spells_head", color = "info" },
          { type = "text",  bind = P .. "spells" },
      } },
      { title = "Focus",    widgets = { { type = "text", bind = P .. "focus" } } },
      { title = "Skills",   widgets = { { type = "text", bind = P .. "skills" } } },
      { title = "Config",   widgets = { { type = "text", bind = P .. "config" } } },
      { title = "Progress", widgets = { { type = "text", bind = P .. "progress" } } },
    },
  }
end

-- ------------------------------------------------------------------ init
for _, s in ipairs({ "status", "spells", "focus", "skills", "config", "progress" }) do dirty[s] = true end
flush()
