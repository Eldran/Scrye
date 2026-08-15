-- 3S Build Planner -- DEPRECATED, merged into 3S Viking Status.
--
-- The planner read the same vik.* feed the Viking Status panel reads and drew the same
-- buildings with the same tier palette, so it was a second window onto one dataset. It now
-- lives in that panel's Builds tab, where the rows sit beside the construction queue they
-- describe. Every command is unchanged: build, build all, build refresh, build scan,
-- build start <name>.
--
-- This stub exists because the plugin shipped enabled in v1.0.0. Deleting the folder would
-- have made the Build Planner panel vanish with no explanation and left a dead entry in the
-- enabled-plugins list of every character that had it on. It is scheduled for removal a
-- release or two after 1.0 -- once nobody is upgrading across the merge.
--
-- It deliberately registers NOTHING: no aliases, no triggers, no panel, no watches, no
-- timers. That is the whole point. Both plugins define the same five build aliases, and
-- with two live registrations the second would shadow the first -- so the safe state for a
-- character with both enabled is that this one is inert.

scrye.print("@{accent,bold}[build]@{} The build planner has moved into "
  .. "@{accent}3S Viking Status@{} -> Builds tab. Same commands "
  .. "(@{dim}build, build all, build scan, build start <name>@{}).")
scrye.print("@{accent,bold}[build]@{} @{dim}Enable 3S Viking Status if it is off, then "
  .. "disable this plugin -- it does nothing else now.@{}")
