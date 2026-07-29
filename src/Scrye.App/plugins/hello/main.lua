-- Sample Scrye plugin. Everything a plugin can do today, in miniature.
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
