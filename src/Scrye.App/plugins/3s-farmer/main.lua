-- 3s-farmer — the area farming bot's chassis (docs/Plan-Map-Farmer.md, phase 2).
--
-- Phase 2 built the FEET: lock to the area you are standing in, patrol its explored rooms
-- one confirmed step at a time, and stop for everything a walk would stop for. Phase 3
-- adds the FISTS, ported from the chaos-sea bot's proven loop and adapted: the target is
-- whatever monster Room.Contents says is in the room, minus the area's excludes and minus
-- anything belonging to the party (which is how your own guards are protected - put your
-- own name on the party list and "A warband in service to <you>" stops being a target).
-- A player in the room who is not party means hands off everything and walk on. Kill
-- timing reads the client's own combat state (enemy.name), not delays guessed from text.
-- Phase B (Plan-Map-Farmer-Round2, 2 Sep) gave it EYES: the server's line-of-sight grid
-- (Room.Map) names, through the farmer's own graph, which fenced rooms hold monsters and
-- which hold players, and the patrol goes where the mobs are and not where the people
-- are; the mapper's map.room feed closes the arrival race; 'farm after' loots; and the
-- HUD says what it is doing and why.
--
-- Three design decisions carried in from the plan:
--
--   * ITS OWN EYES. It builds a featherweight graph of its own from Room.Info - the same
--     feed arrives at every plugin for free - because plugin stores are private and it
--     cannot read the mapper's. A room enters the graph only when you STAND in it, which
--     makes the graph the record of what has actually been explored.
--
--   * ITS OWN FEET, VERIFIED BY NUMBER. One step at a time, and the arrival must be the
--     room number the graph promised. Outside the Sea of Chaos every room has an identity,
--     so there is no dead reckoning and nothing to drift: a step either lands where the
--     graph said or the patrol stops and says so.
--
--   * THE FENCE IS THE GRAPH. The patrol only ever takes an exit whose destination is a
--     room it has stood in, in the locked area. Explore first, then farm - the workflow is
--     the boundary. Locking to 'Unknown' is refused outright: that label names every
--     stretch of connective realm on the MUD, not a place.
--
-- What stops the patrol, and stays stopped: a move you typed, an arrival nothing ordered
-- (a wimpy, a teleport, anything that moved us), a step that lands in the wrong room, a
-- step that never lands, 'farm stop'. What merely PAUSES it: combat - fighting is this
-- bot's eventual job, so a fight is waited out (phase 3 will join in), and the patrol
-- resumes when Char.Combat says it is over. That resume is the one deliberate exception to
-- "nothing resumes on its own", and it exists because a farmer in an aggro area that
-- retired at the first bite would never farm anything.

local DIRS = { n = "s", s = "n", e = "w", w = "e", ne = "sw", sw = "ne", nw = "se",
               se = "nw", u = "d", d = "u",
               north = "south", south = "north", east = "west", west = "east",
               up = "down", down = "up" }

local G = {}              -- graph: num -> { area, name, exits = {dir->dest}, last }
local S = {               -- the run
  on = false,             -- patrolling
  paused = false,         -- 'farm pause' - the hand brake, distinct from combat
  lock = nil,             -- area name locked at start
  fighting = false,       -- Char.Combat says so
  step = nil,             -- { dir, to, at } - the one outstanding step
  visited = 0,            -- rooms stepped into this run (panel)
  timer = nil,            -- the pending pace timer, so stop can cancel it
  started = 0,            -- clock stamp when this patrol began (session counter)
  ended = nil,            -- final length of the LAST patrol, frozen at stop
}
local C = {               -- knobs (persisted)
  pace = 1,               -- seconds between arrival and the next step
  timeout = 10,           -- seconds a step may stay unanswered
  breath = 2,             -- seconds after a killing blow, so YOUR looting triggers go first
  rest_below = 0,         -- Seid floor; 0 = off
  rest_secs = 30,         -- how long to sit when below it
  hp_start = 50,          -- HP% floor for STARTING a fight or a step; 0 = off
  hp_panic = 0,           -- HP% under which a fight is abandoned outright; 0 = off
  panic_cmd = "",         -- one command sent at the panic floor ("flee", "wimpy"...); "" = none
  hunt_wait = 8,          -- seconds a 'kill' may go unanswered before the name is given up
  after_cmd = "",         -- one command sent after each killing blow's breath ("get all from corpse")
}
local X = {}              -- excludes: area -> { "name fragment", ... } (persisted)
local PT = {}             -- party: real player names (lowercase fragments) who are not
                          -- strangers - their presence does not park the fists (persisted)
local NV = {}             -- the never-list: mob names never attacked ANYWHERE (persisted).
                          -- This is where guild followers go - a warband and its cousins
                          -- are mob-typed, follow you across every area, and drift into
                          -- the room a round behind you, changing Room.Contents when they
                          -- do. Per-area excludes cannot cover something that goes
                          -- everywhere you go; this list can.
local B = {               -- the current room's population and the fight (never persisted)
  mobs = {},              -- monster full names, from Room.Contents, freshest wins
  count = {},             -- name -> how many of it Room.Contents listed
  los = nil,              -- what the last Room.Map showed: { mobs = {num=true}, players = {num=true} }
  player = false,         -- a non-party player is here: hands off, walk on
  seen = false,           -- Room.Contents has spoken for this room (absence proves nothing)
  parked = false,         -- the player-present note was said for this population
  hunting = false,        -- committed to clearing this room before patrolling on
  target = nil,           -- { name, kw } of the mob last attacked
  hunt_at = 0,            -- clock stamp of the last 'kill' sent, for the hunt watchdog
  skip = {},              -- names given up on in THIS room: a 'kill' nothing answered
  kwidx = {},             -- name -> which keyword candidate is being tried
  kills = 0,
  resting = false,
  rest_until = 0,
}
local T = {               -- travel ('farm go', phase 4 - never persisted)
  going = nil,            -- the area we asked the mapper to walk us to
  quiet = nil,            -- the nobody-answered timer, cancelled by any walk event
}
local here = nil          -- num of the room we are standing in (tracked even when off)
local now = 1            -- starts at ONE: stamp 0 is reserved for "not visited this run",
                         -- so the room you log in to (stamped before the first clock tick)
                         -- still counts as visited
local P = "plugin.3s-farmer."
-- World-truth - the explored graph, the excludes, the party, the never-list - lives in the
-- MUD-shared store (scrye.shared, API 1.14) when the host offers one, so every character on
-- this MUD farms off the same knowledge: explore on one, farm on another, and 'farm never
-- warband' said once protects them all. Character-ish knobs (pace, the Seid rest floor)
-- stay in scrye.store. On an older host WORLD degrades to the private store.
local WORLD = scrye.shared or scrye.store
local draw                -- defined with the panel

local function note(s) scrye.print("[farm] " .. s) end

