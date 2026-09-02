-- 3S Cyborg -- the Cyborg guild HUD, built on the Guild.* GMCP packages a cyborg
-- character receives. 3Scapes only.
--
-- Built from one capture (Lobo, 1 Sep 2026, 4,662 messages / 13 packages). Every
-- field below was SEEN; nothing here is invented from a guild help file.
--
-- Four figures the capture could not settle were shipped as raw labelled numbers
-- in 1.0.0 rather than as guessed bars. Joakim answered all four on 1 Sep and
-- they are now displayed as what they actually are:
--   * stored_power is a RESERVE, a second pool an implant grants, separate from
--     power/power_max. Both are worth watching, so both are shown.
--   * si_pct is a percentage times 100: 5478 = 54.78% of the way to the next SI
--     level. So 107 + 54.78% -> 108 at 100%.
--   * control_used is what the ACTIVE implants consume and control_avail is what
--     is left to activate more with - a used/remaining pair, so their sum is the
--     capacity and 125 left out of 10,755 is nearly full.
--   * ammo is the magazine and ammo_rounds is the case: at 0 the case tops the
--     magazine back up (which is why ammo_rounds fell 10,000 -> 5,000 mid-capture
--     while ammo held). Two stores, never a ratio.
-- What remains genuinely unknown is marked VERIFY LIVE below.
--
-- Packages consumed:
--   Guild.State    the moment-to-moment numbers: power/power_max/stored_power,
--                  power_rgn, heat_pct, overheating, adrenaline_pct, painedit_pct,
--                  stims, ammo, si_level/si_pct, gexp_round, credits,
--                  target_condition, current_rounds, and the paged activated[]
--   Guild.Chassis  control, hardpoints, power grid, weapon array, machine_pct and
--                  the paged slots_free[] / slots_used[] body-part lists
--   Guild.Combat   kills, combat rounds, online time, paged patterns[], strategy[]
--   Guild.Progress gexp against gexp_cost, donation totals and their labels
--   Guild.Systems  ammo_rounds/ammo_type and the three thresholds
--   Guild.Info     rank_name, affiliation, liaison, guild_age, joined, guild_quest
--
-- A note on the guild split: a Cyborg's Guild.* packages share only their NAMES
-- with a Viking's. Guild.State for a Viking carries bars/points/hp; for a Cyborg
-- it carries power and heat. So this plugin reads the assembled snapshots
-- directly, like 3s-viking-kingdom, and nothing is shared with the Viking line
-- beyond the page assembler below.

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

-- seconds -> "3d 4h" / "5h 12m" / "42m" / "30s"
local function fmt_span(secs)
  secs = N(secs)
  if secs >= 86400 then return string.format("%dd %dh", math.floor(secs / 86400), math.floor((secs % 86400) / 3600)) end
  if secs >= 3600  then return string.format("%dh %dm", math.floor(secs / 3600), math.floor((secs % 3600) / 60)) end
  if secs >= 60    then return string.format("%dm", math.floor(secs / 60)) end
  return secs .. "s"
end

-- a percentage's colour: high is GOOD for a reserve, BAD for heat, so the caller
-- says which way round it reads rather than the function guessing
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

-- "cur/max  (pct%)" with the percent coloured. max 0 or missing -> just the value
local function ratio(cur, max, good_high)
  if not max or N(max) <= 0 then return esc(comma(cur)) end
  local pct = math.floor(N(cur) * 100 / N(max))
  return esc(comma(cur) .. "/" .. comma(max) .. "  ") .. col(pctcol(pct, good_high ~= false), pct .. "%")
end

local function two_col(items, width, add)
  for i = 1, #items, 2 do
    local a = items[i]
    local b = items[i + 1]
    add(padesc(a, width) .. (b and esc(b) or ""))
  end
end

-- ------------------------------------------------------------- snapshots
local ST, CHASSIS, COMBAT, PROGRESS, SYSTEMS, INFO = {}, {}, {}, {}, {}, {}
local function T(t, k) return type(t[k]) == "table" and t[k] or {} end

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

