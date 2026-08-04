-- 3S Raid — Scrye port of MUSHclient ThreeS_Raid (auto-raid dispatcher).
--
-- NOTE: dropped / simplified vs the original:
--   * 'araidwin' + the whole miniwindow (buttons, town grid, inputboxes, drag)
--     are DROPPED — replaced by a minimal HUD panel (armed / target / docked)
--     bound to plugin.3s-raid.* state. The HUD's visibility is app-managed.
--   * GRUDGES parsing + grudge cooldown formatting were only used for window
--     tooltips — dropped with the window.
--   * Clicking a town to target it IS reproduced: the target list is a bound buttonrow
--     fed from the live RTARGETS feed (plugin API 1.3), replacing the miniwindow's
--     per-town hotspots. The row renders on one line, so it is capped at 6 towns; the
--     full list with heat values is in the text table below it and in 'araid targets'.
--   * The per-town heat table IS reproduced: the original colour-coded it in the
--     miniwindow, here it is a text block bound to plugin.3s-raid.heat (also
--     printable with 'araid heat'). Calm towns — those within AR_MARGIN of the
--     lowest heat, i.e. the auto-target pool — are marked, as is the live target.
--   * math.randomseed(os.time()) dropped (no os.* in sandbox); math.random is
--     used unseeded for the auto-target tie-break pool.
--   * Elapsed time (30 s pass throttle, auto-target hold) is counted with a
--     1-second scrye.every clock instead of os.time().
--   * Armed state is NOT persisted: the plugin always loads OFF, matching the
--     original's stated "starts OFF for safety" behaviour.
--   * Feed comes from native MIP state: vik.ships, vik.buildings, vik.heat,
--     vik.rtargets (vik.grudges no longer read).
-- Everything else (commands, dispatch logic, convoy/reserve/keep/hold rules)
-- is a straight port.

local AR_INTERVAL = 30   -- seconds between raid passes
local AR_MARGIN   = 2    -- heat margin for the auto-target pool

local SP = "plugin." .. scrye.id .. "."

-- ---------- helpers ----------

-- "[raid]" tag, restoring the original's orange ColourNote tag
local function note(s) scrye.print("@{#FF2E88,bold}[raid]@{} " .. s) end

