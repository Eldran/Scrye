-- 3S Auto-Raid (GMCP) — the raid dispatcher rebuilt on the Guild.* GMCP feed.
--
-- Lineage: a straight port of 3s-raid 1.x (the MIP classic, itself a port of
-- MUSHclient ThreeS_Raid). The strategy core — convoy/reserve/keep/hold rules,
-- auto-target with the calm-pool tie-break, the 30 s pass throttle, the clickable
-- heat table — is UNCHANGED. Only the feed layer moved:
--   * vik.ships      -> Guild.Fleet ships[]            (name/state per longship)
--                       (Guild.Voyage longship[] since 17 Sep 2026 - see the town
--                        lists block below for what else moved)
--   * vik.buildings  -> Guild.City buildings[]         (dock tier -> capacity, tier*2)
--   * vik.heat       -> Guild.City heat[]              (one slot per home town)
--   * vik.rtargets   -> Guild.Fleet rtargets_lineage[] (home towns, "Town:good:good")
--                       + rtargets_historical[]        (foreign towns)
-- and the redispatch-on-return hook is the Guild.Fleet burst callback instead of
-- scrye.watch("vik.ships").
--
-- Guild.* packages arrive PAGED — see the assembler below and the plan doc
-- (docs/Plan-Viking-GMCP.md §3) for the burst semantics this relies on.
--
-- Field notes from the 27 Aug capture (gmcp-fields-Goran-20260827.md):
--   * heat[] and rtargets_lineage[] were both 13 long; they are paired BY INDEX,
--     the same convention the MIP feed used. VERIFY LIVE on first soak: if a town's
--     displayed heat looks wrong, the server's heat order differs from the
--     rtargets_lineage order and the pairing needs its own lookup.
--   * ships[].held was always 0 in the capture; its semantics are unknown, so only
--     state=="docked" admits a ship to the raid pool (as the MIP port did).
--   * Guild.Kingdom carries per-town grudge cooldowns and Guild.Fleet a raidlog —
--     both planned as later upgrades once this feed layer has soaked live (plan §4a).
--
-- 2.1.0: BOTH target groups in the town table (foreign/historical towns were fed
-- but never shown), and the auto-target pool is a setting: 'araid pool home'
-- (default, the classic lowest-heat behaviour) or 'araid pool foreign' - the feed
-- carries no heat for foreign towns, so that pool picks at random and the hold
-- timer rotates it. Switching pools drops the current lock. VERIFY LIVE: the
-- first foreign auto-dispatch - the same 'vlongship raid/convoy <town>' commands
-- are assumed to accept historical towns (araid targets has always listed them
-- as valid raid targets).

local AR_INTERVAL = 30   -- seconds between raid passes
local AR_MARGIN   = 2    -- heat margin for the auto-target pool

local SP = "plugin." .. scrye.id .. "."

-- ---------- helpers ----------

-- "[raid]" tag, restoring the original's orange ColourNote tag
local function note(s) scrye.print("@{#FD2083,bold}[raid]@{} " .. s) end

-- town names come off the MUD: escape "@" so they can't be read as markup
local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end

local function trim(s) return (s or ""):gsub("^%s+", ""):gsub("%s+$", "") end

-- ---------- Guild.* page assembler (shared snippet; docs/Plan-Viking-GMCP.md §3) ----------
-- Guild packages arrive paged: {page=i, pages=N, full=1?} with list keys split across
-- pages (ships span several Fleet pages). gasm(pkg, on_snap) subscribes to the package
-- and calls on_snap(snap) with the merged snapshot each time a burst completes:
--   * a message with no "pages" is unpaged: its keys merge into the snapshot directly;
--   * a burst whose pages carry full=1 REPLACES the paged keys of the snapshot
--     (keys only ever seen on the unpaged stream survive it); a burst without
--     full merges — keys it never mentions keep their last value (the Guild.State
--     "pointless page" lesson);
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
      on_snap(snap)
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
        -- full replaces the PAGED keys; keys that only ever arrive on the unpaged
        -- stream (City's dcycle/patrol/nexttick ride outside the bursts) survive
        local keep = {}
        for k, v in pairs(snap) do if not paged_keys[k] then keep[k] = v end end
        snap = keep
      end
      for k, v in pairs(burst) do snap[k] = v; paged_keys[k] = true end
      burst, bfull, expect, last_page = nil, false, nil, 0
      on_snap(snap)
    end
  end)
end

-- the merged snapshots (empty until the first complete burst of each package)
local FLEET, CITY, VOY, KING = {}, {}, {}, {}
local raidlog_towns = {}   -- Raids tab "by town" row index -> town, for clicks
local raidlog_rows = {}    -- Raids tab log row index -> town

