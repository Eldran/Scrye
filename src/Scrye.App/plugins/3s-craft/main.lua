-- 3S Crafting -- a HUD for the crafting system, built on the Craft.* GMCP
-- packages. 3Scapes only, any guild: crafting belongs to the character, not to a
-- guild, so this sits beside 3s-vitals and 3s-merc rather than inside a guild HUD.
--
-- 0.3.0, and early: the system went live on 16 Nov 2026 and is still being built on the
-- server, so this is written from four captures of one character (Keyser, 16-17 Nov
-- 2026) who owns one extractor and has trained one skill. Every field READ below
-- was seen with a real value; what was only ever seen empty or zero is not guessed
-- at -- it is shown raw on the "Raw" tab under the server's own name, so the next
-- capture that fills it in can be read straight off the panel.
--
-- The plugin never sends a command of its own accord. It reads the feed and draws
-- it; the only commands it can cause are the two a building's name carries as a
-- link (0.2.0): 'cbuild info <name>' on a click, and 'cbuild build <name> confirm'
-- from the right-click menu.
--
-- Packages consumed, and the three shapes they come in:
--
--   snapshot, then deltas of WHOLE sub-objects (no "full" on the delta)
--     Craft.Infodata   bonuses{}, realms{Realm{level,xp,bonus,license}},
--                      soul{cap,tier}, storage{cap,tier},
--                      standing{Realm{drop_rank,drop_extra,drop_bump}}
--     Craft.Jobs       builds[{building,tier,due}], queue_total, queue_cap, and
--                      ready[] queues[] pending_builds[] masterwork[] (only ever
--                      seen empty -> Raw tab)
--     Craft.Market     exchanges[{id,player,give_token,give_qty,want_token,
--                      want_qty,created}], and auctions[] orders[]
--                      material_orders[] scroll_auctions[] scroll_orders[]
--                      scroll_outbox[] (only ever seen empty -> Raw tab)
--     Craft.Statedata  four pages: stock_used/stock_cap/souls/soul_cap/
--                      soul_tiers[], then tokens_chaos[], tokens_fantasy[],
--                      tokens_science[] ({level,realm,qty,name}); a token delta is
--                      the whole realm's array. Also "materials_chunks_g<N>": 0,
--                      where N changes and the value never has -> Raw tab.
--
--   chunked catalogues: one list cut across many messages under NUMBERED KEYS,
--   the whole set repeated every few minutes
--     Craft.Skills     skills_0 .. skills_17, two skills per message, each
--                      {rank,trained,xp_rank,into,to_next,can_train,unlocked,
--                      train_cost,train_tier,train_realm,train_abbr}
--     Craft.Structures bldgs["Name|tier"], and refineries, refineries_1 ..
--                      refineries_14 of "Building|step|Material|a|b|c" -- a, b
--                      and c were 0 on all sixty rows (no refinery is built) and
--                      are not named here
--     Craft.Recipes    catalogue_<realm>, catalogue_<realm>_<N>, each ALSO paged
--                      (page/pages) under the same key: entries of {name,
--                      building,tier,rank}. This is the whole catalogue, not what
--                      the character knows -- it lists tier 5 recipes for
--                      buildings that are not built.
--
--   a counter
--     Craft.Event      {id} every two seconds, counting up; the first carries
--                      finished[], items[], materials[], all empty so far
--
-- Craft.State, Craft.Info and Craft.Extra are OFFERED by the server and have never
-- sent anything. They are listened for anyway and land on the Raw tab if they do.
--
-- What the numbers mean, where it is known (chelp, 17 Nov 2026):
--   * a skill's rank is what has been TRAINED; xp banks past the threshold until
--     ctrain is paid for, so xp_rank can run ahead of rank and to_next go negative.
--     into + to_next is the threshold.
--   * can_train: 1 once the xp is there, 0 while it is not, -1 for a locked skill.
--     Whether 1 also means the tokens are there is NOT known, so the panel says
--     "xp ready" and shows the price; it does not say "affordable".
--   * due and created are epoch seconds.
--   * a token id is "<realm>_<level>" ("fantasy_5"); the names come from the
--     tokens_* lists.

local P = "plugin." .. scrye.id .. "."
local TAG = "@{#B5894A,bold}[craft]@{} "

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

-- 3725 -> "1h 02m", 3492 -> "58m 12s", 9 -> "9s", 0 or less -> "now"
local function span(secs)
  secs = math.floor(N(secs))
  if secs <= 0 then return "now" end
  local h, m, s = secs // 3600, (secs % 3600) // 60, secs % 60
  if h > 0 then return string.format("%dh %02dm", h, m) end
  if m > 0 then return string.format("%dm %02ds", m, s) end
  return s .. "s"
end

local function cap1(s) s = S(s); return s:sub(1, 1):upper() .. s:sub(2) end

