-- Harness for 3s-craft. Every payload below is VERBATIM from the captures
-- (gmcp-fields-Keyser-20261116-175701, -20261117-141656, -20261117-151653 and
-- -20261117-165051; section 14 from -20261120-122625), except those marked SYNTHETIC: lists the server has only
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
-- upgrade menu at T5 (1), the command not lower-cased (6). 0.4.0: two realms' colours
-- swapped (3, then it stops), the skills left ungrouped (11), a locked skill left in its realm's colour (2).
-- 0.5.0: every built tier in one colour (4), the any-realm skills left uncoloured (4).
-- 0.6.0: tokens coloured by realm instead of tier (3). 0.7.0: the recipe rows in one colour (6).
-- 0.8.0: a train menu on a locked skill (2), a bare rclick= on a skill (7).
-- 0.9.0: pending builds ignored (5). 0.10.0 (20 Nov): Craft.Market merged plainly so a list's
-- last page is all of it (6), the same for Craft.Jobs (1), a half-arrived generation of
-- materials shown (3), a generation numbered lower refused (1), a finished job in the
-- login snapshot announced (1), no tick while only a queue runs (1), a page's dropped
-- list kept (1), pages past the end kept (1), a delta kept beside the pages (1), the
-- pages not forgotten on reconnect (1), known-recipe chunks overwriting (2), the
-- quality's name not learned (1), a stalled queue silent (1), materials unsorted (2),
-- the order lists left on the Raw tab (1). 0.11.0: a bare rclick= on a recipe (1), the
-- spec allowed past 512 (1), the custom quantity ignored (1), names not lower-cased (2).
-- refineries: the unbuilt listed, the allocation read from the wrong column. 0.12.0: closed
-- stages listed (2), a bare rclick= on a material (2), names not lower-cased (1), the
-- extra share ignored (1).
-- 0.13.0 (25 Nov): a generation of jobs shown before it is whole, chunks of one kind
-- overwriting each other, the old generation's lists kept, the chunks not forgotten on
-- reconnect, the stalled item not found by name, Craft.State left on the Raw tab.
-- 0.2.1: a stale
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
local SK_3  = '{ "full": 1, "skills_3": { "butchery": { "rank": 1, "train_abbr": "crk", "train_cost": 8, "trained": 1, "xp_rank": 1, "unlocked": 1, "train_realm": "Chaos", "can_train": 0, "to_next": 50, "train_tier": 1, "into": 0 }, "armstech": { "rank": 1, "train_abbr": "dnt", "train_cost": 8, "trained": 1, "xp_rank": 1, "unlocked": 0, "train_realm": "Science", "can_train": -1, "to_next": 50, "train_tier": 1, "into": 0 } } }'
local SK_17 = '{ "full": 1, "skills_17": { "quantum-smithing": { "rank": 1, "train_abbr": "dnt", "train_cost": 8, "trained": 1, "xp_rank": 1, "unlocked": 1, "train_realm": "Science", "can_train": 0, "to_next": 50, "train_tier": 1, "into": 0 }, "bladesmithing": { "rank": 1, "train_abbr": "wrn", "train_cost": 8, "trained": 1, "xp_rank": 1, "unlocked": 0, "train_realm": "Fantasy", "can_train": -1, "to_next": 50, "train_tier": 1, "into": 0 } } }'

local ST_NONE  = '{ "full": 1, "bldgs": [ ] }'
local ST_MINES = '{ "full": 1, "bldgs": [ "Strip Mines|1" ] }'
local ST_REF   = '{ "full": 1, "refineries": [ "Fleshworks|1|Flesh Pulp|0|0|0", "Fleshworks|2|Bone Lattice|0|0|0", "Fleshworks|3|Blood Resin|0|0|0", "Fleshworks|4|Mutation Spores|0|0|0" ] }'
local ST_REF14 = '{ "full": 1, "refineries_14": [ "Reactor Core|7|Hyperconductor|0|0|0", "Reactor Core|8|Flux Capacitor|0|0|0", "Reactor Core|9|Void Crystal|0|0|0", "Reactor Core|10|Singularity Shard|0|0|0" ] }'

local JOBS_FULL  = '{ "ready": [ ], "full": 1, "pending_builds": [ ], "queue_total": 0, "queues": [ ], "masterwork": [ ], "builds": [ ], "queue_cap": 40 }'
local JOBS_BUILD = '{ "builds": [ { "tier": 1, "due": 1794924593, "building": "Strip Mines" } ] }'
local JOBS_DONE  = '{ "builds": [ ] }'
-- a build asked for and not yet under way (18 Nov, 14:13): the Soul Well, pending
local JOBS_PENDING = '{ "ready": [ ], "full": 1, "pending_builds": [ { "need": 400, "building": "Soul Well", "realm": "", "global": 1, "token": "", "tier": 1 } ], "queue_total": 0, "queues": [ ], "masterwork": [ ], "builds": [ ], "queue_cap": 40 }'

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
  gmcp("Craft.Skills", SK_0); gmcp("Craft.Skills", SK_3); gmcp("Craft.Skills", SK_7_BANKED); gmcp("Craft.Skills", SK_17)
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
check(table.concat(titles, ",") == "Status,Materials,Skills,Tokens,Market,Recipes,Raw", "seven tabs, in order")
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
local T0 = "@{#5FA8F5,menu=Build Plasma Reactors|cbuild build plasma reactors confirm,click=cbuild info plasma reactors}Plasma Reactors@{}"
check(has("status", "  " .. T0 .. "      @{dim}T0@{}"),
  "buildings: one not built is listed at T0; click asks about it, the menu builds it")
check(has("status", "Soul Well@{}") and has("status", "Logistics Depot@{}"), "buildings: the two global ones are listed")
check(has("status", "@{#E0605A,menu=Build Flesh Harvesters|") and has("status", "@{#B48CF0,menu=Build Sacred Groves|"),
  "buildings: a Chaos building is red and a Fantasy one purple (0.4.0)")
check(has("status", "@{#E8C547,menu=Build Soul Well|"), "buildings: a global building is yellow (0.5.0)")
check(has("status", "@{dim,menu=Build Fleshworks|"), "buildings: one the feed could not place keeps the panel's own colour")
check(has("status", "click=cbuild info bone forge}Bone Forge@{}") and has("status", "click=cbuild info crystal refinery}Crystal Refinery@{}"),
  "buildings: the catalogue adds the production buildings and refineries")
check(has("status", "click=cbuild info fleshworks}Fleshworks@{}"), "buildings: a refinery the catalogue did not name is still listed")
do
  local s = st("status")
  local c, f, sc, g, o = s:find("@{#E0605A,bold}Chaos@{}", 1, true), s:find("@{#B48CF0,bold}Fantasy@{}", 1, true),
    s:find("@{#5FA8F5,bold}Science@{}", 1, true), s:find("@{#E8C547,bold}Global@{}", 1, true), s:find("@{dim}Other@{}", 1, true)
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
check(has("tokens", "@{#F28C28}T4 hyper-charged     121@{}"), "tokens: a held token with its count, in its tier's colour (0.6.0)")
check(has("tokens", "@{#E0605A,bold}-- Chaos --@{}") and has("tokens", "@{#B48CF0,bold}-- Fantasy --@{}") and has("tokens", "@{#5FA8F5,bold}-- Science --@{}"),
  "tokens: each realm's heading in the realm's colour")
check(has("tokens", "@{dim}T5 quantum"), "tokens: a zero is dimmed, and still listed")
check(has("summary", "121 tokens"), "summary: the token total")