-- ---------- the town lists (17 Sep 2026: Guild.Fleet is gone) ----------
-- The server stopped sending Guild.Fleet in September 2026 (9,700 messages of the
-- 17 Sep capture, not one). Its ships ride Guild.Voyage as `longship` now, with the
-- same fields; its two town lists went with nothing to replace them, so:
--   * HOME towns come from Guild.Kingdom's grudges (town + lineage number - the
--     same order heat[] is indexed in, town for town against the old list), laid
--     over a baked-in default of the 13 lineage cities in lineage order;
--   * FOREIGN towns have no GMCP source at all. A baked-in default (the 24 of
--     `vlongship targets` on 19 Sep 2026) and a text scan of that same listing
--     ('araid targets' sends it; the frame lines are read as they come) keep the
--     list current, persisted across restarts.
local HOME_DEFAULT = { "Lodbrok's Hold", "Eiriksby", "Imaird", "Holmgard", "Hafrfjord",
  "Uppsala", "Borgarfjord", "Vestergotland", "Sverkersby", "Ericsgard", "Birka", "Lejre",
  "Nidaros" }
local FOREIGN_DEFAULT = { "Paris", "Lindisfarne", "Hamwic", "Dorestad", "Rouen", "Nantes",
  "Quentovic", "Repton", "Seville", "Hedeby", "Jorvik", "Dublin", "Noirmoutier", "Poitiers",
  "Sandwich", "Groningen", "Antwerp", "Kaupang", "Ribe", "Waterford", "Winchester",
  "Trondheim", "Bordeaux", "Utrecht" }
