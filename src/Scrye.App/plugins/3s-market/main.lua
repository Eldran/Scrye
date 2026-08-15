-- 3S Market -- DEPRECATED, merged into 3S Viking Status.
--
-- The market scanner, the arbitrage table and the auto-trader all read the same vik.* feed
-- the Viking Status panel reads -- carts, warehouse stock, daler -- so the two were separate
-- windows onto one settlement. They are now the Trade, Trade Auto and Trade Log tabs of that
-- panel, unchanged: same scan, same clickable rows, same auto-trader, same settings.
--
-- Every command is the same: mkref, mkdispatch, mkunits, markwin, atrade and its
-- sub-commands.
--
-- This stub exists because the plugin shipped enabled in v1.0.0. Deleting the folder would
-- have made the 3S Market panel vanish with no explanation and left a dead entry in the
-- enabled-plugins list of every character that had it on. It is scheduled for removal a
-- release or two after 1.0 -- once nobody is upgrading across the merge.
--
-- It deliberately registers NOTHING: no aliases, no triggers, no panel, no watches, no
-- timers, no auto-trader driver. That matters more here than it did for the build planner:
-- with both copies live, two auto-traders would watch the same feed and dispatch the same
-- carts, and the vtrade scan would run twice with both gagging each other's output.
--
-- NOTE for anyone upgrading: the scanned prices and the auto-trader settings lived in this
-- plugin's private store and the merged copy reads Viking Status's, so the first run after
-- the merge starts from defaults. Run 'mkref' once and set the auto-trader up again; it is
-- a one-time cost of the move, not a bug.

scrye.print("@{#AC811E,bold}[market]@{} The market scanner and auto-trader have moved into "
  .. "@{accent}3S Viking Status@{} -> Trade / Trade Auto / Trade Log tabs. Same commands "
  .. "(@{dim}mkref, mkdispatch, atrade ...@{}).")
scrye.print("@{#AC811E,bold}[market]@{} @{dim}Enable 3S Viking Status if it is off, then "
  .. "disable this plugin. Your saved prices and auto-trader settings do not carry over -- "
  .. "run 'mkref' once and re-check the Trade Auto tab.@{}")
