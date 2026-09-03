-- 3S Mercenary -- the hired mercenary's HUD, built on the five Merc.* GMCP
-- packages. 3Scapes only, any guild: a merc is an NPC anyone can hire, so this
-- plugin knows nothing about guilds and sits beside 3s-vitals rather than inside
-- a guild HUD.
--
-- Built from one capture (Goran, 2 Sep 2026, 8,079 messages / 28 packages - the
-- first session subscribed with "Merc 1"). Every field below was SEEN; nothing is
-- invented from a help file. Fields the capture could not NAME are shown raw under
-- the server's own key, marked VERIFY LIVE, until a player says what they mean.
--
-- Packages consumed - all unpaged, all snapshot-then-delta: the first payload
-- carries "full":1 and every field, every later one only what changed. So the
-- snapshots below are MERGED, never replaced: a delta of {stam, target_hp} must
-- not cost us hp_max.
--   Merc.Info     merc, class, dtype, theme, perm_level/perm_cap, inst_level/
--                 inst_cap, eff_level, cost, follow, status, gender
--   Merc.Vitals   hp/hp_max, stam/stam_max/stam_regen, ap/ap_max/ap_regen,
--                 dormant, abils, target/target_hp - the live one, per round
--   Merc.Stats    perm_xp/perm_xp_next, inst_xp/inst_xp_next, skill_points, fund,
--                 spent_total/boot/skills/spec, and the session counters rounds/
--                 dmg_out/dmg_in/healing/abilities with their life_* twins
--   Merc.Talents  points, allocs, next_cost, and per talent {points, eff, min_level}
--   Merc.Skills   points, allocs, next_cost, and per skill {raw, eff}
--
-- The gauges bind to THIS plugin's state (plugin.3s-merc.hp ...) rather than to
-- merc.vitals.hp directly: the state tree learned snapshot/delta merging the same
-- day this plugin was written, and a gauge that only works on a host with that
-- fix is a gauge that blinks on the one before it. Publishing from the merged
-- snapshot works on both.

local P = "plugin." .. scrye.id .. "."

-- ---------------------------------------------------------------- helpers
local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end
local function col(c, s) return "@{" .. c .. "}" .. esc(s) .. "@{}" end
local function S(x) return x == nil and "" or tostring(x) end
local function N(x) return tonumber(x) or 0 end
local function has(t, k) return t[k] ~= nil end

local function comma(n)
  local s = tostring(math.floor(N(n)))
  while true do
    local a, b = s:gsub("^(%-?%d+)(%d%d%d)", "%1,%2")
    s = a; if b == 0 then break end
  end
  return s
end

-- a percentage's colour, high is good (a pool) unless told otherwise
local function pctcol(v, good_high)
  v = N(v)
  if good_high == false then
    if v >= 75 then return "error" end
    if v >= 40 then return "warning" end
    return "success"
  end
  if v >= 75 then return "success" end
  if v >= 40 then return "warning" end
  return "error"
end

-- "cur/max  (pct%)" with the percent coloured. max 0 or missing -> just the value
local function ratio(cur, max, good_high)
  if not max or N(max) <= 0 then return esc(comma(cur)) end
  local pct = math.floor(N(cur) * 100 / N(max))
  return esc(comma(cur) .. "/" .. comma(max) .. "  ") .. col(pctcol(pct, good_high), pct .. "%")
end

-- "Small cur" from "A small cur {somewhat chaotic}" -- the qualifier is not the name
local function plain(name)
  return (S(name):gsub("%s*%b{}", ""):gsub("^%s+", ""):gsub("%s+$", ""))
end

-- "entropy_herald" -> "entropy herald"; "cost_reduction" -> "cost reduction"
local function words(key) return (S(key):gsub("_", " ")) end

-- ------------------------------------------------------------- snapshots
local INFO, VIT, STATS, TAL, SKL = {}, {}, {}, {}, {}

-- Merge a payload into a snapshot. Top-level keys overwrite; a nested table (a
-- talent, a skill) overwrites WHOLE, since the server sends the whole object.
-- "full" itself is not data and is dropped, so has(snap, "full") never lies.
local function merge(snap, t)
  for k, v in pairs(t) do
    if k ~= "full" then snap[k] = v end
  end
end

-- ------------------------------------------------------- dirty / flush
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

local function name() return has(INFO, "merc") and S(INFO.merc) or (has(VIT, "merc") and S(VIT.merc)) or "" end

