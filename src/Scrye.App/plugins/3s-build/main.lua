-- 3S Build Planner -- Scrye port of MUSHclient ThreeS_Build
--
-- NOTE: dropped / simplified vs the original:
--   * miniwindow -> HUD panel with a monospace text report; colour-per-token is not
--     available, so short items are prefixed with "!" and each row carries a status
--     marker ("OK " affordable / " ! " short / "req" prereq unmet / "wip" building /
--     "max" tier 5). Tier palette colours are gone (plain "T3>4" text).
--   * "build" show/hide is dropped (panels are HUD-managed); "build" now PRINTS the
--     report to the output window instead.
--   * click-a-row-to-start is not possible; replaced with alias "build start <name>"
--     which checks affordability before sending "vbuild start <key>".
--   * movewindow / drag / hotspots / tooltips: dropped (no drawing API).
--   * SendNoEcho -> scrye.send; the "-~*" decorated vbuild-list lines are gagged via
--     an onLine hook while a scan is active (was omit_from_output on the trigger).
--   * the 6-second redraw timer is replaced by scrye.watch("vik", ...) live updates
--     (the feed itself drives redraws now).
--   * feed comes from scrye.getState("vik.daler" / "vik.wstock" / "vik.buildings" /
--     "vik.builds") instead of GetPluginVariable(ThreeS_MIP, "vmip_ser").
--   * persisted via scrye.store: "showmax" toggle and the scanned cost table
--     (serialized to a string).

local STATE_REPORT = "plugin." .. scrye.id .. ".report"

local function note(s) scrye.print(s) end