-- Anything, as text: what the Raw tab prints for a value nobody has named. Arrays
-- as [a, b], objects as {k=v} with the keys sorted so two reads compare.
local function raw(v, depth)
  depth = depth or 0
  if type(v) ~= "table" then return S(v) end
  if depth > 3 then return "..." end
  local parts = {}
  if #v > 0 then
    for i = 1, #v do parts[i] = raw(v[i], depth + 1) end
    return "[" .. table.concat(parts, ", ") .. "]"
  end
  local keys = {}
  for k in pairs(v) do keys[#keys + 1] = k end
  table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
  for _, k in ipairs(keys) do parts[#parts + 1] = tostring(k) .. "=" .. raw(v[k], depth + 1) end
  return "{" .. table.concat(parts, ", ") .. "}"
end

local function filled(v) return type(v) == "table" and next(v) ~= nil end

-- "refineries_14" -> 14, "refineries" -> 0: the order the chunks were cut in
local function chunk_no(key) return N(S(key):match("_(%d+)$")) end

-- ------------------------------------------------------------- snapshots
local INFO, STATE, JOBS, MARKET = {}, {}, {}, {}
local SKILLS = {}        -- name -> the skill object
local TOKENS = {}        -- "tokens_science" -> [{level,realm,qty,name}]
local BLDGS = {}         -- [{name=, tier=}], as the server last listed them
local have_bldgs = false -- an empty bldgs has arrived: "none built", not "not told"
local REFS = {}          -- "refineries_3" -> ["Building|step|Material|a|b|c"]
local CAT = {}           -- "catalogue_chaos_2" -> { [page] = [entries] }
local EVENT = {}         -- last id, and the last non-empty finished/items/materials
local EXTRA = {}         -- package -> { key -> value } for keys this plugin cannot name
local seen_any = false
local CATB = nil         -- building -> realm, from the catalogue; rebuilt when it changes

-- Every building that can be built, so the ones still at T0 can be listed. The
-- feed only names a building once it is built (bldgs), has recipes (the catalogue)
-- or is a refinery; the extractors and the two global buildings have neither until
-- then, so their names are taken from 'chelp buildings' (17 Nov 2026). If the
-- server adds one, it appears under "Other" the moment the feed mentions it.
local KNOWN = {
  { "Chaos",   { "Flesh Harvesters", "Rift Drills", "Soul Pylons", "Blight Farms" } },
  { "Fantasy", { "Sacred Groves", "Crystal Mines", "Herbal Sanctuaries", "Rune Monoliths" } },
  { "Science", { "Strip Mines", "Plasma Reactors", "Biolabs", "Quantum Extractors" } },
  { "Global",  { "Logistics Depot", "Soul Well" } },
}
local MAX_TIER = 5       -- chelp buildings: "Buildings upgrade from T1 to T5"

local function wipe(t) for k in pairs(t) do t[k] = nil end end

-- Merge a payload into a snapshot: top-level keys overwrite, a nested table
-- overwrites WHOLE (the server sends the whole sub-object), and the paging words
-- are not data.
local function merge(snap, t)
  for k, v in pairs(t) do
    if k ~= "full" and k ~= "page" and k ~= "pages" then snap[k] = v end
  end
end

local function extra(pkg, k, v)
  EXTRA[pkg] = EXTRA[pkg] or {}
  EXTRA[pkg][k] = v
end

-- ------------------------------------------------------- derived reads
local function builds() return type(JOBS.builds) == "table" and JOBS.builds or {} end

local function owned(building)
  for _, b in ipairs(BLDGS) do if b.name == building then return true end end
  return false
end

-- skills with the xp to train, sorted by name
local function trainable()
  local out = {}
  for name, s in pairs(SKILLS) do if N(s.can_train) > 0 then out[#out + 1] = name end end
  table.sort(out)
  return out
end

-- "fantasy_5" -> "legendary", from the tokens_* lists; the id itself until they arrive
local function token_name(id)
  local realm, level = S(id):match("^(%a+)_(%d+)$")
  if realm then
    local list = TOKENS["tokens_" .. realm]
    if type(list) == "table" then
      for _, t in ipairs(list) do
        if N(t.level) == N(level) and S(t.name) ~= "" then return S(t.name) end
      end
    end
  end
  return S(id)
end

local function token_total()
  local n = 0
  for _, list in pairs(TOKENS) do
    for _, t in ipairs(list) do n = n + N(t.qty) end
  end
  return n
end

local function realm_names()
  local set, out = {}, {}
  for _, k in ipairs({ "realms", "standing" }) do
    if type(INFO[k]) == "table" then
      for name in pairs(INFO[k]) do
        if not set[name] then set[name] = true; out[#out + 1] = name end
      end
    end
  end
  table.sort(out)
  return out
end

-- every catalogue entry, in the order the server cut them
local function catalogue()
  local keys = {}
  for k in pairs(CAT) do keys[#keys + 1] = k end
  table.sort(keys, function(a, b)
    local ra, rb = a:match("^catalogue_(%a+)") or "", b:match("^catalogue_(%a+)") or ""
    if ra ~= rb then return ra < rb end
    return chunk_no(a) < chunk_no(b)
  end)
  local out = {}
  for _, k in ipairs(keys) do
    local realm = cap1(k:match("^catalogue_(%a+)"))
    local pages = {}
    for p in pairs(CAT[k]) do pages[#pages + 1] = p end
    table.sort(pages)
    for _, p in ipairs(pages) do
      for _, e in ipairs(CAT[k][p]) do
        if type(e) == "table" then
          out[#out + 1] = { realm = realm, name = S(e.name), building = S(e.building),
                            tier = N(e.tier), rank = N(e.rank) }
        end
      end
    end
  end
  return out
end

local function cat_buildings()
  if not CATB then
    CATB = {}
    for _, e in ipairs(catalogue()) do if e.building ~= "" then CATB[e.building] = e.realm end end
  end
  return CATB
end

local function tier_of(building)
  for _, b in ipairs(BLDGS) do if b.name == building then return N(b.tier) end end
  return 0
end

local function build_of(building)
  for _, b in ipairs(builds()) do if S(b.building) == building then return b end end
  return nil
end

-- Every building, grouped by realm: the ones chelp names first and in its order,
-- then whatever the catalogue, the refinery list, bldgs or a build adds, by name.
local function all_buildings()
  local groups, by, seen = {}, {}, {}
  local function group(realm)
    if not by[realm] then by[realm] = { realm = realm, items = {} }; groups[#groups + 1] = by[realm] end
    return by[realm]
  end
  local function put(realm, name)
    if name == "" or seen[name] then return end
    seen[name] = true
    local g = group(realm)
    g.items[#g.items + 1] = name
  end
  for _, k in ipairs(KNOWN) do
    group(k[1])
    for _, name in ipairs(k[2]) do put(k[1], name) end
  end
  local late = {}
  for name, realm in pairs(cat_buildings()) do late[#late + 1] = { realm, name } end
  for _, rows in pairs(REFS) do
    for _, row in ipairs(rows) do
      local name = S(row):match("^([^|]*)")
      if name ~= "" and not cat_buildings()[name] then late[#late + 1] = { "Other", name } end
    end
  end
  for _, b in ipairs(BLDGS) do if not cat_buildings()[b.name] then late[#late + 1] = { "Other", b.name } end end
  for _, b in ipairs(builds()) do
    if not cat_buildings()[S(b.building)] then late[#late + 1] = { "Other", S(b.building) } end
  end
  table.sort(late, function(a, b)
    if a[1] ~= b[1] then return a[1] < b[1] end
    return a[2] < b[2]
  end)
  for _, l in ipairs(late) do put(l[1], l[2]) end
  local order = { Chaos = 1, Fantasy = 2, Science = 3, Global = 4, Other = 6 }
  table.sort(groups, function(a, b)
    local oa, ob = order[a.realm] or 5, order[b.realm] or 5
    if oa ~= ob then return oa < ob end
    return a.realm < b.realm
  end)
  return groups
end

-- A building's name as a link. Left click asks the game about it ('cbuild info
-- <name>'); the right button opens a one-entry menu that builds or upgrades it
-- ('cbuild build <name> confirm'). A MENU and not a bare right click on purpose:
-- building spends tokens, and a slip of the mouse must not. There is no menu
-- while the building is under construction or at the top tier.
local function building_link(name, tier, under)
  local colour = under and "warning" or (tier > 0 and "success" or "dim")
  -- a name the markup could not carry (it never has been) is shown plain
  if name:find("[,;|@{}=]") then return col(colour, name) end
  local cmd = name:lower()
  local menu = ""
  if not under and tier < MAX_TIER then
    local label = tier > 0 and ("Upgrade " .. name .. " to T" .. (tier + 1)) or ("Build " .. name)
    menu = "menu=" .. label .. "|cbuild build " .. cmd .. " confirm,"
  end
  return "@{" .. colour .. "," .. menu .. "click=cbuild info " .. cmd .. "}" .. name .. "@{}"
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

-- ------------------------------------------------------------- Status tab
local function build_line(b)
  local left = N(b.due) - now()
  local when = left > 0 and (col("warning", span(left)) .. col("dim", "  (" .. os.date("%H:%M", N(b.due)) .. ")"))
                         or col("success", "due now")
  return esc(string.format("%-18s T%s   ", S(b.building), S(b.tier))) .. when
end

local function build_status()
  local L = {}
  local add = mkadd(L)

  if not seen_any then
    add("waiting for the Craft.* feed...")
    add(col("dim", "nothing arrives until the server offers Craft and you are subscribed (Craft 1)"))
    scrye.setState(P .. "status", table.concat(L, "\n"))
    return
  end

  add("-- Crafting --")
  if has(STATE, "stock_cap") then
    add("Storage      " .. esc(comma(STATE.stock_used) .. "/" .. comma(STATE.stock_cap))
      .. ((type(INFO.storage) == "table" and N(INFO.storage.tier) == 0) and col("dim", "   no Logistics Depot") or ""))
  end
  if has(STATE, "soul_cap") then
    if N(STATE.soul_cap) <= 0 then
      -- chelp souls: capacity is zero until a Soul Well is built, and every soul a
      -- kill earns before that is thrown away without a message
      add("Souls        " .. col("warning", "no Soul Well") .. col("dim", "   souls from kills are lost until one is built"))
    else
      add("Souls        " .. esc(comma(STATE.souls) .. "/" .. comma(STATE.soul_cap)))
      if type(STATE.soul_tiers) == "table" then
        for _, t in ipairs(STATE.soul_tiers) do
          if N(t.cap) > 0 or N(t.count) > 0 then
            add(esc(string.format("  T%s %-10s ", S(t.tier), S(t.name))) .. esc(comma(t.count) .. "/" .. comma(t.cap)))
          end
        end
      end
    end
  end
  if has(JOBS, "queue_cap") then
    add("Queue        " .. esc(comma(JOBS.queue_total) .. "/" .. comma(JOBS.queue_cap)))
  end
  if filled(JOBS.ready) then
    add("Ready        " .. col("success", #JOBS.ready .. " to collect") .. col("dim", "   ccollect"))
  end
  do
    local t = trainable()
    if #t > 0 then
      add("Train        " .. col("success", table.concat(t, ", ")) .. col("dim", "   xp ready - ctrain"))
    end
  end

  add("")
  add("-- Under construction --")
  if #builds() == 0 then add(col("dim", "nothing")) end
  for _, b in ipairs(builds()) do add(build_line(b)) end

  add("")
  add("-- Buildings --")
  add(col("dim", (have_bldgs and (#BLDGS .. " built") or "not listed yet")
    .. "   click: cbuild info   right-click: build / upgrade"))
  for _, g in ipairs(all_buildings()) do
    if #g.items > 0 then
      add(col("dim", g.realm))
      for _, name in ipairs(g.items) do
        local tier, b = tier_of(name), build_of(name)
        local pad = string.rep(" ", math.max(1, 21 - #name))
        local line = "  " .. building_link(name, tier, b ~= nil) .. pad
          .. (tier > 0 and esc("T" .. tier) or col("dim", "T0"))
        if b then line = line .. col("warning", "   building T" .. S(b.tier) .. ", " .. span(N(b.due) - now())) end
        add(line)
      end
    end
  end

  local realms = realm_names()
  if #realms > 0 then
    add("")
    add("-- Realms --")
    for _, name in ipairs(realms) do
      local r = type(INFO.realms) == "table" and INFO.realms[name] or nil
      local d = type(INFO.standing) == "table" and INFO.standing[name] or nil
      local line = esc(string.format("%-9s", name))
      if type(r) == "table" then
        line = line .. esc("level " .. S(r.level) .. "   xp " .. comma(r.xp) .. "   bonus " .. S(r.bonus))
      end
      if type(d) == "table" then
        -- the three drop numbers move with the realm's drop skill (salvaging took
        -- Science to 4 / 2 / 1); what "extra" and "bump" buy is not known
        line = line .. col("dim", "   drops: rank " .. S(d.drop_rank) .. ", extra " .. S(d.drop_extra) .. ", bump " .. S(d.drop_bump))
      end
      add(line)
    end
  end

  if type(INFO.bonuses) == "table" and next(INFO.bonuses) ~= nil then
    add("")
    add("-- Bonuses --")
    local keys, bits = {}, {}
    for k in pairs(INFO.bonuses) do keys[#keys + 1] = k end
    table.sort(keys)
    for _, k in ipairs(keys) do bits[#bits + 1] = (S(k):gsub("_", " ")) .. " " .. S(INFO.bonuses[k]) end
    add(esc(table.concat(bits, "   ")))
  end

  scrye.setState(P .. "status", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Tokens tab
local function build_tokens()
  local L = {}
  local add = mkadd(L)
  local keys = {}
  for k in pairs(TOKENS) do keys[#keys + 1] = k end
  table.sort(keys)
  if #keys == 0 then
    add("waiting for Craft.Statedata...")
    scrye.setState(P .. "tokens", table.concat(L, "\n"))
    return
  end
  for i, k in ipairs(keys) do
    local list = {}
    for _, t in ipairs(TOKENS[k]) do list[#list + 1] = t end
    table.sort(list, function(a, b) return N(a.level) < N(b.level) end)
    if i > 1 then add("") end
    add("-- " .. ((list[1] and S(list[1].realm) ~= "") and S(list[1].realm) or cap1(k:match("^tokens_(.*)$"))) .. " --")
    for _, t in ipairs(list) do
      local line = string.format("T%s %-14s %6s", S(t.level), S(t.name), comma(t.qty))
      add(N(t.qty) > 0 and esc(line) or col("dim", line))
    end
  end
  scrye.setState(P .. "tokens", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Skills tab
-- 0 = xp ready to train, 1 = unlocked, 2 = locked
local function skill_group(s)
  if N(s.can_train) > 0 then return 0 end
  if N(s.unlocked) > 0 then return 1 end
  return 2
end

local function build_skills()
  local names = {}
  for k in pairs(SKILLS) do names[#names + 1] = k end
  if #names == 0 then
    scrye.setState(P .. "skills_head", "waiting for Craft.Skills...")
    scrye.setState(P .. "skills", "")
    return
  end
  table.sort(names, function(a, b)
    local ga, gb = skill_group(SKILLS[a]), skill_group(SKILLS[b])
    if ga ~= gb then return ga < gb end
    local ra, rb = N(SKILLS[a].rank), N(SKILLS[b].rank)
    if ra ~= rb then return ra > rb end
    local ia, ib = N(SKILLS[a].into), N(SKILLS[b].into)
    if ia ~= ib then return ia > ib end
    return a < b
  end)
  local count = { [0] = 0, [1] = 0, [2] = 0 }
  local rows = {}
  for _, k in ipairs(names) do
    local s = SKILLS[k]
    local g = skill_group(s)
    count[g] = count[g] + 1
    -- the trained rank, and the rank the banked xp has already earned when it is ahead
    local rank = S(s.rank)
    if N(s.xp_rank) > N(s.rank) then rank = rank .. " (xp " .. S(s.xp_rank) .. ")" end
    local xp = comma(s.into) .. "/" .. comma(N(s.into) + N(s.to_next))
    local cost = S(s.train_cost) .. " " .. S(s.train_abbr)
    local state = g == 0 and "xp ready" or (g == 2 and "locked" or "")
    rows[#rows + 1] = table.concat({ k, S(s.train_realm), rank, xp, cost, state }, "\t")
  end
  scrye.setState(P .. "skills_head",
    count[0] .. " with the xp to train   " .. count[1] .. " open   " .. count[2] .. " locked")
  scrye.setState(P .. "skills", table.concat(rows, "\n"))
end

-- ------------------------------------------------------------- Market tab
local function build_market()
  if next(MARKET) == nil then
    scrye.setState(P .. "market_head", "waiting for Craft.Market...")
    scrye.setState(P .. "market", "")
    return
  end
  local rows = {}
  local list = type(MARKET.exchanges) == "table" and MARKET.exchanges or {}
  for _, x in ipairs(list) do
    if type(x) == "table" then
      rows[#rows + 1] = table.concat({
        S(x.id), S(x.player),
        comma(x.give_qty) .. " " .. token_name(x.give_token),
        comma(x.want_qty) .. " " .. token_name(x.want_token),
        has(x, "created") and span(now() - N(x.created)) or "",
      }, "\t")
    end
  end
  -- the other six lists have only ever arrived empty: counted here, and printed
  -- whole on the Raw tab the day one of them is not
  local others = {}
  local keys = {}
  for k, v in pairs(MARKET) do if k ~= "exchanges" and type(v) == "table" then keys[#keys + 1] = k end end
  table.sort(keys)
  for _, k in ipairs(keys) do others[#others + 1] = (k:gsub("_", " ")) .. " " .. #MARKET[k] end
  scrye.setState(P .. "market_head",
    #rows .. " token exchange(s)" .. (#others > 0 and ("   " .. table.concat(others, "   ")) or ""))
  scrye.setState(P .. "market", table.concat(rows, "\n"))
end

-- ------------------------------------------------------------- Recipes tab
-- The catalogue is some 700 recipes, far too many for a panel: the tab is one row
-- per building, and 'crafthud recipes <building>' prints a building's list.
local function recipe_buildings()
  local by, order = {}, {}
  for _, e in ipairs(catalogue()) do
    local b = by[e.building]
    if not b then
      b = { name = e.building, realm = e.realm, n = 0, lo = e.rank, hi = e.rank }
      by[e.building] = b; order[#order + 1] = b
    end
    b.n = b.n + 1
    if e.rank < b.lo then b.lo = e.rank end
    if e.rank > b.hi then b.hi = e.rank end
  end
  table.sort(order, function(a, b)
    if a.realm ~= b.realm then return a.realm < b.realm end
    return a.name < b.name
  end)
  return order
end

local function build_recipes()
  local list = recipe_buildings()
  if #list == 0 then
    scrye.setState(P .. "recipes_head", "waiting for Craft.Recipes...")
    scrye.setState(P .. "recipes", "")
    return
  end
  local total, rows = 0, {}
  for _, b in ipairs(list) do
    total = total + b.n
    rows[#rows + 1] = table.concat({ b.name, b.realm, tostring(b.n), b.lo .. "-" .. b.hi, owned(b.name) and "built" or "" }, "\t")
  end
  scrye.setState(P .. "recipes_head",
    comma(total) .. " recipes in " .. #list .. " buildings - the whole catalogue, not what you know")
  scrye.setState(P .. "recipes", table.concat(rows, "\n"))
end

-- ----------------------------------------------------------------- Raw tab
-- Everything that has arrived with something in it and that this plugin cannot
-- name yet. The point of the tab is the next capture: a list that was always
-- empty and now is not shows up here, whole, under the server's own key.
local function build_raw()
  local L = {}
  local add = mkadd(L)
  local function section(title, lines)
    if #lines == 0 then return end
    if #L > 0 then add("") end
    add("-- " .. title .. " --")
    for _, l in ipairs(lines) do add(esc(l)) end
  end

  do
    local lines = {}
    for _, k in ipairs({ "ready", "queues", "pending_builds", "masterwork" }) do
      if filled(JOBS[k]) then lines[#lines + 1] = k .. " " .. raw(JOBS[k]) end
    end
    section("Craft.Jobs", lines)
  end
  do
    local lines, keys = {}, {}
    for k, v in pairs(MARKET) do if k ~= "exchanges" and filled(v) then keys[#keys + 1] = k end end
    table.sort(keys)
    for _, k in ipairs(keys) do lines[#lines + 1] = k .. " " .. raw(MARKET[k]) end
    section("Craft.Market", lines)
  end
  do
    -- a refinery row is worth showing once any of its three numbers is not zero
    local lines, keys = {}, {}
    for k in pairs(REFS) do keys[#keys + 1] = k end
    table.sort(keys, function(a, b) return chunk_no(a) < chunk_no(b) end)
    for _, k in ipairs(keys) do
      for _, row in ipairs(REFS[k]) do
        local a, b, c = S(row):match("|([^|]*)|([^|]*)|([^|]*)$")
        if a and (N(a) ~= 0 or N(b) ~= 0 or N(c) ~= 0) then lines[#lines + 1] = S(row) end
      end
    end
    section("Craft.Structures refineries (building|step|material|?|?|?)", lines)
  end
  do
    local lines = {}
    for _, k in ipairs({ "finished", "items", "materials" }) do
      if has(EVENT, k) then lines[#lines + 1] = k .. " " .. raw(EVENT[k]) end
    end
    section("Craft.Event", lines)
  end
  do
    local pkgs = {}
    for pkg in pairs(EXTRA) do pkgs[#pkgs + 1] = pkg end
    table.sort(pkgs)
    for _, pkg in ipairs(pkgs) do
      local lines, keys = {}, {}
      for k in pairs(EXTRA[pkg]) do keys[#keys + 1] = k end
      table.sort(keys)
      for _, k in ipairs(keys) do lines[#lines + 1] = k .. " " .. raw(EXTRA[pkg][k]) end
      section(pkg .. " (not named yet)", lines)
    end
  end

  if #L == 0 then add(col("dim", "nothing unexplained has arrived")) end
  scrye.setState(P .. "raw", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- one-liner
local function summary()
  if not seen_any then return "waiting for the crafting feed" end
  local bits = {}
  if has(STATE, "stock_cap") then bits[#bits + 1] = "Stock " .. comma(STATE.stock_used) .. "/" .. comma(STATE.stock_cap) end
  if next(TOKENS) ~= nil then bits[#bits + 1] = comma(token_total()) .. " tokens" end
  if have_bldgs then bits[#bits + 1] = #BLDGS .. " building" .. (#BLDGS == 1 and "" or "s") end
  local soonest
  for _, b in ipairs(builds()) do
    if not soonest or N(b.due) < N(soonest.due) then soonest = b end
  end
  if soonest then bits[#bits + 1] = "building " .. S(soonest.building) .. " " .. span(N(soonest.due) - now()) end
  if filled(JOBS.ready) then bits[#bits + 1] = #JOBS.ready .. " READY" end
  local t = trainable()
  if #t > 0 then bits[#bits + 1] = #t .. " to train" end
  return #bits > 0 and table.concat(bits, "   ") or "crafting"
end

local function publish_gauges()
  scrye.setState(P .. "stock",     S(STATE.stock_used))
  scrye.setState(P .. "stock_cap", S(STATE.stock_cap))
end

local BUILDERS = {
  status = build_status, tokens = build_tokens, skills = build_skills,
  market = build_market, recipes = build_recipes, raw = build_raw,
}
local ALL = { "status", "tokens", "skills", "market", "recipes", "raw" }

flush = function()
  flush_pending = false
  for sec in pairs(dirty) do
    local b = BUILDERS[sec]
    if b then pcall(b) end
  end
  publish_gauges()
  scrye.setState(P .. "summary", summary())
  dirty = {}
end

-- ------------------------------------------------------------------ feed
local function feed(pkg, fn, ...)
  local secs = { ... }
  scrye.onGmcp(pkg, function(json)
    local ok, t = pcall(scrye.json.decode, json)
    if not ok or type(t) ~= "table" then return end
    seen_any = true
    if fn(t) == false then return end      -- nothing worth redrawing for
    for _, s in ipairs(secs) do dirty[s] = true end
    schedule_flush()
  end)
end

feed("Craft.Infodata", function(t) merge(INFO, t) end, "status")

feed("Craft.Statedata", function(t)
  for k, v in pairs(t) do
    if k == "full" or k == "page" or k == "pages" then
      -- paging words, not data
    elseif k:find("^tokens_") and type(v) == "table" then
      TOKENS[k] = v
    elseif k == "stock_used" or k == "stock_cap" or k == "souls" or k == "soul_cap" or k == "soul_tiers" then
      STATE[k] = v
    elseif k:find("^materials_chunks_g%d+$") then
      -- one key whose NAME changes (g1, g133, g168 ...) and whose value never has:
      -- keep only the latest, or the Raw tab grows a line for every one
      EXTRA["Craft.Statedata"] = EXTRA["Craft.Statedata"] or {}
      for old in pairs(EXTRA["Craft.Statedata"]) do
        if old:find("^materials_chunks_g%d+$") then EXTRA["Craft.Statedata"][old] = nil end
      end
      EXTRA["Craft.Statedata"][k] = v
    else
      extra("Craft.Statedata", k, v)
    end
  end
end, "status", "tokens", "market", "raw")     -- market: a token's name comes from here

feed("Craft.Skills", function(t)
  for k, v in pairs(t) do
    if k:find("^skills") and type(v) == "table" then
      for name, s in pairs(v) do if type(s) == "table" then SKILLS[name] = s end end
    elseif k ~= "full" and k ~= "page" and k ~= "pages" then
      extra("Craft.Skills", k, v)
    end
  end
end, "skills", "status")

feed("Craft.Structures", function(t)
  for k, v in pairs(t) do
    if k == "bldgs" and type(v) == "table" then
      wipe(BLDGS); have_bldgs = true
      for _, row in ipairs(v) do
        local name, tier = S(row):match("^([^|]*)|?(.*)$")
        BLDGS[#BLDGS + 1] = { name = name, tier = (tier ~= "" and tier or "?") }
      end
    elseif k:find("^refineries") and type(v) == "table" then
      REFS[k] = v
    elseif k ~= "full" and k ~= "page" and k ~= "pages" then
      extra("Craft.Structures", k, v)
    end
  end
end, "status", "recipes", "raw")

feed("Craft.Recipes", function(t)
  local page = N(t.page) > 0 and N(t.page) or 1
  for k, v in pairs(t) do
    if k:find("^catalogue_") and type(v) == "table" then
      -- Page 1 starts the key over. The catalogue is cut differently from one login to
      -- the next (18 Nov: catalogue_chaos opened with Bone Forge in one session and
      -- Flesh Atelier in the next), so a key that had three pages can come back with
      -- two - and the old third would otherwise stay, counted twice.
      if page == 1 or not CAT[k] then CAT[k] = {} end
      CATB = nil
      CAT[k][page] = v            -- the pages share a key: keep each, or page 3 is all that is left
    elseif k ~= "full" and k ~= "page" and k ~= "pages" then
      extra("Craft.Recipes", k, v)
    end
  end
end, "recipes", "status")      -- status: the catalogue names the production buildings

feed("Craft.Jobs", function(t)
  -- a build that was on the list, is not any more, and was due: it finished.
  -- (One that vanishes long before its time was cancelled, and is not announced.)
  if type(t.builds) == "table" then
    local still = {}
    for _, b in ipairs(t.builds) do if type(b) == "table" then still[S(b.building)] = true end end
    for _, b in ipairs(builds()) do
      if not still[S(b.building)] and N(b.due) - now() <= 60 then
        scrye.print(TAG .. col("success", S(b.building) .. " T" .. S(b.tier) .. " is built"))
      end
    end
  end
  merge(JOBS, t)
end, "status", "raw")

feed("Craft.Market", function(t) merge(MARKET, t) end, "market", "raw")

feed("Craft.Event", function(t)
  -- {id} every two seconds: remembered, and not worth a redraw. The three lists
  -- are kept only when they carry something, so the last real event stays readable.
  EVENT.id = t.id
  local news = false
  for _, k in ipairs({ "finished", "items", "materials" }) do
    if filled(t[k]) then EVENT[k] = t[k]; news = true end
  end
  for k, v in pairs(t) do
    if k ~= "id" and k ~= "full" and k ~= "finished" and k ~= "items" and k ~= "materials" then
      extra("Craft.Event", k, v); news = true
    end
  end
  return news
end, "raw")

-- offered by the server, never yet sent: whatever they turn out to carry is shown
for _, pkg in ipairs({ "Craft.State", "Craft.Info", "Craft.Extra" }) do
  feed(pkg, function(t)
    for k, v in pairs(t) do extra(pkg, k, v) end
  end, "raw")
end

-- A new connection may be another character: everything starts over.
scrye.onConnect(function()
  wipe(INFO); wipe(STATE); wipe(JOBS); wipe(MARKET); wipe(SKILLS); wipe(TOKENS)
  wipe(BLDGS); wipe(REFS); wipe(CAT); wipe(EVENT); wipe(EXTRA)
  have_bldgs = false; seen_any = false; CATB = nil
  for _, s in ipairs(ALL) do dirty[s] = true end
  flush()
end)

-- The countdown: while something is under construction the Status page and the
-- one-liner are redrawn each second, and not at all otherwise.
scrye.every(1, function()
  if #builds() == 0 then return end
  build_status()
  scrye.setState(P .. "summary", summary())
end)

-- --------------------------------------------------------------- aliases
-- 'crafthud' and not a c-word: the game owns chelp, cinv, cscore, cbuild, ctrain,
-- cmake, ccollect, crefine, cextract, csouls, cenchant, cmaster, ctool, cdiscard,
-- cmarket and crecipes, and a plugin alias is matched first.
scrye.addAlias{ pattern = "^crafthud$", regex = true, run = function()
  dirty.status = true; flush()
  for line in (scrye.getState(P .. "status") or ""):gmatch("[^\n]+") do
    scrye.print(TAG .. line)
  end
end }

-- crafthud recipes            the buildings, with how many recipes each has
-- crafthud recipes <building> that building's recipes, lowest rank first
local function print_recipes(what)
  what = S(what):lower():gsub("^%s+", ""):gsub("%s+$", "")
  local list = recipe_buildings()
  if #list == 0 then scrye.print(TAG .. "no recipes have arrived yet (Craft.Recipes)"); return end
  if what == "" then
    for _, b in ipairs(list) do
      scrye.print(TAG .. esc(string.format("%-8s %-22s %3d recipes, ranks %d-%d", b.realm, b.name, b.n, b.lo, b.hi))
        .. (owned(b.name) and col("success", "  built") or ""))
    end
    scrye.print(TAG .. col("dim", "crafthud recipes <building> lists one"))
    return
  end
  -- the building asked for: an exact name wins, else every building containing the words
  local want = {}
  for _, b in ipairs(list) do if b.name:lower() == what then want[b.name] = true end end
  if next(want) == nil then
    for _, b in ipairs(list) do if b.name:lower():find(what, 1, true) then want[b.name] = true end end
  end
  if next(want) == nil then scrye.print(TAG .. esc("no building matches '" .. what .. "'")); return end
  local found = {}
  for _, e in ipairs(catalogue()) do if want[e.building] then found[#found + 1] = e end end
  table.sort(found, function(a, b)
    if a.building ~= b.building then return a.building < b.building end
    if a.rank ~= b.rank then return a.rank < b.rank end
    return a.name < b.name
  end)
  local last
  for _, e in ipairs(found) do
    if e.building ~= last then
      last = e.building
      scrye.print(TAG .. col("accent,bold", e.building) .. col("dim", "  " .. e.realm))
    end
    scrye.print(TAG .. esc(string.format("  r%-3d T%d  %s", e.rank, e.tier, e.name)))
  end
end

scrye.addAlias{ pattern = "^crafthud recipes$", regex = true, run = function() print_recipes("") end }
scrye.addAlias{ pattern = "^crafthud recipes (.+)$", regex = true, run = function(what) print_recipes(what) end }

-- --------------------------------------------------------------- panel
scrye.addPanel{
  title = "Crafting",
  width = 440,
  accent = "#B5894A",          -- signature: worked brass
  tabs = {
    { title = "Status", widgets = {
        { type = "value", text = "", bind = P .. "summary", color = "info" },
        { type = "gauge", text = "Storage", value = P .. "stock", max = P .. "stock_cap", color = "info" },
        { type = "text",  bind = P .. "status" },
    } },
    { title = "Skills", widgets = {
        { type = "label", bind = P .. "skills_head", color = "dim" },
        { type = "table", bind = P .. "skills",
          columns = { "Skill", "Realm", "Rank", "XP", "Cost", "" }, align = "llrrrl" },
    } },
    { title = "Tokens", widgets = { { type = "text", bind = P .. "tokens" } } },
    { title = "Market", widgets = {
        { type = "label", bind = P .. "market_head", color = "dim" },
        { type = "table", bind = P .. "market",
          columns = { "#", "Player", "Gives", "Wants", "Age" }, align = "rlllr" },
    } },
    { title = "Recipes", widgets = {
        { type = "label", bind = P .. "recipes_head", color = "dim" },
        { type = "table", bind = P .. "recipes",
          columns = { "Building", "Realm", "Recipes", "Ranks", "" }, align = "llrrl" },
    } },
    { title = "Raw", widgets = { { type = "text", bind = P .. "raw" } } },
  },
}

-- ------------------------------------------------------------------ init
for _, s in ipairs(ALL) do dirty[s] = true end
flush()
