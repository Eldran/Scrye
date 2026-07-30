-- Viking Status — Scrye port of the MUSHclient ThreeS_VikingStatus plugin.
-- Three tabs on the declarative HUD: Stats (live gauges + war/combat report),
-- City (ships / carts / refinery), Map (live territory as a colour grid).
-- Data: the MIP viking (BBE) feed, surfaced by Scrye as state paths vik.<key>.
-- Type 'vmapon' to ask the MUD for the feed; 'vtick on' for a 5-min keepalive.

-- ---------- small helpers ------------------------------------------------------

local function gs(p) return scrye.getState(p) end
local function num(s) return tonumber(s) or 0 end
local function nz(s, alt) if s == nil or s == "" then return alt or "?" end return s end

local function split(s, sep)
  local out = {}
  for tok in (s or ""):gmatch("([^" .. sep .. "]+)") do out[#out + 1] = tok end
  return out
end

-- strip 3k colour-words / brackets from feed text
local function clean(s)
  s = s or ""
  s = s:gsub("grey:", ""):gsub("gray:", ""):gsub("red:", ""):gsub("green:", "")
  s = s:gsub("blue:", ""):gsub("yellow:", ""):gsub("[%[%]]", "")
  return s
end

local function request_feed()
  scrye.send("vtoggle mip_map")
  scrye.send("vtoggle mip_city")
  scrye.send("vtoggle mip_extra")
  scrye.print("requested mip_map + mip_city + mip_extra feed")
end

-- ---------- the panel ----------------------------------------------------------

scrye.addPanel({
  title = "VIKING STATUS",
  width = 430,
  tabs = {
    { title = "Stats", widgets = {
        { type = "gauge", text = "HP",   value = "character.health.current", max = "character.health.max" },
        { type = "gauge", text = "Seid", value = "vik.seid", max = "vik.mseid" },
        { type = "gauge", text = "Vig",  value = "vik.vig",  max = "vik.mvig" },
        { type = "gauge", text = "Rad",  value = "vik.rad",  max = "vik.mrad" },
        { type = "gauge", text = "Enemy", value = "enemy.health", max = "100" },
        { type = "value", text = "", bind = "enemy.name" },
        { type = "gauge", text = "Modrsokn", value = "plugin.viking.mordsokn", max = "180" },
        { type = "text", bind = "plugin.viking.stats" },
    }},
    { title = "City", widgets = {
        { type = "text", bind = "plugin.viking.city" },
    }},
    { title = "Map", widgets = {
        { type = "value", text = "", bind = "plugin.viking.maphead" },
        { type = "colorgrid", bind = "plugin.viking.map", palette = {
            -- terrain (colours ported from the original, BGR->RGB)
            ["."] = "#101010",                                        -- void / walls
            t = "#606060", T = "#707070",                             -- tundra
            h = "#C0C020", H = "#C0C020",                             -- hills
            A = "#D03030",                                            -- mountains
            f = "#208020", F = "#30A030",                             -- forest
            p = "#60C060",                                            -- plains
            W = "#00A0D0", w = "#00A0D0", ["~"] = "#00A0D0",          -- water
            r = "#181818", ["="] = "#303030",                         -- road / bridge
            P = "#909090",                                            -- gate / passage
            L = "#E08020",                                            -- lin hold
            S = "#E0C040",                                            -- settlement
            C = "#C02020", M = "#C02020",                             -- capital / Midgard
            R = "#C060C0",                                            -- ruins
            ["*"] = "#E060E0",                                        -- point of interest
            X = "#FFFFFF", ["@"] = "#FFFFFF",                         -- you
            ["?"] = "#1A2028",                                        -- unexplored
        }},
        { type = "text", color = "#8A97A8", bind = "plugin.viking.maplegend" },
        { type = "button", text = "Request map feed (vmapon)", action = request_feed },
    }},
  },
})

scrye.setState("plugin.viking.maplegend",
  "tundra grey · hills yellow · mtn red · forest/plains green\n" ..
  "water blue · hold orange · settle gold · capital red · @ you")

-- ---------- aliases + keepalive ------------------------------------------------

scrye.addAlias({ pattern = "vmapon", run = request_feed })

local tick_id = nil
scrye.addAlias({ pattern = "vtick *", run = function(arg)
  if arg == "on" and tick_id == nil then
    tick_id = scrye.every(300, function() scrye.send("l") end)
    scrye.print("keepalive on (l every 5m)")
  elseif arg == "off" and tick_id ~= nil then
    scrye.cancel(tick_id)
    tick_id = nil
    scrye.print("keepalive off")
  end
end })

scrye.onConnect(function()
  scrye.print("Viking Status loaded — type vmapon to request the live feed")
end)

-- ---------- Modrsokn cooldown (no clock in the sandbox: count down by timer) ----

local mordsokn = 0
scrye.addTrigger({ pattern = "You close your eyes and turn the rage inward*",
                   run = function() mordsokn = 180 end })

-- ---------- composers (run each second; setState skips unchanged values) --------

local function compose_stats()
  local L = {}
  local function add(fmt, ...) L[#L + 1] = string.format(fmt, ...) end

  add("-- War --")
  add("God %s > %s   next %ss", nz(gs("vik.god_power")), nz(gs("vik.god_power_focus")), nz(gs("vik.god_power_next")))
  add("Raid %s   Blot %s", nz(gs("vik.raid")), nz(gs("vik.blot")):sub(1, 16))
  add("")
  add("-- %s  GLvl %s --", nz(gs("vik.lin")), nz(gs("vik.glvl")))
  add("Sub %s   Daler %s", nz(gs("vik.sub")), nz(gs("vik.daler")))
  add("Kap %s  Aud %s  Vis %s  Soemd %s", nz(gs("vik.kap")), nz(gs("vik.aud")), nz(gs("vik.vis")), nz(gs("vik.soe")))
  add("VKxp %s  New %s  Reg %s  Tick %ss", nz(gs("vik.vkxp")), nz(gs("vik.vmnew")), nz(gs("vik.vmreg")), nz(gs("vik.nexttick")))
  local wx = split(gs("vik.weather"), "|")
  local dc = split(gs("vik.dcycle"), "|")
  add("Weather %s/%s   Cycle %s", nz(wx[1]), nz(wx[2]), nz(dc[1]))
  local fx = clean(gs("vik.stfx"))
  if fx ~= "" then add(""); add("Effects: %s", fx) end
  add("")
  add("-- Combat --")
  add("Fury %s   Threk %s/%s   Chain %s", clean(gs("vik.fury")):sub(1, 12), nz(gs("vik.threk")), nz(gs("vik.mthrek")), nz(gs("vik.chain")))
  add("Rounds %s   Ledung %s/%s", nz(gs("vik.rndz")), nz(gs("vik.ldng")), nz(gs("vik.mldng")))
  local p = split(gs("vik.patrol"), "|")
  if #p == 0 then
    add("Patrol: none out")
  else
    local mins = tonumber(p[2]) and (math.floor(num(p[2]) / 60) .. "m left") or ""
    add("Patrol: %s hirdmadrs   %s", nz(p[1]), mins)
  end
  scrye.setState("plugin.viking.stats", table.concat(L, "\n"))
end

local function compose_city()
  local L = {}
  local function add(fmt, ...) L[#L + 1] = string.format(fmt, ...) end

  add("-- Ships --")
  local ships = split(gs("vik.ships"), ";")
  if #ships == 0 then add("none") end
  for i = 1, math.min(#ships, 10) do
    local f = split(ships[i], "|")
    local eta = tonumber(f[5]) and (math.floor(num(f[5]) / 60) .. "m") or ""
    add("%-12s %-10s %s", nz(f[1], "?"), f[4] or "", eta)
  end
  add("")
  add("-- Carts --")
  local carts = split(gs("vik.carts"), ";")
  if #carts == 0 then add("no carts out") end
  for i = 1, math.min(#carts, 5) do
    local f = split(carts[i], "|")
    local eta = tonumber(f[4]) and (math.floor(num(f[4]) / 60) .. "m") or "?"
    add("%-4s %-10s > %-14s %5s x%s", nz(f[1]), nz(f[2]), nz(f[3]), eta, nz(f[5]))
  end
  add("")
  add("-- Refinery --")
  local refy = gs("vik.refinery")
  if refy == "" then add("none") end
  for _, r in ipairs(split(refy, "|")) do
    local f = split(r, ":")
    if f[1] and f[1] ~= "" then
      local name = f[1]:gsub("_", " ")
      add("%-16s T%s  %s/%s", name:sub(1, 16), nz(f[2]), nz(f[3], "0"), nz(f[4], "0"))
      local stages = {}
      for _, s in ipairs(split(f[5] or "", ";")) do
        local g = split(s, ",")
        if g[1] and g[1] ~= "" then stages[#stages + 1] = string.format("%s %s (%s%%)", g[1], nz(g[2], "0"), nz(g[3], "0")) end
      end
      if #stages > 0 then add("  %s", table.concat(stages, "  ")) end
    end
  end
  scrye.setState("plugin.viking.city", table.concat(L, "\n"))
end

local function compose_map()
  local hd = split(gs("vik.vmaph"), "|")
  local mw, mh, px, py = num(hd[1]), num(hd[2]), num(hd[3]), num(hd[4])
  if mw <= 0 or mh <= 0 then
    scrye.setState("plugin.viking.maphead", "no map feed — type vmapon")
    scrye.setState("plugin.viking.map", "")
    return
  end
  local rows = {}
  for r = 0, mh - 1 do
    local row = gs(string.format("vik.vmr%02d", r))
    local mask = gs(string.format("vik.mee%02d", r))
    local out = {}
    for c = 1, mw do
      local ch = row:sub(c, c)
      if ch == "" then ch = "." end
      if mask ~= "" and mask:sub(c, c) == "0" then ch = "?" end   -- unexplored: faint
      out[c] = ch
    end
    if r == py and px >= 0 and px < mw then out[px + 1] = "@" end -- you (0-based feed coords)
    rows[#rows + 1] = table.concat(out)
  end
  scrye.setState("plugin.viking.map", table.concat(rows, "\n"))
  scrye.setState("plugin.viking.maphead", string.format("Territory %dx%d   you @ %d,%d", mw, mh, px, py))
end

scrye.every(1, function()
  if mordsokn > 0 then mordsokn = mordsokn - 1 end
  scrye.setState("plugin.viking.mordsokn", tostring(mordsokn))
  compose_stats()
  compose_city()
  compose_map()
end)
