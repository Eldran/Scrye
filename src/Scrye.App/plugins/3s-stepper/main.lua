-- ============================================================
-- 3S Stepper - Scrye port of ThreeS_Stepper (MUSHclient)
-- Walks a recorded route through an area, killing the mobs it meets.
-- Prompt-driven: =S= arms the next step, =M= arms a kill, the prompt acts.
-- Includes the path recorder, so new areas can be captured in-game.
-- ============================================================
-- NOTE: dropped / changed vs the original:
--  * The miniwindow (areabot window, tabs, hotspot buttons, movewindow,
--    utils.inputbox) is gone - Scrye owns panel chrome. Controls are
--    clickable text; the inputbox prompts became aliases.
--  * The deadman/idle switch is NOT here. It belongs in the client, not in
--    one plugin; bot_stop() is the entry point for it to call later.
--  * Party auto-capture from the in-game party table is not ported. 'pa'/'pr'
--    remain, and the list is mirrored into the world variable "party" so the
--    chaos-sea plugin sees it - that is how the two were wired originally.
--  * The recorder cannot write 3s_areas.lua (no plugin filesystem access).
--    Recorded areas persist in scrye.store instead, and 'stepexport <name>'
--    prints the Lua block so it can still be pasted into the source file.
--  * Move capture used OnPluginCommand, which Scrye has no equivalent for.
--    Instead the 23 movement words become pass-through aliases, registered
--    the first time you record. They SEND FIRST and record after, so a bug
--    in the recorder can never stop you walking.
--  * No os.time / DoAfterSpecial: post-kill re-probe and auto-resume use
--    scrye.after (seconds).
--  * The Windows ding.wav call became scrye.sound("beep").
-- ============================================================

local P = "plugin." .. scrye.id .. "."

-- ---------- bundled areas ----------
-- The 22 routes and 156 mob mappings live in areas.json, declared in plugin.json's
-- "data" map and handed to us by the host as a table. It stays a real file you can
-- edit and diff in the repo instead of 13 KB wedged into this script.
local AREAS = scrye.data and scrye.data.areas or nil
if type(AREAS) ~= "table" then
  AREAS = {}
  scrye.print("[bot] areas.json did not load - only recorded areas are available")
end

-- ---------- helpers ----------

-- Markup-escape anything the MUD gave us. Mob names land in panel text, and a
-- literal '@' there would otherwise be read as a colour token.
local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end

-- Pad BEFORE colouring: '#' counts markup characters, so colouring first and
-- padding after mis-aligns every column by the length of the escape.
local function padesc(s, n)
  s = tostring(s or "")
  local pad = n - #s
  return esc(s) .. string.rep(" ", pad > 0 and pad or 0)
end

local function col(c, s) return "@{" .. c .. "}" .. esc(s) .. "@{}" end

local function note(s)  scrye.print("[bot] " .. s) end
local function dnote(s) scrye.print("[bot] " .. s) end

local dbg_on = false
local function dbg(s) if dbg_on then scrye.print("[bot dbg] " .. s) end end

-- reverse of each movement direction, for walking the route backwards
local REV = {
  n = "s", s = "n", e = "w", w = "e", u = "d", d = "u",
  ne = "sw", sw = "ne", nw = "se", se = "nw",
  north = "south", south = "north", east = "west", west = "east",
  up = "down", down = "up",
  northeast = "southwest", southwest = "northeast",
  northwest = "southeast", southeast = "northwest",
}

-- words the recorder treats as a route step when you type them
local REC_MOVES = {
  n=1, s=1, e=1, w=1, u=1, d=1, ne=1, nw=1, se=1, sw=1,
  north=1, south=1, east=1, west=1, up=1, down=1,
  northeast=1, northwest=1, southeast=1, southwest=1,
  out=1, enter=1, leave=1,
}

-- strip {..}/[..] status tags and trailing punctuation so a stored key matches
-- the live line whether or not the mob is carrying conditions.
local function mob_norm(s)
  return (tostring(s):gsub("%s*%b{}", ""):gsub("%s*%b[]", ""):gsub("[%.%s]+$", ""))
end

-- ---------- state ----------