local function split(s, sep)
  local t = {}
  for part in (s .. sep):gmatch("([^" .. sep .. "]*)" .. sep) do t[#t + 1] = part end
  return t
end

local function trim(s) return (s or ""):gsub("^%s+", ""):gsub("%s+$", "") end

local function feed(key) return scrye.getState("vik." .. key) or "" end

-- ---------- settings (persisted via scrye.store; armed always starts OFF) ----------

local ar = {
  on          = false,                                        -- always OFF on load (safety)
  target      = scrye.store.get("target") or "",
  ships       = scrye.store.get("ships") or "2",              -- "all" or a number (kept as string)
  keep        = tonumber(scrye.store.get("keep")) or 0,       -- always leave this many docked
  reserve     = scrye.store.get("reserve") or "",             -- named ship to always keep docked (voyages)
  convoy      = scrye.store.get("convoy") == "1",
  auto_target = scrye.store.get("autotarget") == "1",         -- pick the lowest-heat home town
  hold        = tonumber(scrye.store.get("hold")) or 60,      -- seconds to raid one town before rotating
  last        = 0,
  locked_target = nil,
  locked_at     = nil,
}

-- monotonic seconds since load (no os.time in the sandbox)
local clock = 0
scrye.every(1, function() clock = clock + 1 end)

local connected = true
scrye.onConnect(function() connected = true end)
scrye.onDisconnect(function() connected = false end)

-- ---------- feed parsing ----------

-- docked, available longships (by name), dock capacity (dock tier * 2), raiding count
local function fleet()
  local avail, raiding = {}, 0
  for entry in feed("ships"):gmatch("[^;]+") do
    local f = split(entry, "|")
    if f[1] then
      if f[3] == "docked" then avail[#avail + 1] = f[1]
      elseif f[3] == "raiding" then raiding = raiding + 1 end
    end
  end
  local dock = tonumber(feed("buildings"):match("dock:(%d+)")) or 1
  return avail, dock * 2, raiding
end

-- HEAT = semicolon values matching the home (Norse) towns in RTARGETS order (first group)
local function heat_of()
  local heats = {}
  for x in feed("heat"):gmatch("[^;]+") do heats[#heats + 1] = tonumber(x) or 0 end
  local map, order = {}, {}
  local grp1 = split(feed("rtargets"), "|")[1] or ""
  local i = 0
  for e in grp1:gmatch("[^;]+") do
    local t = e:match("^([^:]+)")
    if t and t ~= "" then i = i + 1; order[i] = t; map[t] = heats[i] or 0 end
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

-- pick a raid target: random among the towns within AR_MARGIN heat of the lowest, so it
-- spreads across the calm towns instead of always hammering the first tied one
local function pick_raid_town()
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
      local shown = (ar.locked_target and ar.locked_target ~= "") and ar.locked_target
                    or (lowest_heat_town() or "?")
      tgt = "auto: " .. shown
    else
      tgt = ar.target ~= "" and ar.target or "(none)"
    end
    scrye.setState(SP .. "armed",  ar.on and "ON" or "OFF")
    scrye.setState(SP .. "target", tgt)
    scrye.setState(SP .. "docked", string.format("%d / %d", #avail, maxs))
    scrye.setState(SP .. "status", string.format(
      "ships %s | keep %d | auto %s | convoy %s | hold %ds | reserve %s",
      tostring(ar.ships), ar.keep, ar.auto_target and "on" or "off",
      ar.convoy and "on" or "off", ar.hold or 60, ar.reserve ~= "" and ar.reserve or "none"))
    -- per-town heat table: the miniwindow drew this colour-coded (green = calm /
    -- auto pick, cyan = manual target); a text widget marks the same two cases.
    local map, order = heat_of()
    local hl = {}
    if #order == 0 then
      hl[1] = "no heat data yet (waiting on the vik.heat feed)"
    else
      local minh
      for _, t in ipairs(order) do
        local h = map[t] or 0
        if not minh or h < minh then minh = h end
      end
      local cur
      if ar.auto_target then
        cur = (ar.locked_target and ar.locked_target ~= "") and ar.locked_target or lowest_heat_town()
      elseif ar.target ~= "" then
        cur = ar.target
      end
      hl[#hl + 1] = string.format("%-18s %5s  %s", "Town", "Heat", "")
      for _, t in ipairs(order) do
        local h = map[t] or 0
        local mark = ""
        if cur and t:lower() == tostring(cur):lower() then mark = "<- target"
        elseif h <= minh + AR_MARGIN then mark = "calm" end
        hl[#hl + 1] = string.format("%-18s %5d  %s", t, h, mark)
      end
    end
    scrye.setState(SP .. "heat", table.concat(hl, "\n"))

    -- clickable target list: the home towns, calmest first, so the button you want is
    -- usually the leftmost one. Capped because a buttonrow lays out on a single line.
    local picks = {}
    for _, t in ipairs(order) do picks[#picks + 1] = t end
    table.sort(picks, function(x, y) return (map[x] or 0) < (map[y] or 0) end)
    while #picks > 6 do table.remove(picks) end
    scrye.setState(SP .. "towns", table.concat(picks, "\n"))

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
  if ar.convoy and want >= 2 and not reserved_docked then
    scrye.send(string.format("vlongship convoy %d %s", want, raid_town(target)))
    note(string.format("convoy of %d -> %s%s", want, target, ar.auto_target and " (lowest heat)" or ""))
  else
    for i = 1, want do scrye.send(string.format("vlongship raid %s %s", pool[i], raid_town(target))) end
    note(string.format("%d ship%s -> %s%s", want, want == 1 and "" or "s", target,
      ar.auto_target and " (lowest heat)" or ""))
  end
end

local function driver()
  local ok, err = pcall(auto_raid_tick)
  if not ok then note("raid tick error: " .. tostring(err)) end
  publish()
end

-- ---------- commands ----------

local function ar_status()
  local tgt = ar.auto_target and ("lowest-heat, rotate " .. (ar.hold or 60) .. "s")
              or (ar.target ~= "" and ar.target or "(none)")
  note(string.format("auto-raid %s | target %s | ships %s | keep %d | voyage ship %s | convoy %s",
    ar.on and "ON" or "OFF", tgt, tostring(ar.ships), ar.keep,
    ar.reserve ~= "" and ar.reserve or "(none)", ar.convoy and "yes" or "no"))
end

-- list the valid raid targets carried in the feed (RTARGETS = Norse|foreign groups)
local function ar_list_targets()
  local rt = feed("rtargets")
  if rt == "" then note("no target list in the feed yet") return end
  for gi, grp in ipairs(split(rt, "|")) do
    local towns = {}
    for e in grp:gmatch("[^;]+") do towns[#towns + 1] = (e:match("^([^:]+)") or e) end
    if #towns > 0 then
      note((gi == 1 and "Home: " or "Foreign: ") .. table.concat(towns, ", "))
    end
  end
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
  if low == "on" then ar.on = true
  elseif low == "off" then ar.on = false
  elseif low == "convoy on" then ar.convoy = true; scrye.store.set("convoy", "1")
  elseif low == "convoy off" then ar.convoy = false; scrye.store.set("convoy", "0")
  elseif low == "all" or low == "ships all" then ar.ships = "all"; scrye.store.set("ships", "all")
  elseif low == "auto on"  then ar.auto_target = true;  scrye.store.set("autotarget", "1")
  elseif low == "auto off" then ar.auto_target = false; scrye.store.set("autotarget", "0")
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
      note("usage: araid on|off | target <name> | auto on|off | ships <n>|all | keep <n> | reserve <ship>|none | hold <sec> | convoy on|off | targets | heat")
      return
    end
  end
  ar_status()
  publish()
end

-- ---------- HUD panel (status + controls) ----------
scrye.addPanel{
  title = "Auto-Raid",
  width = 320,
  accent = "#D6524E",          -- signature: raid red
  widgets = {
    { type = "value", text = "Armed: ",  bind = SP .. "armed",  color = "#E08A3C" },  -- amber: the arm switch
    { type = "value", text = "Target: ", bind = SP .. "target", color = "#6FB7E0" },  -- blue: destination
    { type = "value", text = "Docked: ", bind = SP .. "docked", color = "#4FB05A" },  -- green: ships ready
    { type = "buttonrow", buttons = {
        { text = "Arm On/Off",  action = function() ar_config(ar.on and "off" or "on") end },
        { text = "Targets",     action = function() ar_list_targets() end },
    } },
    { type = "buttonrow", buttons = {
        { text = "Auto-tgt On/Off", action = function() ar_config(ar.auto_target and "auto off" or "auto on") end },
        { text = "Convoy On/Off",   action = function() ar_config(ar.convoy and "convoy off" or "convoy on") end },
    } },
    { type = "label", text = "Settings (type a value, Enter):", color = "#8FA0B0" },
    { type = "input", text = "Target town ",  bind = SP .. "v_target",  onSubmit = function(t) ar_config("target " .. t) end },
    { type = "input", text = "Ships (n/all) ", bind = SP .. "v_ships",   onSubmit = function(t) ar_config("ships " .. t) end },
    { type = "input", text = "Keep docked ",  bind = SP .. "v_keep",    onSubmit = function(t) ar_config("keep " .. t) end },
    { type = "input", text = "Hold secs ",    bind = SP .. "v_hold",    onSubmit = function(t) ar_config("hold " .. t) end },
    { type = "input", text = "Reserve ship ", bind = SP .. "v_reserve", onSubmit = function(t) ar_config("reserve " .. t) end },
    { type = "text", bind = SP .. "status" },
    { type = "label", text = "Set target (calmest first):", color = "#8FA0B0" },
    { type = "buttonrow", bind = SP .. "towns",
      onClick = function(town) ar_config("target " .. town) end },
    { type = "label", text = "Town heat (calm = in the auto-target pool):", color = "#8FA0B0" },
    { type = "text", bind = SP .. "heat" },
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

-- re-dispatch promptly when ships return to dock (fleet feed changes)
scrye.watch("vik.ships", function() driver() end)
scrye.watch("vik.buildings", function() publish() end)

-- ---------- load ----------

publish()
note("loaded - OFF (armed state is never persisted; 'araid on' to arm, 'araid' for status).")
