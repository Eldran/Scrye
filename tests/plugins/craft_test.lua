-- Harness for 3s-craft. Every payload below is VERBATIM from the captures
-- (gmcp-fields-Keyser-20261116-175701, -20261117-141656, -20261117-151653 and
-- -20261117-165051), except the three marked SYNTHETIC: lists the server has only
-- ever sent empty, filled in by hand to prove they reach the Raw tab when it stops.
--
-- What this file protects: that a delta merges instead of replacing (a standing
-- delta must not cost the realm levels, a token delta must not cost the other
-- realms), that the numbered chunks of a catalogue are joined and the PAGES that
-- share one key are all kept, that banked xp reads as "rank 1 (xp 4)" against the
-- real threshold, that a build counts down against the clock and is announced when
-- it finishes but not when it is cancelled, that the changing materials_chunks_g<N>
-- key does not pile up, that the two-second Craft.Event counter never causes a
-- redraw, that a reconnect starts over, that every building is listed (T0 included)
-- as a link whose click asks and whose right-click MENU builds - never a bare right
-- click, never while it is already being built - and that the plugin itself never
-- sends a command.
--
--   lua5.4 craft_test.lua      (from anywhere; tests/plugins/run.sh runs them all, as CI does)
--
-- Mutants killed (17 Nov): replace-instead-of-merge in Infodata (2 checks), pages
-- overwriting each other in Recipes (5), the countdown reading a frozen clock (5),
-- the cancelled build announced (1), every materials_chunks key kept (1), a redraw
-- on a bare Craft.Event id (2), a token delta wiping the other realms (3), the
-- skill chunks replacing instead of joining (7). 0.2.0: T0 buildings dropped (4), a
-- build menu on a building under construction (1), a bare rclick= fallback (3), an
-- upgrade menu at T5 (1), the command not lower-cased (6). 0.2.1: a stale
-- catalogue page kept when its key is resent shorter (1).

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-craft/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

