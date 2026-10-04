-- 3S Changeling -- the Changeling guild HUD, built on the Guild.* GMCP packages a
-- changeling character receives.
--
-- Built from one capture (Kuben, 3 Sep 2026, 737 messages / 14 packages). Every field
-- below was SEEN; nothing is invented from a guild help file. Where the capture shows a
-- number but not what it means, the number is shown under the server's own name rather
-- than dressed up as a bar, and the comment says VERIFY LIVE.
--
-- Packages consumed:
--   Guild.State   protoplasm (0-100, a float), stamina (0-100, a float that regains about
--                 4.5 a message), super (counts DOWN by one every message, i.e. every 2 s -
--                 VERIFY LIVE: what reaching 0 means), the toggles dissipate, thermotaxis,
--                 adrenalize, hyperproliferate ("Off" / anything else = on), the two
--                 instincts (instinct_act + instinct_hp, instinct2_act + instinct2_which +
--                 instinct2_gp), bioplasts, kills, relinquishes, reset (VERIFY LIVE)
--   Guild.Info    current_form, form_group, form_points, form_time (seconds), form_initimacy
--                 (the server's own spelling), perform, next_form_cost, last_form_cost,
--                 complex, ingested, best_kill + best_kill_class, arch_foe +
--                 arch_foe_class, fam, guild_age and combat_age (seconds; guild_age goes
--                 up by 2 every message, which is how its unit is known)
--
-- Both packages send one full message ("full": 1) and then only what changed - a guild_age
-- tick, a stamina and super step - so the snapshot is merged key by key and a delta keeps
-- the rest. Guild.Extra is offered in Core.Supported but never arrived in the capture.
--
-- Guild.State and Guild.Info share their NAMES with every other guild's packages: a
-- changeling's carry guild = "changeling", and another guild's are left alone.

local P = "plugin." .. scrye.id .. "."
local GUILD = "changeling"

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

-- seconds -> "3d 4h" / "5h 12m" / "42m" / "30s"
local function fmt_span(secs)
  secs = N(secs)
  if secs >= 86400 then return string.format("%dd %dh", math.floor(secs / 86400), math.floor((secs % 86400) / 3600)) end
  if secs >= 3600  then return string.format("%dh %dm", math.floor(secs / 3600), math.floor((secs % 3600) / 60)) end
  if secs >= 60    then return string.format("%dm", math.floor(secs / 60)) end
  return math.floor(secs) .. "s"
end

-- a reserve's colour: full is good
local function pctcol(v)
  v = N(v)
  if v >= 75 then return "success" end
  if v >= 40 then return "warning" end
  return "error"
end

-- a ten-cell bar for a percent, and the percent beside it
local function bar(pct)
  local n = math.max(0, math.min(10, math.floor(N(pct) / 10 + 0.5)))
  return col(pctcol(pct), string.rep("#", n)) .. col("dim", string.rep(".", 10 - n))
         .. "  " .. col(pctcol(pct), string.format("%d%%", math.floor(N(pct) + 0.5)))
end

local function nice(s) return (tostring(s or ""):gsub("_", " ")) end

-- a toggle is "Off" when off; anything else ("On", a level) is on
local function on(v) return v ~= nil and v ~= "" and tostring(v):lower() ~= "off" and v ~= 0 end

local TOGGLES = { "dissipate", "thermotaxis", "adrenalize", "hyperproliferate" }

-- ------------------------------------------------------------- snapshots
local ST, INFO = {}, {}

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
  if not has(ST, "protoplasm") and not has(ST, "stamina") and not has(ST, "super") then
    add("waiting for Guild.State...")
    scrye.setState(P .. "status", table.concat(L, "\n"))
    return
  end

  add("-- Body --")
  if has(ST, "protoplasm") then add("Protoplasm   " .. bar(ST.protoplasm)) end
  if has(ST, "stamina") then add("Stamina      " .. bar(ST.stamina)) end
  if has(ST, "super") then
    -- counts down a step every 2 s; shown as the server's number and the time it is
    add("Super        " .. esc(S(ST.super)) .. col("dim", "   (" .. fmt_span(N(ST.super) * 2) .. " at 2 s a step)"))
  end
  if has(ST, "bioplasts") then add("Bioplasts    " .. esc(comma(ST.bioplasts))) end

  add("")
  add("-- Abilities --")
  local any = false
  for _, k in ipairs(TOGGLES) do
    if has(ST, k) then
      any = true
      local v = ST[k]
      local label = string.format("%-17s", nice(k))
      if on(v) then add(esc(label) .. col("success", tostring(v)))
      else add(esc(label) .. col("dim", "off")) end
    end
  end
  if not any then add(col("dim", "(none reported yet)")) end

  -- the instincts: what fires, and on what (VERIFY LIVE: both were empty in the capture)
  local i1, i2 = S(ST.instinct_act), S(ST.instinct2_act)
  if has(ST, "instinct_act") or has(ST, "instinct2_act") then
    add("")
    add("-- Instincts --")
    if i1 ~= "" then add("Instinct     " .. esc(i1) .. col("dim", "   at hp " .. S(ST.instinct_hp)))
    else add("Instinct     " .. col("dim", "none set")) end
    if i2 ~= "" then
      local on_what = S(ST.instinct2_which)
      add("Instinct 2   " .. esc(i2) .. col("dim", "   " .. (on_what ~= "" and (on_what .. " ") or "") .. "at gp " .. S(ST.instinct2_gp)))
    else add("Instinct 2   " .. col("dim", "none set")) end
  end

  if has(INFO, "current_form") then
    add("")
    add("-- Form --")
    add("Form         " .. col("accent", nice(INFO.current_form))
        .. (S(INFO.form_group) ~= "" and col("dim", "   (" .. nice(INFO.form_group) .. ")") or ""))
    if has(INFO, "form_time") then add("In form      " .. esc(fmt_span(INFO.form_time))) end
  end
  scrye.setState(P .. "status", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Form tab
local function build_form()
  local L = {}
  local add = mkadd(L)
  if not has(INFO, "current_form") then
    add("waiting for Guild.Info...")
    scrye.setState(P .. "form", table.concat(L, "\n"))
    return
  end
  add("-- " .. nice(INFO.current_form) .. " --")
  if S(INFO.form_group) ~= "" then add("Group        " .. esc(nice(INFO.form_group))) end
  if has(INFO, "form_points") then add("Form points  " .. esc(comma(INFO.form_points))) end
  if has(INFO, "form_time") then add("Time in form " .. esc(fmt_span(INFO.form_time))) end
  if has(INFO, "form_initimacy") then add("Intimacy     " .. esc(S(INFO.form_initimacy))) end
  if S(INFO.perform) ~= "" then add("Perform      " .. esc(S(INFO.perform))) end
  add("")
  add("-- Morphing --")
  if has(INFO, "next_form_cost") then
    local cost, pts = N(INFO.next_form_cost), N(INFO.form_points)
    add("Next form    " .. esc(comma(cost))
        .. (has(INFO, "form_points") and cost > 0 and pts >= cost and col("success", "   affordable") or ""))
  end
  if has(INFO, "last_form_cost") then add("Last form    " .. esc(comma(INFO.last_form_cost))) end
  if has(INFO, "complex") then add("Complex      " .. esc(comma(INFO.complex))) end
  if has(INFO, "ingested") then add("Ingested     " .. esc(comma(INFO.ingested))) end
  if S(INFO.fam) ~= "" then
    add("")
    add("Familiar     " .. esc(S(INFO.fam)))
  end
  scrye.setState(P .. "form", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Record tab
local function build_record()
  local L = {}
  local add = mkadd(L)
  if not has(INFO, "guild_age") and not has(ST, "kills") then
    add("waiting for Guild.Info...")
    scrye.setState(P .. "record", table.concat(L, "\n"))
    return
  end
  add("-- Record --")
  if has(ST, "kills") then add("Kills        " .. esc(comma(ST.kills))) end
  if has(ST, "relinquishes") then add("Relinquishes " .. esc(comma(ST.relinquishes))) end
  if has(ST, "reset") then add("Reset        " .. esc(S(ST.reset))) end   -- VERIFY LIVE: its unit
  if S(INFO.best_kill) ~= "" then
    add("Best kill    " .. esc(S(INFO.best_kill)))
    if has(INFO, "best_kill_class") then add("             " .. col("dim", "class " .. comma(INFO.best_kill_class))) end
  end
  if has(INFO, "arch_foe") then
    local foe = INFO.arch_foe
    if foe == 0 or S(foe) == "" or S(foe) == "0" then add("Arch foe     " .. col("dim", "none"))
    else add("Arch foe     " .. esc(S(foe)) .. col("dim", "   class " .. comma(INFO.arch_foe_class))) end
  end
  add("")
  add("-- Time --")
  if has(INFO, "guild_age") then add("Guild age    " .. esc(fmt_span(INFO.guild_age))) end
  if has(INFO, "combat_age") then
    local pct = N(INFO.guild_age) > 0 and math.floor(N(INFO.combat_age) * 100 / N(INFO.guild_age) + 0.5) or nil
    add("In combat    " .. esc(fmt_span(INFO.combat_age)) .. (pct and col("dim", "   " .. pct .. "% of it") or ""))
  end
  scrye.setState(P .. "record", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- one-liner
local function summary()
  if not has(ST, "protoplasm") and not has(ST, "stamina") and not has(INFO, "current_form") then
    return "waiting for the changeling feed"
  end
  local bits = {}
  if has(INFO, "current_form") then bits[#bits + 1] = nice(INFO.current_form) end
  if has(ST, "protoplasm") then bits[#bits + 1] = "Proto " .. math.floor(N(ST.protoplasm) + 0.5) .. "%" end
  if has(ST, "stamina") then bits[#bits + 1] = "Stam " .. math.floor(N(ST.stamina) + 0.5) .. "%" end
  if has(ST, "super") then bits[#bits + 1] = "Super " .. S(ST.super) end
  for _, k in ipairs(TOGGLES) do if on(ST[k]) then bits[#bits + 1] = k:upper() end end
  return table.concat(bits, "   ")
end

local BUILDERS = { status = build_status, form = build_form, record = build_record }

flush = function()
  flush_pending = false
  for sec in pairs(dirty) do
    local b = BUILDERS[sec]
    if b then pcall(b) end
  end
  scrye.setState(P .. "summary", summary())
  dirty = {}
end

-- ---------------------------------------------------------------- feeds
-- A full message replaces the snapshot; a delta changes only what it carries. A message
-- whose guild is not ours (another guild's Guild.State) is ignored.
local function feed(pkg, get, set, sections)
  scrye.onGmcp(pkg, function(json)
    local ok, t = pcall(scrye.json.decode, json)
    if not ok or type(t) ~= "table" then return end
    if t.guild ~= nil and t.guild ~= GUILD then return end
    local snap = get()
    if tonumber(t.full) == 1 then snap = {} end
    for k, v in pairs(t) do if k ~= "full" and k ~= "guild" then snap[k] = v end end
    set(snap)
    for _, s in ipairs(sections) do dirty[s] = true end
    schedule_flush()
  end)
end

feed("Guild.State", function() return ST end,   function(s) ST = s end,   { "status", "record" })
feed("Guild.Info",  function() return INFO end, function(s) INFO = s end, { "status", "form", "record" })

-- --------------------------------------------------------------- aliases
-- clhud: the status page in the output window, for a glance without the panel
scrye.addAlias{ pattern = "^clhud$", regex = true, run = function()
  dirty.status = true; flush()
  for line in (scrye.getState(P .. "status") or ""):gmatch("[^\n]+") do
    scrye.print("@{#7BC67B,bold}[cling]@{} " .. line)
  end
end }

-- --------------------------------------------------------------- panel
scrye.addPanel{
  title = "Changeling",
  width = 400,
  accent = "#7BC67B",          -- signature: protoplasm green
  tabs = {
    { title = "Status", widgets = {
        { type = "value", text = "", bind = P .. "summary", color = "info" },
        { type = "text",  bind = P .. "status" },
    } },
    { title = "Form",   widgets = { { type = "text", bind = P .. "form" } } },
    { title = "Record", widgets = { { type = "text", bind = P .. "record" } } },
  },
}

-- ------------------------------------------------------------------ init
for s in pairs(BUILDERS) do dirty[s] = true end
flush()
