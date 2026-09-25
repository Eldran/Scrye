-- 3S Juggernaut -- the Juggernaut guild HUD, built on the Guild.* GMCP packages a
-- juggernaut character receives.
--
-- Built from one capture (icewind, 25 Sep 2026, 1,199 messages / 16 packages).
-- Every field below was SEEN; nothing is invented from a guild help file. Where the
-- capture shows a number but not what it means, the number is shown under the
-- server's own name and marked VERIFY LIVE rather than dressed up as a bar.
--
-- Packages consumed:
--   Guild.State    heat_pct, stim_pct, battery, ammo (a STRING, "23800"),
--                  missiles/missiles_max, clan_powers/clan_powers_max, glevel_pct,
--                  reset_pct, credits, hits_last_round and the 0/1 flags
--                  jump_jets, low_light, follower_stim
--   Guild.Combat   target_condition, target_rounds, hits_last_round, frenzy_active
--   Guild.Suits    current (the suit worn, by name) and the paged suits[] list -
--                  level, gxp_pct, valour/has_valour, current 0/1
--   Guild.Chassis  mounts[] (slot, weapon, enabled, ammo, ammo_type), mounts_available
--   Guild.Tech     credits and the paged tech[] tree (value against max)
--   Guild.Skills   gunnery, piloting, skill_points, skill_max, skills[] (value,
--                  max, cost)
--   Guild.Session  login / reset / lifetime tallies and the paged casualties[]
--                  (the kills this login: name, rounds, damage, class)
--   Guild.Info     clan, rank, title, clan_honor, vault, storage, depot_status,
--                  eval_level, kills, suit_kills, sponsor, recruiter, joined, guild_age
--
-- Guild.State, Guild.Chassis, Guild.Combat and Guild.Info share their NAMES with the
-- Cyborg's packages and nothing else: a juggernaut's Guild.State has heat and stims
-- where a cyborg's has power. Each guild plugin reads its own fields and shows
-- "waiting" for a guild whose fields never come.

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
  return math.floor(secs) .. "s"
end

-- a percentage's colour: high is GOOD for a reserve, BAD for heat
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

-- "cur/max" coloured by how full it is; no max -> the value alone
local function count_of(cur, max)
  if not max or N(max) <= 0 then return esc(comma(cur)) end
  local c = N(cur) >= N(max) and "success" or (N(cur) > 0 and "warning" or "error")
  return col(c, comma(cur) .. "/" .. comma(max))
end

-- a ten-cell bar for a percent
local function bar(pct, good_high)
  local n = math.max(0, math.min(10, math.floor(N(pct) / 10 + 0.5)))
  return col(pctcol(pct, good_high), string.rep("#", n)) .. col("dim", string.rep(".", 10 - n))
end

local function nice(s) return (tostring(s or ""):gsub("_", " ")) end
local function flag(v) return N(v) > 0 end

-- ------------------------------------------------------------- snapshots
local ST, COMBAT, SUITS, CHASSIS, TECH, SKILLS, SESSION, INFO = {}, {}, {}, {}, {}, {}, {}, {}
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

-- the suit worn: Guild.Suits' `current` names it; Guild.Info's current_suit and the
-- suits[] record with current = 1 say the same, whichever arrived
local function current_suit()
  local name = SUITS.current
  if type(name) ~= "string" or name == "" then name = INFO.current_suit end
  local rec
  for _, s in ipairs(T(SUITS, "suits")) do
    if type(s) == "table" and (N(s.current) > 0 or (name and s.name == name)) then rec = s end
  end
  if rec and (not name or name == "") then name = rec.name end
  return name, rec
end

