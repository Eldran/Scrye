-- Harness for 3s-vitals 2.0. State values mirror the 27-28 Aug captures (a viking
-- under GMCP: char.vitals hp 6323/6333, guild.state.points vitka 5852/7152; the
-- Char.Combat enemy "a growing misfigured cur" at 85%); the non-viking guild is
-- SYNTHETIC (an elemental with server-named Mana/Focus pools) - no capture from
-- another guild exists yet, which is exactly why the generic set takes its labels
-- from guild.info rather than a table in the plugin.
--
-- What this file protects: feed detection (GMCP vs MIP paths per set), guild
-- detection + the Settings override (persisted), the server-named GP labels, the
-- drift-proof viking bindings (points.vitka by name, never bars.gp1), the enemy
-- block per feed, the coffin gauge, rebuild-only-on-change, and the panel
-- vocabulary.

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-vitals/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

local printed, aliases, store, state, panel, panel_builds, prompt_hooks

local function fresh_client()
  printed, aliases = {}, {}
  store, state, panel, panel_builds = {}, {}, nil, 0
  prompt_hooks = {}
  scrye = {
    id = "3s-vitals",
    print = function(x) printed[#printed + 1] = tostring(x) end,
    store = {
      get = function(k) return store[k] end,
      set = function(k, v) store[k] = tostring(v) end,
    },
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addPanel = function(pn) panel = pn; panel_builds = panel_builds + 1 end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    onPrompt = function(fn) prompt_hooks[#prompt_hooks + 1] = fn end,
  }
end

local function prompt() for _, fn in ipairs(prompt_hooks) do fn() end end
local function gauges()
  local out = {}
  for _, tab in ipairs(panel.tabs) do
    if tab.title == "Bars" then
      for _, w in ipairs(tab.widgets) do
        if w.type == "gauge" then out[#out + 1] = w end
      end
    end
  end
  return out
end
local function gauge(label)
  for _, g in ipairs(gauges()) do if g.text == label then return g end end
end
local function enemy_value()
  for _, tab in ipairs(panel.tabs) do
    if tab.title == "Bars" then
      for _, w in ipairs(tab.widgets) do
        if w.type == "value" and w.text == "Enemy: " then return w end
      end
    end
  end
end
local function set_alias(v)
  for _, a in ipairs(aliases) do
    -- match on the verb, not the exact alternation: the guild list grows
    if tostring(a.pattern):find("^%^vitals guild ") then a.run(v) return end
  end
end
local function mode() return tostring(state["plugin.3s-vitals.mode"] or "") end

-- =====================================================================
-- cold load, nothing known: the generic MIP set (correct for every guild but
-- one, and populates for a Viking too until the feed speaks)
fresh_client(); dofile(PLUGIN)
check(panel ~= nil and gauge("HP") and gauge("HP").value == "character.health.current",
  "cold load: generic MIP set")
check(gauge("GP1") and gauge("GP1").value == "character.gold.a", "cold load: unlabelled GP slots")
check(mode():find("MIP", 1, true) and mode():find("auto", 1, true), "cold load: mode line says MIP + auto")

-- MIP viking: the BBE feed speaks -> the named vik.* set (classic behaviour)
state["vik.mseid"] = "7152"
prompt()
check(gauge("Seid") and gauge("Seid").value == "vik.seid", "MIP viking: Seid by its named key")
check(gauge("Rad") and gauge("Rad").max == "vik.mrad", "MIP viking: Rad max path")
check(enemy_value().bind == "enemy.name", "MIP: enemy from the MIP paths")
check(mode():find("Viking", 1, true), "MIP viking: mode line")

-- =====================================================================
-- GMCP viking (capture values): char.vitals + guild.state -> the GMCP set
fresh_client(); dofile(PLUGIN)
state["char.vitals.hp"] = "6323"
state["guild.state.guild"] = "viking"
prompt()
check(gauge("HP").value == "char.vitals.hp" and gauge("HP").max == "char.vitals.maxhp",
  "GMCP viking: HP from Char.Vitals")
check(gauge("Seid") and gauge("Seid").value == "guild.state.points.vitka"
  and gauge("Seid").max == "guild.state.points.mvitka",
  "GMCP viking: Seid = points.vitka BY NAME (drift-proof), never bars.gp1")
check(gauge("Vig").value == "guild.state.points.viga" and gauge("Rad").value == "guild.state.points.drotta",
  "GMCP viking: Vig/Rad from points.viga/.drotta")
check(gauge("Coffin") and gauge("Coffin").value == "char.vitals.coffin",
  "GMCP: the coffin gauge rides along")
check(enemy_value().bind == "char.combat.attacker", "GMCP: enemy name from Char.Combat")
local eg
for _, g in ipairs(gauges()) do if g.text == "Enemy" then eg = g end end
check(eg and eg.value == "char.combat.attacker_hp" and eg.max == 100,
  "GMCP: enemy health = attacker_hp out of 100")

-- rebuild only on change: another prompt with the same state builds nothing new
local builds = panel_builds
prompt(); prompt()
check(panel_builds == builds, "no rebuild while nothing changed")

-- =====================================================================
-- GMCP, another guild (SYNTHETIC): the server names the pools itself
fresh_client(); dofile(PLUGIN)
state["char.vitals.hp"] = "1200"
state["guild.state.guild"] = "elemental"
state["guild.info.gp1_name"] = "Mana"
state["guild.info.gp2_name"] = "Focus"
prompt()
check(gauge("Mana") and gauge("Mana").value == "guild.state.bars.gp1",
  "GMCP generic: gp1 bar wears the server's name for it")
check(gauge("Focus") and gauge("Focus").max == "guild.state.bars.gp2_max",
  "GMCP generic: gp2 likewise")
check(gauge("SP") and gauge("SP").value == "char.vitals.sp", "GMCP generic: SP is real here")
check(mode():find("Mana/Focus", 1, true), "mode line names the pools")

-- late-arriving names relabel the bars (one rebuild)
state["guild.info.gp1_name"] = "Chi"
prompt()
check(gauge("Chi") ~= nil and gauge("Mana") == nil, "a changed pool name relabels the bar")

-- =====================================================================
-- the Settings override: pin Generic on a viking, persisted; back to Auto
fresh_client(); dofile(PLUGIN)
state["char.vitals.hp"] = "6323"
state["guild.state.guild"] = "viking"
prompt()
check(gauge("Seid") ~= nil, "auto on a viking: viking set")
set_alias("generic")
check(gauge("Seid") == nil and gauge("GP1") ~= nil, "pinned generic on a viking")
check(store["guild"] == "generic", "the pin persists")
check(mode():find("pinned", 1, true), "mode line says pinned")
set_alias("auto")
check(gauge("Seid") ~= nil, "back to auto: viking set again")

-- a restart honours the stored pin
fresh_client()
store["guild"] = "generic"
dofile(PLUGIN)
state["char.vitals.hp"] = "6323"
state["guild.state.guild"] = "viking"
prompt()
check(gauge("Seid") == nil and gauge("GP1") ~= nil, "restart: the stored pin still holds")

-- the Settings buttons drive the same path as the alias
local btns
for _, tab in ipairs(panel.tabs) do
  if tab.title == "Settings" then
    for _, w in ipairs(tab.widgets) do
      if w.type == "buttonrow" then btns = w.buttons end
    end
  end
end
do
  local labels = {}
  for _, b in ipairs(btns or {}) do labels[#labels + 1] = b.text end
  local joined = table.concat(labels, ",")
  check(joined == "Auto,Viking,Cyborg,Gentech,Mage,Juggernaut,Generic",
    "settings: a button per guild set, Auto first (got " .. joined .. ")")
end
btns[2].action()   -- "Viking"
check(gauge("Seid") ~= nil and store["guild"] == "viking", "the Viking button pins the viking set")

-- =====================================================================
-- panel vocabulary
local VALID = { gauge = true, value = true, label = true, buttonrow = true }
local ok_types = true
for _, tab in ipairs(panel.tabs) do
  for _, w in ipairs(tab.widgets) do
    if not VALID[w.type] then ok_types = false; print("bad widget: " .. tostring(w.type)) end
  end
end
check(ok_types and #panel.tabs == 2, "panel: Bars + Settings, every widget a known type")

-- =====================================================================
-- Cyborg (capture: Lobo, 1 Sep 2026). Values verbatim: power 4825/4912,
-- heat_pct 0, hp 8434/9576, coffin 2/25. The point of this block is that a
-- cyborg must NOT be handed the generic set - guild.info carries no gp1_name or
-- gp2_name for this guild, and guild.state has no bars at all, so the generic
-- bars would bind paths that do not exist and read empty.
do
  fresh_client(); dofile(PLUGIN)
  state["char.vitals.hp"] = 8434
  state["char.vitals.maxhp"] = 9576
  state["char.vitals.coffin"] = 2
  state["char.vitals.coffin_max"] = 25
  state["guild.state.guild"] = "cyborg"
  state["guild.state.power"] = 4825
  state["guild.state.power_max"] = 4912
  state["guild.state.heat_pct"] = 0
  prompt()

  check(gauge("Power") ~= nil, "cyborg: auto-detection gives a Power gauge")
  check(gauge("Power") and gauge("Power").value == "guild.state.power"
    and gauge("Power").max == "guild.state.power_max",
    "cyborg: Power is bound to power/power_max")
  check(gauge("Heat") ~= nil and gauge("Heat").max == 100,
    "cyborg: Heat is a percent, so its max is the literal 100")
  check(gauge("HP") ~= nil and gauge("HP").value == "char.vitals.hp",
    "cyborg: HP still comes from Char.Vitals")
  check(gauge("Coffin") ~= nil, "cyborg: the coffin gauge is kept")
  check(gauge("Seid") == nil and gauge("Vig") == nil, "cyborg: no viking bars")
  check(gauge("SP") == nil, "cyborg: no SP - a cyborg's sp reads 315/45 and means nothing here")
  check(gauge("GP1") == nil, "cyborg: NOT the generic set (guild.info names no pools for it)")
  check(tostring(state["plugin.3s-vitals.mode"]):find("Cyborg", 1, true) ~= nil,
    "cyborg: the Settings line says which set is showing")

  -- the pin works, and survives a restart
  set_alias("cyborg")
  check(store["guild"] == "cyborg", "cyborg: the pin is persisted")
  check(gauge("Power") ~= nil, "cyborg: pinned gives the same set auto did")
  check(mode():find("pinned", 1, true) ~= nil,
    "cyborg: ...and the Settings line stops saying auto")

  -- a viking on the same host still gets viking bars: the sets are chosen by
  -- guild.state.guild, so one guild's arrival must not have taught the plugin
  -- anything sticky about the other
  fresh_client(); dofile(PLUGIN)
  state["char.vitals.hp"] = 6323
  state["char.vitals.maxhp"] = 6333
  state["guild.state.guild"] = "viking"
  state["guild.state.points.vitka"] = 5852
  state["guild.state.points.mvitka"] = 7152
  prompt()
  check(gauge("Seid") ~= nil and gauge("Power") == nil,
    "cyborg: a viking is unaffected by the cyborg set existing")
end

-- =====================================================================
-- Gentech (capture: another player's character, 1 Sep 2026). PU 9750/11250 and
-- CPC 10000/10000 are the guild pools; SP is REAL for this guild (202/202) but
-- does not take a slot, because the pools are what move in a fight.
do
  fresh_client(); dofile(PLUGIN)
  state["char.vitals.hp"] = 3931
  state["char.vitals.maxhp"] = 3931
  state["char.vitals.coffin"] = 5
  state["char.vitals.coffin_max"] = 14
  state["char.vitals.sp"] = 202
  state["guild.state.guild"] = "gentech"
  state["guild.state.pu"] = 9750
  state["guild.state.pu_max"] = 11250
  state["guild.state.cpc"] = 10000
  state["guild.state.cpc_max"] = 10000
  prompt()

  check(gauge("PU") ~= nil and gauge("PU").value == "guild.state.pu"
    and gauge("PU").max == "guild.state.pu_max", "gentech: PU is bound to pu/pu_max")
  check(gauge("CPC") ~= nil, "gentech: CPC is the second pool")
  check(gauge("HP") ~= nil and gauge("Coffin") ~= nil, "gentech: HP and Coffin as everywhere else")
  check(gauge("SP") == nil, "gentech: SP is real here but does not take a slot")
  check(gauge("Power") == nil and gauge("Seid") == nil, "gentech: no other guild's bars")
  check(gauge("GP1") == nil, "gentech: NOT the generic set")
  check(mode():find("Gentech", 1, true) ~= nil, "gentech: the Settings line names the set")

  -- three guilds now detect from the same field, so one must not shadow another
  fresh_client(); dofile(PLUGIN)
  state["char.vitals.hp"] = 8434
  state["guild.state.guild"] = "cyborg"
  state["guild.state.power"] = 4825
  state["guild.state.power_max"] = 4912
  prompt()
  check(gauge("Power") ~= nil and gauge("PU") == nil,
    "gentech: a cyborg still gets the cyborg set")
end

-- Juggernaut (capture: icewind, 25 Sep 2026): HP / SP / Heat / Stim / Coffin
do
  fresh_client(); dofile(PLUGIN)
  state["char.vitals.hp"] = 863
  state["char.vitals.maxhp"] = 955
  state["char.vitals.sp"] = 785
  state["char.vitals.maxsp"] = 785
  state["guild.state.guild"] = "juggernaut"
  state["guild.state.heat_pct"] = 44
  state["guild.state.stim_pct"] = 56
  prompt()
  check(gauge("Heat") ~= nil and gauge("Heat").value == "guild.state.heat_pct" and gauge("Heat").max == 100,
    "juggernaut: heat is a percent of 100")
  check(gauge("Stim") ~= nil and gauge("Stim").value == "guild.state.stim_pct", "juggernaut: stims")
  check(gauge("SP") ~= nil and gauge("HP") ~= nil and gauge("Coffin") ~= nil, "juggernaut: HP, SP and Coffin")
  check(gauge("Power") == nil and gauge("GP1") == nil, "juggernaut: not the cyborg or the generic set")
  check(mode():find("Juggernaut", 1, true) ~= nil, "juggernaut: the Settings line names the set")
  set_alias("juggernaut")
  check(mode():find("pinned", 1, true) ~= nil, "juggernaut: 'vitals guild juggernaut' pins it")
end

print(string.format("vitals_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