-- ------------------------------------------------------------- Status tab
local function build_status()
  local L = {}
  local add = mkadd(L)

  if not has(ST, "power") and not has(ST, "credits") then
    add("waiting for Guild.State...")
    scrye.setState(P .. "status", table.concat(L, "\n"))
    return
  end

  add("-- Power --")
  add("Power        " .. ratio(ST.power, ST.power_max, true))
  -- The reserve an implant grants. No ceiling for it has been seen, so it stays a
  -- figure rather than a bar - the pool is real, its maximum is not known.
  -- VERIFY LIVE: whether stored_power has a cap, and whether power_change is a
  -- delta or a target (it read 4912, equal to power_max, in the one full burst).
  if has(ST, "stored_power") then
    add("Reserve      " .. esc(comma(ST.stored_power)) .. col("dim", "   stored"))
  end
  if has(ST, "power_rgn") then
    local base, max = CHASSIS.power_rgn_base, CHASSIS.power_rgn_max
    add("Regen        " .. esc(comma(ST.power_rgn))
      .. ((base or max) and esc(string.format("   (chassis base %s, max %s)", S(base), S(max))) or ""))
  end

  add("")
  add("-- Condition --")
  if has(ST, "heat_pct") then
    local over = N(ST.overheating) > 0
    add("Heat         " .. col(pctcol(ST.heat_pct, false), N(ST.heat_pct) .. "%")
      .. (over and ("   " .. col("error", "OVERHEATING")) or ""))
  end
  if has(ST, "adrenaline_pct") then
    add("Adrenaline   " .. col(pctcol(ST.adrenaline_pct, true), N(ST.adrenaline_pct) .. "%"))
  end
  if has(ST, "painedit_pct") then
    add("Pain editor  " .. col(pctcol(ST.painedit_pct, true), N(ST.painedit_pct) .. "%"))
  end
  if has(ST, "stims") then
    add("Stims        " .. ratio(ST.stims, SYSTEMS.stims_max, true))
  end
  if has(ST, "ammo") then
    -- Two separate stores, never a ratio: ammo is the magazine, ammo_rounds is the
    -- case, and an empty magazine is refilled from the case. Dividing one by the
    -- other would read as "41% of your ammo left" when the true figure is the two
    -- added together.
    add("Ammo         " .. esc(comma(ST.ammo)) .. col("dim", " loaded")
      .. (has(SYSTEMS, "ammo_rounds")
          and (esc("   " .. comma(SYSTEMS.ammo_rounds)) .. col("dim", " in the case")) or "")
      .. (has(SYSTEMS, "ammo_type") and esc("   " .. S(SYSTEMS.ammo_type)) or ""))
  end
  if has(ST, "target_condition") then
    add("Target       " .. esc(S(ST.target_condition)))
  end

  add("")
  add("-- Synthetic intelligence --")
  if has(ST, "si_level") then
    -- si_pct is hundredths of a percent toward the NEXT level: 5478 = 54.78%, and
    -- at 100% level 107 becomes 108. Printed to two decimals because that is the
    -- precision the server actually sends - rounding it to 55% throws away a digit
    -- the feed went to the trouble of providing.
    local line = "SI level     " .. esc(S(ST.si_level))
    if has(ST, "si_pct") then
      local pct = N(ST.si_pct) / 100
      line = line .. esc(" -> " .. (N(ST.si_level) + 1) .. "   ")
        .. col(pctcol(pct, true), string.format("%.2f%%", pct))
    end
    add(line)
  end
  if has(PROGRESS, "gexp") then
    add("Guild xp     " .. ratio(PROGRESS.gexp, PROGRESS.gexp_cost, true))
  end
  if has(ST, "gexp_round") then add("  per round  " .. esc(comma(ST.gexp_round))) end

  add("")
  add("-- Standing --")
  if has(ST, "credits") then add("Credits      " .. esc(comma(ST.credits))) end
  if has(INFO, "rank_name") then
    add("Rank         " .. esc(S(INFO.rank_name))
      .. (has(INFO, "affiliation") and esc("   " .. S(INFO.affiliation)) or ""))
  end
  if has(INFO, "guild_age") then add("In the guild " .. esc(fmt_span(INFO.guild_age))) end
  -- the feed writes the literal string "None" when there is no liaison, which is
  -- a value pretending to be data - drop the row rather than print it
  local liaison = S(INFO.liaison)
  if liaison ~= "" and liaison:lower() ~= "none" then add("Liaison      " .. esc(liaison)) end
  if N(INFO.guild_quest) > 0 then add(col("info", "guild quest active")) end

  scrye.setState(P .. "status", table.concat(L, "\n"))
