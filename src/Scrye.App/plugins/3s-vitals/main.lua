-- 3S Vitals -- the fight-glance bars, and nothing else.
--
-- 2.0: rebuilt for the two-feed world. Every gauge still BINDS to state the host
-- keeps live - no polling, no assembler, no machinery - but the paths now come in
-- two flavours per set:
--
--   * GMCP (3Scapes with the new feed): Char.Vitals files under char.vitals.*,
--     Guild.State's points/bars/target blocks under guild.state.* (each block
--     arrives whole in one message, so the automatic state filing is reliable for
--     them - unlike the paged LISTS the big viking plugins assemble), and the
--     enemy comes from Char.Combat (attacker = the enemy's name, attacker_hp =
--     its health percent).
--   * MIP (3K, and 3S with MIP on): the classic character.* / vik.* / enemy.*
--     paths, unchanged - this plugin ships on both MUDs.
--
-- Which feed is live is detected per prompt (char.vitals.hp only exists under
-- GMCP); which GUILD'S bars to draw is the Settings tab:
--
--   * Auto (default): a Viking is recognised by guild.state.guild == "viking"
--     (GMCP) or the vik.mseid key the BBE feed writes (MIP), and gets the named
--     pools - Seid/Vig/Rad from guild.state.points (vitka/viga/drotta), which
--     cannot drift when GP1/GP2 are retoggled in game (the same reasoning as the
--     classic's vik.seid-by-name rule). Everyone else gets the generic set - and
--     under GMCP the GP bars are labelled with THE SERVER'S OWN NAMES for that
--     guild (guild.info.gp1_name / gp2_name), so any guild's pools come out
--     correctly named without this file knowing the guild at all.
--   * Viking / Generic: pin a set regardless of detection (persisted).
--
-- New in 2.0 besides the above: a Coffin gauge under GMCP (char.vitals.coffin -
-- the plan's coffin-alert idea, in its natural home).

local P = "plugin." .. scrye.id .. "."

-- ---------------------------------------------------------------- the sets
-- Each entry: { label, value path, max path (or number) }.

-- A note for whoever debugs blinking gauges next: Seid/Vig/Rad used to drop to
-- zero at random while HP sat still, and the cause was NOT here. Guild.State
-- arrives paged - bars on page 1, points on page 3 - and StateStore.SetJson
-- pruned every key a payload did not carry, so each page deleted the one before
-- it and guild.state.points.* existed only between page 3 and the next page 1.
-- HP was steady because char.vitals.* is never paged. Fixed in the host (31 Aug):
-- a package seen to arrive paged is never pruned again. Nothing in this file
-- needed to change, and nothing here should be changed to work around it.
local function viking_set(gmcp)
  if gmcp then
    return {
      { "HP",   "char.vitals.hp",           "char.vitals.maxhp"          },
      { "Seid", "guild.state.points.vitka", "guild.state.points.mvitka"  },
      { "Vig",  "guild.state.points.viga",  "guild.state.points.mviga"   },
      { "Rad",  "guild.state.points.drotta","guild.state.points.mdrotta" },
      { "Coffin", "char.vitals.coffin",     "char.vitals.coffin_max"     },
    }
  end
  -- MIP: named vik.* keys, which cannot drift when GP1/GP2 are retoggled
  return {
    { "HP",   "character.health.current", "character.health.max" },
    { "Seid", "vik.seid",                 "vik.mseid"            },
    { "Vig",  "vik.vig",                  "vik.mvig"             },
    { "Rad",  "vik.rad",                  "vik.mrad"             },
  }
end

local function generic_set(gmcp, gp1, gp2)
  if gmcp then
    return {
      { "HP", "char.vitals.hp", "char.vitals.maxhp" },
      { "SP", "char.vitals.sp", "char.vitals.maxsp" },
      { gp1,  "guild.state.bars.gp1", "guild.state.bars.gp1_max" },
      { gp2,  "guild.state.bars.gp2", "guild.state.bars.gp2_max" },
      { "Coffin", "char.vitals.coffin", "char.vitals.coffin_max" },
    }
  end
  -- MIP: the standard vitals every guild reports. SP is real outside the Viking
  -- guild (Vikings get junk in it, which is why they need their own set at all).
  return {
    { "HP",  "character.health.current", "character.health.max" },
    { "SP",  "character.spell.current",  "character.spell.max"  },
    { gp1,   "character.gold.a",         "character.gold.amax"  },
    { gp2,   "character.gold.b",         "character.gold.bmax"  },
  }
end

-- ---------------------------------------------------------------- settings
local pref = scrye.store.get("guild") or "auto"   -- auto | viking | generic
local set_pref   -- forward decl (the Settings buttons call it; apply is below)

-- ------------------------------------------------------------------ build
local built_sig = nil

local function build(set, gmcp, why)
  local w = {}
  for _, g in ipairs(set) do
    -- dim = true: the bar darkens as the value drops (green base for stats)
    w[#w + 1] = { type = "gauge", text = g[1], value = g[2], max = g[3], dim = true }
  end
  if gmcp then
    -- Char.Combat: attacker = the enemy's name, attacker_hp = its health percent
    w[#w + 1] = { type = "value", text = "Enemy: ", bind = "char.combat.attacker", color = "error" }
    w[#w + 1] = { type = "gauge", text = "Enemy", value = "char.combat.attacker_hp", max = 100,
                  dim = true, color = "error" }
  else
    w[#w + 1] = { type = "value", text = "Enemy: ", bind = "enemy.name", color = "error" }
    w[#w + 1] = { type = "gauge", text = "Enemy", value = "enemy.health", max = 100,
                  dim = true, color = "error" }
  end
  -- Same title every time: the host replaces a panel under the same key rather
  -- than adding a second one, keeping position and the companion's panel id.
  scrye.addPanel{
    title = "Vitals",
    width = 240,
    accent = "#D855B8",          -- signature: vitals rose (validated accent set)
    tabs = {
      { title = "Bars", widgets = w },
      { title = "Settings", widgets = {
          { type = "value", text = "Showing: ", bind = P .. "mode", color = "info" },
          { type = "label", text = "Which guild's bars to draw:", color = "dim" },
          { type = "buttonrow", buttons = {
              { text = "Auto",    action = function() set_pref("auto") end },
              { text = "Viking",  action = function() set_pref("viking") end },
              { text = "Generic", action = function() set_pref("generic") end },
          } },
          { type = "label", color = "dim",
            text = "Auto reads the feed (guild.state.guild / the vik.* keys). Generic labels the GP bars with the server's own names for your guild. ('vitals guild auto|viking|generic' works too.)" },
      } },
    },
  }
  scrye.setState(P .. "mode", why)
end

-- ------------------------------------------------------------------ apply
-- Which set does this character want right now? Cheap state reads, a rebuild
-- only when the answer changes - in practice once per character, then nothing.
local function apply()
  local gmcp = (scrye.getState("char.vitals.hp") or "") ~= ""
  local is_viking
  if gmcp then
    is_viking = (scrye.getState("guild.state.guild") or "") == "viking"
  else
    is_viking = (scrye.getState("vik.mseid") or "") ~= ""
  end
  local choice = pref
  if choice == "auto" then choice = is_viking and "viking" or "generic" end

  local set, why
  if choice == "viking" then
    set = viking_set(gmcp)
    why = "Viking"
  else
    -- generic: under GMCP the server names the guild's pools itself
    local gp1 = scrye.getState("guild.info.gp1_name") or ""
    local gp2 = scrye.getState("guild.info.gp2_name") or ""
    if gp1 == "" then gp1 = "GP1" end
    if gp2 == "" then gp2 = "GP2" end
    set = generic_set(gmcp, gp1, gp2)
    why = "Generic (" .. gp1 .. "/" .. gp2 .. ")"
  end
  why = why .. (gmcp and " - GMCP" or " - MIP") .. (pref == "auto" and " (auto)" or " (pinned)")

  -- rebuild only when something visible changed (set, labels, feed, or source)
  local sig = why
  for _, g in ipairs(set) do sig = sig .. "|" .. g[1] .. "=" .. g[2] end
  if sig == built_sig then
    scrye.setState(P .. "mode", why)   -- keep the Settings line fresh regardless
    return
  end
  built_sig = sig
  build(set, gmcp, why)
end

set_pref = function(v)
  pref = v
  scrye.store.set("guild", v)
  scrye.print("[vitals] guild bars: " .. v)
  apply()
end

scrye.addAlias{
  pattern = "^vitals guild (auto|viking|generic)$", regex = true,
  run = function(v) set_pref(v) end,
}

-- Nothing is known at load - start on what the state already says (fresh load
-- mid-session has live state; a cold connect gets the generic MIP set until the
-- first data lands). The prompt is the cheapest place to notice changes.
apply()
scrye.onPrompt(apply)
