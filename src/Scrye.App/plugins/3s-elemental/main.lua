-- 3S Elemental -- the Elemental guild HUD, built on the four Guild.* GMCP packages an
-- elemental character is sent. 3Scapes only.
--
-- Built from ONE capture of Joakim's own elemental (Keyser, Time form, 25 Nov 2026:
-- 3,113 messages, 12 minutes, most of it a Sea of Chaos run). The capture establishes the
-- shapes and how the numbers MOVE; what three of them are FOR came from Joakim the same
-- day (gtnl, reset and the Guild.Extra counts, below). A field nobody has named yet
-- carries the server's own word and nothing more.
--
-- Packages consumed:
--   Guild.Info   (about every 2 s; full:1 once, then only what changed)
--                energy / max_energy   the pool; the maximum follows the wisdom stat (a
--                                      temporary +5 wisdom took it from 1,576 to 1,586),
--                                      and it is only sent in the whole message, so the
--                                      pool can read over a maximum that has since grown.
--                                      Out of combat it climbed 1 a tick to the
--                                      maximum; in a fight it stepped down 13 at a time
--                                      and jumped back up.
--                lifetime_energy, used_energy, energy_to_spend, link_energy
--                                      four running totals that all rose by the same 48
--                                      a tick while fighting. Shown under those names.
--                gtnl                  gexp to the next guild level. It fell by exactly
--                                      what lifetime_energy rose by.
--                reset                 a STRING, "65.8": it rose 0.3 a tick all session and
--                                      went past 100 once, coming back at the remainder,
--                                      so it is a 0-100 clock of about eleven minutes.
--                                      When it turns, the charges of blasts, waves and
--                                      natural convergence are given back.
--   Guild.State  (sent whole) guild_title, form, size, current_vortex / max_vortex,
--                vortex_cost (not shown), and defs{}: one entry per defence - negation, barrier, wave,
--                siphon, engulfed, diffused, dissipated - a string of damage-type codes
--                while it is up (" RA IC AC FI CR TO SH MA SL ME") and "" while it is not,
--                plus eshield_rounds as a number.
--   Guild.Extra  (whole at the guild change, then only what changed) the settings: panic_hp
--                and panic_action, resolve_type / resolve_efficiency, prismatic_emission,
--                equipollent, consistency, eshield_remaining, and:
--                emit                  the power output SETTING: 1 at guild level 1, and
--                                      the most it can be set to grows with the level
--                                      (16 at Keyser's). The maximum is not on the wire.
--                blasts, waves, natural_convergence
--                                      charges, given back when the reset clock turns
--                engulfs, shrouds      charges too, on a DAILY reset (the game says so;
--                                      when in the day is not yet seen)
--                A spent charge arrives at once as a one-key message - a second capture
--                the same day caught { "waves": 6 } as a wave went up, with Guild.State's
--                defs.wave turning to "WV" in the same second.
--   Guild.Skills (added to the feed the same afternoon; PAGED, 4 pages at login, then a
--                one-key message a tick) energy_to_spend - the same figure Guild.Info
--                carries - and skills[]: sixteen records of id, name, current, total and
--                cost. cost is what the next level takes out of energy_to_spend, and -1
--                on a skill whose current has reached its total.
--
-- Every message is stamped with "guild". The same character can `gswap` to another guild
-- (Keyser arrived in this capture as a gentech), and these package NAMES are shared with
-- every other guild's, so a message stamped with another guild is left alone - and the
-- panel says which guild the character is in now, since what it shows is then old.

local P = "plugin." .. scrye.id .. "."
local GUILD = "elemental"

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

-- The bar: ten solid cells running from dark blue at the empty end to light blue at the
-- full end, each cell its own step of the way, so a fuller bar reaches a lighter blue.
-- The cells it has not reached are a dim shade.
local BAR_FROM, BAR_TO = { 0x1B, 0x3A, 0x8C }, { 0x9F, 0xD8, 0xFF }
local BAR = {}
for i = 1, 10 do
  local t = (i - 1) / 9
  local function mix(k) return math.floor(BAR_FROM[k] + (BAR_TO[k] - BAR_FROM[k]) * t + 0.5) end
  BAR[i] = string.format("@{#%02X%02X%02X}\u{2588}@{}", mix(1), mix(2), mix(3))
end

local function cells(pct)
  local n = math.max(0, math.min(10, math.floor(pct / 10 + 0.5)))
  return table.concat(BAR, "", 1, n) .. (n < 10 and col("dim", string.rep("\u{2591}", 10 - n)) or "")
end

-- the bar for a pool and "cur / max" beside it, the maximum in bright blue; with no
-- max, just the number
local BRIGHT = "#3DB8FF"
local function pool(cur, max)
  cur, max = N(cur), N(max)
  if max <= 0 then return esc(comma(cur)) end
  local pct = math.max(0, math.min(100, cur * 100 / max))
  return cells(pct) .. "  " .. col(pctcol(pct), comma(cur)) .. col(BRIGHT, " / " .. comma(max))
end

-- "panic_action" -> "Panic action": a field this plugin has no row for still gets shown
local function nice(key)
  local s = S(key):gsub("_", " ")
  return (s:gsub("^%l", string.upper))
end

local function squeeze(s) return (S(s):gsub("^%s+", ""):gsub("%s+$", ""):gsub("%s+", " ")) end

local function sorted_keys(t)
  local ks = {}
  for k in pairs(t) do ks[#ks + 1] = tostring(k) end
  table.sort(ks)
  return ks
end

-- ------------------------------------------------------------- snapshots
local INFO, ST, EX, SK = {}, {}, {}, {}
local now_guild = nil          -- the last guild stamp seen on any Guild.* message

-- ---------------------------------------------------------- the two clocks
-- Nothing on the wire is a rate, so the two that matter are measured here from what the
-- server sends: how fast lifetime_energy grows (which dates the next guild level), and
-- how fast the reset clock runs (which dates its next turn).
local WINDOW = 600             -- pace is judged over the last ten minutes
local samples = {}             -- { t, v } of lifetime_energy
local session0 = nil           -- the first lifetime_energy seen, and when
local reset0, reset_prev = nil, nil

local function sample_energy(v)
  local t = now()
  if not session0 or v < session0.v then session0 = { t = t, v = v }; samples = {} end
  local last = samples[#samples]
  if last and last.t == t then last.v = v else samples[#samples + 1] = { t = t, v = v } end
  while #samples > 2 and t - samples[2].t >= WINDOW do table.remove(samples, 1) end
end

-- energy per second over the window, or nil until half a minute has been watched
local function energy_rate()
  local a, b = samples[1], samples[#samples]
  if not a or b.t - a.t < 30 then return nil end
  return (b.v - a.v) / (b.t - a.t)
end

local function sample_reset(v)
  local t = now()
  -- a lower number than last time is the clock having turned: measure afresh from there
  if not reset0 or (reset_prev and v < reset_prev) then reset0 = { t = t, v = v } end
  reset_prev = v
end

-- seconds until the reset clock reads 100, or nil until it has been watched for 20 s
local function reset_eta()
  if not reset0 or not reset_prev then return nil end
  local dt, dv = now() - reset0.t, reset_prev - reset0.v
  if dt < 20 or dv <= 0 then return nil end
  return math.max(0, math.floor((100 - reset_prev) * dt / dv + 0.5))
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

-- the character has gswapped away: what the panel holds is from before
local function away_note(add)
  if now_guild and now_guild ~= GUILD then
    add(col("warning", "now in the " .. now_guild .. " guild") .. col("dim", " - this is from the last time as an elemental"))
    add("")
  end
end

-- one line for the panic setting; the share of max hp comes from Char.Vitals when it is there
local function panic_line()
  if N(EX.panic_hp) <= 0 and S(EX.panic_action) == "" then return nil end
  local hp, max = N(EX.panic_hp), N(scrye.getState("char.vitals.maxhp"))
  local line = hp > 0 and esc("below " .. comma(hp) .. " hp") or col("dim", "no hp set")
  if hp > 0 and max > 0 then line = line .. col("dim", "  (" .. math.floor(hp * 100 / max + 0.5) .. "% of max)") end
  if S(EX.panic_action) ~= "" then line = line .. "   " .. col("info", S(EX.panic_action)) end
  return line
end

-- ------------------------------------------------------------- Status tab
-- the defences in the order worth reading them; anything else in defs{} follows by name
local DEFS = {
  { "negation", "Negation" }, { "barrier", "Barrier" }, { "wave", "Wave" }, { "siphon", "Siphon" },
  { "engulfed", "Engulfed" }, { "diffused", "Diffused" }, { "dissipated", "Dissipated" },
}

-- Guild.Extra's charges: { key, label, given back by the reset clock }
local CHARGES = {
  { "blasts", "Blasts", true }, { "waves", "Waves", true }, { "natural_convergence", "Convergence", true },
  { "engulfs", "Engulfs", false }, { "shrouds", "Shrouds", false },
}

local function def_value(v)
  if type(v) == "number" then return v > 0 and col("success", comma(v)) or col("dim", "--") end
  local s = squeeze(v)
  return s ~= "" and col("success", s) or col("dim", "--")
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

  add("-- " .. (S(ST.guild_title) ~= "" and esc(S(ST.guild_title)) or "Elemental") .. " --")
  if S(ST.form) ~= "" then add("Form         " .. esc(S(ST.form))) end
  if has(ST, "size") then add("Size         " .. esc(comma(ST.size))) end
  if has(INFO, "energy") then add("Energy       " .. pool(INFO.energy, INFO.max_energy)) end
  if has(ST, "current_vortex") then
    add("Vortex       " .. pool(ST.current_vortex, ST.max_vortex))
  end
  if has(INFO, "reset") then
    local r = N(INFO.reset)
    local eta = reset_eta()
    add("Reset        " .. cells(math.max(0, math.min(100, r))) .. "  " .. esc(string.format("%.1f%%", r))
        .. (eta and ("   " .. col(BRIGHT, fmt_span(eta))) or ""))
  end

  local defs = type(ST.defs) == "table" and ST.defs or nil
  if defs then
    add("")
    add("-- Defences --")
    local shown = { eshield_rounds = true }
    for _, d in ipairs(DEFS) do
      shown[d[1]] = true
      if defs[d[1]] ~= nil then add(esc(string.format("%-12s ", d[2])) .. def_value(defs[d[1]])) end
    end
    for _, k in ipairs(sorted_keys(defs)) do
      if not shown[k] then add(esc(string.format("%-12s ", nice(k))) .. def_value(defs[k])) end
    end
    if defs.eshield_rounds ~= nil or has(EX, "eshield_remaining") then
      local rounds = N(defs.eshield_rounds)
      add("E-shield     " .. (rounds > 0 and col("success", comma(rounds) .. " rounds") or col("dim", "down"))
          .. (has(EX, "eshield_remaining") and col("dim", "   " .. comma(EX.eshield_remaining) .. " remaining") or ""))
    end
  end

  -- the charges: the three the reset clock gives back first, then the two on the daily reset
  local any = false
  for _, c in ipairs(CHARGES) do if has(EX, c[1]) then any = true end end
  if any then
    add("")
    add("-- Charges --")
    local function rows(on_reset)
      for _, c in ipairs(CHARGES) do
        if c[3] == on_reset and has(EX, c[1]) then
          local n = N(EX[c[1]])
          add(esc(string.format("%-12s ", c[2])) .. col(n > 0 and "text" or "error", comma(n)))
        end
      end
    end
    rows(true)
    if has(EX, "blasts") or has(EX, "waves") or has(EX, "natural_convergence") then
      local eta = reset_eta()
      add(col("dim", "  given back when the reset clock turns" .. (eta and (", in ~" .. fmt_span(eta)) or "")))
    end
    rows(false)
    if has(EX, "engulfs") or has(EX, "shrouds") then add(col("dim", "  given back at the daily reset")) end
  end

  local panic = panic_line()
  if panic then
    add("")
    add("-- Panic --")
    add("Fires        " .. panic)
  end
  scrye.setState(P .. "status", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Energy tab
local function build_energy()
  local L = {}
  local add = mkadd(L)
  if not has(INFO, "lifetime_energy") and not has(INFO, "gtnl") then
    add("waiting for Guild.Info...")
    scrye.setState(P .. "energy_tab", table.concat(L, "\n"))
    return
  end
  away_note(add)

  local rate = energy_rate()
  if has(INFO, "gtnl") then
    add("-- Next guild level --")
    local left = N(INFO.gtnl)
    add("Still owed   " .. (left > 0 and esc(comma(left) .. " gexp") or col("success", "ready")))
    if left > 0 then
      add("At this pace " .. ((rate and rate > 0) and esc("~" .. fmt_span(left / rate))
                              or col("dim", rate and "no gexp coming in" or "watching...")))
    end
    add("")
  end

  add("-- Energy --")
  for _, r in ipairs({ { "lifetime_energy", "Lifetime" }, { "used_energy", "Used" },
                       { "energy_to_spend", "To spend" }, { "link_energy", "Link" } }) do
    if has(INFO, r[1]) then add(esc(string.format("%-12s %15s", r[2], comma(INFO[r[1]])))) end
  end

  if session0 and has(INFO, "lifetime_energy") then
    add("")
    add("-- This session --")
    local gained, secs = N(INFO.lifetime_energy) - session0.v, now() - session0.t
    add("Gained       " .. esc(comma(gained)) .. col("dim", "   in " .. fmt_span(secs)))
    add("Pace         " .. (rate and esc(comma(rate * 3600) .. " / hour") .. col("dim", "   over the last " .. fmt_span(math.min(WINDOW, samples[#samples].t - samples[1].t)))
                             or col("dim", "watching...")))
  end
  scrye.setState(P .. "energy_tab", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Skills tab
-- One row per skill, in the server's own order: where it stands against its total, and
-- what the next level costs. A skill at its total is light green, name and level and
-- nothing more; one that can still grow is yellow when energy_to_spend covers the next
-- level and orange, with how much is missing, when it does not. Left-click any skill
-- for `info <skill>`; right-click one that can still grow to `enhance` it.
local CAN, CANNOT = "#FFD84A", "#FF9A3D"   -- yellow: affordable now; orange: not yet
local MAXED = "#8FE38F"                     -- light green: at its total

local function build_skills()
  local L = {}
  local add = mkadd(L)
  local list = SK.skills
  if type(list) ~= "table" then
    add("waiting for Guild.Skills...")
    scrye.setState(P .. "skills", table.concat(L, "\n"))
    return
  end
  away_note(add)
  local have = SK.energy_to_spend ~= nil and N(SK.energy_to_spend) or N(INFO.energy_to_spend)
  add("-- Skills --")
  add("To spend     " .. col(BRIGHT, comma(have)) .. col("dim", " energy"))
  add("")
  local rows, done, can = 0, 0, 0
  for _, k in ipairs(list) do
    if type(k) == "table" then
      rows = rows + 1
      local cur, total, cost = N(k.current), N(k.total), N(k.cost)
      local name = S(k.name) ~= "" and S(k.name) or nice(k.id)
      local pad = string.rep(" ", math.max(1, 15 - #name))
      local levels = string.format("%4s / %-4s", comma(cur), comma(total))
      -- The name is a link: left click asks the guild about the skill (`info <skill>`), and
      -- on one with a level still to buy the right button trains it (`enhance <skill>`).
      -- menu= is the 1.19 form, rclick= what an older host reads, and both go BEFORE
      -- click= so an old host still finds the left click. The id goes into a command only
      -- if it is a plain word - a comma or a bar in it would cut the markup short.
      local word = S(k.id) ~= "" and S(k.id) or name:lower()
      local safe = word:find("^[%w_%-]+$") ~= nil and not name:find("[,;|{}]")
      local function link(colour, trainable)
        if not safe then return col(colour, name) end
        local train = trainable and (",menu=Enhance " .. esc(name) .. "|enhance " .. word .. ",rclick=enhance " .. word) or ""
        return "@{" .. colour .. train .. ",click=info " .. word .. "}" .. esc(name) .. "@{}"
      end
      local line
      if cost < 0 or k.cost == nil then
        -- nothing to buy. At its total the row is light green, name and level and no more;
        -- short of it with no price, it is plain and says only that there is none.
        if cur >= total then
          done = done + 1
          line = link(MAXED, false) .. pad .. col(MAXED, levels)
        else
          line = link("text", false) .. pad .. esc(levels) .. col("dim", "  --")
        end
      else
        -- a level still to buy: the whole row is yellow when energy_to_spend covers it and
        -- orange, with how much is missing, when it does not.
        local afford = have >= cost
        local C = afford and CAN or CANNOT
        local c = comma(cost)
        line = link(C, true) .. pad .. col(C, levels) .. "  "
               .. string.rep(" ", math.max(0, 10 - #c))   -- costs line up on their last digit
               .. col(C, c)
        if afford then
          can = can + 1          -- the yellow says it; no words needed
        else
          line = line .. col("dim", "   short " .. comma(cost - have))
        end
      end
      add(line)
    end
  end
  if rows == 0 then add(col("dim", "(no skills reported)")) else
    add("")
    add(col("dim", rows .. " skills, " .. done .. " at their total")
        .. (can > 0 and col("success", "   " .. can .. " affordable now") or ""))
  end
  scrye.setState(P .. "skills", table.concat(L, "\n"))
end

-- -------------------------------------------------------------- Setup tab
-- Guild.Extra: the settings. Rows this plugin knows are laid out by hand; a key it has
-- never seen is still listed, under the server's own name, in the last block. The charges
-- are on the Status tab, where they are wanted in a fight.

local function build_setup()
  local L = {}
  local add = mkadd(L)
  if next(EX) == nil then
    add("waiting for Guild.Extra...")
    add(col("dim", "it is sent at login and when you change guild"))
    scrye.setState(P .. "setup", table.concat(L, "\n"))
    return
  end
  away_note(add)
  local shown = { eshield_remaining = true }
  for _, c in ipairs(CHARGES) do shown[c[1]] = true end
  local function take(k) shown[k] = true; return EX[k] end

  local panic = panic_line()
  take("panic_hp"); take("panic_action")
  if panic then
    add("-- Panic --")
    add("Fires        " .. panic)
    add("")
  end

  if has(EX, "resolve_type") or has(EX, "resolve_efficiency") then
    add("-- Resolve --")
    if has(EX, "resolve_type") then add("Type         " .. esc(S(take("resolve_type")))) end
    if has(EX, "resolve_efficiency") then
      local v = N(take("resolve_efficiency"))
      add("Efficiency   " .. col(pctcol(v), v .. "%"))
    end
    add("")
  end

  add("-- Emission --")
  if has(EX, "emit") then add("Emit         " .. esc(comma(take("emit"))) .. col("dim", "   power output")) end
  if has(EX, "prismatic_emission") then add("Prismatic    " .. esc(S(take("prismatic_emission")))) end
  if has(EX, "equipollent") then
    local v = S(take("equipollent"))
    add("Equipollent  " .. col(v:lower() == "off" and "dim" or "success", v))
  end
  if has(EX, "consistency") then
    local v = N(take("consistency"))
    add("Consistency  " .. col(pctcol(v), v .. "%"))
  end

  local rest = {}
  for _, k in ipairs(sorted_keys(EX)) do
    if not shown[k] and type(EX[k]) ~= "table" then rest[#rest + 1] = k end
  end
  if #rest > 0 then
    add("")
    add("-- Also sent --")
    for _, k in ipairs(rest) do add(esc(string.format("%-22s %s", k, S(EX[k])))) end
  end
  scrye.setState(P .. "setup", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- one-liner
local function summary()
  if next(ST) == nil and next(INFO) == nil then
    if now_guild and now_guild ~= GUILD then return "in the " .. now_guild .. " guild" end
    return "waiting for the elemental feed"
  end
  local bits = {}
  if now_guild and now_guild ~= GUILD then bits[#bits + 1] = "(now " .. now_guild .. ")" end
  if S(ST.form) ~= "" then bits[#bits + 1] = S(ST.form) end
  if has(INFO, "energy") then
    bits[#bits + 1] = "Energy " .. comma(INFO.energy) .. (N(INFO.max_energy) > 0 and ("/" .. comma(INFO.max_energy)) or "")
  end
  if has(ST, "current_vortex") then
    bits[#bits + 1] = "Vortex " .. comma(ST.current_vortex) .. (N(ST.max_vortex) > 0 and ("/" .. comma(ST.max_vortex)) or "")
  end
  if has(INFO, "reset") then bits[#bits + 1] = string.format("Reset %.0f%%", N(INFO.reset)) end
  return table.concat(bits, "   ")
end

-- the pool as numbers, for any plugin (or gauge) that wants an elemental's energy
local function publish_pool()
  if has(INFO, "energy") then
    scrye.setState(P .. "energy", tostring(N(INFO.energy)))
    scrye.setState(P .. "energy_max", tostring(N(INFO.max_energy)))
  end
end

local BUILDERS = { status = build_status, energy = build_energy, skills = build_skills, setup = build_setup }

flush = function()
  flush_pending = false
  for sec in pairs(dirty) do
    local b = BUILDERS[sec]
    if b then pcall(b) end
  end
  scrye.setState(P .. "summary", summary())
  publish_pool()
  dirty = {}
end

local function mark_all()
  for s in pairs(BUILDERS) do dirty[s] = true end
  schedule_flush()
end

-- ---------------------------------------------------------------- feeds
-- The guild stamp is read first, on every message, ours or not. After that an unpaged
-- message is either whole ("full": 1, which starts the snapshot over) or just the keys
-- that changed, merged in; a paged burst (page / pages - Guild.Skills) is gathered, its
-- lists joined page after page, and applied when the last page lands. This is the page
-- assembler the other guild HUDs use (docs/Plan-Viking-GMCP.md 3).
local function feed(pkg, on_snap)
  local snap, burst, bfull, expect, last_page = {}, nil, false, nil, 0
  local paged_keys = {}
  local function is_list(v) return type(v) == "table" and v[1] ~= nil end
  scrye.onGmcp(pkg, function(json)
    local ok, t = pcall(scrye.json.decode, json)
    if not ok or type(t) ~= "table" then return end
    if t.guild ~= nil then
      local g = tostring(t.guild):lower()
      if g ~= now_guild then now_guild = g; mark_all() end
      if g ~= GUILD then return end
    end
    local page, pages = tonumber(t.page), tonumber(t.pages)
    if not pages then
      if tonumber(t.full) == 1 then snap = {}; paged_keys = {} end
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

feed("Guild.Info", function(snap)
  INFO = snap
  if has(INFO, "lifetime_energy") then sample_energy(N(INFO.lifetime_energy)) end
  if has(INFO, "reset") then sample_reset(N(INFO.reset)) end
  dirty.status = true; dirty.energy = true   -- the reset clock dates the charges on Status
  schedule_flush()
end)

feed("Guild.State", function(snap)
  ST = snap
  dirty.status = true
  schedule_flush()
end)

feed("Guild.Skills", function(snap)
  SK = snap
  dirty.skills = true
  schedule_flush()
end)

feed("Guild.Extra", function(snap)
  EX = snap
  dirty.setup = true; dirty.status = true   -- charges, panic and the e-shield count show there
  schedule_flush()
end)

-- --------------------------------------------------------------- aliases
-- elhud: the status page in the output window, for a glance without the panel
scrye.addAlias{ pattern = "^elhud$", regex = true, run = function()
  dirty.status = true; flush()
  for line in (scrye.getState(P .. "status") or ""):gmatch("[^\n]+") do
    scrye.print("@{#5CC8E8,bold}[elemental]@{} " .. line)
  end
end }

-- --------------------------------------------------------------- panel
scrye.addPanel{
  title = "Elemental",
  width = 430,
  accent = "#5CC8E8",          -- signature: plane-light, a pale cyan
  tabs = {
    { title = "Status", widgets = {
        { type = "value", text = "", bind = P .. "summary", color = "info" },
        { type = "text",  bind = P .. "status" },
    } },
    { title = "Energy", widgets = { { type = "text", bind = P .. "energy_tab" } } },
    { title = "Skills", widgets = { { type = "text", bind = P .. "skills" } } },
    { title = "Setup",  widgets = { { type = "text", bind = P .. "setup" } } },
  },
}

-- ------------------------------------------------------------------ init
for s in pairs(BUILDERS) do dirty[s] = true end
flush()