-- skills: the numbered chunks joined; banked xp ahead of the trained rank
-- grouped by realm and in the realm's colour (0.4.0): Chaos red, Fantasy purple, Science blue
local sk = rows("skills")
check(#sk == 12, "skills: the four chunks are joined - eight skills under four headings (got " .. #sk .. ")")
check(sk[1] == "@{#E0605A,bold}-- Chaos --@{}" and sk[2] == "@{#E0605A,menu=Train butchery|ctrain butchery,click=chelp butchery}butchery@{}@{#E0605A}          1              0/50  8 crk@{}",
  "skills: Chaos first, its heading and its skill in red (got " .. tostring(sk[2]) .. ")")
check(sk[3] == "@{#B48CF0,bold}-- Fantasy --@{}", "skills: then Fantasy, in purple")
check(sk[4] == "@{dim,click=chelp arcana}arcana@{}@{dim}            1              0/50  8 wrn locked@{}", "skills: a locked skill is dim, whatever its realm (got " .. tostring(sk[4]) .. ")")
check(sk[6] == "@{#5FA8F5,bold}-- Science --@{}" and sk[7] == "@{#5FA8F5,menu=Train quantum-smithing|ctrain quantum-smithing,click=chelp quantum-smithing}quantum-smithing@{}@{#5FA8F5}  1              0/50  8 dnt@{}",
  "skills: then Science in blue, the open skill ahead of the locked ones (got " .. tostring(sk[7]) .. ")")
check(sk[8]:find("}armstech@{}", 1, true) ~= nil and sk[9]:find("}augmetics@{}", 1, true) ~= nil, "skills: ...which follow, by name")
check(sk[10] == "@{#E8C547,bold}-- Any realm --@{}", "skills: the skills any realm's tokens train come last, under their own heading, in yellow (0.5.0)")
check(sk[11] == "@{#E8C547,menu=Train salvaging|ctrain salvaging,click=chelp salvaging}salvaging@{}@{#E8C547}         1 (xp 4)     363/50  8 any@{}@{success,bold} xp ready@{}",
  "skills: banked xp shows rank 1 (xp 4), 363 against a threshold of 50, the price, and 'xp ready' first (got " .. tostring(sk[11]) .. ")")
check(sk[12] == "@{#E8C547,menu=Train reaving|ctrain reaving,click=chelp reaving}reaving@{}@{#E8C547}           1              0/50  8 any@{}", "skills: ...and so is each skill under it")
check(has("skills_head", "1 with the xp to train   3 open   4 locked"), "skills: the head counts each kind")
-- the name is a link (0.8.0): click asks, the right-click MENU trains - never a bare right click
check(has("skills", "@{#E8C547,menu=Train salvaging|ctrain salvaging,click=chelp salvaging}salvaging@{}"),
  "skills: a skill's name clicks to chelp and its menu trains it")
check(has("skills", "@{dim,click=chelp arcana}arcana@{}") and not has("skills", "ctrain arcana"), "skills: a locked skill can be read about, and has no train menu")
check(has("skills", "ctrain quantum-smithing,click=chelp quantum-smithing}"), "skills: a hyphenated name goes out whole")
check(not has("skills", "rclick="), "skills: training is never one bare right click away")
check(has("status", "Train        ") and has("status", "salvaging"), "status: the trainable skill is named")
check(has("summary", "1 to train"), "summary: ...and counted")

-- market: token ids become the names the token lists gave them
local mk = rows("market")
check(#mk == 1 and mk[1] == "1\ttimmy\t10 legendary\t10 apocalyptic\t3h 48m",
  "market: an exchange reads as who, gives, wants and how old (got " .. tostring(mk[1]) .. ")")
check(has("market_head", "1 token exchange(s)") and has("market_head", "0 scroll order(s)") and has("market_head", "auctions 0"), "market: the empty lists are counted")

-- recipes: THE PROPERTY - three pages under one key are all kept
local rc = rows("recipes")
-- grouped by realm, in the realm's colour (0.7.0): a column heading, then a heading and
-- a row per building for each realm
check(#rc == 5, "recipes: a column heading, two realm headings, one row per building (got " .. #rc .. ")")
check(rc[2] == "@{#E0605A,bold}-- Chaos --@{}" and rc[4] == "@{#B48CF0,bold}-- Fantasy --@{}", "recipes: the realms' headings in the realms' colours")
check(rc[3] == "@{#E0605A}Bone Forge                  12    1-100@{}", "recipes: all three pages of catalogue_chaos are counted, in red (got " .. tostring(rc[3]) .. ")")
check(rc[5] == "@{#B48CF0}Crystal Refinery            10    1-100@{}", "recipes: ...and both pages of a numbered chunk, in purple (got " .. tostring(rc[5]) .. ")")
check(has("recipes_head", "22 recipes in 2 buildings"), "recipes: the head totals them and says it is the catalogue")

-- SYNTHETIC (0.2.1): the same key comes back cut into two pages instead of three -
-- the third page of the old cut must not stay behind and be counted twice
gmcp("Craft.Recipes", (REC_C_1:gsub('"pages": 3', '"pages": 2')))
gmcp("Craft.Recipes", (REC_C_2:gsub('"pages": 3', '"pages": 2'))); fire_timers()
check(rows("recipes")[3] == "@{#E0605A}Bone Forge                  10    1-100@{}", "recipes: a key resent with fewer pages drops the stale one (got " .. tostring(rows("recipes")[3]) .. ")")
gmcp("Craft.Recipes", REC_C_1); gmcp("Craft.Recipes", REC_C_2); gmcp("Craft.Recipes", REC_C_3); fire_timers()
check(rows("recipes")[3] == "@{#E0605A}Bone Forge                  12    1-100@{}", "recipes: ...and the full cut comes back whole")

-- raw: the sixty all-zero refinery rows and the empty lists say nothing
check(not has("raw", "Fleshworks") and not has("raw", "Reactor Core"), "raw: no refinery row is shown")
check(not has("raw", "Craft.Jobs") and not has("raw", "Craft.Market"), "raw: an empty list is not shown")
check(not has("raw", "materials_chunks"), "raw: the chunk count is understood since 0.10.0, and is not on the Raw tab")
check(has("materials", "nothing in store") and has("materials_head", "0 materials, 0 in store"), "materials: a count of zero chunks is an empty store")
gmcp("Craft.Statedata", STATE_G134); fire_timers()
check(has("materials", "nothing in store") and not has("raw", "g134"), "materials: ...and stays one as the generation moves")

-- 3. a token delta is the one realm's array: the others stay
gmcp("Craft.Statedata", STATE_TOK71); fire_timers()
check(has("tokens", "@{#F28C28}T4 hyper-charged      71@{}"), "token delta: the count moves")
check(has("tokens", "-- Chaos --") and has("tokens", "T5 legendary"), "token delta: the other realms are kept")
check(has("status", "Storage      0/165"), "token delta: ...and so is page one")

gmcp("Craft.Statedata", STATE_TOK68); fire_timers()
check(has("tokens", "@{#E6E6E6}T1 dented              4@{}") and has("tokens", "@{#F28C28}T4 hyper-charged      68@{}"),
  "tokens: tier 1 is white and tier 4 orange - the buildings' tier colours")
check(has("tokens", "@{dim}T2 scorched            0@{}"), "tokens: ...and one you hold none of is dim whatever its tier")

-- 4. a build: counted down against the clock
gmcp("Craft.Jobs", JOBS_BUILD); fire_timers()
check(has("status", "Strip Mines        T1"), "build: listed under construction")
check(has("status", "  @{#5FA8F5,click=cbuild info strip mines}Strip Mines@{}          @{dim}T0@{}@{warning}   building T1, 1h 00m@{}"),
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
check(has("status", "  @{#5FA8F5,bold,menu=Upgrade Strip Mines to T2|cbuild build strip mines confirm,click=cbuild info strip mines}Strip Mines@{}          @{#E6E6E6}T1@{}")
  and has("status", "1 built"), "finish: the building is listed at T1 - bold in its realm's colour - and its menu now upgrades")
check(has("summary", "1 building") and not has("summary", "1 buildings"), "finish: the one-liner counts it")

-- a build that vanishes long before its time was cancelled: not announced
printed = {}
CLOCK = 1794930618
gmcp("Craft.Jobs", '{ "builds": [ { "tier": 1, "due": 1794934217, "building": "Plasma Reactors" } ] }'); fire_timers()
check(has("status", "Plasma Reactors    T1") and has("status", "59m 59s"), "second build: listed with its own countdown")
gmcp("Craft.Jobs", JOBS_DONE); fire_timers()
check(not said("is built"), "cancel: a build that leaves the list 59 minutes early is not announced")

-- a pending build (0.9.0): asked for, not yet under way
gmcp("Craft.Jobs", JOBS_PENDING); fire_timers()
check(has("status", "Soul Well          T1   @{warning}pending@{}@{dim}  need 400@{}"),
  "pending: listed under construction as pending, with what it needs")
check(not has("status", "@{dim}nothing@{}"), "pending: ...so 'nothing' is no longer true")
check(has("status", "Soul Well@{}            @{dim}T0@{}@{warning}   pending T1@{}@{dim}, need 400@{}"),
  "pending: marked in the building list too, still at T0")
check(has("status", "@{#E8C547,menu=Build Soul Well|cbuild build soul well confirm,click=cbuild info soul well}"),
  "pending: its menu is still there - pending is not under construction")
check(has("summary", "1 pending"), "pending: the one-liner counts it")
check(not has("raw", "pending_builds"), "pending: a list the plugin now reads is not on the Raw tab")
-- SYNTHETIC: a realm's building naming its token - the id becomes the token's name
gmcp("Craft.Jobs", '{ "pending_builds": [ { "need": 50, "building": "Biolabs", "realm": "Science", "global": 0, "token": "science_4", "tier": 1 } ] }'); fire_timers()
check(has("status", "need 50 hyper-charged"), "synthetic: a named token is shown by its name")
check(not has("status", "Soul Well          T1   @{warning}pending"), "synthetic: the pending list is replaced whole by a delta")
gmcp("Craft.Jobs", '{ "pending_builds": [ ] }'); fire_timers()
check(has("status", "@{dim}nothing@{}") and not has("summary", "pending"), "pending: gone when the list empties")

-- 6. the skill, trained
gmcp("Craft.Skills", SK_7_TRAINED); fire_timers()
sk = rows("skills")
check(sk[11] == "@{#E8C547,menu=Train salvaging|ctrain salvaging,click=chelp salvaging}salvaging@{}@{#E8C547}         4           153/200  20 any@{}", "trained: rank 4, 153 of 200, the new price, no flag (got " .. tostring(sk[11]) .. ")")
check(#sk == 12, "trained: the resent chunk replaced its two skills and added none")
check(not has("status", "Train        "), "trained: the status page stops asking")
check(has("skills_head", "0 with the xp to train   4 open"), "trained: the head follows")

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
check(has("materials_head", "last in: +2 Iron Ore") and not has("raw", "Craft.Event"), "synthetic: materials from Craft.Event are named on the Materials tab, not Raw (0.10.0)")
gmcp("Craft.Event", '{ "id": 10901, "items": [ { "name": "Thing" } ] }'); fire_timers()
check(has("raw", "-- Craft.Event --") and has("raw", "items [{name=Thing}]"), "synthetic: the Craft.Event list still unseen is shown whole")
gmcp("Craft.Jobs", '{ "masterwork": [ { "item": "Smart Alloy", "qty": 1 } ] }'); fire_timers()
check(has("raw", "masterwork [{item=Smart Alloy, qty=1}]"), "synthetic: a filled Craft.Jobs.masterwork is shown whole")
gmcp("Craft.Jobs", '{ "ready": [ { "name": "Smart Alloy" } ] }'); fire_timers()
check(has("status", "Ready        ") and has("status", "1 to collect"), "synthetic: ...and the status page says there is something to collect")
-- refineries (0.11.0): "Building|step|Material|allocated %|building tier|max tier". VERBATIM, 20 Nov.
check(not has("status", "-- Refineries --"), "refineries: no heading while none is built (every row 0|0|0)")
gmcp("Craft.Structures", '{ "full": 1, "refineries_1": [ "Alloy Works|5|Neural Matrix|0|1|2", "Alloy Works|6|Nanite Gel|0|1|2", "Alloy Works|7|Biosteel|0|1|2", "Alloy Works|8|Gene Mesh|0|1|2" ] }')
gmcp("Craft.Structures", '{ "full": 1, "refineries_2": [ "Alloy Works|9|Nanoweave|0|1|2", "Alloy Works|10|Singularity Tissue|0|1|2", "Reactor Core|1|Plasma Cell|100|1|2", "Reactor Core|2|Thermal Conduit|0|1|2" ] }'); fire_timers()
check(has("status", "-- Refineries --") and has("status", "@{accent,bold,click=crefine}Reactor Core@{}       Plasma Cell 100%@{dim}   stages 1-2 of 10@{}"),
  "refineries: a built one with what it is set to refine, and how much of it")
gmcp("Craft.Structures", ST_REF14); fire_timers()      -- Reactor Core's tiers 7-10
check(has("status", "@{accent,bold,click=crefine}Alloy Works@{}        @{dim}no split set@{}@{dim}   stages 1-2 of 10@{}"), "refineries: 0% everywhere is said plainly and is not a warning - the game still works it")
check(not has("status", "Fleshworks         ") and not has("raw", "Reactor Core|"), "refineries: one that is not built is not listed, and no row is on the Raw tab")
-- SYNTHETIC: split between two materials
gmcp("Craft.Structures", '{ "full": 1, "refineries_2": [ "Alloy Works|9|Nanoweave|0|1|2", "Alloy Works|10|Singularity Tissue|0|1|2", "Reactor Core|1|Plasma Cell|60|1|2", "Reactor Core|2|Thermal Conduit|40|1|2" ] }'); fire_timers()
check(has("status", "Reactor Core@{}       Plasma Cell 60%, Thermal Conduit 40%"), "synthetic: an allocation split in two names both")
check(has("status", "  @{dim}T2  @{}@{accent,click=crefine}Thermal Conduit@{}       @{success} 40%@{}"), "refinery link: an open stage is listed under its refinery with its share, and a click is crefine")
check(not has("status", "Ion Capacitor") and not has("status", "crefine allocate"), "refinery link: a stage past the open ones is not listed, and with the realm unknown there is no menu to get wrong")
gmcp("Craft.Extra", '{ "tick_in": 3087 }'); fire_timers()
check(has("raw", "-- Craft.Extra (not named yet) --") and has("raw", "tick_in 3087"), "synthetic: a package offered but never sent is shown if it arrives")

-- SYNTHETIC: a building at the top tier has nothing left to build
gmcp("Craft.Structures", '{ "full": 1, "bldgs": [ "Strip Mines|5" ] }'); fire_timers()
check(has("status", "@{#5FA8F5,bold,click=cbuild info strip mines}Strip Mines@{}          @{#FF5FA2,bold}T5@{}"), "synthetic: at T5 there is no upgrade menu")
gmcp("Craft.Structures", ST_MINES); fire_timers()

-- SYNTHETIC (0.5.0): every tier has a colour of its own, and none is a realm's
do
  local seen, distinct = {}, true
  for t, want in pairs({ [2] = "@{#6CCB5F}T2@{}", [3] = "@{#3FC1C9}T3@{}", [4] = "@{#F28C28}T4@{}" }) do
    gmcp("Craft.Structures", '{ "full": 1, "bldgs": [ "Strip Mines|' .. t .. '" ] }'); fire_timers()
    check(has("status", "Strip Mines@{}          " .. want), "synthetic: T" .. t .. " has its own colour")
  end
  for _, c in ipairs({ "dim", "#E6E6E6", "#6CCB5F", "#3FC1C9", "#F28C28", "#FF5FA2" }) do
    if seen[c] or c == "#E0605A" or c == "#B48CF0" or c == "#5FA8F5" or c == "#E8C547" then distinct = false end
    seen[c] = true
  end
  check(distinct, "synthetic: the six tier colours differ from each other and from the realms'")
  gmcp("Craft.Structures", ST_MINES); fire_timers()
end

-- SYNTHETIC (0.7.0): a building you own is bold on the Recipes tab and says so
gmcp("Craft.Structures", '{ "full": 1, "bldgs": [ "Bone Forge|1" ] }'); fire_timers()
check(rows("recipes")[3] == "@{#E0605A,bold}Bone Forge                  12    1-100@{}@{success,bold} built@{}",
  "synthetic: a built building's recipe row is bold and marked (got " .. tostring(rows("recipes")[3]) .. ")")
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

-- 14. 20 Nov 2026: production, what is ready, materials, orders, known recipes.
-- Every payload VERBATIM from gmcp-fields-Keyser-20261120-122625 unless marked.
for _, fn in ipairs(connect_hooks) do fn() end
fire_timers()
CLOCK = 1795166088 - 4096          -- the moment the first armour went on the queue
gmcp("Craft.Statedata", '{ "stock_used": 143, "full": 1, "stock_cap": 360, "pages": 4, "soul_tiers": [ { "count": 70, "cap": 70, "name": "Faint", "tier": 1 }, { "count": 0, "cap": 0, "name": "Bound", "tier": 2 }, { "count": 0, "cap": 0, "name": "Potent", "tier": 3 }, { "count": 0, "cap": 0, "name": "Ascendant", "tier": 4 }, { "count": 0, "cap": 0, "name": "Mythic", "tier": 5 } ], "page": 1, "soul_cap": 70, "souls": 70 }')
gmcp("Craft.Statedata", '{ "pages": 4, "tokens_science": [ { "level": 1, "realm": "Science", "qty": 0, "name": "dented" }, { "level": 2, "realm": "Science", "qty": 0, "name": "scorched" }, { "level": 3, "realm": "Science", "qty": 0, "name": "calibrated" }, { "level": 4, "realm": "Science", "qty": 48, "name": "hyper-charged" }, { "level": 5, "realm": "Science", "qty": 4, "name": "quantum" } ], "page": 4, "full": 1 }')
local MAT = {
  '{ "full": 1, "materials_g34c0": [ { "quality": "crude, poor", "realm": "Science", "qty": 2, "category": "Refined", "best": "crude", "name": "Plasma Cell" }, { "quality": "crude, poor", "realm": "Science", "qty": 8, "category": "Raw", "best": "crude", "name": "Phase Dust" } ] }',
  '{ "full": 1, "materials_g34c1": [ { "quality": "crude", "realm": "Science", "qty": 1, "category": "Raw", "best": "crude", "name": "Ion Plasma" }, { "quality": "poor", "realm": "Science", "qty": 4, "category": "Raw", "best": "poor", "name": "Stem Cultures" } ] }',
  '{ "full": 1, "materials_g34c2": [ { "quality": "crude, poor", "realm": "Science", "qty": 7, "category": "Raw", "best": "crude", "name": "Thermal Isotopes" }, { "quality": "poor", "realm": "Science", "qty": 3, "category": "Raw", "best": "poor", "name": "Synaptic Fluid" } ] }',
  '{ "full": 1, "materials_g34c3": [ { "quality": "poor", "realm": "Science", "qty": 7, "category": "Raw", "best": "poor", "name": "Organic Gel" }, { "quality": "crude, poor", "realm": "Science", "qty": 9, "category": "Raw", "best": "crude", "name": "Reactor Slag" } ] }',
  '{ "full": 1, "materials_g34c4": [ { "quality": "poor", "realm": "Science", "qty": 6, "category": "Raw", "best": "poor", "name": "Wetware Tissue" }, { "quality": "crude, poor", "realm": "Science", "qty": 6, "category": "Raw", "best": "crude", "name": "Entangled Particles" } ] }',
  '{ "full": 1, "materials_g34c5": [ { "quality": "crude, poor", "realm": "Science", "qty": 53, "category": "Raw", "best": "crude", "name": "Carbon Ore" }, { "quality": "crude, poor", "realm": "Science", "qty": 7, "category": "Raw", "best": "crude", "name": "Flux Residue" } ] }',
  '{ "full": 1, "materials_g34c6": [ { "quality": "crude, poor", "realm": "Science", "qty": 6, "category": "Raw", "best": "crude", "name": "Gravity Residue" }, { "quality": "crude, poor", "realm": "Science", "qty": 15, "category": "Raw", "best": "crude", "name": "Titanium Ore" } ] }',
  '{ "full": 1, "materials_g34c7": [ { "quality": "crude, poor", "realm": "Science", "qty": 9, "category": "Refined", "best": "crude", "name": "Smart Alloy" } ] }',
}
for i = 1, 7 do gmcp("Craft.Statedata", MAT[i]) end
fire_timers()
check(has("materials_head", "waiting for the materials") and st("materials") == "", "materials: seven of eight chunks and no count is not a list yet")
gmcp("Craft.Statedata", MAT[8]); fire_timers()
check(st("materials") == "", "materials: all the chunks, and still no count of them: not yet")
-- SYNTHETIC: the count for g34 was not among the payloads the capture kept (g96's was: 9)
gmcp("Craft.Statedata", '{ "full": 1, "materials_chunks_g34": 8 }'); fire_timers()
local mt = rows("materials")
check(has("materials_head", "15 materials, 143 in store"), "materials: fifteen of them, and their sum is the storage figure (143)")
check(mt[2] == "@{accent,bold}-- Raw --@{}" and mt[3] == "@{#5FA8F5}Carbon Ore                53  crude    crude, poor@{}",
  "materials: raw first, by name, in the realm's colour (got " .. tostring(mt[3]) .. ")")
check(mt[#mt - 2] == "@{accent,bold}-- Refined --@{}" and mt[#mt] == "@{#5FA8F5}Smart Alloy                9  crude    crude, poor@{}",
  "materials: then refined (got " .. tostring(mt[#mt]) .. ")")
check(not has("raw", "materials_"), "materials: none of it is on the Raw tab")
-- the next generation: the old list stands until the new one is whole (count first this time)
gmcp("Craft.Statedata", '{ "full": 1, "materials_chunks_g96": 2 }')      -- SYNTHETIC count of 2; the chunks are verbatim
gmcp("Craft.Statedata", '{ "full": 1, "materials_g96c7": [ { "quality": "crude, poor", "realm": "Science", "qty": 2, "category": "Raw", "best": "crude", "name": "Ion Plasma" }, { "quality": "poor", "realm": "Science", "qty": 3, "category": "Special", "best": "poor", "name": "Science Essence" } ] }'); fire_timers()
check(has("materials_head", "15 materials"), "materials: a generation half arrived does not replace the list")
gmcp("Craft.Statedata", (MAT[1]:gsub("g34c0", "g96c0"))); gmcp("Craft.Statedata", (MAT[8]:gsub("g34c7", "g96c1"))); fire_timers()
check(has("materials_head", "3 materials, 19 in store"), "materials: the new generation replaces the old whole, whichever of count and chunks came first")
-- SYNTHETIC: the counter starts over (a reboot without a reconnect) - a lower number is still believed
gmcp("Craft.Statedata", (MAT[8]:gsub("g34c7", "g2c0"))); gmcp("Craft.Statedata", '{ "full": 1, "materials_chunks_g2": 1 }'); fire_timers()
check(has("materials_head", "1 material, 9 in store"), "synthetic: a generation numbered lower than the last is still shown")

local JOBS20 = '{ "ready": [ ], "full": 1, "pending_builds": [ ], "queue_total": 0, "queues": [ ], "masterwork": [ ], "builds": [ { "tier": 1, "due": 1795161725, "building": "Isotope Separator" }, { "tier": 1, "due": 1795161713, "building": "Chem Synthlab" } ], "queue_cap": 40 }'
gmcp("Craft.Jobs", JOBS20); gmcp("Craft.Jobs", '{ "builds": [ ] }'); fire_timers()
check(not has("status", "-- Production --") and not has("status", "-- Ready to collect --"), "production: no heading while nothing is queued or ready")
gmcp("Craft.Jobs", '{ "queues": [ { "current_due": 1795166088, "current_name": "Armour@smartalloy", "building": "Powersuit Bay", "items": "Armour@smartalloy, Armour@smartalloy, Armour@smartalloy, Armour@smartalloy", "cap": 5, "stalled": 0, "current_id": "armour@smartalloy", "depth": 4 } ], "queue_total": 4 }'); fire_timers()
check(has("status", "-- Production --") and has("status", "Powersuit Bay      Armour@@smartalloy   @{warning}1h 08m@{}"),
  "production: the building, what it is making (the @ escaped) and the time left")
check(has("status", "@{dim}   4/5 queued@{}") and not has("status", "STALLED"), "production: the depth against the cap, and no stall")
check(has("status", "Queue        4/40") and has("summary", "4 queued"), "production: the totals follow on the status line and the one-liner")
check(not has("raw", "queues"), "production: the queue is not on the Raw tab any more")
CLOCK = CLOCK + 96; tick()
check(has("status", "@{warning}1h 06m@{}"), "production: the tick moves it with no build under way")
-- SYNTHETIC: stalled has only been seen 0
gmcp("Craft.Jobs", '{ "queues": [ { "current_due": 1795166088, "current_name": "Armour@smartalloy", "building": "Powersuit Bay", "items": "Armour@smartalloy", "cap": 5, "stalled": 1, "current_id": "armour@smartalloy", "depth": 1 } ] }'); fire_timers()
check(has("status", "@{warning,bold}  STALLED@{}@{warning} short of materials@{}"), "synthetic: a stalled queue says so, and why")
-- with the recipe known and the store listed, it says what the job takes and what there is
gmcp("Craft.Recipes", '{ "full": 1, "known_science": [ { "rank": 1, "skill": 0, "building": "Powersuit Bay", "materials": "2x Smart Alloy", "time": 4176, "name": "Smart Alloy Body Armour", "id": "prod-3102-armour@smartalloy" }, { "rank": 1, "skill": 0, "building": "Powersuit Bay", "materials": "2x Smart Alloy", "time": 4176, "name": "Smart Alloy Cuirass", "id": "prod-3102-breastplate@smartalloy" } ] }')
gmcp("Craft.Recipes", '{ "full": 1, "known_science_2": [ { "rank": 1, "skill": 0, "building": "Chem Synthlab", "materials": "2x Organic Gel", "time": 3636, "name": "Combat Stim", "id": "prod-3103-stim" }, { "rank": 1, "skill": 0, "building": "Chem Synthlab", "materials": "2x Organic Gel", "time": 3636, "name": "Reflex Stim", "id": "prod-3103-stim_dodge_1" } ] }')
gmcp("Craft.Statedata", (MAT[1]:gsub("g34c0", "g97c0"))); gmcp("Craft.Statedata", (MAT[8]:gsub("g34c7", "g97c1")))
gmcp("Craft.Statedata", '{ "full": 1, "materials_chunks_g97": 2 }'); fire_timers()      -- SYNTHETIC count
check(has("status", "short of materials - needs 2x Smart Alloy (have 9)@{}"), "synthetic: ...naming what the recipe takes beside what is in store")

-- finished: said once, the quality's name learned, and the item listed as ready
printed = {}
CLOCK = 1795166088
gmcp("Craft.Event", '{ "finished": [ { "building": "Powersuit Bay", "quality": 2, "quality_name": "poor", "name": "Smart Alloy Body Armour" } ], "materials": [ ], "id": 6252 }')
check(said("Powersuit Bay finished Smart Alloy Body Armour (poor)"), "finished: announced as it happens")
gmcp("Craft.Jobs", '{ "ready": [ { "expires": 1795252488, "quality": 2, "obj": "\\/players\\/skuggis\\/crafting\\/objs\\/crafted\\/gear_armour", "name": "Smart Alloy Body Armour" } ], "queue_total": 3, "queues": [ { "current_due": 1795170184, "current_name": "Armour@smartalloy", "building": "Powersuit Bay", "items": "Armour@smartalloy, Armour@smartalloy, Armour@smartalloy", "cap": 5, "stalled": 0, "current_id": "armour@smartalloy", "depth": 3 } ] }'); fire_timers()
check(has("status", "-- Ready to collect --") and has("status", "Smart Alloy Body Armour    @{dim}poor@{}@{dim}   expires in 24h 00m@{}"),
  "ready: the item, its quality by the name Craft.Event gave the number, and when it expires")
check(has("status", "1 to collect") and has("summary", "1 READY") and not has("raw", "ready"), "ready: counted, and off the Raw tab")
printed = {}
gmcp("Craft.Event", '{ "finished": [ { "building": "Powersuit Bay", "quality": 2, "quality_name": "poor", "name": "Smart Alloy Body Armour" } ], "materials": [ ], "full": 1, "id": 7000 }')   -- SYNTHETIC: "full" added
check(not said("finished"), "synthetic: a finished list in the login snapshot is not announced")
-- Jobs paged: queues on page 1, ready on page 2
gmcp("Craft.Jobs", '{ "pages": 2, "queue_total": 2, "page": 1, "queues": [ { "current_due": 1795174280, "current_name": "Armour@smartalloy", "building": "Powersuit Bay", "items": "Armour@smartalloy, Armour@smartalloy", "cap": 5, "stalled": 0, "current_id": "armour@smartalloy", "depth": 2 } ] }')
gmcp("Craft.Jobs", '{ "pages": 2, "ready": [ { "expires": 1795252488, "quality": 2, "obj": "x", "name": "Smart Alloy Body Armour" }, { "expires": 1795256584, "quality": 2, "obj": "x", "name": "Smart Alloy Body Armour" } ], "page": 2 }'); fire_timers()
check(has("status", "2 to collect") and has("status", "2/5 queued"), "paged jobs: page 2 does not cost page 1 its queue")
-- SYNTHETIC: a ready list long enough to run over two pages is both pages
gmcp("Craft.Jobs", '{ "pages": 3, "ready": [ { "expires": 1795260000, "quality": 2, "obj": "x", "name": "Smart Alloy Boots" } ], "page": 3 }'); fire_timers()
check(has("status", "3 to collect") and has("status", "Smart Alloy Boots"), "synthetic: a list that runs over two pages of Craft.Jobs is all of it")
-- the 20 Nov pending build names its token by NAME, and its realm is the number 0
gmcp("Craft.Jobs", '{ "pending_builds": [ { "need": 400, "building": "Cybernetics Lab", "realm": 0, "global": 0, "token": "dented", "tier": 1 } ] }'); fire_timers()
check(has("status", "Cybernetics Lab    T1   @{warning}pending@{}@{dim}  need 400 dented@{}"), "pending: a token given by name is shown as it is")

-- the market: one list over two pages
gmcp("Craft.Market", '{ "full": 1, "pages": 4, "auctions": [ ], "exchanges": [ { "give_qty": 10, "want_token": "chaos_5", "want_qty": 10, "give_token": "fantasy_5", "created": 1794907287, "player": "timmy", "id": 1 }, { "give_qty": 100, "want_token": "chaos_5", "want_qty": 100, "give_token": "fantasy_5", "created": 1795122359, "player": "timmy", "id": 2 } ], "page": 1 }')
local MK2 = '{ "pages": 4, "page": 2, "material_orders": [ { "type": "sell", "material": 2206, "price": 50, "material_name": "Phoenix Bloom", "qty": 10, "id": 1, "player": "din", "created": 1795141687 }, { "type": "sell", "material": 2203, "price": 50, "material_name": "Frost Petals", "qty": 41, "id": 2, "player": "din", "created": 1795141743 }, { "type": "sell", "material": 2205, "price": 50, "material_name": "Ember Root", "qty": 31, "id": 3, "player": "din", "created": 1795141873 } ], "full": 1 }'
local MK3 = '{ "pages": 4, "page": 3, "material_orders": [ { "type": "sell", "material": 2206, "price": 50, "material_name": "Phoenix Bloom", "qty": 38, "id": 4, "player": "din", "created": 1795141881 }, { "type": "sell", "material": 2303, "price": 50, "material_name": "Arcane Dust", "qty": 45, "id": 5, "player": "din", "created": 1795141887 } ], "full": 1 }'
local MK4 = '{ "scroll_orders": [ { "type": "sell", "recipe": "prod-1103-venom_sunder_1", "recipe_name": "Venom Still - Flaking Venom", "price": 100, "player": "zev", "id": 1, "created": 1795139835 } ], "scroll_outbox": [ ], "full": 1, "scroll_auctions": [ ], "pages": 4, "orders": [ ], "page": 4 }'
gmcp("Craft.Market", MK2); gmcp("Craft.Market", MK3); gmcp("Craft.Market", MK4); fire_timers()
check(#rows("market") == 2, "market: both exchanges")
check(has("market_head", "2 token exchange(s)   5 material order(s)   1 scroll order(s)"), "market: material orders on pages 2 AND 3 are all five, not the last page's two")
local mo = rows("market_orders")
check(mo[1] == "@{accent,bold}-- Material orders --@{}" and mo[3] == "  1  sell  10 Phoenix Bloom                    50  din        6h 46m",
  "market: a material order - what, how many, the price, who, how old (got " .. tostring(mo[3]) .. ")")
check(mo[7] == "  5  sell  45 Arcane Dust                      50  din        6h 43m", "market: ...the fifth, from page 3 (got " .. tostring(mo[7]) .. ")")
check(mo[8] == "@{accent,bold}-- Scroll orders --@{}" and mo[10] == "  1  sell  Venom Still - Flaking Venom        100  zev        7h 17m",
  "market: a scroll order by its recipe's name (got " .. tostring(mo[10]) .. ")")
check(not has("raw", "material_orders") and not has("raw", "scroll_orders"), "market: neither list is on the Raw tab")
gmcp("Craft.Market", MK2); gmcp("Craft.Market", MK3); gmcp("Craft.Market", MK4); fire_timers()
check(has("market_head", "5 material order(s)"), "market: the same pages again do not double the list")
-- a delta has no page and is the whole list
gmcp("Craft.Market", '{ "exchanges": [ { "give_qty": 100, "want_token": "chaos_5", "want_qty": 100, "give_token": "fantasy_5", "created": 1795122359, "player": "timmy", "id": 2 } ] }'); fire_timers()
check(#rows("market") == 1 and has("market_head", "5 material order(s)"), "market: an unpaged delta replaces its own list and no other")
-- SYNTHETIC: an order sells, the scroll orders move up to page 3, and there are three pages
gmcp("Craft.Market", MK2); gmcp("Craft.Market", (MK4:gsub('"pages": 4', '"pages": 3'):gsub('"page": 4', '"page": 3'))); fire_timers()
check(has("market_head", "3 material order(s)   1 scroll order(s)"), "synthetic: a page that comes back without a list drops that part of it, and nothing is doubled")

-- known recipes
gmcp("Craft.Recipes", REC_C_1); gmcp("Craft.Recipes", REC_C_2); gmcp("Craft.Recipes", REC_C_3)
gmcp("Craft.Recipes", '{ "full": 1, "known_science": [ { "rank": 1, "skill": 0, "building": "Powersuit Bay", "materials": "2x Smart Alloy", "time": 4176, "name": "Smart Alloy Body Armour", "id": "prod-3102-armour@smartalloy" }, { "rank": 1, "skill": 0, "building": "Powersuit Bay", "materials": "2x Smart Alloy", "time": 4176, "name": "Smart Alloy Cuirass", "id": "prod-3102-breastplate@smartalloy" } ] }')
gmcp("Craft.Recipes", '{ "full": 1, "known_science_2": [ { "rank": 1, "skill": 0, "building": "Chem Synthlab", "materials": "2x Organic Gel", "time": 3636, "name": "Combat Stim", "id": "prod-3103-stim" }, { "rank": 1, "skill": 0, "building": "Chem Synthlab", "materials": "2x Organic Gel", "time": 3636, "name": "Reflex Stim", "id": "prod-3103-stim_dodge_1" } ] }'); fire_timers()
local kr = rows("recipes")
check(kr[1] == "@{accent,bold}-- You know --@{}" and kr[2] == "@{#5FA8F5,bold}Chem Synthlab@{}", "known: a heading of their own, then the building in its realm's colour (got " .. tostring(kr[2]) .. ")")
local STIM = "cmake chem synthlab combat stim "
check(kr[3] == "  @{#5FA8F5,menu=Make 1|" .. STIM .. "1;Make 2|" .. STIM .. "2;Make 3|" .. STIM .. "3;Make 4|" .. STIM .. "4;Make 5|" .. STIM .. "5,click=crecipes combat stim}Combat Stim@{}@{#5FA8F5}                r1   @{}@{dim}2x Organic Gel   1h 00m@{}", "known: the recipe, its rank, what it takes and how long (got " .. tostring(kr[3]) .. ")")
check(kr[5] == "@{#5FA8F5,bold}Powersuit Bay@{}" and kr[6]:find("}Smart Alloy Body Armour@{}@{#5FA8F5}    r1   @{}@{dim}2x Smart Alloy   1h 09m", 1, true) ~= nil, "known: both chunks are joined")
-- 0.11.0: a known recipe is a link - a click reads, the right-click menu makes
check(has("recipes", "click=crecipes smart alloy body armour}Smart Alloy Body Armour@{}") and has("recipes", "Make 1|cmake powersuit bay smart alloy body armour 1;"),
  "recipe link: the building and the item go out lower-cased, the quantity last")
check(not has("recipes", "rclick=") and not has("recipes", "Make 6"), "recipe link: making is never a bare right click, and the menu stops at five")
printed = {}
alias("^crafthud qty (\\d+)$", "12"); fire_timers()
check(has("recipes", ";Make 5|" .. STIM .. "5;Make 12|" .. STIM .. "12,click=") and said("also makes 12"), "crafthud qty 12: a sixth entry on every recipe's menu")
alias("^crafthud qty (\\d+)$", "3"); fire_timers()
check(not has("recipes", "Make 12") and has("recipes", "Make 5|"), "crafthud qty 3: one the menu already has adds nothing, and takes the 12 away")
do
  local longest = 0
  for spec in st("recipes"):gmatch("@(%b{})") do if #spec - 2 > longest then longest = #spec - 2 end end
  check(longest > 100 and longest <= 512, "recipe link: no spec runs past the 512 the parser reads (longest " .. longest .. ")")
end
-- SYNTHETIC: a name long enough that five entries would not fit loses the larger ones, never the link
gmcp("Craft.Recipes", '{ "full": 1, "known_science_9": [ { "rank": 1, "building": "Weapons Fabricator", "materials": "", "time": 0, "name": "' .. string.rep("Very Long Name ", 5) .. 'Rifle", "id": "x-1" }, { "rank": 2, "building": "Weapons Fabricator", "materials": "", "time": 0, "name": "Odd;Name", "id": "x-2" } ] }'); fire_timers()
do
  local longest, n = 0, 0
  for spec in st("recipes"):gmatch("@(%b{})") do if #spec - 2 > longest then longest = #spec - 2 end end
  for _ in st("recipes"):gmatch("Make %d|cmake weapons fabricator very long") do n = n + 1 end
  check(longest <= 512 and n >= 1 and n < 5, "synthetic: a very long name keeps its link and drops quantities to fit (" .. n .. " left, longest " .. longest .. ")")
  check(has("recipes", "@{#5FA8F5}Odd;Name@{}") and not has("recipes", "cmake weapons fabricator odd"), "synthetic: a name that would break the menu grammar is plain text")
end
gmcp("Craft.Recipes", '{ "full": 1, "known_science_9": [ ] }'); fire_timers()
check(has("recipes_head", "4 known   12 recipes in 1 buildings in the catalogue"), "known: the head counts them apart from the catalogue")
check(has("recipes", "-- The catalogue --") and not has("raw", "known_"), "known: the catalogue follows under its own heading, and nothing is on the Raw tab")

-- refinery links (0.12.0): the realm for the command comes from the store's materials (rows VERBATIM)
gmcp("Craft.Structures", '{ "full": 1, "refineries": [ "Alloy Works|1|Smart Alloy|0|1|2", "Alloy Works|2|Carbon Mesh|0|1|2", "Alloy Works|3|Circuit Fiber|0|1|2", "Alloy Works|4|Synth Tissue|0|1|2" ] }'); fire_timers()
local AL = "crefine allocate science carbon mesh "
check(has("status", "  @{dim}T2  @{}@{#5FA8F5,menu=0%|" .. AL .. "0;20%|" .. AL .. "20;40%|" .. AL .. "40;50%|" .. AL .. "50;60%|" .. AL .. "60;80%|" .. AL .. "80;100%|" .. AL .. "100,click=crefine}Carbon Mesh@{}           @{dim}  0%@{}"),
  "refinery link: a material's menu allocates - realm and material lower-cased, the share a bare number (the % is only on the label) - and its click is crefine")
check(has("status", "@{#5FA8F5,bold,click=crefine}Alloy Works@{}") and not has("status", "Circuit Fiber"), "refinery link: the refinery itself is a plain crefine link in its realm's colour; stage 3 is not open")
check(not has("status", "rclick="), "refinery link: allocating is never a bare right click")
alias("^crafthud pct (\\d+)$", "35"); fire_timers()
check(has("status", ";100%|" .. AL .. "100;35%|" .. AL .. "35,click=crefine}"), "crafthud pct 35: one more share on the menu")
alias("^crafthud pct (\\d+)$", "50"); fire_timers()
check(not has("status", "35%|"), "crafthud pct 50: a share the menu already has takes the extra one away")
do
  local longest = 0
  for spec in st("status"):gmatch("@(%b{})") do if #spec - 2 > longest then longest = #spec - 2 end end
  check(longest <= 512, "refinery link: no spec on the status page runs past 512 (longest " .. longest .. ")")
end

-- SYNTHETIC: the building list cut in two, by a numbered key and by a page (the first message is verbatim)
gmcp("Craft.Structures", '{ "full": 1, "bldgs": [ "Strip Mines|1", "Plasma Reactors|1", "Biolabs|1", "Quantum Extractors|1", "Weapons Fabricator|1", "Powersuit Bay|1", "Chem Synthlab|1", "Isotope Separator|1", "Alloy Works|1", "Reactor Core|1", "Logistics Depot|1", "Soul Well|1" ] }')
gmcp("Craft.Structures", '{ "full": 1, "bldgs_1": [ "Cybernetics Lab|1" ] }'); fire_timers()
check(has("status", "13 built") and has("status", "Cybernetics Lab@{}      @{#E6E6E6}T1@{}") and has("status", "Strip Mines@{}          @{#E6E6E6}T1@{}"),
  "synthetic: a building list that runs into bldgs_1 is all thirteen")
check(not has("raw", "bldgs"), "synthetic: ...and bldgs_1 is not on the Raw tab")
gmcp("Craft.Structures", '{ "full": 1, "pages": 2, "page": 1, "bldgs": [ "Strip Mines|2" ] }')
gmcp("Craft.Structures", '{ "full": 1, "pages": 2, "page": 2, "bldgs": [ "Biolabs|1" ] }'); fire_timers()
check(has("status", "3 built") and has("status", "@{#6CCB5F}T2@{}"), "synthetic: ...and one cut into pages keeps every page; page 1 starts the key over")

for _, fn in ipairs(connect_hooks) do fn() end
fire_timers()
gmcp("Craft.Structures", '{ "full": 1, "bldgs_1": [ "Cybernetics Lab|1" ] }'); fire_timers()
check(has("status", "1 built"), "reconnect: no part of the old character's building list is left to be joined on")
check(st("materials") == "" and st("market_orders") == "" and not has("status", "Production"), "reconnect: the new lists start over too")
gmcp("Craft.Market", MK3); fire_timers()
check(has("market_head", "2 material order(s)"), "reconnect: ...with no page of the old character's market left behind")

-- 15. 25 Nov 2026: Craft.Jobs as generations of numbered chunks; Craft.State and Craft.Info
-- as mirrors; Craft.Effects. Payloads VERBATIM from gmcp-fields-Keyser-20261125-162232
-- unless marked.
for _, fn in ipairs(connect_hooks) do fn() end
fire_timers()
CLOCK = 1795620120           -- 25 Nov 2026 16:22 Stockholm, when the capture was taken
local G28 = {
  [==[{ "full": 1, "jobs_28_1_9_r": [ { "expires": 1795695331, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_weapon", "name": "Plasma Cell Vibro-Knife" }, { "expires": 1795695666, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/crafted_accessory", "name": "Smart Alloy Ring of Might" }, { "expires": 1795695828, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_armour", "name": "Smart Alloy Gauntlets" } ] }]==],
  [==[{ "full": 1, "jobs_28_2_9_r": [ { "expires": 1795699178, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/crafted_accessory", "name": "Smart Alloy Ring of Might" }, { "expires": 1795699315, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_weapon", "name": "Plasma Cell Sidearm" }, { "expires": 1795699946, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_armour", "name": "Smart Alloy Cuirass" } ] }]==],
  [==[{ "full": 1, "jobs_28_3_9_r": [ { "expires": 1795702512, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/brew", "name": "Reflex Stim" }, { "expires": 1795702752, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/crafted_accessory", "name": "Smart Alloy Ring of Might" }, { "expires": 1795703361, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_weapon", "name": "Plasma Cell Sidearm" } ] }]==],
  [==[{ "full": 1, "jobs_28_4_9_r": [ { "expires": 1795704058, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_armour", "name": "Smart Alloy Cuirass" } ] }]==],
  [==[{ "full": 1, "jobs_28_5_9_p": [ { "need": 800, "building": "Quantum Extractors", "realm": 0, "global": 0, "token": "scorched", "tier": 2 } ] }]==],
  [==[{ "full": 1, "jobs_28_6_9_q": [ { "current_due": 1795620928, "current_name": "Plasma Cell Sidearm", "building": "Weapons Fabricator", "items": "Plasma Cell Sidearm, Plasma Cell Chain-Axe, Plasma Cell Chain-Axe, Plasma Cell Chain-Axe", "cap": 5, "stalled": 0, "current_id": "sidearm@plasmacell", "depth": 4 } ] }]==],
  [==[{ "full": 1, "jobs_28_7_9_q": [ { "current_due": 1795623254, "current_name": "Smart Alloy Cuirass", "building": "Powersuit Bay", "items": "Smart Alloy Cuirass, Smart Alloy Gauntlets, Smart Alloy Gauntlets, Smart Alloy Gauntlets", "cap": 5, "stalled": 0, "current_id": "breastplate@smartalloy", "depth": 4 } ] }]==],
  [==[{ "full": 1, "jobs_28_8_9_q": [ { "current_due": 1795628660, "current_name": "Reflex Stim", "building": "Chem Synthlab", "items": "Reflex Stim, Reflex Stim, Reflex Stim, Reflex Stim, Reflex Stim", "cap": 5, "stalled": 0, "current_id": "stim_dodge_1", "depth": 5 }, { "current_due": 0, "current_name": "", "building": "Cybernetics Lab", "items": "Smart Alloy Ring of Might, Smart Alloy Ring of Might", "cap": 5, "stalled": 1, "current_id": "", "depth": 2 } ] }]==],
  [==[{ "full": 1, "jobs_28_9_9_l": [ 15, 40 ] }]==],
}
local G29 = {
  [==[{ "full": 1, "jobs_29_1_9_r": [ { "expires": 1795695331, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_weapon", "name": "Plasma Cell Vibro-Knife" }, { "expires": 1795695666, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/crafted_accessory", "name": "Smart Alloy Ring of Might" }, { "expires": 1795695828, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_armour", "name": "Smart Alloy Gauntlets" } ] }]==],
  [==[{ "full": 1, "jobs_29_2_9_r": [ { "expires": 1795699178, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/crafted_accessory", "name": "Smart Alloy Ring of Might" }, { "expires": 1795699315, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_weapon", "name": "Plasma Cell Sidearm" }, { "expires": 1795699946, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_armour", "name": "Smart Alloy Cuirass" } ] }]==],
  [==[{ "full": 1, "jobs_29_3_9_r": [ { "expires": 1795702512, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/brew", "name": "Reflex Stim" }, { "expires": 1795702752, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/crafted_accessory", "name": "Smart Alloy Ring of Might" }, { "expires": 1795703361, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_weapon", "name": "Plasma Cell Sidearm" } ] }]==],
  [==[{ "full": 1, "jobs_29_7_9_q": [ { "current_due": 1795623254, "current_name": "Smart Alloy Cuirass", "building": "Powersuit Bay", "items": "Smart Alloy Cuirass, Smart Alloy Gauntlets, Smart Alloy Gauntlets, Smart Alloy Gauntlets", "cap": 5, "stalled": 0, "current_id": "breastplate@smartalloy", "depth": 4 } ] }]==],
  [==[{ "full": 1, "jobs_29_8_9_q": [ { "current_due": 1795628660, "current_name": "Reflex Stim", "building": "Chem Synthlab", "items": "Reflex Stim, Reflex Stim, Reflex Stim, Reflex Stim, Reflex Stim", "cap": 5, "stalled": 0, "current_id": "stim_dodge_1", "depth": 5 }, { "current_due": 0, "current_name": "", "building": "Cybernetics Lab", "items": "Smart Alloy Ring of Might, Smart Alloy Ring of Might", "cap": 5, "stalled": 1, "current_id": "", "depth": 2 } ] }]==],
  [==[{ "full": 1, "jobs_29_9_9_l": [ 15, 40 ] }]==],
}
local G2A = {
  [==[{ "full": 1, "jobs_2a_1_9_r": [ { "expires": 1795695331, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_weapon", "name": "Plasma Cell Vibro-Knife" }, { "expires": 1795695666, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/crafted_accessory", "name": "Smart Alloy Ring of Might" }, { "expires": 1795695828, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_armour", "name": "Smart Alloy Gauntlets" } ] }]==],
  [==[{ "full": 1, "jobs_2a_2_9_r": [ { "expires": 1795699178, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/crafted_accessory", "name": "Smart Alloy Ring of Might" }, { "expires": 1795699315, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_weapon", "name": "Plasma Cell Sidearm" }, { "expires": 1795699946, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_armour", "name": "Smart Alloy Cuirass" } ] }]==],
  [==[{ "full": 1, "jobs_2a_3_9_r": [ { "expires": 1795702512, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/brew", "name": "Reflex Stim" }, { "expires": 1795702752, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/crafted_accessory", "name": "Smart Alloy Ring of Might" }, { "expires": 1795703361, "quality": 2, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_weapon", "name": "Plasma Cell Sidearm" } ] }]==],
  [==[{ "full": 1, "jobs_2a_4_9_r": [ { "expires": 1795704058, "quality": 1, "obj": "\/players\/skuggis\/crafting\/objs\/crafted\/gear_armour", "name": "Smart Alloy Cuirass" } ] }]==],
  [==[{ "full": 1, "jobs_2a_5_9_q": [ { "current_due": 1795620928, "current_name": "Plasma Cell Sidearm", "building": "Weapons Fabricator", "items": "Plasma Cell Sidearm, Plasma Cell Chain-Axe, Plasma Cell Chain-Axe, Plasma Cell Chain-Axe", "cap": 5, "stalled": 0, "current_id": "sidearm@plasmacell", "depth": 4 } ] }]==],
  [==[{ "full": 1, "jobs_2a_6_9_q": [ { "current_due": 1795623254, "current_name": "Smart Alloy Cuirass", "building": "Powersuit Bay", "items": "Smart Alloy Cuirass, Smart Alloy Gauntlets, Smart Alloy Gauntlets, Smart Alloy Gauntlets", "cap": 5, "stalled": 0, "current_id": "breastplate@smartalloy", "depth": 4 } ] }]==],
  [==[{ "full": 1, "jobs_2a_7_9_q": [ { "current_due": 1795628660, "current_name": "Reflex Stim", "building": "Chem Synthlab", "items": "Reflex Stim, Reflex Stim, Reflex Stim, Reflex Stim, Reflex Stim", "cap": 5, "stalled": 0, "current_id": "stim_dodge_1", "depth": 5 }, { "current_due": 0, "current_name": "", "building": "Cybernetics Lab", "items": "Smart Alloy Ring of Might, Smart Alloy Ring of Might", "cap": 5, "stalled": 1, "current_id": "", "depth": 2 } ] }]==],
  [==[{ "full": 1, "jobs_2a_8_9_b": [ { "tier": 2, "due": 1795634340, "building": "Quantum Extractors" } ] }]==],
  [==[{ "full": 1, "jobs_2a_9_9_l": [ 15, 40 ] }]==],
}
for i = 1, 8 do gmcp("Craft.Jobs", G28[i]) end; fire_timers()
check(not has("status", "-- Production --") and not has("status", "to collect"), "jobs: eight chunks of nine change nothing")
gmcp("Craft.Jobs", G28[9]); fire_timers()
check(has("status", "Queue        15/40") and has("summary", "15 queued   10 READY"), "jobs: the ninth chunk makes the generation whole - the l chunk is [queued, cap]")
check(has("status", "Weapons Fabricator Plasma Cell Sidearm   @{warning}13m 28s@{}") and has("status", "Chem Synthlab      Reflex Stim   @{warning}2h 22m@{}") and has("status", "5/5 queued"),
  "jobs: the q chunks (three of them) are one list of four queues")
check(has("status", "10 to collect") and has("status", "Smart Alloy Cuirass        @{dim}q1@{}@{dim}   expires in 23h 18m@{}"), "jobs: the r chunks (four of them) are one list of ten, the tenth from the fourth chunk")
check(has("status", "Quantum Extractors T2   @{warning}pending@{}@{dim}  need 800 scorched@{}") and has("summary", "1 pending"), "jobs: the p chunk is the pending build")
check(has("status", "Cybernetics Lab    @{dim}waiting: @{}Smart Alloy Ring of Might@{dim}   2/5 queued@{}") and has("status", "@{warning,bold}  STALLED@{}@{warning} short of materials@{}"),
  "stalled, as the server sends it: no job in hand, so the first of its items is what waits")
check(not has("raw", "jobs_") and not has("raw", "Craft.Jobs"), "jobs: no chunk is on the Raw tab")
for _, m in ipairs(G29) do gmcp("Craft.Jobs", m) end; fire_timers()
check(has("summary", "1 pending") and not has("status", "3h 57m"), "jobs: six chunks of the next generation leave the last whole one standing")
for i = 9, 1, -1 do gmcp("Craft.Jobs", G2A[i]) end; fire_timers()
check(has("status", "Quantum Extractors T2   @{warning}3h 57m@{}") and has("summary", "building Quantum Extractors 3h 57m") and not has("status", "pending"),
  "jobs: a later generation, its chunks arriving backwards, replaces everything - the pending build is now a build (b chunk)")
check(has("status", "10 to collect") and has("status", "Queue        15/40"), "jobs: ...and nothing is doubled or lost")
CLOCK = CLOCK + 120; tick()
check(has("status", "Weapons Fabricator Plasma Cell Sidearm   @{warning}11m 28s@{}"), "jobs: the tick still counts the queues down")
-- SYNTHETIC: the stalled job's recipe is known, by the NAME in items (there is no current_id to go by)
gmcp("Craft.Recipes", '{ "full": 1, "known_science": [ { "rank": 1, "skill": 0, "building": "Cybernetics Lab", "materials": "2x Smart Alloy, 1x Plasma Cell", "time": 3600, "name": "Smart Alloy Ring of Might", "id": "prod-3105-ring_might@smartalloy" } ] }'); fire_timers()
check(has("status", "short of materials - needs 2x Smart Alloy, 1x Plasma Cell@{}"), "synthetic: a stalled queue with no current_id finds its recipe by the item's name")
-- SYNTHETIC: the build is gone from the next generation once its time is up - built; and a kind never seen
printed = {}
CLOCK = 1795634340
gmcp("Craft.Jobs", '{ "full": 1, "jobs_2b_1_3_q": [ ] }')
gmcp("Craft.Jobs", '{ "full": 1, "jobs_2b_3_3_l": [ 0, 40 ] }'); fire_timers()
check(not said("is built") and has("status", "10 to collect"), "synthetic: two chunks of three: nothing yet, and nothing announced")
gmcp("Craft.Jobs", '{ "full": 1, "jobs_2b_2_3_m": [ { "name": "Thing" } ] }'); fire_timers()
check(said("Quantum Extractors T2 is built"), "synthetic: a build absent from a whole generation, at its due time, is announced")
check(not has("status", "to collect") and not has("status", "-- Production --") and has("status", "Queue        0/40"), "synthetic: a kind absent from a generation is an empty list")
check(has("raw", "-- Craft.Jobs") and has("raw", "kind m [{name=Thing}]"), "synthetic: a chunk kind not seen before is shown whole on the Raw tab")
-- SYNTHETIC: the named lists of before 25 Nov are still read
gmcp("Craft.Jobs", '{ "queue_total": 1, "ready": [ { "name": "Old Style", "quality": 2, "expires": 1795700000 } ] }'); fire_timers()
check(has("status", "1 to collect") and has("status", "Old Style"), "synthetic: the old named lists still work beside the new chunks")
gmcp("Craft.Jobs", '{ "masterwork": [ { "item": "Relic" } ] }'); fire_timers()
check(has("raw", "masterwork [{item=Relic}]"), "synthetic: ...an old list with no chunk kind of its own included")
gmcp("Craft.Jobs", '{ "full": 1, "jobs_2c_1_1_l": [ 0, 40 ] }'); fire_timers()
check(not has("raw", "masterwork") and not has("raw", "kind m") and not has("status", "Old Style"), "synthetic: a whole generation leaves nothing of what came before it")

-- Craft.State and Craft.Info are Craft.Statedata and Craft.Infodata under a second name
gmcp("Craft.State", '{ "pages": 4, "page": 2, "tokens_chaos": [ { "level": 1, "realm": "Chaos", "qty": 3, "name": "cracked" }, { "level": 2, "realm": "Chaos", "qty": 2, "name": "splintered" }, { "level": 3, "realm": "Chaos", "qty": 1271, "name": "seething" }, { "level": 4, "realm": "Chaos", "qty": 60, "name": "imploded" }, { "level": 5, "realm": "Chaos", "qty": 0, "name": "apocalyptic" } ], "full": 1 }')
gmcp("Craft.State", '{ "stock_used": 335, "full": 1, "stock_cap": 600, "pages": 4, "soul_tiers": [ { "count": 100, "cap": 100, "name": "Faint", "tier": 1 }, { "count": 0, "cap": 0, "name": "Bound", "tier": 2 }, { "count": 0, "cap": 0, "name": "Potent", "tier": 3 }, { "count": 0, "cap": 0, "name": "Ascendant", "tier": 4 }, { "count": 0, "cap": 0, "name": "Mythic", "tier": 5 } ], "page": 1, "soul_cap": 100, "souls": 100 }'); fire_timers()
check(has("tokens", "seething") and has("tokens", "1,271"), "Craft.State: read as Craft.Statedata is - the tokens arrive")
check(not has("raw", "Craft.State") and not has("raw", "tokens_chaos") and not has("raw", "soul_tiers"), "Craft.State: ...and none of it lands on the Raw tab")
gmcp("Craft.Info", '{ "full": 1, "bonuses": { "speed": 6, "soul_gain": 25, "logistics": 150, "soul_cap": 50 }, "realms": { "Chaos": { "bonus": 2, "license": 0, "level": 1, "xp": 0 }, "Science": { "bonus": 2, "license": 0, "level": 1, "xp": 0 }, "Fantasy": { "bonus": 2, "license": 0, "level": 1, "xp": 0 } }, "soul": { "cap": 100, "tier": 1 }, "storage": { "cap": 600, "tier": 2 }, "standing": { "Chaos": { "drop_rank": 17, "drop_extra": 8, "drop_bump": 4 }, "Science": { "drop_rank": 15, "drop_extra": 7, "drop_bump": 3 }, "Fantasy": { "drop_rank": 10, "drop_extra": 5, "drop_bump": 2 } } }'); fire_timers()
check(not has("raw", "Craft.Info") and not has("raw", "bonuses"), "Craft.Info: read as Craft.Infodata is, and not on the Raw tab")
-- Craft.Effects: nothing while its lists are empty
gmcp("Craft.Effects", '{ "cooldowns": [ ], "full": 1, "active": [ ] }'); fire_timers()
check(not has("raw", "Craft.Effects"), "Craft.Effects: two empty lists are not shown")
gmcp("Craft.Effects", '{ "active": [ { "name": "Reflex Stim", "left": 600 } ] }'); fire_timers()      -- SYNTHETIC
check(has("raw", "Craft.Effects") and has("raw", "Reflex Stim"), "synthetic: a filled Craft.Effects list is shown whole on the Raw tab")
gmcp("Craft.Effects", '{ "active": [ ] }'); fire_timers()      -- SYNTHETIC
check(not has("raw", "Craft.Effects"), "synthetic: ...and goes again when it empties")
for i = 1, 8 do gmcp("Craft.Jobs", G28[i]) end
for _, fn in ipairs(connect_hooks) do fn() end
fire_timers()
gmcp("Craft.Jobs", G28[9]); fire_timers()
check(not has("status", "15/40"), "reconnect: no chunk of the old character's jobs is left to complete a generation")

-- 13. THE PROPERTY: the plugin itself never sends a command - the two it can cause
-- are links the player clicks
check(scrye.send == nil, "the client stub has no send, and the plugin never asked for one")

print(string.format("craft_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