end

-- ----------------------------------------------------------- Implants tab
local function build_implants()
  local L = {}
  local add = mkadd(L)
  local on = T(ST, "activated")
  add("-- Activated systems --")
  if #on == 0 then
    add("nothing activated (or Guild.State has not listed them yet)")
  else
    add(col("dim", #on .. " running"))
    add("")
    local names = {}
    for _, s in ipairs(on) do names[#names + 1] = S(s) end
    table.sort(names)
    two_col(names, 34, add)
  end
  scrye.setState(P .. "implants", table.concat(L, "\n"))
end

-- ------------------------------------------------------------ Chassis tab
local function build_chassis()
  local L = {}
  local add = mkadd(L)
  if not has(CHASSIS, "hardpoint_max") and #T(CHASSIS, "slots_used") == 0 then
    add("waiting for Guild.Chassis...")
    scrye.setState(P .. "chassis", table.concat(L, "\n"))
    return
  end

  add("-- Capacity --")
  if has(CHASSIS, "hardpoint_max") then
    add("Hardpoints   " .. ratio(CHASSIS.hardpoint_used, CHASSIS.hardpoint_max, false))
  end
  if has(CHASSIS, "power_grid_max") then
    add("Power grid   " .. ratio(CHASSIS.power_grid_used, CHASSIS.power_grid_max, false))
  end
  if has(CHASSIS, "weapon_array_max") then
    add("Weapon array " .. ratio(CHASSIS.weapon_array_used, CHASSIS.weapon_array_max, false))
  end
  if has(CHASSIS, "machine_pct") then
    add("Machine      " .. col("info", N(CHASSIS.machine_pct) .. "%"))
  end
  -- used + available IS the capacity: control_used is what the active implants
  -- draw, control_avail is what is left to activate more with. The feed sends no
  -- total, so it is summed here rather than read - which is why the headroom is
  -- spelled out beside the ratio instead of left for you to subtract.
  if has(CHASSIS, "control_used") and has(CHASSIS, "control_avail") then
    local used, avail = N(CHASSIS.control_used), N(CHASSIS.control_avail)
    add("Control      " .. ratio(used, used + avail, false)
      .. col(avail <= 0 and "error" or "dim", esc("   " .. comma(avail) .. " free")))
  elseif has(CHASSIS, "control_used") then
    add("Control      " .. esc("used " .. comma(CHASSIS.control_used)))
  end

  local used, free = T(CHASSIS, "slots_used"), T(CHASSIS, "slots_free")
  add("")
  add(string.format("-- Slots -- %d used, %d free --", #used, #free))
  if #used > 0 then
    add("")
    add(col("dim", "used"))
    local u = {}
    for _, s in ipairs(used) do u[#u + 1] = S(s) end
    table.sort(u)
    two_col(u, 30, add)
  end
  if #free > 0 then
    add("")
    add(col("dim", "free"))
    local fr = {}
    for _, s in ipairs(free) do fr[#fr + 1] = S(s) end
    table.sort(fr)
    two_col(fr, 30, add)
  end
  scrye.setState(P .. "chassis", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Combat tab
local function build_combat()
  local L = {}
  local add = mkadd(L)
  if not has(COMBAT, "combat_rounds") and not has(COMBAT, "kills_total") then
    add("waiting for Guild.Combat...")
    scrye.setState(P .. "combat", table.concat(L, "\n"))
    return
  end

  add("-- Tally --")
  if has(COMBAT, "kills_total") then
    add("Kills        " .. esc(comma(COMBAT.kills_total))
      .. esc("   this login " .. comma(COMBAT.kills_login)))
  end
  if has(COMBAT, "combat_rounds") then add("Rounds       " .. esc(comma(COMBAT.combat_rounds))) end
  if has(ST, "current_rounds") or has(COMBAT, "current_rounds") then
    add("  this fight " .. esc(comma(COMBAT.current_rounds or ST.current_rounds)))
  end
  if has(COMBAT, "online_time") then add("Online       " .. esc(fmt_span(COMBAT.online_time))) end
  if has(COMBAT, "login_xp") or has(COMBAT, "login_bil") then
    add("This login   " .. esc("xp " .. comma(COMBAT.login_xp) .. "   bil " .. comma(COMBAT.login_bil)))
  end
  if has(COMBAT, "si_gained_pct") then add("SI gained    " .. esc(N(COMBAT.si_gained_pct) .. "%")) end

  local strat = T(COMBAT, "strategy")
  if #strat > 0 then
    add("")
    add("-- Strategy --")
    local parts = {}
    for _, v in ipairs(strat) do parts[#parts + 1] = S(v) end
    add(esc(table.concat(parts, " > ")))
  end

  local pats = T(COMBAT, "patterns")
  if #pats > 0 then
    add("")
    add("-- Firing patterns --")
    -- the capture's pattern strings run past 100 characters and are cut off by the
    -- server itself mid-word, so they are wrapped rather than truncated again here
    for i, p in ipairs(pats) do
      local s = S(p)
      add(col("dim", string.format("%2d.", i)) .. " " .. esc(s:sub(1, 66)))
      local rest = s:sub(67)
      while #rest > 0 do
        add("    " .. esc(rest:sub(1, 66)))
        rest = rest:sub(67)
      end
    end
  end
  scrye.setState(P .. "combat", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- one-liner
local function summary()
  if not has(ST, "power") then return "waiting for the cyborg feed" end
  local bits = { "Power " .. comma(ST.power) .. "/" .. comma(ST.power_max) }
  if has(ST, "heat_pct") then bits[#bits + 1] = "Heat " .. N(ST.heat_pct) .. "%" end
  if N(ST.overheating) > 0 then bits[#bits + 1] = "OVERHEATING" end
  if has(ST, "stored_power") then bits[#bits + 1] = "Reserve " .. comma(ST.stored_power) end
  if has(ST, "stims") then bits[#bits + 1] = "Stims " .. comma(ST.stims) end
  if has(ST, "ammo") then bits[#bits + 1] = "Ammo " .. comma(ST.ammo) end
  return table.concat(bits, "   ")
end

local BUILDERS = {
  status = build_status, implants = build_implants,
  chassis = build_chassis, combat = build_combat,
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
-- Guild packages arrive paged: {page=i, pages=N, full=1?} with list keys split
-- across pages. gasm(pkg, on_snap) subscribes and calls on_snap(snap) with the
-- merged snapshot each time a burst completes. Verbatim from the Viking plugins -
-- the paging rule is a property of the server, not of a guild.
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
  dirty.status = true; dirty.implants = true; dirty.combat = true
  schedule_flush()
end)

gasm("Guild.Chassis", function(snap)
  CHASSIS = snap
  dirty.chassis = true; dirty.status = true   -- the regen line reads chassis figures
  schedule_flush()
end)

gasm("Guild.Combat", function(snap)
  COMBAT = snap
  dirty.combat = true
  schedule_flush()
end)

gasm("Guild.Progress", function(snap)
  PROGRESS = snap
  dirty.status = true
  schedule_flush()
end)

gasm("Guild.Systems", function(snap)
  SYSTEMS = snap
  dirty.status = true       -- stims_max and the ammo figures are read there
  schedule_flush()
end)

gasm("Guild.Info", function(snap)
  INFO = snap
  dirty.status = true
  schedule_flush()
end)

-- --------------------------------------------------------------- aliases
-- cyb: the status page in the output window, for a glance without the panel
scrye.addAlias{ pattern = "^cyb$", regex = true, run = function()
  dirty.status = true; flush()
  for line in (scrye.getState(P .. "status") or ""):gmatch("[^\n]+") do
    scrye.print("@{#3FD8C0,bold}[cyborg]@{} " .. line)
  end
end }

-- --------------------------------------------------------------- panel
scrye.addPanel{
  title = "Cyborg",
  width = 420,
  accent = "#3FD8C0",          -- signature: cybernetic teal
  tabs = {
    { title = "Status",   widgets = {
        { type = "value", text = "", bind = P .. "summary", color = "info" },
        { type = "text",  bind = P .. "status" },
    } },
    { title = "Implants", widgets = { { type = "text", bind = P .. "implants" } } },
    { title = "Chassis",  widgets = { { type = "text", bind = P .. "chassis" } } },
    { title = "Combat",   widgets = { { type = "text", bind = P .. "combat" } } },
  },
}

-- ------------------------------------------------------------------ init
for _, s in ipairs({ "status", "implants", "chassis", "combat" }) do dirty[s] = true end
flush()
