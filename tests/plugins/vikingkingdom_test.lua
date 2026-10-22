-- Harness for 3s-viking-kingdom. Every Guild.Roster / Guild.Kingdom payload is
-- verbatim from the 27 Aug 2026 capture (_lab/captures/gmcp-fields-Goran-20260827.md),
-- page numbering adjusted only to make complete bursts. Guild.War's real payload
-- (the empty no-war shape) is fed verbatim too. SYNTHETIC payloads, marked at each
-- use: the active-war ledger and active campaign (the capture never saw either;
-- shapes follow the verbatim idle blocks' field names), and one extra roster
-- member (Astrid, low loyalty) whose name order deliberately disagrees with her
-- loyalty order so the sort cannot be faked alphabetically.
--
-- What this file protects: the roster grid (sort, champion mark, wounded colour,
-- gneeds header), the bond matrix name resolution, recruit needs, thrall columns,
-- the grudge board's soonest-first order + ready note (+ vgrudge print), standings/
-- vrep/diplo rendering, the dynasty pages, the campaign/war ledger, and the panel
-- vocabulary.
--
-- Equivalent mutants, documented rather than faked: gasm keeping "full"/"guild" as
-- snapshot data keys - the composers read named fields only (same note as the
-- sibling harnesses).

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-viking-kingdom/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

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

local printed, gmcp_hooks, aliases, store, state, panel, timers, next_id

local function fresh_client()
  printed = {}
  gmcp_hooks, aliases = {}, {}
  store, state, panel = {}, {}, nil
  timers, next_id = {}, 0
  scrye = {
    id = "3s-viking-kingdom",
    print = function(x) printed[#printed + 1] = tostring(x) end,
    send = function() end,
    notify = function() end,
    json = { decode = decode },
    store = {
      get = function(k) return store[k] end,
      set = function(k, v) store[k] = tostring(v) end,
      delete = function(k) store[k] = nil end,
    },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addPanel = function(pn) panel = pn end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    after = function(d, fn) next_id = next_id + 1; timers[next_id] = { at = d, fn = fn }; return next_id end,
    every = function(_, fn) return 0 end,
    cancel = function() end,
  }
end

local function gmcp(pkg, json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == pkg then h.fn(json) end end
end
local function fire_timers()
  for _ = 1, 4 do
    local due = {}
    for id, e in pairs(timers) do due[#due + 1] = { id = id, e = e } end
    if #due == 0 then return end
    table.sort(due, function(a, b) return a.id < b.id end)
    for _, d in ipairs(due) do timers[d.id] = nil; d.e.fn() end
  end
end
local function st(k) return tostring(state["plugin.3s-viking-kingdom." .. k] or "") end
local function said(pat)
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end
  return false
end

-- ---- capture payloads (verbatim; page numbering adjusted to complete bursts) ----
local ROSTER_P1 = '{ "full": 1, "pages": 6, "bonds": [ { "a": 1, "tier": 3, "ticks": 1312394, "b": 6 }, { "a": 4, "tier": 3, "ticks": 1584438, "b": 5 }, { "a": 1, "tier": 3, "ticks": 1584438, "b": 5 } ], "guild": "viking", "page": 1 }'
-- verbatim capture page 2 (bonds SPAN pages in real bursts - the concat matters)
local ROSTER_P1B = '{ "full": 1, "pages": 6, "bonds": [ { "a": 2, "tier": 3, "ticks": 1584438, "b": 5 } ], "guild": "viking", "page": 2 }'
local ROSTER_P2 = '{ "full": 1, "pages": 6, "courier_tier": 1, "gneeds": { "on_duty": 0, "hird_cap": 18, "city_pool": 6, "stationed": 41, "hird_count": 6, "garrisoned": 0, "wounded": 0, "garrison_cap": 41 }, "page": 3, "courier": [ ], "guild": "viking" }'
local ROSTER_P3 = '{ "full": 1, "pages": 6, "hird": [ { "level": 50, "age": "veteran", "atk": 8, "status": "city_pool", "champ": 0, "hired": 1781676778, "mode": "neutral", "wpn": 0, "def": 10, "arm": 0, "loyalty": 3, "name": "Thyra Ivarardottir", "id": 5 }, { "level": 50, "age": "veteran", "atk": 10, "status": "city_pool", "champ": 0, "hired": 1782582760, "mode": "neutral", "wpn": 0, "def": 10, "arm": 0, "loyalty": 4, "name": "Roar Svensson", "id": 6 }, { "level": 41, "age": "green", "atk": 6, "status": "city_pool", "champ": 0, "mode": "neutral", "def": 7, "loyalty": 2, "name": "Astrid Bui", "id": 7 } ], "guild": "viking", "page": 4 }'
local ROSTER_P4 = '{ "spy_scouts": [ ], "spy": { "sabsecs": 0, "cdsecs": 0, "tier": 2, "mode": "", "sabpct": 0, "secs": 0, "sablin": 0, "village": "" }, "full": 1, "thrall_follower": { "level": 0, "xp_cap": 0, "carry_cap": 0, "state": "none", "xp": 0, "carry_used": 0, "name": "" }, "pages": 6, "page": 5, "guild": "viking" }'
local ROSTER_P5 = '{ "train": { "secs": 0, "trained": 0, "name": "", "stat": "", "tier": 3 }, "vfind_posts": [ ], "vfind_offers": [ ], "full": 1, "varang_out": [ ], "thralls": { "mine": 4, "skald_hall": 3, "total": 30, "palisade": 5, "farm": 5, "warehouse": 5, "smithy": 4, "tannery": 4 }, "vfind_auctions": [ ], "pages": 6, "varang_in": [ ], "page": 6, "guild": "viking", "vfind_hall": { "max_finds": 3, "tier": 3 } }'
-- rneeds (verbatim page, renumbered into its own complete non-full burst)
local ROSTER_RNEEDS = '{ "pages": 1, "rneeds": [ { "stat": "charm", "trait": "iron_grip", "target": "thrall_pen" }, { "stat": "craft", "trait": "taskmaster", "target": "bakehouse" }, { "stat": "sea", "trait": "sea_dog", "target": "ship_22" }, { "stat": "land", "trait": "husbandman", "target": "sheepfold" } ], "page": 1, "guild": "viking" }'

local KINGDOM_P1 = '{ "campaign_prison": { "capacity": 2, "kin": 0, "pend_name": "", "pending": 0, "pend_cmd": 0, "pend_size": 0, "held": 0 }, "army_traits": [ ], "campaign": { "spoils_wpts": 0, "upkeep_tools": 0, "turn": 0, "town": "", "upkeep_iron": 0, "dim": 0, "works_budget": 0, "upkeep_food": 0, "mode": "", "pending": 0, "upkeep_mead": 0, "spoils_deeds": 0, "spoils_daler": 0, "march_eta": 0, "upkeep_daler": 0, "active": 0 }, "army_units": [ ], "full": 1, "pages": 6, "army": { "unit_count": 0, "conscripts": 0, "levy_rate": 0, "unit_cap": 4, "cap": 195 }, "guild": "viking", "page": 1 }'
local KINGDOM_P2 = '{ "campaign_terrain": [ ], "dynasty_heir": "", "campaign_siege": { "capacity": 6, "engines": 0 }, "dynasty_living": 0, "campaign_queue": [ ], "campaign_units": [ ], "dynasty_children": [ ], "full": 1, "campaign_prison_roster": [ ], "diplo": [ { "standing": 100, "side": "you", "name": "Skjoldung" }, { "standing": -100, "side": "foe", "name": "Ui Imair" } ], "pages": 6, "dynasty_house": "Bastarsson", "dynasty_cap": 5, "page": 2, "guild": "viking" }'
local KINGDOM_P3 = '{ "full": 1, "dynasty_schooling": [ ], "pages": 6, "dynasty_realm": "Borvegas\'s Realm", "dynasty_spouse": { "house": "Winchester", "rank": 1, "age": 26, "name": "Yrsa" }, "page": 3, "guild": "viking" }'
local KINGDOM_P4 = '{ "full": 1, "pages": 6, "guild": "viking", "grudges": [ { "town": "Hafrfjord", "secs": 62394, "lineage": 5 }, { "town": "Eiriksby", "secs": 75746, "lineage": 2 }, { "town": "Lejre", "secs": 156440, "lineage": 12 }, { "town": "Imaird", "secs": 207320, "lineage": 3 } ], "page": 4 }'
local KINGDOM_P5 = '{ "full": 1, "pages": 6, "standings": [ { "lin": 1, "score": 53, "own": 0, "label": "Allied", "name": "Lodbrok" }, { "lin": 3, "score": -100, "own": 0, "label": "Feud", "name": "Ui Imair" }, { "lin": 13, "score": 65, "own": 1, "label": "Allied", "name": "Sigurdsson" } ], "guild": "viking", "page": 5 }'
local KINGDOM_P6 = '{ "pages": 6, "vrep": [ { "rank": 4, "lin": 0, "rep": 5305, "next_at": 10000, "start_at": 5000, "name": "Midgard" }, { "rank": 4, "lin": 1, "rep": 6614, "next_at": 10000, "start_at": 5000, "name": "Lodbrok" } ], "page": 6, "guild": "viking", "full": 1 }'

-- verbatim: the one Guild.War payload the capture ever carried (no war)
local WAR_EMPTY = '{ "terrain": [], "war_points": 0, "reserve": [], "target": "", "wall_tier": 0, "works": [], "spent": 0, "turn": 0, "wall_hp": [], "full": 1, "h": 0, "dz": 0, "guild": "viking", "mode": "", "phase": "", "budget": 0, "active": 0, "units": [], "w": 0 }'
-- SYNTHETIC: an active war ledger (shape follows the empty payload's field names)
local WAR_LIVE = '{ "terrain": [], "war_points": 12, "reserve": [ { "name": "huscarls" }, { "name": "raiders" } ], "target": "Ui Imair", "wall_tier": 3, "works": [], "spent": 4, "turn": 2, "wall_hp": [], "full": 1, "h": 8, "dz": 2, "guild": "viking", "mode": "defence", "phase": "order", "budget": 20, "active": 1, "units": [], "w": 10 }'

local function feed_all()
  gmcp("Guild.Roster", ROSTER_P1); gmcp("Guild.Roster", ROSTER_P1B)
  gmcp("Guild.Roster", ROSTER_P2); gmcp("Guild.Roster", ROSTER_P3)
  gmcp("Guild.Roster", ROSTER_P4); gmcp("Guild.Roster", ROSTER_P5)
  gmcp("Guild.Roster", ROSTER_RNEEDS)
  gmcp("Guild.Kingdom", KINGDOM_P1); gmcp("Guild.Kingdom", KINGDOM_P2); gmcp("Guild.Kingdom", KINGDOM_P3)
  gmcp("Guild.Kingdom", KINGDOM_P4); gmcp("Guild.Kingdom", KINGDOM_P5); gmcp("Guild.Kingdom", KINGDOM_P6)
  gmcp("Guild.War", WAR_EMPTY)
  fire_timers()
end

-- =====================================================================
fresh_client(); dofile(PLUGIN); fire_timers()

-- honest before the feed
check(st("hird"):find("waiting for Guild.Roster", 1, true), "hird: waiting line")
check(st("grudges"):find("waiting for Guild.Kingdom", 1, true), "grudges: waiting line")

feed_all()

-- Hird tab
local hird = st("hird")
check(hird:find("Hird 6/18", 1, true) and hird:find("Garrison 0/41", 1, true)
  and hird:find("City pool 6", 1, true), "hird: gneeds header")
check(hird:find("Roar Svensson", 1, true) and hird:find("Thyra Ivarardottir", 1, true), "hird: roster rows")
-- most-loyal first: Roar (4) before Thyra (3)
check(hird:find("Roar Svensson", 1, true) < hird:find("Thyra Ivarardottir", 1, true)
  and hird:find("Thyra Ivarardottir", 1, true) < hird:find("Astrid Bui", 1, true),
  "hird: sorted most-loyal first (Astrid would lead an alphabetical sort)")
check(hird:find("10/10", 1, true) and hird:find("veteran", 1, true) and hird:find("city pool", 1, true),
  "hird: atk/def, age and status render")
-- bond matrix resolves ids to first names from the roster
check(hird:find("Bonds", 1, true) and hird:find("6 Roar", 1, true) and hird:find("T3", 1, true),
  "hird: bond matrix rows carry resolved first names")
check(hird:find("#2", 1, true), "hird: page-2 bond (id 2, not on the roster) appears as #2")

-- Recruit tab
local rec = st("recruit")
check(rec:find("Thrall Pen", 1, true) and rec:find("charm", 1, true) and rec:find("Iron Grip", 1, true),
  "recruit: needs rows (building wants stat + trait)")
check(rec:find("Ship 22", 1, true) and rec:find("sea", 1, true), "recruit: ship posts want sea dogs")
check(rec:find("Hiring hall T3", 1, true) and rec:find("finds 3", 1, true), "recruit: hall line")
check(rec:find("Spymaster", 1, true) and rec:find("idle (shadow house T2)", 1, true), "recruit: idle spymaster")

-- Thralls tab
local th = st("thralls")
check(th:find("Total 30", 1, true), "thralls: total")
check(th:find("Palisade", 1, true) and th:find("Skald Hall", 1, true), "thralls: per-building rows")
-- biggest first: a 5 before a 3
check(th:find("Warehouse", 1, true) < th:find("Mine", 1, true),
  "thralls: biggest counts first (alphabetical would put Mine before Warehouse)")
check(th:find("no thrall follower out", 1, true), "thralls: follower empty-state")

-- Grudges tab: soonest first, spans humane, ready note
local gr = st("grudges")
check(gr:find("Hafrfjord", 1, true) and gr:find("17h 19m", 1, true), "grudges: 62394s renders 17h 19m")
check(gr:find("Imaird", 1, true) and gr:find("2d 9h", 1, true), "grudges: 207320s renders 2d 9h")
check(gr:find("Hafrfjord", 1, true) < gr:find("Eiriksby", 1, true)
  and gr:find("Eiriksby", 1, true) < gr:find("Imaird", 1, true), "grudges: soonest first")
check(gr:find("towns not listed carry no grudge", 1, true), "grudges: ready note")

-- vgrudge prints the board
printed = {}
for _, a in ipairs(aliases) do if a.pattern == "^vgrudge$" then a.run() end end
check(said("Hafrfjord") and said("[kingdom]"), "vgrudge: board printed to the output window")

-- Kingdom tab
local kg = st("kingdom")
check(kg:find("Sigurdsson", 1, true) and kg:find("<- yours", 1, true), "kingdom: own lineage marked")
check(kg:find("Allied", 1, true) and kg:find("Feud", 1, true), "kingdom: standing labels")
check(kg:find("Sigurdsson", 1, true) < kg:find("Ui Imair", 1, true), "kingdom: best standing first")
check(kg:find("Midgard", 1, true) and kg:find("5305/10000", 1, true) and kg:find("  6%% to next"),
  "kingdom: vrep progress measures from start_at (5305 of 5000->10000 = 6%)")
check(kg:find("Skjoldung", 1, true) and kg:find("with you", 1, true)
  and kg:find("against you", 1, true), "kingdom: diplomacy sides")

-- Dynasty tab
local dy = st("dynasty")
check(dy:find("House Bastarsson", 1, true) and dy:find("Borvegas's Realm", 1, true), "dynasty: house + realm")
check(dy:find("Living 0 / 5", 1, true) and dy:find("no heir named", 1, true), "dynasty: living/cap + heir warning")
check(dy:find("Spouse Yrsa of house Winchester (rank 1, age 26)", 1, true), "dynasty: spouse line")
check(dy:find("Children", 1, true) and dy:find("none", 1, true), "dynasty: children empty-state")

-- War tab: no war
local war = st("war")
check(war:find("Units 0/4", 1, true) and war:find("Manpower cap 195", 1, true), "war: army line")
check(war:find("no campaign in the field", 1, true), "war: campaign empty-state")
check(war:find("no war declared", 1, true), "war: no-war state from the verbatim empty payload")

-- SYNTHETIC active campaign (field shapes per the verbatim idle campaign block)
gmcp("Guild.Kingdom", '{ "pages": 1, "page": 1, "campaign": { "spoils_wpts": 3, "upkeep_tools": 2, "turn": 4, "town": "Holmgard", "upkeep_iron": 5, "dim": 8, "works_budget": 10, "upkeep_food": 12, "mode": "siege", "pending": 0, "upkeep_mead": 6, "spoils_deeds": 1, "spoils_daler": 800, "march_eta": 3600, "upkeep_daler": 40, "active": 1 }, "guild": "viking" }')
fire_timers()
war = st("war")
check(war:find("siege against Holmgard", 1, true) and war:find("turn 4", 1, true)
  and war:find("march 1h 0m", 1, true), "war: active campaign lines")
check(war:find("spoils so far: 800d, 1 deeds, 3 war points", 1, true), "war: campaign spoils")

-- War tab: the synthetic active war (shape per the empty payload's fields)
gmcp("Guild.War", WAR_LIVE); fire_timers()
war = st("war")
check(war:find("WAR against Ui Imair", 1, true) and war:find("phase order", 1, true)
  and war:find("points 12 (spent 4)", 1, true), "war: active ledger")
check(war:find("reserve 2 companies", 1, true), "war: reserve count")
check(war:find("battle board is not built yet", 1, true), "war: board honestly deferred")

-- keep-last: an rneeds-only burst must not clobber the hird
gmcp("Guild.Roster", ROSTER_RNEEDS); fire_timers()
check(st("hird"):find("Roar Svensson", 1, true), "keep-last: rneeds refresh keeps the roster")

-- full-replace: a full Roster burst without bonds drops the bond matrix
gmcp("Guild.Roster", '{ "full": 1, "pages": 1, "page": 1, "hird": [ { "level": 50, "age": "veteran", "atk": 8, "status": "wounded", "champ": 1, "mode": "neutral", "def": 10, "loyalty": 2, "name": "Thyra Ivarardottir", "id": 5 } ], "guild": "viking" }')
fire_timers()
hird = st("hird")
check(not hird:find("Bonds", 1, true), "full-replace: bonds gone from a full burst without them")
check(hird:find("@{warning,bold}*@{}", 1, true), "hird: champion marked")
check(hird:find("@{error}wounded", 1, true) or hird:find('col%("error"') or hird:find("error", 1, true),
  "hird: wounded status coloured")

-- =====================================================================
-- panel vocabulary
local VALID = { value = true, label = true, text = true, buttonrow = true, input = true,
                button = true, colorgrid = true, barlist = true, row = true }
local widgets_ok = true
for _, tab in ipairs(panel and panel.tabs or {}) do
  for _, w in ipairs(tab.widgets or {}) do
    if not VALID[w.type] then widgets_ok = false end
  end
end
check(panel and #panel.tabs == 7 and widgets_ok, "panel: seven tabs, every widget a known type")

-- =====================================================================
-- 6 Oct 2026: the roster's windows, the needs pieces, the finds hall in Guild.Warehouse,
-- an incoming war host and the war saga
fresh_client(); dofile(PLUGIN); fire_timers()
gmcp("Guild.Roster", '{ "pages": 3, "hird_from": 0, "page": 1, "guild": "viking" }')
gmcp("Guild.Roster", '{ "hird_page": [ { "level": 50, "status": "personal_guard", "age": "elder", "atk": 10, "def": 10, "loyalty": 2, "mode": "neutral", "name": "Njord Brandsson", "id": 1 } ], "pages": 3, "guild": "viking", "page": 2 }')
gmcp("Guild.Roster", '{ "hird_total": 2, "rneeds_c": "shadow_house:charm:spymaster;courier_post:trade:silver_tongue;beacon:sea:old_salt;training_yard:combat:taskmaster;moat:combat:ir", "gneeds": { "hird_cap": 24, "hird_count": 2 }, "pages": 3, "page": 3, "guild": "viking" }')
gmcp("Guild.Roster", '{ "pages": 1, "hird_from": 1, "hird_page": [ { "level": 50, "age": "young", "atk": 8, "def": 10, "loyalty": 4, "name": "Ivar Ketilsson", "id": 9 } ], "page": 1, "guild": "viking" }')
gmcp("Guild.Warehouse", '{ "vfind_offers_total": 0, "vfind_posts": [ ], "vfind_offers": [ ], "vfind_auctions": [ ], "full": 1, "pages": 1, "vfind_hall": { "max_finds": 4, "tier": 4 }, "guild": "viking", "page": 1 }')
gmcp("Guild.Kingdom", '{ "war_cb": [ ], "full": 1, "pages": 1, "war_camp": [ ], "war_incoming": { "strength": 116, "days": 2, "town": "Rouen" }, "page": 1, "guild": "viking" }')
gmcp("Guild.War", '{ "full": 1, "pages": 2, "guild": "viking", "saga_war": [ { "tone": "omen", "text": "Winchester declares war and gathers a host against your realm!", "t": 1788442323 }, { "tone": "loss", "text": "Winchester sacks your holdings -- 66 settlers and 337391 daler carried off.", "t": 1788875481 } ], "page": 1 }')
gmcp("Guild.War", '{ "war_points": 0, "target": "", "full": 1, "pages": 2, "page": 2, "guild": "viking", "w": 0 }')
fire_timers()
local h6 = st("hird")
check(h6:find("Njord Brandsson", 1, true) and h6:find("Ivar Ketilsson", 1, true), "6 Oct: the hird is read from its windows")
local r6 = st("recruit")
check(r6:find("Beacon", 1, true) and r6:find("Old Salt", 1, true), "6 Oct: recruit needs from the rneeds pieces (" .. r6:sub(1, 160) .. ")")
check(not r6:find("Moat", 1, true), "6 Oct: the entry a piece cut in half is not guessed at")
check(r6:find("Hiring hall T4", 1, true) and r6:find("finds 4 at a time", 1, true), "6 Oct: the finds hall comes from Guild.Warehouse")
local w6 = st("war")
check(w6:find("INCOMING: Rouen marches on you - strength 116, 2 days left", 1, true), "6 Oct: the War tab warns of the incoming host")
local loss, omen = w6:find("Winchester sacks", 1, true), w6:find("Winchester declares", 1, true)
check(loss and omen and loss < omen and w6:find("@{error}Winchester sacks", 1, true), "6 Oct: the war saga, newest first, losses in red")

print(string.format("vikingkingdom_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
