-- 3S Viking Kingdom -- the dynasty: hird roster, recruiting, thralls, the grudge
-- board, lineage standings, dynasty pages and the war ledger. The all-new third of
-- the Viking split (docs/Plan-Viking-GMCP.md §4b) - no MUSHclient or MIP ancestor,
-- so unlike its siblings the composers here read the assembled Guild.* snapshots
-- DIRECTLY; there is no vik-string translation layer.
--
-- Packages consumed: Guild.Roster (hird, gneeds, thralls, bonds, spy, rneeds,
-- train, thrall_follower, vfind, varang), Guild.Kingdom (grudges, standings, vrep,
-- diplo, dynasty_*, army*, campaign*), Guild.War (the battle ledger).
--
-- VERIFY LIVE: Guild.War fired exactly ONE payload in the 27 Aug capture - the
-- empty no-war shape. The War tab renders army/campaign from Guild.Kingdom and
-- shows the Guild.War numbers it has; the battle BOARD waits until a real war
-- sends real terrain/units to build against (plan §6).

local P = "plugin." .. scrye.id .. "."

-- ---------------------------------------------------------------- helpers
local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end
local function col(c, s) return "@{" .. c .. "}" .. esc(s) .. "@{}" end
local function S(x) return x == nil and "" or tostring(x) end
local function N(x) return tonumber(x) or 0 end

