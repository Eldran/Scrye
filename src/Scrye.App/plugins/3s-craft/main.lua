-- 3S Crafting -- a HUD for the crafting system, built on the Craft.* GMCP
-- packages. 3Scapes only, any guild: crafting belongs to the character, not to a
-- guild, so this sits beside 3s-vitals and 3s-merc rather than inside a guild HUD.
--
-- 0.13.0, and early: the system went live on 16 Nov 2026 and is still being built on the
-- server, so this is written from eight captures of one character (Keyser, 16-25 Nov
-- 2026), who by the last had a dozen buildings, a production queue and goods to collect.
-- Every field READ below
-- was seen with a real value; what was only ever seen empty or zero is not guessed
-- at -- it is shown raw on the "Raw" tab under the server's own name, so the next
-- capture that fills it in can be read straight off the panel.
--
-- The plugin never sends a command of its own accord. It reads the feed and draws
-- it; the only commands it can cause are the two a building's name carries as a
-- link (0.2.0): 'cbuild info <name>' on a click, and 'cbuild build <name> confirm'
-- from the right-click menu - and the two a skill's name carries (0.8.0): 'chelp <skill>'
-- on a click, 'ctrain <skill>' from its menu - and the two a known recipe's name carries
-- (0.11.0): 'crecipes <name>' on a click, 'cmake <building> <name> <qty>' from its menu -
-- and a refinery's (0.12.0): 'crefine' on a click, and from an open material's menu
-- 'crefine allocate <realm> <material> <n>'.
--
-- Packages consumed, and the three shapes they come in:
--
--   snapshot, then deltas of WHOLE sub-objects (no "full" on the delta)
--     Craft.Infodata   bonuses{}, realms{Realm{level,xp,bonus,license}},
--                      soul{cap,tier}, storage{cap,tier},
--                      standing{Realm{drop_rank,drop_extra,drop_bump}}
--     Craft.Jobs       builds[{building,tier,due}], queue_total, queue_cap, and
--                      pending_builds[{building,tier,need,token,realm,global}],
--                      queues[{building,current_name,current_id,current_due,items,
--                      depth,cap,stalled}], ready[{name,quality,expires,obj}], and
--                      masterwork[] (only ever seen empty -> Raw tab). Paged since
--                      20 Nov. SINCE 25 NOV none of those names: generations of
--                      numbered chunks, jobs_<gen>_<i>_<n>_<kind> (see commit_jobs).
--     Craft.Market     exchanges[{id,player,give_token,give_qty,want_token,
--                      want_qty,created}], material_orders[{id,type,material_name,
--                      material,qty,price,player,created}], scroll_orders[{id,type,
--                      recipe_name,recipe,price,player,created}], and auctions[]
--                      orders[] scroll_auctions[] scroll_outbox[] (only ever seen
--                      empty -> Raw tab). Paged, and one list can run over pages.
--     Craft.Statedata  four pages: stock_used/stock_cap/souls/soul_cap/
--                      soul_tiers[], then tokens_chaos[], tokens_fantasy[],
--                      tokens_science[] ({level,realm,qty,name}); a token delta is
--                      the whole realm's array. And the stored materials, as a
--                      generation: materials_g<N>c<i>[{name,qty,category,realm,
--                      quality,best}], then "materials_chunks_g<N>": how many.
--
--   chunked catalogues: one list cut across many messages under NUMBERED KEYS,
--   the whole set repeated every few minutes
--     Craft.Skills     skills_0 .. skills_17, two skills per message, each
--                      {rank,trained,xp_rank,into,to_next,can_train,unlocked,
--                      train_cost,train_tier,train_realm,train_abbr}
--     Craft.Structures bldgs["Name|tier"], and refineries, refineries_1 ..
--                      refineries_14 of "Building|step|Material|allocated %|
--                      building tier|max tier" -- all 0 for a refinery not built
--     Craft.Recipes    catalogue_<realm>, catalogue_<realm>_<N>, each ALSO paged
--                      (page/pages) under the same key: entries of {name,
--                      building,tier,rank}. This is the whole catalogue, not what
--                      the character knows -- it lists tier 5 recipes for
--                      buildings that are not built. What the character DOES know
--                      comes as known_<realm>, known_<realm>_<N>: {name,building,
--                      rank,materials,time,id,skill}.
--
--   a counter
--     Craft.Event      {id} every two seconds, counting up; with materials[{name,
--                      qty}] when some arrive and finished[{building,name,quality,
--                      quality_name}] when a job is done. items[] is still empty.
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
local BPARTS = {}        -- "bldgs" / "bldgs_1" -> { [page] = ["Name|tier"] }
local REFS = {}          -- "refineries_3" -> ["Building|step|Material|a|b|c"]
local CAT = {}           -- "catalogue_chaos_2" -> { [page] = [entries] }
local EVENT = {}         -- last id, and the last non-empty finished/items/materials
local EXTRA = {}         -- package -> { key -> value } for keys this plugin cannot name
local JOBGEN = {}        -- generation -> { n = 9, chunks = { [i] = { kind = "q", list = {...} } } }: see commit_jobs
local PARTS = {}         -- snapshot -> key -> { [page] = list } or { delta = list }: see merge_paged
local MATS = nil         -- the stored materials, once a whole generation has arrived
local MATGEN = {}        -- generation -> { count = n or nil, chunks = { [i] = [materials] } }
local KNOWNR = {}        -- "known_science_1" -> { [page] = [recipes] }: what the character can make
local QNAME = {}         -- a quality's number -> its name, learned from Craft.Event ("poor" for 2)
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

-- A realm's colour, wherever a realm's things are listed (0.4.0): Chaos red, Fantasy
-- purple, Science blue. What belongs to no realm - the global buildings, and the skills
-- any realm's tokens train - is yellow (0.5.0).
local ANY_COLOUR = "#E8C547"
local REALM_COLOUR = { Chaos = "#E0605A", Fantasy = "#B48CF0", Science = "#5FA8F5", Global = ANY_COLOUR }
local IS_REALM = { Chaos = true, Fantasy = true, Science = true }

