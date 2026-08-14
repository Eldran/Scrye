-- 3S Vitals — the fight-glance bars, and nothing else.
--
-- Extracted from 3S Viking Status: these gauges bind straight to global state the
-- MIP feed keeps current (character.health.*, vik.*, enemy.*), so they never needed
-- the big plugin's machinery — and in their own panel they can sit small next to
-- the output while the full Viking Status panel stays closed or parked elsewhere.
--
-- There is deliberately NO code here beyond the panel: no triggers, no timers,
-- no state writes. The host keeps every bound widget live.

scrye.addPanel{
  title = "Vitals",
  width = 240,
  accent = "#D855B8",          -- signature: vitals rose (validated accent set)
  widgets = {
    -- dim = true: the bar darkens as the value drops (green base for stats)
    { type = "gauge", text = "HP",   value = "character.health.current", max = "character.health.max", dim = true },
    { type = "gauge", text = "Seid", value = "vik.seid", max = "vik.mseid", dim = true },
    { type = "gauge", text = "Vig",  value = "vik.vig",  max = "vik.mvig",  dim = true },
    { type = "gauge", text = "Rad",  value = "vik.rad",  max = "vik.mrad",  dim = true },
    { type = "value", text = "Enemy: ", bind = "enemy.name", color = "error" },
    { type = "gauge", text = "Enemy", value = "enemy.health", max = 100, dim = true, color = "error" },
  },
}