-- ---- a small JSON reader (the payloads below are the plugin's real input) ----
local decode
do
  local pos, src
  local function ws() pos = src:find("[^ \t\r\n]", pos) or #src + 1 end
  local function value()
    ws()
    local c = src:sub(pos, pos)
    if c == "{" then
      local t = {} ; pos = pos + 1 ; ws()
      if src:sub(pos, pos) == "}" then pos = pos + 1 ; return t end
      while true do
        ws()
        local k = value() ; ws() ; pos = pos + 1
        t[k] = value() ; ws()
        local d = src:sub(pos, pos) ; pos = pos + 1
        if d == "}" then return t end
      end
    elseif c == "[" then
      local t = {} ; pos = pos + 1 ; ws()
      if src:sub(pos, pos) == "]" then pos = pos + 1 ; return t end
      while true do
        t[#t + 1] = value() ; ws()
        local d = src:sub(pos, pos) ; pos = pos + 1
        if d == "]" then return t end
      end
    elseif c == '"' then
      local out, i = {}, pos + 1
      while true do
        local ch = src:sub(i, i)
        if ch == "\\" then out[#out + 1] = src:sub(i + 1, i + 1) ; i = i + 2
        elseif ch == '"' then pos = i + 1 ; return table.concat(out)
        else out[#out + 1] = ch ; i = i + 1 end
      end
    else
      local lit = src:match("^%-?%d+%.?%d*", pos)
      if lit then pos = pos + #lit ; return tonumber(lit) end
      lit = src:match("^%a+", pos)
      pos = pos + #(lit or "")
      if lit == "true" then return true elseif lit == "false" then return false end
      return nil
    end
  end
  decode = function(s)
    if type(s) ~= "string" then return nil end
    src, pos = s, 1
    local ok, v = pcall(value)
    return ok and v or nil
  end
end

-- ---- the client, with a clock the test owns ----
local CLOCK = 1794920992          -- 17 Nov 2026 14:09:52 local, when the first build was started
os.time = function() return CLOCK end

local printed, gmcp_hooks, aliases, state, panel, timers, next_id, connect_hooks, tickers

local function fresh_client()
  printed, gmcp_hooks, aliases, connect_hooks, tickers = {}, {}, {}, {}, {}
  state, panel, timers, next_id = {}, nil, {}, 0
  scrye = {
    id = "3s-craft",
    print = function(x) printed[#printed + 1] = tostring(x) end,
    json = { decode = decode },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    onConnect = function(fn) connect_hooks[#connect_hooks + 1] = fn end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addPanel = function(pn) panel = pn end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    after = function(d, fn) next_id = next_id + 1; timers[next_id] = fn; return next_id end,
    every = function(d, fn) tickers[#tickers + 1] = fn; return #tickers end,
    cancel = function() end,
  }
end

local function gmcp(pkg, json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == pkg then h.fn(json) end end
end
local function pending() local n = 0; for _ in pairs(timers) do n = n + 1 end; return n end
local function fire_timers()
  for _ = 1, 4 do
    local due = {}
    for id, fn in pairs(timers) do due[#due + 1] = { id = id, fn = fn } end
    if #due == 0 then return end
    table.sort(due, function(a, b) return a.id < b.id end)
    for _, d in ipairs(due) do timers[d.id] = nil; d.fn() end
  end
end
local function tick() for _, fn in ipairs(tickers) do fn() end end
local function st(k) return tostring(state["plugin.3s-craft." .. k] or "") end
local function has(k, pat) return st(k):find(pat, 1, true) ~= nil end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end
local function rows(k)
  local out = {}
  for line in (st(k) .. "\n"):gmatch("([^\n]*)\n") do if line ~= "" then out[#out + 1] = line end end
  return out
end
local function alias(pat, ...)
  for _, a in ipairs(aliases) do if a.pattern == pat then a.run(...) end end
end

-- ---- capture payloads, verbatim ----
local INFO_FULL = '{ "full": 1, "bonuses": { "speed": 0, "soul_gain": 5, "logistics": 15, "soul_cap": 10 }, "realms": { "Chaos": { "bonus": 2, "license": 0, "level": 1, "xp": 0 }, "Science": { "bonus": 2, "license": 0, "level": 1, "xp": 0 }, "Fantasy": { "bonus": 2, "license": 0, "level": 1, "xp": 0 } }, "soul": { "cap": 0, "tier": 0 }, "storage": { "cap": 165, "tier": 0 }, "standing": { "Chaos": { "drop_rank": 1, "drop_extra": 0, "drop_bump": 0 }, "Science": { "drop_rank": 1, "drop_extra": 0, "drop_bump": 0 }, "Fantasy": { "drop_rank": 1, "drop_extra": 0, "drop_bump": 0 } } }'
local INFO_STANDING = '{ "standing": { "Chaos": { "drop_rank": 1, "drop_extra": 0, "drop_bump": 0 }, "Science": { "drop_rank": 4, "drop_extra": 2, "drop_bump": 1 }, "Fantasy": { "drop_rank": 1, "drop_extra": 0, "drop_bump": 0 } } }'

local STATE_P1 = '{ "stock_used": 0, "full": 1, "stock_cap": 165, "pages": 4, "soul_tiers": [ { "count": 0, "cap": 0, "name": "Faint", "tier": 1 }, { "count": 0, "cap": 0, "name": "Bound", "tier": 2 }, { "count": 0, "cap": 0, "name": "Potent", "tier": 3 }, { "count": 0, "cap": 0, "name": "Ascendant", "tier": 4 }, { "count": 0, "cap": 0, "name": "Mythic", "tier": 5 } ], "page": 1, "soul_cap": 0, "souls": 0 }'
local STATE_P2 = '{ "pages": 4, "page": 2, "tokens_chaos": [ { "level": 1, "realm": "Chaos", "qty": 0, "name": "cracked" }, { "level": 2, "realm": "Chaos", "qty": 0, "name": "splintered" }, { "level": 3, "realm": "Chaos", "qty": 0, "name": "seething" }, { "level": 4, "realm": "Chaos", "qty": 0, "name": "imploded" }, { "level": 5, "realm": "Chaos", "qty": 0, "name": "apocalyptic" } ], "full": 1 }'
local STATE_P3 = '{ "pages": 4, "page": 3, "tokens_fantasy": [ { "level": 1, "realm": "Fantasy", "qty": 0, "name": "worn" }, { "level": 2, "realm": "Fantasy", "qty": 0, "name": "weathered" }, { "level": 3, "realm": "Fantasy", "qty": 0, "name": "rune-marked" }, { "level": 4, "realm": "Fantasy", "qty": 0, "name": "radiant" }, { "level": 5, "realm": "Fantasy", "qty": 0, "name": "legendary" } ], "full": 1 }'
local STATE_P4 = '{ "pages": 4, "tokens_science": [ { "level": 1, "realm": "Science", "qty": 0, "name": "dented" }, { "level": 2, "realm": "Science", "qty": 0, "name": "scorched" }, { "level": 3, "realm": "Science", "qty": 0, "name": "calibrated" }, { "level": 4, "realm": "Science", "qty": 121, "name": "hyper-charged" }, { "level": 5, "realm": "Science", "qty": 0, "name": "quantum" } ], "page": 4, "full": 1 }'
local STATE_G133 = '{ "full": 1, "materials_chunks_g133": 0 }'
local STATE_G134 = '{ "full": 1, "materials_chunks_g134": 0 }'
-- the delta that followed "cbuild build strip mines": the whole Science array, nothing else
local STATE_TOK71 = '{ "tokens_science": [ { "level": 1, "realm": "Science", "qty": 0, "name": "dented" }, { "level": 2, "realm": "Science", "qty": 0, "name": "scorched" }, { "level": 3, "realm": "Science", "qty": 0, "name": "calibrated" }, { "level": 4, "realm": "Science", "qty": 71, "name": "hyper-charged" }, { "level": 5, "realm": "Science", "qty": 0, "name": "quantum" } ] }'
local STATE_TOK68 = '{ "tokens_science": [ { "level": 1, "realm": "Science", "qty": 4, "name": "dented" }, { "level": 2, "realm": "Science", "qty": 0, "name": "scorched" }, { "level": 3, "realm": "Science", "qty": 0, "name": "calibrated" }, { "level": 4, "realm": "Science", "qty": 68, "name": "hyper-charged" }, { "level": 5, "realm": "Science", "qty": 0, "name": "quantum" } ] }'

local SK_0  = '{ "full": 1, "skills_0": { "reaving": { "rank": 1, "train_abbr": "any", "train_cost": 8, "trained": 1, "xp_rank": 1, "unlocked": 1, "train_realm": "any", "can_train": 0, "to_next": 50, "train_tier": 1, "into": 0 }, "arcana": { "rank": 1, "train_abbr": "wrn", "train_cost": 8, "trained": 1, "xp_rank": 1, "unlocked": 0, "train_realm": "Fantasy", "can_train": -1, "to_next": 50, "train_tier": 1, "into": 0 } } }'
-- salvaging with the xp banked and the rank not yet paid for (17 Nov, 14:16)
local SK_7_BANKED  = '{ "full": 1, "skills_7": { "augmetics": { "rank": 1, "train_abbr": "dnt", "train_cost": 8, "trained": 1, "xp_rank": 1, "unlocked": 0, "train_realm": "Science", "can_train": -1, "to_next": 50, "train_tier": 1, "into": 0 }, "salvaging": { "rank": 1, "train_abbr": "any", "train_cost": 8, "trained": 1, "xp_rank": 4, "unlocked": 1, "train_realm": "any", "can_train": 1, "to_next": -313, "train_tier": 1, "into": 363 } } }'
-- ...and after ctrain (17 Nov, 16:48)
local SK_7_TRAINED = '{ "full": 1, "skills_7": { "augmetics": { "rank": 1, "train_abbr": "dnt", "train_cost": 8, "trained": 1, "xp_rank": 1, "unlocked": 0, "train_realm": "Science", "can_train": -1, "to_next": 50, "train_tier": 1, "into": 0 }, "salvaging": { "rank": 4, "train_abbr": "any", "train_cost": 20, "trained": 4, "xp_rank": 4, "unlocked": 1, "train_realm": "any", "can_train": 0, "to_next": 47, "train_tier": 1, "into": 153 } } }'
local SK_17 = '{ "full": 1, "skills_17": { "quantum-smithing": { "rank": 1, "train_abbr": "dnt", "train_cost": 8, "trained": 1, "xp_rank": 1, "unlocked": 1, "train_realm": "Science", "can_train": 0, "to_next": 50, "train_tier": 1, "into": 0 }, "bladesmithing": { "rank": 1, "train_abbr": "wrn", "train_cost": 8, "trained": 1, "xp_rank": 1, "unlocked": 0, "train_realm": "Fantasy", "can_train": -1, "to_next": 50, "train_tier": 1, "into": 0 } } }'

local ST_NONE  = '{ "full": 1, "bldgs": [ ] }'
local ST_MINES = '{ "full": 1, "bldgs": [ "Strip Mines|1" ] }'
local ST_REF   = '{ "full": 1, "refineries": [ "Fleshworks|1|Flesh Pulp|0|0|0", "Fleshworks|2|Bone Lattice|0|0|0", "Fleshworks|3|Blood Resin|0|0|0", "Fleshworks|4|Mutation Spores|0|0|0" ] }'
local ST_REF14 = '{ "full": 1, "refineries_14": [ "Reactor Core|7|Hyperconductor|0|0|0", "Reactor Core|8|Flux Capacitor|0|0|0", "Reactor Core|9|Void Crystal|0|0|0", "Reactor Core|10|Singularity Shard|0|0|0" ] }'

local JOBS_FULL  = '{ "ready": [ ], "full": 1, "pending_builds": [ ], "queue_total": 0, "queues": [ ], "masterwork": [ ], "builds": [ ], "queue_cap": 40 }'
local JOBS_BUILD = '{ "builds": [ { "tier": 1, "due": 1794924593, "building": "Strip Mines" } ] }'
local JOBS_DONE  = '{ "builds": [ ] }'

local MARKET_FULL = '{ "scroll_outbox": [], "scroll_orders": [], "full": 1, "scroll_auctions": [], "auctions": [], "orders": [], "exchanges": [ { "give_qty": 10, "want_token": "chaos_5", "want_qty": 10, "give_token": "fantasy_5", "created": 1794907287, "player": "timmy", "id": 1 } ], "material_orders": [] }'

-- one key, three pages: the shape that loses two thirds of a building to a plain merge
local REC_C_1 = '{ "pages": 3, "catalogue_chaos": [ { "rank": 1, "building": "Bone Forge", "tier": 1, "name": "Void Iron One-Handed Sword" }, { "rank": 12, "building": "Bone Forge", "tier": 1, "name": "Entropy Shards One-Handed Sword" }, { "rank": 23, "building": "Bone Forge", "tier": 2, "name": "Warp Glass One-Handed Sword" }, { "rank": 34, "building": "Bone Forge", "tier": 2, "name": "Rift Steel One-Handed Sword" }, { "rank": 45, "building": "Bone Forge", "tier": 3, "name": "Phantom Ore One-Handed Sword" } ], "page": 1, "full": 1 }'
local REC_C_2 = '{ "pages": 3, "catalogue_chaos": [ { "rank": 56, "building": "Bone Forge", "tier": 3, "name": "Obsidian Core One-Handed Sword" }, { "rank": 67, "building": "Bone Forge", "tier": 4, "name": "Blood Crystal One-Handed Sword" }, { "rank": 78, "building": "Bone Forge", "tier": 4, "name": "Voidstone One-Handed Sword" }, { "rank": 89, "building": "Bone Forge", "tier": 5, "name": "Hellforged Plate One-Handed Sword" }, { "rank": 100, "building": "Bone Forge", "tier": 5, "name": "Singularite One-Handed Sword" } ], "page": 2, "full": 1 }'
local REC_C_3 = '{ "pages": 3, "catalogue_chaos": [ { "rank": 8, "building": "Bone Forge", "tier": 2, "name": "Void Iron Two-Handed Sword" }, { "rank": 12, "building": "Bone Forge", "tier": 2, "name": "Entropy Shards Two-Handed Sword" } ], "page": 3, "full": 1 }'
local REC_F18_1 = '{ "pages": 2, "page": 1, "catalogue_fantasy_18": [ { "rank": 1, "building": "Crystal Refinery", "tier": 1, "name": "Mana Crystal" }, { "rank": 12, "building": "Crystal Refinery", "tier": 1, "name": "Star Silver" }, { "rank": 23, "building": "Crystal Refinery", "tier": 2, "name": "Aether Pearl" }, { "rank": 34, "building": "Crystal Refinery", "tier": 2, "name": "Runestone" }, { "rank": 45, "building": "Crystal Refinery", "tier": 3, "name": "Celestine" }, { "rank": 56, "building": "Crystal Refinery", "tier": 3, "name": "Leyline Shard" } ], "full": 1 }'
local REC_F18_2 = '{ "pages": 2, "page": 2, "catalogue_fantasy_18": [ { "rank": 67, "building": "Crystal Refinery", "tier": 4, "name": "Arcane Diamond" }, { "rank": 78, "building": "Crystal Refinery", "tier": 4, "name": "Moonstone" }, { "rank": 89, "building": "Crystal Refinery", "tier": 5, "name": "Ethereal Crystal" }, { "rank": 100, "building": "Crystal Refinery", "tier": 5, "name": "Omnystall" } ], "full": 1 }'

local EV_FULL = '{ "finished": [ ], "full": 1, "items": [ ], "id": 10751, "materials": [ ] }'
local EV_ID   = '{ "id": 10752 }'

local function login()
  gmcp("Craft.Infodata", INFO_FULL)
  gmcp("Craft.Statedata", STATE_P1); gmcp("Craft.Statedata", STATE_P2)
  gmcp("Craft.Statedata", STATE_P3); gmcp("Craft.Statedata", STATE_P4)
  gmcp("Craft.Statedata", STATE_G133)
  gmcp("Craft.Skills", SK_0); gmcp("Craft.Skills", SK_7_BANKED); gmcp("Craft.Skills", SK_17)
  gmcp("Craft.Structures", ST_NONE); gmcp("Craft.Structures", ST_REF); gmcp("Craft.Structures", ST_REF14)
  gmcp("Craft.Jobs", JOBS_FULL)
  gmcp("Craft.Market", MARKET_FULL)
  gmcp("Craft.Recipes", REC_C_1); gmcp("Craft.Recipes", REC_C_2); gmcp("Craft.Recipes", REC_C_3)
  gmcp("Craft.Recipes", REC_F18_1); gmcp("Craft.Recipes", REC_F18_2)
  fire_timers()
end

-- =====================================================================
-- 1. honest before the feed
fresh_client(); dofile(PLUGIN); fire_timers()
check(panel ~= nil and panel.title == "Crafting", "the panel is registered")
local titles = {}
for _, t in ipairs(panel.tabs) do titles[#titles + 1] = t.title end
check(table.concat(titles, ",") == "Status,Skills,Tokens,Market,Recipes,Raw", "six tabs, in order")
check(has("status", "waiting for the Craft.* feed"), "before the feed: the status page says it is waiting")
check(has("summary", "waiting"), "before the feed: so does the one-liner")
check(st("skills") == "" and has("skills_head", "waiting"), "before the feed: no skill rows are invented")

-- 2. the login burst
login()
check(has("status", "Storage      0/165"), "status: storage used against its cap")
check(has("status", "no Logistics Depot"), "status: storage tier 0 is said to be no depot")
check(has("status", "no Soul Well"), "status: a soul cap of zero is no Soul Well, not 0/0")
check(has("status", "Queue        0/40"), "status: the production queue")
check(has("status", "0 built"), "status: an empty bldgs is '0 built', not 'not listed'")

-- every building, built or not (0.2.0): the name is a link
local T0 = "@{dim,menu=Build Plasma Reactors|cbuild build plasma reactors confirm,click=cbuild info plasma reactors}Plasma Reactors@{}"
check(has("status", "  " .. T0 .. "      @{dim}T0@{}"),
  "buildings: one not built is listed at T0; click asks about it, the menu builds it")
check(has("status", "Soul Well@{}") and has("status", "Logistics Depot@{}"), "buildings: the two global ones are listed")
check(has("status", "click=cbuild info bone forge}Bone Forge@{}") and has("status", "click=cbuild info crystal refinery}Crystal Refinery@{}"),
  "buildings: the catalogue adds the production buildings and refineries")
check(has("status", "click=cbuild info fleshworks}Fleshworks@{}"), "buildings: a refinery the catalogue did not name is still listed")
do
  local s = st("status")
  local c, f, sc, g, o = s:find("@{dim}Chaos@{}", 1, true), s:find("@{dim}Fantasy@{}", 1, true),
    s:find("@{dim}Science@{}", 1, true), s:find("@{dim}Global@{}", 1, true), s:find("@{dim}Other@{}", 1, true)
  check(c and f and sc and g and o and c < f and f < sc and sc < g and g < o, "buildings: grouped Chaos, Fantasy, Science, Global, Other")
  local bone, harv = s:find("}Bone Forge@{}", 1, true), s:find("}Flesh Harvesters@{}", 1, true)
  check(bone and harv and c < harv and harv < bone and bone < f, "buildings: a realm's extractors first, then the rest, inside the realm")
  local worst = 0
  for spec in s:gmatch("@(%b{})") do if #spec - 2 > worst then worst = #spec - 2 end end
  check(worst > 0 and worst <= 512, "buildings: no markup spec is over the host's 512 (longest " .. worst .. ")")
end
check(not has("status", "rclick="), "buildings: building is never one bare right click away - a menu only")
check(st("stock") == "0" and st("stock_cap") == "165", "gauge: storage reads the merged state")
check(has("status", "Science  level 1   xp 0   bonus 2"), "status: a realm's level, xp and bonus")
check(has("status", "logistics 15   soul cap 10   soul gain 5   speed 0"), "status: bonuses, sorted, under their own names")

-- tokens: all three realms, each tier, the zeroes dimmed rather than dropped
check(has("tokens", "-- Chaos --") and has("tokens", "-- Fantasy --") and has("tokens", "-- Science --"), "tokens: three realms")
check(has("tokens", "T4 hyper-charged     121"), "tokens: a held token with its count")
check(has("tokens", "@{dim}T5 quantum"), "tokens: a zero is dimmed, and still listed")
check(has("summary", "121 tokens"), "summary: the token total")

-- skills: the numbered chunks joined; banked xp ahead of the trained rank
local sk = rows("skills")
check(#sk == 6, "skills: the three chunks are joined into six rows (got " .. #sk .. ")")
check(sk[1] == "salvaging\tany\t1 (xp 4)\t363/50\t8 any\txp ready",
  "skills: banked xp shows rank 1 (xp 4), 363 against a threshold of 50, the price, and 'xp ready' first")
check(sk[#sk]:find("\tlocked$") ~= nil, "skills: locked skills sort last")
check(has("skills_head", "1 with the xp to train   2 open   3 locked"), "skills: the head counts each kind")
check(has("status", "Train        ") and has("status", "salvaging"), "status: the trainable skill is named")
check(has("summary", "1 to train"), "summary: ...and counted")

-- market: token ids become the names the token lists gave them
local mk = rows("market")
check(#mk == 1 and mk[1] == "1\ttimmy\t10 legendary\t10 apocalyptic\t3h 48m",
  "market: an exchange reads as who, gives, wants and how old (got " .. tostring(mk[1]) .. ")")
check(has("market_head", "1 token exchange(s)") and has("market_head", "scroll orders 0"), "market: the empty lists are counted")

-- recipes: THE PROPERTY - three pages under one key are all kept
local rc = rows("recipes")
check(#rc == 2, "recipes: one row per building (got " .. #rc .. ")")
check(rc[1] == "Bone Forge\tChaos\t12\t1-100\t", "recipes: all three pages of catalogue_chaos are counted (got " .. tostring(rc[1]) .. ")")
check(rc[2] == "Crystal Refinery\tFantasy\t10\t1-100\t", "recipes: ...and both pages of a numbered chunk")
check(has("recipes_head", "22 recipes in 2 buildings"), "recipes: the head totals them and says it is the catalogue")

-- SYNTHETIC (0.2.1): the same key comes back cut into two pages instead of three -
-- the third page of the old cut must not stay behind and be counted twice
gmcp("Craft.Recipes", (REC_C_1:gsub('"pages": 3', '"pages": 2')))
gmcp("Craft.Recipes", (REC_C_2:gsub('"pages": 3', '"pages": 2'))); fire_timers()
check(rows("recipes")[1] == "Bone Forge\tChaos\t10\t1-100\t", "recipes: a key resent with fewer pages drops the stale one (got " .. tostring(rows("recipes")[1]) .. ")")
gmcp("Craft.Recipes", REC_C_1); gmcp("Craft.Recipes", REC_C_2); gmcp("Craft.Recipes", REC_C_3); fire_timers()
check(rows("recipes")[1] == "Bone Forge\tChaos\t12\t1-100\t", "recipes: ...and the full cut comes back whole")

-- raw: the sixty all-zero refinery rows and the empty lists say nothing
check(not has("raw", "Fleshworks") and not has("raw", "Reactor Core"), "raw: an all-zero refinery row is not shown")
check(not has("raw", "Craft.Jobs") and not has("raw", "Craft.Market"), "raw: an empty list is not shown")
check(has("raw", "materials_chunks_g133 0"), "raw: the unexplained Statedata key is")
gmcp("Craft.Statedata", STATE_G134); fire_timers()
check(has("raw", "materials_chunks_g134 0") and not has("raw", "g133"), "raw: its changing name does not pile up")

-- 3. a token delta is the one realm's array: the others stay
gmcp("Craft.Statedata", STATE_TOK71); fire_timers()
check(has("tokens", "T4 hyper-charged      71"), "token delta: the count moves")
check(has("tokens", "-- Chaos --") and has("tokens", "T5 legendary"), "token delta: the other realms are kept")
check(has("status", "Storage      0/165"), "token delta: ...and so is page one")

-- 4. a build: counted down against the clock
gmcp("Craft.Jobs", JOBS_BUILD); fire_timers()
check(has("status", "Strip Mines        T1"), "build: listed under construction")
check(has("status", "  @{warning,click=cbuild info strip mines}Strip Mines@{}          @{dim}T0@{}@{warning}   building T1, 1h 00m@{}"),
  "build: in the building list it is marked, counts down, and has NO build menu")
check(has("status", "1h 00m"), "build: an hour to go at the moment it was started")
check(has("status", "Queue        0/40"), "build: the delta did not cost the queue numbers")
check(has("summary", "building Strip Mines 1h 00m"), "build: the one-liner carries it")
CLOCK = CLOCK + 125; tick()
check(has("status", "57m 56s"), "build: the tick moves the countdown without a message (got " .. (st("status"):match("%d+m %d+s") or "?") .. ")")
check(has("summary", "57m 56s"), "build: ...in the one-liner too")
CLOCK = 1794924593; tick()
check(has("status", "due now"), "build: at its time it is due, not negative")

-- 5. finished, and announced once
printed = {}
CLOCK = 1794924594
gmcp("Craft.Jobs", JOBS_DONE); fire_timers()
check(said("Strip Mines T1 is built"), "finish: a build that leaves the list at its time is announced")
check(has("status", "@{dim}nothing@{}") and not has("status", "due now"), "finish: nothing is under construction")
gmcp("Craft.Structures", ST_MINES); fire_timers()
check(has("status", "  @{success,menu=Upgrade Strip Mines to T2|cbuild build strip mines confirm,click=cbuild info strip mines}Strip Mines@{}          T1")
  and has("status", "1 built"), "finish: the building is listed at T1, and its menu now upgrades")
check(has("summary", "1 building") and not has("summary", "1 buildings"), "finish: the one-liner counts it")

-- a build that vanishes long before its time was cancelled: not announced
printed = {}
CLOCK = 1794930618
gmcp("Craft.Jobs", '{ "builds": [ { "tier": 1, "due": 1794934217, "building": "Plasma Reactors" } ] }'); fire_timers()
check(has("status", "Plasma Reactors    T1") and has("status", "59m 59s"), "second build: listed with its own countdown")
gmcp("Craft.Jobs", JOBS_DONE); fire_timers()
check(not said("is built"), "cancel: a build that leaves the list 59 minutes early is not announced")

-- 6. the skill, trained
gmcp("Craft.Skills", SK_7_TRAINED); fire_timers()
sk = rows("skills")
check(sk[1] == "salvaging\tany\t4\t153/200\t20 any\t", "trained: rank 4, 153 of 200, the new price, no flag (got " .. tostring(sk[1]) .. ")")
check(#sk == 6, "trained: the resent chunk replaced its two skills and added none")
check(not has("status", "Train        "), "trained: the status page stops asking")
check(has("skills_head", "0 with the xp to train   3 open"), "trained: the head follows")

-- 7. a standing delta carries no realms: they must survive it
gmcp("Craft.Infodata", INFO_STANDING); fire_timers()
check(has("status", "drops: rank 4, extra 2, bump 1"), "standing delta: the drop numbers move")
check(has("status", "Science  level 1   xp 0   bonus 2"), "standing delta: the realm levels are kept")
check(has("status", "logistics 15"), "standing delta: ...and the bonuses")

-- 8. Craft.Event: a counter every two seconds must not redraw anything
gmcp("Craft.Event", EV_FULL)
check(pending() == 0, "event: the first one, all lists empty, schedules nothing")
gmcp("Craft.Event", EV_ID)
check(pending() == 0, "event: a bare id schedules nothing")
check(not has("raw", "Craft.Event"), "event: ...and puts nothing on the Raw tab")

-- 9. SYNTHETIC: what has only been seen empty reaches the Raw tab when it is not
gmcp("Craft.Event", '{ "id": 10900, "materials": [ { "name": "Iron Ore", "qty": 2 } ] }'); fire_timers()
check(has("raw", "-- Craft.Event --") and has("raw", "materials [{name=Iron Ore, qty=2}]"), "synthetic: a filled Craft.Event list is shown whole")
gmcp("Craft.Jobs", '{ "ready": [ { "item": "Smart Alloy", "qty": 1 } ] }'); fire_timers()
check(has("raw", "ready [{item=Smart Alloy, qty=1}]"), "synthetic: a filled Craft.Jobs.ready is shown whole")
check(has("status", "Ready        ") and has("status", "1 to collect"), "synthetic: ...and the status page says there is something to collect")
gmcp("Craft.Structures", '{ "full": 1, "refineries_10": [ "Alloy Works|1|Smart Alloy|3|0|12", "Alloy Works|2|Carbon Mesh|0|0|0" ] }'); fire_timers()
check(has("raw", "Alloy Works|1|Smart Alloy|3|0|12") and not has("raw", "Carbon Mesh"), "synthetic: a refinery row is shown once a number moves, the zero rows still are not")
gmcp("Craft.Extra", '{ "tick_in": 3087 }'); fire_timers()
check(has("raw", "-- Craft.Extra (not named yet) --") and has("raw", "tick_in 3087"), "synthetic: a package offered but never sent is shown if it arrives")

-- SYNTHETIC: a building at the top tier has nothing left to build
gmcp("Craft.Structures", '{ "full": 1, "bldgs": [ "Strip Mines|5" ] }'); fire_timers()
check(has("status", "@{success,click=cbuild info strip mines}Strip Mines@{}          T5"), "synthetic: at T5 there is no upgrade menu")
gmcp("Craft.Structures", ST_MINES); fire_timers()

-- 10. the aliases
printed = {}
alias("^crafthud$")
check(said("[craft]") and said("Storage      0/165"), "crafthud: prints the status page")
printed = {}
alias("^crafthud recipes$")
check(said("Bone Forge") and said("12 recipes, ranks 1-100") and said("Crystal Refinery"), "crafthud recipes: the buildings")
printed = {}
alias("^crafthud recipes (.+)$", "bone")
check(said("Bone Forge") and not said("Mana Crystal"), "crafthud recipes bone: only that building")
local order = {}
for _, l in ipairs(printed) do local r = l:match("  r(%d+)") ; if r then order[#order + 1] = tonumber(r) end end
local sorted = #order == 12
for i = 2, #order do if order[i] < order[i - 1] then sorted = false end end
check(sorted, "crafthud recipes bone: all twelve, lowest rank first")
printed = {}
alias("^crafthud recipes (.+)$", "nosuch")
check(said("no building matches"), "crafthud recipes nosuch: says so")
local game = { chelp = 1, cinv = 1, cscore = 1, cbuild = 1, ctrain = 1, cmake = 1, ccollect = 1, crefine = 1,
               cextract = 1, csouls = 1, cenchant = 1, cmaster = 1, ctool = 1, cdiscard = 1, cmarket = 1, crecipes = 1, csave = 1 }
local claims = false
for _, a in ipairs(aliases) do if game[(a.pattern:match("^%^(%a+)") or "")] then claims = true end end
check(not claims, "aliases: none of the game's own crafting commands is taken")

-- 11. a reconnect starts over
for _, fn in ipairs(connect_hooks) do fn() end
fire_timers()
check(has("status", "waiting for the Craft.* feed") and has("summary", "waiting"), "reconnect: the status page starts over")
check(st("skills") == "" and st("market") == "" and st("recipes") == "", "reconnect: ...and the tables")
check(st("stock") == "" and has("raw", "nothing unexplained"), "reconnect: ...and the gauge and the Raw tab")
tick()
check(has("status", "waiting"), "reconnect: no build is left counting down")

-- 12. a bad payload is ignored, not fatal
gmcp("Craft.Jobs", "not json"); fire_timers()
check(has("status", "waiting"), "garbage: ignored")

-- 13. THE PROPERTY: the plugin itself never sends a command - the two it can cause
-- are links the player clicks
check(scrye.send == nil, "the client stub has no send, and the plugin never asked for one")

print(string.format("craft_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