-- ---------------- helpers ----------------
local function split(s, sep)
  local t = {}
  for part in (s .. sep):gmatch("([^" .. sep .. "]*)" .. sep) do t[#t + 1] = part end
  return t
end
local function num(s) return tonumber((tostring(s or ""):match("%-?%d+"))) or 0 end
local function comma(n)
  local s, sign = tostring(math.floor(n)), ""
  if s:sub(1, 1) == "-" then sign, s = "-", s:sub(2) end
  while true do
    local a, b = s:gsub("^(%d+)(%d%d%d)", "%1,%2")
    s = a; if b == 0 then break end
  end
  return sign .. s
end

-- ---------------- cost table ----------------
-- C(daler, {resource = amount, ...}) ; tier index = build/upgrade target tier
local function C(d, r) return { d = d, r = r or {} } end
-- prereqs as { {"key", tier}, ... }
local BLD = {
  { key="warehouse",     name="Warehouse",       req={},
    cost={ C(1840,{timber=10}), C(6440,{iron=10,timber=25}), C(36800,{sunstone=20,iron=40,timber=100}),
           C(151800,{sunstone=40,iron=70,runestones=10,timber=180}), C(515200,{sunstone=80,iron=120,runestones=30,timber=300}) } },
  { key="trading_post",  name="Trading Post",    req={},
    cost={ C(460,{}), C(4600,{iron=15,timber=20}), C(27600,{sunstone=20,iron=50,runestones=10,timber=80}),
           C(115920,{sunstone=40,iron=90,runestones=20,timber=140}), C(404800,{sunstone=80,iron=140,runestones=40,timber=240}) } },
  { key="dock",          name="Dock",            req={},
    cost={ C(0,{}), C(4140,{timber=20}), C(23920,{iron=20,timber=80}),
           C(104880,{iron=40,timber=140}), C(368000,{iron=80,timber=240}) } },
  { key="lumber_yard",   name="Lumber Yard",     req={{"warehouse",1}},
    cost={ C(1104,{iron=5}), C(3680,{iron=15}), C(22080,{iron=60,timber=20}),
           C(96600,{iron=110,timber=40}), C(331200,{iron=180,timber=70}) } },
  { key="smithy",        name="Smithy",          req={{"warehouse",1}},
    cost={ C(1840,{timber=15}), C(5060,{iron=5,timber=25}), C(33120,{iron=40,timber=80}),
           C(138000,{iron=80,timber=140}), C(460000,{iron=140,timber=240}) } },
  { key="tannery",       name="Tannery",         req={{"warehouse",1}},
    cost={ C(1380,{timber=5}), C(4600,{furs=10,timber=15}), C(25760,{iron=20,furs=40,timber=60}),
           C(110400,{iron=40,furs=80,timber=110}), C(368000,{iron=70,furs=140,timber=180}) } },
  { key="fishery",       name="Fishery",         req={{"warehouse",1}},
    cost={ C(1104,{timber=10}), C(3680,{iron=5,timber=20}), C(22080,{iron=30,timber=80}),
           C(96600,{iron=60,timber=140}), C(331200,{iron=100,timber=240}) } },
  { key="farm",          name="Farm",            req={{"warehouse",1}},
    cost={ C(920,{timber=5}), C(3220,{grain=20,timber=15}), C(18400,{mead=20,grain=80,timber=60}),
           C(77280,{mead=40,grain=140,timber=110}), C(257600,{mead=70,grain=240,timber=180}) } },
  { key="brewery",       name="Brewery",         req={{"warehouse",1},{"farm",1}},
    cost={ C(2300,{grain=15}), C(7360,{timber=10,grain=30}), C(46000,{sunstone=10,mead=30,timber=40,grain=120}),
           C(193200,{sunstone=20,mead=60,timber=70,grain=200}), C(644000,{sunstone=40,timber=120,mead=100,grain=320}) } },
  { key="mead_cellar",   name="Mead Cellar",     req={{"brewery",1}},
    cost={ C(2760,{grain=10,timber=15}), C(9200,{iron=5,grain=25,timber=30}), C(55200,{sunstone=10,iron=20,grain=80,timber=90}),
           C(230000,{sunstone=20,iron=40,grain=140,timber=160}), C(736000,{sunstone=40,iron=70,grain=220,timber=260}) } },
  { key="longhouse",     name="Longhouse",       req={{"warehouse",1}},
    cost={ C(1840,{timber=20}), C(6440,{iron=10,timber=40}), C(40480,{sunstone=10,iron=40,timber=140}),
           C(165600,{sunstone=20,iron=80,timber=240}), C(552000,{sunstone=40,iron=140,timber=400}) } },
  { key="garrison",      name="Garrison",        req={{"longhouse",1}},
    cost={ C(1656,{iron=10,timber=15}), C(5520,{iron=20,timber=30}), C(33120,{sunstone=10,iron=70,timber=100}),
           C(138000,{sunstone=20,iron=120,timber=180}), C(460000,{sunstone=40,iron=200,timber=300}) } },
  { key="palisade",      name="Palisade",        req={{"warehouse",1}},
    cost={ C(1380,{timber=25}), C(5060,{iron=15,timber=50}), C(29440,{iron=60,timber=160}),
           C(124200,{iron=110,timber=280}), C(423200,{iron=180,timber=460}) } },
  { key="watchtower",    name="Watchtower",      req={{"palisade",1}},
    cost={ C(1104,{iron=5,timber=10}), C(3680,{iron=10,timber=20}), C(22080,{sunstone=10,iron=40,timber=70}),
           C(96600,{sunstone=20,iron=70,timber=120}), C(331200,{sunstone=40,iron=120,timber=200}) } },
  { key="mead_hall",     name="Mead-Hall",       req={{"longhouse",1}},
    cost={ C(2300,{timber=15,grain=20}), C(8280,{timber=25,mead=10,grain=40}), C(51520,{sunstone=20,timber=80,mead=50,grain=120}),
           C(215280,{sunstone=40,mead=100,timber=140,grain=200}), C(717600,{sunstone=80,mead=160,timber=240,grain=320}) } },
  { key="thrall_pen",    name="Thrall Pen",      req={{"longhouse",1}},
    cost={ C(1656,{iron=10,timber=15}), C(5520,{iron=20,timber=30}), C(33120,{sunstone=10,iron=70,timber=100}),
           C(138000,{sunstone=20,iron=120,timber=180}), C(460000,{sunstone=40,iron=200,timber=300}) } },
  { key="muster_ground", name="Muster Ground",   req={{"garrison",1}},
    cost={ C(2300,{iron=10,timber=20}), C(7360,{iron=20,timber=40}), C(46000,{sunstone=10,iron=60,timber=120}),
           C(193200,{sunstone=20,iron=110,timber=200}), C(644000,{sunstone=40,iron=180,timber=320}) } },
  { key="settler_plots", name="Settler Plots",   req={{"warehouse",1}},
    cost={ C(2760,{iron=5,timber=20}), C(11040,{iron=15,grain=20,timber=40}), C(64400,{iron=60,mead=20,grain=80,timber=140}),
           C(248400,{iron=110,grain=140,mead=40,timber=240}), C(736000,{iron=180,grain=240,mead=70,timber=400}) } },
  { key="well",          name="Well",            req={{"warehouse",1}},
    cost={ C(736,{iron=5,timber=5}), C(2300,{iron=10,timber=15}), C(12880,{iron=40,timber=50}),
           C(55200,{iron=70,timber=80}), C(184000,{iron=120,timber=140}) } },
  { key="salting_house", name="Salting House",   req={{"fishery",1}},
    cost={ C(2760,{iron=10,timber=15}), C(9200,{iron=20,timber=30}), C(55200,{sunstone=10,iron=45,timber=90}),
           C(230000,{sunstone=20,iron=90,timber=160}), C(736000,{sunstone=40,iron=150,timber=260}) } },
  { key="bakehouse",     name="Bakehouse",       req={{"farm",1}},
    cost={ C(2760,{iron=10,timber=15}), C(9200,{iron=20,timber=30}), C(55200,{sunstone=10,iron=45,timber=90}),
           C(230000,{sunstone=20,iron=90,timber=160}), C(736000,{sunstone=40,iron=150,timber=260}) } },
  { key="furriers_lodge",name="Furrier's Lodge", req={{"tannery",1}},
    cost={ C(2760,{iron=10,timber=15}), C(9200,{iron=20,timber=30}), C(55200,{sunstone=10,iron=45,timber=90}),
           C(230000,{sunstone=20,iron=90,timber=160}), C(736000,{sunstone=40,iron=150,timber=260}) } },
  { key="mine",          name="Mine",            req={{"warehouse",1}},
    cost={ C(1288,{iron=6,timber=12}), C(4416,{iron=15,timber=26}), C(24840,{sunstone=8,iron=35,timber=75}),
           C(105800,{sunstone=16,iron=70,timber=135}), C(349600,{sunstone=32,iron=120,timber=220}) } },
  { key="smelter",       name="Smelter",         req={{"mine",1}},
    cost={ C(2760,{iron=10,timber=15}), C(9200,{iron=20,timber=30}), C(55200,{sunstone=10,iron=45,timber=90}),
           C(230000,{sunstone=20,iron=90,timber=160}), C(736000,{sunstone=40,iron=150,timber=260}) } },
}

local plan = BLD          -- active cost table; replaced by a live 'vbuild list' scan when available
local NAME = {}
local function rebuild_names() NAME = {}; for _, b in ipairs(plan) do NAME[b.key] = b.name end end
rebuild_names()

-- preferred order for listing resources; any others are appended alphabetically
local RESORDER = { "iron", "timber", "sunstone", "runestones", "furs", "fine_furs", "grain", "mead", "gemstones" }
-- ordered keys actually present in a cost's resource table (RESORDER first, then extras)
local function res_keys(r)
  local out, seen = {}, {}
  for _, k in ipairs(RESORDER) do if r[k] then out[#out + 1] = k; seen[k] = true end end
  local extra = {}
  for k in pairs(r) do if not seen[k] then extra[#extra + 1] = k end end
  table.sort(extra)
  for _, k in ipairs(extra) do out[#out + 1] = k end
  return out
end

-- ---------------- persistence (scrye.store, strings only) ----------------
-- plan serialization: one building per line; TAB-separated fields:
--   key \t name \t req(k=t,k=t) \t tier:daler:res=amt,res=amt \t ...
local function serialize_plan(p)
  local lines = {}
  for _, b in ipairs(p) do
    local req = {}
    for _, r in ipairs(b.req) do req[#req + 1] = r[1] .. "=" .. r[2] end
    local parts = { b.key, b.name, table.concat(req, ",") }
    local tiers = {}
    for t in pairs(b.cost) do tiers[#tiers + 1] = t end
    table.sort(tiers)
    for _, t in ipairs(tiers) do
      local c = b.cost[t]
      local rs = {}
      for k, v in pairs(c.r) do rs[#rs + 1] = k .. "=" .. v end
      table.sort(rs)
      parts[#parts + 1] = t .. ":" .. c.d .. ":" .. table.concat(rs, ",")
    end
    lines[#lines + 1] = table.concat(parts, "\t")
  end
  return table.concat(lines, "\n")
end

local function deserialize_plan(str)
  local p = {}
  for _, line in ipairs(split(str or "", "\n")) do
    if line ~= "" then
      local f = split(line, "\t")
      if f[1] and f[1] ~= "" and f[2] and f[2] ~= "" then
        local b = { key = f[1], name = f[2], req = {}, cost = {} }
        if f[3] and f[3] ~= "" then
          for _, part in ipairs(split(f[3], ",")) do
            local k, t = part:match("^(.-)=(%d+)$")
            if k and k ~= "" then b.req[#b.req + 1] = { k, tonumber(t) } end
          end
        end
        for i = 4, #f do
          local t, d, rs = f[i]:match("^(%d+):(%d+):(.*)$")
          if t then
            local c = { d = tonumber(d) or 0, r = {} }
            for _, part in ipairs(split(rs, ",")) do
              local k, a = part:match("^(.-)=(%d+)$")
              if k and k ~= "" then c.r[k] = tonumber(a) end
            end
            b.cost[tonumber(t)] = c
          end
        end
        p[#p + 1] = b
      end
    end
  end
  return p
end

-- restore scanned plan + toggle from the store
local show_max = (scrye.store.get("showmax") == "1")
do
  local saved = scrye.store.get("plan")
  if saved and saved ~= "" then
    local ok, p = pcall(deserialize_plan, saved)
    if ok and p and #p > 0 then plan = p; rebuild_names() end
  end
end

-- ---------------- feed ----------------
-- reads: vik.daler, vik.wstock, vik.buildings, vik.builds
local function getvars()
  return {
    DALER     = scrye.getState("vik.daler"),
    WSTOCK    = scrye.getState("vik.wstock"),
    BUILDINGS = scrye.getState("vik.buildings"),
    BUILDS    = scrye.getState("vik.builds"),
  }
end

-- ---------------- compute ----------------
-- returns rows (sorted for display) + current daler
local function compute(vmip)
  vmip = vmip or getvars()
  local daler = num(vmip.DALER)
  -- warehouse stock, summed per good
  local stock = {}
  for _, e in ipairs(split(vmip.WSTOCK or "", ";")) do
    local f = split(e, "|")
    if f[1] and f[1] ~= "" then stock[f[1]] = (stock[f[1]] or 0) + num(f[2]) end
  end
  -- current tiers
  local tier = {}
  for _, e in ipairs(split(vmip.BUILDINGS or "", ",")) do
    local k, t = e:match("^(.-):(%d+)$")
    if k then tier[k] = tonumber(t) end
  end
  -- in-progress builds (normalise name -> key form)
  local building = {}
  for _, e in ipairs(split(vmip.BUILDS or "", ";")) do
    local nm = (split(e, "|")[1] or ""):lower():gsub("[%s%-]", "_")
    if nm ~= "" then building[nm] = true end
  end

  -- match a scanned building key to the game's feed key, tolerating a dropped possessive 's'
  -- (display "Goldsmith's" -> goldsmiths but the feed uses "goldsmith"; "Skald's Hall" -> skald_hall).
  -- Exact match is tried first, so "furriers_lodge"/"settler_plots" still resolve to themselves.
  local function feed_key(k)
    if tier[k] ~= nil or building[k] then return k end
    -- drop the possessive 's ("skalds_hall" -> "skald_hall", "goldsmiths" -> "goldsmith")
    local ks = k:gsub("s(_)", "%1"):gsub("s$", "")
    if tier[ks] ~= nil or building[ks] then return ks end
    -- the game also shortens some names ("Goldsmith's Hall" -> "goldsmith"): match a feed key
    -- that forms the leading whole word(s) of the scanned key.
    for fk in pairs(tier)     do if ks:sub(1, #fk + 1) == fk .. "_" then return fk end end
    for fk in pairs(building) do if ks:sub(1, #fk + 1) == fk .. "_" then return fk end end
    return k
  end

  local rows = {}
  for _, b in ipairs(plan) do
    local cur   = tier[feed_key(b.key)] or 0
    local nextt = cur + 1
    local row = { b = b, cur = cur, nextt = nextt }

    if cur >= 5 then
      row.cat, row.locked = 4, "MAX (T5)"
    elseif building[feed_key(b.key)] then
      row.cat, row.locked = 3, "building..."
    else
      local unmet = {}
      for _, p in ipairs(b.req) do
        if (tier[feed_key(p[1])] or 0) < p[2] then unmet[#unmet + 1] = (NAME[p[1]] or p[1]) .. " T" .. p[2] end
      end
      local c = b.cost[nextt]
      row.cost = c
      if #unmet > 0 then
        row.cat, row.locked = 2, "needs " .. table.concat(unmet, ", ")
      elseif not c then
        row.cat, row.locked = 2, "no cost data (build scan)"
      else
        -- full cost as tokens, each flagged have-enough / short
        local toks = {}
        local dok  = daler >= c.d
        toks[#toks + 1] = { text = comma(c.d) .. "d", ok = dok }
        local allok, missing = dok, (dok and 0 or 1)
        for _, res in ipairs(res_keys(c.r)) do
          local need = c.r[res]
          if need then
            local ok = (stock[res] or 0) >= need
            toks[#toks + 1] = { text = need .. " " .. res, ok = ok }
            if not ok then allok = false; missing = missing + 1 end
          end
        end
        row.toks, row.buildable = toks, allok
        row.cat, row.missing, row.dcost = (allok and 0 or 1), missing, c.d
      end
    end
    rows[#rows + 1] = row
  end

  -- buildable first, then closest-to-affordable (fewest missing, then cheapest)
  table.sort(rows, function(a, b)
    if a.cat ~= b.cat then return a.cat < b.cat end
    if a.cat == 1 and a.missing ~= b.missing then return a.missing < b.missing end
    local ad, bd = a.dcost or (a.cost and a.cost.d) or 0, b.dcost or (b.cost and b.cost.d) or 0
    if ad ~= bd then return ad < bd end
    return a.b.name < b.b.name
  end)
  return rows, daler
end

-- ---------------- report (replaces the miniwindow) ----------------
local function cost_str(c)
  local parts = { comma(c.d) .. "d" }
  for _, res in ipairs(res_keys(c.r)) do
    if c.r[res] then parts[#parts + 1] = c.r[res] .. " " .. res end
  end
  return table.concat(parts, " +")
end

local report_lines = {}

local function bp_draw()
  local ok, rows, daler = pcall(compute)
  if not ok then return end

  -- filter maxed unless show_max
  local shown = {}
  for _, r in ipairs(rows) do if show_max or r.cat ~= 4 then shown[#shown + 1] = r end end

  local lines = {}
  lines[#lines + 1] = "Daler " .. comma(daler) .. (show_max and "   [all]" or "   [+max]") .. "   (! = short)"
  for _, r in ipairs(shown) do
    local mark, body
    if r.cat == 4 then
      mark, body = "max", r.locked
    elseif r.cat == 3 then
      mark, body = "wip", r.locked
    elseif r.cat == 2 then
      mark, body = "req", r.locked
    else
      mark = r.buildable and "OK " or " ! "
      local toks = {}
      for _, t in ipairs(r.toks) do toks[#toks + 1] = (t.ok and "" or "!") .. t.text end
      body = table.concat(toks, "  ")
    end
    local tierstr = (r.nextt <= 5) and string.format("T%d>%d", r.cur, r.nextt) or ("T" .. r.cur)
    lines[#lines + 1] = string.format("%s %-15s %-5s %s", mark, r.b.name, tierstr, body)
  end
  report_lines = lines
  scrye.setState(STATE_REPORT, table.concat(lines, "\n"))
end

-- ---------------- live 'vbuild list' scan (keeps costs in sync with the game) ----------------
local KNOWN = { timber = true, iron = true, sunstone = true, runestones = true, furs = true,
                fine_furs = true, grain = true, mead = true, gemstones = true }
local function nkey(s)
  s = tostring(s or "")
  s = s:gsub("^%s+", ""):gsub("%s+$", "")
  s = s:lower():gsub("'", ""):gsub("[%s%-]+", "_")
  return s
end
local function parse_req(txt)
  local req = {}
  if not txt or txt:find("none") then return req end
  for part in (txt .. ","):gmatch("%s*(.-)%s*,") do
    local nm, t = part:match("^(.-)%s+tier%s+(%d+)")
    if nm and nm ~= "" then req[#req + 1] = { nkey(nm), tonumber(t) } end
  end
  return req
end

local bp_parse, bp_cur, bp_cur_tier, bp_scanning = {}, nil, nil, false
local scan_active = false          -- emulates the enabled/disabled "vblist" trigger group
local scan_timer = nil

local function bp_scan_done()
  if not bp_scanning and not scan_active then return end
  bp_scanning, scan_active = false, false
  if scan_timer then scrye.cancel(scan_timer); scan_timer = nil end
  -- drop any unnamed placeholder entries (a building whose header we couldn't read)
  local clean = {}
  for _, b in ipairs(bp_parse) do if b.key ~= "" and b.name ~= "?" then clean[#clean + 1] = b end end
  bp_parse = clean
  if #bp_parse > 0 then
    plan = bp_parse
    rebuild_names()
    scrye.store.set("plan", serialize_plan(plan))   -- persist scanned costs
    note("scanned " .. #bp_parse .. " buildings from vbuild list")
  else
    note("vbuild list scan found nothing - using built-in costs")
  end
  bp_draw()
end

-- called per decorated line during a scan; runs a small state machine
local function bp_scan_line(cap)
  -- drop the closing *~- marker (and anything after it) before trimming; on a wrapped
  -- header line the marker is absent, which is fine.
  local c = (cap or ""):gsub("%*~%-.*$", ""):gsub("^%s+", ""):gsub("%s+$", "")
  if c == "" then return end
  if c:find("Available Buildings") then bp_parse, bp_cur, bp_cur_tier, bp_scanning = {}, nil, nil, true; return end
  if not bp_scanning then return end
  if c:match("^Commands") or c:find("'vbuild") then bp_scan_done(); return end
  -- building header:  "<Name>   Req: <req>"
  local nm, req = c:match("^(.-)%s+Req:%s*(.*)$")
  if nm and nm ~= "" then
    bp_cur = { key = nkey(nm), name = nm, req = parse_req(req), cost = {} }
    bp_parse[#bp_parse + 1] = bp_cur; bp_cur_tier = nil
    return
  end
  -- tier line:  "Tier N: X daler"
  local tn, dal = c:match("^Tier (%d+):%s*([%d,]+)%s*daler")
  if tn and bp_cur then
    local t = tonumber(tn)
    -- safety net: if tiers stop increasing, a new building's header was missed -- start a
    -- fresh (unnamed) entry rather than overwriting the current building's costs.
    if bp_cur_tier and t <= bp_cur_tier then
      bp_cur = { key = "", name = "?", req = {}, cost = {} }
      bp_parse[#bp_parse + 1] = bp_cur
    end
    bp_cur_tier = t
    bp_cur.cost[bp_cur_tier] = { d = tonumber((dal:gsub(",", ""))) or 0, r = {} }
    return
  end
  -- resource line:  "+ 10 Iron, 25 Timber"  (or a bare continuation line when a cost wraps).
  -- Names are Capitalised and may be multi-word ("Fine Furs"), so normalise every one with nkey().
  if bp_cur and bp_cur_tier and bp_cur.cost[bp_cur_tier] then
    local body = c:match("^%+%s*(.+)$")
    if not body and c:match("^%d") then            -- wrapped continuation of a resource list
      local ok = true
      for item in (c .. ","):gmatch("%s*(.-)%s*,") do
        local a, m = item:match("^(%d+)%s+([%a][%a ]*)$")
        if not (a and KNOWN[nkey(m)]) then ok = false; break end
      end
      if ok then body = c end
    end
    if body then
      for item in (body .. ","):gmatch("%s*(.-)%s*,") do
        local a, m = item:match("^(%d+)%s+([%a][%a ]*)$")
        if a and m then bp_cur.cost[bp_cur_tier].r[nkey(m)] = tonumber(a) end
      end
    end
  end
end

local function bp_scan()
  bp_parse, bp_cur, bp_cur_tier, bp_scanning = {}, nil, nil, true
  scan_active = true
  scrye.send("vbuild list")
  if scan_timer then scrye.cancel(scan_timer) end
  scan_timer = scrye.after(8, bp_scan_done)   -- safety finalize if the closing line is missed
end

-- captures 'vbuild list' output (decorated -~* ... *~- lines); active only during a scan.
-- Match on the opening -~* marker alone: a long header line can wrap so its closing *~-
-- falls on the next physical line; requiring both markers would drop that header.
scrye.addTrigger{
  pattern = [[^-~\*(.*)]],
  regex   = true,
  run     = function(cap)
    if not scan_active then return end
    pcall(bp_scan_line, cap)
  end,
}

-- gag the decorated lines while a scan is active (was omit_from_output on the trigger)
scrye.onLine(function(line)
  if scan_active and line:match("^%-~%*") then return false end
end)

-- ---------------- actions ----------------
local function bp_toggle_all()
  show_max = not show_max
  scrye.store.set("showmax", show_max and "1" or "0")
  note(show_max and "showing maxed buildings" or "hiding maxed buildings")
  bp_draw()
end

-- print the report to the output window (replaces the window show/hide toggle)
local function bp_print()
  bp_draw()
  for _, l in ipairs(report_lines) do note(l) end
  note("commands: build all | build refresh | build scan | build start <name>")
end

-- "build start <name>": affordability-checked replacement for click-to-build
local function bp_start(arg)
  local k = nkey(arg)
  if k == "" then note("usage: build start <building>"); return end
  local rows = compute()
  local target
  for _, r in ipairs(rows) do
    if r.b.key == k or nkey(r.b.name) == k then target = r; break end
  end
  if not target then note("unknown building: " .. arg); return end
  if target.cat == 4 then note(target.b.name .. " is already MAX (T5)"); return end
  if target.cat == 3 then note(target.b.name .. " is already building"); return end
  if target.cat == 2 then note(target.b.name .. ": " .. target.locked); return end
  if not target.buildable then
    note("cannot afford " .. target.b.name .. " T" .. target.nextt ..
         (target.cost and (":  " .. cost_str(target.cost)) or ""))
    return
  end
  scrye.send("vbuild start " .. target.b.key)
  note("vbuild start " .. target.b.key)
  scrye.after(2, bp_draw)   -- refresh after the build registers
end

-- ---------------- aliases ----------------
scrye.addAlias{ pattern = [[^build$]],         regex = true, run = function() bp_print() end }
scrye.addAlias{ pattern = [[^build all$]],     regex = true, run = function() bp_toggle_all() end }
scrye.addAlias{ pattern = [[^build refresh$]], regex = true, run = function() bp_draw() end }
scrye.addAlias{ pattern = [[^build scan$]],    regex = true, run = function() bp_scan() end }
scrye.addAlias{ pattern = [[^build start (.+)$]], regex = true, run = function(w1) bp_start(w1) end }

-- ---------------- panel ----------------
scrye.addPanel{
  title = "Build Planner",
  width = 480,
  widgets = {
    { type = "text",   bind = STATE_REPORT },
    { type = "button", text = "Refresh", action = function() bp_draw() end },
    { type = "button", text = "Scan",    action = function() bp_scan() end },
  },
}

-- ---------------- init ----------------
-- live updates: redraw whenever any vik.* feed key changes (replaces the 6 s tick)
scrye.watch("vik", function() bp_draw() end)

-- re-scan costs on connect, after a delay so we're logged in first
scrye.onConnect(function() scrye.after(20, bp_scan) end)

bp_draw()
