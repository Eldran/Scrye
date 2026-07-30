-- Sample Scrye plugin. A tour of the plugin API, in miniature.
-- Loaded automatically for every world (mudIds "*"). Its output appears in green.

scrye.print("loaded — I react to 'welcome' lines and watch your HP")

-- React to incoming lines (observe-only for now).
scrye.onLine(function(line)
    if line:lower():find("welcome") then
        scrye.print("saw a welcome line")
    end
end)

-- React to a GMCP package (fires only on MUDs that send GMCP).
scrye.onGmcp("Char.Vitals", function(json)
    scrye.print("Char.Vitals: " .. json)
end)

-- Watch a structured-state path. On 3Scapes this is fed from MIP vitals.
scrye.watch("character.health.current", function(hp)
    local max = scrye.getState("character.health.max")
    scrye.print("HP " .. hp .. (max ~= "" and ("/" .. max) or ""))
end)

-- Lifecycle hooks (Phase 1).
scrye.onConnect(function() scrye.print("connected — hello plugin ready") end)
scrye.onDisconnect(function() scrye.print("disconnected") end)

-- Timers (Phase 1). after() fires once; every() repeats until cancelled.
scrye.after(3, function() scrye.print("3s after load — timers work") end)

-- Example of a repeating timer + cancel (kept off by default so it doesn't spam):
-- local n, id = 0
-- id = scrye.every(10, function()
--     n = n + 1
--     scrye.print("heartbeat " .. n)
--     if n >= 3 then scrye.cancel(id) end
-- end)
