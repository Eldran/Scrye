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

local function trim(s) return (s or ""):gsub("^%s+", ""):gsub("%s+$", "") end
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
  scrye.setState(P .. "status", status)
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
      lines[#lines + 1] = string.format(FMT, r.res, bp, bt, bq, sp, st, sq, profit)
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
    lines[#lines + 1] = "* stock under " .. LOWSTOCK .. " (next-best town shown)"
  end
  scrye.setState(P .. "report", table.concat(lines, "\n"))
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
  mk_render("updated this session - " .. #results .. " goods")
  if not quiet then scrye.print("refreshed " .. #results .. " goods") end
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
  -- separators, "price N daler", "Trading Post tier", column headers, etc:
  -- still part of the vtrade block, so hide them too
  return false
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
    if update_pending or scanning then return end
    update_pending = true
    scrye.after(2, function()
      update_pending = false
      if not scanning then mk_refresh(true) end
    end)
  end,
}

-- ====================== aliases ======================
-- (markwin dropped: the HUD manages panel visibility)
scrye.addAlias{
  pattern = "^mkref$", regex = true,
  run = function() mk_refresh(false) end,
}

-- ====================== HUD panel (replaces the miniwindow) ======================
scrye.addPanel{
  title = "3S Market",
  width = 520,
  accent = "#D9A521",          -- signature: market gold
  widgets = {
    { type = "button", text = "Refresh", action = function() mk_refresh(false) end },
    { type = "label",  bind = P .. "status", color = "#E0A830" },   -- status line in gold
    { type = "text",   bind = P .. "report" },
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
