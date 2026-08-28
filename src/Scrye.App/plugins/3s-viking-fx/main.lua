-- 3S Viking Effects — the effect timer bar (first of the new GMCP plugin ideas,
-- docs/Plan-Viking-GMCP.md §5.1).
--
-- Guild.State carries fx.stfx: the complete status-effect list as "[aeg:205
-- skad:203 valg:182 ...]" — name:seconds pairs, refreshed with nearly every burst.
-- This panel renders them as countdown bars, ticks them down locally between
-- refreshes (the server is counting the same seconds), and warns — on screen and
-- optionally on the phone — when an effect drops under the warn threshold.
--
-- Design notes:
--   * The bar is scaled against the LONGEST value seen for that effect this
--     session (first sight of a fresh cast ≈ its full duration). Loaded mid-buff,
--     the bar starts conservative and corrects at the next re-cast. Session-only:
--     durations are game knowledge the server owns; better an honest short bar
--     than a stored guess.
--   * god{name, focus, expires_at} is shown WITHOUT a countdown: expires_at is a
--     wall-clock epoch and the plugin sandbox has no wall clock to subtract it
--     from. Name + focus only.
--   * An effect that vanishes from a fresh stfx list is dropped (dispelled or
--     expired server-side beats local arithmetic). One that ticks to 0 locally is
--     held for a few seconds as "gone?" awaiting the server's confirming refresh.
--   * The warn fires once per cast: crossing back above the threshold (a re-cast
--     lengthened it) re-arms it.
--   * fx.queue / fx.chan are surfaced raw when non-empty (rare, and better shown
--     than dropped).

local SP = "plugin." .. scrye.id .. "."

local GONE_HOLD = 5        -- seconds a locally-expired effect lingers as "gone?"
local BARW      = 10       -- characters in the countdown bar

local function note(s) scrye.print("@{#4BE4FF,bold}[fx]@{} " .. s) end

-- ---------- Guild.* page assembler (shared snippet; docs/Plan-Viking-GMCP.md §3) ----------
-- Guild packages arrive paged: {page=i, pages=N, full=1?} with list keys split across
-- pages. gasm(pkg, on_snap) subscribes to the package and calls on_snap(snap) with the
-- merged snapshot each time a burst completes:
--   * a message with no "pages" is unpaged: its keys merge into the snapshot directly;
--   * a burst whose pages carry full=1 REPLACES the paged keys of the snapshot
--     (keys only ever seen on the unpaged stream survive it); a burst without
--     full merges — keys it never mentions keep their last value;
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

-- ---------- settings ----------

local warn_at   = tonumber(scrye.store.get("warn")) or 30
local nf_expire = scrye.store.get("notify_expire") == "1"

local function publish_notify_state()
  scrye.setState(SP .. "notify",
    string.format("Expiring effects\tan effect drops under %ds left\t%s\tvfx notify %s",
      warn_at, nf_expire and "on" or "off", nf_expire and "off" or "on"))
end

-- ---------- effect state ----------

-- fx[name] = { secs =, maxseen =, warned =, gone = nil|seconds-since-local-zero }
local fx, fx_order = {}, {}
local seen_any = false          -- a Guild.State with fx has arrived at least once
local god_name, god_focus = "", ""
local extra = ""                -- fx.queue / fx.chan, raw, when non-empty

local function fmt_mmss(s)
  s = math.max(0, math.floor(s))
  return string.format("%d:%02d", math.floor(s / 60), s % 60)
end

local function bar(cur, max)
  if not max or max <= 0 then max = cur end
  if max <= 0 then return string.rep("-", BARW) end
  local fill = math.floor((cur / max) * BARW + 0.5)
  fill = math.max(0, math.min(BARW, fill))
  return string.rep("#", fill) .. string.rep("-", BARW - fill)
end

local function tokcol(secs)
  if secs <= warn_at then return "error"
  elseif secs <= warn_at * 4 then return "warning" end
  return "success"
end