-- ------------------------------------------------------------- Status tab
local function build_status()
  local L = {}
  local add = mkadd(L)

  if not has(ST, "heat_pct") and not has(ST, "ammo") then
    add("waiting for Guild.State...")
    scrye.setState(P .. "status", table.concat(L, "\n"))
    return
  end

  add("-- Mech --")
  if has(ST, "heat_pct") then
    add("Heat         " .. bar(ST.heat_pct, false) .. " " .. col(pctcol(ST.heat_pct, false), N(ST.heat_pct) .. "%"))
  end
  if has(ST, "stim_pct") then
    add("Stims        " .. bar(ST.stim_pct, true) .. " " .. col(pctcol(ST.stim_pct, true), N(ST.stim_pct) .. "%")
      .. (flag(ST.follower_stim) and col("info", "   follower stim on") or ""))
  end
  if has(ST, "battery") then add("Battery      " .. esc(comma(ST.battery))) end
  if has(ST, "ammo") then
    -- Guild.State sends ammo as a STRING ("23800"); it is the total, where the chassis
    -- mounts below carry what each weapon holds
    add("Ammo         " .. esc(comma(ST.ammo)))
  end
  if has(ST, "missiles") then add("Missiles     " .. count_of(ST.missiles, ST.missiles_max)) end
  if has(ST, "clan_powers") then add("Clan powers  " .. count_of(ST.clan_powers, ST.clan_powers_max)) end
  local flags = {}
  if flag(ST.jump_jets) then flags[#flags + 1] = col("info", "jump jets") end
  if flag(ST.low_light) then flags[#flags + 1] = col("info", "low light") end
  if #flags > 0 then add("Active       " .. table.concat(flags, "  ")) end

  add("")
  add("-- Fight --")
  local tc = S(COMBAT.target_condition)
  if tc ~= "" then add("Target       " .. esc(tc)) end
  if has(COMBAT, "target_rounds") then add("Rounds       " .. esc(comma(COMBAT.target_rounds))) end
  local hits = COMBAT.hits_last_round
  if hits == nil then hits = ST.hits_last_round end
  if hits ~= nil then add("Hits last    " .. esc(S(hits))) end
  if flag(COMBAT.frenzy_active) then add(col("warning", "FRENZY")) end

  add("")
  add("-- Suit --")
  local name, rec = current_suit()
  if name and name ~= "" then
    local pct = has(ST, "glevel_pct") and N(ST.glevel_pct) or (rec and N(rec.gxp_pct)) or nil
    add("Suit         " .. col("accent", nice(name))
      .. (rec and esc("   level " .. S(rec.level)) or "")
      .. (pct and ("   " .. bar(pct, true) .. " " .. esc(string.format("%.2f%%", pct))) or ""))
    if rec and N(rec.has_valour) > 0 then add("Valour       " .. esc(S(rec.valour))) end
  end
  -- VERIFY LIVE: reset_pct climbs slowly out of combat (82 -> 85 over a few minutes
  -- in the capture); shown as the server's number until its meaning is known
  if has(ST, "reset_pct") then add("Reset        " .. esc(N(ST.reset_pct) .. "%")) end
  if has(ST, "credits") then add("Credits      " .. esc(comma(ST.credits))) end

  scrye.setState(P .. "status", table.concat(L, "\n"))
end

-- -------------------------------------------------------------- Suits tab
local function build_suits()
  local L = {}
  local add = mkadd(L)
  local suits = {}
  for _, s in ipairs(T(SUITS, "suits")) do if type(s) == "table" then suits[#suits + 1] = s end end
  if #suits == 0 then
    add("waiting for Guild.Suits...")
    scrye.setState(P .. "suits", table.concat(L, "\n"))
    return
  end
  local name = current_suit()
  table.sort(suits, function(a, b)
    if N(a.level) ~= N(b.level) then return N(a.level) > N(b.level) end
    return S(a.name) < S(b.name)
  end)
  add("-- Suits --")
  add(col("dim", string.format("  %-12s %5s  %-10s %8s  %s", "Suit", "Level", "", "Next", "Valour")))
  for _, s in ipairs(suits) do
    local worn = N(s.current) > 0 or (name and s.name == name)
    local valour = N(s.has_valour) > 0 and S(s.valour) or col("dim", "-")
    add((worn and col("accent", ">") or " ") .. " "
      .. (worn and col("accent", string.format("%-12s", nice(s.name))) or padesc(nice(s.name), 12))
      .. esc(string.format(" %5s  ", S(s.level))) .. bar(s.gxp_pct, true)
      .. esc(string.format(" %7.2f%%  ", N(s.gxp_pct))) .. valour)
  end
  add("")
  add(col("dim", "> the suit you wear   Next: guild xp toward the next level"))
  if has(INFO, "suit_kills") then
    add("")
    add("Kills in this suit  " .. esc(comma(INFO.suit_kills)) .. col("dim", "   (all suits " .. comma(INFO.kills) .. ")"))
  end
  scrye.setState(P .. "suits", table.concat(L, "\n"))
end

-- ------------------------------------------------------------ Loadout tab
local function build_loadout()
  local L = {}
  local add = mkadd(L)

  local mounts = T(CHASSIS, "mounts")
  add("-- Weapon mounts --")
  if #mounts == 0 then
    add(col("dim", "waiting for Guild.Chassis..."))
  else
    table.sort(mounts, function(a, b) return N(a.slot) < N(b.slot) end)
    for _, m in ipairs(mounts) do
      local on = N(m.enabled) > 0
      local ammo = S(m.ammo_type) ~= "" and (esc("   " .. comma(m.ammo) .. " ") .. col("dim", S(m.ammo_type)))
                   or (N(m.ammo) > 0 and esc("   " .. comma(m.ammo)) or "")
      add(esc(string.format("%2s ", S(m.slot))) .. (on and padesc(S(m.weapon), 22) or col("dim", string.format("%-22s", S(m.weapon) .. " (off)")))
        .. ammo)
    end
    if has(CHASSIS, "mounts_available") then
      add(col("dim", comma(CHASSIS.mounts_available) .. " mount(s) free"))
    end
  end

  local skills = T(SKILLS, "skills")
  add("")
  add("-- Skills --")
  if #skills == 0 and not has(SKILLS, "skill_points") then
    add(col("dim", "waiting for Guild.Skills..."))
  else
    local head = {}
    if has(SKILLS, "gunnery") then head[#head + 1] = "gunnery " .. S(SKILLS.gunnery) end
    if has(SKILLS, "piloting") then head[#head + 1] = "piloting " .. S(SKILLS.piloting) end
    if has(SKILLS, "skill_points") then head[#head + 1] = comma(SKILLS.skill_points) .. " skill points" end
    if #head > 0 then add(esc(table.concat(head, "   "))) end
    table.sort(skills, function(a, b) return S(a.name) < S(b.name) end)
    for _, sk in ipairs(skills) do
      local pct = N(sk.max) > 0 and N(sk.value) * 100 / N(sk.max) or 0
      local afford = has(SKILLS, "skill_points") and N(sk.cost) > 0 and N(SKILLS.skill_points) >= N(sk.cost)
      add(padesc(nice(sk.name), 18) .. bar(pct, true) .. esc(string.format(" %3s/%-3s ", S(sk.value), S(sk.max)))
        .. (N(sk.value) >= N(sk.max) and N(sk.max) > 0 and col("success", "max")
            or col(afford and "success" or "dim", "next " .. comma(sk.cost))))
    end
  end

  local tech = T(TECH, "tech")
  add("")
  add("-- Tech --")
  if #tech == 0 then
    add(col("dim", "waiting for Guild.Tech..."))
  else
    table.sort(tech, function(a, b) return S(a.name) < S(b.name) end)
    for _, t in ipairs(tech) do
      if N(t.max) > 0 then
        local pct = N(t.value) * 100 / N(t.max)
        add(padesc(nice(t.name), 20) .. bar(pct, true) .. esc(string.format(" %2s/%-2s", S(t.value), S(t.max)))
          .. (N(t.value) >= N(t.max) and ("  " .. col("success", "max")) or ""))
      end
    end
    if has(TECH, "credits") then add(col("dim", comma(TECH.credits) .. " credits to spend")) end
  end
  scrye.setState(P .. "loadout", table.concat(L, "\n"))
end

-- ------------------------------------------------------------ Session tab
local function build_session()
  local L = {}
  local add = mkadd(L)
  local lg, rs, lt = T(SESSION, "login"), T(SESSION, "reset"), T(SESSION, "lifetime")
  if not next(lg) and not next(rs) then
    add("waiting for Guild.Session...")
    scrye.setState(P .. "session", table.concat(L, "\n"))
    return
  end

  local function rate(kills, secs)
    if N(secs) < 60 then return "-" end
    return string.format("%.1f/h", N(kills) * 3600 / N(secs))
  end
  add("-- This login --")
  if next(lg) then
    add(esc(string.format("Kills %s in %s   %s   %s rounds", comma(lg.kills), fmt_span(lg.seconds),
      rate(lg.kills, lg.seconds), comma(lg.rounds))))
    -- "class" is the server's number for how big a kill was; shown as it comes
    if has(lg, "best_kill") then
      add("Best         " .. esc(S(lg.best_kill)) .. col("dim", "  class " .. comma(lg.best_kill_class)
        .. (has(lg, "best_kill_rounds") and (", " .. S(lg.best_kill_rounds) .. " rounds") or "")))
    end
    if has(lg, "worst_kill") then
      add("Smallest     " .. esc(S(lg.worst_kill)) .. col("dim", "  class " .. comma(lg.worst_kill_class)))
    end
    if has(lg, "last_kill") then
      add("Last         " .. esc(S(lg.last_kill)) .. col("dim", "  class " .. comma(lg.last_kill_class)))
    end
    local function use(label, u, w, d)
      if u == nil and w == nil and d == nil then return end
      add(padesc(label, 13) .. esc(string.format("used %s   wasted ", comma(u)))
        .. col(N(w) > 0 and "warning" or "dim", comma(w)) .. esc("   donated " .. comma(d)))
    end
    use("Missiles", lg.missiles_used, lg.missiles_wasted, lg.missiles_donated)
    use("Clan powers", lg.cpowers_used, lg.cpowers_wasted, lg.cpowers_donated)
  end
  if next(rs) then
    add("")
    add("-- Since reset --")
    add(esc(string.format("Kills %s in %s   %s   %s rounds", comma(rs.kills), fmt_span(rs.seconds),
      rate(rs.kills, rs.seconds), comma(rs.rounds))))
  end
  if next(lt) then
    add("")
    add("-- Lifetime --")
    add(esc(string.format("%s combat rounds   %s in combat   %s%% of the time",
      comma(lt.combat_rounds), fmt_span(lt.combat_time), S(lt.combat_pct))))
  end

  local cas = {}
  for _, c in ipairs(T(SESSION, "casualties")) do if type(c) == "table" then cas[#cas + 1] = c end end
  if #cas > 0 then
    add("")
    add("-- Kills this login --")
    add(col("dim", string.format("%-24s %6s %7s %9s", "Mob", "Rounds", "Damage", "Class")))
    for i = #cas, 1, -1 do       -- newest first: the server lists them in the order they fell
      local c = cas[i]
      add(padesc(S(c.name):sub(1, 24), 24) .. esc(string.format(" %6s %7s %9s", comma(c.rounds), comma(c.damage), comma(c.class))))
    end
  end
  scrye.setState(P .. "session", table.concat(L, "\n"))
end

-- --------------------------------------------------------------- Clan tab
local function build_clan()
  local L = {}
  local add = mkadd(L)
  if not has(INFO, "clan") and not has(INFO, "rank") then
    add("waiting for Guild.Info...")
    scrye.setState(P .. "clan", table.concat(L, "\n"))
    return
  end
  add("-- Clan --")
  if has(INFO, "title") then add(col("accent", S(INFO.title))) end
  if has(INFO, "clan") then add("Clan         " .. esc(S(INFO.clan))
    .. (has(INFO, "clan_honor") and col("dim", "   honour " .. S(INFO.clan_honor)) or "")) end
  if has(INFO, "rank") then add("Rank         " .. esc(S(INFO.rank))
    .. (N(INFO.house_leader) > 0 and col("info", "   house leader") or "")
    .. (N(INFO.oathmaster) > 0 and col("info", "   oathmaster") or "")) end
  if has(INFO, "eval_level") then add("Evaluation   " .. esc(string.format("%.2f", N(INFO.eval_level)))) end
  local sponsor = S(INFO.sponsor)
  if sponsor ~= "" and sponsor:lower() ~= "nobody" then add("Sponsor      " .. esc(sponsor)) end
  local recruiter = S(INFO.recruiter)
  if recruiter ~= "" and recruiter:lower() ~= "none" then add("Recruiter    " .. esc(recruiter)) end
  if N(INFO.recruits) > 0 then add("Recruits     " .. esc(S(INFO.recruits))) end
  if has(INFO, "guild_age") then add("In the guild " .. esc(fmt_span(INFO.guild_age))) end

  add("")
  add("-- Holdings --")
  if has(INFO, "vault") then add("Vault        " .. esc(comma(INFO.vault))) end
  if has(INFO, "storage") then add("Storage      " .. esc(comma(INFO.storage))) end
  -- depot_status is two fields in one string, "None|Wheeled Tank 14%": the depot
  -- and what it is building (VERIFY LIVE: the reading of the two halves)
  if has(INFO, "depot_status") then
    local a, b = S(INFO.depot_status):match("^([^|]*)|(.*)$")
    if a then
      add("Depot        " .. esc(a))
      if b ~= "" then add("  building   " .. esc(b)) end
    else
      add("Depot        " .. esc(S(INFO.depot_status)))
    end
  end
  if has(INFO, "kills") then
    add("")
    add("Kills        " .. esc(comma(INFO.kills)) .. col("dim", "   in this suit " .. comma(INFO.suit_kills)))
  end
  scrye.setState(P .. "clan", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- one-liner
local function summary()
  if not has(ST, "heat_pct") and not has(ST, "ammo") then return "waiting for the juggernaut feed" end
  local bits = {}
  if has(ST, "heat_pct") then bits[#bits + 1] = "Heat " .. N(ST.heat_pct) .. "%" end
  if has(ST, "stim_pct") then bits[#bits + 1] = "Stim " .. N(ST.stim_pct) .. "%" end
  if has(ST, "ammo") then bits[#bits + 1] = "Ammo " .. comma(ST.ammo) end
  if has(ST, "missiles") then bits[#bits + 1] = "Msl " .. S(ST.missiles) .. "/" .. S(ST.missiles_max) end
  if has(ST, "clan_powers") then bits[#bits + 1] = "CP " .. S(ST.clan_powers) .. "/" .. S(ST.clan_powers_max) end
  if flag(COMBAT.frenzy_active) then bits[#bits + 1] = "FRENZY" end
  return table.concat(bits, "   ")
end

local BUILDERS = {
  status = build_status, suits = build_suits, loadout = build_loadout,
  session = build_session, clan = build_clan,
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

-- Every package carries guild = "juggernaut"; a snapshot from another guild's
-- same-named package (a cyborg's Guild.State) is not ours and is left alone.
local function ours(t) local g = t and t.guild ; return g == nil or g == "juggernaut" end
local function feed(pkg, set, sections)
  scrye.onGmcp(pkg, function(json)
    -- the assembler drops `guild` from the snapshot, so the guild is checked on the
    -- raw message: one foreign message marks the package as not ours
    local ok, t = pcall(scrye.json.decode, json)
    if ok and type(t) == "table" and not ours(t) then set(nil) end
  end)
  gasm(pkg, function(snap)
    set(snap)
    for _, s in ipairs(sections) do dirty[s] = true end
    schedule_flush()
  end)
end

local foreign = {}
local function setter(name, assign)
  return function(snap)
    if snap == nil then foreign[name] = true ; return end
    if foreign[name] then return end
    assign(snap)
  end
end

feed("Guild.State",   setter("state",   function(s) ST = s end),      { "status" })
feed("Guild.Combat",  setter("combat",  function(s) COMBAT = s end),  { "status" })
feed("Guild.Suits",   setter("suits",   function(s) SUITS = s end),   { "status", "suits" })
feed("Guild.Chassis", setter("chassis", function(s) CHASSIS = s end), { "loadout" })
feed("Guild.Tech",    setter("tech",    function(s) TECH = s end),    { "loadout" })
feed("Guild.Skills",  setter("skills",  function(s) SKILLS = s end),  { "loadout" })
feed("Guild.Session", setter("session", function(s) SESSION = s end), { "session" })
feed("Guild.Info",    setter("info",    function(s) INFO = s end),    { "clan", "suits", "status" })

-- --------------------------------------------------------------- aliases
-- jug: the status page in the output window, for a glance without the panel
scrye.addAlias{ pattern = "^jug$", regex = true, run = function()
  dirty.status = true; flush()
  for line in (scrye.getState(P .. "status") or ""):gmatch("[^\n]+") do
    scrye.print("@{#D08A3A,bold}[jugg]@{} " .. line)
  end
end }

-- --------------------------------------------------------------- panel
scrye.addPanel{
  title = "Juggernaut",
  width = 440,
  accent = "#D08A3A",          -- signature: hot armour
  tabs = {
    { title = "Status",  widgets = {
        { type = "value", text = "", bind = P .. "summary", color = "info" },
        { type = "text",  bind = P .. "status" },
    } },
    { title = "Suits",   widgets = { { type = "text", bind = P .. "suits" } } },
    { title = "Loadout", widgets = { { type = "text", bind = P .. "loadout" } } },
    { title = "Session", widgets = { { type = "text", bind = P .. "session" } } },
    { title = "Clan",    widgets = { { type = "text", bind = P .. "clan" } } },
  },
}

-- ------------------------------------------------------------------ init
for s in pairs(BUILDERS) do dirty[s] = true end
flush()