-- ------------------------------------------------------------- gauges
-- The four numbers the bars read, published from the merged snapshot so a delta
-- that carried only stam leaves hp_max standing.
local function publish_gauges()
  scrye.setState(P .. "hp",       S(VIT.hp))
  scrye.setState(P .. "hp_max",   S(VIT.hp_max))
  scrye.setState(P .. "stam",     S(VIT.stam))
  scrye.setState(P .. "stam_max", S(VIT.stam_max))
  scrye.setState(P .. "ap",       S(VIT.ap))
  scrye.setState(P .. "ap_max",   S(VIT.ap_max))
  -- the merc's own enemy: an empty target means it is not fighting, and the bar
  -- reads zero rather than showing the last mob's remaining health
  local target = plain(VIT.target)
  scrye.setState(P .. "target",    target)
  scrye.setState(P .. "target_hp", target ~= "" and S(VIT.target_hp) or "0")
end

-- ------------------------------------------------------------- Status tab
local function build_status()
  local L = {}
  local add = mkadd(L)

  if not has(VIT, "hp") and not has(INFO, "merc") then
    add("waiting for Merc.Info / Merc.Vitals...")
    add(col("dim", "nothing arrives until a mercenary is hired and with you"))
    scrye.setState(P .. "status", table.concat(L, "\n"))
    return
  end

  add("-- " .. (name() ~= "" and name() or "Mercenary") .. " --")
  if has(INFO, "class") or has(INFO, "dtype") then
    -- class is "offensive"; dtype the weapon's damage type ("Edged")
    add("Class        " .. esc(words(INFO.class)) .. (has(INFO, "dtype") and col("dim", "   " .. S(INFO.dtype)) or ""))
  end
  if has(INFO, "perm_level") then
    -- permanent level against its cap, instance level (this hire) against its own
    add("Level        " .. esc(S(INFO.perm_level) .. "/" .. S(INFO.perm_cap)) .. col("dim", " permanent")
      .. (has(INFO, "inst_level") and (esc("   " .. S(INFO.inst_level) .. "/" .. S(INFO.inst_cap)) .. col("dim", " this hire")) or ""))
  end
  if has(INFO, "eff_level") then add("Effective    " .. esc(S(INFO.eff_level))) end

  add("")
  add("-- Pools --")
  if has(VIT, "hp") then add("HP           " .. ratio(VIT.hp, VIT.hp_max)) end
  if has(VIT, "stam") then
    add("Stamina      " .. ratio(VIT.stam, VIT.stam_max)
      .. (has(VIT, "stam_regen") and col("dim", "   +" .. S(VIT.stam_regen) .. "/round") or ""))
  end
  if has(VIT, "ap") then
    add("Action pts   " .. ratio(VIT.ap, VIT.ap_max)
      .. (has(VIT, "ap_regen") and col("dim", "   +" .. S(VIT.ap_regen) .. "/round") or ""))
  end
  do
    local target = plain(VIT.target)
    if target ~= "" then
      add("Target       " .. col("error", target) .. "   " .. col(pctcol(VIT.target_hp, false), N(VIT.target_hp) .. "%") .. col("dim", " left"))
    elseif has(VIT, "target") then
      add("Target       " .. col("dim", "none"))
    end
  end

  add("")
  add("-- State --")
  do
    local bits = {}
    if has(INFO, "follow") then bits[#bits + 1] = N(INFO.follow) > 0 and col("success", "following") or col("warning", "not following") end
    if has(VIT, "dormant") then bits[#bits + 1] = N(VIT.dormant) > 0 and col("warning", "dormant") or col("dim", "awake") end
    if #bits > 0 then add(table.concat(bits, "   ")) end
  end
  -- VERIFY LIVE: status (read 1), cost (read 8), theme ("entropy_herald"), gender
  -- (read 2) and abils (read "") are shown under the server's own names, raw, so a
  -- player can read them off the panel and say what they are. Nothing is derived
  -- from them.
  do
    local raw = {}
    for _, k in ipairs({ "status", "cost", "theme", "gender" }) do
      if has(INFO, k) then raw[#raw + 1] = k .. "=" .. S(INFO[k]) end
    end
    if has(VIT, "abils") and S(VIT.abils) ~= "" then raw[#raw + 1] = "abils=" .. S(VIT.abils) end
    if #raw > 0 then add(col("dim", "not yet understood: " .. table.concat(raw, "  "))) end
  end

  scrye.setState(P .. "status", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Stats tab
local function build_stats()
  local L = {}
  local add = mkadd(L)

  if not has(STATS, "perm_xp") and not has(STATS, "rounds") then
    add("waiting for Merc.Stats...")
    scrye.setState(P .. "stats", table.concat(L, "\n"))
    return
  end

  add("-- Experience --")
  if has(STATS, "perm_xp") then
    -- xp toward the next PERMANENT level; perm_xp_next is what the next one costs
    add("Permanent    " .. esc(comma(STATS.perm_xp))
      .. (has(STATS, "perm_xp_next") and col("dim", "   next " .. comma(STATS.perm_xp_next)) or ""))
  end
  if has(STATS, "inst_xp") then
    add("This hire    " .. esc(comma(STATS.inst_xp))
      .. (has(STATS, "inst_xp_next") and col("dim", "   next " .. comma(STATS.inst_xp_next)) or ""))
  end
  if has(STATS, "skill_points") then add("Skill points " .. esc(comma(STATS.skill_points)) .. col("dim", " unspent")) end
  if has(STATS, "fund") then add("Fund         " .. esc(comma(STATS.fund))) end

  add("")
  add("-- This session / lifetime --")
  local function pair(label, k)
    if not has(STATS, k) and not has(STATS, "life_" .. k) then return end
    add(string.format("%-12s ", label) .. esc(comma(STATS[k] or 0))
      .. (has(STATS, "life_" .. k) and col("dim", "   / " .. comma(STATS["life_" .. k])) or ""))
  end
  pair("Rounds", "rounds")
  pair("Damage out", "dmg_out")
  pair("Damage in", "dmg_in")
  pair("Healing", "healing")
  pair("Abilities", "abilities")

  if has(STATS, "spent_total") then
    add("")
    add("-- Spent --")
    add("Total        " .. esc(comma(STATS.spent_total)))
    -- VERIFY LIVE: "boot" is read as the boost/levelling spend, "spec" as talents;
    -- the three add up to the total in the capture (21,133,479 + 4,948,955 +
    -- 2,036,650 = 28,119,084), which says they are the parts, not what they buy.
    if has(STATS, "spent_boot")   then add("  boost      " .. esc(comma(STATS.spent_boot))) end
    if has(STATS, "spent_skills") then add("  skills     " .. esc(comma(STATS.spent_skills))) end
    if has(STATS, "spent_spec")   then add("  talents    " .. esc(comma(STATS.spent_spec)) .. col("dim", "  (spent_spec)")) end
  end

  scrye.setState(P .. "stats", table.concat(L, "\n"))
end

-- ------------------------------------------------------- Talents / Skills
-- Both tabs are a header line plus a table: rows are tab-separated columns, one
-- per line, exactly what the table widget takes.

local function talent_rows()
  -- every table-valued key is a talent; order by min_level, then name, so a new
  -- talent the capture never saw still lands in the right place
  local names = {}
  for k, v in pairs(TAL) do if type(v) == "table" then names[#names + 1] = k end end
  table.sort(names, function(a, b)
    local la, lb = N(TAL[a].min_level), N(TAL[b].min_level)
    if la ~= lb then return la < lb end
    return a < b
  end)
  local level = N(INFO.eff_level ~= nil and INFO.eff_level or INFO.perm_level)
  local rows = {}
  for _, k in ipairs(names) do
    local t = TAL[k]
    local gate = N(t.min_level)
    local state
    if N(t.points) > 0 then state = "trained"
    elseif has(INFO, "perm_level") and gate > level then state = "locked (lvl " .. gate .. ")"
    else state = "open" end
    rows[#rows + 1] = table.concat({ words(k), S(t.points), S(t.eff), S(t.min_level), state }, "\t")
  end
  return rows
end

local function build_talents()
  if not has(TAL, "points") and next(TAL) == nil then
    scrye.setState(P .. "talents_head", "waiting for Merc.Talents...")
    scrye.setState(P .. "talents", "")
    return
  end
  local head = {}
  if has(TAL, "points") then head[#head + 1] = S(TAL.points) .. " free" end
  if has(TAL, "allocs") then head[#head + 1] = S(TAL.allocs) .. " allocated" end
  if has(TAL, "next_cost") then head[#head + 1] = "next point " .. comma(TAL.next_cost) end
  scrye.setState(P .. "talents_head", table.concat(head, "   "))
  scrye.setState(P .. "talents", table.concat(talent_rows(), "\n"))
end

local function skill_rows()
  local names = {}
  for k, v in pairs(SKL) do if type(v) == "table" then names[#names + 1] = k end end
  table.sort(names, function(a, b)
    -- trained first (by effective value, highest up), then the rest by name
    local ea, eb = N(SKL[a].eff), N(SKL[b].eff)
    if (ea > 0) ~= (eb > 0) then return ea > 0 end
    if ea ~= eb then return ea > eb end
    return a < b
  end)
  local rows = {}
  for _, k in ipairs(names) do
    local s = SKL[k]
    rows[#rows + 1] = table.concat({ words(k), S(s.raw), S(s.eff) }, "\t")
  end
  return rows
end

local function build_skills()
  if not has(SKL, "points") and next(SKL) == nil then
    scrye.setState(P .. "skills_head", "waiting for Merc.Skills...")
    scrye.setState(P .. "skills", "")
    return
  end
  local head = {}
  if has(SKL, "points") then head[#head + 1] = S(SKL.points) .. " free" end
  if has(SKL, "allocs") then head[#head + 1] = S(SKL.allocs) .. " allocated" end
  if has(SKL, "next_cost") then head[#head + 1] = "next point " .. comma(SKL.next_cost) end
  scrye.setState(P .. "skills_head", table.concat(head, "   "))
  scrye.setState(P .. "skills", table.concat(skill_rows(), "\n"))
end

-- ------------------------------------------------------------- one-liner
local function summary()
  if not has(VIT, "hp") and not has(INFO, "merc") then return "waiting for the merc feed" end
  local bits = { name() ~= "" and name() or "merc" }
  if has(VIT, "hp") then bits[#bits + 1] = "HP " .. comma(VIT.hp) .. "/" .. comma(VIT.hp_max) end
  if has(VIT, "stam") then bits[#bits + 1] = "Stam " .. comma(VIT.stam) end
  if has(VIT, "ap") then bits[#bits + 1] = "AP " .. comma(VIT.ap) end
  local target = plain(VIT.target)
  if target ~= "" then bits[#bits + 1] = "-> " .. target .. " " .. N(VIT.target_hp) .. "%" end
  if N(VIT.dormant) > 0 then bits[#bits + 1] = "DORMANT" end
  return table.concat(bits, "   ")
end

local BUILDERS = {
  status = build_status, stats = build_stats, talents = build_talents, skills = build_skills,
}

flush = function()
  flush_pending = false
  for sec in pairs(dirty) do
    local b = BUILDERS[sec]
    if b then pcall(b) end
  end
  publish_gauges()
  scrye.setState(P .. "summary", summary())
  dirty = {}
end

-- ------------------------------------------------------------------ feed
local function on(pkg, snap, ...)
  local secs = { ... }
  scrye.onGmcp(pkg, function(json)
    local ok, t = pcall(scrye.json.decode, json)
    if not ok or type(t) ~= "table" then return end
    merge(snap, t)
    for _, s in ipairs(secs) do dirty[s] = true end
    schedule_flush()
  end)
end

on("Merc.Info",    INFO,  "status", "talents")   -- talents read the level gate
on("Merc.Vitals",  VIT,   "status")
on("Merc.Stats",   STATS, "stats")
on("Merc.Talents", TAL,   "talents")
on("Merc.Skills",  SKL,   "skills")

-- A new connection is a new merc, or none: the snapshots start over, so a
-- character who logs in WITHOUT the merc does not keep showing the last one's bars.
scrye.onConnect(function()
  for k in pairs(INFO) do INFO[k] = nil end
  for k in pairs(VIT) do VIT[k] = nil end
  for k in pairs(STATS) do STATS[k] = nil end
  for k in pairs(TAL) do TAL[k] = nil end
  for k in pairs(SKL) do SKL[k] = nil end
  for _, s in ipairs({ "status", "stats", "talents", "skills" }) do dirty[s] = true end
  flush()
end)

-- --------------------------------------------------------------- aliases
-- merc: the status page in the output window, for a glance without the panel
scrye.addAlias{ pattern = "^merc$", regex = true, run = function()
  dirty.status = true; flush()
  for line in (scrye.getState(P .. "status") or ""):gmatch("[^\n]+") do
    scrye.print("@{#C08A3E,bold}[merc]@{} " .. line)
  end
end }

-- --------------------------------------------------------------- panel
scrye.addPanel{
  title = "Mercenary",
  width = 400,
  accent = "#C08A3E",          -- signature: hired steel, a bronze
  tabs = {
    { title = "Status", widgets = {
        { type = "value", text = "", bind = P .. "summary", color = "info" },
        { type = "gauge", text = "HP",   value = P .. "hp",   max = P .. "hp_max",   dim = true },
        { type = "gauge", text = "Stam", value = P .. "stam", max = P .. "stam_max", dim = true },
        { type = "gauge", text = "AP",   value = P .. "ap",   max = P .. "ap_max",   dim = true },
        { type = "value", text = "Target: ", bind = P .. "target", color = "error" },
        { type = "gauge", text = "Target", value = P .. "target_hp", max = 100, dim = true, color = "error" },
        { type = "text",  bind = P .. "status" },
    } },
    { title = "Stats", widgets = { { type = "text", bind = P .. "stats" } } },
    { title = "Talents", widgets = {
        { type = "label", bind = P .. "talents_head", color = "dim" },
        { type = "table", bind = P .. "talents", columns = { "Talent", "Pts", "Eff", "Lvl", "" }, align = "lrrrl" },
    } },
    { title = "Skills", widgets = {
        { type = "label", bind = P .. "skills_head", color = "dim" },
        { type = "table", bind = P .. "skills", columns = { "Skill", "Raw", "Eff" }, align = "lrr" },
    } },
  },
}

-- ------------------------------------------------------------------ init
for _, s in ipairs({ "status", "stats", "talents", "skills" }) do dirty[s] = true end
flush()
