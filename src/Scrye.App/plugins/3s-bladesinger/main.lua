-- 3S Bladesinger -- the Bladesinger guild HUD, built on the three Guild.* GMCP packages a
-- bladesinger character is sent. 3Scapes only.
--
-- Built from ONE capture of Joakim's own bladesinger (Erebseth, 26 Nov 2026: 14,209
-- messages, 58 minutes - rune inscribing in the guild hall, then a Dungeon of Shame run
-- and a death). The capture establishes the shapes and how the numbers MOVE. What a field
-- is FOR is only said here where the numbers prove it; a field nobody has named yet carries
-- the server's own word and nothing more.
--
-- Packages consumed:
--   Guild.State  (every 2 s; full:1 once, then only what changed)
--                nekra / nekra_max     a guild pool (5,100 at its fullest). In a fight it
--                                      stepped down 26 a tick.
--                sokra / sokra_max     the other guild pool (3,700). Both maxima are only
--                                      in the whole message.
--                gxp, gtnl             guild xp into this level and still owed for the next:
--                                      gxp rose by exactly what gtnl fell by (1,267 a tick
--                                      in the fight), and the two always add up to the same
--                                      38,850,000 - the size of the level.
--                gxp_last_round        only in the whole message (0 there)
--                reset                 a 0-100 clock: it rose 2/27 a tick all session and
--                                      turned over once, so one turn is 45 minutes. When it
--                                      turns, the charges of the Blur power are given back.
--                portal_reset          another 0-100 clock: 0.25 a tick, a turn every
--                                      13 minutes 20 seconds, four turns in the capture.
--                                      Portal is a power with a limited number of uses per
--                                      turn of this clock. (What the two clocks are for is
--                                      from Joakim, 26 Nov 2026; the charges themselves are
--                                      not on the wire.)
--                evade_cmd, evade_hp, evade2_cmd, evade2_var, evade2_level
--                                      the evade settings; all empty or 0 in the capture.
--   Guild.Info   (full:1 once, then only what changed) glevel, runes_prepared and
--                runes_inscribed (running totals: inscribed rose 9 at a time in the rune
--                chamber), and eight regeneration figures - hp / sp / nekra / sokra, each
--                as x_regen and x_max_regen. Runes go on gear to set up regeneration (and
--                resistances, armour and so on): x_regen is what the runes worn give, and
--                x_max_regen is the most of it the character can use (Joakim, 26 Nov 2026).
--                So sokra_regen 23 against a sokra_max_regen of 18 is 18 in use and 5 of
--                rune going to waste - which is what the Status tab points out.
--   Guild.Extra  (the whole table every time) runes{}: thirty-three rune names, each with
--                how many are held.
--
-- Every message is stamped with "guild". These package NAMES are shared with every other
-- guild's, so a message stamped with another guild is left alone - and the panel says
-- which guild the character is in now, since what it shows is then old.

local P = "plugin." .. scrye.id .. "."
local GUILD = "bladesinger"

-- ---------------------------------------------------------------- helpers
local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end
local function col(c, s) return "@{" .. c .. "}" .. esc(s) .. "@{}" end
local function S(x) return x == nil and "" or tostring(x) end
local function N(x) return tonumber(x) or 0 end
local function has(t, k) return t[k] ~= nil end
local function now() return os.time() end

local function comma(n)
  local s = tostring(math.floor(N(n)))
  while true do
    local a, b = s:gsub("^(%-?%d+)(%d%d%d)", "%1,%2")
    s = a; if b == 0 then break end
  end
  return s
end