-- ---------- persistence ----------
local dirty = false
local function save()
  if not dirty then return end
  dirty = false
  local rooms = {}
  for num, r in pairs(G) do
    rooms[#rooms + 1] = { num = num, area = r.area, name = r.name, exits = r.exits, last = r.last }
  end
  WORLD.set("graph", scrye.json.encode({ rooms = rooms }))
  WORLD.set("excludes", scrye.json.encode(X))
  WORLD.set("party", scrye.json.encode(PT))
  WORLD.set("never", scrye.json.encode(NV))
  scrye.store.set("pace", tostring(C.pace))
  scrye.store.set("rest", C.rest_below .. " " .. C.rest_secs)
  scrye.store.set("hp", C.hp_start .. " " .. C.hp_panic)
  scrye.store.set("panic_cmd", C.panic_cmd)
  scrye.store.set("after_cmd", C.after_cmd)
end

local function load()
  -- One-time migration: data saved before scrye.shared existed sits in the private store.
  -- Adopt anything the shared store does not have yet; the private copies stay as backups
  -- (nothing writes them again).
  if scrye.shared then
    for _, key in ipairs({ "graph", "excludes", "party", "never" }) do
      if scrye.shared.get(key) == nil then
        local old = scrye.store.get(key)
        if old ~= nil then scrye.shared.set(key, old) end
      end
    end
  end
  local ok, data = pcall(scrye.json.decode, WORLD.get("graph") or "")
  if ok and type(data) == "table" and type(data.rooms) == "table" then
    for _, r in ipairs(data.rooms) do
      local num = tonumber(r.num)
      if num then
        -- last = 0, NEVER the saved stamp. The clock is session-local (it starts at zero
        -- every load), so a saved stamp is meaningless here - and loading it was a live
        -- bug: last session's rooms came back stamped in the hundreds, this session's
        -- walking stamped rooms near zero, and "smallest stamp first" then orbited only
        -- the rooms touched this session, forever. A loaded room has not been visited
        -- THIS run, and zero says exactly that.
        G[num] = { area = tostring(r.area or ""), name = tostring(r.name or ""),
                   exits = type(r.exits) == "table" and r.exits or {},
                   last = 0 }
      end
    end
  end
  local ok2, ex = pcall(scrye.json.decode, WORLD.get("excludes") or "")
  if ok2 and type(ex) == "table" then X = ex end
  local ok3, pt = pcall(scrye.json.decode, WORLD.get("party") or "")
  if ok3 and type(pt) == "table" then PT = pt end
  local ok4, nv = pcall(scrye.json.decode, WORLD.get("never") or "")
  if ok4 and type(nv) == "table" then NV = nv end
  C.pace = tonumber(scrye.store.get("pace")) or C.pace
  local rb, rs = tostring(scrye.store.get("rest") or ""):match("^(%d+) (%d+)$")
  if rb then C.rest_below, C.rest_secs = tonumber(rb), tonumber(rs) end
  local hs, hpn = tostring(scrye.store.get("hp") or ""):match("^(%d+) (%d+)$")
  if hs then C.hp_start, C.hp_panic = tonumber(hs), tonumber(hpn) end
  C.panic_cmd = tostring(scrye.store.get("panic_cmd") or "")
  C.after_cmd = tostring(scrye.store.get("after_cmd") or "")
end

-- ---------- the fight ----------
-- Combat truth, from the best feed available. The client's enemy.name state is fed by
-- MIP as well as GMCP - and 3Scapes runs them TOGETHER (verified live 29 Aug; the 25 Aug
-- "one or the other" belief was wrong) - but a no-MIP character has only GMCP, and there
-- whether a terminal Char.Combat
-- arrives at the kill, and what it carries, is unverified. So the raw stream this plugin
-- already reads is tracked with a shelf life: attacker + live rounds = fighting, and
-- silence longer than CC_STALE means the fight is over whatever the last payload said.
-- The state read stays as the fallback for a character still on MIP without GMCP.
local CC = { seen = false, active = false, at = 0 }
local CC_STALE = 8

local function in_combat()
  if CC.seen then
    return CC.active and (now - CC.at) < CC_STALE
  end
  local e = scrye.getState("enemy.name")
  return e ~= nil and e ~= ""
end

-- Seid, from the best feed available. Guild.State (the Viking GMCP, live 27 Aug) carries
-- it as points.vitka - the number in the status line's S[cur|max] slot - and the MIP
-- vik.* state is dead on a GMCP world, so the GMCP number wins once seen. The vik.*
-- reads stay as the fallback for a MIP character. (vitka = Seid is read off the live
-- capture where points.vitka tracked S[..] exactly; if resting ever floors on the wrong
-- number, this mapping is the first thing to re-check.)
local seid_gmcp = nil
scrye.onGmcp("Guild.State", function(json)
  local ok, t = pcall(scrye.json.decode, json)
  if not ok or type(t) ~= "table" then return end
  local p = t.points
  if type(p) == "table" and tonumber(p.vitka) then seid_gmcp = tonumber(p.vitka) end
end)

local function get_seid()
  if seid_gmcp then return seid_gmcp end
  local v = tonumber(scrye.getState("vik.seid"))
  if v then return v end
  local ser = scrye.getState("vik.ser") or ""
  return tonumber(ser:match("%f[%w]SEID=(%d+)"))
end

-- HP as a percent, from the best feed available: Char.Vitals (GMCP) files hp/maxhp
-- under char.vitals.*, MIP under character.health.*. nil when neither has spoken -
-- and nil is NOT "fine": a floor with no number behind it refuses to run (see start()).
local function get_hp()
  local cur, max = tonumber(scrye.getState("char.vitals.hp")), tonumber(scrye.getState("char.vitals.maxhp"))
  if not cur or not max then
    cur, max = tonumber(scrye.getState("character.health.current")), tonumber(scrye.getState("character.health.max"))
  end
  if not cur or not max or max <= 0 then return nil end
  return math.floor(cur * 100 / max)
end

-- Why we are not fit to start a fight or a step right now, or nil. One gate for every
-- floor - Seid (guild-specific, the original) and HP (any guild) - so the patrol and the
-- prompt agree on it: a mob in the room is not attacked at 20% HP any more than the next
-- room is walked into.
local function unfit()
  if C.rest_below > 0 then
    local seid = get_seid()
    if seid and seid < C.rest_below then
      return string.format("Seid low (%s < %d)", tostring(seid), C.rest_below)
    end
  end
  if C.hp_start > 0 then
    local hp = get_hp()
    if hp and hp < C.hp_start then
      return string.format("HP low (%d%% < %d%%)", hp, C.hp_start)
    end
  end
  return nil
end

local function in_party(name)
  local low = tostring(name or ""):lower()
  for _, member in ipairs(PT) do
    if low:find(member, 1, true) then return true end
  end
  return false
end

local function is_excluded(name)
  local low = tostring(name or ""):lower()
  for _, frag in ipairs(NV) do
    if low:find(frag, 1, true) then return true end
  end
  for _, frag in ipairs(S.lock and X[S.lock] or {}) do
    if low:find(frag, 1, true) then return true end
  end
  return false
end

-- The words the MUD's parser might take for a name, best first: strip the bracketed
-- tags, drop the little words that are never a noun, and take the plain words from the
-- END - the noun is usually last ("Small cur" -> cur, small; "Blob-like abhorrence" ->
-- abhorrence, blob, like). A name of the shape "<something> to <owner>" is two halves,
-- and the owner is the wrong half to swing at: "A warband in service to Goran" ->
-- warband, goran. Tried in order on "There is no X here"; when the last one fails the
-- mob leaves the roster. Letters only, so the property "every kill is 'kill <word>'"
-- holds.
local STOP = { the = true, of = true, a = true, an = true, ["in"] = true, to = true, ["and"] = true,
               with = true, at = true, on = true, ["for"] = true, service = true, some = true }
local function keywords(name)
  local n = tostring(name or ""):gsub("%b[]", ""):gsub("%b{}", ""):gsub("%b()", ""):lower()
  local head, tail = n:match("^(.-)%s+to%s+(.*)$")
  local out, seen = {}, {}
  local function take(part)
    local words = {}
    for w in part:gmatch("%a+") do
      if not STOP[w] and not seen[w] then seen[w] = true; words[#words + 1] = w end
    end
    for i = #words, 1, -1 do out[#out + 1] = words[i] end
  end
  if head then take(head); take(tail) else take(n) end
  return out
end

-- the candidate being tried for a name, or nil once they are all used up
local function keyword(name)
  local list = keywords(name)
  local i = B.kwidx[name] or 1
  return list[i], #list
end

-- The next mob worth killing, from the freshest roster: not excluded, not the party's,
-- not given up on in this room.
local function next_target()
  for _, name in ipairs(B.mobs) do
    if not is_excluded(name) and not in_party(name) and not B.skip[name] then
      local kw = keyword(name)
      if kw then return name, kw end
    end
  end
  return nil
end

-- ---------- the patrol ----------
local function in_area(num)
  local r = G[num]
  return r ~= nil and S.lock ~= nil and r.area == S.lock
end

-- Breadth-first over the fenced graph. Collects every reachable in-area room and the first
-- step toward each, in one pass - target choice and routing come out of the same search.
local function survey()
  if not here or not in_area(here) then return nil end
  local first, order = {}, {}
  local seen, queue, qi = { [here] = true }, { here }, 1
  while queue[qi] do
    local at = queue[qi] ; qi = qi + 1
    for dir, dest in pairs(G[at].exits) do
      dest = tonumber(dest)
      if dest and not seen[dest] and in_area(dest) then
        seen[dest] = true
        first[dest] = (at == here) and dir or first[at]
        order[#order + 1] = dest
        queue[#queue + 1] = dest
      end
    end
  end
  return order, first
end

-- The next room worth standing in: the reachable in-area room we have not stood in for the
-- longest - and among equally-stale rooms, the NEAREST, which BFS order gives for free
-- (survey() visits rooms in increasing distance, so the first room carrying the minimum
-- stamp is the closest one carrying it). The old lowest-number tie-break marched a fresh
-- patrol across the whole area to its lowest-numbered corner first - from Smurfette's
-- house that is four rooms of walking before anything nearby gets visited, which watched
-- live looks exactly like an orbit. Nearest-first spreads from where you stand.
-- Room.Map (phase B): the server draws a line-of-sight grid on every arrival, 'm' where a
-- neighbouring room holds monsters and 'p' where it holds players, several rooms out.
-- The grid has no numbers, but it has GEOMETRY: rooms sit two cells apart with a link
-- glyph between, and the direction of each hop is an exit of the room we came from - so
-- from '@', following the links and pairing each hop with G[num].exits[dir] names the
-- room in every cell the graph can name. Cells the graph cannot name are left alone: the
-- patrol never walks anywhere the graph does not know.
local LINKS = { e = { 1, 0, "-" }, w = { -1, 0, "-" }, n = { 0, -1, "|" }, s = { 0, 1, "|" },
                ne = { 1, -1, "/" }, sw = { -1, 1, "/" }, nw = { -1, -1, "\\" }, se = { 1, 1, "\\" } }
local ROOM_GLYPH = { O = true, m = true, p = true, ["^"] = true, v = true, ["+"] = true, E = true, ["@"] = true }

local function los_scan(rows)
  if type(rows) ~= "table" or not here or not G[here] then return nil end
  local at_r, at_c
  for r, line in ipairs(rows) do
    local c = line:find("@", 1, true)
    if c then at_r, at_c = r, c ; break end
  end
  if not at_r then return nil end
  local function cell(r, c)
    local line = rows[r]
    if not line or c < 1 or c > #line then return " " end
    return line:sub(c, c)
  end
  local out = { mobs = {}, players = {} }
  local seen = { [here] = true }
  local queue, qi = { { num = here, r = at_r, c = at_c } }, 1
  while queue[qi] do
    local cur = queue[qi] ; qi = qi + 1
    for dir, lk in pairs(LINKS) do
      local dest = tonumber(G[cur.num] and G[cur.num].exits[dir])
      if dest and dest ~= 0 and not seen[dest] then
        local mid = cell(cur.r + lk[2], cur.c + lk[1])
        local g = cell(cur.r + 2 * lk[2], cur.c + 2 * lk[1])
        if (mid == lk[3] or mid == "X") and ROOM_GLYPH[g] then
          seen[dest] = true
          if g == "m" then out.mobs[dest] = true end
          if g == "p" then out.players[dest] = true end
          queue[#queue + 1] = { num = dest, r = cur.r + 2 * lk[2], c = cur.c + 2 * lk[1] }
        end
      end
    end
  end
  return out
end

local function on_room_map(json)
  local ok, t = pcall(scrye.json.decode, json)
  if not ok or type(t) ~= "table" then return end
  B.los = los_scan(t.rows)
end

-- The next room worth standing in. First choice: the NEAREST reachable in-area room the
-- line-of-sight map shows monsters in - go where the mobs are, instead of walking every
-- room to find out. A room it shows players in is skipped (walk in, park, walk out is
-- worse than not walking in). With nothing in sight the patrol falls back to the room we
-- have not stood in for the longest - and among equally-stale rooms, the NEAREST, which
-- BFS order gives for free (survey() visits rooms in increasing distance, so the first
-- room carrying the minimum stamp is the closest one carrying it).
local function pick_target()
  local order, first = survey()
  if not order then return nil end
  local los = B.los
  if los then
    for _, num in ipairs(order) do
      if los.mobs[num] then return num, first[num], "mobs in sight" end   -- a cell is 'm' or 'p', never both
    end
  end
  local best, best_last = nil, nil
  for _, num in ipairs(order) do
    local l = G[num].last
    if not (los and los.players[num]) and (best_last == nil or l < best_last) then
      best, best_last = num, l
    end
  end
  if not best then return nil end
  return best, first[best]
end

local function stop_patrol(why)
  if not S.on then return end
  S.ended = now - (S.started or now)   -- freeze the session clock at its final length
  S.on = false ; S.step = nil
  B.hunting = false ; B.target = nil   -- no patrol, no hunt: the HUD shows no target
  if S.timer then scrye.cancel(S.timer) ; S.timer = nil end
  if why then note(why .. " - patrol stopped. 'farm start' starts it again") end
  draw()
end

local step_out   -- forward: the rest timer below resumes into it

local function rest_over()
  if not B.resting then return end
  local why = unfit()
  if why then
    B.rest_until = now + C.rest_secs
    note(why .. " still - resting " .. C.rest_secs .. "s more")
    scrye.after(C.rest_secs, rest_over)
    return
  end
  B.resting = false
  note("recovered - patrol resuming")
  if S.on and not S.paused then
    if S.timer then scrye.cancel(S.timer) end
    S.timer = scrye.after(C.pace, function() S.timer = nil ; step_out() end)
  end
  draw()
end

-- Sit out a floor: nothing is sent until rest_over finds us fit again.
local function begin_rest(why)
  B.resting = true
  B.rest_until = now + C.rest_secs
  note(why .. " - resting " .. C.rest_secs .. "s before going on")
  scrye.after(C.rest_secs, rest_over)
  draw()
end

-- Why the patrol is standing still, in words, or nil when it is not. Read by the panel
-- (the 'Waiting' row), by 'farm', and by step_out when a tick finds nothing to do - so a
-- patrol that is not moving always says why, instead of sitting silently on a roster it
-- will not fight (Joakim, 16 Sep 2026: 'Wiremouth guard - next', and nothing happened).
local consider            -- the attack decision; defined with the prompt, used by the tick
local function blocker()
  if not S.on then return nil end
  if S.paused then return "paused" end
  if B.resting then return "resting" end
  if S.fighting then return "in combat (Char.Combat)" end
  if B.hunting then return "hunting " .. (B.target and B.target.name or "?") end
  if in_combat() then
    return "in combat (enemy.name = '" .. tostring(scrye.getState("enemy.name")) .. "')"
  end
  local name = next_target()
  if name and B.player then return "a player is here - leaving " .. name .. " alone" end
  if name then return "about to attack " .. name end
  if S.step then return "stepping " .. S.step.dir end
  return nil
end

-- A tick that cannot step does not go quiet for good: it re-arms itself and says once
-- what it is waiting on. Every reason here clears through an event that reschedules
-- (combat ending, a kill, rest ending) - but a stale enemy.name, or a prompt that never
-- arrives, cleared nothing, and the patrol stood forever. Now it looks again.
local function hold(why)
  if why and why ~= S.held_on then S.held_on = why ; note("holding - " .. why) end
  S.timer = scrye.after(C.pace * 5, step_out)
end

step_out = function()
  S.timer = nil
  if not S.on or S.paused or S.step then return end
  if S.fighting or B.hunting or in_combat() then hold(blocker()) return end
  if next_target() ~= nil and not B.player then
    -- A fight is about to start. The prompt is where attacks are decided, but the tick
    -- is entitled to the same decision: a prompt the client never flagged (no telnet
    -- GA that time) must not leave a live target unfought and the patrol frozen.
    consider()
    if not B.hunting then hold(blocker()) end
    return
  end
  if B.resting then return end
  S.held_on = nil
  do
    local why = unfit()
    if why then begin_rest(why) return end
  end
  local target, dir, why_target = pick_target()
  if not target then
    -- A one-room area, or the fence has nothing else reachable. Not an error: sit here
    -- (phase 3 fights what respawns) and look again in a while.
    S.timer = scrye.after(C.pace * 5, step_out)
    return
  end
  if target == here then
    -- we are already the stalest room; wait for time to pass rather than jitter in place
    S.timer = scrye.after(C.pace * 5, step_out)
    return
  end
  -- The stalest reachable room already visited this run = a full circuit is done. Said
  -- once per patrol: from then on "the same rooms again" is the loop working, not stuck.
  if not S.swept and G[target].last > 0 then
    S.swept = true
    note(string.format("full circuit - every reachable %s room visited this run; looping", S.lock))
  end
  local dest = tonumber(G[here].exits[dir])
  S.step = { dir = dir, to = dest, at = now }
  S.led = why_target ~= nil
  scrye.send(dir)
  draw()
end

local function schedule_step()
  if S.timer then scrye.cancel(S.timer) end
  S.timer = scrye.after(C.pace, step_out)
end

-- ---------- the feed ----------
local function on_room_info(json)
  local ok, r = pcall(scrye.json.decode, json)
  if not ok or type(r) ~= "table" then return end
  local num = tonumber(r.num)
  if not num then return end
  local area = tostring(r.area or "")
  local exits = {}
  if type(r.exits) == "table" then
    for d, v in pairs(r.exits) do exits[tostring(d):lower()] = tonumber(v) or 0 end
  end

  local moved = (num ~= here)
  local explained = S.step ~= nil and S.step.to == num
  if moved then B.skip = {} ; B.kwidx = {} ; B.los = nil end   -- give-ups, keyword tries and the LOS picture are per room

  -- The roster is deliberately NOT touched on arrival. The server suppresses a
  -- Room.Contents whose payload is IDENTICAL to the last one it sent - so silence on
  -- crossing a threshold means "same population as the room you left", and the carried
  -- roster is exactly right: walk from one cur-room into another and the suppressed
  -- Contents is the server saying the cur is here too. A differing population always
  -- arrives and replaces the roster wholesale (on_room_contents rebuilds it). Nothing is
  -- cached about what to do with it either: the prompt asks the roster fresh every time.

  -- The graph learns from every arrival, on patrol or not: standing in a room is what
  -- makes it farmable later.
  G[num] = { area = area, name = tostring(r.name or ""), exits = exits, last = now }
  dirty = true

  if S.on and moved then
    if explained then
      here = num
      S.step = nil
      S.visited = S.visited + 1
      schedule_step()
    elseif S.step then
      here = num
      stop_patrol(string.format("stepped %s expecting %d but arrived in %d %s",
                                S.step.dir, S.step.to, num, tostring(r.name or "")))
    else
      -- Nothing ordered a move and we moved anyway: a wimpy, a teleport, a summon,
      -- someone dragging us. Whatever it was, the patrol is not in charge any more.
      here = num
      stop_patrol("moved by something that was not the patrol (" .. tostring(r.name or "") .. ")")
    end
  else
    here = num
  end
  draw()
end

-- ---------- the room's population (Room.Contents) ----------
-- Typed and named by the server, so no mob file exists anywhere: the target list IS the
-- feed. An empty payload ({ "items": [] }) is a positive statement of emptiness; no
-- payload at all is silence and proves nothing - the chaos-sea lesson, kept.
local function on_room_contents(json)
  local ok, info = pcall(scrye.json.decode, json)
  if not ok or type(info) ~= "table" then return end
  B.seen = true
  B.mobs = {}
  B.count = {}
  B.player = false
  if type(info.items) ~= "table" then return end
  for _, it in ipairs(info.items) do
    if type(it) == "table" then
      local name = tostring(it.name or "")
      local kind = tostring(it.type or ""):lower()
      if name ~= "" then
        if kind == "monster" then
          B.mobs[#B.mobs + 1] = name
          B.count[name] = tonumber(it.count) or 1
        elseif kind == "player" then
          if not in_party(name) then B.player = true end
        end
      end
    end
  end
  B.parked = false        -- a new population: the player-present note may be due again
  draw()                  -- the roster IS the panel's middle: show it as it arrives
end

-- ---------- the prompt: where attacks are decided ----------
-- The whole room has printed by the prompt, so we know by then whether a player is
-- standing here. A player who is not party means hands off EVERYTHING in the room -
-- their guards arrive typed as monsters, and a fight you start next to a stranger is a
-- fight you may not be starting alone.
local function attack()
  local name, kw = next_target()
  if not name then
    B.hunting = false
    if S.on and not S.paused then schedule_step() end
    return
  end
  B.target = { name = name, kw = kw }
  B.hunting = true
  B.hunt_at = now
  scrye.send("kill " .. kw)
  draw()
end

consider = function()
  if not S.on or S.paused or B.resting then return end
  if B.hunting or in_combat() then return end
  if not next_target() then return end
  if B.player then
    if not B.parked then
      B.parked = true
      note("a player is here - leaving the room's mobs alone, walking on")
      schedule_step()
    end
    return
  end
  do
    -- the same gate the patrol uses: a mob is not a reason to fight at 20% HP
    local why = unfit()
    if why then
      if S.timer then scrye.cancel(S.timer) ; S.timer = nil end
      begin_rest(why)
      return
    end
  end
  if S.timer then scrye.cancel(S.timer) ; S.timer = nil end   -- fight first, step later
  attack()
end
local function on_prompt() consider() end

-- ---------- after the blow ----------
-- Your own killing-blow triggers (looting, skinning) go first: a short breath, then the
-- roster - which the server refreshes itself, since a death CHANGES Room.Contents and a
-- changed payload is never suppressed - says whether anything is left to fight.
local resume_hunt
resume_hunt = function()
  if not (S.on and B.hunting) then return end
  if in_combat() then
    scrye.after(C.breath, resume_hunt)
    return
  end
  attack()   -- next mob from the fresh roster, or hunting ends and the patrol resumes
end

-- ---------- combat we did not start: wait it out ----------
-- Char.Combat active means something is on us - our own attack or an aggro mob's. Either
-- way no steps go out until it ends; when it ends and we were hunting, the hunt continues
-- (the killblow trigger usually gets there first; this is the net under it).
local function on_combat(json)
  local ok, c = pcall(scrye.json.decode, json)
  if not ok or type(c) ~= "table" then return end
  -- BOTH attacker and live rounds: a terminal that keeps the attacker's name but zeroes
  -- the rounds is a fight report, not a fight.
  local active = tostring(c.attacker or "") ~= "" and (tonumber(c.rounds) or 0) > 0
  CC.seen = true
  CC.active = active
  if active then CC.at = now end
  local was = S.fighting
  S.fighting = active
  if active and not was and S.on then
    if S.timer then scrye.cancel(S.timer) ; S.timer = nil end
    note("combat - patrol waiting for it to end")
  elseif was and not active and S.on and not S.paused then
    if B.hunting then
      scrye.after(C.breath, resume_hunt)
    else
      note("combat over - patrol resuming")
      schedule_step()
    end
  end
  draw()
end

-- ---------- excludes (stored now, enforced by the phase-3 kill loop) ----------
local function excludes_here() return S.lock and X[S.lock] or nil end

local function exclude(name)
  if not S.lock then note("no area locked - 'farm start' first, excludes are per area") return end
  name = name:lower()
  X[S.lock] = X[S.lock] or {}
  for _, e in ipairs(X[S.lock]) do
    if e == name then note("'" .. name .. "' is already excluded in " .. S.lock) return end
  end
  table.insert(X[S.lock], name)
  dirty = true ; save()
  note("excluded in " .. S.lock .. ": " .. name .. " (substring match, case-insensitive)")
  draw()
end

local function include(name)
  if not S.lock or not X[S.lock] then note("nothing is excluded here") return end
  name = name:lower()
  for i, e in ipairs(X[S.lock]) do
    if e == name then
      table.remove(X[S.lock], i)
      if #X[S.lock] == 0 then X[S.lock] = nil end
      dirty = true ; save()
      note("no longer excluded in " .. S.lock .. ": " .. name)
      draw()
      return
    end
  end
  note("'" .. name .. "' was not excluded here - 'farm excludes' lists what is")
end

-- ---------- commands ----------
local function area_rooms(name)
  local n = 0
  for _, r in pairs(G) do if r.area == name then n = n + 1 end end
  return n
end

local function start()
  if S.on then note("already patrolling - 'farm stop' first") return end
  if not here or not G[here] then
    note("we are not anywhere yet - walk one room and try again")
    return
  end
  local area = G[here].area
  if area == "" or area:lower() == "unknown" then
    note("refusing to lock to '" .. (area == "" and "?" or area) .. "' - that is not one place,")
    note("  it is every stretch of connective realm on the MUD. Farm a named area.")
    return
  end
  if (C.hp_start > 0 or C.hp_panic > 0) and get_hp() == nil then
    note("an HP floor is set (farm hp) but no HP feed has spoken - neither Char.Vitals nor MIP.")
    note("  refusing to start: a floor with no number behind it is no floor. 'farm hp 0 0' turns it off.")
    return
  end
  S.on = true ; S.paused = false ; S.lock = area ; S.visited = 0 ; S.step = nil
  B.skip = {} ; B.kwidx = {}
  S.swept = false
  S.held_on = nil
  S.started = now ; S.ended = nil
  B.kills = 0 ; B.hunting = false ; B.parked = false ; B.resting = false
  -- Ask the mapper what it knows about this area; its answer (map.area.rooms) arrives
  -- before this emit returns - events dispatch in-process - so the note below already
  -- counts the adopted rooms.
  scrye.emit("map.query.area", scrye.json.encode({ area = area }))
  note(string.format("patrolling %s - %d explored room(s). The fence is the explored map:",
                     area, area_rooms(area)))
  note("  only rooms you have stood in, only in this area. 'farm stop' stops it; so does")
  note("  moving yourself, or anything moving you.")
  schedule_step()
  draw()
end

-- 'farm go <area>': ask the mapper (map.goto) to walk us into the area, and start the
-- patrol on arrival. The farmer does not walk this itself - long-haul routing is the
-- mapper's craft, and the walk contract already reports exactly one of arrived/stopped.
-- The mapper picks the NEAREST room whose area matches what you typed (substring,
-- case-insensitive), so the patrol locks to whatever exact area you land in - 'farm go
-- smurf' can land in Smurfland 2 if that is closer, and the start note names it.
local function travel(area)
  if T.going then note("already traveling to '" .. T.going .. "' - 'farm stop' cancels it") return end
  if S.on then stop_patrol(nil) ; note("patrol stopped - traveling first") end
  T.going = area
  -- Armed BEFORE the ask: the mapper answers during the emit (events dispatch in-process),
  -- so by the time the emit returns, an answering mapper has already cancelled this. Only
  -- silence - no mapper loaded, or one too old to know map.goto - lets it fire.
  T.quiet = scrye.after(2, function()
    T.quiet = nil
    if T.going then
      T.going = nil
      note("no mapper answered - is the map plugin loaded?")
      draw()
    end
  end)
  note("asking the mapper for a walk to '" .. area .. "' - the patrol starts on arrival")
  scrye.emit("map.goto", scrye.json.encode({ area = area }))
  draw()
end

local function travel_answered()
  if T.quiet then scrye.cancel(T.quiet) ; T.quiet = nil end
end

local function status()
  note("phase 2 - the chassis: patrols, fences, waits out combat. The kill loop is phase 3.")
  if T.going then note("  traveling to '" .. T.going .. "' - the patrol starts on arrival") end
  if S.on then
    note(string.format("  patrolling %s%s%s%s - %d room(s), %d visited, %d kill(s) this run",
      S.lock, S.paused and " (PAUSED)" or "", S.fighting and " (in combat)" or "",
      B.resting and " (resting)" or "", area_rooms(S.lock), S.visited, B.kills))
    local why = blocker()
    if why then note("  waiting: " .. why) end
  elseif here and G[here] then
    note(string.format("  off. Standing in %d %s [%s] - %d explored room(s) in this area",
      here, G[here].name, G[here].area, area_rooms(G[here].area)))
  else
    note("  off, and not anywhere yet")
  end
  local rooms = 0 ; for _ in pairs(G) do rooms = rooms + 1 end
  note(string.format("  graph: %d room(s) stood in, across all areas", rooms))
  local ex = excludes_here()
  if ex then note("  excluded here: " .. table.concat(ex, ", ")) end
  local hp = get_hp()
  note(string.format("  HP %s - floors: start %s, panic %s%s",
    hp and (hp .. "%") or "unknown (no feed)",
    C.hp_start > 0 and (C.hp_start .. "%") or "off",
    C.hp_panic > 0 and (C.hp_panic .. "%") or "off",
    C.panic_cmd ~= "" and (" -> '" .. C.panic_cmd .. "'") or ""))
end

scrye.addAlias{
  pattern = "^farm(?:\\s+(.*))?$",
  regex = true,
  run = function(args)
    args = tostring(args or ""):gsub("^%s+", ""):gsub("%s+$", "")
    local verb, rest = args:match("^(%S*)%s*(.*)$")
    verb = verb:lower()
    if verb == "" or verb == "status" then status()
    elseif verb == "start" then start()
    elseif verb == "go" then
      if rest == "" then note("farm go <area>   travel there (the mapper walks), then lock and start")
      else travel(rest) end
    elseif verb == "stop" then
      local did = false
      if T.going then
        T.going = nil
        travel_answered()
        scrye.emit("map.stop", "{}")           -- take the mapper's walk down with the trip
        note("travel cancelled")
        did = true
      end
      if S.on then stop_patrol(nil) ; note("patrol stopped") ; did = true end
      if not did then note("not patrolling") end
    elseif verb == "pause" then
      if not S.on then note("not patrolling") return end
      S.paused = not S.paused
      if S.paused then
        if S.timer then scrye.cancel(S.timer) ; S.timer = nil end
        note("patrol PAUSED - 'farm pause' again to continue")
      else
        note("patrol resuming")
        schedule_step()
      end
      draw()
    elseif verb == "pace" then
      local n = tonumber(rest)
      if not n or n < 1 then note("farm pace <seconds> - currently " .. C.pace) return end
      C.pace = math.floor(n) ; dirty = true ; save()
      note("pace: one step every " .. C.pace .. "s (after each arrival)")
    elseif verb == "never" then
      if rest == "" then
        note(#NV > 0 and ("never attacked anywhere: " .. table.concat(NV, ", "))
                      or "never-list empty - 'farm never <name>' adds. Guild followers go here:")
        if #NV == 0 then note("  a warband (or any guild's cousin of one) is mob-typed and follows you everywhere") end
      elseif rest:sub(1, 1) == "-" then
        local who = rest:sub(2):lower():gsub("^%s+", "")
        for i, m in ipairs(NV) do if m == who then table.remove(NV, i) break end end
        dirty = true ; save() ; draw()
        note("never-list: " .. (#NV > 0 and table.concat(NV, ", ") or "(empty)"))
      else
        local who = rest:lower()
        for _, m in ipairs(NV) do if m == who then note("'" .. who .. "' is already on the never-list") return end end
        NV[#NV + 1] = who
        dirty = true ; save() ; draw()
        note("never attacked anywhere: " .. table.concat(NV, ", "))
      end
    elseif verb == "party" then
      if rest == "" then
        note(#PT > 0 and ("party (players who are not strangers): " .. table.concat(PT, ", "))
                      or "party list empty - 'farm party <name>' adds real player party members")
      elseif rest:sub(1, 1) == "-" then
        local who = rest:sub(2):lower():gsub("^%s+", "")
        for i, m in ipairs(PT) do if m == who then table.remove(PT, i) break end end
        dirty = true ; save()
        note("party: " .. (#PT > 0 and table.concat(PT, ", ") or "(empty)"))
      else
        PT[#PT + 1] = rest:lower()
        dirty = true ; save()
        note("party: " .. table.concat(PT, ", "))
      end
    elseif verb == "rest" then
      local below, secs = rest:match("^(%d+)%s+(%d+)$")
      if not below then
        note(string.format("farm rest <seid floor> <seconds>  (now: %s)",
             C.rest_below > 0 and (C.rest_below .. " for " .. C.rest_secs .. "s") or "off"))
        note("farm rest 0 0    switches it off")
      else
        C.rest_below, C.rest_secs = tonumber(below), math.max(5, tonumber(secs))
        if C.rest_below == 0 then note("resting off")
        else note(string.format("resting %ds whenever Seid drops under %d", C.rest_secs, C.rest_below)) end
        dirty = true ; save()
      end
    elseif verb == "hp" then
      local start_, panic = rest:match("^(%d+)%s+(%d+)$")
      if not start_ then start_ = rest:match("^(%d+)$") ; panic = start_ and tostring(C.hp_panic) end
      if not start_ then
        note(string.format("farm hp <start%%> [<panic%%>]   (now: start %s, panic %s)",
             C.hp_start > 0 and (C.hp_start .. "%") or "off", C.hp_panic > 0 and (C.hp_panic .. "%") or "off"))
        note("  under start%, no new fight and no step - it rests like the Seid floor; under panic% a")
        note("  fight is abandoned: patrol stopped, a notify, and 'farm panic <cmd>' sent if set. 0 = off")
      else
        C.hp_start, C.hp_panic = math.min(100, tonumber(start_)), math.min(100, tonumber(panic))
        dirty = true ; save()
        note(string.format("HP floors: start below %s, panic below %s",
             C.hp_start > 0 and (C.hp_start .. "%") or "off", C.hp_panic > 0 and (C.hp_panic .. "%") or "off"))
      end
    elseif verb == "after" then
      if rest == "" then
        note("farm after <command>   sent once after each killing blow's breath (now: "
             .. (C.after_cmd ~= "" and ("'" .. C.after_cmd .. "'") or "none") .. "); 'farm after -' clears")
      else
        C.after_cmd = rest == "-" and "" or rest
        dirty = true ; save()
        note(C.after_cmd ~= "" and ("after each kill: '" .. C.after_cmd .. "'") or "after-kill command cleared")
      end
    elseif verb == "panic" then
      if rest == "" then
        note("farm panic <command>   one command sent at the panic floor (now: "
             .. (C.panic_cmd ~= "" and ("'" .. C.panic_cmd .. "'") or "none") .. "); 'farm panic -' clears")
      else
        C.panic_cmd = rest == "-" and "" or rest
        dirty = true ; save()
        note(C.panic_cmd ~= "" and ("at the panic floor: '" .. C.panic_cmd .. "'") or "panic command cleared")
      end
    elseif verb == "exclude" and rest ~= "" then exclude(rest)
    elseif verb == "include" and rest ~= "" then include(rest)
    elseif verb == "excludes" then
      local ex = excludes_here()
      if ex then note("excluded in " .. S.lock .. ": " .. table.concat(ex, ", "))
      else note(S.lock and ("nothing excluded in " .. S.lock)
                        or "no area locked - excludes are per area") end
    elseif verb == "rooms" then
      -- The coverage report: what the fence holds, what the patrol can reach from here,
      -- and what it has visited this run. This is how "it only walks the same rooms" gets
      -- settled by numbers instead of by watching names scroll past - a patrolled area is
      -- MOSTLY re-walked spine on any circuit, which looks like an orbit from the outside.
      local area = S.lock or (here and G[here] and G[here].area) or nil
      if not area or area == "" then note("stand somewhere (or 'farm start') first") return end
      local reach = {}
      do
        local order = survey()
        for _, num in ipairs(order or {}) do reach[num] = true end
        if here and G[here] and G[here].area == area then reach[here] = true end
      end
      local nums = {}
      for num, r in pairs(G) do if r.area == area then nums[#nums + 1] = num end end
      table.sort(nums)
      local visited, total = 0, #nums
      note(string.format("%s - %d explored room(s):", area, total))
      for _, num in ipairs(nums) do
        local r = G[num]
        local state
        if not S.on or S.lock ~= area then
          state = ""
        elseif not reach[num] then
          state = "  NO PATH inside the fence"
        elseif r.last > 0 or num == here then
          state = string.format("  visited this run%s", num == here and " (here)" or "")
        else
          state = "  not yet this run"
        end
        if r.last > 0 or num == here then visited = visited + 1 end
        note(string.format("  %d  %s%s", num, r.name, state))
      end
      if S.on and S.lock == area then
        note(string.format("  visited %d of %d this run", visited, total))
      end
    elseif verb == "wipe" and rest:lower() == "yes" then
      G = {} ; here = nil ; dirty = true ; save()
      note("graph wiped - walk the area again to re-learn it")
      draw()
    elseif verb == "help" then
      note("farm start      lock to this area and patrol its explored rooms")
      note("farm go <area>  travel there (the mapper walks), then lock and start")
      note("farm stop       stop; so does moving yourself or anything moving you")
      note("farm pause      hand brake, toggles")
      note("farm pace <s>   seconds between arrival and the next step (now " .. C.pace .. ")")
      note("farm exclude <name>   never attack this here (phase 3) - substring match")
      note("farm include <name>   un-exclude")
      note("farm excludes   list this area's excludes")
      note("farm party [<name>|-<name>]   real players whose presence is not a stranger's")
      note("farm never [<name>|-<name>]   mobs never attacked anywhere - guild followers go here")
      note("farm rest <seid> <secs>       sit out low Seid between fights")
      note("farm hp <start%> [<panic%>]   no new fight or step under start%; abandon under panic%")
      note("farm panic <cmd>              the one command sent at the panic floor ('-' clears)")
      note("farm after <cmd>              sent after each killing blow's breath, e.g. get all from corpse ('-' clears)")
      note("farm rooms      this area's rooms: reachable, visited this run, or cut off")
      note("farm wipe yes   forget the graph")
    else
      note("don't know 'farm " .. args .. "' - 'farm help' lists what there is")
    end
  end,
}

-- ---------- what stops us ----------
scrye.onCommand(function(cmd)
  local word = tostring(cmd or ""):gsub("^%s+", ""):gsub("%s+$", ""):lower()
  if not DIRS[word] then return end
  if S.step and S.step.dir == word then return end   -- our own step echoing back
  if S.on then stop_patrol("you moved yourself") end
end)

scrye.addTrigger{ pattern = [[dealt the killing blow to (.+)\.]], regex = true, run = function()
  if not (S.on and B.hunting) then return end
  B.kills = B.kills + 1
  draw()
  scrye.after(C.breath, function()
    -- your own looting triggers went first, during the breath; the one command you
    -- asked for goes now, before the roster is consulted for the next mob
    if C.after_cmd ~= "" and S.on then scrye.send(C.after_cmd) end
    resume_hunt()
  end)
end }

scrye.addTrigger{ pattern = [[^There is no (.+) here\.$]], regex = true, run = function(what)
  -- We swung at a name the room no longer holds - it died to someone else, wandered off,
  -- or the roster was a beat stale. Drop it and consult the roster again.
  if not (S.on and B.hunting) then return end
  local gone = tostring(what or ""):lower()
  local t = B.target
  if t and t.kw == gone then
    -- the word we swung with is not one the parser takes for anything here. If the
    -- name has another word, try it next; when they are all spent the mob is not here
    -- under any name we can say, and it leaves the roster until the server refreshes it.
    local _, n = keyword(t.name)
    local i = (B.kwidx[t.name] or 1) + 1
    if i <= n then
      B.kwidx[t.name] = i
    else
      for j, name in ipairs(B.mobs) do if name == t.name then table.remove(B.mobs, j) break end end
    end
  else
    for i, name in ipairs(B.mobs) do
      if name:lower():find(gone, 1, true) then table.remove(B.mobs, i) break end
    end
  end
  attack()
end }

scrye.addTrigger{ pattern = [[^You cannot go ]], regex = true, run = function()
  -- The graph promised an exit the room does not have. Drop the edge so it is not
  -- promised twice, and carry on - by-number verification means we know exactly where
  -- we still are.
  if not S.step then return end
  local bad = S.step
  S.step = nil
  if here and G[here] then G[here].exits[bad.dir] = nil ; dirty = true end
  if S.on then
    note("refused going " .. bad.dir .. " - edge dropped, picking another way")
    schedule_step()
  end
end }

scrye.onDisconnect(function() stop_patrol(nil) ; save() end)
scrye.onIdle(function() stop_patrol("idle guard") end)

-- ---------- the clock ----------
scrye.every(1, function()
  now = now + 1
  if S.step and (now - S.step.at) > C.timeout then
    stop_patrol(string.format("step '%s' never landed - nothing arrived in %ds",
                              S.step.dir, C.timeout))
  end
  -- The hunt watchdog: a 'kill' that never became a fight - an unattackable NPC, a word
  -- the parser swallowed without a "There is no X here", a mob that left between roster
  -- and swing - would otherwise hold B.hunting forever and the patrol with it. Give the
  -- name up for this room and ask the roster again.
  if S.on and B.hunting and not in_combat() and C.hunt_wait > 0 and (now - B.hunt_at) > C.hunt_wait then
    local t = B.target
    if t then
      B.skip[t.name] = true
      note("'kill " .. t.kw .. "' went unanswered for " .. C.hunt_wait .. "s - giving up on "
           .. t.name .. " here")
    end
    attack()          -- the next name, or the hunt ends and the patrol steps on
  end
  -- The panic floor: a fight that is going wrong is not a fight to finish. Stop the
  -- patrol (nothing here resumes on its own), say so where the phone hears it, and send
  -- the one command the player chose for this moment - a flee or a wimpy moves us, which
  -- the arrival-nothing-ordered rule already treats as the end of the patrol.
  if S.on and C.hp_panic > 0 then
    local hp = get_hp()
    if hp and hp < C.hp_panic then
      local why = string.format("HP %d%% under the panic floor of %d%%", hp, C.hp_panic)
      stop_patrol(why)
      B.hunting = false
      scrye.notify("farm: " .. why)
      if C.panic_cmd ~= "" then scrye.send(C.panic_cmd) end
    end
  end
  do
    local hp = get_hp()
    scrye.setState(P .. "hp", hp and tostring(hp) or "")
  end
  if S.on then draw() end        -- the session clock on the panel ticks visibly
  -- No terminal Char.Combat ever arrived and the rounds stopped coming: the fight is
  -- over. One empty snapshot through the ordinary handler keeps a single code path for
  -- combat ending - the same resume logic, the same notes.
  if CC.seen and CC.active and (now - CC.at) >= CC_STALE then
    on_combat('{ "attacker": "", "rounds": 0 }')
  end
  if now % 30 == 0 then save() end
end)

-- ---------- the panel ----------
-- How long this farm session has run: live while patrolling, frozen at its final length
-- once stopped - "how long did that run go" should survive the stop that ends it.
local function session_secs()
  if S.on then return now - (S.started or now) end
  return S.ended or 0
end

local function session_clock()
  local s = session_secs()
  if s >= 3600 then return string.format("%d:%02d:%02d", s // 3600, (s % 3600) // 60, s % 60) end
  return string.format("%d:%02d", s // 60, s % 60)
end

-- The panel's MOOD: one word naming the state the widgets should be coloured for. The
-- widget set is fixed once a panel is built, so a colour change means rebuilding the panel
-- - done only when this word changes, never per tick (build_panel below).
local function mood()
  if T.going then return "travel" end
  if not S.on then return "off" end
  if S.fighting or B.hunting then return "fight" end
  if S.paused or B.resting then return "hold" end
  return "patrol"
end
local MOOD_COLOR = { travel = "accent", off = "dim", fight = "error", hold = "warning", patrol = "success" }
local build_panel      -- forward: defined with the panel below
local panel_mood = nil

-- The roster as the panel shows it: every monster Room.Contents listed, how many, and what
-- the farmer makes of it - the same verdict next_target() would reach, spelled out, so a
-- mob that is not being attacked says WHY on the HUD rather than in a 'farm status' dump.
local roster_names = {}   -- panel row index -> the mob's name as listed, for row clicks
local function roster_rows()
  local rows = {}
  roster_names = {}
  for _, name in ipairs(B.mobs) do
    roster_names[#rows + 1] = name
    local verdict
    if B.hunting and B.target and B.target.name == name then verdict = "fighting"
    elseif in_party(name) then verdict = "party"
    elseif is_excluded(name) then verdict = "excluded"
    elseif B.skip[name] then verdict = "gave up"
    elseif not keyword(name) then verdict = "no keyword"
    else verdict = "next" end
    rows[#rows + 1] = name .. "\t" .. tostring(B.count[name] or 1) .. "\t" .. verdict
  end
  if B.player then rows[#rows + 1] = "(a player is here)\t\thands off" end
  return table.concat(rows, "\n")
end

draw = function()
  local bits = {}
  if T.going then
    bits[#bits + 1] = "traveling to '" .. T.going .. "'"
  elseif S.on then
    bits[#bits + 1] = "PATROLLING " .. tostring(S.lock)
    if S.paused then bits[#bits + 1] = "paused" end
    if S.fighting then bits[#bits + 1] = "combat" end
    if B.resting then bits[#bits + 1] = "resting" end
  elseif here and G[here] then
    bits[#bits + 1] = string.format("off - %d %s [%s]", here, G[here].name, G[here].area)
  else
    bits[#bits + 1] = "off"
  end
  scrye.setState(P .. "status", table.concat(bits, "  -  "))
  scrye.setState(P .. "target", (B.hunting and B.target) and B.target.name or "-")
  scrye.setState(P .. "roster", roster_rows())
  local secs = session_secs()
  local kph = secs >= 60 and string.format("%.0f", B.kills * 3600 / secs) or "-"
  scrye.setState(P .. "counters", table.concat({
    "Session\t" .. session_clock(),
    "Rooms\t" .. tostring(S.visited),
    "Kills\t" .. tostring(B.kills),
    "Kills/h\t" .. kph,
    "Next room\t" .. (S.on and (S.led and "mobs in sight" or "stalest") or "-"),
    "Waiting\t" .. (blocker() or "-"),
  }, "\n"))
  local ex = excludes_here()
  scrye.setState(P .. "excludes", ex and ("excluded here: " .. table.concat(ex, ", ")) or "")
  local m = mood()
  if m ~= panel_mood and build_panel then panel_mood = m ; build_panel(m) end
end

-- The panel. Widget vocabulary per PanelSpec: `widgets` + `type` (the first version of
-- this panel used keys the host has never had - `layout`/`kind`/`children` - and the host
-- rendered NOTHING, silently; the harness cannot catch that, only eyes on the HUD can).
-- Rebuilt (same title = replaced) whenever the mood changes: the status line takes the
-- mood's colour and the Pause button reads "Resume" while paused. Buttons themselves have
-- no colour in the widget spec, so the line above them carries it.
build_panel = function(m)
  scrye.addPanel{
  title = "3S Farmer",
  widgets = {
    { type = "label", bind = P .. "status", color = MOOD_COLOR[m] or "dim" },
    { type = "buttonrow", buttons = {
      { text = "Start", action = function() start() end },
      { text = S.paused and "Resume" or "Pause", action = function()
          if not S.on then note("not patrolling") ; return end
          S.paused = not S.paused
          if S.paused then
            if S.timer then scrye.cancel(S.timer) ; S.timer = nil end
            note("patrol PAUSED - Pause again to continue")
          else
            note("patrol resuming")
            schedule_step()
          end
          draw()
        end },
      { text = "Stop", action = function()
          -- the same full stop the typed verb does: a trip in flight goes down too
          if T.going then
            T.going = nil ; travel_answered()
            scrye.emit("map.stop", "{}")
            note("travel cancelled")
          end
          if S.on then stop_patrol(nil) ; note("patrol stopped") end
          draw()
        end },
    } },
    { type = "gauge", text = "HP", value = P .. "hp", max = 100, dim = true },
    -- the enemy's health straight from Char.Combat (the vitals plugin binds the same path)
    { type = "gauge", text = "Enemy", value = "char.combat.attacker_hp", max = 100 },
    { type = "value", text = "Target: ", bind = P .. "target" },
    { type = "table", bind = P .. "roster", separator = "\t", columns = { "Mob", "N", "Verdict" },
      align = { "left", "right", "left" },
      -- Excluding from the roster itself: a left click on a mob row toggles it in THIS
      -- area's exclude list (the exact name, so the toggle finds its own entry again);
      -- right-click offers the never-list too, for the guild follower that turns up in
      -- every area. Every entry is the typed command, so 'farm excludes' / 'farm never'
      -- and the panel can never disagree about what is excluded.
      onRowClick = function(_, index)
        local name = roster_names[index]
        if not name then return end
        local low = name:lower()
        for _, e in ipairs(S.lock and X[S.lock] or {}) do
          if e == low then include(low) return end
        end
        exclude(low)
      end,
      onRowMenu = function(_, index)
        local name = roster_names[index]
        if not name then return end
        local low = name:lower()
        local here_x, never = false, false
        for _, e in ipairs(S.lock and X[S.lock] or {}) do if e == low then here_x = true end end
        for _, e in ipairs(NV) do if e == low then never = true end end
        return {
          here_x and { "Attack again here",      "farm include " .. low }
                 or { "Exclude here",           "farm exclude " .. low },
          never  and { "Allow everywhere again", "farm never -" .. low }
                 or { "Never attack anywhere",  "farm never " .. low },
        }
      end },
    { type = "list", bind = P .. "counters" },
    { type = "label", bind = P .. "excludes", color = "dim" },
  },
  }
end
build_panel("off") ; panel_mood = "off"


-- ---------- the mapper's memory (map.area.rooms) ----------
-- The farmer's own graph only grows while the farmer is loaded - but "explored" should
-- mean every room anybody stood in, including sessions before this plugin existed. That
-- record is the mapper's, and plugin stores are private, so at 'farm start' the farmer
-- ASKS: it emits map.query.area and the mapper answers with the area's rooms, exits
-- resolved. Seeded rooms arrive with last = 0 - never visited this run - so the patrol
-- heads for them first.
--
-- The mapper is the authority on WHERE a room is. A room already in G whose filed area
-- disagrees with the offer is CORRECTED - area, name and exits refreshed, only the
-- farmer's own visit stamp kept. This is the cure for the live 4-room-orbit's second
-- mechanism (Joakim, 25 Aug): an earlier adoption had persisted rooms under the right
-- numbers but the wrong (empty) area into the shared graph, so every later offer said
-- "already known" while the exact-area fence could not see them - poison that add-only
-- adoption could never heal. A room whose filed area already agrees is left alone: the
-- farmer's fresher copy (pruned exits and all) is never stomped by a matching offer.
scrye.on("map.area.rooms", function(data)
  local ok, t = pcall(scrye.json.decode, data)
  if not ok or type(t) ~= "table" or type(t.rooms) ~= "table" then return end
  local asked = tostring(t.area or "")
  local added, fixed, linked = 0, 0, 0
  local elsewhere = {}         -- offered rooms whose area is a different NAME: they matched
                               -- the mapper's substring search but not the exact fence
  for _, r in ipairs(t.rooms) do
    local num = tonumber(r.num)
    if num then
      -- The room's area, with a fallback: an answer that lost its per-room area in
      -- transit still only contains rooms that matched the area we ASKED about.
      local a = tostring(r.area or "")
      if a == "" then a = asked end
      local exits = {}
      if type(r.exits) == "table" then
        for d, v in pairs(r.exits) do exits[tostring(d):lower()] = tonumber(v) or 0 end
      end
      local g = G[num]
      if not g then
        G[num] = { area = a, name = tostring(r.name or ""), exits = exits, last = 0 }
        added = added + 1
      elseif g.area ~= a then
        g.area = a
        if tostring(r.name or "") ~= "" then g.name = tostring(r.name or "") end
        if next(exits) then g.exits = exits end
        fixed = fixed + 1
      else
        -- Same room, same area - but the mapper may have RESOLVED exits this copy has not:
        -- it fills destinations in by walking, where the server's own payload says 0 for
        -- some exits even outside the sea (live: Smurfland 1849's n reads 0, yet the
        -- mapper walked it and knows it is 2295 - without this merge that room is forever
        -- outside the fence). Only gaps are filled; a destination this graph already
        -- names is its own. (A 'You cannot go'-pruned exit can come back this way until
        -- the mapper forgets it too - one refused step per farm start, self-pruned again.)
        for d, v in pairs(exits) do
          if v ~= 0 and (tonumber(g.exits[d]) or 0) == 0 then
            g.exits[d] = v
            linked = linked + 1
          end
        end
      end
      if a ~= asked then elsewhere[a] = (elsewhere[a] or 0) + 1 end
    end
  end
  if added > 0 or fixed > 0 or linked > 0 then dirty = true ; save() end
  note(string.format("the mapper offered %d room(s) for this area - %d adopted, %d corrected, %d already right",
                     #t.rooms, added, fixed, #t.rooms - added - fixed))
  if linked > 0 then
    note(string.format("  and %d exit destination(s) the mapper had resolved were filled in", linked))
  end
  local names = {}
  for a, n in pairs(elsewhere) do names[#names + 1] = string.format("%s (%d)", a, n) end
  if #names > 0 then
    table.sort(names)
    note("  filed under other area names, outside this fence: " .. table.concat(names, ", "))
  end
  draw()
end)

-- ---------- the trip (map.walk.*) ----------
-- Only ever ours while T.going is set: the contract promises exactly one of
-- arrived/stopped per walk, and the farmer never has a walk of its own in flight while
-- traveling (the patrol is stopped before the ask). A walk someone ELSE started while we
-- wait would end our trip state with it - a real race, accepted: two drivers were giving
-- orders, and stopping is the honest reading.
scrye.on("map.walk.started", function()
  if not T.going then return end
  travel_answered()
  note("the mapper is walking us toward '" .. T.going .. "'")
end)

scrye.on("map.walk.stopped", function(data)
  if not T.going then return end
  travel_answered()
  local ok, t = pcall(scrye.json.decode, data)
  local why = (ok and type(t) == "table" and tostring(t.reason or "")) or ""
  note("the trip to '" .. T.going .. "' did not finish" .. (why ~= "" and (" - " .. why) or ""))
  T.going = nil
  draw()
end)

-- map.room: the mapper's own arrival feed, emitted for every mapped arrival BEFORE its
-- walk narration, in exactly Room.Info's shape. Fed through the same handler, so by the
-- time map.walk.arrived follows it `here` already names the room we landed in whichever
-- order the client fans the burst out - the one-beat timer this used to need is gone.
-- Idempotent with our own Room.Info hook: the second copy of an arrival is not a move.
scrye.on("map.room", function(data) on_room_info(data) end)

scrye.on("map.walk.arrived", function()
  if not T.going then return end
  travel_answered()
  T.going = nil
  start()
end)

scrye.onGmcp("Room.Info", on_room_info)
scrye.onGmcp("Room.Contents", on_room_contents)
scrye.onGmcp("Room.Map", on_room_map)
scrye.onGmcp("Char.Combat", on_combat)
scrye.onPrompt(on_prompt)

load()
draw()
