-- 3S Vitals — the fight-glance bars, and nothing else.
--
-- Extracted from 3S Viking Status: these gauges bind straight to global state the
-- MIP feed keeps current (character.*, vik.*, enemy.*), so they never needed the big
-- plugin's machinery — and in their own panel they can sit small next to the output
-- while the full Viking Status panel stays closed or parked elsewhere.
--
-- The one piece of logic here is picking WHICH bars to draw, because the four stats
-- worth watching are not the same in every guild. See below.

local VIKING_FEED = "vik.mseid"   -- a key only the BBE viking feed ever writes

-- The two sets. Each entry is { label, value path, max path }; the host keeps every
-- bound gauge live, so nothing here polls or copies numbers around.
--
-- The Viking set reads the BBE feed BY NAME, and deliberately ignores the standard
-- gp1/gp2 slots even though those normally hold two of these same numbers. A Viking
-- can retoggle GP1/GP2 between Rad, Seid, Vigor and Fury in game, so a bar labelled
-- "Seid" bound to gp1 would quietly start showing Fury the day they changed it.
-- vik.seid is named by the feed and cannot drift like that.
local VIKING = {
  { "HP",   "character.health.current", "character.health.max" },
  { "Seid", "vik.seid",                 "vik.mseid"            },
  { "Vig",  "vik.vig",                  "vik.mvig"             },
  { "Rad",  "vik.rad",                  "vik.mrad"             },
}

-- Everyone else: the standard MIP vitals, which every guild reports (FFF fields A-H).
-- GP1/GP2 are whatever that guild assigns them — preset outside the Viking guild — so
-- they are labelled by their slot rather than guessed at. SP is real here and is the
-- reason Vikings need their own set at all: Vikings do not use the stat, and the MUD
-- sends junk for it (observed: 933/759 on an elemental, 4885/254 on a viking), which
-- would draw a permanently pegged bar.
local GENERIC = {
  { "HP",  "character.health.current", "character.health.max" },
  { "SP",  "character.spell.current",  "character.spell.max"  },
  { "GP1", "character.gold.a",         "character.gold.amax"  },
  { "GP2", "character.gold.b",         "character.gold.bmax"  },
}

local function build(set)
  local w = {}
  for _, g in ipairs(set) do
    -- dim = true: the bar darkens as the value drops (green base for stats)
    w[#w + 1] = { type = "gauge", text = g[1], value = g[2], max = g[3], dim = true }
  end
  w[#w + 1] = { type = "value", text = "Enemy: ", bind = "enemy.name", color = "error" }
  w[#w + 1] = { type = "gauge", text = "Enemy", value = "enemy.health", max = 100,
                dim = true, color = "error" }
  -- Same title every time: the host replaces a panel under the same key rather than
  -- adding a second one, keeping the canvas position and the companion's panel id.
  scrye.addPanel{
    title = "Vitals",
    width = 240,
    accent = "#D855B8",          -- signature: vitals rose (validated accent set)
    widgets = w,
  }
end

-- Which set does this character want? The viking feed is the only reliable signal.
-- It is NOT enough to look at the standard vitals: every guild fills those in, and on
-- a Viking their meaning is player-configurable.
local shown = nil
local function apply()
  local want = (scrye.getState(VIKING_FEED) or "") ~= "" and VIKING or GENERIC
  if want == shown then return end          -- already drawing the right bars
  shown = want
  build(want)
end

-- Nothing is known at load — no MIP data has arrived — so start on the generic set,
-- which is correct for every guild but one and populates for a Viking too (their HP is
-- in the same place). The first prompt after the viking feed speaks swaps it. That is
-- one rebuild, once, seconds into a session.
apply()

-- The prompt is the cheapest place to notice: one state read, and a rebuild only when
-- the answer actually changes. Guild membership does not change mid-session, so in
-- practice this fires once per character and then does nothing.
--
-- Switching characters on the same connection is covered: the client clears character.*
-- and vik.* when it sees a fresh login and re-sends the MIP handshake, so the feed goes
-- quiet and this swaps back to the generic bars before the new character's data lands.
scrye.onPrompt(apply)