local home_lin = {}       -- lineage number -> town, learned from grudges (overlay)
-- 23 Sep 2026: Guild.Fleet is back, and with it both lists - rtargets_lineage (home,
-- "Town:good:good" in lineage order) on its full burst, rtargets_historical (foreign)
-- on the delta burst. The two never ride the same burst, and a full burst replaces
-- the paged keys, so each is kept here as it arrives rather than read off FLEET. When
-- the feed has a list it wins over the scan and the default; grudges still overlay.
local fleet_list = { home = nil, foreign = nil }
local scanned = { home = nil, foreign = nil }   -- from the last `vlongship targets` scan
do
  local function load(k)
    local raw = scrye.store.get("towns_" .. k)
    if not raw or raw == "" then return nil end
    local out = {}
    for t in raw:gmatch("[^\n]+") do out[#out + 1] = t end
    return out[1] and out or nil
  end
  scanned.home, scanned.foreign = load("home"), load("foreign")
end

-- the home towns in lineage order (1 = Lodbrok's Hold): grudges overlay the scan
-- overlays the default, so a renamed or reordered server heals itself
local function home_towns()
  local base = fleet_list.home or scanned.home or HOME_DEFAULT
  local out = {}
  for i, t in ipairs(base) do out[i] = home_lin[i] or t end
  for lin, t in pairs(home_lin) do if lin > #out then out[lin] = t end end
  local list = {}
  for i = 1, #out do if out[i] then list[#list + 1] = out[i] end end
  return list
end

-- ---------- settings (persisted via scrye.store; armed always starts OFF) ----------

local ar = {
  on          = false,                                        -- always OFF on load (safety)
  target      = scrye.store.get("target") or "",
  ships       = scrye.store.get("ships") or "2",              -- "all" or a number (kept as string)
  keep        = tonumber(scrye.store.get("keep")) or 0,       -- always leave this many docked
  reserve     = scrye.store.get("reserve") or "",             -- named ship to always keep docked (voyages)
  convoy      = scrye.store.get("convoy") == "1",
  auto_target = scrye.store.get("autotarget") == "1",         -- pick a town from the pool below
  pool        = scrye.store.get("pool") or "home",            -- auto-target pool: home | foreign
  hold        = tonumber(scrye.store.get("hold")) or 60,      -- seconds to raid one town before rotating
  last        = 0,
  locked_target = nil,
  locked_at     = nil,
}

-- monotonic seconds since load (no os.time in the sandbox)
local clock = 0
scrye.every(1, function() clock = clock + 1 end)

-- ---------- phone notifications (plugin.<id>.notify convention) ----------
-- Two sources, both default OFF (raiding is routine; the phone is for exceptions):
--   fleet  - ships came back to dock (useful un-armed too: a manual voyage returned)
--   send   - every auto-dispatch (convoy/raid sent), for watching the bot from afar
local nf = {
  fleet = scrye.store.get("notify_fleet") == "1",
  send  = scrye.store.get("notify_send") == "1",
}
local last_docked = nil   -- previous docked count; nil = no baseline yet

local function publish_notify_state()
  scrye.setState(SP .. "notify", table.concat({
    string.format("Fleet returns\tships arriving back in dock\t%s\taraid notify fleet %s",
      nf.fleet and "on" or "off", nf.fleet and "off" or "on"),
    string.format("Dispatches\teach convoy/raid the bot sends\t%s\taraid notify send %s",
      nf.send and "on" or "off", nf.send and "off" or "on"),
  }, "\n"))
end

local connected = true
scrye.onConnect(function() connected = true end)
scrye.onDisconnect(function() connected = false end)

-- ---------- feed reading (snapshots, not strings) ----------

-- docked, available longships (by name), dock capacity (dock tier * 2), raiding count
local function fleet()
  local avail, raiding = {}, 0
  -- Guild.Voyage `longship` since 17 Sep 2026; Guild.Fleet `ships` before it
  for _, s in ipairs(VOY.longship or FLEET.ships or {}) do
    if s.state == "docked" then avail[#avail + 1] = tostring(s.name or "")
    elseif s.state == "raiding" then raiding = raiding + 1 end
  end
  local dock = 1
  for _, b in ipairs(CITY.buildings or {}) do
    if b.id == "dock" then dock = tonumber(b.tier) or 1 end
  end
  return avail, dock * 2, raiding
end

-- heat by home town: Guild.City heat[] values paired by index with the lineage
-- order (heat[i] is lineage i's town - the pairing Guild.Fleet's rtargets_lineage
-- used, and the grudges' lineage numbers confirm)
local function heat_of()
  local heats = CITY.heat or {}
  local map, order = {}, {}
  for i, t in ipairs(home_towns()) do
    order[#order + 1] = t
    map[t] = tonumber(heats[i]) or 0
  end
  return map, order
end

-- the home town with the least heat - deterministic, for display
local function lowest_heat_town()
  local map, order = heat_of()
  local best, bh
  for _, t in ipairs(order) do
    local h = map[t] or 0
    if not bh or h < bh then best, bh = t, h end
  end
  return best, bh
end

-- the foreign (historical) towns - the last `vlongship targets` scan, else the
-- baked-in default. No feed carries heat for them (heat[] is exactly as long as
-- the lineage list), so foreign targeting cannot be heat-guided; see pick_raid_town.
local function foreign_towns()
  local out = {}
  for _, t in ipairs(fleet_list.foreign or scanned.foreign or FOREIGN_DEFAULT) do out[#out + 1] = t end
  return out
end

-- ---------- `vlongship targets` scan ----------
-- The listing is framed "-~*  <town>  <good> & <good>  [<state>]  *~-" under two
-- section headers; a lineage row ends in a heat word, a historical row does not.
-- Both lists are taken whole from one listing and persisted; a listing cut short
-- (disconnect mid-frame) is dropped rather than half-applied.
local scan_mode, scan_buf = nil, nil
local function targets_line(inner)
  inner = trim(inner)
  if inner:find("^Historical Targets") then scan_mode = "foreign"; scan_buf = scan_buf or { home = {}, foreign = {} } return end
  if inner:find("^Lineage Cities") then scan_mode = "home"; scan_buf = scan_buf or { home = {}, foreign = {} } return end
  if inner:find("^Runestones") then
    if scan_buf and scan_buf.foreign[1] then
      scanned.foreign = scan_buf.foreign
      scrye.store.set("towns_foreign", table.concat(scan_buf.foreign, "\n"))
    end
    if scan_buf and scan_buf.home[1] then
      scanned.home = scan_buf.home
      scrye.store.set("towns_home", table.concat(scan_buf.home, "\n"))
    end
    if scan_buf then
      note(string.format("targets read: %d home, %d foreign", #scan_buf.home, #scan_buf.foreign))
    end
    scan_mode, scan_buf = nil, nil
    return true            -- the lists changed: the caller republishes the table
  end
  if not scan_mode or not scan_buf then return end
  -- "<town>  <good>  & <good>  [<state>]": the town is everything before the first
  -- run of two or more spaces (multi-word names are single-spaced)
  local town = inner:match("^(.-)%s%s+%S")
  if town and town ~= "" and inner:find("&", 1, true) then
    local list = scan_buf[scan_mode]
    list[#list + 1] = town
  end
end

-- pick a raid target from the chosen pool.
--   home:    random among the towns within AR_MARGIN heat of the lowest, so it
--            spreads across the calm towns instead of always hammering the first tied one
--   foreign: random among all foreign towns - no heat rides the feed for them, so an
--            even spread is the only honest strategy (the hold timer still rotates it)
local function pick_raid_town()
  if ar.pool == "foreign" then
    local f = foreign_towns()
    if #f == 0 then return nil end
    return f[math.random(#f)], nil
  end
  local map, order = heat_of()
  if #order == 0 then return nil end
  local minh
  for _, t in ipairs(order) do local h = map[t] or 0; if not minh or h < minh then minh = h end end
  local pool = {}
  for _, t in ipairs(order) do if (map[t] or 0) <= minh + AR_MARGIN then pool[#pool + 1] = t end end
  if #pool == 0 then return nil end
  local pick = pool[math.random(#pool)]
  return pick, map[pick]
end

-- multi-word town names need a short word for vlongship (single-word names work as-is)
local RAIDTOWN = { ["lodbrok's hold"] = "lodbrok" }
local function raid_town(t) return RAIDTOWN[(t or ""):lower()] or t end

-- ---------- published state / panel ----------

local function publish()
  local ok = pcall(function()
    local avail, maxs = fleet()
    local tgt
    if ar.auto_target then
      -- foreign pool: nothing to predict before the first pick (no heat to rank by)
      local shown = (ar.locked_target and ar.locked_target ~= "") and ar.locked_target
                    or (ar.pool == "foreign" and "?" or (lowest_heat_town() or "?"))
      tgt = "auto (" .. ar.pool .. "): " .. shown
    else
      tgt = ar.target ~= "" and ar.target or "(none)"
    end
    scrye.setState(SP .. "armed",  ar.on and "ON" or "OFF")
    scrye.setState(SP .. "target", tgt)
    scrye.setState(SP .. "docked", string.format("%d / %d", #avail, maxs))

    -- fleet-return notify: an INCREASE in docked ships means something came home.
    -- The first pass only takes a baseline, so loading with a full dock stays quiet.
    if last_docked ~= nil and #avail > last_docked and nf.fleet then
      local back = #avail - last_docked
      scrye.notify(string.format("%d ship%s back in dock (%d/%d available)",
        back, back == 1 and "" or "s", #avail, maxs))
    end
    last_docked = #avail
    scrye.setState(SP .. "status", string.format(
      "pool %s - convoy %s - ships %s - keep %d - hold %ds - reserve %s",
      ar.pool, ar.convoy and "on" or "off", tostring(ar.ships), ar.keep,
      ar.hold or 60, ar.reserve ~= "" and ar.reserve or "none"))
    -- Per-town table, home (calmest first) then foreign — and it IS the target
    -- picker: each town name is a click link that runs 'araid target <town>' (the
    -- miniwindow's colour coding comes back as theme tokens: green calm pool, blue
    -- live target). Foreign towns carry NO heat in the feed, so their column is '-'
    -- and 'calm' never marks them; the auto pool's section header is highlighted.
    local map, order = heat_of()
    local foreign = foreign_towns()
    local hl = {}
    local cur
    if ar.auto_target then
      cur = (ar.locked_target and ar.locked_target ~= "") and ar.locked_target
            or (ar.pool ~= "foreign" and lowest_heat_town() or nil)
    elseif ar.target ~= "" then
      cur = ar.target
    end
    local function town_row(t, heatcol, mark)
      -- pad by the RAW length (markup characters are not drawn), inside the link
      local padded = esc(t) .. string.rep(" ", math.max(0, 18 - #t))
      return string.format("@{accent,click=araid target %s}%s@{} %5s%s", t, padded, heatcol, mark)
    end
    local function section(title, is_pool)
      hl[#hl + 1] = string.format("%s%-18s %5s%s%s",
        is_pool and "@{warning,bold}" or "@{dim}", title, "Heat",
        is_pool and "  <- auto pool" or "", "@{}")
    end
    if #order == 0 and #foreign == 0 then
      hl[1] = "no target data yet"
    else
      if #order > 0 then
        local minh
        for _, t in ipairs(order) do
          local h = map[t] or 0
          if not minh or h < minh then minh = h end
        end
        local picks = {}
        for _, t in ipairs(order) do picks[#picks + 1] = t end
        table.sort(picks, function(x, y)
          local hx, hy = map[x] or 0, map[y] or 0
          if hx ~= hy then return hx < hy end
          return x < y
        end)
        section("Home", ar.pool ~= "foreign")
        for _, t in ipairs(picks) do
          local h = map[t] or 0
          local mark = ""
          if cur and t:lower() == tostring(cur):lower() then mark = "  @{info}<- target@{}"
          elseif h <= minh + AR_MARGIN then mark = "  @{success}calm@{}" end
          hl[#hl + 1] = town_row(t, string.format("%5d", h), mark)
        end
      end
      if #foreign > 0 then
        if #hl > 0 then hl[#hl + 1] = "" end
        section("Foreign", ar.pool == "foreign")
        for _, t in ipairs(foreign) do
          local mark = (cur and t:lower() == tostring(cur):lower()) and "  @{info}<- target@{}" or ""
          hl[#hl + 1] = town_row(t, "    -", mark)
        end
      end
    end
    scrye.setState(SP .. "heat", table.concat(hl, "\n"))

    -- The Raids tab (20 Sep): Guild.Fleet's raidlog - one record per raid (ship, target,
    -- daler, thralls, ships lost), the goods it brought joined by idx from raidlog_goods.
    -- Server order kept; a total line; the same log folded by town and by ship, so "which
    -- town pays" is a glance. Town rows and log rows are the target picker too.
    do
      local log = type(FLEET.raidlog) == "table" and FLEET.raidlog or {}
      local goods = {}
      for _, g in ipairs(type(FLEET.raidlog_goods) == "table" and FLEET.raidlog_goods or {}) do
        local i = tonumber(g.idx)
        if i then
          goods[i] = goods[i] or {}
          goods[i][#goods[i] + 1] = tostring(g.amount or "?") .. " " .. tostring(g.good or "?"):gsub("_", " ")
        end
      end
      local rows, total, thralls, lost = {}, 0, 0, 0
      local by_town, by_ship, towns, ships = {}, {}, {}, {}
      raidlog_rows = {}
      for _, r in ipairs(log) do
        local town, ship = tostring(r.target or "?"), tostring(r.ship or "?")
        local d = tonumber(r.daler) or 0
        total = total + d
        thralls = thralls + (tonumber(r.thralls) or 0)
        lost = lost + (tonumber(r.lost) or 0)
        raidlog_rows[#raidlog_rows + 1] = town
        rows[#rows + 1] = string.format("%s\t%s\t%d\t%s", esc(ship):sub(1, 10), esc(town):sub(1, 14), d,
          esc(table.concat(goods[tonumber(r.idx) or -1] or {}, ", ")))
        if not by_town[town] then by_town[town] = { n = 0, d = 0 } ; towns[#towns + 1] = town end
        by_town[town].n = by_town[town].n + 1 ; by_town[town].d = by_town[town].d + d
        if not by_ship[ship] then by_ship[ship] = { n = 0, d = 0 } ; ships[#ships + 1] = ship end
        by_ship[ship].n = by_ship[ship].n + 1 ; by_ship[ship].d = by_ship[ship].d + d
      end
      scrye.setState(SP .. "raidlog", #rows > 0 and table.concat(rows, "\n") or "(no raids logged - Guild.Fleet's raidlog is empty or not here yet)\t\t\t")
      scrye.setState(SP .. "raidsum", #log > 0 and string.format("%d raid(s): %d daler (%d avg), %d thrall(s), %d ship(s) lost",
        #log, total, total // #log, thralls, lost) or "no raids logged")
      table.sort(towns, function(a, b)
        local ta, tb = by_town[a], by_town[b]
        if ta.d ~= tb.d then return ta.d > tb.d end
        return a < b
      end)
      table.sort(ships, function(a, b)
        local ta, tb = by_ship[a], by_ship[b]
        if ta.d ~= tb.d then return ta.d > tb.d end
        return a < b
      end)
      raidlog_towns = towns
      local trows, srows = {}, {}
      for _, t in ipairs(towns) do
        local e = by_town[t]
        trows[#trows + 1] = string.format("%s\t%d\t%d\t%d", esc(t):sub(1, 14), e.n, e.d // e.n, e.d)
      end
      for _, sh in ipairs(ships) do
        local e = by_ship[sh]
        srows[#srows + 1] = string.format("%s\t%d\t%d\t%d", esc(sh):sub(1, 12), e.n, e.d // e.n, e.d)
      end
      scrye.setState(SP .. "raidtowns", #trows > 0 and table.concat(trows, "\n") or "\t\t\t")
      scrye.setState(SP .. "raidships", #srows > 0 and table.concat(srows, "\n") or "\t\t\t")
    end

    -- seed the panel's input fields with the current settings
    scrye.setState(SP .. "v_target",  ar.target)
    scrye.setState(SP .. "v_ships",   tostring(ar.ships))
    scrye.setState(SP .. "v_keep",    tostring(ar.keep))
    scrye.setState(SP .. "v_hold",    tostring(ar.hold))
    scrye.setState(SP .. "v_reserve", ar.reserve ~= "" and ar.reserve or "none")
  end)
  if not ok then scrye.setState(SP .. "status", "feed parse error") end
end

-- (HUD panel is defined at the end, after the config functions it calls.)

-- ---------- core raid pass ----------

local function auto_raid_tick()
  if not ar.on then return end
  if not connected then return end
  local now = clock
  if now - (ar.last or 0) < AR_INTERVAL then return end
  ar.last = now

  local avail, maxs = fleet()

  -- hold back the named voyage ship: build the raid pool from docked ships minus that one.
  local pool, reserved_docked = {}, false
  for _, s in ipairs(avail) do
    if ar.reserve ~= "" and s:lower() == ar.reserve:lower() then reserved_docked = true
    else pool[#pool + 1] = s end
  end

  -- only act (and only re-check heat / switch town) when we actually have ships to send out.
  -- If the whole fleet is still out raiding, bail here WITHOUT touching the target.
  local usable = #pool - (ar.keep or 0)
  if usable < 1 then return end
  local want = (ar.ships == "all") and maxs or (tonumber(ar.ships) or 2)
  want = math.min(want, usable)
  if want < 1 then return end

  -- we are about to dispatch, so now is the moment to (re)pick the town: refresh to a fresh
  -- low-heat town if the hold has elapsed, otherwise keep the current one.
  local target = ar.target
  if ar.auto_target then
    if not ar.locked_target or ar.locked_target == "" or not ar.locked_at
       or (now - ar.locked_at) >= (ar.hold or 60) then
      target = pick_raid_town() or ""
      ar.locked_target = target
      ar.locked_at = now
    else
      target = ar.locked_target
    end
  end
  if target == "" then return end

  -- convoy picks ships by count (the game chooses which), so it can't protect a named ship.
  -- When the reserved ship is docked, dispatch by name from the pool instead.
  local how = ar.auto_target
    and (ar.pool == "foreign" and " (auto foreign)" or " (lowest heat)") or ""
  if ar.convoy and want >= 2 and not reserved_docked then
    scrye.send(string.format("vlongship convoy %d %s", want, raid_town(target)))
    local msg = string.format("convoy of %d -> %s%s", want, target, how)
    note(msg)
    if nf.send then scrye.notify(msg) end
  else
    for i = 1, want do scrye.send(string.format("vlongship raid %s %s", pool[i], raid_town(target))) end
    local msg = string.format("%d ship%s -> %s%s", want, want == 1 and "" or "s", target, how)
    note(msg)
    if nf.send then scrye.notify(msg) end
  end
end

local function driver()
  local ok, err = pcall(auto_raid_tick)
  if not ok then note("raid tick error: " .. tostring(err)) end
  publish()
end

-- ---------- commands ----------

local function ar_status()
  local tgt
  if ar.auto_target then
    tgt = (ar.pool == "foreign" and "foreign towns" or "lowest-heat home town")
          .. ", rotate " .. (ar.hold or 60) .. "s"
  else
    tgt = ar.target ~= "" and ar.target or "(none)"
  end
  note(string.format("auto-raid %s | target %s | ships %s | keep %d | voyage ship %s | convoy %s",
    ar.on and "ON" or "OFF", tgt, tostring(ar.ships), ar.keep,
    ar.reserve ~= "" and ar.reserve or "(none)", ar.convoy and "yes" or "no"))
end

-- list the valid raid targets carried in the feed (lineage = home, historical = foreign)
local function ar_list_targets()
  local function names(list)
    local out = {}
    for _, e in ipairs(list or {}) do out[#out + 1] = tostring(e):match("^([^:]+)") or tostring(e) end
    return out
  end
  local home    = home_towns()
  local foreign = foreign_towns()
  if #home > 0 then note("Home: " .. table.concat(home, ", ")) end
  if #foreign > 0 then note("Foreign: " .. table.concat(foreign, ", ")) end
  note(string.format("(%s; 'vlongship targets' refreshes both lists - sending it now)",
    fleet_list.foreign and "foreign list from Guild.Fleet"
      or scanned.foreign and "foreign list from the last listing scan" or "foreign list is the built-in default"))
  scrye.send("vlongship targets")
end

local function ar_config(rest)
  local low = trim(rest or ""):lower()
  if low == "targets" then ar_list_targets() return end
  if low == "heat" then
    publish()
    for line in scrye.getState(SP .. "heat"):gmatch("[^\n]+") do note(line) end
    return
  end
  -- NB: the armed flag is deliberately NOT persisted -- the plugin always loads
  -- disarmed (see `ar.on` above), so writing it to the store would be a dead write.
  local nk, nv = low:match("^notify%s+(%w+)%s+(%w+)$")
  if nk then
    if nf[nk] == nil or (nv ~= "on" and nv ~= "off") then
      note("usage: araid notify fleet|send on|off") return
    end
    nf[nk] = (nv == "on")
    scrye.store.set("notify_" .. nk, nf[nk] and "1" or "0")
    note("phone notify '" .. nk .. "': " .. nv)
    publish_notify_state()
    return
  end
  if low == "notify" then
    note(string.format("phone notify: fleet %s, send %s (araid notify fleet|send on|off)",
      nf.fleet and "on" or "off", nf.send and "on" or "off"))
    return
  end
  if low == "on" then ar.on = true
  elseif low == "off" then ar.on = false
  elseif low == "convoy on" then ar.convoy = true; scrye.store.set("convoy", "1")
  elseif low == "convoy off" then ar.convoy = false; scrye.store.set("convoy", "0")
  elseif low == "all" or low == "ships all" then ar.ships = "all"; scrye.store.set("ships", "all")
  elseif low == "auto on"  then ar.auto_target = true;  scrye.store.set("autotarget", "1")
  elseif low == "auto off" then ar.auto_target = false; scrye.store.set("autotarget", "0")
  elseif low == "pool home" or low == "pool foreign" then
    -- switching pools mid-run drops the lock, so the next pass picks from the new
    -- pool instead of riding out the hold on a town from the old one
    ar.pool = low:match("^pool%s+(%w+)$")
    scrye.store.set("pool", ar.pool)
    ar.locked_target, ar.locked_at = nil, nil
  else
    local n  = low:match("^ships%s+(%d+)$")
    local k  = low:match("^keep%s+(%d+)$")
    local hd = low:match("^hold%s+(%d+)$")
    if n then ar.ships = n; scrye.store.set("ships", n)
    elseif k then ar.keep = tonumber(k); scrye.store.set("keep", k)
    elseif hd then ar.hold = tonumber(hd); scrye.store.set("hold", hd)
    elseif rest:match("^%s*[Tt][Aa][Rr][Gg][Ee][Tt]%s+") then
      ar.target = trim(rest:gsub("^%s*[Tt][Aa][Rr][Gg][Ee][Tt]%s+", ""))
      scrye.store.set("target", ar.target)
    elseif rest:match("^%s*[Rr][Ee][Ss][Ee][Rr][Vv][Ee]%s+") then
      local ship = trim(rest:gsub("^%s*[Rr][Ee][Ss][Ee][Rr][Vv][Ee]%s+", ""))
      if ship:lower() == "none" or ship:lower() == "off" then ship = "" end
      ar.reserve = ship; scrye.store.set("reserve", ship)
    elseif low ~= "" then
      note("usage: araid on|off | target <name> | auto on|off | pool home|foreign | ships <n>|all | keep <n> | reserve <ship>|none | hold <sec> | convoy on|off | targets | heat | notify")
      return
    end
  end
  ar_status()
  publish()
end

-- ---------- HUD panel (Raid / Settings tabs) ----------
-- Static: built once, never rebuilt. The heat list is the target picker (each town
-- name is a click link), so the panel has nothing to rebuild when the feed arrives —
-- and the Settings inputs can never be re-seeded while you're typing in them.
scrye.addPanel{
  title = "Auto-Raid",
  width = 320,
  accent = "#E7574E",          -- signature: raid red (validated accent set)
  tabs = {
    { title = "Raid", widgets = {
        { type = "value", text = "Armed: ",  bind = SP .. "armed",  color = "warning" },  -- semantic: the arm switch
        { type = "value", text = "Target: ", bind = SP .. "target", color = "info" },     -- semantic: destination
        { type = "value", text = "Docked: ", bind = SP .. "docked", color = "success" },  -- semantic: ships ready
        { type = "value", text = "", bind = SP .. "status", color = "dim" },              -- the current settings, at a glance
        { type = "buttonrow", buttons = {
            { text = "Arm on/off", action = function() ar_config(ar.on and "off" or "on") end },
            { text = "Auto-target", action = function() ar_config(ar.auto_target and "auto off" or "auto on") end },
            { text = "Home/Foreign", action = function() ar_config(ar.pool == "foreign" and "pool home" or "pool foreign") end },
            { text = "Convoy",     action = function() ar_config(ar.convoy and "convoy off" or "convoy on") end },
        } },
        { type = "label", text = "Click a town to target it (calm = home auto pool):", color = "dim" },
        { type = "text", bind = SP .. "heat" },
    } },
    { title = "Raids", widgets = {
        { type = "label", bind = SP .. "raidsum", color = "dim" },
        -- the log, in the server's order; a row click targets that town
        { type = "table", bind = SP .. "raidlog", separator = "\t", columns = { "Ship", "Town", "Daler", "Brought" },
          align = "llrl",
          onRowClick = function(_, index)
            local town = raidlog_rows[index]
            if town then ar_config("target " .. town) end
          end },
        { type = "label", text = "by town - best paying first (click to target)", color = "dim" },
        { type = "table", bind = SP .. "raidtowns", separator = "\t", columns = { "Town", "Raids", "Avg", "Total" },
          align = "lrrr",
          onRowClick = function(_, index)
            local town = raidlog_towns[index]
            if town then ar_config("target " .. town) end
          end },
        { type = "label", text = "by ship", color = "dim" },
        { type = "table", bind = SP .. "raidships", separator = "\t", columns = { "Ship", "Raids", "Avg", "Total" },
          align = "lrrr" },
    } },
    { title = "Settings", widgets = {
        { type = "label", text = "Type a value, press Enter (or Set):", color = "dim" },
        { type = "input", text = "Target town ",  bind = SP .. "v_target",  onSubmit = function(t) ar_config("target " .. t) end },
        { type = "input", text = "Ships (n/all) ", bind = SP .. "v_ships",   onSubmit = function(t) ar_config("ships " .. t) end },
        { type = "input", text = "Keep docked ",  bind = SP .. "v_keep",    onSubmit = function(t) ar_config("keep " .. t) end },
        { type = "input", text = "Hold secs ",    bind = SP .. "v_hold",    onSubmit = function(t) ar_config("hold " .. t) end },
        { type = "input", text = "Reserve ship ", bind = SP .. "v_reserve", onSubmit = function(t) ar_config("reserve " .. t) end },
    } },
  },
}

scrye.addAlias{ pattern = "^araid$",         regex = true, run = function() ar_status() end }
scrye.addAlias{ pattern = "^araid targets$", regex = true, run = function() ar_list_targets() end }
scrye.addAlias{ pattern = "^araid (.+)$",    regex = true, run = function(rest) ar_config(rest) end }
-- 'araidwin' is consumed rather than passed to the MUD: the HUD panel replaces the
-- miniwindow and its visibility is app-managed.
scrye.addAlias{ pattern = "^araidwin$", regex = true, run = function()
  note("the Auto-Raid panel is managed by Scrye - show or hide it from the HUD. ('araid heat' prints the town heat table.)")
end }

-- ---------- timers / feed hooks ----------

-- raid pass driver (the 30 s AR_INTERVAL throttle lives inside auto_raid_tick,
-- matching the original's 6 s driver + 30 s pass interval)
scrye.every(6, driver)

-- Ships coming home change the docked pool -> run a pass promptly (the classic's
-- watch("vik.ships")): Guild.Voyage's longship list since 17 Sep 2026, Guild.Fleet's
-- ships before it (kept for a server still sending it). Guild.City: heat / dock
-- tier -> refresh display. Guild.Kingdom: grudges name the home towns by lineage.
gasm("Guild.Voyage", function(snap) VOY = snap; if snap.longship then driver() end end)
gasm("Guild.Fleet",  function(snap)
  FLEET = snap
  for key, which in pairs({ rtargets_lineage = "home", rtargets_historical = "foreign" }) do
    local list = snap[key]
    if type(list) == "table" and list[1] ~= nil then
      local names = {}
      for _, e in ipairs(list) do
        local town = tostring(e):match("^([^:]+)")
        if town and town ~= "" then names[#names + 1] = town end
      end
      if names[1] then fleet_list[which] = names end
    end
  end
  driver()                 -- the pass republishes the town table too
end)
gasm("Guild.City",   function(snap) CITY = snap; publish() end)
gasm("Guild.Kingdom", function(snap)
  KING = snap
  if type(snap.grudges) ~= "table" then return end
  local changed = false
  for _, g in ipairs(snap.grudges) do
    local lin, town = tonumber(g.lineage), tostring(g.town or "")
    if lin and lin > 0 and town ~= "" and home_lin[lin] ~= town then home_lin[lin] = town; changed = true end
  end
  if changed then publish() end
end)

-- the `vlongship targets` listing, read as it scrolls by
scrye.addTrigger{
  pattern = [[^-~\*(.*)\*~-\s*$]],
  regex   = true,
  run     = function(cap)
    local ok, changed = pcall(targets_line, cap)
    if ok and changed then publish() end
  end,
}

-- ---------- load ----------

publish()
publish_notify_state()
note("loaded - OFF (armed state is never persisted; 'araid on' to arm, 'araid' for status).")