-- pad on the RAW string, colour after (markup characters are never drawn)
local function padesc(s, n)
  s = tostring(s or "")
  return esc(s .. string.rep(" ", math.max(0, n - #s)))
end

local function titlecase(s)
  return (s or ""):gsub("_", " "):gsub("(%a)([%w]*)", function(a, b) return a:upper() .. b end)
end

-- seconds -> "3d 4h" / "5h 12m" / "42m" / "30s"
local function fmt_span(secs)
  secs = N(secs)
  if secs >= 86400 then return string.format("%dd %dh", math.floor(secs / 86400), math.floor((secs % 86400) / 3600)) end
  if secs >= 3600  then return string.format("%dh %dm", math.floor(secs / 3600), math.floor((secs % 3600) / 60)) end
  if secs >= 60    then return string.format("%dm", math.floor(secs / 60)) end
  return secs .. "s"
end

-- standing words -> semantic tokens (same map the status panel's Holds tab uses)
local STANDCOL = {
  allied = "success", friendly = "success", cordial = "success",
  neutral = "dim", wary = "warning", unfriendly = "warning",
  hostile = "error", war = "error", enemy = "error", feud = "error",
}
local function standcol(w) return STANDCOL[tostring(w or ""):lower()] or "text" end

local function loyalcol(v)
  v = N(v)
  if v >= 4 then return "success" end
  if v <= 2 then return "warning" end
  return "text"
end

-- ------------------------------------------------------------- snapshots
local ROSTER, KINGDOM, WAR = {}, {}, {}

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

-- ------------------------------------------------------ report builders
local function mkadd(L)
  return function(s)
    s = tostring(s or "")
    if s:find("^%-%- ") and s:find(" %-%-$") then s = "@{accent,bold}" .. s .. "@{}" end
    L[#L + 1] = s
  end
end

-- --------------------------------------------------------------- Hird tab
local function build_hird()
  local L = {}
  local add = mkadd(L)
  local gn = T(ROSTER, "gneeds")
  add("-- Hird --")
  if gn.hird_cap ~= nil then
    add(string.format("Hird %s/%s   Garrison %s/%s   Stationed %s   City pool %s   On duty %s%s",
      S(gn.hird_count), S(gn.hird_cap), S(gn.garrisoned), S(gn.garrison_cap),
      S(gn.stationed), S(gn.city_pool), S(gn.on_duty),
      N(gn.wounded) > 0 and ("   " .. col("error", "wounded " .. S(gn.wounded))) or ""))
  else
    add("waiting for Guild.Roster...")
  end
  add("")
  local hird = T(ROSTER, "hird")
  if #hird == 0 then
    add("no hirdmadr on the roster")
  else
    local rows = {}
    for _, h in ipairs(hird) do rows[#rows + 1] = h end
    table.sort(rows, function(a, b)
      if N(a.loyalty) ~= N(b.loyalty) then return N(a.loyalty) > N(b.loyalty) end
      return S(a.name) < S(b.name)
    end)
    add(string.format("@{dim}%-22s %3s %-8s %5s %3s %-10s %s@{}",
      "Name", "Lvl", "Age", "A/D", "Loy", "Status", "Mode"))
    for _, h in ipairs(rows) do
      local champ = N(h.champ) > 0 and "@{warning,bold}*@{}" or " "
      local status = S(h.status):gsub("_", " ")
      add(champ .. padesc(S(h.name):sub(1, 21), 21) .. " "
        .. esc(string.format("%3s %-8s %2s/%-2s ", S(h.level), S(h.age), S(h.atk), S(h.def)))
        .. col(loyalcol(h.loyalty), string.format("%3s", S(h.loyalty))) .. " "
        .. col(status == "wounded" and "error" or "text", string.format("%-10s", status)) .. " "
        .. esc(S(h.mode)))
    end
    add("")
    add("@{dim}* champion   sorted most-loyal first@{}")
  end
  -- bond matrix (ids -> first names via the roster)
  local bonds = T(ROSTER, "bonds")
  if #bonds > 0 then
    add("")
    add("-- Bonds --")
    local name = {}
    for _, h in ipairs(hird) do
      name[S(h.id)] = (S(h.name):match("^(%S+)") or S(h.name))
    end
    local m, ids, seen = {}, {}, {}
    for _, b in ipairs(bonds) do
      local a, c, val = S(b.a), S(b.b), "T" .. S(b.tier)
      m[a] = m[a] or {}; m[a][c] = val
      m[c] = m[c] or {}; m[c][a] = val
      if not seen[a] then seen[a] = true; ids[#ids + 1] = a end
      if not seen[c] then seen[c] = true; ids[#ids + 1] = c end
    end
    table.sort(ids, function(x, y) return (tonumber(x) or 0) < (tonumber(y) or 0) end)
    local hdr = string.rep(" ", 11)
    for _, c in ipairs(ids) do hdr = hdr .. string.format("%-4s", c) end
    add(esc(hdr))
    for _, a in ipairs(ids) do
      local row = string.format("%-2s%-9s", a, (name[a] or ("#" .. a)):sub(1, 8))
      for _, c in ipairs(ids) do
        if a == c then row = row .. string.format("%-4s", "-")
        else row = row .. string.format("%-4s", (m[a] and m[a][c]) or ".") end
      end
      add(esc(row))
    end
  end
  return table.concat(L, "\n")
end

-- ------------------------------------------------------------ Recruit tab
local function build_recruit()
  local L = {}
  local add = mkadd(L)
  add("-- Recruit needs --")
  local rn = T(ROSTER, "rneeds")
  if #rn == 0 then
    add("nothing asks for a hire")
  else
    add("@{dim}what each post wants in a hirdmadr (stat + trait):@{}")
    local rows = {}
    for _, r in ipairs(rn) do rows[#rows + 1] = r end
    table.sort(rows, function(a, b)
      if S(a.stat) ~= S(b.stat) then return S(a.stat) < S(b.stat) end
      return S(a.target) < S(b.target)
    end)
    for _, r in ipairs(rows) do
      add(string.format("%-18s %-8s %s",
        titlecase(S(r.target)):sub(1, 18), S(r.stat), titlecase(S(r.trait))))
    end
  end
  local hall = T(ROSTER, "vfind_hall")
  if hall.tier ~= nil then
    add("")
    add(string.format("Hiring hall T%s   finds %s at a time", S(hall.tier), S(hall.max_finds)))
  end
  local tr = T(ROSTER, "train")
  if S(tr.name) ~= "" then
    add("")
    add(string.format("Training: %s (%s)  %s left", S(tr.name), S(tr.stat), fmt_span(tr.secs)))
  end
  local spy = T(ROSTER, "spy")
  if spy.tier ~= nil then
    add("")
    add("-- Spymaster --")
    if S(spy.mode) == "" and S(spy.village) == "" then
      add(string.format("idle (shadow house T%s)", S(spy.tier)))
    else
      add(string.format("%s %s%s", S(spy.mode), S(spy.village),
        N(spy.secs) > 0 and ("  " .. fmt_span(spy.secs) .. " left") or ""))
      if N(spy.sabpct) > 0 then add(string.format("sabotage %s%%", S(spy.sabpct))) end
    end
  end
  local nin, nout = #T(ROSTER, "varang_in"), #T(ROSTER, "varang_out")
  if nin + nout > 0 then
    add("")
    add(string.format("Varangians: %d serving here, %d abroad", nin, nout))
  end
  return table.concat(L, "\n")
end

-- ------------------------------------------------------------ Thralls tab
local function build_thralls()
  local L = {}
  local add = mkadd(L)
  local th = T(ROSTER, "thralls")
  add("-- Thralls --")
  local total = th.total
  local rows = {}
  for k, v in pairs(th) do
    if k ~= "total" then rows[#rows + 1] = { bldg = k, n = N(v) } end
  end
  if total == nil and #rows == 0 then
    add("waiting for Guild.Roster...")
  else
    add("Total " .. S(total))
    add("")
    table.sort(rows, function(a, b)
      if a.n ~= b.n then return a.n > b.n end
      return a.bldg < b.bldg
    end)
    local half = math.ceil(#rows / 2)
    for i = 1, half do
      local a, b = rows[i], rows[i + half]
      local left = string.format("%-18s %2d", titlecase(a.bldg):sub(1, 18), a.n)
      local right = b and string.format("   %-18s %2d", titlecase(b.bldg):sub(1, 18), b.n) or ""
      add(esc(left .. right))
    end
  end
  local f = T(ROSTER, "thrall_follower")
  add("")
  if S(f.name) ~= "" then
    add(string.format("Follower: %s (%s)  L%s  carry %s/%s",
      S(f.name), S(f.state), S(f.level), S(f.carry_used), S(f.carry_cap)))
  else
    add("@{dim}no thrall follower out@{}")
  end
  return table.concat(L, "\n")
end

-- ------------------------------------------------------------ Grudges tab
-- One row per lineage town: a town with a grudge shows its cooldown, one without
-- is READY - the raid planner's other half (3s-raid-gmcp picks by heat; this
-- board says who can be raided at all).
local function build_grudges()
  local L = {}
  local add = mkadd(L)
  add("-- Grudges (raid cooldowns) --")
  local gr = T(KINGDOM, "grudges")
  if #gr == 0 then
    add("no grudge data yet - waiting for Guild.Kingdom")
    return table.concat(L, "\n")
  end
  local rows = {}
  for _, g in ipairs(gr) do rows[#rows + 1] = g end
  table.sort(rows, function(a, b)
    if N(a.secs) ~= N(b.secs) then return N(a.secs) < N(b.secs) end
    return S(a.town) < S(b.town)
  end)
  add(string.format("@{dim}%-18s %10s@{}", "Town", "cools in"))
  for _, g in ipairs(rows) do
    local span = fmt_span(g.secs)
    local tok = N(g.secs) < 21600 and "warning" or "error"   -- under 6h: nearly ready
    add(padesc(S(g.town):sub(1, 18), 18) .. " " .. col(tok, string.format("%10s", span)))
  end
  add("")
  add("@{dim}towns not listed carry no grudge - ready to raid@{}")
  return table.concat(L, "\n")
end

-- ------------------------------------------------------------ Kingdom tab
local function build_kingdom()
  local L = {}
  local add = mkadd(L)
  add("-- Lineage standings --")
  local st = T(KINGDOM, "standings")
  if #st == 0 then add("waiting for Guild.Kingdom...")
  else
    local rows = {}
    for _, v in ipairs(st) do rows[#rows + 1] = v end
    table.sort(rows, function(a, b)
      if N(a.score) ~= N(b.score) then return N(a.score) > N(b.score) end
      return S(a.name) < S(b.name)
    end)
    for _, v in ipairs(rows) do
      local own = N(v.own) > 0 and "@{info,bold}<- yours@{}" or ""
      add(padesc(S(v.name):sub(1, 14), 14) .. " "
        .. col(standcol(v.label), string.format("%-9s", S(v.label)))
        .. esc(string.format(" %4s  ", S(v.score))) .. own)
    end
  end
  local vr = T(KINGDOM, "vrep")
  if #vr > 0 then
    add("")
    add("-- Trade reputation --")
    local rows = {}
    for _, v in ipairs(vr) do rows[#rows + 1] = v end
    table.sort(rows, function(a, b) return N(a.rep) > N(b.rep) end)
    for _, v in ipairs(rows) do
      local span = N(v.next_at) - N(v.start_at)
      local pct = span > 0 and math.floor((N(v.rep) - N(v.start_at)) * 100 / span) or 0
      pct = math.max(0, math.min(100, pct))
      add(string.format("%-14s R%s  %5s/%s  %3d%% to next",
        S(v.name):sub(1, 14), S(v.rank), S(v.rep), S(v.next_at), pct))
    end
  end
  local dp = T(KINGDOM, "diplo")
  if #dp > 0 then
    add("")
    add("-- Diplomacy --")
    for _, d in ipairs(dp) do
      local side = S(d.side) == "you" and col("success", "with you") or col("error", "against you")
      add(padesc(S(d.name):sub(1, 14), 14) .. " " .. esc(string.format("%4s  ", S(d.standing))) .. side)
    end
  end
  return table.concat(L, "\n")
end

-- ------------------------------------------------------------ Dynasty tab
local function build_dynasty()
  local L = {}
  local add = mkadd(L)
  add("-- Dynasty --")
  if S(KINGDOM.dynasty_house) == "" then
    add("waiting for Guild.Kingdom...")
    return table.concat(L, "\n")
  end
  add(string.format("House %s   of %s", S(KINGDOM.dynasty_house), S(KINGDOM.dynasty_realm)))
  add(string.format("Living %s / %s%s", S(KINGDOM.dynasty_living), S(KINGDOM.dynasty_cap),
    S(KINGDOM.dynasty_heir) ~= "" and ("   heir: " .. S(KINGDOM.dynasty_heir)) or "   " .. col("warning", "no heir named")))
  local sp = T(KINGDOM, "dynasty_spouse")
  if S(sp.name) ~= "" then
    add(string.format("Spouse %s of house %s (rank %s, age %s)",
      S(sp.name), S(sp.house), S(sp.rank), S(sp.age)))
  end
  local ch = T(KINGDOM, "dynasty_children")
  add("")
  add("-- Children --")
  if #ch == 0 then add("none")
  else
    for _, c in ipairs(ch) do
      if type(c) == "table" then
        add(string.format("%-16s age %s%s", S(c.name):sub(1, 16), S(c.age),
          S(c.schooling) ~= "" and ("  schooling: " .. S(c.schooling)) or ""))
      else
        add(S(c))
      end
    end
  end
  local sc = T(KINGDOM, "dynasty_schooling")
  if #sc > 0 then
    add("")
    add("-- Schooling --")
    for _, c in ipairs(sc) do
      if type(c) == "table" then
        add(string.format("%-16s %s%s", S(c.name):sub(1, 16), S(c.stat or c.school),
          N(c.secs) > 0 and ("  " .. fmt_span(c.secs) .. " left") or ""))
      else
        add(S(c))
      end
    end
  end
  return table.concat(L, "\n")
end

-- ---------------------------------------------------------------- War tab
local function build_war()
  local L = {}
  local add = mkadd(L)
  local ar = T(KINGDOM, "army")
  add("-- Army --")
  if ar.unit_cap == nil then add("waiting for Guild.Kingdom...")
  else
    add(string.format("Units %s/%s   Conscripts %s   Levy %s%%   Manpower cap %s",
      S(ar.unit_count), S(ar.unit_cap), S(ar.conscripts), S(ar.levy_rate), S(ar.cap)))
    local units = T(KINGDOM, "army_units")
    for _, u in ipairs(units) do
      if type(u) == "table" then
        add(string.format("  %-14s %s", S(u.name or u.kind):sub(1, 14), S(u.size or u.count)))
      end
    end
  end
  local cp = T(KINGDOM, "campaign")
  add("")
  add("-- Campaign --")
  if N(cp.active) > 0 then
    add(string.format("%s against %s   turn %s   march %s",
      S(cp.mode), S(cp.town), S(cp.turn), fmt_span(cp.march_eta)))
    add(string.format("upkeep/turn: %sd %s food %s mead %s iron %s tools",
      S(cp.upkeep_daler), S(cp.upkeep_food), S(cp.upkeep_mead), S(cp.upkeep_iron), S(cp.upkeep_tools)))
    add(string.format("spoils so far: %sd, %s deeds, %s war points",
      S(cp.spoils_daler), S(cp.spoils_deeds), S(cp.spoils_wpts)))
  else
    add("no campaign in the field")
  end
  local pr = T(KINGDOM, "campaign_prison")
  if pr.capacity ~= nil and (N(pr.held) > 0 or N(pr.pending) > 0) then
    add(string.format("Prison %s/%s held%s", S(pr.held), S(pr.capacity),
      N(pr.pending) > 0 and ("   " .. col("warning", S(pr.pend_name) .. " pending")) or ""))
  end
  add("")
  add("-- War --")
  if N(WAR.active) > 0 then
    add(string.format("WAR against %s   phase %s   turn %s   points %s (spent %s)",
      S(WAR.target), S(WAR.phase), S(WAR.turn), S(WAR.war_points), S(WAR.spent)))
    add(string.format("wall T%s   budget %s   reserve %d companies",
      S(WAR.wall_tier), S(WAR.budget), #T(WAR, "reserve")))
    add("")
    add(col("warning", "the battle board is not built yet - it waits on a real war's"))
    add(col("warning", "terrain/units payloads (the capture only ever saw the empty shape)"))
  else
    add("no war declared")
  end
  return table.concat(L, "\n")
end

-- ------------------------------------------------------------ the flush
local BUILDERS = {
  hird    = function() scrye.setState(P .. "hird", build_hird()) end,
  recruit = function() scrye.setState(P .. "recruit", build_recruit()) end,
  thralls = function() scrye.setState(P .. "thralls", build_thralls()) end,
  grudges = function() scrye.setState(P .. "grudges", build_grudges()) end,
  kingdom = function() scrye.setState(P .. "kingdom", build_kingdom()) end,
  dynasty = function() scrye.setState(P .. "dynasty", build_dynasty()) end,
  war     = function() scrye.setState(P .. "war", build_war()) end,
}

flush = function()
  flush_pending = false
  for sec in pairs(dirty) do
    local b = BUILDERS[sec]
    if b then pcall(b) end
  end
  dirty = {}
end

local ROSTER_TABS  = { "hird", "recruit", "thralls" }
local KINGDOM_TABS = { "grudges", "kingdom", "dynasty", "war" }

-- ---------- Guild.* page assembler (shared snippet; docs/Plan-Viking-GMCP.md §3) ----------
-- Guild packages arrive paged: {page=i, pages=N, full=1?} with list keys split across
-- pages. gasm(pkg, on_snap) subscribes to the package and calls on_snap(snap) with the
-- merged snapshot each time a burst completes:
--   * a message with no "pages" is unpaged: its keys merge into the snapshot directly;
--   * a burst whose pages carry full=1 REPLACES the paged keys of the snapshot
--     (keys only ever seen on the unpaged stream survive it); a burst without
--     full merges - keys it never mentions keep their last value;
--   * a (non-empty) array key met on several pages of one burst CONCATENATES;
--     everything else is last-write;
--   * page/pages/full/guild are bookkeeping, never data;
--   * a page that doesn't continue the current burst (different pages count, or page
--     not past the last one seen) abandons the stale burst and starts fresh.
local function gasm(pkg, on_snap)
  local snap, burst, bfull, expect, last_page = {}, nil, false, nil, 0
  local paged_keys = {}     -- keys that have ever arrived in a paged burst
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

gasm("Guild.Roster", function(snap)
  ROSTER = snap
  for _, s in ipairs(ROSTER_TABS) do dirty[s] = true end
  schedule_flush()
end)

gasm("Guild.Kingdom", function(snap)
  KINGDOM = snap
  for _, s in ipairs(KINGDOM_TABS) do dirty[s] = true end
  schedule_flush()
end)

gasm("Guild.War", function(snap)
  WAR = snap
  dirty.war = true
  schedule_flush()
end)

-- --------------------------------------------------------------- aliases
-- vgrudge: the grudge board in the output window - the raid-planning glance
scrye.addAlias{ pattern = "^vgrudge$", regex = true, run = function()
  dirty.grudges = true; flush()
  for line in (scrye.getState(P .. "grudges") or ""):gmatch("[^\n]+") do
    scrye.print("@{#B99EE9,bold}[kingdom]@{} " .. line)
  end
end }

-- --------------------------------------------------------------- panel
scrye.addPanel{
  title = "Viking Kingdom",
  width = 460,
  accent = "#B99EE9",          -- signature: dynasty violet (validated accent set)
  tabs = {
    { title = "Hird",    widgets = { { type = "text", bind = P .. "hird" } } },
    { title = "Recruit", widgets = { { type = "text", bind = P .. "recruit" } } },
    { title = "Thralls", widgets = { { type = "text", bind = P .. "thralls" } } },
    { title = "Grudges", widgets = { { type = "text", bind = P .. "grudges" } } },
    { title = "Kingdom", widgets = { { type = "text", bind = P .. "kingdom" } } },
    { title = "Dynasty", widgets = { { type = "text", bind = P .. "dynasty" } } },
    { title = "War",     widgets = { { type = "text", bind = P .. "war" } } },
  },
}

-- ------------------------------------------------------------------ init
for _, s in ipairs(ROSTER_TABS)  do dirty[s] = true end
for _, s in ipairs(KINGDOM_TABS) do dirty[s] = true end
flush()
