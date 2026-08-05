-- 3S Market -- 3Scapes Viking market arbitrage finder (Scrye port of ThreeS_Market)
--
-- `mkref` (or the panel's Refresh button) runs `vtrade goods <resource>` for every
-- good with the output gagged, parses the buy/sell prices per town, and renders the
-- best trade route per good -- buy cheap in one town, sell dear in another -- ranked
-- by profit per unit, in the HUD panel. Results persist across restarts.
--
-- NOTE: dropped / simplified vs the MUSHclient original:
--   * `markwin` show/hide alias DROPPED -- the HUD manages panel visibility.
--   * The entire auto-trader subsystem DROPPED (the `atrade` alias and config,
--     scalper/restock/flush/clearing logic, cart-dispatch cooldown handling,
--     session stats, Log/Stats tabs, 3s_autotrade.log file) -- it relied on
--     click hotspots, inputboxes, io.* log files, os.time and the MIP companion
--     plugin's broadcasts, and is outside this port's scope (market scan + report).
--   * Town-click dispatch, Units/Escort controls DROPPED (no per-row hotspots in
--     the declarative panel API); the ranked report is display-only.
--   * The "updated HH:MM" stamp DROPPED (no clock in the sandbox); the status line
--     says whether data is from this session or restored from the last one.
--   * Low-stock towns were orange in the miniwindow; here they are marked with "*"
--     (the 2nd/3rd-best town is still shown when the better ones are low, as before).
--   * Scan sends were spaced 0.4 s apart; scrye.after has 1 s granularity, so they
--     go out 1 s apart. The settle check (finish when output goes quiet, hard cap)
--     is kept, plus an early finish once the last good's reply has been seen.
--   * The auto-refresh on the "[Viking-Trade] ... %" market tick is kept (debounced,
--     quiet), but only after the first manual refresh this session -- the original
--     gated it on "window visible or auto-trader on", neither of which exists here.
--   * Best sell prices are still published for other plugins: world variable
--     "prices" (same "cmd=price;..." format) and state plugin.3s-market.prices.

local P = "plugin." .. scrye.id .. "."

-- the tradeable goods (command word for `vtrade goods <word>`)
local RES = {
  { name = "Timber",      cmd = "timber"     },
  { name = "Iron",        cmd = "iron"       },
  { name = "Grain",       cmd = "grain"      },
  { name = "Furs",        cmd = "furs"       },
  { name = "Fish",        cmd = "fish"       },
  { name = "Mead",        cmd = "mead"       },
  { name = "Sunstone",    cmd = "sunstone"   },
  { name = "Runestones",  cmd = "runestones" },
  { name = "Spoils",      cmd = "spoils"     },
  { name = "Ore",         cmd = "ore"        },
  { name = "Salted Fish", cmd = "salted"     },
  { name = "Bread",       cmd = "bread"      },
  { name = "Fine Furs",   cmd = "fine"       },
  { name = "Tools",       cmd = "tools"      },
  { name = "Gemstones",   cmd = "gems"       },
  { name = "Finery",      cmd = "finery"     },
}
local DISPLAY = {}                       -- lower cmd -> nice name
for _, r in ipairs(RES) do DISPLAY[r.cmd] = r.name end
local LAST_NAME = RES[#RES].name:lower() -- header of the final reply => scan nearly done

local LOWSTOCK = 100   -- below this stock, also show the next-best town (marked *)

-- ====================== auto-trader constants ======================
local DCMD = {}                          -- market header name (lowercased) -> short vtrade word
for _, r in ipairs(RES) do DCMD[r.name:lower()] = r.cmd end
local function disp_cmd(res) return DCMD[res] or res end

-- display town name -> the word vtrade expects (default: first word lowercased)
local TOWNCMD = { ["lodbrok's hold"] = "lodbrok", ["lodbrok's hol"] = "lodbrok" }
local function town_cmd(town)
  local key = (town or ""):lower()
  return TOWNCMD[key] or key:match("^%a+") or key
end

local function comma(n)
  local s = tostring(math.floor(tonumber(n) or 0))
  while true do local a, b = s:gsub("^(%d+)(%d%d%d)", "%1,%2"); s = a; if b == 0 then break end end
  return s
end

-- refined goods (towns only buy these) - matched by market-key name and by cmd
local REFINED = { ["salted fish"] = true, salted = true, ["fine furs"] = true, fine = true,
                  bread = true, finery = true, tools = true }
-- special commodities (not raw materials): sellable, but keep a small reserve
local SPECIAL = { runestones = true, gemstones = true, gems = true }
-- raw materials the auto-buyer will restock up to the Raw> buffer when they run low
local RAWBUILD = { timber = true, iron = true, furs = true, grain = true, mead = true,
                   fish = true, sunstone = true, spoils = true }

local function trim(s) return (s or ""):gsub("^%s+", ""):gsub("%s+$", "") end
-- escape MUD-sourced text before embedding it in colour markup
local function esc(x) return (tostring(x or ""):gsub("@", "@@")) end
local function titlecase(s)
  return (s:gsub("(%a)([%w]*)", function(a, b) return a:upper() .. b:lower() end))
end

-- ====================== captured market data ======================
local market  = {}      -- market[resource][town] = { buy=, sup=, sell=, dem=, aff= }
local results = {}      -- computed arbitrage rows, sorted by profit desc
local cur_res = nil     -- resource currently being parsed

local scanning       = false  -- a refresh is in flight (gag window open)
local quiet          = false  -- suppress the refresh notes (auto/background refreshes)
local got_data       = false  -- new market lines arrived since the last settle check
local checks         = 0      -- settle-check counter (hard cap)
local settle_running = false
local scan_token     = 0      -- invalidates timers from an abandoned scan
local update_pending = false  -- a market-tick refresh is already queued (debounce)
local user_refreshed = false  -- at least one manual refresh this session

local mk_refreshed_at = 0     -- os.time() the market data was last finalised (for the auto-trader)
local connected = true        -- tracked via onConnect/onDisconnect. Starts true: a plugin
                              -- (re)load mid-session never receives an onConnect, and the
                              -- auto-trader must not sit idle waiting for one.
local mk_last_dispatch = 0    -- os.time() of the last auto-dispatch (self-rescan guard)
local last_status = ""        -- last status line passed to mk_render (for re-rendering in place)

-- ====================== auto-trader settings (persisted via scrye.store) ======================
local function sget(k) local x = scrye.store.get(k); if x == nil or x == "" then return nil end; return x end
local function sset(k, v) scrye.store.set(k, tostring(v)) end

local at = {
  on      = (sget("at_on") == "1"),                 -- default OFF (safety)
  reserve = tonumber(sget("at_reserve")) or 5000,   -- keep-back daler (scalper won't spend below this)
  margin  = tonumber(sget("at_margin"))  or 1,      -- min profit/unit for arbitrage buys
  stock   = tonumber(sget("at_stock"))   or 300,    -- Raw> buffer to keep for raw building materials
  carts   = tonumber(sget("at_carts"))   or 0,      -- cap (0 = auto from Trading Post tier)
  refined = (sget("at_refined") ~= "0"),            -- also sell refined goods (default yes)
  min_pct = tonumber(sget("at_minpct")) or 70,      -- min % of cart capacity before sending a cart
  min_rel = tonumber(sget("at_minrel")) or 40,      -- min % of the best available load's value
  keep    = tonumber(sget("at_keep"))   or 20,      -- units of EVERY good to keep (mission reserve)
  scalp   = (sget("at_scalp") ~= "0"),              -- buy-low/sell-high scalp (default on)
  restock = (sget("at_restock") == "1"),            -- actively buy raws to top up Raw> (default off)
  flush   = tonumber(sget("at_flush")) or 500,      -- pile >= this jumps the queue, ignores value floor (0=off)
  soft    = tonumber(sget("at_soft"))     or 70,    -- % full: rank by biggest pile, stop scalping
  full    = tonumber(sget("at_full"))     or 90,    -- % full that switches to clearing mode
  clear_pct = tonumber(sget("at_clearpct")) or 25,  -- min cart fill % while clearing
  escort  = tonumber(sget("at_escort")) or 5,       -- escort size for auto-dispatched carts
  pending = 0, last_carts = nil, cd_wait = false, pending_check = false,
  stats = { buys = 0, sells = 0, spent = 0, earned = 0, since = os.time(), recent = {} },
  exempt = {},
}
for w in (sget("at_exempt") or ""):gmatch("[^,]+") do at.exempt[w] = true end

-- manual dispatch cart size (the MUSHclient window had a Units hotspot, 20-350)
local MK_UNITS_MIN, MK_UNITS_MAX = 20, 350
local mk_units = math.max(MK_UNITS_MIN, math.min(MK_UNITS_MAX, tonumber(sget("mk_units")) or 100))

-- forward declarations (mk_finish / the feed watch call these before they're defined)
local at_schedule, at_draw, auto_trade_tick, publish_dispatch

-- forward declaration (mk_header schedules an early finish)
local start_settle

local function mk_header(res)
  cur_res = trim(res):lower()
  market[cur_res] = {}             -- fresh data for this good
  got_data = true
  if cur_res == LAST_NAME then     -- last good replying: finish as soon as it settles
    start_settle(scan_token)
  end
end

local function mk_row(kind, town, price, qty, aff)
  if not cur_res then return end
  got_data = true
  town = trim(town)
  if town == "" then return end
  local m = market[cur_res]
  m[town] = m[town] or {}
  if kind == "buy" then
    m[town].buy = tonumber(price); m[town].sup = tonumber(qty)
  else
    m[town].sell = tonumber(price); m[town].dem = tonumber(qty)
  end
  m[town].aff = trim(aff)
end

-- rank towns to buy (cheapest) and to sell (dearest) per good, keeping stock.
-- ties break toward more stock so the headline town is also the best supplied.
local function mk_compute()
  results = {}
  for res, towns in pairs(market) do
    local buys, sells = {}, {}
    for town, d in pairs(towns) do
      if d.buy  then buys[#buys + 1]   = { price = d.buy,  town = town, qty = d.sup or 0 } end
      if d.sell then sells[#sells + 1] = { price = d.sell, town = town, qty = d.dem or 0 } end
    end
    if #sells > 0 then      -- include sell-only goods (produced, no town supplies them)
      table.sort(sells, function(a, b)
        if a.price ~= b.price then return a.price > b.price end return a.qty > b.qty end)
      local profit = nil
      if #buys > 0 then
        table.sort(buys, function(a, b)
          if a.price ~= b.price then return a.price < b.price end return a.qty > b.qty end)
        profit = sells[1].price - buys[1].price
      end
      -- Normalize to number-or-nil. A MoonSharp codegen quirk in this nested-loop +
      -- closures function can leave the unassigned `profit` aliasing a table for
      -- sell-only goods, which then blows up "a.profit > b.profit" (sort) and
      -- "r.profit >= 0" (render) with "compare number with table". Force it clean.
      if type(profit) ~= "number" then profit = nil end
      results[#results + 1] = {
        res = DISPLAY[res] or titlecase(res), cmd = res, buys = buys, sells = sells, profit = profit,
      }
    end
  end
  -- arbitrage goods (with a buy side) first by profit; sell-only goods after, by sell price
  table.sort(results, function(a, b)
    if a.profit and b.profit then return a.profit > b.profit end
    if a.profit ~= nil then return true end
    if b.profit ~= nil then return false end
    return a.sells[1].price > b.sells[1].price
  end)
end

-- ====================== persistence (scrye.store, strings only) ======================
local function mk_serialize()
  local out = {}
  for res, towns in pairs(market) do
    for town, d in pairs(towns) do
      out[#out + 1] = table.concat({
        res, town,
        d.buy and tostring(d.buy) or "", d.sup and tostring(d.sup) or "",
        d.sell and tostring(d.sell) or "", d.dem and tostring(d.dem) or "",
        (d.aff or ""):gsub("[\t\n]", " "),
      }, "\t")
    end
  end
  return table.concat(out, "\n")
end

local function mk_deserialize(s)
  local m = {}
  for line in s:gmatch("[^\n]+") do
    local res, town, buy, sup, sell, dem, aff =
      line:match("^([^\t]*)\t([^\t]*)\t([^\t]*)\t([^\t]*)\t([^\t]*)\t([^\t]*)\t(.*)$")
    if res and res ~= "" and town and town ~= "" then
      m[res] = m[res] or {}
      m[res][town] = {
        buy = tonumber(buy), sup = tonumber(sup),
        sell = tonumber(sell), dem = tonumber(dem), aff = aff,
      }
    end
  end
  return m
end

-- ====================== report rendering (HUD text widget) ======================
local FMT = "%-11s %4s %-14s %6s  %4s %-14s %6s  %s"

local function cell(e)                 -- price, town, stock strings for one side
  if not e then return "-", "", "" end
  local q = tostring(e.qty) .. (e.qty < LOWSTOCK and "*" or "")
  return tostring(e.price), e.town:sub(1, 14), q
end

-- extra towns to show for a side: 2nd if the 1st is low (<100), 3rd if the 1st two are both low
local function extra_towns(list)
  local t1 = list and list[1]
  if not t1 then return nil, nil end
  local t2 = (t1.qty < LOWSTOCK) and list[2] or nil
  local t3 = (t2 and t2.qty < LOWSTOCK) and list[3] or nil
  return t2, t3
end

local function mk_render(status)
  last_status = status or last_status
  scrye.setState(P .. "status", last_status)
  local lines = {}
  lines[#lines + 1] = string.format(FMT, "Good", "Buy", "Town", "Stk", "Sell", "Town", "Stk", "Profit")
  if #results == 0 then
    lines[#lines + 1] = "No data - click Refresh (or type mkref)."
  else
    for _, r in ipairs(results) do
      local bp, bt, bq = cell(r.buys[1])
      local sp, st, sq = cell(r.sells[1])
      local profit
      if r.profit then
        profit = (r.profit >= 0 and "+" or "") .. r.profit
      else
        profit = "sell"          -- sell-only good, no buy side
      end
      -- held goods are marked "#" (the MUSHclient window tinted them blue instead)
      local label = at.exempt[disp_cmd(r.cmd)] and ("#" .. r.res) or r.res
      lines[#lines + 1] = string.format(FMT, label, bp, bt, bq, sp, st, sq, profit)
      local b2, b3 = extra_towns(r.buys)
      local s2, s3 = extra_towns(r.sells)
      if b2 or s2 then
        local xbp, xbt, xbq = "", "", ""
        if b2 then xbp, xbt, xbq = cell(b2) end
        local xsp, xst, xsq = "", "", ""
        if s2 then xsp, xst, xsq = cell(s2) end
        lines[#lines + 1] = string.format(FMT, "  or", xbp, xbt, xbq, xsp, xst, xsq, "")
      end
      if b3 or s3 then
        local xbp, xbt, xbq = "", "", ""
        if b3 then xbp, xbt, xbq = cell(b3) end
        local xsp, xst, xsq = "", "", ""
        if s3 then xsp, xst, xsq = cell(s3) end
        lines[#lines + 1] = string.format(FMT, "  or", xbp, xbt, xbq, xsp, xst, xsq, "")
      end
    end
    lines[#lines + 1] = ""
    lines[#lines + 1] = "* stock under " .. LOWSTOCK .. " (next-best town shown)   # held (atrade exempt)"
  end
  scrye.setState(P .. "report", table.concat(lines, "\n"))
  if publish_dispatch then publish_dispatch() end
end

-- ====================== refresh scan ======================
local mk_finish

-- keep waiting while new market data is still arriving; finalize when it settles
start_settle = function(tok)
  if settle_running then return end
  settle_running = true
  local function step()
    if not scanning or tok ~= scan_token then settle_running = false; return end
    checks = checks + 1
    if got_data and checks < 25 then     -- output still flowing: wait another beat
      got_data = false
      scrye.after(2, step)
    else
      settle_running = false
      mk_finish()
    end
  end
  scrye.after(2, step)
end

mk_finish = function()
  if not scanning then return end
  scanning = false                       -- close the gag window
  mk_compute()
  -- publish best sell prices for other plugins (e.g. Viking Status settler upkeep)
  local px = {}
  for _, r in ipairs(results) do
    local p = (r.sells and r.sells[1] and r.sells[1].price)
           or (r.buys and r.buys[1] and r.buys[1].price)
    if p then px[#px + 1] = r.cmd .. "=" .. p end
  end
  scrye.setVariable("prices", table.concat(px, ";"))
  scrye.setState(P .. "prices", table.concat(px, ";"))
  scrye.store.set("market", mk_serialize())     -- survive restarts
  mk_refreshed_at = os.time()
  mk_render("updated this session - " .. #results .. " goods")
  if not quiet then scrye.print("refreshed " .. #results .. " goods") end
  if at.on and at_schedule then at_schedule() end   -- dispatch on the freshly-refreshed prices
end

local function mk_refresh(is_quiet)
  if scanning then
    if not is_quiet then scrye.print("refresh already in progress") end
    return
  end
  scanning = true
  quiet = is_quiet and true or false
  if not is_quiet then user_refreshed = true end
  cur_res = nil
  market = {}            -- drop stale data so a good that stops responding disappears
  got_data = false
  checks = 0
  settle_running = false
  scan_token = scan_token + 1
  local tok = scan_token
  -- space the sends out (original throttled at 0.4 s; scrye.after ticks at 1 s)
  for i, r in ipairs(RES) do
    local cmd = "vtrade goods " .. r.cmd
    if i == 1 then
      scrye.send(cmd)
    else
      scrye.after(i - 1, function()
        if scanning and tok == scan_token then scrye.send(cmd) end
      end)
    end
  end
  -- settle fallback in case the last good's header never arrives
  scrye.after(#RES + 2, function()
    if scanning and tok == scan_token then start_settle(tok) end
  end)
  -- absolute hard cap: never leave the gag window open forever
  scrye.after(#RES + 45, function()
    if scanning and tok == scan_token then mk_finish() end
  end)
  scrye.setState(P .. "status", "refreshing market...")
  if not quiet then scrye.print("refreshing market...") end
end

-- ====================== gag + parse (replaces the "market" trigger group) ======================
-- while a scan is in flight, the vtrade-goods block is parsed and gagged
-- (return false), exactly like the original's omit_from_output trigger group,
-- which was only enabled during a refresh.
--
-- The whole vtrade block is wrapped in a "-~*  ...  *~-" decoration frame, e.g.
--   -~*   Timber - Market Overview                       *~-
--   -~*  Lodbrok's Hol    22  413 avail export++ Vinur    *~-
-- The MUSHclient regexes were unanchored, so they matched the town/price/qty
-- substring inside the frame. We strip the frame first, then match on the
-- clean text, and gag every framed line (the block is all ours during a scan).

-- remove the leading "-~*" and trailing "*~-" decoration (and the padding spaces)
local function strip_frame(s)
  s = s:gsub("^%s*%-~%*%s*", "")   -- leading  "-~*" + spaces
  s = s:gsub("%s*%*~%-%s*$", "")   -- trailing "*~-" + spaces
  return s
end

-- match a market row on frame-stripped text: town, price, qty, then the keyword
-- ("avail"/"wants") on a word boundary, then the affinity remainder.
--
-- IMPORTANT: MoonSharp (Scrye's Lua engine) aborts with "pattern too complex" when
-- a pattern has to backtrack a lot -- e.g. a monolithic "town + two numbers + word"
-- pattern run against a "sold out" row (one number, no keyword) or a column-header
-- line. So we (1) gate on a cheap plain-text find of the keyword, then (2) use small,
-- lightly-backtracking patterns for the numbers and the town separately.
local function rowmatch(line, word)
  if not line:find(word, 1, true) then return nil end             -- cheap gate: keyword must be present
  local price, qty, tail = line:match("(%d+)%s+(%d+)%s+" .. word .. "(.*)$")
  if not price then return nil end
  local town = line:match("^%s*([%a][%a' ]*)%s+%d") or ""          -- leading letters/'/space run before the price
  town = (town:gsub("%s+$", ""))                                    -- drop the greedy town's trailing spaces
  if tail == "" then return town, price, qty, "" end
  local aff = tail:match("^[^%w](.*)$")   -- word boundary: "available" (tail "able...") is rejected
  if aff == nil then return nil end
  return town, price, qty, aff
end

scrye.onLine(function(line)
  if not scanning then return end
  -- our own scan commands, if echoed
  if line:match("^%s*vtrade goods %a") then return false end
  -- only touch the framed vtrade block; leave everything else (tells, etc.) alone
  if not line:match("^%s*%-~%*") then return end
  local clean = strip_frame(line)
  -- "<Good> - Market Overview" header (gate on a plain find first: the header pattern
  -- would otherwise backtrack over long all-letter column-header lines and MoonSharp
  -- would abort with "pattern too complex")
  if clean:find("Market Overview", 1, true) then
    local res = clean:match("^([%a][%a ]*)%s*%-%s*Market Overview")
    if res then
      pcall(mk_header, res)
      return false
    end
  end
  -- buy row:  Town  <price>  <qty> avail [affinity]
  local town, price, qty, aff = rowmatch(clean, "avail")
  if town then
    pcall(mk_row, "buy", town, price, qty, aff)
    return false
  end
  -- sell row: Town  <price>  <qty> wants [affinity]
  town, price, qty, aff = rowmatch(clean, "wants")
  if town then
    pcall(mk_row, "sell", town, price, qty, aff)
    return false
  end
  -- The remaining vtrade decoration. Only the lines the MUSHclient version gagged are
  -- hidden here: a blanket `return false` also swallowed unrelated framed output (channel
  -- banners, `vbuild list`) whenever a background auto-trader scan happened to be running.
  if clean:find("price", 1, true) and clean:match("price%s+%d+%s+daler") then return false end
  if clean:find("Trading Post tier", 1, true) then return false end
  if clean:find("Best places to", 1, true) then return false end
  if clean:find("Settlement", 1, true) and clean:match("Settlement%s+Price") then return false end
  if clean:match("^[%s%-=~%*%+_|]*$") then return false end   -- separators / empty frame lines
  return
end)

-- market tick: the periodic price-update line carries percentages (e.g. "Mead +3%").
-- Other [Viking-Trade] lines (cart returns etc.) have no percentages and are ignored.
-- Debounced (the trade update is a burst of lines); quiet background refresh; only
-- after the user has refreshed once this session (stand-in for the original's
-- "window visible or auto-trader on" gate).
scrye.addTrigger{
  pattern = [[\[Viking-Trade\].*\d%]],
  regex = true,
  run = function()
    if not user_refreshed then return end
    -- our own `vtrade dispatch` echo comes back as a [Viking-Trade] line with a
    -- percentage in it; without this guard every auto-dispatch kicks off a full rescan.
    if os.time() - mk_last_dispatch < 10 then return end
    if update_pending or scanning then return end
    update_pending = true
    scrye.after(2, function()
      update_pending = false
      if not scanning then mk_refresh(true) end
    end)
  end,
}

-- ====================== auto-trader ======================
local function note(s) scrye.print("@{#FFD028,bold}[auto]@{} " .. s) end

-- read the shared viking feed (daler / warehouse / carts / buildings) from vik.* state
local function feed(k) return scrye.getState("vik." .. k) end
local function at_getvars()
  return {
    DALER = feed("daler"), WSTOCK = feed("wstock"), CARTS = feed("carts"),
    CIDLE = feed("cidle"), CUPG = feed("cupg"), CDTIME = feed("cdtime"),
    BUILDINGS = feed("buildings"),
  }
end

-- Trading Post tier -> (max carts = tier, largest cart capacity seen in the feed)
local WCAP = { 400, 1000, 1750, 3000, 5250 }   -- warehouse unit cap, tier 1..5
local function at_capacity(v)
  local tier = tonumber((v.BUILDINGS or ""):match("trading_post:(%d+)")) or 1
  local function nth(s, n)
    local i = 0
    for p in (s .. "|"):gmatch("([^|]*)|") do i = i + 1; if i == n then return tonumber(p) end end
  end
  local cap = 0
  for e in (v.CIDLE or ""):gmatch("[^;]+") do local c = nth(e, 4);  if c and c > cap then cap = c end end
  for e in (v.CARTS or ""):gmatch("[^;]+") do local c = nth(e, 11); if c and c > cap then cap = c end end
  if cap <= 0 then cap = ({ 20, 30, 65, 90, 125 })[tier] or 20 end
  return tier, cap
end
local function at_warehouse(v)
  local used = 0
  for entry in (v.WSTOCK or ""):gmatch("[^;]+") do
    local a = entry:match("^[^|]+|(%d+)"); if a then used = used + tonumber(a) end
  end
  local tier = tonumber((v.BUILDINGS or ""):match("warehouse:(%d+)")) or 3
  return used, (WCAP[tier] or 1750), tier
end

-- ---------- trade log + stats ----------
local function at_record(side, qty, cmd, town, amount)
  amount = amount or 0
  local s = at.stats
  if side == "sell" then s.sells = s.sells + 1; s.earned = s.earned + amount
  else s.buys = s.buys + 1; s.spent = s.spent + amount end
  local line = string.format("[%s] %-4s %4d %-11s %-4s %-14s ~%dd",
    os.date("%Y-%m-%d %H:%M:%S"), side:upper(), qty, cmd,
    (side == "buy") and "from" or "to", town, amount)
  s.recent[#s.recent + 1] = line
  while #s.recent > 40 do table.remove(s.recent, 1) end
  scrye.log(line)
  if at_draw then at_draw() end
end

local at_cd_retry

-- one dispatch pass: pick the single most worthwhile cart to send (restock > sells/scalps)
auto_trade_tick = function()
  at.pending_check = false
  if not at.on then return end
  if not connected then return end
  if scanning then return end   -- a refresh is in flight; mk_finish re-runs us on fresh data

  local v = at_getvars()
  local maxc, cap = at_capacity(v)
  if at.carts and at.carts > 0 then maxc = math.min(maxc, at.carts) end

  if v.CARTS ~= at.last_carts then at.pending = 0; at.last_carts = v.CARTS end
  local active = 0
  for _ in (v.CARTS or ""):gmatch("[^;]+") do active = active + 1 end
  local upgrading = 0
  for _ in (v.CUPG or ""):gmatch("[^;]+") do upgrading = upgrading + 1 end
  local free = maxc - active - upgrading - (at.pending or 0)
  if free <= 0 then return end
  local cd = tonumber(v.CDTIME) or 0
  if cd > 0 then
    if not at.cd_wait then at.cd_wait = true; scrye.after(cd + 1, at_cd_retry) end
    return
  end
  free = math.min(free, 1)   -- one dispatch per pass (each starts a fresh cooldown)

  if #results == 0 or (os.time() - mk_refreshed_at) > 60 then
    mk_refresh(true); return   -- prices stale: refresh, mk_finish re-runs us
  end

  -- warehouse stock per good (normalise "fine_furs" -> "fine furs")
  local stock = {}
  for entry in (v.WSTOCK or ""):gmatch("[^;]+") do
    local g, a = entry:match("^([^|]+)|(%d+)")
    if g then local k = trim(g):lower():gsub("_", " "); stock[k] = (stock[k] or 0) + tonumber(a) end
  end
  local function have_of(r)
    return stock[(r.cmd or ""):gsub("_", " ")] or stock[(r.res or ""):lower():gsub("_", " ")] or 0
  end

  -- goods already inbound on a BUY cart: don't double up
  local inbound = {}
  for entry in (v.CARTS or ""):gmatch("[^;]+") do
    local kind, good = entry:match("^(%a+)|([^|]+)")
    if kind == "buy" and good then inbound[trim(good):lower()] = true end
  end

  local used, wcap = at_warehouse(v)
  local fillpct  = (wcap > 0) and (used * 100 / wcap) or 0
  local pressure = fillpct >= (at.soft or 70)
  local clearing = fillpct >= (at.full or 90)
  local fillmin  = clearing and (at.clear_pct or 25) or (at.min_pct or 70)
  local need = math.max(1, math.min(cap, math.ceil(cap * fillmin / 100)))

  -- SELL: offload warehouse stock to its best-paying town
  local cand = {}
  for _, r in ipairs(results) do
    if not at.exempt[disp_cmd(r.cmd)] and r.sells and r.sells[1] then
      local have  = have_of(r)
      local isref = REFINED[r.cmd] and true or false
      local reserve = at.keep or 20
      if not (isref or SPECIAL[r.cmd]) then reserve = math.max(reserve, at.stock or 300) end
      local avail = have - reserve
      if isref and not at.refined then avail = 0 end
      if avail > 0 and math.min(avail, cap) >= need then
        -- explicit nil init: MoonSharp leaves an UNinitialised local aliasing a stale
        -- table (same codegen quirk as mk_compute's profit), which breaks val > best.value
        local best, bestq = nil, nil
        for _, s in ipairs(r.sells) do
          local dem = tonumber(s.qty)
          local qty = math.min(avail, cap, (dem and dem > 0) and dem or cap)
          if qty >= 1 then
            local val = qty * (s.price or 0)
            if not best or val > best.value then best = { town = s.town, qty = qty, value = val } end
            if not bestq or qty > bestq.qty or (qty == bestq.qty and val > bestq.value) then
              bestq = { town = s.town, qty = qty, value = val }
            end
          end
        end
        local isflush = (at.flush and at.flush > 0 and have >= at.flush) or false
        local pick = isflush and bestq or best
        if pick then
          cand[#cand + 1] = { kind = "sell", cmd = r.cmd, town = pick.town, qty = pick.qty,
                              value = pick.value, avail = avail, flush = isflush }
        end
      end
    end
  end

  -- SCALPER: buy low / sell high (competes on value with the sells above)
  local daler  = tonumber(v.DALER) or 0
  local budget = math.max(0, daler - (at.reserve or 0))
  local space  = math.max(0, wcap - used - 200)
  if at.scalp and not pressure and budget > 0 and space >= need then
    for _, r in ipairs(results) do
      if not at.exempt[disp_cmd(r.cmd)] and not inbound[disp_cmd(r.cmd)]
         and r.buys and r.buys[1] and r.sells and r.sells[1] then
        local buy, sell = r.buys[1], r.sells[1]
        local per = (sell.price or 0) - (buy.price or 0)
        if per >= (at.margin or 1) and (buy.price or 0) > 0 then
          local supply = tonumber(buy.qty)  or cap
          local demand = tonumber(sell.qty) or cap
          local afford = math.floor(budget / buy.price)
          local qty = math.min(cap, supply, demand, afford, space)
          if qty >= need then
            cand[#cand + 1] = { kind = "buy", cmd = r.cmd, town = buy.town, qty = qty,
                                value = qty * per, cost = qty * buy.price, unit = buy.price, per = per }
          end
        end
      end
    end
  end

  -- RESTOCK (top priority): buy raws back up to Raw> when low
  local restock = {}
  if at.restock and not clearing and budget > 0 and space >= need then
    for _, r in ipairs(results) do
      if RAWBUILD[r.cmd] and not at.exempt[disp_cmd(r.cmd)]
         and not inbound[disp_cmd(r.cmd)] and r.buys and r.buys[1] then
        local buy = r.buys[1]
        local have = have_of(r)
        if have < (at.stock or 300) and (buy.price or 0) > 0 then
          local supply = tonumber(buy.qty) or cap
          local afford = math.floor(budget / buy.price)
          local qty = math.min(cap, supply, afford, space)
          if qty >= need then
            restock[#restock + 1] = { cmd = r.cmd, town = buy.town, qty = qty,
                                      cost = qty * buy.price, unit = buy.price, have = have }
          end
        end
      end
    end
    table.sort(restock, function(a, b) return a.have < b.have end)
  end

  -- rank sell/scalp candidates by cart value; flush piles jump the queue
  table.sort(cand, function(a, b)
    if (a.flush or false) ~= (b.flush or false) then return a.flush or false end
    return a.value > b.value
  end)
  local bestnf = 0
  for _, c in ipairs(cand) do if not c.flush and c.value > bestnf then bestnf = c.value end end
  local floor = pressure and 0 or (bestnf * (at.min_rel or 40) / 100)

  local sent, seen = 0, {}
  -- restock first: keep raw building materials topped up
  for _, c in ipairs(restock) do
    if sent >= free then break end
    if not seen[c.cmd] then
      local q = math.min(c.qty, space)
      local cost = q * (c.unit or 0)
      if q >= need and cost <= budget then
        scrye.send(string.format("vtrade dispatch buy %d %s %s escort %d",
          q, disp_cmd(c.cmd), town_cmd(c.town), at.escort))
        note(string.format("restock buy %d %s from %s (-%dd, had %d)", q, c.cmd, c.town, cost, c.have))
        at_record("buy", q, disp_cmd(c.cmd), c.town, cost)
        budget = budget - cost; space = space - q; seen[c.cmd] = true
        at.pending = (at.pending or 0) + 1; sent = sent + 1
      end
    end
  end
  -- then the most valuable sell/scalp carts
  for _, c in ipairs(cand) do
    if sent >= free then break end
    if not c.flush and c.value < floor then break end
    if not seen[c.cmd] then
      if c.kind == "buy" then
        local q = math.min(c.qty, space)
        local cost = q * (c.unit or 0)
        if q >= need and cost <= budget then
          scrye.send(string.format("vtrade dispatch buy %d %s %s escort %d",
            q, disp_cmd(c.cmd), town_cmd(c.town), at.escort))
          note(string.format("scalp buy %d %s from %s (-%dd, ~+%dd margin)",
            q, c.cmd, c.town, cost, math.floor(q * (c.per or 0))))
          at_record("buy", q, disp_cmd(c.cmd), c.town, cost)
          budget = budget - cost; space = space - q; seen[c.cmd] = true
          at.pending = (at.pending or 0) + 1; sent = sent + 1
        end
      else
        scrye.send(string.format("vtrade dispatch sell %d %s %s escort %d",
          c.qty, disp_cmd(c.cmd), town_cmd(c.town), at.escort))
        note(string.format("sell %d %s to %s (~%dd cart)", c.qty, c.cmd, c.town, c.value))
        at_record("sell", c.qty, disp_cmd(c.cmd), c.town, c.value)
        seen[c.cmd] = true; at.pending = (at.pending or 0) + 1; sent = sent + 1
      end
    end
  end

  if sent > 0 then
    mk_last_dispatch = os.time()
    if not at.cd_wait then at.cd_wait = true; scrye.after(10, at_cd_retry) end
  end
end

at_schedule = function()
  if at.pending_check then return end
  at.pending_check = true
  scrye.after(1, function() auto_trade_tick() end)
end

at_cd_retry = function()
  at.cd_wait = false
  auto_trade_tick()
end

local function at_driver() if at.on then at_schedule() end end

-- react to the live feed: dispatch when the warehouse gains goods or a cart frees up
local at_last_free, at_last_stock = -1, -1
local function at_on_feed()
  if not at.on then return end
  local v = at_getvars()
  local maxc = at_capacity(v)
  if at.carts and at.carts > 0 then maxc = math.min(maxc, at.carts) end
  local active = 0
  for _ in (v.CARTS or ""):gmatch("[^;]+") do active = active + 1 end
  local free = maxc - active
  local total = 0
  for e in (v.WSTOCK or ""):gmatch("[^;]+") do local a = e:match("|(%d+)"); if a then total = total + tonumber(a) end end
  local trig = (at_last_stock >= 0 and total > at_last_stock)
            or (at_last_free  >= 0 and free  > at_last_free)
  at_last_free, at_last_stock = free, total
  if trig then at_schedule() end
end

-- ---------- held / exempt goods ----------
local function at_save_exempt()
  local list = {}
  for k, on in pairs(at.exempt) do if on then list[#list + 1] = k end end
  table.sort(list); sset("at_exempt", table.concat(list, ",")); return list
end
local function at_toggle_exempt(word)
  word = trim(word or ""):lower(); word = DCMD[word] or word
  if word == "" then return end
  at.exempt[word] = (not at.exempt[word]) or nil
  at_save_exempt()
  mk_render(nil)          -- refresh the "#" held markers in the Market report
end

-- ---------- numeric settings ----------
local AT_KEY   = { reserve="at_reserve", margin="at_margin", stock="at_stock", flush="at_flush",
                   min="at_minpct", rel="at_minrel", keep="at_keep", soft="at_soft",
                   full="at_full", clear="at_clearpct", carts="at_carts", escort="at_escort" }
local AT_FIELD = { reserve="reserve", margin="margin", stock="stock", flush="flush",
                   min="min_pct", rel="min_rel", keep="keep", soft="soft",
                   full="full", clear="clear_pct", carts="carts", escort="escort" }
-- clamps for settings the game itself bounds; everything else is just floored at 0
local AT_RANGE = { escort = { 1, 20 } }
local function at_setnum(name, val)
  local field, key = AT_FIELD[name], AT_KEY[name]
  if not field then note("unknown setting: " .. tostring(name)); return end
  local n = tonumber(val)
  if not n then note("not a number: " .. tostring(val)); return end
  n = math.floor(n)
  local r = AT_RANGE[name]
  if r then n = math.max(r[1], math.min(r[2], n)) else n = math.max(0, n) end
  at[field] = n; sset(key, n); at_draw()
  note(string.format("%s = %d%s", name, n, (n ~= math.floor(tonumber(val))) and " (clamped)" or ""))
end

-- ---------- status + drawing the Auto / Log tabs ----------
local function at_modeline(v)
  local used, wcap, wtier = at_warehouse(v or at_getvars())
  local pct = (wcap > 0) and (used * 100 / wcap) or 0
  local pressure, clearing = pct >= (at.soft or 70), pct >= (at.full or 90)
  local mode = clearing and "CLEARING - biggest piles, buying paused"
            or (pressure and "PRESSURE - biggest piles, scalping paused" or "normal - best value first")
  return used, wcap, wtier, pct, mode
end

at_draw = function()
  local v = at_getvars()
  local used, wcap, wtier, pct, mode = at_modeline(v)
  local cd = tonumber(v.CDTIME) or 0
  local L = {}
  L[#L+1] = string.format("Auto-trade: %s     Scalp: %s   Restock: %s   Refined: %s",
    at.on and "ON" or "OFF", at.scalp and "on" or "off", at.restock and "on" or "off", at.refined and "on" or "off")
  L[#L+1] = string.format("Warehouse %s / %s  (%d%%, tier %d)   Daler %s%s",
    comma(used), comma(wcap), math.floor(pct), wtier, comma(tonumber(v.DALER) or 0),
    cd > 0 and ("   cart cooldown " .. cd .. "s") or "")
  L[#L+1] = "mode: " .. mode
  local held = {}
  for k, on in pairs(at.exempt) do if on then held[#held+1] = k end end
  table.sort(held)
  if #held > 0 then L[#L+1] = "held (never sold): " .. table.concat(held, ", ") end
  scrye.setState(P .. "atstatus", table.concat(L, "\n"))

  scrye.setState(P .. "v_keep",    tostring(at.keep))
  scrye.setState(P .. "v_stock",   tostring(at.stock))
  scrye.setState(P .. "v_reserve", tostring(at.reserve))
  scrye.setState(P .. "v_carts",   tostring(at.carts))
  scrye.setState(P .. "v_min",     tostring(at.min_pct))
  scrye.setState(P .. "v_rel",     tostring(at.min_rel))
  scrye.setState(P .. "v_margin",  tostring(at.margin))
  scrye.setState(P .. "v_flush",   tostring(at.flush))
  scrye.setState(P .. "v_soft",    tostring(at.soft))
  scrye.setState(P .. "v_full",    tostring(at.full))
  scrye.setState(P .. "v_clear",   tostring(at.clear_pct))
  scrye.setState(P .. "v_escort",  tostring(at.escort))
  scrye.setState(P .. "v_units",   tostring(mk_units))

  local s = at.stats
  local mins = math.floor((os.time() - (s.since or os.time())) / 60)
  local lg = {}
  lg[#lg+1] = string.format("this session (%dm):  sold %d (~+%s d)   bought %d (-%s d)",
    mins, s.sells, comma(s.earned), s.buys, comma(s.spent))
  lg[#lg+1] = ""
  if #s.recent == 0 then lg[#lg+1] = "(no auto-trades yet)"
  else for i = #s.recent, math.max(1, #s.recent - 25), -1 do lg[#lg+1] = s.recent[i] end end
  scrye.setState(P .. "atlog", table.concat(lg, "\n"))
end

local function at_status()
  local v = at_getvars()
  local used, wcap, wtier, pct, mode = at_modeline(v)
  note(string.format("auto %s | scalp %s | restock %s | refined %s | keep %d | raw>%d | reserve %s | carts %s",
    at.on and "ON" or "OFF", at.scalp and "on" or "off", at.restock and "on" or "off",
    at.refined and "yes" or "no", at.keep, at.stock, comma(at.reserve),
    (at.carts > 0) and tostring(at.carts) or "auto"))
  note(string.format("warehouse %s/%s (%d%%, tier %d) | mode: %s",
    comma(used), comma(wcap), math.floor(pct), wtier, mode))
end

local function at_show_stats()
  local s = at.stats
  local mins = math.floor((os.time() - (s.since or os.time())) / 60)
  note(string.format("this session (%dm): sold %d (~+%s d)  bought %d (-%s d)",
    mins, s.sells, comma(s.earned), s.buys, comma(s.spent)))
end

local function at_show_log()
  local s = at.stats
  if #s.recent == 0 then note("no trades this session (full history is in the plugin log file)"); return end
  note("recent auto-trades:")
  for i = math.max(1, #s.recent - 14), #s.recent do scrye.print("  " .. s.recent[i]) end
end

-- panel toggles
local function at_toggle_on()      at.on = not at.on; sset("at_on", at.on and "1" or "0"); if at.on then at_schedule() end; at_draw(); at_status() end
local function at_toggle_scalp()   at.scalp = not at.scalp; sset("at_scalp", at.scalp and "1" or "0"); if at.on and at.scalp then at_schedule() end; at_draw() end
local function at_toggle_restock() at.restock = not at.restock; sset("at_restock", at.restock and "1" or "0"); if at.on and at.restock then at_schedule() end; at_draw() end
local function at_toggle_refined() at.refined = not at.refined; sset("at_refined", at.refined and "1" or "0"); at_draw() end

-- ---------- `atrade` command ----------
local function at_config(rest)
  rest = trim(rest or ""):lower()
  local key, val = rest:match("^(%a+)%s+(%-?%d+)$")
  if rest == "" or rest == "status" then at_status(); return
  elseif rest == "on"  then at.on = true;  sset("at_on", "1"); at_schedule(); at_draw()
  elseif rest == "off" then at.on = false; sset("at_on", "0"); at_draw()
  elseif rest == "refined on"  then at.refined = true;  sset("at_refined", "1"); at_draw()
  elseif rest == "refined off" then at.refined = false; sset("at_refined", "0"); at_draw()
  elseif rest == "scalp on"    then at.scalp = true;  sset("at_scalp", "1"); if at.on then at_schedule() end; at_draw()
  elseif rest == "scalp off"   then at.scalp = false; sset("at_scalp", "0"); at_draw()
  elseif rest == "restock on"  then at.restock = true;  sset("at_restock", "1"); if at.on then at_schedule() end; at_draw()
  elseif rest == "restock off" then at.restock = false; sset("at_restock", "0"); at_draw()
  elseif rest == "stats"       then at_show_stats(); return
  elseif rest == "stats reset" then at.stats = { buys=0, sells=0, spent=0, earned=0, since=os.time(), recent={} }; note("session stats reset"); at_draw(); return
  elseif rest == "log"         then at_show_log(); return
  elseif rest == "exempt"       then local l={}; for k,on in pairs(at.exempt) do if on then l[#l+1]=k end end; table.sort(l); note("held: " .. (#l>0 and table.concat(l, ", ") or "(none)")); return
  elseif rest == "exempt clear" then at.exempt = {}; sset("at_exempt", ""); note("held list cleared"); at_draw(); return
  elseif rest:match("^exempt%s+") then at_toggle_exempt(rest:gsub("^exempt%s+", "")); at_draw(); return
  elseif key and AT_FIELD[key] then at_setnum(key, val); return
  else
    note("usage: atrade on|off | scalp|restock|refined on|off | keep|stock|reserve|margin|min|rel|carts|escort|flush|soft|full|clear <n> | exempt <good> | stats | log")
    return
  end
  at_status()
end


-- ====================== manual dispatch ======================
-- Restores the MUSHclient window's click-a-town action: it sent
--   vtrade dispatch <side> <units> <good> <town> escort <n>
-- Here it is a command instead, since panel widgets are built once at load and the
-- ranked town list changes on every refresh.
local function mnote(t) scrye.print("@{#FFD028,bold}[market]@{} " .. t) end

-- every town name present in the current market data
local function known_towns()
  local seen, out = {}, {}
  for _, towns in pairs(market) do
    for town in pairs(towns) do
      if not seen[town] then seen[town] = true; out[#out + 1] = town end
    end
  end
  table.sort(out)
  return out
end

-- exact, then prefix, then substring -- so "lodbrok" finds "Lodbrok's Hold"
local function resolve_town(str)
  local q = trim(str or ""):lower()
  if q == "" then return nil end
  local towns = known_towns()
  for _, t in ipairs(towns) do if t:lower() == q then return t end end
  for _, t in ipairs(towns) do if t:lower():sub(1, #q) == q then return t end end
  for _, t in ipairs(towns) do if t:lower():find(q, 1, true) then return t end end
  return nil
end

-- pull a leading good name off "<good> <town>". Goods can be two words ("fine furs",
-- "salted fish"), so the longest matching name or command word wins.
local function split_good_town(str)
  str = trim(str or "")
  local low = str:lower()
  local cmd, len = nil, 0
  for _, r in ipairs(RES) do
    for _, form in ipairs({ r.name:lower(), r.cmd }) do
      if #form > len and low:sub(1, #form) == form then
        local nxt = low:sub(#form + 1, #form + 1)
        if nxt == "" or nxt == " " then cmd, len = r.cmd, #form end
      end
    end
  end
  if not cmd then return nil, nil end
  return cmd, trim(str:sub(len + 1))
end

local function mk_setunits(val)
  local n = tonumber(val)
  if not n then mnote("not a number: " .. tostring(val)); return end
  n = math.max(MK_UNITS_MIN, math.min(MK_UNITS_MAX, math.floor(n)))
  mk_units = n; sset("mk_units", n)
  scrye.setState(P .. "v_units", tostring(n))
  mnote(string.format("manual dispatch units = %d", n))
end

-- ---------- quick dispatch (the window's click-a-town action, as buttons) ----------
-- The original let you click a town in the market window to send a cart there. A bound
-- buttonrow is the equivalent: the options are recomputed from the live feed, and the
-- click carries the index back so we dispatch the exact cart the label described rather
-- than re-parsing it.
-- The carts are clickable TEXT, not buttons. That matters for more than looks: a text widget
-- is driven by bound state, so refreshing the list never rebuilds the panel -- which is what
-- used to threaten the twelve settings fields and forced the carts into a panel of their own.
-- Clicking sends "mkdispatch ...", the plugin's own alias, so it goes through mk_dispatch and
-- gets the same clamping, logging and rescan-guard as a typed command.
publish_dispatch = function()
  local labels = {}
  local ok = pcall(function()
    local v = at_getvars()
    -- warehouse stock per good (same normalisation the auto-trader uses)
    local stock = {}
    for entry in (v.WSTOCK or ""):gmatch("[^;]+") do
      local g, a = entry:match("^([^|]+)|(%d+)")
      if g then
        local k = trim(g):lower():gsub("_", " ")
        stock[k] = (stock[k] or 0) + tonumber(a)
      end
    end
    local _, cap = at_capacity(v)
    if not cap or cap <= 0 then return end

    -- one candidate per good: what you hold, sold to its best-paying town
    local cand = {}
    for _, r in ipairs(results) do
      local best = r.sells[1]
      if best then
        local have = (stock[(r.cmd or ""):gsub("_", " ")] or stock[(r.res or ""):lower()] or 0)
                     - (at.keep or 0)                       -- respect the mission reserve
        if have > 0 and not at.exempt[disp_cmd(r.cmd)] then  -- and the held list
          local qty = math.floor(math.min(have, cap, best.qty))
          if qty >= MK_UNITS_MIN then
            cand[#cand + 1] = {
              side = "sell", qty = qty, cmd = disp_cmd(r.cmd), town = best.town,
              value = qty * best.price,
              label = string.format("%d %s>%s", qty, r.res, best.town:sub(1, 10)),
            }
          end
        end
      end
    end
    table.sort(cand, function(a, b) return a.value > b.value end)
    while #cand > 8 do table.remove(cand) end   -- keep the panel a sensible height
    for _, c in ipairs(cand) do
      -- the click runs the plugin's own alias; disp_cmd/town_cmd give the words vtrade wants
      labels[#labels + 1] = string.format(
        "@{success,click=mkdispatch sell %d %s %s}%4d %-12s %s %-12s@{} @{dim}~%sd@{}",
        c.qty, c.cmd, town_cmd(c.town),
        c.qty, esc(DISPLAY[c.cmd] or c.cmd), ">", esc(c.town:sub(1, 12)), esc(comma(c.value)))
    end
  end)
  if not ok then labels = {} end

  if #labels == 0 then
    labels[1] = "@{dim}nothing worth sending - Refresh to update@{}"
  end
  scrye.setState(P .. "carts", table.concat(labels, "\n"))
end

local MK_USAGE = "usage: mkdispatch buy|sell [qty] <good> <town>   (qty defaults to the Units setting)"

local function mk_dispatch(rest)
  local side, tail = trim(rest or ""):match("^(%a+)%s+(.+)$")
  side = side and side:lower()
  if side ~= "buy" and side ~= "sell" then mnote(MK_USAGE); return end

  local qty = mk_units
  local n, remainder = tail:match("^(%d+)%s+(.+)$")
  if n then qty = tonumber(n); tail = remainder end
  qty = math.max(MK_UNITS_MIN, math.min(MK_UNITS_MAX, math.floor(qty)))

  local cmd, townstr = split_good_town(tail)
  if not cmd then mnote("don\'t recognise a good in: " .. tail); mnote(MK_USAGE); return end
  if townstr == "" then mnote("no town given. " .. MK_USAGE); return end

  -- fall back to the word as typed when there is no scan data to match against
  local town = resolve_town(townstr) or townstr
  local escort = math.max(1, math.min(20, at.escort or 5))

  scrye.send(string.format("vtrade dispatch %s %d %s %s escort %d",
    side, qty, disp_cmd(cmd), town_cmd(town), escort))
  mnote(string.format("dispatch %s %d %s %s %s (escort %d)",
    side, qty, DISPLAY[cmd] or cmd, (side == "buy") and "from" or "to", town, escort))

  -- record it in the Log tab, but keep it out of the auto-trader's counters
  local line = string.format("[%s] MAN  %-4s %4d %-11s %-4s %-14s",
    os.date("%Y-%m-%d %H:%M:%S"), side:upper(), qty, disp_cmd(cmd),
    (side == "buy") and "from" or "to", town)
  scrye.log(line)
  local rec = at.stats.recent
  rec[#rec + 1] = line
  while #rec > 40 do table.remove(rec, 1) end

  mk_last_dispatch = os.time()   -- don't let our own echo trigger a rescan
  if at_draw then at_draw() end
end

-- ====================== aliases ======================
scrye.addAlias{
  pattern = "^mkref$", regex = true,
  run = function() mk_refresh(false) end,
}

-- manual dispatch:  mkdispatch buy 200 mead lodbrok   /   mkdispatch sell bread eirik
scrye.addAlias{
  pattern = "^mkdispatch$", regex = true,
  run = function() mnote(MK_USAGE) end,
}
scrye.addAlias{
  pattern = "^mkdispatch (.+)$", regex = true,
  run = function(rest) mk_dispatch(rest) end,
}
-- default cart size for manual dispatch
scrye.addAlias{
  pattern = "^mkunits$", regex = true,
  run = function() mnote(string.format("manual dispatch units = %d (range %d-%d)",
    mk_units, MK_UNITS_MIN, MK_UNITS_MAX)) end,
}
scrye.addAlias{
  pattern = "^mkunits (.+)$", regex = true,
  run = function(v) mk_setunits(v) end,
}

-- consumed, not passed to the MUD: the HUD owns panel visibility
scrye.addAlias{
  pattern = "^markwin$", regex = true,
  run = function() mnote("the Market panel is managed by Scrye - show or hide it from the HUD.") end,
}

-- auto-trader command:  atrade | atrade <setting> <value> | atrade on|off | ...
scrye.addAlias{
  pattern = "^atrade$", regex = true,
  run = function() at_config("") end,
}
scrye.addAlias{
  pattern = "^atrade (.+)$", regex = true,
  run = function(rest) at_config(rest) end,
}

-- ====================== HUD panel (Market / Auto / Log tabs) ======================
scrye.addPanel{
  title = "3S Market",
  width = 520,
  accent = "#D9A521",          -- signature: market gold
  tabs = {
    { title = "Market", widgets = {
        { type = "button", text = "Refresh", action = function() mk_refresh(false) end },
        { type = "label",  bind = P .. "status", color = "#E0A830" },   -- status line in gold
        { type = "text",   bind = P .. "report" },
        { type = "label",  text = "Quick dispatch - click a cart to send it:", color = "#8FA0B0" },
        { type = "text",   bind = P .. "carts" },
        { type = "input",  text = "Units (20-350) ", bind = P .. "v_units",
          onSubmit = function(t) mk_setunits(t) end },
        { type = "input",  text = "Escort (1-20) ",  bind = P .. "v_escort",
          onSubmit = function(t) at_setnum("escort", t) end },
    } },
    { title = "Auto", widgets = {
        { type = "text", bind = P .. "atstatus" },
        { type = "buttonrow", buttons = {
            { text = "Auto On/Off", action = function() at_toggle_on() end },
            { text = "Scalp On/Off", action = function() at_toggle_scalp() end },
        } },
        { type = "buttonrow", buttons = {
            { text = "Restock On/Off", action = function() at_toggle_restock() end },
            { text = "Refined On/Off", action = function() at_toggle_refined() end },
        } },
        { type = "label", text = "Settings (type a value, Enter):", color = "#8FA0B0" },
        { type = "input", text = "Keep (every good) ",  bind = P .. "v_keep",    onSubmit = function(t) at_setnum("keep", t) end },
        { type = "input", text = "Raw> buffer ",        bind = P .. "v_stock",   onSubmit = function(t) at_setnum("stock", t) end },
        { type = "input", text = "Daler reserve ",      bind = P .. "v_reserve", onSubmit = function(t) at_setnum("reserve", t) end },
        { type = "input", text = "Cart cap (0=auto) ",  bind = P .. "v_carts",   onSubmit = function(t) at_setnum("carts", t) end },
        { type = "input", text = "Cart fill min % ",    bind = P .. "v_min",     onSubmit = function(t) at_setnum("min", t) end },
        { type = "input", text = "Value floor % ",      bind = P .. "v_rel",     onSubmit = function(t) at_setnum("rel", t) end },
        { type = "input", text = "Scalp margin/unit ",  bind = P .. "v_margin",  onSubmit = function(t) at_setnum("margin", t) end },
        { type = "input", text = "Flush cap (0=off) ",  bind = P .. "v_flush",   onSubmit = function(t) at_setnum("flush", t) end },
        { type = "input", text = "Pressure % ",         bind = P .. "v_soft",    onSubmit = function(t) at_setnum("soft", t) end },
        { type = "input", text = "Clearing % ",         bind = P .. "v_full",    onSubmit = function(t) at_setnum("full", t) end },
        { type = "input", text = "Clearing fill % ",    bind = P .. "v_clear",   onSubmit = function(t) at_setnum("clear", t) end },
        { type = "input", text = "Escort size ",        bind = P .. "v_escort",  onSubmit = function(t) at_setnum("escort", t) end },
        { type = "label", text = "Hold a good: type  atrade exempt <good>", color = "#8FA0B0" },
    } },
    { title = "Log", widgets = {
        { type = "text", bind = P .. "atlog" },
    } },
  },
}

-- ====================== init: restore the last scan ======================
local saved = scrye.store.get("market")
if saved and saved ~= "" then
  local ok = pcall(function() market = mk_deserialize(saved) end)
  if ok then
    mk_compute()
    mk_render("restored from previous session - " .. #results .. " goods (mkref to update)")
  else
    market = {}
    mk_render("not loaded yet - click Refresh or type mkref")
  end
else
  mk_render("not loaded yet - click Refresh or type mkref")
end

-- ====================== auto-trader wiring ======================
-- connection tracking: dispatch any idle carts on connect (if auto is on)
scrye.onConnect(function() connected = true; at_driver() end)
scrye.onDisconnect(function() connected = false end)

-- react to live warehouse/cart changes: only dispatch on a real edge (goods gained / cart freed)
scrye.watch("vik", function() at_on_feed(); publish_dispatch() end)

at_draw()   -- seed the Auto / Log tab state

-- a (re)load mid-session gets no onConnect, so start the driver here as well
if at.on then at_driver() end
