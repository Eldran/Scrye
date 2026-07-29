-- MIP Vitals HUD — the first bundled *panel* plugin (Foundation D).
-- No drawing code: it declares a panel of widgets bound to structured-state paths.
-- MudSession mirrors 3Scapes MIP vitals into character.* / enemy.* / combat.*,
-- so these bars update live as the game state changes.

scrye.addPanel({
    title = "Vitals",
    widgets = {
        { type = "progress", text = "HP", value = "character.health.current", max = "character.health.max" },
        { type = "progress", text = "SP", value = "character.spell.current",  max = "character.spell.max"  },
        { type = "value",    text = "Enemy: ", bind = "enemy.name" },
        { type = "value",    text = "Round: ", bind = "combat.round" },
    }
})