local bot = {
  active = false,
  area = nil, cfg = nil,
  path = {}, pos = 1,
  armed = false,           -- next prompt -> walk
  mob_pending = nil,       -- mob seen this room, engage on next prompt
  paused_on_mob = false,
  user_paused = false,
  foundmob = false,        -- set on engage, reset per lap; kept from the original
                           -- (nothing reads it yet - it is what a "stop when a lap
                           -- finds nothing" rule would hang off)
  stacknum = 0,
  nohome = false, hardmode = false,
  loop = false,
  return_path = nil, return_sent = 0,
  current_mob = nil,
  wimpy_warned = false,
  autoresume = true,
}
local flags = { player = false }
local party = {}             -- lowercased names that do NOT count as "a player is here"
local rec = nil              -- active recording: { name=, path={}, mobs={}, kill= }
local move_aliases_added = false

-- ---------- area storage ----------
-- AREAS is the bundled set (ported from 3s_areas.lua). Anything you record goes
-- into scrye.store and is layered on top at load, so a recorded area with the
-- same name wins - same precedence the original had once it rewrote the file.

local recorded = {}          -- name -> area, from the store

local function split_path(s)
  local t = {}
  for w in tostring(s or ""):gmatch("%S+") do t[#t + 1] = w end
  return t
end

local function area_get(name)
  return recorded[name] or AREAS[name]
end

local function area_names()
  local seen, t = {}, {}
  for k in pairs(AREAS)   do seen[k] = true end
  for k in pairs(recorded) do seen[k] = true end
  for k in pairs(seen) do t[#t + 1] = k end
  table.sort(t)
  return t
end

-- One store key per area, line-oriented rather than serialized Lua: the sandbox
-- has no load(), and a flat format survives a half-written value without taking
-- the whole plugin down with a syntax error.
local function store_area(name, a)
  local out = {
    "p=" .. table.concat(a.p and split_path(a.p) or {}, " "),
    "noloop=" .. (a.noloop or 0),
    "nohome=" .. (a.nohome or 0),
    "hard=" .. (a.hard or 0),
  }
  if a.dflt then out[#out + 1] = "dflt=" .. a.dflt end
  local mn = {}
  for m in pairs(a.mobs or {}) do mn[#mn + 1] = m end
  table.sort(mn)
  for _, m in ipairs(mn) do out[#out + 1] = "m=" .. m .. "\t" .. a.mobs[m] end
  scrye.store.set("area." .. name, table.concat(out, "\n"))
  local list = scrye.store.get("recorded") or ""
  if not (" " .. list .. " "):find(" " .. name .. " ", 1, true) then
    scrye.store.set("recorded", (list == "" and name or (list .. " " .. name)))
  end
end

local function load_recorded()
  local list = scrye.store.get("recorded") or ""
  local n = 0
  for name in list:gmatch("%S+") do
    local blob = scrye.store.get("area." .. name)
    if blob then
      local a = { p = "", mobs = {} }
      for line in blob:gmatch("[^\n]+") do
        local k, v = line:match("^(%w+)=(.*)$")
        if k == "p" then a.p = v
        elseif k == "noloop" then a.noloop = tonumber(v) or 0
        elseif k == "nohome" then a.nohome = tonumber(v) or 0
        elseif k == "hard"   then a.hard   = tonumber(v) or 0
        elseif k == "dflt"   then a.dflt   = v
        elseif k == "m" then
          local mob, kill = v:match("^(.-)\t(.*)$")
          if mob and mob ~= "" then a.mobs[mob] = kill end
        end
      end
      recorded[name] = a
      n = n + 1
    end
  end
  return n
end

-- ---------- panel ----------

local function state_word()
  if rec then return col("warning", "RECORDING " .. rec.name) end
  if not bot.active then return col("dim", "idle") end
  if bot.return_path then return col("accent", "returning to start") end
  if bot.user_paused then return col("warning", "paused") end
  if bot.paused_on_mob then return col("error", "fighting " .. (bot.current_mob or "?")) end
  return col("success", "running")
end

local function draw()
  -- status
  local head
  if bot.active then
    head = string.format("%s  %s  %s",
      col("accent", bot.area or "?"),
      col("dim", string.format("step %d/%d", math.min(bot.pos, #bot.path), #bot.path)),
      state_word())
  else
    head = state_word()
  end
  scrye.setState(P .. "status", head)

  -- controls, as clickable words rather than a button row
  local c = {}
  if bot.active then
    c[#c + 1] = "@{success,click=..}Step@{}"
    c[#c + 1] = bot.user_paused and "@{success,click=.resume}Resume@{}"
                                or "@{warning,click=.pause}Pause@{}"
    c[#c + 1] = "@{accent,click=.reset}Reset@{}"
    c[#c + 1] = "@{accent,click=.tostart}Return@{}"
    c[#c + 1] = "@{error,click=killbot}Stop@{}"
    c[#c + 1] = "@{dim,click=.set loop " .. (bot.loop and "off" or "on") .. "}loop:"
                .. (bot.loop and "on" or "off") .. "@{}"
  else
    c[#c + 1] = "@{success,click=.resume}Resume saved@{}"
    c[#c + 1] = col("dim", "or pick an area below")
  end
  scrye.setState(P .. "controls", "  " .. table.concat(c, "    "))

  -- area list: click a name to start it, four per line
  local names, lines, row = area_names(), {}, {}
  for _, n in ipairs(names) do
    local mark = recorded[n] and "*" or ""
    -- padding sits OUTSIDE the link so the gaps between names are dead space:
    -- a stray click on a bot panel should not start walking an area
    local label = n .. mark
    row[#row + 1] = string.format("@{accent,click=- %s}%s@{}%s",
      n, esc(label), string.rep(" ", math.max(1, 16 - #label)))
    if #row == 4 then lines[#lines + 1] = table.concat(row); row = {} end
  end
  if #row > 0 then lines[#lines + 1] = table.concat(row) end
  if #lines == 0 then lines[1] = col("dim", "no areas") end
  scrye.setState(P .. "areas", table.concat(lines, "\n"))

  -- recorder
  local r = {}
  if rec then
    local nm = 0
    for _ in pairs(rec.mobs) do nm = nm + 1 end
    r[#r + 1] = string.format("%s  %s  %s",
      col("warning", "recording '" .. rec.name .. "'"),
      col("dim", #rec.path .. " steps, " .. nm .. " mob types"),
      rec.kill and col("success", "kill: " .. rec.kill)
               or col("error", "no kill word - 'record kill <word>'"))
    r[#r + 1] = "  @{success,click=record save}Save@{}    @{warning,click=record undo}Undo@{}"
             .. "    @{error,click=record cancel}Cancel@{}"
    if #rec.path > 0 then
      local tail = {}
      for i = math.max(1, #rec.path - 24), #rec.path do tail[#tail + 1] = rec.path[i] end
      r[#r + 1] = col("dim", "  ..." .. table.concat(tail, " "))
    end
  else
    r[#r + 1] = col("dim", "not recording - 'record <name>' starts, then just walk the route")
  end
  scrye.setState(P .. "rec", table.concat(r, "\n"))
end

-- ---------- persistence ----------

local function save_position()
  scrye.store.set("save_area", bot.area or "")
  scrye.store.set("save_pos", tostring(bot.pos))
  scrye.store.set("bot_active", bot.active and "1" or "0")
end

local function save_party()
  local t = {}
  for n in pairs(party) do t[#t + 1] = n end
  table.sort(t)
  scrye.store.set("party", table.concat(t, "\n"))
  -- world variable, so the chaos-sea plugin's whitelist sees the same names.
  -- The MUSHclient versions were wired this way (it read our plugin variable).
  scrye.setVariable("party", table.concat(t, "\n"))
end

-- ---------- walking ----------

local bot_kill, bot_resume_step, bot_step, bot_stop

local function reset_room_flags() flags.player = false end

local function end_of_path()
  if bot.loop then
    note("end of path - looping back to the start")
    bot.pos = 1
    bot.foundmob = false
    -- a fresh glance, not a bare step: the start room's mob has to be seen again,
    -- otherwise every lap after the first walks straight past it
    bot_resume_step()
  else
    note("end of path - stopping" .. (bot.nohome and "" or ", heading home"))
    bot_kill()
    if not bot.nohome then scrye.send("go home") end
  end
end

bot_step = function()
  if not bot.active or bot.user_paused then return end
  if bot.pos > #bot.path then end_of_path() return end
  local dir = bot.path[bot.pos]
  bot.pos = bot.pos + 1
  scrye.send(dir)
  draw()
end

local function bot_path_undo()
  if rec then
    if #rec.path > 0 then
      local dropped = table.remove(rec.path)
      note("recording: move failed - dropped '" .. dropped .. "'")
      draw()
    end
    return
  end
  if not bot.active then return end
  if bot.return_path then return end   -- blocked-move undo is meaningless while returning
  bot.pos = math.max(1, bot.pos - 1)
  note("could not move - path position now " .. bot.pos .. "/" .. #bot.path)
  draw()
end

local function bot_advance_return()
  if not bot.return_path then return end
  if bot.return_sent < #bot.return_path then
    bot.return_sent = bot.return_sent + 1
    scrye.send(bot.return_path[bot.return_sent])
  else
    bot.return_path = nil
    bot.pos = 1
    bot.paused_on_mob = false
    bot.user_paused = true
    note("arrived at start - paused ('..' or Resume to run again)")
    draw()
  end
end

local function bot_to_start()
  if not bot.active then note("bot is not active") return end
  local steps = bot.pos - 1
  if steps <= 0 then note("already at the start") return end
  local rp, skipped = {}, 0
  for i = steps, 1, -1 do
    local back = REV[bot.path[i]]
    if back then rp[#rp + 1] = back else skipped = skipped + 1 end
  end
  if #rp == 0 then note("route has no reversible steps - can't auto-return") return end
  bot.mob_pending = nil
  bot.paused_on_mob = false
  bot.armed = false
  bot.return_path = rp
  bot.return_sent = 0
  note("returning to start: " .. #rp .. " steps back"
    .. (skipped > 0 and (" (" .. skipped .. " special steps skipped)") or ""))
  bot_advance_return()
  draw()
end

-- ---------- events ----------

local function on_room()
  if not bot.active then return end
  if bot.return_path then return end   -- prompts drive the return walk
  if bot.paused_on_mob then
    if not bot.wimpy_warned then
      bot.wimpy_warned = true
      note("WIMPY! Moved during the fight - bot stopped, position saved.")
      note("Walk back to where you fought, then '.resume' to continue.")
      bot_stop()
    end
    return
  end
  bot.wimpy_warned = false
  reset_room_flags()
  dbg("=S= room -> clearing mob_pending, arming"
    .. (bot.mob_pending and ("  (DROPPED pending kill '" .. esc(bot.mob_pending) .. "')") or ""))
  bot.mob_pending = nil
  bot.armed = true
end

local function in_party(name)
  local low = (name or ""):lower()
  for member in pairs(party) do
    if low:find(member, 1, true) then return true end
  end
  return false
end

local function on_player(name)
  if not bot.active then return end
  if in_party(name) then return end
  flags.player = true
end

local function on_mob(name)
  if rec then
    local clean = mob_norm(name)
    if #clean > 0 and not rec.mobs[clean] then
      rec.mobs[clean] = true
      note("recording: mob noted - " .. esc(clean))
      draw()
    end
    return
  end
  if not bot.active or bot.paused_on_mob or bot.return_path or not bot.cfg then
    dbg("=M= '" .. esc(name) .. "' IGNORED (active=" .. tostring(bot.active)
      .. " paused_on_mob=" .. tostring(bot.paused_on_mob)
      .. " returning=" .. tostring(bot.return_path ~= nil) .. ")")
    return
  end
  -- match the normalised name against normalised keys, so a live
  -- 'A cave troll {angry} [scratched].' still matches a stored 'A cave troll'
  local clean = mob_norm(name)
  local entry = bot.cfg.mobs and (bot.cfg.mobs[name] or bot.cfg.mobs[clean])
  if not entry and bot.cfg.mobs then
    for k, v in pairs(bot.cfg.mobs) do
      local ck = mob_norm(k)
      if ck ~= "" and (clean == ck or clean:sub(1, #ck) == ck) then entry = v break end
    end
  end
  local kill
  if entry then
    -- a table entry may carry hard=1 (only fought in hardmode); a plain string is the kill word
    if type(entry) == "table" then
      if entry.hard == 1 and not bot.hardmode then return end
      kill = entry.kill
    else
      kill = entry
    end
  elseif bot.cfg.dflt then
    kill = bot.cfg.dflt
  end
  dbg("=M= '" .. esc(name) .. "' -> matched=" .. tostring(entry ~= nil)
    .. " kill=" .. tostring(kill) .. (kill and "" or "  (NO MATCH, no default)"))
  if kill then bot.mob_pending = kill end
end

local function on_prompt()
  if not bot.active then return end
  if bot.user_paused then return end
  if bot.return_path then bot_advance_return() return end
  if bot.mob_pending then
    local mob = bot.mob_pending
    bot.mob_pending = nil
    if flags.player then
      note("Found player. skipping!")
      bot_step()
      return
    end
    bot.armed = false
    bot.paused_on_mob = true
    bot.foundmob = true
    save_position()
    bot.current_mob = mob
    scrye.send("kill " .. mob)
    if bot.stacknum > 0 then
      bot.stacknum = bot.stacknum - 1
      note("Stacking " .. bot.stacknum .. " more.")
      bot.paused_on_mob = false
      bot.armed = true
      bot_step()
    end
    draw()
    return
  end
  if bot.armed then
    bot.armed = false
    bot_step()
  end
end

-- ---------- commands ----------

local function bot_start(area, quiet_pos)
  local cfg = area_get(area)
  if not cfg then note("unknown area '" .. tostring(area) .. "' (.areas to list)") return end
  bot.area = area
  bot.cfg = cfg
  bot.path = split_path(cfg.p)
  bot.pos = 1
  bot.loop     = (cfg.noloop ~= 1)     -- loop by default unless the area says otherwise
  bot.nohome   = (cfg.nohome == 1)
  bot.hardmode = (cfg.hard == 1)
  bot.foundmob = false
  bot.paused_on_mob = false
  bot.user_paused = false
  bot.active = true
  reset_room_flags()
  if quiet_pos then bot.pos = math.min(quiet_pos, #bot.path) end
  save_position()
  note(("botting '%s' - %d steps%s. '..' to step, .stop to stop.")
    :format(area, #bot.path, quiet_pos and (", resumed at " .. bot.pos) or ""))
  bot_resume_step()
  draw()
end

bot_resume_step = function()
  if not bot.active then note("bot is not active") return end
  bot.user_paused = false
  bot.paused_on_mob = false
  bot.mob_pending = nil
  bot.armed = false
  scrye.send("!glance")
  draw()
end

local function bot_pause()
  if not bot.active then note("bot is not active") return end
  bot.user_paused = true
  bot.mob_pending = nil
  bot.armed = false
  note("paused at " .. bot.pos .. "/" .. #bot.path)
  draw()
end

bot_stop = function()
  if not bot.active then note("bot is not active") return end
  save_position()
  bot.paused_on_mob = true
  bot.armed = false
  note("stopped. position " .. bot.pos .. "/" .. #bot.path .. " saved (.resume / .dcr to continue)")
  draw()
end

bot_kill = function()
  bot.active = false
  bot.paused_on_mob = false
  bot.user_paused = false
  bot.armed = false
  bot.mob_pending = nil
  bot.return_path = nil
  save_position()
  note("bot killed")
  draw()
end

local function bot_resume_saved()
  local area = scrye.store.get("save_area") or ""
  local pos  = tonumber(scrye.store.get("save_pos") or "") or 1
  if area == "" or not area_get(area) then note("nothing to resume") return end
  bot_start(area, pos)
end

local function bot_reset()
  if not bot.active then note("bot is not active") return end
  bot.pos = 1
  bot.user_paused = false
  save_position()
  note("reset to step 1")
  bot_resume_step()
end

-- ---------- recorder ----------

-- Registered the first time you record, not at load: a player who never records
-- never has their movement keys routed through this plugin at all.
local function add_move_aliases()
  if move_aliases_added then return end
  move_aliases_added = true
  for word in pairs(REC_MOVES) do
    scrye.addAlias{
      pattern = "^" .. word .. "$", regex = true,
      run = function()
        -- SEND FIRST. If anything below throws, you still moved.
        scrye.send(word)
        if rec then
          rec.path[#rec.path + 1] = word
          note("recording: step " .. #rec.path .. " = " .. word)
          draw()
        end
      end,
    }
  end
end

local function rec_status()
  if not rec then
    note("not recording. 'record <name>' starts; the route + mobs are captured as you walk.")
    return
  end
  local nm = 0
  for _ in pairs(rec.mobs) do nm = nm + 1 end
  note(string.format("recording '%s': %d steps, %d mob types, kill word: %s",
    rec.name, #rec.path, nm, rec.kill or "(not set - 'record kill <word>')"))
  if #rec.path > 0 then note("route so far: " .. table.concat(rec.path, " ")) end
end

local function rec_start(name)
  name = name:lower()
  if name == "status" then rec_status() return end
  if rec then note("already recording '" .. rec.name .. "' - 'record save' or 'record cancel' first") return end
  if not name:match("^[a-z_]%w*$") then
    note("invalid area name '" .. name .. "' - letters/digits/underscore, no spaces")
    return
  end
  if bot.active then bot_kill() end
  add_move_aliases()
  rec = { name = name, path = {}, mobs = {}, kill = nil }
  note("RECORDING area '" .. name .. "'. Walk the route; moves are captured.")
  note("Special steps: 'r: open door'   Set kill word: 'record kill troll'")
  note("'record undo' drops the last step, 'record save' stores it, 'record cancel' discards.")
  draw()
end

local function rec_special(cmd)
  if not rec then note("not recording - 'record <name>' first") scrye.send(cmd) return end
  scrye.send(cmd)
  rec.path[#rec.path + 1] = cmd
  note("recording: step " .. #rec.path .. " = '" .. cmd .. "'")
  draw()
end

local function rec_undo()
  if not rec then note("not recording") return end
  if #rec.path == 0 then note("recording: nothing to undo") return end
  local dropped = table.remove(rec.path)
  note("recording: dropped '" .. dropped .. "' (" .. #rec.path .. " steps left)")
  draw()
end

local function rec_setkill(word)
  if not rec then note("not recording") return end
  rec.kill = word
  note("recording: kill word = " .. word)
  draw()
end

local function rec_cancel()
  if not rec then note("not recording") return end
  note("recording '" .. rec.name .. "' discarded")
  rec = nil
  draw()
end

local function rec_save()
  if not rec then note("not recording") return end
  if #rec.path == 0 then note("recording: no steps - walk the route first (or 'record cancel')") return end
  local kill = rec.kill or "CHANGEME"
  local a = { p = table.concat(rec.path, " "), noloop = 0, nohome = 1, hard = 0, mobs = {} }
  local n = 0
  for m in pairs(rec.mobs) do a.mobs[m] = kill; n = n + 1 end
  if area_get(rec.name) then note("note: '" .. rec.name .. "' existed - the new recording takes over") end
  recorded[rec.name] = a
  store_area(rec.name, a)
  note(("saved '%s': %d steps, %d mob types"):format(rec.name, #rec.path, n))
  if kill == "CHANGEME" then
    note("WARNING: no kill word was set - 'record kill <word>' next time, or re-record")
  end
  note("'stepexport " .. rec.name .. "' prints it as Lua for pasting into 3s_areas.lua")
  rec = nil
  draw()
end

-- The original wrote straight into 3s_areas.lua. We cannot, so this prints the
-- same block: the recording is usable immediately from the store either way,
-- and this keeps the source file reachable for anyone who wants it there.
local function rec_export(name)
  local a = area_get(name)
  if not a then note("unknown area '" .. tostring(name) .. "'") return end
  local function q(s) return '"' .. tostring(s):gsub('\\', '\\\\'):gsub('"', '\\"') .. '"' end
  local steps = {}
  for _, s in ipairs(split_path(a.p)) do steps[#steps + 1] = q(s) end
  local b = {}
  b[#b + 1] = "  " .. name .. " = {"
  b[#b + 1] = "    path = { " .. table.concat(steps, ", ") .. " },"
  b[#b + 1] = ("    no_loop = %d, no_home = %d, hardmode = %d,")
    :format(a.noloop or 0, a.nohome or 0, a.hard or 0)
  local mn = {}
  for m in pairs(a.mobs or {}) do mn[#mn + 1] = m end
  table.sort(mn)
  if #mn > 0 then
    b[#b + 1] = "    mobs = {"
    for _, m in ipairs(mn) do
      local k = a.mobs[m]
      b[#b + 1] = "      [" .. q(m) .. "] = { kill = " .. q(type(k) == "table" and k.kill or k) .. " },"
    end
    b[#b + 1] = "    },"
  end
  b[#b + 1] = "  },"
  for _, line in ipairs(b) do scrye.print(line) end
end

-- ---------- wiring ----------

scrye.addTrigger{ pattern = [[^=S=(.*)=S=]], regex = true, run = function() on_room() end }
scrye.addTrigger{ pattern = [[^(?:=M= ?|\[MONSTAR!\])(.+)$]], regex = true,
  run = function(name) on_mob(name) end }
scrye.addTrigger{ pattern = [[^(?:=P= ?|\[PLAYAR!\])(.+)$]], regex = true,
  run = function(name) on_player(name) end }
scrye.addTrigger{ pattern = [[^You cannot go (\w+)\.$]], regex = true,
  run = function() bot_path_undo() end }
scrye.addTrigger{ pattern = [[^You are unable to penetrate the wall that]], regex = true,
  run = function() bot_path_undo() end }
scrye.addTrigger{ pattern = [[collapses, unblocking the escape routes\.]], regex = true,
  run = function() if bot.active then scrye.send("!glance") end end }
scrye.addTrigger{ pattern = [[^There is no (.+) here\.$]], regex = true,
  run = function(name)
    if bot.active and not bot.user_paused and bot.paused_on_mob and bot.autoresume
       and name == bot.current_mob then
      scrye.after(1, function()
        -- re-check at fire time: pausing during the delay has to stick
        if bot.active and not bot.user_paused then bot_resume_step() end
      end)
    end
  end }
scrye.addTrigger{ pattern = [[dealt the killing blow to (.+)\.]], regex = true,
  run = function()
    -- re-attack the same target: another of the same mob here gets engaged,
    -- otherwise "There is no X here." fires above and the bot walks on
    if bot.active and not bot.user_paused and bot.paused_on_mob then
      scrye.after(1, function()
        if bot.active and not bot.user_paused and bot.paused_on_mob and bot.current_mob then
          scrye.send("kill " .. bot.current_mob)
        end
      end)
    end
  end }

scrye.onPrompt(function() on_prompt() end)

scrye.addAlias{ pattern = [[^(?:-|walker )\s*(\w+)$]], regex = true,
  run = function(a) bot_start(a) end }
scrye.addAlias{ pattern = [[^\.\.$]],     regex = true, run = function() bot_resume_step() end }
scrye.addAlias{ pattern = [[^\.stop$]],   regex = true, run = function() bot_stop() end }
scrye.addAlias{ pattern = [[^\.pause$]],  regex = true, run = function() bot_pause() end }
-- The original's .resume always restarted from the SAVED position, which after a
-- plain .pause meant jumping back to wherever the last mob was engaged. Here it
-- continues from where you actually are when the bot is running, and only falls
-- back to the saved position when it is not - which is what Pause/Resume implies.
scrye.addAlias{ pattern = [[^\.resume$]], regex = true, run = function()
  if bot.active then bot_resume_step() else bot_resume_saved() end
end }
scrye.addAlias{ pattern = [[^\.dcr$]],    regex = true, run = function() bot_resume_saved() end }
scrye.addAlias{ pattern = [[^\.reset$]],  regex = true, run = function() bot_reset() end }
scrye.addAlias{ pattern = [[^\.tostart$]], regex = true, run = function() bot_to_start() end }
scrye.addAlias{ pattern = [[^killbot$]],  regex = true, run = function() bot_kill() end }
scrye.addAlias{ pattern = [[^\.stack (\d+)$]], regex = true, run = function(n)
  bot.stacknum = tonumber(n) or 0
  note("Stacking up to " .. bot.stacknum .. " mobs on next move")
end }
scrye.addAlias{ pattern = [[^\.set (\w+) (on|off)$]], regex = true, run = function(opt, val)
  local on = (val == "on")
  if     opt == "hardmode"   then bot.hardmode = on
  elseif opt == "autoresume" then bot.autoresume = on
  elseif opt == "loop"       then bot.loop = on
  else note("unknown option '" .. opt .. "' (autoresume hardmode loop)") return end
  note(opt .. ": " .. val)
  draw()
end }
scrye.addAlias{ pattern = [[^\.areas$]], regex = true, run = function()
  local n = area_names()
  note(#n .. " areas: " .. table.concat(n, " "))
end }
scrye.addAlias{ pattern = [[^\.dbg (on|off)$]], regex = true, run = function(v)
  dbg_on = (v == "on")
  scrye.store.set("dbg", dbg_on and "1" or "0")
  note("debug " .. v)
end }
scrye.addAlias{ pattern = [[^\.binfo$]], regex = true, run = function()
  scrye.print([[
3S Stepper
  - <area> / walker <area>   start botting (.areas to list)
  ..            step/resume        .pause   pause          .stop   stop + save
  .resume/.dcr  resume saved       .reset   back to step 1 .tostart walk back
  killbot       stop completely    .stack <n>  stack mobs
  .set <opt> on|off              autoresume | hardmode | loop
  pa/pr <name>  party add/remove (a party member is not "a player is here")
Recorder
  record <name>   start        r: <cmd>       record a non-movement step
  record kill <w> kill word    record undo    drop last step
  record save     store it     record cancel  discard      record  status
  stepexport <name>            print an area as Lua for 3s_areas.lua]])
end }

scrye.addAlias{ pattern = [[^pa (.+)$]], regex = true, run = function(n)
  party[n:lower()] = true; save_party(); note("party + " .. n)
end }
scrye.addAlias{ pattern = [[^pr (.+)$]], regex = true, run = function(n)
  party[n:lower()] = nil; save_party(); note("party - " .. n)
end }

-- record aliases: the specific forms must beat the generic 'record <name>',
-- so they are registered first (rules are matched in registration order).
scrye.addAlias{ pattern = [[^record$]], regex = true, run = function() rec_status() end }
scrye.addAlias{ pattern = [[^record (?:stop|save)$]], regex = true, run = function() rec_save() end }
scrye.addAlias{ pattern = [[^record cancel$]], regex = true, run = function() rec_cancel() end }
scrye.addAlias{ pattern = [[^record undo$]],   regex = true, run = function() rec_undo() end }
scrye.addAlias{ pattern = [[^record kill (\w+)$]], regex = true, run = function(w) rec_setkill(w) end }
scrye.addAlias{ pattern = [[^record ([A-Za-z_]\w*)$]], regex = true, run = function(n) rec_start(n) end }
scrye.addAlias{ pattern = [[^r: (.+)$]], regex = true, run = function(c) rec_special(c) end }
scrye.addAlias{ pattern = [[^stepexport (\w+)$]], regex = true, run = function(n) rec_export(n) end }

scrye.addPanel{
  title = "3S Stepper",
  tabs = {
    { title = "Bot", widgets = {
        { type = "text", bind = P .. "status" },
        { type = "text", bind = P .. "controls" },
        { type = "label", text = "Areas  (* = recorded)", color = "dim" },
        { type = "text", bind = P .. "areas" },
    } },
    { title = "Record", widgets = {
        { type = "text", bind = P .. "rec" },
        { type = "label", text = "record <name> to start, then walk. 'r: open door' for a non-move step.",
          color = "dim" },
    } },
  },
}

-- ---------- load ----------

dbg_on = (scrye.store.get("dbg") == "1")
local pty = scrye.store.get("party")
if pty then for n in pty:gmatch("[^\n]+") do party[n] = true end end
local nrec = load_recorded()
local nall = #area_names()
note(nall .. " areas loaded" .. (nrec > 0 and (" (" .. nrec .. " recorded)") or "")
  .. " - .areas to list, .binfo for help")
draw()

scrye.onDisconnect(function()
  if bot.active then save_position() end
end)

-- The client's idle guard says nobody is at the keyboard. This is the deadman switch the
-- MUSHclient version implemented itself and reached into the chaos-sea plugin to enforce;
-- now the client owns the clock and every plugin gets told. Stop, keep the position: coming
-- back should be '.resume', not a bot that quietly kept walking while you were gone.
scrye.onIdle(function()
  if rec then return end
  if bot.active and not bot.paused_on_mob then
    scrye.print("[bot] idle guard fired - stopping. '.resume' when you are back.")
    bot_stop()
  end
end)