local function publish()
  local lines = {}
  if god_name ~= "" then
    lines[#lines + 1] = string.format("@{dim}God@{}   @{info}%s@{}%s",
      (god_name:gsub("@", "@@")),
      god_focus ~= "" and ("  @{dim}focus@{} " .. god_focus:gsub("@", "@@")) or "")
    lines[#lines + 1] = ""
  end
  local names = {}
  for _, n in ipairs(fx_order) do if fx[n] then names[#names + 1] = n end end
  table.sort(names, function(a, b)
    if fx[a].secs ~= fx[b].secs then return fx[a].secs < fx[b].secs end
    return a < b
  end)
  if #names == 0 then
    lines[#lines + 1] = seen_any and "@{dim}no effects up@{}"
                        or "@{dim}waiting for Guild.State (fx.stfx)...@{}"
  else
    for _, n in ipairs(names) do
      local e = fx[n]
      if e.gone then
        lines[#lines + 1] = string.format("@{dim}%-8s %s  gone?@{}", n, string.rep("-", BARW))
      else
        lines[#lines + 1] = string.format("@{%s}%-8s %s %6s@{}",
          tokcol(e.secs), n, bar(e.secs, e.maxseen), fmt_mmss(e.secs))
      end
    end
  end
  if extra ~= "" then
    lines[#lines + 1] = ""
    lines[#lines + 1] = "@{dim}" .. extra:gsub("@", "@@") .. "@{}"
  end
  scrye.setState(SP .. "list", table.concat(lines, "\n"))
end

local function warn_check(name, e)
  if e.gone then return end
  if e.secs < warn_at then      -- strictly under: "drops under the threshold"
    if not e.warned then
      e.warned = true
      note(string.format("%s under %ds (%s left)", name, warn_at, fmt_mmss(e.secs)))
      if nf_expire then scrye.notify(string.format("%s expiring: %s left", name, fmt_mmss(e.secs))) end
    end
  else
    e.warned = false          -- re-cast lifted it back over the line: re-arm
  end
end

-- a fresh stfx list is the truth: update, add, and drop to match it
local function on_stfx(stfx)
  seen_any = true
  local now_up = {}
  for name, secs in tostring(stfx):gmatch("([%a_][%w_]*):(%d+)") do
    now_up[name] = tonumber(secs)
  end
  -- drop what the server no longer lists
  for name in pairs(fx) do
    if now_up[name] == nil then fx[name] = nil end
  end
  for i = #fx_order, 1, -1 do if not fx[fx_order[i]] then table.remove(fx_order, i) end end
  -- update / add the rest
  for name, secs in pairs(now_up) do
    local e = fx[name]
    if not e then
      e = { secs = secs, maxseen = secs, warned = false }
      fx[name] = e
      fx_order[#fx_order + 1] = name
    else
      e.secs = secs
      e.gone = nil
      if secs > (e.maxseen or 0) then e.maxseen = secs end
    end
    warn_check(name, e)
  end
  publish()
end

gasm("Guild.State", function(snap)
  if type(snap.fx) == "table" and snap.fx.stfx ~= nil then
    -- extra first: on_stfx publishes, and the queue/chan line rides that publish
    local q  = tostring(snap.fx.queue or "")
    local ch = tostring(snap.fx.chan or "")
    local parts = {}
    if q ~= "" then parts[#parts + 1] = "queue: " .. q end
    if ch ~= "" then parts[#parts + 1] = "chan: " .. ch end
    extra = table.concat(parts, "  ")
    on_stfx(snap.fx.stfx)
  end
  if type(snap.god) == "table" then
    god_name  = tostring(snap.god.name or "")
    god_focus = tostring(snap.god.focus or "")
    publish()
  end
end)

-- local 1 s tick-down between server refreshes (the server counts the same seconds;
-- the next stfx refresh corrects any drift)
scrye.every(1, function()
  local dirty = false
  for name, e in pairs(fx) do
    if e.gone then
      e.gone = e.gone + 1
      if e.gone > GONE_HOLD then
        fx[name] = nil
        for i = #fx_order, 1, -1 do if fx_order[i] == name then table.remove(fx_order, i) end end
      end
      dirty = true
    elseif e.secs > 0 then
      e.secs = e.secs - 1
      if e.secs <= 0 then e.secs = 0; e.gone = 0 end
      warn_check(name, e)
      dirty = true
    end
  end
  if dirty then publish() end
end)

-- ---------- commands ----------

local function fx_status()
  publish()
  note(string.format("warn at %ds, phone notify %s (vfx warn <secs> | vfx notify on|off)",
    warn_at, nf_expire and "on" or "off"))
  for line in (scrye.getState(SP .. "list") or ""):gmatch("[^\n]+") do note(line) end
end

scrye.addAlias{ pattern = "^vfx$", regex = true, run = function() fx_status() end }
scrye.addAlias{ pattern = "^vfx (.+)$", regex = true, run = function(rest)
  local low = (rest or ""):gsub("^%s+", ""):gsub("%s+$", ""):lower()
  local w = low:match("^warn%s+(%d+)$")
  if w then
    warn_at = tonumber(w)
    scrye.store.set("warn", w)
    -- new line, new judgement: re-arm every warn against the new threshold
    for _, e in pairs(fx) do e.warned = false end
    note("warn threshold: " .. warn_at .. "s")
    publish_notify_state()
    publish()
    return
  end
  local nv = low:match("^notify%s+(%w+)$")
  if nv == "on" or nv == "off" then
    nf_expire = (nv == "on")
    scrye.store.set("notify_expire", nf_expire and "1" or "0")
    -- fresh setting, fresh judgement: an effect already under the line gets one
    -- more warn (and now the phone hears it) instead of staying silently consumed
    for _, e in pairs(fx) do e.warned = false end
    note("phone notify: " .. nv)
    publish_notify_state()
    return
  end
  note("usage: vfx | vfx warn <secs> | vfx notify on|off")
end }

-- ---------- panel ----------

scrye.addPanel{
  title = "Viking Effects",
  width = 260,
  accent = "#4BE4FF",          -- signature: seid cyan (validated accent set)
  tabs = {
    { title = "Effects", widgets = {
        { type = "text", bind = SP .. "list" },
        { type = "label", text = "vfx warn <secs> sets the red line", color = "dim" },
    } },
  },
}

-- ---------- load ----------

publish()
publish_notify_state()
note(string.format("loaded - warn at %ds ('vfx' for status).", warn_at))