-- A building's tier, each in a colour of its own (0.5.0) so what you own reads down the
-- column: T0 dim (not built), then white, green, teal, orange, and pink for the top tier.
-- None of them is a realm's colour - the tier sits right beside the realm-coloured name.
local TIER_COLOUR = { [0] = "dim", "#E6E6E6", "#6CCB5F", "#3FC1C9", "#F28C28", "#FF5FA2,bold" }
local function tier_label(tier) return col(TIER_COLOUR[tier] or "#FF5FA2,bold", "T" .. tier) end

local function wipe(t) for k in pairs(t) do t[k] = nil end end

-- Merge a payload into a snapshot: top-level keys overwrite, a nested table
-- overwrites WHOLE (the server sends the whole sub-object), and the paging words
-- are not data.
local function merge(snap, t)
  for k, v in pairs(t) do
    if k ~= "full" and k ~= "page" and k ~= "pages" then snap[k] = v end
  end
end

-- Craft.Jobs and Craft.Market are paged since 20 Nov 2026, and ONE LIST CAN RUN OVER
-- SEVERAL PAGES under the same key (material_orders came as three rows on page 2 and
-- two more on page 3), so a plain merge would keep only the last page. Each page of a
-- list is kept and the snapshot holds them joined. A page that comes back WITHOUT a key
-- it used to carry drops that part (the list got shorter), and so does a page number
-- past "pages". A delta carries no page and is the whole list.
local function merge_paged(snap, t)
  PARTS[snap] = PARTS[snap] or {}
  local parts = PARTS[snap]
  local page, pages = tonumber(t.page), tonumber(t.pages)
  local touched = {}
  if page then
    for k, ps in pairs(parts) do
      if ps[page] ~= nil and type(t[k]) ~= "table" then ps[page] = nil; touched[k] = true end
    end
  end
  if pages then
    for k, ps in pairs(parts) do
      for n in pairs(ps) do
        if type(n) == "number" and n > pages then ps[n] = nil; touched[k] = true end
      end
    end
  end
  for k, v in pairs(t) do
    if k == "full" or k == "page" or k == "pages" then
      -- paging words, not data
    elseif type(v) == "table" then
      if page then
        parts[k] = parts[k] or {}
        parts[k].delta = nil
        parts[k][page] = v
      else
        parts[k] = { delta = v }
      end
      touched[k] = true
    else
      snap[k] = v
    end
  end
  for k in pairs(touched) do
    local ps = parts[k]
    if ps.delta then
      snap[k] = ps.delta
    else
      local nums, all = {}, {}
      for n in pairs(ps) do nums[#nums + 1] = n end
      table.sort(nums)
      for _, n in ipairs(nums) do for _, x in ipairs(ps[n]) do all[#all + 1] = x end end
      snap[k] = all
    end
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

-- The refineries, from rows of "Building|material tier|Material|allocated %|building tier|
-- max tier" (the three numbers named by the game's own coder, 20 Nov 2026). A refinery that
-- is not built reads 0|0|0 on every row. A refinery's ten materials are tiers 1 to 10
-- (Joakim: "tier 10 is max"), and max tier is how far up that list this refinery reaches
-- for now: 2 at building tier 1, which the game prints as "T1 (stages 1-2)".
local function refineries()
  local keys, by, order = {}, {}, {}
  for k in pairs(REFS) do keys[#keys + 1] = k end
  table.sort(keys, function(a, b) return chunk_no(a) < chunk_no(b) end)
  for _, k in ipairs(keys) do
    for _, row in ipairs(REFS[k]) do
      local name, mtier, material, pct, tier, max = S(row):match("^([^|]*)|([^|]*)|([^|]*)|([^|]*)|([^|]*)|([^|]*)$")
      if name then
        local r = by[name]
        if not r then r = { name = name, tier = 0, max = 0, top = 0, alloc = {}, stages = {} }; by[name] = r; order[#order + 1] = r end
        r.stages[#r.stages + 1] = { tier = N(mtier), material = material, pct = N(pct) }
        if N(mtier) > r.top then r.top = N(mtier) end
        if N(tier) > r.tier then r.tier = N(tier) end
        if N(max) > r.max then r.max = N(max) end
        if N(pct) > 0 then r.alloc[#r.alloc + 1] = material .. " " .. S(pct) .. "%" end
      end
    end
  end
  return order
end

local function queues() return type(JOBS.queues) == "table" and JOBS.queues or {} end
local function ready() return type(JOBS.ready) == "table" and JOBS.ready or {} end

-- "poor" for 2 once Craft.Event has said so; "q2" until then
local function quality_name(q)
  if q == nil then return "" end
  return QNAME[N(q)] or ("q" .. S(q))
end

-- The stored materials. They arrive as a GENERATION: materials_g96c0 .. materials_g96c8,
-- two materials a message, then "materials_chunks_g96": 9 to say how many there were.
-- Every change sends a whole new generation under a new number. A generation is shown
-- only once it is complete, so the list never flickers half-built; "materials_chunks_g<N>": 0
-- is an empty store.
local function commit_materials(gen)
  local g = MATGEN[gen]
  if not g or not g.count then return end
  local all = {}
  for i = 0, g.count - 1 do
    if type(g.chunks[i]) ~= "table" then return end
    for _, m in ipairs(g.chunks[i]) do if type(m) == "table" then all[#all + 1] = m end end
  end
  MATS = all
  wipe(MATGEN)       -- whatever else was half here is older: the server sends them in order
end

-- every recipe the character knows, with its realm taken from the key it came under
local function known_recipes()
  local out = {}
  for key, pages in pairs(KNOWNR) do
    local realm = cap1(key:match("^known_(%a+)") or "")
    for _, list in pairs(pages) do
      for _, r in ipairs(list) do
        if type(r) == "table" then
          out[#out + 1] = { realm = realm, building = S(r.building), name = S(r.name), rank = N(r.rank),
                            materials = S(r.materials), time = N(r.time), id = S(r.id) }
        end
      end
    end
  end
  table.sort(out, function(a, b)
    if a.realm ~= b.realm then return a.realm < b.realm end
    if a.building ~= b.building then return a.building < b.building end
    if a.rank ~= b.rank then return a.rank < b.rank end
    return a.name < b.name
  end)
  return out
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

-- Builds that are asked for and not yet under way (seen 18 Nov 2026):
--   { building = "Soul Well", tier = 1, need = 400, token = "", realm = "", global = 1 }
-- "need" is a number of tokens; whether it is the whole price or what is still missing is
-- not known from one sighting, so it is shown as the server gives it. token and realm were
-- empty for a global building - a realm's building presumably names its token there.
local function pending() return type(JOBS.pending_builds) == "table" and JOBS.pending_builds or {} end

local function pending_of(building)
  for _, p in ipairs(pending()) do if type(p) == "table" and S(p.building) == building then return p end end
  return nil
end

-- "need 400", or "need 400 hyper-charged" when the server names the token
local function need_text(p)
  local t = S(p.token)
  return "need " .. comma(p.need) .. (t ~= "" and (" " .. token_name(t)) or "")
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
  for _, p in ipairs(pending()) do
    if type(p) == "table" and not cat_buildings()[S(p.building)] then late[#late + 1] = { "Other", S(p.building) } end
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
-- The colour is the realm's; a building you own is bold in it, so built and not built
-- still read apart at a glance. A building the feed could not place (Other) is green when
-- built, dim when not.
local function building_link(name, tier, under, realm)
  local colour = REALM_COLOUR[realm] and (REALM_COLOUR[realm] .. (tier > 0 and ",bold" or ""))
    or (under and "warning" or (tier > 0 and "success" or "dim"))
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
local ALLOC_PCT = nil     -- an extra share on a material's menu, set with 'crafthud pct <n>'

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

  -- What the production buildings are making (seen 20 Nov 2026):
  --   { building, current_name, current_id, current_due, items = "A, A, A", depth, cap, stalled }
  -- depth counts the job in hand (4 with four names in items).
  if #queues() > 0 then
    add("")
    add("-- Production --")
    for _, q in ipairs(queues()) do
      if type(q) == "table" then
        local left = N(q.current_due) - now()
        local line = esc(string.format("%-18s ", S(q.building)))
        -- a queue that cannot start has no job in hand (25 Nov 2026: current_name "",
        -- current_id "", current_due 0, stalled 1): what waits is the first of its items
        local first_item = (S(q.items):match("^%s*([^,]+)") or ""):gsub("%s+$", "")
        if S(q.current_name) == "" then
          line = line .. (first_item ~= "" and (col("dim", "waiting: ") .. esc(first_item)) or col("dim", "idle"))
        else
          line = line .. esc(S(q.current_name) .. "   ")
            .. (left > 0 and (col("warning", span(left)) .. col("dim", "  (" .. os.date("%H:%M", N(q.current_due)) .. ")"))
                          or col("success", "due now"))
        end
        line = line .. col("dim", "   " .. S(q.depth) .. "/" .. S(q.cap) .. " queued")
        add(line)
        -- stalled: the job cannot start because a material is short (Joakim, 20 Nov 2026:
        -- one Smart Alloy missing). The feed does not say which, so the known recipe is
        -- asked what the job takes and the store how much of each there is.
        if N(q.stalled) ~= 0 then
          local why = ""
          for _, r in ipairs(known_recipes()) do
            if r.materials ~= "" and ((S(q.current_id) ~= "" and r.id:sub(-#S(q.current_id) - 1) == "-" .. S(q.current_id))
                or (S(q.current_id) == "" and first_item ~= "" and r.name == first_item)) then
              local bits = {}
              for part in r.materials:gmatch("[^,]+") do
                part = part:gsub("^%s+", ""):gsub("%s+$", "")
                local need, name = part:match("^(%d+)x%s+(.+)$")
                local bit = part
                if need and MATS then
                  local got = 0
                  for _, m in ipairs(MATS) do if S(m.name) == name then got = got + N(m.qty) end end
                  bit = part .. " (have " .. comma(got) .. ")"
                end
                bits[#bits + 1] = bit
              end
              why = " - needs " .. table.concat(bits, ", ")
              break
            end
          end
          add(col("warning,bold", "  STALLED") .. col("warning", " short of materials" .. why))
        end
      end
    end
  end
  -- What each refinery you own is set to refine, and which stages it has open. Links
  -- (0.12.0): the refinery's name and each open material run 'crefine' on a click, and a
  -- material's right-click MENU sets its share - 'crefine allocate <realm> <material> <n>',
  -- the number bare: the menu's label says "40%", the command says 40.
  do
    local first = true
    for _, r in ipairs(refineries()) do
      if r.tier > 0 then
        if first then
          add(""); add("-- Refineries --"); first = false
          add(col("dim", "click: crefine   right-click a material: allocate"))
        end
        -- the realm the command needs: the catalogue's for the building, else the realm
        -- the store gives one of its materials; with neither there is no menu
        local realm = cat_buildings()[r.name]
        if not realm and MATS then
          for _, st in ipairs(r.stages) do
            for _, m in ipairs(MATS) do if S(m.name) == st.material and S(m.realm) ~= "" then realm = S(m.realm) end end
          end
        end
        local colour = REALM_COLOUR[realm or ""] or "accent"
        add("@{" .. colour .. ",bold,click=crefine}" .. esc(r.name) .. "@{}" .. string.rep(" ", math.max(1, 19 - #r.name))
          -- 0% on every stage is not idle: the game's own Alloy Works page read "Working:
          -- next unit ETA 53m" with 0% beside both open stages (20 Nov 2026). So no warning.
          .. (#r.alloc > 0 and esc(table.concat(r.alloc, ", ")) or col("dim", "no split set"))
          .. col("dim", "   stages 1-" .. r.max .. " of " .. r.top))
        table.sort(r.stages, function(a, b) return a.tier < b.tier end)
        for _, st in ipairs(r.stages) do
          if st.tier <= r.max then
            local link
            if st.material:find("[,;|@{}=]") then
              link = col(colour, st.material)
            else
              local menu = ""
              if IS_REALM[realm or ""] then
                local pcts = { 0, 20, 40, 50, 60, 80, 100 }
                if ALLOC_PCT then pcts[#pcts + 1] = ALLOC_PCT end
                local e = {}
                for _, n in ipairs(pcts) do
                  e[#e + 1] = n .. "%|crefine allocate " .. realm:lower() .. " " .. st.material:lower() .. " " .. n
                end
                menu = "menu=" .. table.concat(e, ";") .. ","
              end
              link = "@{" .. colour .. "," .. menu .. "click=crefine}" .. st.material .. "@{}"
            end
            add("  " .. col("dim", string.format("T%-3d", st.tier)) .. link
              .. string.rep(" ", math.max(1, 22 - #st.material))
              .. col(st.pct > 0 and "success" or "dim", string.format("%3d%%", st.pct)))
          end
        end
      end
    end
  end
  -- What is finished and waiting: { name, quality, expires, obj }. It does not wait for ever.
  if #ready() > 0 then
    add("")
    add("-- Ready to collect --")
    for _, r in ipairs(ready()) do
      if type(r) == "table" then
        local line = esc(string.format("%-26s ", S(r.name))) .. col("dim", quality_name(r.quality))
        if has(r, "expires") then
          local left = N(r.expires) - now()
          line = line .. (left > 0 and col("dim", "   expires in " .. span(left)) or col("warning", "   expired"))
        end
        add(line)
      end
    end
  end

  add("")
  add("-- Under construction --")
  if #builds() == 0 and #pending() == 0 then add(col("dim", "nothing")) end
  for _, b in ipairs(builds()) do add(build_line(b)) end
  for _, p in ipairs(pending()) do
    if type(p) == "table" then
      add(esc(string.format("%-18s T%s   ", S(p.building), S(p.tier)))
        .. col("warning", "pending") .. col("dim", "  " .. need_text(p)))
    end
  end

  add("")
  add("-- Buildings --")
  add(col("dim", (have_bldgs and (#BLDGS .. " built") or "not listed yet")
    .. "   click: cbuild info   right-click: build / upgrade"))
  for _, g in ipairs(all_buildings()) do
    if #g.items > 0 then
      add(col(REALM_COLOUR[g.realm] and (REALM_COLOUR[g.realm] .. ",bold") or "dim", g.realm))
      for _, name in ipairs(g.items) do
        local tier, b = tier_of(name), build_of(name)
        local pad = string.rep(" ", math.max(1, 21 - #name))
        local line = "  " .. building_link(name, tier, b ~= nil, g.realm) .. pad
          .. tier_label(tier)
        if b then line = line .. col("warning", "   building T" .. S(b.tier) .. ", " .. span(N(b.due) - now())) end
        local p = pending_of(name)
        if p then line = line .. col("warning", "   pending T" .. S(p.tier)) .. col("dim", ", " .. need_text(p)) end
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

-- ---------------------------------------------------------- Materials tab
-- { name, qty, category = "Raw"|"Refined"|"Special", realm, quality = "crude, poor", best }.
-- "best" is shown under the server's own word: it read "crude" beside "crude, poor" while a
-- finished item of quality 2 was "poor", so which end of the scale it names is not known.
local function build_materials()
  local last = ""
  if filled(EVENT.materials) then
    local bits = {}
    for _, m in ipairs(EVENT.materials) do
      if type(m) == "table" then bits[#bits + 1] = "+" .. comma(m.qty) .. " " .. S(m.name) end
    end
    last = "   last in: " .. table.concat(bits, ", ")
  end
  if not MATS then
    scrye.setState(P .. "materials_head", "waiting for the materials (Craft.Statedata)..." .. last)
    scrye.setState(P .. "materials", "")
    return
  end
  local list, total = {}, 0
  for _, m in ipairs(MATS) do list[#list + 1] = m; total = total + N(m.qty) end
  table.sort(list, function(a, b)
    if S(a.category) ~= S(b.category) then return S(a.category) < S(b.category) end
    return S(a.name) < S(b.name)
  end)
  local L, cat = {}, nil
  if #list == 0 then
    L[#L + 1] = col("dim", "nothing in store")
  else
    L[#L + 1] = col("dim", string.format("%-22s %5s  %-8s %s", "Material", "Qty", "Best", "Qualities"))
  end
  for _, m in ipairs(list) do
    if S(m.category) ~= cat then
      cat = S(m.category)
      L[#L + 1] = ""
      L[#L + 1] = "@{accent,bold}-- " .. esc(cat) .. " --@{}"
    end
    L[#L + 1] = col(REALM_COLOUR[S(m.realm)] or "dim",
      string.format("%-22s %5s  %-8s %s", S(m.name), comma(m.qty), S(m.best), S(m.quality)))
  end
  scrye.setState(P .. "materials_head",
    #list .. " material" .. (#list == 1 and "" or "s") .. ", " .. comma(total) .. " in store" .. last)
  scrye.setState(P .. "materials", table.concat(L, "\n"))
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
    -- the heading in the realm's colour, and each token in its TIER's colour (0.6.0) - the
    -- same six the buildings' tiers wear, so "orange" means tier 4 on either tab. A token
    -- you hold none of stays dim, whatever its tier.
    local realm = (list[1] and S(list[1].realm) ~= "") and S(list[1].realm) or cap1(k:match("^tokens_(.*)$"))
    L[#L + 1] = col((REALM_COLOUR[realm] or "accent") .. ",bold", "-- " .. realm .. " --")
    for _, t in ipairs(list) do
      local line = string.format("T%s %-14s %6s", S(t.level), S(t.name), comma(t.qty))
      add(col(N(t.qty) > 0 and (TIER_COLOUR[N(t.level)] or "accent") or "dim", line))
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

-- The realm a skill is listed under: the one whose tokens train it. The six that take
-- any realm's tokens (the three drop skills and the three general ones) come last,
-- under their own heading.
local SKILL_GROUPS = { "Chaos", "Fantasy", "Science", "Any realm" }
local function skill_realm(s)
  local r = S(s.train_realm)
  return IS_REALM[r] and r or "Any realm"
end

-- A skill's name as a link (0.8.0): click asks the game about it ('chelp <skill>'), and
-- the right button opens a one-entry menu that trains it ('ctrain <skill>'). A menu, as
-- on a building and for the same reason - training spends tokens. A locked skill has no
-- menu: there is nothing to train yet.
local function skill_link(name, colour, locked)
  if name:find("[,;|@{}=]") then return col(colour, name) end
  local menu = locked and "" or ("menu=Train " .. name .. "|ctrain " .. name .. ",")
  return "@{" .. colour .. "," .. menu .. "click=chelp " .. name .. "}" .. name .. "@{}"
end

local function build_skills()
  local by, total = {}, 0
  for name, s in pairs(SKILLS) do
    local r = skill_realm(s)
    by[r] = by[r] or {}
    by[r][#by[r] + 1] = name
    total = total + 1
  end
  if total == 0 then
    scrye.setState(P .. "skills_head", "waiting for Craft.Skills...")
    scrye.setState(P .. "skills", "")
    return
  end
  local count = { [0] = 0, [1] = 0, [2] = 0 }
  local L = {}
  for _, realm in ipairs(SKILL_GROUPS) do
    local names = by[realm]
    if names then
      -- inside a realm: xp ready first, then what is open (highest rank, then most xp),
      -- then the locked ones
      table.sort(names, function(a, b)
        local ga, gb = skill_group(SKILLS[a]), skill_group(SKILLS[b])
        if ga ~= gb then return ga < gb end
        local ra, rb = N(SKILLS[a].rank), N(SKILLS[b].rank)
        if ra ~= rb then return ra > rb end
        local ia, ib = N(SKILLS[a].into), N(SKILLS[b].into)
        if ia ~= ib then return ia > ib end
        return a < b
      end)
      local colour = IS_REALM[realm] and REALM_COLOUR[realm] or ANY_COLOUR
      if #L > 0 then L[#L + 1] = "" end
      L[#L + 1] = col(colour .. ",bold", "-- " .. realm .. " --")
      for _, k in ipairs(names) do
        local s = SKILLS[k]
        local g = skill_group(s)
        count[g] = count[g] + 1
        -- the trained rank, and the rank the banked xp has already earned when it is ahead
        local rank = S(s.rank)
        if N(s.xp_rank) > N(s.rank) then rank = rank .. " (xp " .. S(s.xp_rank) .. ")" end
        local xp = comma(s.into) .. "/" .. comma(N(s.into) + N(s.to_next))
        local cost = S(s.train_cost) .. " " .. S(s.train_abbr)
        -- the name is the link; the padding and the numbers after it are plain text in
        -- the same colour, so only the word itself is clickable
        local rest = string.rep(" ", math.max(1, 18 - #k)) .. string.format("%-9s %9s  %s", rank, xp, cost)
        if g == 2 then
          -- a locked skill is dim in any realm
          L[#L + 1] = skill_link(k, "dim", true) .. col("dim", rest .. " locked")
        else
          L[#L + 1] = skill_link(k, colour, false) .. col(colour, rest)
            .. (g == 0 and col("success,bold", " xp ready") or "")
        end
      end
    end
  end
  scrye.setState(P .. "skills_head",
    count[0] .. " with the xp to train   " .. count[1] .. " open   " .. count[2] .. " locked"
    .. "   click: chelp   right-click: train")
  scrye.setState(P .. "skills", table.concat(L, "\n"))
end

-- ------------------------------------------------------------- Market tab
local MARKET_NAMED = { exchanges = true, material_orders = true, scroll_orders = true }
local function build_market()
  if next(MARKET) == nil then
    scrye.setState(P .. "market_head", "waiting for Craft.Market...")
    scrye.setState(P .. "market", "")
    scrye.setState(P .. "market_orders", "")
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
  -- Material and scroll orders (seen 20 Nov 2026), under the exchanges as text:
  --   { id, type = "sell", material_name, material, qty, price, player, created }
  --   { id, type = "sell", recipe_name, recipe, price, player, created }
  -- What a price is counted in, and whether it is for one or for the lot, is not in the feed.
  local L = {}
  local function orders(title, list, what)
    if type(list) ~= "table" or #list == 0 then return 0 end
    if #L > 0 then L[#L + 1] = "" end
    L[#L + 1] = "@{accent,bold}-- " .. title .. " --@{}"
    L[#L + 1] = col("dim", string.format("%3s  %-5s %-30s %7s  %-10s %s", "#", "", what, "Price", "Player", "Age"))
    for _, o in ipairs(list) do
      if type(o) == "table" then
        local item = has(o, "material_name") and (comma(o.qty) .. " " .. S(o.material_name)) or S(o.recipe_name)
        L[#L + 1] = esc(string.format("%3s  %-5s %-30s %7s  %-10s %s", S(o.id), S(o.type), item, comma(o.price),
          S(o.player), has(o, "created") and span(now() - N(o.created)) or ""))
      end
    end
    return #list
  end
  local nm = orders("Material orders", MARKET.material_orders, "Material")
  local ns = orders("Scroll orders", MARKET.scroll_orders, "Recipe")
  scrye.setState(P .. "market_orders", table.concat(L, "\n"))
  -- the other lists have only ever arrived empty: counted here, and printed
  -- whole on the Raw tab the day one of them is not
  local others = {}
  local keys = {}
  for k, v in pairs(MARKET) do if not MARKET_NAMED[k] and type(v) == "table" then keys[#keys + 1] = k end end
  table.sort(keys)
  for _, k in ipairs(keys) do others[#others + 1] = (k:gsub("_", " ")) .. " " .. #MARKET[k] end
  scrye.setState(P .. "market_head",
    #rows .. " token exchange(s)   " .. nm .. " material order(s)   " .. ns .. " scroll order(s)"
    .. (#others > 0 and ("   " .. table.concat(others, "   ")) or ""))
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

-- A recipe you know is a link (0.11.0): a click reads about it ('crecipes <name>'), and the
-- right-click MENU queues it - 'cmake <building> <name> <qty>' for 1 to 5, five being a
-- building's queue cap, plus the quantity set with 'crafthud qty <n>' when that is another.
-- Making spends materials, so like building and training it is never a bare right click.
local MAKE_QTY = nil
local function recipe_link(r, colour)
  if (r.name .. r.building):find("[,;|@{}=]") then return col(colour, r.name) end
  local item, bld = r.name:lower(), r.building:lower()
  local qtys = { 1, 2, 3, 4, 5 }
  if MAKE_QTY and MAKE_QTY > 5 then qtys[#qtys + 1] = MAKE_QTY end
  local entries = {}
  for _, q in ipairs(qtys) do entries[#entries + 1] = "Make " .. q .. "|cmake " .. bld .. " " .. item .. " " .. q end
  local click = ",click=crecipes " .. item
  -- a spec is cut off at 512 characters: drop the larger quantities before that happens
  while #entries > 1 and #colour + 6 + #table.concat(entries, ";") + #click > 500 do table.remove(entries) end
  return "@{" .. colour .. ",menu=" .. table.concat(entries, ";") .. click .. "}" .. r.name .. "@{}"
end

local function build_recipes()
  local list = recipe_buildings()
  local known = known_recipes()
  if #list == 0 and #known == 0 then
    scrye.setState(P .. "recipes_head", "waiting for Craft.Recipes...")
    scrye.setState(P .. "recipes", "")
    return
  end
  -- Grouped by realm, each in the realm's colour (0.7.0); the list arrives sorted by realm
  -- and then by name. A building you own is bold and says so.
  local total, L, last = 0, {}, nil
  -- What you know (seen 20 Nov 2026): { name, building, rank, materials = "2x Smart Alloy",
  -- time (seconds), id, skill }. Under its building, in the realm's colour.
  if #known > 0 then
    L[#L + 1] = "@{accent,bold}-- You know --@{}"
    local kb
    for _, r in ipairs(known) do
      local colour = REALM_COLOUR[r.realm] or "accent"
      if r.building ~= kb then
        kb = r.building
        L[#L + 1] = col(colour .. ",bold", r.building)
      end
      L[#L + 1] = "  " .. recipe_link(r, colour)
        .. col(colour, string.rep(" ", math.max(1, 27 - #r.name)) .. string.format("r%-3d ", r.rank))
        .. col("dim", r.materials .. (r.time > 0 and ("   " .. span(r.time)) or ""))
    end
    L[#L + 1] = ""
    L[#L + 1] = "@{accent,bold}-- The catalogue --@{}"
  end
  L[#L + 1] = col("dim", string.format("%-22s %7s  %7s", "Building", "Recipes", "Ranks"))
  for _, b in ipairs(list) do
    total = total + b.n
    local colour = REALM_COLOUR[b.realm] or "accent"
    if b.realm ~= last then
      last = b.realm
      L[#L + 1] = ""
      L[#L + 1] = col(colour .. ",bold", "-- " .. b.realm .. " --")
    end
    local built = owned(b.name)
    L[#L + 1] = col(colour .. (built and ",bold" or ""), string.format("%-22s %7d  %7s", b.name, b.n, b.lo .. "-" .. b.hi))
      .. (built and col("success,bold", " built") or "")
  end
  scrye.setState(P .. "recipes_head",
    #known .. " known   " .. comma(total) .. " recipes in " .. #list .. " buildings in the catalogue"
    .. (#known > 0 and "   click: crecipes   right-click: make" or ""))
  scrye.setState(P .. "recipes", table.concat(L, "\n"))
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
    for _, k in ipairs({ "masterwork" }) do
      if filled(JOBS[k]) then lines[#lines + 1] = k .. " " .. raw(JOBS[k]) end
    end
    section("Craft.Jobs", lines)
  end
  do
    local lines, keys = {}, {}
    for k, v in pairs(MARKET) do if not MARKET_NAMED[k] and filled(v) then keys[#keys + 1] = k end end
    table.sort(keys)
    for _, k in ipairs(keys) do lines[#lines + 1] = k .. " " .. raw(MARKET[k]) end
    section("Craft.Market", lines)
  end
  do
    local lines = {}
    for _, k in ipairs({ "items" }) do
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
  if #pending() > 0 then bits[#bits + 1] = #pending() .. " pending" end
  if N(JOBS.queue_total) > 0 then bits[#bits + 1] = comma(JOBS.queue_total) .. " queued" end
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
  market = build_market, recipes = build_recipes, raw = build_raw, materials = build_materials,
}
local ALL = { "status", "materials", "tokens", "skills", "market", "recipes", "raw" }

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

-- Craft.Info and Craft.State carry the same messages as Craft.Infodata and
-- Craft.Statedata since 25 Nov 2026 (81 of each, payload for payload). Both names are
-- read by the one handler: taking a message twice changes nothing.
for _, pkg in ipairs({ "Craft.Infodata", "Craft.Info" }) do
  feed(pkg, function(t) merge(INFO, t) end, "status")
end

local function on_state(t)
  for k, v in pairs(t) do
    if k == "full" or k == "page" or k == "pages" then
      -- paging words, not data
    elseif k:find("^tokens_") and type(v) == "table" then
      TOKENS[k] = v
    elseif k == "stock_used" or k == "stock_cap" or k == "souls" or k == "soul_cap" or k == "soul_tiers" then
      STATE[k] = v
    elseif k:find("^materials_chunks_g%d+$") then
      -- how many chunks generation N has: with them all here, it becomes the list
      local gen = N(k:match("g(%d+)$"))
      MATGEN[gen] = MATGEN[gen] or { chunks = {} }
      MATGEN[gen].count = N(v)
      commit_materials(gen)
    elseif k:find("^materials_g%d+c%d+$") and type(v) == "table" then
      local gen, i = k:match("g(%d+)c(%d+)$")
      gen, i = N(gen), N(i)
      MATGEN[gen] = MATGEN[gen] or { chunks = {} }
      MATGEN[gen].chunks[i] = v
      commit_materials(gen)
    else
      extra("Craft.Statedata", k, v)
    end
  end
end
for _, pkg in ipairs({ "Craft.Statedata", "Craft.State" }) do
  feed(pkg, on_state, "status", "tokens", "market", "raw", "materials")     -- market: a token's name comes from here
end

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
  local page = N(t.page) > 0 and N(t.page) or 1
  for k, v in pairs(t) do
    if (k == "bldgs" or k:find("^bldgs_%d+$")) and type(v) == "table" then
      -- One message held all twelve buildings on 20 Nov; the thirteenth did not show as
      -- built, so a longer list is taken to be cut the way every other list here is -
      -- into bldgs_1, bldgs_2 ... and/or pages under one key. Both are joined.
      if page == 1 or not BPARTS[k] then BPARTS[k] = {} end
      BPARTS[k][page] = v
      wipe(BLDGS); have_bldgs = true
      local keys = {}
      for key in pairs(BPARTS) do keys[#keys + 1] = key end
      table.sort(keys, function(a, b) return chunk_no(a) < chunk_no(b) end)
      for _, key in ipairs(keys) do
        local pages = {}
        for n in pairs(BPARTS[key]) do pages[#pages + 1] = n end
        table.sort(pages)
        for _, n in ipairs(pages) do
          for _, row in ipairs(BPARTS[key][n]) do
            local name, tier = S(row):match("^([^|]*)|?(.*)$")
            BLDGS[#BLDGS + 1] = { name = name, tier = (tier ~= "" and tier or "?") }
          end
        end
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
    elseif k:find("^known_") and type(v) == "table" then
      -- what the character can make, cut and (should it come to that) paged the same way
      if page == 1 or not KNOWNR[k] then KNOWNR[k] = {} end
      KNOWNR[k][page] = v
    elseif k ~= "full" and k ~= "page" and k ~= "pages" then
      extra("Craft.Recipes", k, v)
    end
  end
end, "recipes", "status")      -- status: the catalogue names the production buildings

-- a build that was on the list, is not any more, and was due: it finished.
-- (One that vanishes long before its time was cancelled, and is not announced.)
local function announce_built(new_builds)
  local still = {}
  for _, b in ipairs(new_builds) do if type(b) == "table" then still[S(b.building)] = true end end
  for _, b in ipairs(builds()) do
    if not still[S(b.building)] and N(b.due) - now() <= 60 then
      scrye.print(TAG .. col("success", S(b.building) .. " T" .. S(b.tier) .. " is built"))
    end
  end
end

-- Craft.Jobs since 25 Nov 2026: no named lists any more. Everything comes as a GENERATION
-- of numbered chunks, one key a message:
--     jobs_<gen>_<i>_<n>_<kind>      "jobs_2a_7_9_q" = generation 2a (hex), chunk 7 of 9
-- with the kind in the last letter:
--     r  ready to collect   [{name,quality,expires,obj}]
--     q  production queues  [{building,current_name,current_id,current_due,items,depth,cap,stalled}]
--     p  pending builds     [{building,tier,need,token,realm,global}]
--     b  builds under way   [{building,tier,due}]
--     l  the totals         [queued, cap]   ([15, 40] beside queue depths of 4+4+5+2)
-- A list too long for one chunk runs over several of the same kind (ten ready items came
-- as r, r, r, r), and a kind with nothing in it is simply absent - so a whole generation
-- is the whole truth, and it replaces everything once its last chunk is here. Half a
-- generation changes nothing. A kind not listed above is kept for the Raw tab.
local JOB_KIND = { r = "ready", q = "queues", p = "pending_builds", b = "builds" }
local function commit_jobs(gen)
  local g = JOBGEN[gen]
  if not g then return false end
  for i = 1, g.n do if not g.chunks[i] then return false end end
  local new = { ready = {}, queues = {}, pending_builds = {}, builds = {} }
  local odd = {}
  for i = 1, g.n do
    local c = g.chunks[i]
    if c.kind == "l" then
      new.queue_total, new.queue_cap = c.list[1], c.list[2]
    else
      local into = new[JOB_KIND[c.kind] or ""]
      if not into then odd[c.kind] = odd[c.kind] or {}; into = odd[c.kind] end
      for _, x in ipairs(c.list) do into[#into + 1] = x end
    end
  end
  announce_built(new.builds)
  wipe(JOBS); PARTS[JOBS] = nil
  for k, v in pairs(new) do JOBS[k] = v end
  EXTRA["Craft.Jobs"] = nil
  for kind, list in pairs(odd) do extra("Craft.Jobs", "kind " .. kind, list) end
  wipe(JOBGEN)       -- whatever else was half here is older
  return true
end

feed("Craft.Jobs", function(t)
  local old, changed = {}, false
  for k, v in pairs(t) do
    local gen, i, n, kind = k:match("^jobs_(%x+)_(%d+)_(%d+)_(%a+)$")
    if gen and type(v) == "table" then
      i, n = N(i), N(n)
      if i >= 1 and i <= n then
        JOBGEN[gen] = JOBGEN[gen] or { n = n, chunks = {} }
        JOBGEN[gen].chunks[i] = { kind = kind, list = v }
        if commit_jobs(gen) then changed = true end
      end
    elseif k ~= "full" then
      old[k] = v
    end
  end
  -- the named lists of before 25 Nov, should a server still send them
  local named = false
  for k in pairs(old) do if k ~= "page" and k ~= "pages" then named = true end end
  if named then
    if type(old.builds) == "table" then announce_built(old.builds) end
    merge_paged(JOBS, old)
    changed = true
  end
  return changed
end, "status", "raw")

feed("Craft.Market", function(t) merge_paged(MARKET, t) end, "market", "raw")

feed("Craft.Event", function(t)
  -- {id} every two seconds: remembered, and not worth a redraw. The three lists
  -- are kept only when they carry something, so the last real event stays readable.
  EVENT.id = t.id
  local news = false
  -- something came off a production queue: { building, name, quality, quality_name }.
  -- Said once, as it happens; the snapshot at login ("full") is not news.
  if type(t.finished) == "table" then
    for _, f in ipairs(t.finished) do
      if type(f) == "table" then
        if has(f, "quality") and S(f.quality_name) ~= "" then QNAME[N(f.quality)] = S(f.quality_name) end
        if not has(t, "full") then
          scrye.print(TAG .. col("success", S(f.building) .. " finished " .. S(f.name)
            .. (S(f.quality_name) ~= "" and (" (" .. S(f.quality_name) .. ")") or "")))
        end
      end
    end
  end
  for _, k in ipairs({ "finished", "items", "materials" }) do
    if filled(t[k]) then EVENT[k] = t[k]; news = true end
  end
  for k, v in pairs(t) do
    if k ~= "id" and k ~= "full" and k ~= "finished" and k ~= "items" and k ~= "materials" then
      extra("Craft.Event", k, v); news = true
    end
  end
  return news
end, "raw", "status", "materials")

-- offered by the server, never yet sent: whatever they turn out to carry is shown
for _, pkg in ipairs({ "Craft.Extra" }) do
  feed(pkg, function(t)
    for k, v in pairs(t) do extra(pkg, k, v) end
  end, "raw")
end

-- Craft.Effects (first seen 25 Nov 2026): { active = [], cooldowns = [] }, both empty so
-- far. A list is shown whole on the Raw tab the day it holds something, and not before.
feed("Craft.Effects", function(t)
  for k, v in pairs(t) do
    if k ~= "full" then
      EXTRA["Craft.Effects"] = EXTRA["Craft.Effects"] or {}
      EXTRA["Craft.Effects"][k] = filled(v) and v or nil
      if next(EXTRA["Craft.Effects"]) == nil then EXTRA["Craft.Effects"] = nil end
    end
  end
end, "raw")

-- A new connection may be another character: everything starts over.
scrye.onConnect(function()
  wipe(INFO); wipe(STATE); wipe(JOBS); wipe(MARKET); wipe(SKILLS); wipe(TOKENS)
  wipe(BLDGS); wipe(REFS); wipe(CAT); wipe(EVENT); wipe(EXTRA)
  wipe(BPARTS); wipe(PARTS); wipe(JOBGEN); wipe(MATGEN); wipe(KNOWNR); wipe(QNAME); MATS = nil
  have_bldgs = false; seen_any = false; CATB = nil
  for _, s in ipairs(ALL) do dirty[s] = true end
  flush()
end)

-- The countdown: while something is under construction the Status page and the
-- one-liner are redrawn each second, and not at all otherwise.
scrye.every(1, function()
  if #builds() == 0 and #queues() == 0 then return end
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

local function set_qty(n)
  n = tonumber(n)
  if not n then
    scrye.print(TAG .. esc("a recipe's menu makes 1 to 5" .. (MAKE_QTY and MAKE_QTY > 5 and (", or " .. MAKE_QTY) or "")
      .. " - 'crafthud qty <n>' adds another quantity to it"))
    return
  end
  MAKE_QTY = (n > 5) and math.floor(n) or nil
  scrye.print(TAG .. esc(MAKE_QTY and ("a recipe's menu now also makes " .. MAKE_QTY) or "a recipe's menu makes 1 to 5"))
  dirty.recipes = true; schedule_flush()
end
scrye.addAlias{ pattern = "^crafthud pct (\\d+)$", regex = true, run = function(n)
  n = math.floor(tonumber(n) or -1)
  local stock = { [0] = true, [20] = true, [40] = true, [50] = true, [60] = true, [80] = true, [100] = true }
  if n < 0 or n > 100 then scrye.print(TAG .. "a share is 0 to 100"); return end
  ALLOC_PCT = (not stock[n]) and n or nil
  scrye.print(TAG .. esc(ALLOC_PCT and ("a refinery material's menu now also offers " .. n .. "%")
    or "a refinery material's menu offers 0, 20, 40, 50, 60, 80 and 100%"))
  dirty.status = true; schedule_flush()
end }
scrye.addAlias{ pattern = "^crafthud qty$", regex = true, run = function() set_qty(nil) end }
scrye.addAlias{ pattern = "^crafthud qty (\\d+)$", regex = true, run = function(n) set_qty(n) end }
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
    { title = "Materials", widgets = {
        { type = "label", bind = P .. "materials_head", color = "dim" },
        { type = "text", bind = P .. "materials" },
    } },
    { title = "Skills", widgets = {
        { type = "label", bind = P .. "skills_head", color = "dim" },
        -- a text block, not a table: a table has one colour, and the skills are grouped
        -- and coloured by realm (0.4.0). Columns: skill, rank, xp, price.
        { type = "text", bind = P .. "skills" },
    } },
    { title = "Tokens", widgets = { { type = "text", bind = P .. "tokens" } } },
    { title = "Market", widgets = {
        { type = "label", bind = P .. "market_head", color = "dim" },
        { type = "table", bind = P .. "market",
          columns = { "#", "Player", "Gives", "Wants", "Age" }, align = "rlllr" },
        { type = "text", bind = P .. "market_orders" },
    } },
    { title = "Recipes", widgets = {
        { type = "label", bind = P .. "recipes_head", color = "dim" },
        -- a text block for the same reason as Skills: grouped and coloured by realm
        { type = "text", bind = P .. "recipes" },
    } },
    { title = "Raw", widgets = { { type = "text", bind = P .. "raw" } } },
  },
}

-- ------------------------------------------------------------------ init
for _, s in ipairs(ALL) do dirty[s] = true end
flush()
