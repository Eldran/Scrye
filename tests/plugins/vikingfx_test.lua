-- Harness for 3s-viking-fx (the effect timer bar). The Guild.State payloads are
-- verbatim from the 27 Aug 2026 capture (_lab/captures/gmcp-fields-Goran-20260827.md):
-- the unpaged fx refresh and the full 3-page burst (god / gxp+hp / points+target).
--
-- What this file protects: stfx parsing, the local 1 s tick-down + server-refresh
-- correction, the once-per-cast warn (and its re-arm on a re-cast), the drop of
-- effects the server no longer lists, the "gone?" hold on a local zero, and the
-- paged-burst path through the assembler (god + fx from a full Guild.State burst).

--
-- Equivalent mutant, documented rather than faked: gasm stripping only page/pages
-- (letting "full"/"guild" sit in the snapshot as data keys) survives - no consumer
-- in this plugin reads those names, so no published state changes. It matters the
-- day a consumer iterates snapshot keys (the viking-status Feeds tab will); the
-- shared-snippet contract in the plan doc §3 is the guard until then.

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-viking-fx/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

-- (decoder shared shape with the other harnesses)
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

local printed, notified, gmcp_hooks, aliases, repeats, store, state, panel

local function fresh_client()
  printed, notified = {}, {}
  gmcp_hooks, aliases, repeats = {}, {}, {}
  store, state, panel = {}, {}, nil
  scrye = {
    id = "3s-viking-fx",
    print = function(s) printed[#printed + 1] = tostring(s) end,
    send = function() end,
    notify = function(s) notified[#notified + 1] = tostring(s) end,
    json = { decode = decode },
    store = {
      get = function(k) return store[k] end,
      set = function(k, v) store[k] = v end,
      delete = function(k) store[k] = nil end,
    },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addPanel = function(p) panel = p end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    every = function(_, fn) repeats[#repeats + 1] = fn; return #repeats end,
    cancel = function() end,
  }
end

local function gmcp(json)
  for _, h in ipairs(gmcp_hooks) do if h.pkg == "Guild.State" then h.fn(json) end end
end
local function tick(n) for _ = 1, n or 1 do for _, fn in ipairs(repeats) do fn() end end end
local function vfx(rest)
  for _, a in ipairs(aliases) do
    if rest ~= nil and a.pattern == "^vfx (.+)$" then a.run(rest) return end
    if rest == nil and a.pattern == "^vfx$" then a.run() return end
  end
end
local function list() return tostring(state["plugin.3s-viking-fx.list"] or "") end
local function said(pat)
  local n = 0
  for _, l in ipairs(printed) do if l:find(pat, 1, true) then n = n + 1 end end
  return n
end

-- verbatim: the unpaged Guild.State fx refresh (capture main example)
local UNPAGED = '{ "guild": "viking", "fx": { "stfx": "[aeg:205 skad:203 valg:182 bif:256 veth:35 gsjon:295 hrei:158 gro:167]", "queue": "", "chan": "" } }'
-- verbatim: the full 3-page Guild.State burst (capture, first three payload samples)
local FULL_P1 = '{ "god": { "expires_at": 1787849225, "focus": "rite", "name": "Freyja" }, "chain": { "bsdepth": 0, "chain": 0 }, "gline2": "G[68498(0)|37405(0)|82783(0)|1486778(0)] L[4|4(%)] E[None||0]", "bars": { "gp1_max": 7152, "gp2_max": 8082, "gp1": 5852, "gp2": 8082 }, "daler": 943976, "encounter": { "rounds": 0, "active": 0 }, "full": 1, "pages": 3, "gline1": "H[6312|6333(0|9499)] S[5852|7152] V[8082|8082] R[6964|6964] F[----------] C[0\\/0]", "page": 1, "guild": "viking", "fx": { "stfx": "[valg:588 veth:201 gro:259]", "queue": "", "chan": "" } }'
local FULL_P2 = '{ "ledung": { "max": 4, "reset_pct": 0, "charges": 4 }, "missions_reg": 5, "gxp": { "buandi_last": 0, "vitka_max": 58875, "drotta_last": 0, "vitka": 68498, "drotta_max": 58875, "viga_max": 58875, "buandi": 1486778, "buandi_max": 58875, "drotta": 82783, "viga_last": 0, "vitka_last": 0, "viga": 37405 }, "hp": { "mthrek": 9499, "cur": 6312, "threk": 0, "delta": 0, "max": 6333 }, "full": 1, "pages": 3, "missions_newbie": 3, "guild": "viking", "page": 2 }'
local FULL_P3 = '{ "points": { "mvitka": 7152, "mviga": 8082, "mfury": 333, "vitka": 5852, "mdrotta": 6964, "mbuandi": 6963, "viga_delta": 0, "drotta_delta": 0, "fury": 0, "buandi": 714, "drotta": 6964, "viga": 8082, "vitka_delta": -75 }, "full": 1, "sp": { "cur": 4885, "max": 53 }, "target": { "hp_status": "", "name5": "None", "name": "None" }, "pages": 3, "tox": { "cur": 0, "max": 18 }, "page": 3, "guild": "viking" }'

-- =====================================================================
-- honest before the feed
fresh_client(); dofile(PLUGIN)
check(list():find("waiting for Guild.State", 1, true), "before any burst the panel says what it waits for")

-- unpaged refresh: all eight effects parsed, sorted soonest-first
gmcp(UNPAGED)
check(list():find("veth", 1, true) and list():find("gsjon", 1, true), "all effects listed")
local first = list():match("@{%w+}(%a+)")
check(first == "veth", "soonest effect (veth 35s) sorts first (got " .. tostring(first) .. ")")
check(list():find("0:35", 1, true), "veth shows 0:35")
check(list():find("4:55", 1, true), "gsjon shows 4:55 (295s)")

-- local tick-down between refreshes
tick(5)
check(list():find("0:30", 1, true), "5 local ticks: veth 35 -> 0:30")

-- once-per-cast warn at the default 30 s line
printed = {}
tick(1)   -- veth 29
check(said("veth under 30s") == 1, "crossing 30 s warns once")
tick(3)
check(said("veth under 30s") == 1, "further ticks do not repeat the warn")

-- phone notify only when enabled
check(#notified == 0, "phone quiet with notify off")
vfx("notify on")
gmcp('{ "guild": "viking", "fx": { "stfx": "[veth:26 aeg:200]", "queue": "", "chan": "" } }')
tick(1)
check(#notified >= 1 and notified[#notified]:find("veth expiring", 1, true),
  "notify on: the phone hears about veth")

-- a re-cast (value back above the line) re-arms the warn
printed = {}
gmcp('{ "guild": "viking", "fx": { "stfx": "[veth:300 aeg:199]", "queue": "", "chan": "" } }')
check(said("veth under 30s") == 0, "re-cast: no immediate warn")
check(list():find("5:00", 1, true), "re-cast veth shows 5:00")
gmcp('{ "guild": "viking", "fx": { "stfx": "[veth:25 aeg:198]", "queue": "", "chan": "" } }')
check(said("veth under 30s") == 1, "after the re-cast the warn is armed again")

-- server truth beats local arithmetic: a vanished effect is dropped at once
gmcp('{ "guild": "viking", "fx": { "stfx": "[aeg:197]", "queue": "", "chan": "" } }')
check(not list():find("veth", 1, true), "veth vanished from stfx -> dropped")

-- a local zero holds as "gone?" then leaves without a server refresh
gmcp('{ "guild": "viking", "fx": { "stfx": "[aeg:3]", "queue": "", "chan": "" } }')
tick(3)
check(list():find("gone?", 1, true), "locally-expired effect lingers as gone?")
tick(6)
check(not list():find("aeg", 1, true) and list():find("no effects up", 1, true),
  "after the hold it leaves; the panel says no effects up")

-- =====================================================================
-- the paged path: a full 3-page burst delivers god + fx together
fresh_client(); dofile(PLUGIN)
gmcp(FULL_P1)
check(not list():find("Freyja", 1, true), "half a burst publishes nothing")
gmcp(FULL_P2); gmcp(FULL_P3)
check(list():find("Freyja", 1, true) and list():find("rite", 1, true), "burst complete: god + focus shown")
check(list():find("valg", 1, true) and list():find("3:21", 1, true), "burst fx parsed (gro 201... veth 3:21)")

-- warn threshold is configurable and persisted
vfx("warn 250")
check(store["warn"] == "250", "vfx warn persists")
printed = {}
tick(1)
check(said("under 250s") >= 1, "the new line judges the current effects")

-- bar scale: maxseen never shrinks, so a mid-buff load undersells and corrects
fresh_client(); dofile(PLUGIN)
gmcp('{ "guild": "viking", "fx": { "stfx": "[gro:100]", "queue": "", "chan": "" } }')
local full_bar = list():match("(#+)")
check(full_bar and #full_bar == 10, "first sight = full bar")
gmcp('{ "guild": "viking", "fx": { "stfx": "[gro:50]", "queue": "", "chan": "" } }')
local half_bar = list():match("(#+)")
check(half_bar and #half_bar == 5, "50 of maxseen 100 = half bar")

-- a re-cast that outlasts the first sighting raises the scale too
gmcp('{ "guild": "viking", "fx": { "stfx": "[gro:200]", "queue": "", "chan": "" } }')
gmcp('{ "guild": "viking", "fx": { "stfx": "[gro:100]", "queue": "", "chan": "" } }')
local rebar = list():match("(#+)")
check(rebar and #rebar == 5, "re-cast to 200 rescales: 100 left = half bar (got " .. tostring(rebar and #rebar) .. ")")

-- queue / chan surface when non-empty
gmcp('{ "guild": "viking", "fx": { "stfx": "[gro:50]", "queue": "bif", "chan": "" } }')
check(list():find("queue: bif", 1, true), "fx.queue surfaces when non-empty")

-- =====================================================================
-- panel: only widget types the host renders (the farmer's empty-panel lesson)
local VALID = { value = true, label = true, text = true, buttonrow = true, input = true }
local widgets_ok = true
for _, tab in ipairs(panel and panel.tabs or {}) do
  for _, w in ipairs(tab.widgets or {}) do
    if not VALID[w.type] then widgets_ok = false end
  end
end
check(panel and widgets_ok and panel.tabs and #panel.tabs == 1,
  "panel: one tab, every widget a type the host knows")

print(string.format("vikingfx_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