-- seconds -> "3d 4h" / "5h 12m" / "4m 05s" / "30s"
local function fmt_span(secs)
  secs = math.floor(N(secs))
  if secs >= 86400 then return string.format("%dd %dh", secs // 86400, (secs % 86400) // 3600) end
  if secs >= 3600  then return string.format("%dh %dm", secs // 3600, (secs % 3600) // 60) end
  if secs >= 60    then return string.format("%dm %02ds", secs // 60, secs % 60) end
  return secs .. "s"
end

-- a pool's colour: full is good
local function pctcol(v)
  v = N(v)
  if v >= 75 then return "success" end
  if v >= 40 then return "warning" end
  return "error"
end

-- A bar is ten solid cells running from a dark shade at the empty end to a light one at
-- the full end, each cell its own step of the way; the cells it has not reached are dim.
-- Each thing measured has its own hue, so the rows can be told apart at a glance.
local function ramp(from, to)
  local out = {}
  for i = 1, 10 do
    local t = (i - 1) / 9
    local function mix(k) return math.floor(from[k] + (to[k] - from[k]) * t + 0.5) end
    out[i] = string.format("@{#%02X%02X%02X}\u{2588}@{}", mix(1), mix(2), mix(3))
  end
  return out
end
local BAR_NEKRA = ramp({ 0x4A, 0x1F, 0x7A }, { 0xD9, 0xB8, 0xFF })   -- violet
local BAR_SOKRA = ramp({ 0x7A, 0x56, 0x12 }, { 0xFF, 0xE2, 0x8A })   -- gold
local BAR_CLOCK = ramp({ 0x3A, 0x46, 0x58 }, { 0xD6, 0xE2, 0xF2 })   -- steel
local BAR_LEVEL = ramp({ 0x1E, 0x5A, 0x3A }, { 0xA8, 0xF0, 0xC0 })   -- green

local function cells(bar, pct)
  local n = math.max(0, math.min(10, math.floor(pct / 10 + 0.5)))
  return table.concat(bar, "", 1, n) .. (n < 10 and col("dim", string.rep("\u{2591}", 10 - n)) or "")
end

-- the bar for a pool and "cur / max" beside it; with no max, just the number
local BRIGHT = "#D6E2F2"
local function pool(bar, cur, max)
  cur, max = N(cur), N(max)
  if max <= 0 then return esc(comma(cur)) end
  local pct = math.max(0, math.min(100, cur * 100 / max))
  return cells(bar, pct) .. "  " .. col(pctcol(pct), comma(cur)) .. col(BRIGHT, " / " .. comma(max))
end

-- "evade2_level" -> "Evade2 level": a field this plugin has no row for still gets shown
local function nice(key)
  local s = S(key):gsub("_", " ")
  return (s:gsub("^%l", string.upper))
end

local function sorted_keys(t)
  local ks = {}
  for k in pairs(t) do ks[#ks + 1] = tostring(k) end
  table.sort(ks)
  return ks
end

-- ------------------------------------------------------------- snapshots
local INFO, ST, EX = {}, {}, {}
local now_guild = nil          -- the last guild stamp seen on any Guild.* message

-- ------------------------------------------------------------ the clocks
-- Nothing on the wire is a rate, so how fast each 0-100 clock runs is measured here from
-- what the server sends. A lower number than last time is the clock having turned: the
-- pace it had before the turn is kept, so the time left never goes blank for it.
local function new_clock() return { first = nil, prev = nil, rate = nil } end
local CLOCKS = { reset = new_clock(), portal_reset = new_clock() }

local function clock_sample(c, v)
  local t = now()
  if not c.first or (c.prev and v < c.prev) then
    c.first = { t = t, v = v }
  else
    local dt, dv = t - c.first.t, v - c.first.v
    if dt >= 20 and dv > 0 then c.rate = dv / dt end
    -- measured over the last two minutes or so, not since login: a stretch the clock was
    -- not watched for (a relog) must not stay in the average for good
    if dt >= 120 then c.first = { t = t, v = v } end
  end
  c.prev = v
end

-- seconds until the clock reads 100, or nil until it has been watched for 20 s
local function clock_eta(c)
  if not c.prev or not c.rate or c.rate <= 0 then return nil end
  return math.max(0, math.floor((100 - c.prev) / c.rate + 0.5))
end

-- ------------------------------------------------------- guild xp pace
-- gxp counts from nothing at each guild level, so what is summed here is how much it has
-- GROWN since the plugin first saw it; a fall is a new level and adds nothing.
local WINDOW = 600             -- pace is judged over the last ten minutes
local samples = {}             -- { t, v } of the gxp gained since first seen
local gained, gxp_prev, since = 0, nil, nil

local function sample_gxp(v)
  local t = now()
  if not gxp_prev then since = t
  elseif v > gxp_prev then gained = gained + (v - gxp_prev) end
  gxp_prev = v
  local last = samples[#samples]
  if last and last.t == t then last.v = gained else samples[#samples + 1] = { t = t, v = gained } end
  while #samples > 2 and t - samples[2].t >= WINDOW do table.remove(samples, 1) end
end

-- gxp per second over the window, or nil until half a minute has been watched
local function gxp_rate()
  local a, b = samples[1], samples[#samples]
  if not a or b.t - a.t < 30 then return nil end
  return (b.v - a.v) / (b.t - a.t)
end

-- ------------------------------------------------------------ rune counts
-- what each rune stood at when it was first seen, so the tab can say what has changed
local rune0 = {}

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

-- the character has gswapped away: what the panel holds is from before
local function away_note(add)
  if now_guild and now_guild ~= GUILD then
    add(col("warning", "now in the " .. now_guild .. " guild") .. col("dim", " - this is from the last time as a bladesinger"))
    add("")
  end
end

-- ------------------------------------------------------------- Status tab
-- { key stem, label }: Guild.Info carries <stem>_regen and <stem>_max_regen for each
local REGEN = { { "hp", "HP" }, { "sp", "SP" }, { "nekra", "Nekra" }, { "sokra", "Sokra" } }

local function clock_row(label, key)
  local v = N(ST[key])
  local eta = clock_eta(CLOCKS[key])
  return label .. cells(BAR_CLOCK, math.max(0, math.min(100, v))) .. "  " .. esc(string.format("%.1f%%", v))
         .. (eta and ("   " .. col(BRIGHT, fmt_span(eta))) or "")
end

-- one line for an evade setting, or nil when nothing is set
local function evade_line(cmd, hp)
  if S(cmd) == "" and N(hp) <= 0 then return nil end
  local max = N(scrye.getState("char.vitals.maxhp"))
  local line = N(hp) > 0 and esc("below " .. comma(hp) .. " hp") or col("dim", "no hp set")
  if N(hp) > 0 and max > 0 then line = line .. col("dim", "  (" .. math.floor(N(hp) * 100 / max + 0.5) .. "% of max)") end
  if S(cmd) ~= "" then line = line .. "   " .. col("info", S(cmd)) end
  return line
end

local function build_status()
  local L = {}
  local add = mkadd(L)
  if next(ST) == nil and next(INFO) == nil then
    add("waiting for Guild.State...")
    if now_guild and now_guild ~= GUILD then add(col("dim", "this character is in the " .. now_guild .. " guild")) end
    scrye.setState(P .. "status", table.concat(L, "\n"))
    return
  end
  away_note(add)

  add("-- Bladesinger --")
  if has(INFO, "glevel") then add("Guild level  " .. esc(comma(INFO.glevel))) end
  if has(ST, "nekra") then add("Nekra        " .. pool(BAR_NEKRA, ST.nekra, ST.nekra_max)) end
  if has(ST, "sokra") then add("Sokra        " .. pool(BAR_SOKRA, ST.sokra, ST.sokra_max)) end
  -- the reset clock gives the Blur charges back; the portal clock, the Portal power's uses
  if has(ST, "reset") then add(clock_row("Blur reset   ", "reset")) end
  if has(ST, "portal_reset") then add(clock_row("Portal reset ", "portal_reset")) end

  local any = false
  for _, r in ipairs(REGEN) do
    if has(INFO, r[1] .. "_regen") or has(INFO, r[1] .. "_max_regen") then any = true end
  end
  if any then
    add("")
    -- Regeneration comes from the runes on your gear. "runes" is what the ones worn give,
    -- "max" the most of it you can use, "in use" the smaller of the two - and whatever the
    -- runes give beyond the max is wasted, which is worth a word in amber.
    add("-- Regeneration --")
    add(col("dim", string.format("%-12s %7s %7s %8s", "", "runes", "max", "in use")))
    for _, r in ipairs(REGEN) do
      local a, b = INFO[r[1] .. "_regen"], INFO[r[1] .. "_max_regen"]
      if a ~= nil or b ~= nil then
        local over = (a ~= nil and b ~= nil) and N(a) - N(b) or 0
        local used = (a ~= nil and b ~= nil) and comma(math.min(N(a), N(b))) or (a ~= nil and comma(a) or "--")
        add(esc(string.format("%-12s ", r[2]))
            .. col(over > 0 and "warning" or (N(a) > 0 and "success" or "dim"), string.format("%7s", a ~= nil and comma(a) or "--"))
            .. esc(string.format(" %7s", b ~= nil and comma(b) or "--"))
            .. col(BRIGHT, string.format(" %8s", used))
            .. (over > 0 and col("warning", "   " .. comma(over) .. " over the max") or ""))
      end
    end
  end

  local e1 = evade_line(ST.evade_cmd, ST.evade_hp)
  if e1 then
    add("")
    add("-- Evade --")
    add("Fires        " .. e1)
  end
  scrye.setState(P .. "status", table.concat(L, "\n"))
end

-- -------------------------------------------------------------- Guild tab
local function build_guild()
  local L = {}
  local add = mkadd(L)
  if not has(ST, "gxp") and not has(ST, "gtnl") then
    add("waiting for Guild.State...")
    scrye.setState(P .. "guild_tab", table.concat(L, "\n"))
    return
  end
  away_note(add)

  local rate = gxp_rate()
  local got, left = N(ST.gxp), N(ST.gtnl)
  add("-- Guild level" .. (has(INFO, "glevel") and (" " .. esc(comma(INFO.glevel))) or "") .. " --")
  if got + left > 0 then
    local pct = got * 100 / (got + left)
    add("Progress     " .. cells(BAR_LEVEL, pct) .. "  " .. esc(string.format("%.1f%%", pct)))
  end
  if has(ST, "gxp") then add("Earned       " .. esc(comma(got) .. " gxp")) end
  if has(ST, "gtnl") then
    add("Still owed   " .. (left > 0 and esc(comma(left) .. " gxp") or col("success", "ready")))
    if left > 0 then
      add("At this pace " .. ((rate and rate > 0) and esc("~" .. fmt_span(left / rate))
                              or col("dim", rate and "no gxp coming in" or "watching...")))
    end
  end
  if has(ST, "gxp_last_round") then add("Last round   " .. esc(comma(ST.gxp_last_round))) end

  if since then
    add("")
    add("-- This session --")
    add("Gained       " .. esc(comma(gained)) .. col("dim", "   in " .. fmt_span(now() - since)))
    add("Pace         " .. (rate and esc(comma(rate * 3600) .. " / hour") .. col("dim", "   over the last " .. fmt_span(math.min(WINDOW, samples[#samples].t - samples[1].t)))
                             or col("dim", "watching...")))
  end
  scrye.setState(P .. "guild_tab", table.concat(L, "\n"))
end

-- -------------------------------------------------------------- Runes tab
-- Every rune the guild names and how many are held, by name, in two columns; a rune with
-- none is dim. Beside a count that has moved since the plugin first saw it: by how much.
local function rune_cell(name, n)
  local d = n - (rune0[name] or n)
  local cell = col(n > 0 and "text" or "dim", string.format("%-14s %5s", name, comma(n)))
  if d > 0 then return cell .. col("success", string.format(" %-5s", "+" .. comma(d))) end
  if d < 0 then return cell .. col("warning", string.format(" %-5s", comma(d))) end
  return cell .. "      "
end

local function build_runes()
  local L = {}
  local add = mkadd(L)
  local runes = type(EX.runes) == "table" and EX.runes or nil
  if not runes and not has(INFO, "runes_inscribed") and not has(INFO, "runes_prepared") then
    add("waiting for Guild.Extra...")
    scrye.setState(P .. "runes", table.concat(L, "\n"))
    return
  end
  away_note(add)

  add("-- Runes --")
  if has(INFO, "runes_prepared") then add("Prepared     " .. esc(comma(INFO.runes_prepared))) end
  if has(INFO, "runes_inscribed") then add("Inscribed    " .. esc(comma(INFO.runes_inscribed))) end
  if runes then
    local names, total, kinds = {}, 0, 0
    for _, k in ipairs(sorted_keys(runes)) do
      if type(runes[k]) ~= "table" then
        names[#names + 1] = k
        total = total + N(runes[k])
        if N(runes[k]) > 0 then kinds = kinds + 1 end
      end
    end
    add("Held         " .. esc(comma(total)) .. col("dim", "   of " .. kinds .. " kinds"))
    add("")
    local half = math.ceil(#names / 2)
    for i = 1, half do
      local a, b = names[i], names[i + half]
      add(rune_cell(a, N(runes[a])) .. (b and ("  " .. rune_cell(b, N(runes[b]))) or ""))
    end
    if #names == 0 then add(col("dim", "(no runes reported)")) end
  end
  scrye.setState(P .. "runes", table.concat(L, "\n"))
end

-- -------------------------------------------------------------- Setup tab
-- The evade settings, laid out by hand; a key of Guild.State or Guild.Info this plugin
-- has never seen is still listed, under the server's own name, in the last block.
local KNOWN_STATE = { nekra = 1, nekra_max = 1, sokra = 1, sokra_max = 1, gxp = 1, gtnl = 1,
  gxp_last_round = 1, reset = 1, portal_reset = 1, evade_cmd = 1, evade_hp = 1,
  evade2_cmd = 1, evade2_var = 1, evade2_level = 1 }
local KNOWN_INFO = { glevel = 1, runes_prepared = 1, runes_inscribed = 1 }
for _, r in ipairs(REGEN) do KNOWN_INFO[r[1] .. "_regen"] = 1; KNOWN_INFO[r[1] .. "_max_regen"] = 1 end

local function build_setup()
  local L = {}
  local add = mkadd(L)
  if next(ST) == nil then
    add("waiting for Guild.State...")
    scrye.setState(P .. "setup", table.concat(L, "\n"))
    return
  end
  away_note(add)

  add("-- Evade --")
  add("Fires        " .. (evade_line(ST.evade_cmd, ST.evade_hp) or col("dim", "not set")))
  add("")
  add("-- Evade 2 --")
  add("Command      " .. (S(ST.evade2_cmd) ~= "" and col("info", S(ST.evade2_cmd)) or col("dim", "not set")))
  if has(ST, "evade2_var") then add("Var          " .. esc(S(ST.evade2_var))) end
  if has(ST, "evade2_level") then add("Level        " .. esc(S(ST.evade2_level))) end

  local rest = {}
  for _, k in ipairs(sorted_keys(ST)) do
    if not KNOWN_STATE[k] and type(ST[k]) ~= "table" then rest[#rest + 1] = { k, ST[k] } end
  end
  for _, k in ipairs(sorted_keys(INFO)) do
    if not KNOWN_INFO[k] and type(INFO[k]) ~= "table" then rest[#rest + 1] = { k, INFO[k] } end
  end
  for _, k in ipairs(sorted_keys(EX)) do
    if k ~= "runes" and type(EX[k]) ~= "table" then rest[#rest + 1] = { k, EX[k] } end
  end
  if #rest > 0 then
    add("")
    add("-- Also sent --")
    for _, r in ipairs(rest) do add(esc(string.format("%-22s %s", r[1], S(r[2])))) end
  end
  scrye.setState(P .. "setup", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- one-liner
local function summary()
  if next(ST) == nil and next(INFO) == nil then
    if now_guild and now_guild ~= GUILD then return "in the " .. now_guild .. " guild" end
    return "waiting for the bladesinger feed"
  end
  local bits = {}
  if now_guild and now_guild ~= GUILD then bits[#bits + 1] = "(now " .. now_guild .. ")" end
  for _, p in ipairs({ { "nekra", "Nekra" }, { "sokra", "Sokra" } }) do
    if has(ST, p[1]) then
      local max = N(ST[p[1] .. "_max"])
      bits[#bits + 1] = p[2] .. " " .. comma(ST[p[1]]) .. (max > 0 and ("/" .. comma(max)) or "")
    end
  end
  if has(ST, "reset") then bits[#bits + 1] = string.format("Blur %.0f%%", N(ST.reset)) end
  if has(ST, "portal_reset") then bits[#bits + 1] = string.format("Portal %.0f%%", N(ST.portal_reset)) end
  return table.concat(bits, "   ")
end

local BUILDERS = { status = build_status, guild = build_guild, runes = build_runes, setup = build_setup }

flush = function()
  flush_pending = false
  for sec in pairs(dirty) do
    local b = BUILDERS[sec]
    if b then pcall(b) end
  end
  scrye.setState(P .. "summary", summary())
  dirty = {}
end

local function mark_all()
  for s in pairs(BUILDERS) do dirty[s] = true end
  schedule_flush()
end

-- ---------------------------------------------------------------- feeds
-- The guild stamp is read first, on every message, ours or not. After that a message is
-- either whole ("full": 1, which starts the snapshot over) or just the keys that changed,
-- merged in. None of a bladesinger's three packages has been seen paged.
local function feed(pkg, on_snap)
  local snap = {}
  scrye.onGmcp(pkg, function(json)
    local ok, t = pcall(scrye.json.decode, json)
    if not ok or type(t) ~= "table" then return end
    if t.guild ~= nil then
      local g = tostring(t.guild):lower()
      if g ~= now_guild then now_guild = g; mark_all() end
      if g ~= GUILD then return end
    end
    if tonumber(t.full) == 1 then snap = {} end
    for k, v in pairs(t) do if k ~= "guild" and k ~= "full" then snap[k] = v end end
    pcall(on_snap, snap)
  end)
end

feed("Guild.State", function(snap)
  ST = snap
  if has(ST, "gxp") then sample_gxp(N(ST.gxp)) end
  for key, c in pairs(CLOCKS) do
    if has(ST, key) then clock_sample(c, N(ST[key])) end
  end
  dirty.status = true; dirty.guild = true; dirty.setup = true
  schedule_flush()
end)

feed("Guild.Info", function(snap)
  INFO = snap
  dirty.status = true; dirty.guild = true; dirty.runes = true; dirty.setup = true
  schedule_flush()
end)

feed("Guild.Extra", function(snap)
  EX = snap
  if type(EX.runes) == "table" then
    for k, v in pairs(EX.runes) do
      if rune0[k] == nil and type(v) ~= "table" then rune0[k] = N(v) end
    end
  end
  dirty.runes = true; dirty.setup = true
  schedule_flush()
end)

-- --------------------------------------------------------------- aliases
-- bshud: the status page in the output window, for a glance without the panel
scrye.addAlias{ pattern = "^bshud$", regex = true, run = function()
  dirty.status = true; flush()
  for line in (scrye.getState(P .. "status") or ""):gmatch("[^\n]+") do
    scrye.print("@{#AFC2D9,bold}[bladesinger]@{} " .. line)
  end
end }

-- --------------------------------------------------------------- panel
scrye.addPanel{
  title = "Bladesinger",
  width = 430,
  accent = "#AFC2D9",          -- signature: moonlit steel
  tabs = {
    { title = "Status", widgets = {
        { type = "value", text = "", bind = P .. "summary", color = "info" },
        { type = "text",  bind = P .. "status" },
    } },
    { title = "Guild", widgets = { { type = "text", bind = P .. "guild_tab" } } },
    { title = "Runes", widgets = { { type = "text", bind = P .. "runes" } } },
    { title = "Setup", widgets = { { type = "text", bind = P .. "setup" } } },
  },
}

-- ------------------------------------------------------------------ init
for s in pairs(BUILDERS) do dirty[s] = true end
flush()
