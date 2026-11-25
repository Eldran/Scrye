-- Harness for 3s-compendium (sightings and kills; fights, hit points and XP since 0.2.0).
--
-- The payload SHAPES are the ones the farmer's captures established: Room.Info { num, name,
-- area }, Room.Contents { page, items: [ { name, type, count } ] } and Room.Death { killer,
-- name, npc, corpse } with npc as 1 / 0, Char.Combat { attacker, attacker_hp, rounds } with
-- rounds per attacker from 0 and the empty payload as the end, Char.Vitals { hp, maxhp } and
-- Char.XP { xp, per_hour, gain30 }. The rooms, monsters, names and numbers are made up.
--
--   lua5.4 compendium_test.lua   (from anywhere; tests/plugins/run.sh runs them all, as CI does)

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-compendium/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

-- ---- a small JSON reader and writer ----
local decode, encode
do
  local pos, src
  local function ws() pos = src:find("[^ \t\r\n]", pos) or #src + 1 end
  local function value()
    ws()
    local c = src:sub(pos, pos)
    if c == "{" then
      local t = {} ; pos = pos + 1 ; ws()
      if src:sub(pos, pos) == "}" then pos = pos + 1 ; return t end
      while true do
        ws()
        local k = value() ; ws() ; pos = pos + 1
        t[k] = value() ; ws()
        local d = src:sub(pos, pos) ; pos = pos + 1
        if d == "}" then return t end
      end
    elseif c == "[" then
      local t = {} ; pos = pos + 1 ; ws()
      if src:sub(pos, pos) == "]" then pos = pos + 1 ; return t end
      while true do
        t[#t + 1] = value() ; ws()
        local d = src:sub(pos, pos) ; pos = pos + 1
        if d == "]" then return t end
      end
    elseif c == '"' then
      local out, i = {}, pos + 1
      while true do
        local ch = src:sub(i, i)
        if ch == "" then error("unterminated string") end
        if ch == "\\" then out[#out + 1] = src:sub(i + 1, i + 1) ; i = i + 2
        elseif ch == '"' then pos = i + 1 ; return table.concat(out)
        else out[#out + 1] = ch ; i = i + 1 end
      end
    else
      local lit = src:match("^%-?%d+%.?%d*", pos)
      if lit then pos = pos + #lit ; return tonumber(lit) end
      lit = src:match("^%a+", pos)
      if not lit then error("bad json") end
      pos = pos + #lit
      if lit == "true" then return true elseif lit == "false" then return false elseif lit == "null" then return nil end
      error("bad json")
    end
  end
  decode = function(s)
    if type(s) ~= "string" or s == "" then error("not json") end   -- the host raises too; the plugin pcalls
    src, pos = s, 1
    return value()
  end
  local function str(s) return '"' .. tostring(s):gsub('[\\"]', "\\%0") .. '"' end
  encode = function(v)
    local t = type(v)
    if t == "table" then
      local n = 0
      for _ in pairs(v) do n = n + 1 end
      local parts = {}
      if n > 0 and n == #v then
        for i = 1, n do parts[i] = encode(v[i]) end
        return "[" .. table.concat(parts, ",") .. "]"
      end
      local keys = {}
      for k in pairs(v) do keys[#keys + 1] = tostring(k) end
      table.sort(keys)
      for _, k in ipairs(keys) do parts[#parts + 1] = str(k) .. ":" .. encode(v[k]) end
      return "{" .. table.concat(parts, ",") .. "}"
    elseif t == "string" then return str(v)
    elseif t == "number" then return (v == math.floor(v)) and string.format("%d", v) or tostring(v)
    else return tostring(v) end
  end
end

-- ---- the client ----
local CLOCK = 1000000
os.time = function() return CLOCK end   -- the plugin's only clock
os.date = function() return "20261121-1700" end   -- and the stamp in an export's name
local FILES = {}   -- the exports folder: outlives every client, emptied by hand

local printed, gmcp_hooks, event_hooks, aliases, triggers, state, panel, timers, next_id, on_disc, emitted, on_cmd
local SHARED, PRIVATE, writes   -- the two stores outlive a client: a reload reads them back

local function make_store(tbl)
  return {
    get = function(k) return tbl[k] end,
    set = function(k, v) tbl[k] = tostring(v) ; writes[#writes + 1] = k end,
    setMany = function(b) for k, v in pairs(b) do tbl[k] = tostring(v) ; writes[#writes + 1] = k end end,
    delete = function(k) tbl[k] = nil ; writes[#writes + 1] = "-" .. k end,
    keys = function() local ks = {} ; for k in pairs(tbl) do ks[#ks + 1] = k end ; table.sort(ks) ; return ks end,
  }
end

local function fresh_client(opts)
  opts = opts or {}
  printed, gmcp_hooks, event_hooks, aliases, triggers = {}, {}, {}, {}, {}
  state, panel, timers, next_id, on_disc, writes, emitted = {}, nil, {}, 0, nil, {}, {}
  if not opts.keep then SHARED, PRIVATE = {}, {} end
  if opts.new_char then PRIVATE = {} end   -- another character: the same MUD's book, its own private store
  scrye = {
    id = "3s-compendium",
    print = function(x) printed[#printed + 1] = tostring(x) end,
    json = { decode = decode, encode = encode },
    onGmcp = function(pkg, fn) gmcp_hooks[#gmcp_hooks + 1] = { pkg = pkg, fn = fn } end,
    on = function(name, fn) event_hooks[#event_hooks + 1] = { name = name, fn = fn } end,
    emit = function(name, data) emitted[#emitted + 1] = { name = name, data = data } end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addTrigger = function(t) triggers[#triggers + 1] = t end,
    addPanel = function(pn) panel = pn end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    after = function(d, fn) next_id = next_id + 1; timers[next_id] = { fn = fn, d = d }; return next_id end,
    every = function(d, fn) next_id = next_id + 1; timers[next_id] = { fn = fn, d = d, rep = true }; return next_id end,
    cancel = function(id) timers[id] = nil end,
    onDisconnect = function(fn) on_disc = fn end,
    onCommand = function(fn) on_cmd = fn end,
    store = make_store(PRIVATE),
  }
  if not opts.no_shared then scrye.shared = make_store(SHARED) end
  if not opts.no_exports then
    scrye.exports = {
      write = function(name, text)
        if not tostring(name):match("^[%w ._%-()]+%.json$") then return nil, "not a name the exports folder takes" end
        FILES[name] = text ; return "C:/exports/" .. name
      end,
      read = function(name) if FILES[name] then return FILES[name] end return nil, "no such file" end,
      list = function() local l = {} ; for k in pairs(FILES) do l[#l + 1] = k end ; table.sort(l) ; return l end,
    }
  end
  dofile(PLUGIN)
end

local function gmcp(pkg, json) for _, h in ipairs(gmcp_hooks) do if h.pkg == pkg then h.fn(json) end end end
local function event(name, data) for _, h in ipairs(event_hooks) do if h.name == name then h.fn(data, name, "3s-map-gmcp") end end end
-- one pass of the clock: the repeating view tick, and every one-shot that is waiting
local function tick_once()
  local due = {}
  for id, t in pairs(timers) do due[#due + 1] = { id = id, t = t } end
  table.sort(due, function(a, b) return a.id < b.id end)
  for _, d in ipairs(due) do
    if not d.t.rep then timers[d.id] = nil end
    d.t.fn()
  end
end
-- twice: a one-shot that changes the book (a kill's XP settling) arms the save, which the
-- second pass then writes
local function tick() tick_once() ; tick_once() end
local function view() for _, t in pairs(timers) do if t.rep then t.fn() end end end   -- the view only, no save
local function typed(s)
  printed = {}
  for _, a in ipairs(aliases) do
    local caps = { s:match(a.pattern:gsub("\\d", "%%d")) }
    if #caps > 0 or s:match(a.pattern) then
      if s:match(a.pattern) == s then a.run() else a.run(table.unpack(caps)) end
      return true
    end
  end
  return false
end
-- a line of MUD text through the plugin's triggers (their regexes, as Lua patterns)
local function line(s)
  for _, t in ipairs(triggers) do
    if t.pattern:find("killing blow", 1, true) then
      local victim = s:match("^You dealt the killing blow to (.+)%.")
      if victim then t.run(victim) end
    elseif t.pattern:find("Taken", 1, true) then
      local what = s:match("^(.+): Taken%.%s*$")
      if what then t.run(what) end
    elseif t.pattern:find(": Ok", 1, true) then
      local what = s:match("^(.+): Ok%.%s*$")
      if what then t.run(what) end
    elseif t.pattern:find("from the remains", 1, true) then
      local n, what = s:match("^You bind (%d+) (.+) from the remains%.")
      if not n then n, what = s:match("^You salvage (%d+) (.+) from the remains%.") end
      if n then t.run(n, what) end
    end
  end
end
local function st(k) return tostring(state["plugin.3s-compendium." .. k] or "") end
local function said(needle)
  for _, l in ipairs(printed) do if l:find(needle, 1, true) then return true end end
  return false
end
local function has(text, needle) return text:find(needle, 1, true) ~= nil end
local function book(area) local raw = SHARED["area:" .. area] ; return raw and decode(raw) or nil end
-- one character's numbers inside an entry (0.3.0): Keyser's, else the unnamed character's
local function me(e, who) return (e.by and (e.by[who or "Keyser"] or (not who and e.by["?"]))) or {} end
local function wrote(key) for _, k in ipairs(writes) do if k == key then return true end end return false end

local function room(num, name, area)
  gmcp("Room.Info", encode({ num = num, name = name, area = area, exits = { n = num + 1 } }))
end
local function contents(items, page)
  local list = {}
  for _, it in ipairs(items) do list[#list + 1] = { name = it[1], type = it[2] or "monster", count = it[3] } end
  local body = #list == 0 and '{ "items": [] }' or encode({ page = page, items = list })
  gmcp("Room.Contents", body)
end
local function death(name, killer, npc)
  gmcp("Room.Death", encode({ killer = killer, name = name, npc = npc == nil and 1 or npc, corpse = "corpse of " .. name }))
end

-- 1. nothing yet
fresh_client()
check(panel ~= nil and panel.title == "Compendium" and #panel.tabs == 5, "the panel has five tabs")
do
  local known = { value = 1, text = 1, label = 1, gauge = 1, list = 1, table = 1, input = 1 }
  local bad = 0
  for _, t in ipairs(panel.tabs) do for _, w in ipairs(t.widgets) do if not known[w.type] or not (w.bind or w.text) then bad = bad + 1 end end end
  check(bad == 0, "every widget has a type the host knows and a bind or a text")
end
check(has(st("here"), "waiting for Room.Info") and has(st("area"), "waiting for Room.Info"), "empty: both tabs wait for a room")
check(has(st("summary"), "0 monsters in 0 areas, 0 kills"), "empty: the summary counts nothing (" .. st("summary") .. ")")
check(#writes == 0, "loading writes nothing")
typed("mdb")
check(said("the book is empty"), "mdb on an empty book says so")

-- 2. a room and its monsters: one sighting each, tags and articles off the names
room(101, "A muddy track", "Farmland")
contents({ { "an orc", "monster", 3 }, { "Wiremouth {angry}", "monster", 1 }, { "Eldran", "player", 1 }, { "a rusty sword", "item", 1 } })
view()
local h = st("here")
check(has(h, "A muddy track") and has(h, "Farmland"), "here: the room and its area")
check(has(h, "3 x @{accent,click=mdb view Farmland|orc}Orc@{}"), "here: three orcs, the name a link to its entry (" .. h .. ")")
check(has(h, "click=mdb view Farmland|wiremouth}Wiremouth@{}") and not has(h, "angry"), "here: the server's {tag} is not part of the name")
check(not has(h, "Eldran") and not has(h, "sword"), "here: players and items are not monsters")
check(has(h, "@{success}new@{}"), "here: a first meeting is marked new")
check(has(st("summary"), "2 monsters in 1 area, 0 kills"), "summary after the first room (" .. st("summary") .. ")")
check(has(st("area_head"), "Farmland") and has(st("area"), "Orc") and has(st("area"), "Wiremouth"), "area tab lists both")

-- 3. the server refreshes the list while you stand there: no new sighting, the group can grow
contents({ { "an orc", "monster", 4 }, { "Wiremouth {calm}", "monster", 1 } })
tick()   -- the view and the pending save
local b = book("Farmland")
check(b ~= nil and b.orc ~= nil and b.orc.seen == 1, "a refresh in the same room is the same sighting (seen " .. tostring(b and b.orc and b.orc.seen) .. ")")
check(b.orc.max == 4, "the largest group follows a refresh (max " .. tostring(b.orc.max) .. ")")
check(b.orc.name == "Orc" and b.wiremouth ~= nil and b.wiremouth.seen == 1, "stored under the bare name, shown capitalised")
check(b.orc.rooms.r101 ~= nil and b.orc.rooms.r101.n == 1 and b.orc.rooms.r101.name == "A muddy track", "the room it was met in is kept")
check(b.orc.first == CLOCK and b.orc.last == CLOCK, "first and last seen are stamped")

-- 4. leave and come back: a second sighting
CLOCK = CLOCK + 120
room(102, "A barn", "Farmland")
contents({})
view()
check(has(st("here"), "no monsters here"), "an empty room says so")
room(101, "A muddy track", "Farmland")
contents({ { "an orc", "monster", 2 } })
tick()
b = book("Farmland")
check(b.orc.seen == 2 and b.orc.rooms.r101.n == 2, "back in the room: a second sighting (seen " .. b.orc.seen .. ")")
check(b.orc.max == 4, "a smaller group does not lower the record")
check(b.orc.first == CLOCK - 120 and b.orc.last == CLOCK, "first stays, last moves")
check(b.wiremouth.seen == 1, "the one that was not there is not seen again")
check(has(st("here"), "seen 2x") and not has(st("here"), "@{success}new"), "here: no longer new")

-- 5. a paged room: page 2 adds to page 1
room(103, "The orc camp", "Farmland")
contents({ { "an orc", "monster", 2 } }, 1)
contents({ { "an orc chieftain", "monster", 1 }, { "an orc", "monster", 1 } }, 2)
view()
check(has(st("here"), "3 x ") and has(st("here"), "Orc chieftain"), "page 2 adds to page 1's roster (" .. st("here") .. ")")
tick()
b = book("Farmland")
check(b.orc.seen == 3 and b["orc chieftain"] ~= nil and b["orc chieftain"].seen == 1, "a paged room is still one sighting each")

-- 6. kills: an npc's death counts, a player's does not, and the dead leave the roster
death("an orc chieftain", "Goran")
death("Eldran", "an orc", 0)
view()
tick()
b = book("Farmland")
check(b["orc chieftain"].kills == 1 and (me(b["orc chieftain"]).kills or 0) == 0, "an npc's death is a kill, not yet known to be yours")
check(b["orc chieftain"].lastkill == CLOCK, "the kill is stamped")
check(b.eldran == nil, "a player's death (npc 0) is not a kill")
check(not has(st("here"), "Orc chieftain"), "the dead leave the room's list before the server's refresh")
check(has(st("summary"), "1 kill"), "summary counts the kill (" .. st("summary") .. ")")

-- 7. learning your name: Room.Death first, then the line
death("an orc", "Keyser")
line("You dealt the killing blow to an orc.")
check(said("your character is Keyser"), "the pair names you (gmcp first)")
tick()
b = book("Farmland")
check(b.orc.kills == 1 and (me(b.orc).kills or 0) == 1, "that kill became yours (kills " .. b.orc.kills .. ", yours " .. (me(b.orc).kills or 0) .. ")")
check(PRIVATE.me == "Keyser", "the name is stored")
-- from now on the killer alone is enough
death("an orc", "Keyser")
death("an orc", "Goran")
tick()
b = book("Farmland")
check(b.orc.kills == 3 and (me(b.orc).kills or 0) == 2, "your kills by name, a friend's are kills only (kills " .. b.orc.kills .. ", yours " .. (me(b.orc).kills or 0) .. ")")
view()
check(has(st("area"), "Seen") and has(st("area"), "Kills") and has(st("area"), "XP") and has(st("area"), "Time"), "the area table's columns")
check(has(st("best"), "no kill has been measured"), "kills with no fight of your own measure nothing (" .. st("best") .. ")")

-- 7b. the line first, then Room.Death; and a line too old to pair
fresh_client()
room(1, "Den", "Caves")
contents({ { "a bat", "monster", 2 } })
line("You dealt the killing blow to a bat.")
death("a bat", "Keyser")
check(said("your character is Keyser"), "the pair names you (line first)")
tick()
check(book("Caves").bat.kills == 1 and (me(book("Caves").bat).kills or 0) == 1, "line first: one kill, yours")
fresh_client()
room(1, "Den", "Caves")
contents({ { "a bat", "monster", 2 } })
line("You dealt the killing blow to a bat.")
CLOCK = CLOCK + 30
death("a bat", "Goran")
check(not said("your character is"), "a line half a minute old does not pair with a later death")
check(PRIVATE.me == nil, "nobody was named")

-- 8. nothing is written per sighting: the save waits, and a disconnect flushes it
fresh_client()
room(7, "Gate", "Town")
contents({ { "a guard", "monster", 2 } })
view()
check(not wrote("area:Town"), "a sighting alone writes nothing yet")
check(on_disc ~= nil, "the plugin listens for the disconnect")
on_disc()
check(wrote("area:Town") and book("Town").guard.seen == 1, "disconnecting writes what was pending")
writes = {}
tick()
check(not wrote("area:Town"), "the timer that follows has nothing left to write")

-- 9. a restart reads the book back (and the visit is new, so it is a sighting)
CLOCK = CLOCK + 86400 * 2
fresh_client({ keep = true })
check(has(st("summary"), "1 monster in 1 area"), "the book survives a restart (" .. st("summary") .. ")")
room(7, "Gate", "Town")
contents({ { "a guard", "monster", 1 } })
tick()
check(book("Town").guard.seen == 2 and book("Town").guard.max == 2, "the reloaded entry carries on (seen " .. book("Town").guard.seen .. ")")
typed("mdb guard")
check(said("Guard") and said("Town") and said("seen 2x, up to 2 together") and said("first 2d ago"), "mdb <name> prints the entry")
check(said("Gate") and said("#7"), "with the rooms it was met in")
check(said("not killed"), "and that it was never killed")

-- 10. loaded mid-session: the roster can be replayed before the room
fresh_client()
contents({ { "a rat", "monster", 1 } })
tick()
check(next(SHARED) == nil, "a roster with no room is not recorded anywhere")
room(55, "Cellar", "Inn")
tick()
check(book("Inn") ~= nil and book("Inn").rat.seen == 1 and book("Inn").rat.rooms.r55 ~= nil, "the room that follows claims the waiting roster")

-- 11. the same name in two areas is two entries
room(900, "Sewer", "Undercity")
contents({ { "a rat", "monster", 5 } })
tick()
check(book("Undercity").rat.seen == 1 and book("Inn").rat.seen == 1, "one entry per area")
typed("mdb rat")
check(said("Inn") and said("Undercity"), "mdb <name> shows every area's entry")
typed("mdb")
check(said("click=mdb area Inn}Inn") and said("click=mdb area Undercity}Undercity") and said("1 monster, 0 kills"), "mdb lists the areas")
typed("mdb area under")
check(said("Undercity") and said("Rat") and not said("no area"), "mdb area <part of a name>")
typed("mdb area nowhere")
check(said("no area in the book matches 'nowhere'"), "an unknown area says so")
typed("mdb area")
check(said("Undercity") and said("Rat"), "mdb area with no name is the area you stand in")
typed("mdb here")
check(said("Sewer") and said("5 x "), "mdb here prints the room")
typed("mdb dragon")
check(said("nothing in the book matches 'dragon'"), "an unknown monster says so")
typed("mdb help")
check(said("mdb ignore") and said("mdb forget"), "mdb help")

-- 12. the mapper's map.room event moves you too
event("map.room", encode({ num = 901, name = "Drain", area = "Undercity", exits = {} }))
contents({ { "a rat", "monster", 1 } })
tick()
check(book("Undercity").rat.seen == 2 and book("Undercity").rat.rooms.r901 ~= nil, "a room heard from the mapper counts as a move")
event("map.room", encode({ num = 901, name = "Drain", area = "Undercity", exits = {} }))   -- the mapper's copy again
contents({ { "a rat", "monster", 1 } })
tick()
check(book("Undercity").rat.seen == 2, "the mapper announcing the room you stand in is not a new visit")
room(902, "Drain end", "Undercity")   -- GMCP first, then the mapper's copy of the same room, then the list
event("map.room", encode({ num = 902, name = "Drain end", area = "Undercity", exits = {} }))
contents({ { "a rat", "monster", 1 } })
tick()
check(book("Undercity").rat.seen == 3, "a room told by GMCP and then by the mapper is one visit")

-- 13. ignore: your own followers are monster-typed
typed("mdb ignore a hirdmadr")
check(said("ignoring 'hirdmadr'"), "ignore takes the name with its article")
room(902, "Pipe", "Undercity")
contents({ { "a hirdmadr", "monster", 2 }, { "a rat", "monster", 1 } })
death("a hirdmadr", "a rat")
view()
tick()
check(book("Undercity").hirdmadr == nil, "an ignored monster is neither seen nor killed in the book")
check(has(st("here"), "Hirdmadr  (ignored)"), "here still shows it, marked (" .. st("here") .. ")")
check(decode(SHARED.ignore)[1] == "hirdmadr", "the ignore list is stored")
typed("mdb ignore")
check(said("ignored: hirdmadr"), "mdb ignore lists it")
typed("mdb ignore hirdmadr")
check(said("already ignored"), "ignoring twice says so")
fresh_client({ keep = true })
room(902, "Pipe", "Undercity")
contents({ { "a hirdmadr", "monster", 2 } })
tick()
check(book("Undercity").hirdmadr == nil, "the ignore list survives a restart")
typed("mdb unignore hirdmadr")
check(said("recording 'hirdmadr' again"), "unignore")
room(903, "Grate", "Undercity")
contents({ { "a hirdmadr", "monster", 2 } })
tick()
check(book("Undercity").hirdmadr ~= nil and book("Undercity").hirdmadr.seen == 1, "and it is recorded from then on")

-- 14. forget asks first, deletes everywhere, and an emptied area's key goes too
typed("mdb forget rat")
check(said("mdb forget rat confirm") and said("Inn, Undercity"), "forget names the areas and asks")
tick()
check(book("Inn").rat ~= nil, "nothing is deleted before the confirm")
typed("mdb forget rat confirm")
check(said("forgot 'rat' (2 areas)"), "forget confirmed")
tick()
check(SHARED["area:Inn"] == nil and wrote("-area:Inn"), "the area that held nothing else is removed from the store")
check(book("Undercity").rat == nil and book("Undercity").hirdmadr ~= nil, "the other area keeps its other entries")
typed("mdb forget rat")
check(said("no entry is named exactly 'rat'"), "forgetting what is not there says so")

-- 15. mdb me
typed("mdb me")
check(said("not known yet"), "mdb me before any kill")
typed("mdb me Keyser")
check(said("your character is Keyser (set by you)") and PRIVATE.me == "Keyser", "mdb me <name> sets it")
death("a hirdmadr", "Keyser")
tick()
check(book("Undercity").hirdmadr.kills == 1 and (me(book("Undercity").hirdmadr).kills or 0) == 1, "a kill by the name you gave is yours")
typed("mdb hirdmadr")
check(said("1 kill (1 yours)"), "the entry shows your share")

-- 16. an entry remembers at most 40 rooms; the least visited makes way
fresh_client()
for i = 1, 41 do
  room(2000 + i, "Tunnel " .. i, "Mine")
  contents({ { "a kobold", "monster", 1 } })
  if i == 1 then   -- room 2001 is visited twice, so it is not the one dropped
    room(1999, "Shaft", "Mine") ; contents({})
    room(2001, "Tunnel 1", "Mine") ; contents({ { "a kobold", "monster", 1 } })
  end
end
tick()
do
  local n = 0
  for _ in pairs(book("Mine").kobold.rooms) do n = n + 1 end
  check(n == 40, "40 rooms kept of 41 (" .. n .. ")")
  check(book("Mine").kobold.rooms.r2001 ~= nil and book("Mine").kobold.rooms.r2002 == nil, "the least-visited, lowest room made way")
  check(book("Mine").kobold.seen == 42, "every visit is still a sighting (" .. book("Mine").kobold.seen .. ")")
end

-- 17. a kill before any room, and junk on the wire
fresh_client()
death("a ghost", "Goran")
gmcp("Room.Info", "not json")
gmcp("Room.Contents", "{ broken")
gmcp("Room.Death", "")
gmcp("Room.Info", '{ "name": "No number" }')
tick()
check(book("unknown") ~= nil and book("unknown").ghost.kills == 1, "a kill with no room known goes under 'unknown'")
check(has(st("here"), "waiting for Room.Info"), "junk payloads change nothing")

-- 18. a host before scrye.shared: the private store holds the book
fresh_client({ no_shared = true })
room(5, "Yard", "Farm")
contents({ { "a hen", "monster", 6 } })
tick()
check(PRIVATE["area:Farm"] ~= nil and next(SHARED) == nil, "without scrye.shared the private store is used")

-- 19. the plugin never sends: it has no way to
check(scrye.send == nil, "the harness offers no scrye.send and the plugin loaded without one")

-- ---------------------------------------------------------------- 0.2.0: fights
local function combat(name, rounds, hp)
  if not name then gmcp("Char.Combat", '{ "attacker": "", "rounds": 0 }') ; return end
  gmcp("Char.Combat", encode({ attacker = name, attacker_hp = hp or 100, rounds = rounds, target = name }))
end
local function vitals(hp, maxhp) gmcp("Char.Vitals", encode({ hp = hp, maxhp = maxhp or 1000, sp = 500, maxsp = 500 })) end
local function xp(n) gmcp("Char.XP", encode({ xp = n, per_hour = 0, gain30 = 0 })) end
local function arena(mobs)
  fresh_client()
  room(10, "Pit", "Arena")
  contents(mobs or { { "A troll {wounded}", "monster", 1 } })
  typed("mdb me Keyser")
  vitals(1000) ; xp(5000)
end

-- 20. one monster, fought alone: time, rounds, hit points lost, lowest point, XP
arena()
combat("a troll {wounded}", 0)          -- Char.Combat spells it in lower case
vitals(800)
CLOCK = CLOCK + 6 ; combat("a troll {wounded}", 3, 60)
vitals(650) ; vitals(700)               -- a heal in between is not a loss
CLOCK = CLOCK + 6 ; combat("a troll {badly wounded}", 6, 5)
vitals(640)
death("a troll", "Keyser")
xp(6200)                                -- the kill's award lands just after the blow
combat(nil)
tick()
local t = book("Arena").troll
check(t.kills == 1 and me(t).fights == 1, "the kill is a measured fight (fights " .. tostring(me(t).fights) .. ")")
check(me(t).secs == 12 and me(t).rounds == 6, "the fight's length: seconds and the server's rounds (" .. tostring(me(t).secs) .. "s, " .. tostring(me(t).rounds) .. ")")
check(me(t).dmg == 410 and me(t).dmgn == 1, "hit points lost: every drop, a heal not subtracted (" .. tostring(me(t).dmg) .. ")")
check(me(t).low == 64, "the lowest your hit points fell, in percent (" .. tostring(me(t).low) .. ")")
check(me(t).xp == 1200 and me(t).xpn == 1 and me(t).xplast == 1200, "the kill's XP: after the blow minus the fight's start (" .. tostring(me(t).xp) .. ")")
check(me(t).open == nil, "a won fight is not an unfinished one")
view()
check(has(st("here"), "no monsters here") or not has(st("here"), "Troll"), "the dead troll left the list")
typed("mdb troll")
check(said("1 fight measured: 12s and 6 rounds on average"), "entry: the fight line")
check(said("1200 xp a kill") and said("6000 xp/min of fighting"), "entry: XP a kill and per minute of fighting")
check(said("410 hp lost a kill (fought alone, 1x)") and said("your lowest: @{dim}64%@{}"), "entry: hit points")
view()
check(has(st("best"), "click=mdb fold Arena}+ Arena@{}") and has(st("best"), "1 monster, best 1200 xp") and not has(st("best"), "Troll"), "the Best tab shows the area, closed (" .. st("best") .. ")")
typed("mdb fold Arena")
check(#printed == 0, "opening an area prints nothing")
check(has(st("best"), "click=mdb fold Arena}- Arena@{}") and has(st("best"), "click=mdb view Arena|troll}Troll") and has(st("best"), "1200") and has(st("best"), "12s") and has(st("best"), "6000"), "a click opens it into its monsters (" .. st("best") .. ")")
typed("mdb fold Arena")
check(has(st("best"), "+ Arena") and not has(st("best"), "Troll"), "another click closes it")
typed("mdb fold Nowhere")
check(not has(st("best"), "Nowhere"), "folding an area that is not in the book does nothing")
check(has(st("area"), "1200") and has(st("area"), "12s"), "the area table shows XP and time")

-- 21. the next visit: Here says what a fight with it is like
room(11, "Gate", "Arena") ; contents({})
room(10, "Pit", "Arena") ; contents({ { "A troll", "monster", 1 } })
view()
check(has(st("here"), "~1200 xp, 12s, @{dim}low 64%@{}"), "here: the fight in brief (" .. st("here") .. ")")

-- 22. a second kill inside the settle is not charged; the one after counts from the settle
arena({ { "a rat", "monster", 3 } })
combat("a rat", 0) ; CLOCK = CLOCK + 2 ; combat("a rat", 2)
death("a rat", "Keyser")
xp(5100)
combat("a rat", 0)                      -- the next rat steps up, same name, rounds from 0
death("a rat", "Keyser")                -- and dies in the same second
xp(5200)
tick()                                  -- the first kill's settle
local r = book("Arena").rat
check(r.kills == 2 and me(r).fights == 2, "two kills, two fights")
check(me(r).xpn == 1 and me(r).xp == 200, "two kills inside a second: one measurement, the whole take (" .. tostring(me(r).xp) .. " over " .. tostring(me(r).xpn) .. ")")
combat("a rat", 0) ; CLOCK = CLOCK + 4 ; combat("a rat", 3)
death("a rat", "Keyser") ; xp(5290)
tick()
r = book("Arena").rat
check(me(r).xpn == 2 and me(r).xp == 290 and me(r).xplast == 90, "the third counts from where the settle left off (" .. tostring(me(r).xplast) .. ")")

-- 23. two on you: the hit points lost are nobody's, the lowest point is both's
arena({ { "an ogre", "monster", 1 }, { "a goblin", "monster", 1 } })
combat("an ogre", 0) ; vitals(900)
combat("a goblin", 0) ; vitals(400)
CLOCK = CLOCK + 5
combat("an ogre", 4) ; combat("a goblin", 4)
death("a goblin", "Keyser") ; xp(5050)
tick()
CLOCK = CLOCK + 5 ; combat("an ogre", 8)
vitals(350)
death("an ogre", "Keyser") ; xp(5900)
combat(nil)
tick()
local og, gb = book("Arena").ogre, book("Arena").goblin
check(me(gb).fights == 1 and me(gb).dmgn == nil and me(og).fights == 1 and me(og).dmgn == nil, "with two attackers no hit points are charged to either")
check(me(gb).low == 40 and me(og).low == 35, "the lowest point is kept for both (" .. tostring(me(gb).low) .. ", " .. tostring(me(og).low) .. ")")
check(me(gb).xp == 50 and me(og).xp == 850, "each kill's XP is what came in since the one before")
check(me(og).secs == 10 and me(gb).secs == 5, "each attacker is timed from when it joined")
typed("mdb best")
do
  local o_at, g_at
  for i, l in ipairs(printed) do
    if l:find("Ogre", 1, true) then o_at = i end
    if l:find("Goblin", 1, true) then g_at = i end
  end
  check(o_at and g_at and o_at < g_at, "mdb best: the better XP first")
end
typed("mdb best arena")
check(said("Ogre") and said("Goblin"), "mdb best <area>")
typed("mdb best nowhere")
check(said("no area in the book matches 'nowhere'"), "mdb best with an unknown area")

-- 24. two of the same name at once: the round count restarts, so it was not alone
arena({ { "a wolf", "monster", 2 } })
combat("a wolf", 0) ; combat("a wolf", 5) ; vitals(700)
combat("a wolf", 1)                     -- the other wolf's count
CLOCK = CLOCK + 3
death("a wolf", "Keyser") ; xp(5010)
tick()
check(me(book("Arena").wolf).fights == 1 and me(book("Arena").wolf).dmgn == nil, "a restarted round count is a second attacker: no hit points charged")
check(me(book("Arena").wolf).rounds == 5, "the rounds kept are the highest count seen")

-- 25. a fight that ends with the monster alive is unfinished - after the grace, not before
arena({ { "a dragon", "monster", 1 } })
combat("a dragon", 0) ; vitals(200) ; combat("a dragon", 2)
combat(nil)
tick()
check(me(book("Arena").dragon).open == nil, "combat just ended: a Room.Death may still come")
CLOCK = CLOCK + 3
tick() ; tick()
local dr = book("Arena").dragon
check(me(dr).open == 1 and dr.kills == 0 and me(dr).fights == nil, "the grace passed: one unfinished fight, no kill")
check(me(dr).low == 20, "and how low it took you")
typed("mdb dragon")
check(said("1 fight left unfinished") and said("@{error}20%@{}"), "entry: unfinished, the lowest point in red")
view()
check(has(st("here"), "@{error}low 20%@{}"), "here: the warning (" .. st("here") .. ")")
-- the death arriving inside the grace is a kill
arena({ { "a dragon", "monster", 1 } })
combat("a dragon", 0) ; CLOCK = CLOCK + 9 ; combat("a dragon", 4)
combat(nil)
vitals(900)                             -- poison ticking after the fight is not the dragon's blow
CLOCK = CLOCK + 1
death("a dragon", "Keyser") ; xp(9000)
tick()
CLOCK = CLOCK + 5 ; tick()
dr = book("Arena").dragon
check(dr.kills == 1 and me(dr).fights == 1 and me(dr).open == nil and me(dr).secs == 10, "a death a second after combat ended still closes the fight")
check(me(dr).xp == 4000, "with its XP")
check(me(dr).dmg == 0 and me(dr).dmgn == 1 and me(dr).low == 100, "hit points lost after combat ended are not charged to it (" .. tostring(me(dr).dmg) .. ")")

-- 26. a fight that just goes quiet is over after ten seconds
arena({ { "a ghost", "monster", 1 } })
combat("a ghost", 0)
CLOCK = CLOCK + 9 ; tick()
check(me(book("Arena").ghost).open == nil, "nine quiet seconds: still a fight")
CLOCK = CLOCK + 2 ; tick()
CLOCK = CLOCK + 3 ; tick() ; tick()
check(me(book("Arena").ghost).open == 1, "ten quiet seconds and the grace: unfinished")

-- 27. a new fight sends the last one's survivor off as unfinished
arena({ { "a ghost", "monster", 1 }, { "a bat", "monster", 1 } })
combat("a ghost", 0) ; combat(nil)
combat("a bat", 0) ; CLOCK = CLOCK + 2
death("a bat", "Keyser") ; xp(5005)
tick()
check(me(book("Arena").ghost).open == 1 and me(book("Arena").bat).fights == 1, "the ghost got away, the bat did not")
check(me(book("Arena").bat).dmgn == 1, "and the bat counts as fought alone")

-- 28. kills with no fight, no Char.XP, a loss of XP, and an ignored attacker
arena({ { "a sheep", "monster", 1 } })
death("a sheep", "Goran")               -- somebody else's fight
tick()
check(book("Arena").sheep.kills == 1 and me(book("Arena").sheep).fights == nil and me(book("Arena").sheep).xpn == nil, "a death you were not fighting measures nothing")
fresh_client()
room(10, "Pit", "Arena") ; contents({ { "a sheep", "monster", 1 } })
combat("a sheep", 0) ; CLOCK = CLOCK + 4 ; combat("a sheep", 2)
death("a sheep", "Keyser")
tick()
local sh = book("Arena").sheep
check(me(sh).fights == 1 and me(sh).secs == 4 and me(sh).xpn == nil and me(sh).dmgn == nil, "no Char.XP and no Char.Vitals: the fight is timed, nothing else is claimed")
typed("mdb sheep")
check(said("1 fight measured") and not said("xp a kill") and not said("hp lost"), "and the entry prints only what it has")
arena({ { "a wraith", "monster", 1 } })
combat("a wraith", 0) ; CLOCK = CLOCK + 2
death("a wraith", "Keyser") ; xp(4000)  -- it drained you
tick()
check(me(book("Arena").wraith).fights == 1 and me(book("Arena").wraith).xpn == nil, "a fight that cost XP is not recorded as a negative reward")
arena({ { "a hirdmadr", "monster", 1 } })
typed("mdb ignore hirdmadr")
combat("a hirdmadr", 0) ; combat(nil)
CLOCK = CLOCK + 5 ; tick() ; tick()
check(me(book("Arena").hirdmadr).open == nil and me(book("Arena").hirdmadr).fights == nil, "an ignored attacker is not a fight")
-- ...and does not make the monster beside it a shared fight
combat("a hirdmadr", 0) ; combat("a troll", 0) ; vitals(900)
CLOCK = CLOCK + 2
death("a troll", "Keyser") ; xp(5100)
tick()
check(me(book("Arena").troll).dmgn == 1 and me(book("Arena").troll).dmg == 100, "an ignored name in Char.Combat does not stop the other one being fought alone")
gmcp("Char.Combat", "junk") ; gmcp("Char.Vitals", "{") ; gmcp("Char.XP", '{ "per_hour": 3 }')
check(true, "junk on the three new packages does not raise")

-- 29. the fight numbers survive a restart and keep averaging
arena()
combat("a troll", 0) ; CLOCK = CLOCK + 10 ; combat("a troll", 5)
death("a troll", "Keyser") ; xp(6000)
tick() ; on_disc()
fresh_client({ keep = true })
room(10, "Pit", "Arena") ; contents({ { "a troll", "monster", 1 } })
vitals(1000) ; xp(6000)
combat("a troll", 0) ; CLOCK = CLOCK + 20 ; combat("a troll", 9)
death("a troll", "Keyser") ; xp(8000)
tick()
t = book("Arena").troll
check(me(t).fights == 2 and me(t).secs == 30 and me(t).rounds == 14 and me(t).xp == 3000 and me(t).xpn == 2, "the reloaded entry keeps adding up")
typed("mdb troll")
check(said("2 fights measured: 15s and 7 rounds on average") and said("1500 xp a kill"), "and the averages are over both")

-- 30. the Best tab is capped; the command is not
fresh_client()
room(10, "Pit", "Arena") ; vitals(1000) ; xp(0)
for i = 1, 22 do
  local name = "beast " .. string.char(96 + i)
  combat(name, 0) ; CLOCK = CLOCK + 1
  death(name, "Keyser") ; xp(i * (i + 1) / 2 * 100)   -- beast a pays 100, b 200, ...
  tick()
end
view()
do
  local rows = 0
  check(has(st("best"), "22 monsters, best 2200 xp") and not has(st("best"), "Beast"), "one line for the area while it is closed (" .. st("best") .. ")")
  typed("mdb fold Arena")
  for l in st("best"):gmatch("[^\n]+") do if l:find("click=mdb view Arena|beast", 1, true) then rows = rows + 1 end end
  check(rows == 22, "opened: every monster of the area (" .. rows .. ")")
  check(st("best"):find("Beast v", 1, true) < st("best"):find("Beast u", 1, true), "best first")
end
typed("mdb best")
check(said("Beast a") and said("Beast v"), "mdb best prints them all")

-- ---------------------------------------------------------------- 0.3.0: per character
-- 31. the same troll is a different fight for each character
arena()                                  -- Keyser: 10 seconds, 1000 xp
combat("a troll", 0) ; vitals(900) ; CLOCK = CLOCK + 10 ; combat("a troll", 5)
death("a troll", "Keyser") ; xp(6000)
tick() ; on_disc()
fresh_client({ keep = true, new_char = true })
check(PRIVATE.me == nil, "a second character does not inherit the first one's name")
typed("mdb me Goran")
room(10, "Pit", "Arena") ; contents({ { "a troll", "monster", 1 } })
view()
check(has(st("here"), "seen 2x, 1 kill (0 yours)"), "the monster's own facts are shared: seen and killed (" .. st("here") .. ")")
check(not has(st("here"), " xp") and not has(st("here"), "10s"), "but Keyser's fight is not shown as Goran's")
check(has(st("best"), "no kill has been measured for Goran"), "Goran's Best tab is empty")
check(has(st("area"), "Troll") and not has(st("area"), "10s"), "and the area table has no time for him yet")
vitals(400, 500) ; xp(100)
combat("a troll", 0) ; vitals(150, 500) ; CLOCK = CLOCK + 30 ; combat("a troll", 14)
death("a troll", "Goran") ; xp(2100)
tick()
t = book("Arena").troll
check(t.kills == 2, "both kills count for the monster")
check(me(t, "Keyser").fights == 1 and me(t, "Keyser").secs == 10 and me(t, "Keyser").xp == 1000 and me(t, "Keyser").kills == 1, "Keyser's numbers are untouched")
check(me(t, "Goran").fights == 1 and me(t, "Goran").secs == 30 and me(t, "Goran").xp == 2000 and me(t, "Goran").kills == 1, "Goran's are his own")
check(me(t, "Goran").low == 30 and me(t, "Keyser").low == 90, "the lowest point is per character too")
view()
check(has(st("best"), "best 2000 xp"), "Goran's Best tab counts Goran's fight (" .. st("best") .. ")")
typed("mdb fold Arena")
check(has(st("best"), "30s") and has(st("best"), "2000") and not has(st("best"), "10s"), "and opened it shows his numbers, not Keyser's (" .. st("best") .. ")")
check(has(st("area"), "30s"), "and so does the area table")
typed("mdb troll")
check(said("2 kills (1 yours)") and said("1 fight measured: 30s and 14 rounds"), "the entry leads with the character you play")
check(said("Keyser: 1 kill, 10s a fight, 1000 xp, low 90%"), "and lists the other one beneath")
check(not said("Goran: "), "without repeating your own as an 'other'")
-- back on Keyser
on_disc()
fresh_client({ keep = true, new_char = true })
typed("mdb me Keyser")
room(10, "Pit", "Arena") ; contents({ { "a troll", "monster", 1 } })
view()
check(has(st("here"), "~1000 xp, 10s") and has(st("here"), "2 kills (1 yours)"), "Keyser sees Keyser's fight (" .. st("here") .. ")")
typed("mdb troll")
check(said("Goran: 1 kill, 30s a fight, 2000 xp, low 30%"), "with Goran beneath")

-- 32. fights before the name is known are kept unnamed, and become yours when it is learned
fresh_client()
room(10, "Pit", "Arena") ; contents({ { "a troll", "monster", 2 } })
vitals(1000) ; xp(0)
combat("a troll", 0) ; CLOCK = CLOCK + 8 ; combat("a troll", 4)
death("a troll", "Keyser") ; xp(500)
tick()
t = book("Arena").troll
check(t.by["?"] ~= nil and t.by["?"].fights == 1 and t.by.Keyser == nil, "no name yet: the fight is kept unnamed")
view()
check(has(st("here"), "~500 xp, 8s"), "and still shown as yours")
combat("a troll", 0) ; CLOCK = CLOCK + 4 ; combat("a troll", 2)
death("a troll", "Keyser")
line("You dealt the killing blow to a troll.")   -- the name arrives while this kill's XP is settling
xp(900)
check(said("the fights recorded before it was known are now Keyser's"), "learning the name says what happened to them")
tick()
t = book("Arena").troll
check(t.by["?"] == nil, "nothing is left unnamed")
check(t.by.Keyser.fights == 2 and t.by.Keyser.secs == 12 and t.by.Keyser.kills == 1, "both fights are Keyser's (and the kill the line named)")
check(t.by.Keyser.xp == 900 and t.by.Keyser.xpn == 2, "the XP that settled after the name was learned went to the name")
check(PRIVATE.me == "Keyser" and SHARED.me == nil, "the name is kept in the private store only")

-- 33. a book written by 0.2.0: one character's numbers on the monster itself, the name shared
fresh_client()
SHARED["area:Arena"] = '{"troll":{"name":"Troll","seen":4,"kills":3,"mine":2,"max":1,"first":5,"last":6,"rooms":{"r10":{"n":4,"name":"Pit"}},'
  .. '"fights":2,"secs":24,"rounds":10,"xp":2400,"xpn":2,"xplast":1300,"dmg":300,"dmgn":2,"low":55,"open":1}}'
SHARED.me = "Keyser"
fresh_client({ keep = true })
check(PRIVATE.me == "Keyser" and SHARED.me == nil, "the shared name moves to the private store")
room(10, "Pit", "Arena") ; contents({ { "a troll", "monster", 1 } })
view()
check(has(st("here"), "~1200 xp, 12s") and has(st("here"), "3 kills (2 yours)"), "the old numbers read as that character's (" .. st("here") .. ")")
tick()
t = book("Arena").troll
check(t.fights == nil and t.mine == nil and t.xp == nil and t.low == nil, "they are off the monster itself")
check(t.by.Keyser.fights == 2 and t.by.Keyser.secs == 24 and t.by.Keyser.xp == 2400 and t.by.Keyser.kills == 2
      and t.by.Keyser.dmg == 300 and t.by.Keyser.low == 55 and t.by.Keyser.open == 1 and t.by.Keyser.xplast == 1300,
      "and under the name, every one of them")
check(t.seen == 5 and t.kills == 3, "the monster's own counts carry on")
fresh_client({ keep = true })
room(10, "Pit", "Arena") ; contents({ { "a troll", "monster", 1 } })
tick()
check(book("Arena").troll.by.Keyser.fights == 2, "loading twice does not move or double anything")

-- ---------------------------------------------------------------- 0.4.0: notes, tags, files
local function two_areas()
  fresh_client()
  room(900, "Sewer", "Undercity") ; contents({ { "a rat", "monster", 2 }, { "a rat king", "monster", 1 } })
  room(55, "Cellar", "Inn") ; contents({ { "a rat", "monster", 1 }, { "an innkeeper", "monster", 1 } })
  tick()
end

-- 34. a note is yours: shown where the monster is, kept through the feed and a restart
two_areas()
typed("mdb note innkeeper = sells   keys; do NOT kill @ night")
check(said("noted on Innkeeper (Inn): sells   keys; do NOT kill @@ night"), "a note is set, its '@' escaped for the markup")
view()
check(has(st("here"), "sells   keys; do NOT kill @@ night"), "here shows it (" .. st("here") .. ")")
check(has(st("notes"), "Innkeeper") and has(st("notes"), "Inn") and has(st("notes"), "sells   keys"), "the Notes tab lists it")
tick()
check(book("Inn").innkeeper.note == "sells   keys; do NOT kill @ night", "stored as typed")
room(56, "Bar", "Inn") ; contents({ { "an innkeeper", "monster", 1 } })
death("an innkeeper", "Goran")
tick() ; on_disc()
fresh_client({ keep = true })
check(book("Inn").innkeeper.note == "sells   keys; do NOT kill @ night" and book("Inn").innkeeper.seen == 2, "the feed adds to the entry and leaves the note alone")
typed("mdb innkeeper")
check(said("sells   keys") and said("prompt=mdb note innkeeper = }[note]") and said("prompt=mdb tag innkeeper = }[tag]"), "the entry shows it, with links that start the next one")
typed("mdb note innkeeper")
check(said("Innkeeper") and said("sells   keys"), "mdb note <name> with no '=' shows the note")
typed("mdb note innkeeper = " .. string.rep("x", 300))
tick()
check(#book("Inn").innkeeper.note == 200, "a note is clipped at 200 characters")
typed("mdb note innkeeper =")
check(said("note removed from Innkeeper"), "nothing after '=' removes it")
tick()
check(book("Inn").innkeeper.note == nil, "and it is gone from the store")
view()
check(has(st("notes"), "no notes or tags yet"), "the Notes tab is empty again")

-- 35. tags: a few plain words, sorted; the ones that warn are red
two_areas()
typed("mdb tag rat king = Quest, AVOID  avoid good-xp!")
check(said("Rat king (Undercity) is tagged @{error}avoid@{} @{info}good-xp@{} @{info}quest@{}"), "tags are lower-cased, de-duplicated and sorted; avoid is red")
tick()
do
  local tg = book("Undercity")["rat king"].tags
  check(#tg == 3 and tg[1] == "avoid" and tg[2] == "good-xp" and tg[3] == "quest", "stored as a list")
end
room(900, "Sewer", "Undercity") ; contents({ { "a rat king", "monster", 1 } })
view()
check(has(st("here"), "@{error}avoid@{}") and has(st("area"), "@{error}avoid@{}"), "here and the area table carry the tags")
typed("mdb tag innkeeper = quest")
typed("mdb tagged quest")
check(said("Rat king") and said("Innkeeper") and not said("Sewer"), "mdb tagged <tag> lists every entry with it")
typed("mdb tagged avoid")
check(said("Rat king") and not said("Innkeeper"), "and only those")
typed("mdb tagged")
check(said("click=mdb tagged quest}quest@{}@{dim}  2") and said("avoid") and said("good-xp"), "mdb tagged lists the tags in use with their counts")
typed("mdb tagged nothing")
check(said("nothing is tagged 'nothing'"), "an unused tag says so")
typed("mdb tag rat king = a b c d e f g h i j")
tick()
check(#book("Undercity")["rat king"].tags == 8, "eight tags at most")
typed("mdb tag rat king =")
check(said("tags removed from Rat king"), "nothing after '=' removes them")
tick()
check(book("Undercity")["rat king"].tags == nil, "gone from the store")

-- 36. which entry: where you stand, else the only one, else it asks
two_areas()                              -- standing in the Inn; 'rat' is in both areas
typed("mdb note rat = cellar rats")
tick()
check(book("Inn").rat.note == "cellar rats" and book("Undercity").rat.note == nil, "the same name in two areas: the one where you stand")
room(1, "Square", "Town") ; contents({})
typed("mdb note rat = x")
check(said("'rat' is in Inn, Undercity - stand in the one you mean"), "standing in neither: it asks")
typed("mdb tag innk = quest")
check(said("Innkeeper (Inn) is tagged"), "part of a name is enough when only one entry matches")
typed("mdb note ra = x")
check(said("entries match 'ra' - give the whole name"), "several partial matches: it asks")
typed("mdb note dragon = x")
check(said("nothing in the book matches 'dragon'"), "no such monster")

-- 37. a clicked name is looked up, never obeyed
fresh_client()
room(1, "Den", "Caves") ; contents({ { "a troll", "monster", 1 }, { "forget troll confirm", "monster", 1 } })
view()
check(has(st("here"), "click=mdb view Caves|forget troll confirm}"), "a name's link in the panel runs 'mdb view'")
typed("mdb view Caves|forget troll confirm")
check(#printed == 0 and has(st("here"), "seen 1x"), "so a monster named like a command is opened, not obeyed")
typed("mdb here")
check(said("click=mdb show forget troll confirm}"), "in the output pane its link runs 'mdb show'")
typed("mdb show forget troll confirm")
check(said("Forget troll confirm") and not said("forgot"), "which shows it too")
tick()
check(book("Caves").troll ~= nil, "and nothing was deleted")

-- 38. export
FILES = {}
fresh_client()
typed("mdb export")
check(said("nothing to export"), "an empty book is not exported")
fresh_client({ no_exports = true })
typed("mdb export") ; check(said("needs Scrye 1.10.0"), "a host without scrye.exports: export says so")
typed("mdb import x.json") ; check(said("needs Scrye 1.10.0"), "and so does import")
arena({ { "a troll", "monster", 2 } })
combat("a troll", 0) ; vitals(700) ; CLOCK = CLOCK + 10 ; combat("a troll", 5)
death("a troll", "Keyser") ; xp(6000)
tick()
typed("mdb note troll = regenerates")
typed("mdb tag troll = avoid")
typed("mdb ignore hirdmadr")
typed("mdb export")
local FILE = "3s-compendium-20261121-1700.json"
check(FILES[FILE] ~= nil and said("1 monster exported: C:/exports/" .. FILE), "the book is written under a dated name")
do
  local d = decode(FILES[FILE])
  check(d.format == "3s-compendium" and d.ver == 1 and d.areas.Arena.troll.kills == 1 and d.areas.Arena.troll.by.Keyser.secs == 10
        and d.areas.Arena.troll.note == "regenerates", "the file holds the entry, the character's numbers and the note")
end
typed("mdb import")
check(said("prompt=mdb import " .. FILE .. "}" .. FILE), "mdb import with no file lists what is there")

-- 39. import into an empty book: everything is new; twice: nothing changes
fresh_client()                           -- another machine: an empty book, the same exports folder
typed("mdb import " .. FILE)
check(said(FILE .. ": 1 new, 0 updated, 0 already the same"), "an empty book takes it all")
tick()
t = book("Arena").troll
check(t.seen == 1 and t.kills == 1 and t.max == 2 and t.rooms.r10.n == 1 and t.rooms.r10.name == "Pit", "the monster's own facts arrive")
check(t.by.Keyser.fights == 1 and t.by.Keyser.secs == 10 and t.by.Keyser.xp == 1000 and t.by.Keyser.low == 70, "a character's numbers arrive under the name")
check(t.note == "regenerates" and t.tags[1] == "avoid", "the note and the tags arrive")
writes = {}
typed("mdb import " .. FILE)
check(said(FILE .. ": 0 new, 0 updated, 1 already the same"), "the same file again changes nothing")
tick()
check(#writes == 0, "and writes nothing")

-- 40. import over a book of your own: the larger of each, your note, both sets of tags
fresh_client()
room(10, "Pit", "Arena") ; vitals(1000) ; xp(0)
typed("mdb me Keyser")
for i = 1, 3 do                          -- three sightings, three kills, three fights of 4 seconds
  room(11, "Gate", "Arena") ; contents({})
  room(10, "Pit", "Arena") ; contents({ { "a troll", "monster", 1 } })
  combat("a troll", 0) ; CLOCK = CLOCK + 4 ; combat("a troll", 2)
  death("a troll", "Keyser") ; xp(i * 100)
  combat(nil) ; tick()
end
typed("mdb note troll = mine")
typed("mdb tag troll = quest")
typed("mdb import " .. FILE)
check(said("0 new, 1 updated, 0 already the same"), "an overlapping entry is updated")
tick()
t = book("Arena").troll
check(t.seen == 3 and t.kills == 3, "counts are the larger side's, never the sum (seen " .. t.seen .. ", kills " .. t.kills .. ")")
check(t.max == 2, "the file's larger group is taken")
check(t.rooms.r10.n == 3, "a room's visits likewise")
check(t.by.Keyser.fights == 3 and t.by.Keyser.secs == 12, "your own numbers stand: you have fought it more than the file's Keyser")
check(t.note == "mine", "your note wins")
check(#t.tags == 2 and t.tags[1] == "avoid" and t.tags[2] == "quest", "the tags are both sets")

-- 41. a file is not trusted
fresh_client()
typed("mdb ignore hirdmadr")
FILES["3s-compendium-bad.json"] = encode({ format = "3s-compendium", ver = 1, areas = {
  Arena = {
    ["troll"] = { name = "Troll", seen = -5, kills = "many", max = 2.9, rooms = { r10 = { n = 3, name = "Pit" }, ["../x"] = { n = 9 }, r11 = { n = -1 } },
                  by = { Goran = { fights = 2, secs = 20, low = 250, xp = -4 }, ["?"] = { fights = 9 }, ["Bad}Name"] = { fights = 9 }, Empty = { junk = "x" } },
                  note = "line one\nline two", tags = { "Avoid", 7, "ok" } },
    ["evil}@{error,click=drop all}"] = { name = "x", seen = 1 },
    ["Troll"] = { name = "Troll", seen = 1 },           -- not a key this plugin writes
    ["hirdmadr"] = { name = "Hirdmadr", seen = 4 },     -- ignored here
    ["ghost"] = "not an entry",
    ["sprite"] = { name = "x}@{error,click=drop all}Sprite", seen = 2 },
  },
  ["Bad@Area"] = { rat = { name = "Rat", seen = 1 } },
} })
typed("mdb import 3s-compendium-bad.json")
check(said("2 new, 0 updated, 0 already the same, 5 skipped"), "what is usable is taken, the rest counted as skipped")
tick()
t = book("Arena").troll
check(t.seen == 0 and t.kills == 0 and t.max == 2, "negative and non-numeric counts are not taken; a fraction is floored")
check(t.rooms.r10 ~= nil and t.rooms["../x"] == nil and t.rooms.r11 == nil, "only r<number> rooms with a real count")
check(t.by.Goran ~= nil and t.by.Goran.fights == 2 and t.by.Goran.low == nil and t.by.Goran.xp == nil, "a character's numbers are checked one by one")
check(t.by["?"] == nil and t.by["Bad}Name"] == nil and t.by.Empty == nil, "the unnamed, the unprintable and the empty are left out")
check(t.note == "line one line two", "a note's control characters become spaces")
check(#t.tags == 2 and t.tags[1] == "avoid" and t.tags[2] == "ok", "tags are cleaned like typed ones")
check(book("Arena").sprite.name == "Sprite", "a name that is not plain text is replaced by the key's own")
check(book("Arena").hirdmadr == nil and book("Arena").ghost == nil and book("Arena").Troll == nil and book("Bad@Area") == nil, "the rest never reached the book")
do
  local leaked = false
  view()
  for _, k in ipairs({ "here", "area", "best", "notes" }) do if has(st(k), "drop all") then leaked = true end end
  typed("mdb area arena") ; if said("drop all") then leaked = true end
  typed("mdb sprite") ; if said("drop all") then leaked = true end
  check(not leaked, "no view shows a command that came from a file")
end
FILES["3s-compendium-other.json"] = '{ "format": "3s-map", "ver": 1, "areas": {} }'
typed("mdb import 3s-compendium-other.json")
check(said("is not a book exported by 'mdb export'"), "another plugin's file is refused")
FILES["3s-compendium-new.json"] = '{ "format": "3s-compendium", "ver": 2, "areas": {} }'
typed("mdb import 3s-compendium-new.json")
check(said("exported by a newer compendium"), "a newer format is refused")
FILES["3s-compendium-junk.json"] = "{ not json"
typed("mdb import 3s-compendium-junk.json")
check(said("is not a book exported by 'mdb export'"), "junk is refused")
typed("mdb import nothere.json")
check(said("could not read nothere.json: no such file"), "a missing file says so")

-- 42. an import respects the room cap
fresh_client()
do
  local rooms = {}
  for i = 1, 60 do rooms["r" .. i] = { n = i, name = "Room " .. i } end
  FILES["3s-compendium-rooms.json"] = encode({ format = "3s-compendium", ver = 1, areas = { Maze = { minotaur = { name = "Minotaur", seen = 60, rooms = rooms } } } })
end
typed("mdb import 3s-compendium-rooms.json")
tick()
do
  local n = 0
  for _ in pairs(book("Maze").minotaur.rooms) do n = n + 1 end
  check(n == 40 and book("Maze").minotaur.rooms.r60 ~= nil and book("Maze").minotaur.rooms.r20 == nil, "40 rooms kept, the most visited (" .. n .. ")")
end

-- ---------------------------------------------------------------- 0.5.0: drops, the mapper, the bus
-- the floor of a room, in the shapes the 2 Sep 2026 capture has (corpse names and all)
local function floor(mobs, items, page, pages)
  local list = {}
  for _, m in ipairs(mobs) do list[#list + 1] = { name = m[1], type = "monster", count = m[2] or 1 } end
  for _, it in ipairs(items) do list[#list + 1] = { name = it[1], type = "item", count = it[2] or 1 } end
  if #list == 0 then gmcp("Room.Contents", '{ "full": 1, "items": [] }') ; return end
  gmcp("Room.Contents", encode({ full = 1, page = page, pages = pages, items = list }))
end
local PISTOL, BAG = "A scrappy looking pistol", "Bag of dried worm protein"
local FRESH, MOLDY = "The sliced up, freshly slain corpse of Wiremouth", "The bashed up, slightly moldy corpse of Wiremouth"
local function megacity()
  fresh_client()
  room(44942, "Grinder Street", "Megacity")
end
local function settle() CLOCK = CLOCK + 3 ; tick() end

-- 43. the dead monster leaves the list: what grew on the floor in that list fell from it
megacity()
floor({ { "Wiremouth guard", 2 } }, { { "A rusty sign", 1 } })
death("Wiremouth guard", "Keyser")
floor({ { "Wiremouth guard", 1 } }, { { FRESH, 1 }, { "A rusty sign", 1 }, { PISTOL, 1 }, { BAG, 1 }, { "A large pile of gold coins", 1 } })
tick()
check(book("Megacity")["wiremouth guard"].lootn == nil, "drops are not read in the same second as the death")
settle()
local wg = book("Megacity")["wiremouth guard"]
check(wg.lootn == 1, "one kill watched")
check(wg.loot["scrappy looking pistol"].n == 1 and wg.loot["scrappy looking pistol"].name == "Scrappy looking pistol" and wg.loot["bag of dried worm protein"].n == 1, "the pistol and the bag are its drops")
check(wg.loot.coins ~= nil and wg.loot.coins.n == 1, "a pile of coins is 'coins'")
check(wg.loot["rusty sign"] == nil, "what was already lying there is not a drop")
do
  local corpse = false
  for k in pairs(wg.loot) do if k:find("corpse", 1, true) then corpse = true end end
  check(not corpse, "the corpse is the marker, not a drop")
end
-- the second kill: the list arrives BEFORE the death this time, the pile of coins only changes its name
CLOCK = CLOCK + 60
floor({}, { { FRESH, 1 }, { MOLDY, 1 }, { "A rusty sign", 1 }, { PISTOL, 2 }, { BAG, 1 }, { "A really large pile of gold coins", 1 } })
CLOCK = CLOCK + 1                        -- a second ahead of the death
death("Wiremouth guard", "Keyser")
settle()
wg = book("Megacity")["wiremouth guard"]
check(wg.lootn == 2 and wg.loot["scrappy looking pistol"].n == 2, "a list that arrives just before the death counts too (pistol " .. wg.loot["scrappy looking pistol"].n .. "/" .. wg.lootn .. ")")
check(wg.loot["bag of dried worm protein"].n == 1, "what did not grow was not dropped this time")
check(wg.loot.coins.n == 2, "a pile that grew by changing its name is a drop")
typed("mdb wiremouth guard")
check(said("left on the floor: Coins @{dim}2x@{}, Scrappy looking pistol @{dim}2x@{}, Bag of dried worm protein @{dim}1x@{}@{dim} (2 kills watched)@{}"), "the entry lists what was seen on the floor, most frequent first")
typed("mdb drops pistol")
check(said("Scrappy looking pistol @{dim}2x floor@{}") and said("click=mdb show wiremouth guard}") and said("Megacity"), "mdb drops <item> says who left it")
typed("mdb drops unicorn")
check(said("nothing in the book has given 'unicorn'"), "an item nobody gave")

-- 44. what is not counted
megacity()                               -- two deaths at once: whose is whose?
floor({ { "Wiremouth guard", 1 }, { "Dagger Jack", 1 } }, {})
death("Wiremouth guard", "Keyser")
floor({ { "Dagger Jack", 1 } }, { { FRESH, 1 }, { PISTOL, 1 } })
death("Dagger Jack", "Keyser")           -- the same second, each with a list of its own
floor({}, { { FRESH, 1 }, { "The corpse of Dagger Jack", 1 }, { PISTOL, 1 }, { "A dagger", 1 } })
settle()
check(book("Megacity")["wiremouth guard"].lootn == nil and book("Megacity")["dagger jack"].lootn == nil, "two deaths in the same moment: neither is charged with the floor")
megacity()                               -- the corpse was destroyed: no corpse, its things on the floor
floor({ { "Wiremouth guard", 1 } }, { { MOLDY, 1 } })
death("Wiremouth guard", "Keyser")
floor({}, { { MOLDY, 1 }, { PISTOL, 1 } })
settle()
check(book("Megacity")["wiremouth guard"].lootn == 1 and book("Megacity")["wiremouth guard"].loot["scrappy looking pistol"] ~= nil, "a destroyed corpse: what it carried is read off the floor")
megacity()                               -- the list never showed the monster leaving
floor({ { "Wiremouth guard", 2 } }, {})
death("Wiremouth guard", "Keyser")
floor({ { "Wiremouth guard", 2 } }, { { PISTOL, 1 } })   -- another walked in as this one fell
settle()
check(book("Megacity")["wiremouth guard"].lootn == nil, "the monster's count did not fall by one: not read")
megacity()                               -- a kill that leaves nothing
floor({ { "A rat", 1 } }, { { PISTOL, 3 } })
death("A rat", "Keyser")
floor({}, { { "The corpse of a rat", 1 }, { PISTOL, 2 } })   -- and you picked a pistol up meanwhile
settle()
check(book("Megacity").rat.lootn == 1 and next(book("Megacity").rat.loot) == nil, "a watched kill with nothing new: counted, no drops, a pickup is not one")
typed("mdb rat")
check(not said("on the floor") and not said("dropped"), "and the entry claims nothing about it - the loot is in the corpse")
megacity()                               -- you walked on before the list came
floor({ { "A rat", 1 } }, {})
death("A rat", "Keyser")
room(44943, "Grinder Street north", "Megacity")
floor({}, { { "The corpse of a rat", 1 }, { PISTOL, 1 } })
settle()
check(book("Megacity").rat.lootn == nil, "another room's floor is not compared")
megacity()                               -- a paged list is one list
floor({ { "A rat", 1 } }, { { PISTOL, 1 } }, 1, 2) ; floor({}, { { BAG, 1 } }, 2, 2)
death("A rat", "Keyser")
floor({}, { { "The corpse of a rat", 1 }, { PISTOL, 1 } }, 1, 2)
tick()
floor({}, { { BAG, 2 } }, 2, 2)
settle()
check(book("Megacity").rat.lootn == 1 and book("Megacity").rat.loot["bag of dried worm protein"] ~= nil
      and book("Megacity").rat.loot["scrappy looking pistol"] == nil, "pages are joined before the floor is compared")

-- 45. drops travel in an export, checked like the rest
megacity()
floor({ { "A rat", 1 } }, {})
death("A rat", "Keyser")
floor({}, { { "The corpse of a rat", 1 }, { PISTOL, 1 } })
settle()
FILES = {}
typed("mdb export")
fresh_client()
typed("mdb import " .. FILE)
tick()
check(book("Megacity").rat.lootn == 1 and book("Megacity").rat.loot["scrappy looking pistol"].n == 1, "drops arrive with an import")
FILES["3s-compendium-loot.json"] = encode({ format = "3s-compendium", ver = 1, areas = { Megacity = { rat = { name = "Rat", seen = 1, lootn = 4,
  loot = { ["scrappy looking pistol"] = { n = 3, name = "Scrappy looking pistol" }, ["x}@{error,click=drop all}"] = { n = 9, name = "x" },
           ["tail"] = { n = 2, name = "y}@{error,click=drop all}z" }, ["ear"] = { n = -1, name = "Ear" } } } } } })
typed("mdb import 3s-compendium-loot.json")
tick()
local rt = book("Megacity").rat
check(rt.lootn == 4 and rt.loot["scrappy looking pistol"].n == 3, "the larger counts are kept")
check(rt.loot.tail ~= nil and rt.loot.tail.name == "Tail" and rt.loot.ear == nil, "an item's name is checked like a monster's; a bad count is left out")
do
  local n = 0
  for _ in pairs(rt.loot) do n = n + 1 end
  check(n == 2, "and a key that could break a link never arrives")
end

-- 46. a room in an entry is the mapper's: click for the route, the menu walks
typed("mdb rat")
check(said("menu=Walk there|mapg go 44942;Show the route|mapg path 44942,click=mapg path 44942}Grinder Street@{}"), "the room is a link into the mapper")

-- 47. the bus: who is tagged, asked for or told
two_areas()
emitted = {}
typed("mdb tag rat king = avoid quest")
do
  local av, qu
  for _, m in ipairs(emitted) do
    if m.name == "mdb.tagged" then
      local d = decode(m.data)
      if d.tag == "avoid" then av = d elseif d.tag == "quest" then qu = d end
    end
  end
  check(av ~= nil and av.areas.Undercity[1] == "rat king" and qu ~= nil, "tagging broadcasts each tag's new list")
end
emitted = {}
typed("mdb tag rat king = quest")
check(#emitted == 1 and decode(emitted[1].data).tag == "avoid" and next(decode(emitted[1].data).areas) == nil, "dropping a tag broadcasts that one, now empty; an unchanged tag is not repeated")
typed("mdb tag rat = avoid")             -- standing in the Inn
typed("mdb tag rat king = avoid")
emitted = {}
event("mdb.query.tagged", '{ "tag": "Avoid" }')
do
  local d = #emitted == 1 and decode(emitted[1].data) or { areas = {} }
  check(emitted[1] and emitted[1].name == "mdb.tagged" and d.tag == "avoid" and d.areas.Inn and d.areas.Inn[1] == "rat"
        and d.areas.Undercity and d.areas.Undercity[1] == "rat king", "a query is answered with every area's keys")
end
emitted = {}
event("mdb.query.tagged", "junk") ; event("mdb.query.tagged", "{}")
check(#emitted == 0, "a query with no tag is not answered")
typed("mdb forget rat king confirm")
check(#emitted == 1 and decode(emitted[1].data).tag == "avoid" and decode(emitted[1].data).areas.Undercity == nil, "forgetting a tagged monster tells the listeners")

-- ---------------------------------------------------------------- 0.6.0: the Best tab by area
-- 48. areas in order of their best monster; each opens on its own; more than 30 are counted
fresh_client()
vitals(1000) ; xp(0)
do
  local total = 0
  local function kill(area, num, name, worth)
    room(num, "Room " .. num, area)
    combat(name, 0) ; CLOCK = CLOCK + 5
    total = total + worth
    death(name, "Keyser") ; xp(total)
    combat(nil) ; tick()
  end
  kill("Farmland", 1, "a hen", 50) ; kill("Farmland", 1, "a bull", 900)
  kill("Megacity", 2, "a drone", 4000)
  kill("Caves", 3, "a bat", 300)
  for i = 1, 32 do kill("Zoo", 4, "animal " .. string.format("%02d", i), 10 + i) end
end
view()
do
  local b = st("best")
  local mc, fl, cv, zo = b:find("+ Megacity", 1, true), b:find("+ Farmland", 1, true), b:find("+ Caves", 1, true), b:find("+ Zoo", 1, true)
  check(mc and fl and cv and zo and mc < fl and fl < cv and cv < zo, "areas are listed by their best monster (" .. b .. ")")
  check(has(b, "2 monsters, best 900 xp") and has(b, "1 monster, best 4000 xp"), "each with how many it holds and the best of them")
  local lines = 0
  for _ in b:gmatch("[^\n]+") do lines = lines + 1 end
  check(lines == 4, "closed, the tab is one line an area (" .. lines .. ")")
end
typed("mdb fold Farmland")
do
  local b = st("best")
  check(has(b, "- Farmland") and has(b, "+ Megacity") and has(b, "Bull") and has(b, "Hen") and not has(b, "Drone"), "one area opened, the others stay closed")
  check(b:find("Bull", 1, true) < b:find("Hen", 1, true), "its monsters best first")
  check(b:find("Hen", 1, true) < b:find("+ Caves", 1, true), "and they sit under their own area")
end
typed("mdb fold Megacity")
check(has(st("best"), "- Farmland") and has(st("best"), "- Megacity") and has(st("best"), "Drone"), "a second area opens beside the first")
typed("mdb fold Zoo")
do
  local rows = 0
  for l in st("best"):gmatch("[^\n]+") do if l:find("click=mdb view Zoo|animal", 1, true) then rows = rows + 1 end end
  check(rows == 30 and has(st("best"), "... 2 more: 'mdb best Zoo' prints them all"), "30 monsters under an area, the rest counted (" .. rows .. ")")
end
typed("mdb best")
check(said("Drone") and said("Bull") and said("Bat") and said("Megacity"), "mdb best still prints the flat list")

-- ---------------------------------------------------------------- 0.7.0: taken (text)
-- 49. the lines of Keyser's log (21 Nov 2026), in its order, after "Wiremouth ... slumps into death"
megacity()
typed("mdb me Keyser")
floor({ { "Wiremouth", 1 }, { "Wiremouth guard", 1 } }, {})
vitals(6197, 6197) ; xp(1000)
local function wiremouth_dies(coins)
  combat("Wiremouth", 0) ; CLOCK = CLOCK + 6 ; combat("Wiremouth", 3)
  death("Wiremouth", "Keyser")
  line("Keyser dealt the killing blow to Wiremouth.")
  line("You bind 1 Faint soul from the remains.")
  line("You salvage 1 Hyper-charged Science Token from the remains.")
  line("Stabby solemnly lays the foe to rest.")
  line("You pick up The glowing, freshly slain corpse of Wiremouth and wrap it")
  line("up into the coffin's protective hold!")
  CLOCK = CLOCK + 4
  line("There is no corpse here to reclaim.")
  line("coins (" .. coins .. "): Taken.")
  combat(nil) ; tick()
end
wiremouth_dies(1600)
local wm = book("Megacity").wiremouth
check(wm.coins == 1600 and wm.coinn == 1, "'coins (1600): Taken.' is 1600 coins from the monster that just died")
check(wm.take["faint soul"] ~= nil and wm.take["faint soul"].n == 1 and wm.take["faint soul"].name == "Faint soul", "'You bind 1 Faint soul from the remains.' is booked to it")
check(wm.take["hyper-charged science token"] ~= nil and wm.take["hyper-charged science token"].q == 1, "and so is what was salvaged")
do
  local n = 0
  for _ in pairs(wm.take) do n = n + 1 end
  check(n == 2 and wm.take.coins == nil, "nothing else in the log is an item - coins are not listed twice (" .. n .. ")")
end
check(book("Megacity")["wiremouth guard"].coins == nil and book("Megacity")["wiremouth guard"].take == nil, "the guard standing beside it gets none of it")
floor({ { "Wiremouth", 1 }, { "Wiremouth guard", 1 } }, {})
wiremouth_dies(2000)
line("You salvage 3 Hyper-charged Science Token from the remains.")
tick()
wm = book("Megacity").wiremouth
check(wm.coins == 3600 and wm.coinn == 2, "coins add up over kills")
check(wm.take["faint soul"].n == 2 and wm.take["hyper-charged science token"].n == 3 and wm.take["hyper-charged science token"].q == 5, "times taken, and how many in all")
typed("mdb wiremouth")
check(said("coins: ~1800 a pickup@{dim} (2x, 3600 in all)@{}"), "the entry: what a pickup is worth")
check(said("taken after a kill: Hyper-charged Science Token @{dim}3x (5 in all)@{}, Faint soul @{dim}2x@{}"), "the entry: what was taken, most often first")
typed("mdb drops token")
check(said("Hyper-charged Science Token @{dim}3x taken@{}") and said("click=mdb show wiremouth}"), "mdb drops finds what was taken")
room(44943, "Grinder Street north", "Megacity") ; room(44942, "Grinder Street", "Megacity")
floor({ { "Wiremouth", 1 } }, {})
view()
check(has(st("here"), "~1800 coins"), "here: the coins beside the fight (" .. st("here") .. ")")
typed("mdb fold Megacity")
check(has(st("best"), "Coins") and has(st("best"), "1800"), "the Best tab has a Coins column (" .. st("best") .. ")")

-- 50. whose it is: the last kill, in this room, not long ago
megacity()
floor({ { "A rat", 1 }, { "A bat", 1 } }, {})
line("coins (50): Taken.")               -- nothing has died
line("You bind 1 Faint soul from the remains.")
death("A rat", "Goran") ; death("A bat", "Goran")
line("coins (70): Taken.")
tick()
check(book("Megacity").bat.coins == 70 and book("Megacity").rat.coins == nil, "two deaths: the last one's")
check(book("Megacity").bat.take == nil, "a line before any kill is nobody's")
CLOCK = CLOCK + 21
line("coins (900): Taken.") ; line("a rusty sword: Taken.")
tick()
check(book("Megacity").bat.coins == 70 and book("Megacity").bat.take == nil, "twenty seconds on, a pickup is not the kill's any more")
death("A bat", "Goran")
line("a rusty sword: Taken.")
room(44943, "Grinder Street north", "Megacity")
line("coins (5): Taken.")
tick()
check(book("Megacity").bat.take["rusty sword"] ~= nil and book("Megacity").bat.take["rusty sword"].name == "Rusty sword", "any '<item>: Taken.' after a kill is booked")
check(book("Megacity").bat.coins == 70, "what you pick up in the next room is not")
typed("mdb ignore bat")
room(44942, "Grinder Street", "Megacity")
death("A bat", "Goran") ; line("coins (5): Taken.")
tick()
check(book("Megacity").bat.coins == 70, "an ignored monster's death books nothing - and does not pass it to the kill before")

-- 51. the name the killing-blow line prints: asked once, one click to answer
megacity()
floor({ { "Wiremouth", 2 } }, {})
combat("Wiremouth", 0) ; death("Wiremouth", "Keyser")
check(said("Keyser killed what you were fighting. If that is you: @{accent,click=mdb me Keyser}mdb me Keyser@{}"), "with no name known, the killer of your own fight is offered as you")
printed = {}
combat("Wiremouth", 0) ; death("Wiremouth", "Keyser")
check(not said("If that is you"), "once for a name, not at every kill")
death("Wiremouth guard", "Goran")
check(not said("If that is you"), "a kill you were not fighting asks nothing")
typed("mdb me Keyser")
printed = {}
floor({ { "Wiremouth", 1 } }, {})
combat("Wiremouth", 0) ; death("Wiremouth", "Stabby")
check(not said("If that is you"), "and with a name known nobody is asked")

-- 52. coins and what was taken travel in an export
megacity()
floor({ { "A rat", 1 } }, {})
death("A rat", "Goran") ; line("coins (40): Taken.") ; line("You bind 2 Faint soul from the remains.")
tick()
FILES = {}
typed("mdb export")
fresh_client()
typed("mdb import " .. FILE)
tick()
rt = book("Megacity").rat
check(rt.coins == 40 and rt.coinn == 1 and rt.take["faint soul"].n == 1 and rt.take["faint soul"].q == 2, "they arrive with an import")
FILES["3s-compendium-take.json"] = encode({ format = "3s-compendium", ver = 1, areas = { Megacity = { rat = { name = "Rat", seen = 1,
  coins = 900, coinn = 3, take = { ["faint soul"] = { n = 4, q = 9, name = "Faint soul" }, ["x}@{error,click=drop all}"] = { n = 1, q = 1, name = "x" },
                                   ["gem"] = { n = "lots", name = "Gem" } } } } } })
typed("mdb import 3s-compendium-take.json")
tick()
rt = book("Megacity").rat
check(rt.coins == 900 and rt.coinn == 3, "the side with more pickups keeps its coins, total and count together")
FILES["3s-compendium-few.json"] = encode({ format = "3s-compendium", ver = 1, areas = { Megacity = { rat = { name = "Rat", seen = 1, coins = 10, coinn = 1 } } } })
typed("mdb import 3s-compendium-few.json")
tick()
check(book("Megacity").rat.coins == 900 and book("Megacity").rat.coinn == 3, "a file with fewer pickups does not replace yours")
rt = book("Megacity").rat
check(rt.take["faint soul"].n == 4 and rt.take["faint soul"].q == 9 and rt.take.gem == nil, "the larger counts of what was taken; a bad count is left out")
do
  local n = 0
  for _ in pairs(rt.take) do n = n + 1 end
  check(n == 1, "and a key that could break a link never arrives")
end

-- ---------------------------------------------------------------- 0.7.1: your name from your own chat
-- 53. the payloads of Keyser's capture (21 Nov 2026): an incoming tell, then one he sent
megacity()
floor({ { "Wiremouth guard", 2 } }, {})
vitals(6197, 6197) ; xp(1000)
combat("Wiremouth guard", 0) ; CLOCK = CLOCK + 9 ; combat("Wiremouth guard", 89, 1)
death("Wiremouth guard", "Keyser") ; xp(4800000)
combat(nil) ; tick()
check(PRIVATE.me == nil and book("Megacity")["wiremouth guard"].by["?"] ~= nil, "before any message the fight is unnamed")
gmcp("Comm.Channel.Text", '{ "text": "nice :)", "talker": "Rocky", "prefix": "Rocky tells you:", "channel": "tell" }')
check(PRIVATE.me == nil, "somebody else's tell names nobody")
printed = {}
gmcp("Comm.Channel.Text", '{ "text": "hello", "prefix": "You tell Rocky:", "outgoing": 1, "talker": "Keyser", "targets": [ "Rocky" ], "channel": "tell" }')
check(PRIVATE.me == "Keyser" and said("your character is Keyser (you sent a message as Keyser)"), "a message you sent names you")
tick()
check(book("Megacity")["wiremouth guard"].by["?"] == nil and book("Megacity")["wiremouth guard"].by.Keyser.fights == 1, "and the unnamed fight becomes yours")
printed = {}
gmcp("Comm.Channel.Text", '{ "text": "again", "outgoing": 1, "talker": "Keyser", "channel": "tell" }')
check(#printed == 0, "the same name again says nothing")
typed("mdb me Goran")
gmcp("Comm.Channel.Text", '{ "text": "x", "outgoing": 1, "talker": "Keyser", "channel": "tell" }')
check(PRIVATE.me == "Keyser", "a name typed wrong is corrected by the next message you send")
gmcp("Comm.Channel.Text", '{ "text": "x", "outgoing": 1, "talker": "Bad}Name", "channel": "tell" }')
gmcp("Comm.Channel.Text", '{ "text": "x", "outgoing": 1, "talker": "", "channel": "tell" }')
gmcp("Comm.Channel.Text", "junk")
check(PRIVATE.me == "Keyser", "an unprintable or empty talker changes nothing")

-- ---------------------------------------------------------------- 0.8.0: levels of an area
-- 54. Megacity: the room's own name says which floor it is (names from Keyser's and Goran's captures)
fresh_client()
typed("mdb me Keyser")
vitals(6197, 6197) ; xp(0)
do
  local total = 0
  local function guard(num, name, worth, secs)
    room(num, name, "Megacity")
    contents({ { "Wiremouth guard", "monster", 2 } })
    combat("Wiremouth guard", 0) ; CLOCK = CLOCK + secs ; combat("Wiremouth guard", 5)
    total = total + worth
    death("Wiremouth guard", "Keyser") ; xp(total)
    combat(nil) ; tick()
  end
  guard(44559, "Central Plaza Open Core floor 10", 1000, 10)
  guard(44401, "Floor 30   Elevator Lobby", 5000, 40)
  guard(44942, "Central Plaza Open Core floor 100", 9000, 90)
  guard(45664, "Grinder Street", 300, 5)
  room(700, "Den", "Caves") ; contents({ { "a bat", "monster", 1 } })
  combat("a bat", 0) ; CLOCK = CLOCK + 5
  total = total + 7000
  death("a bat", "Keyser") ; xp(total) ; combat(nil) ; tick()
end
check(book("Megacity [floor 10]") ~= nil and book("Megacity [floor 30]") ~= nil and book("Megacity [floor 100]") ~= nil, "each floor is its own section of the book")
check(book("Megacity") ~= nil and book("Megacity")["wiremouth guard"].kills == 1, "a room that names no floor stays under the plain area")
check(me(book("Megacity [floor 10]")["wiremouth guard"]).secs == 10 and me(book("Megacity [floor 30]")["wiremouth guard"]).secs == 40
      and me(book("Megacity [floor 30]")["wiremouth guard"]).xp == 5000, "the same guard is measured per floor, not clumped")
typed("mdb")
do
  local order = {}
  for i, l in ipairs(printed) do
    for _, z in ipairs({ "Megacity@{}", "Megacity [floor 10]@{}", "Megacity [floor 30]@{}", "Megacity [floor 100]@{}" }) do
      if l:find(z, 1, true) then order[z] = i end
    end
  end
  check(order["Megacity@{}"] and order["Megacity@{}"] < order["Megacity [floor 10]@{}"] and order["Megacity [floor 10]@{}"] < order["Megacity [floor 30]@{}"]
        and order["Megacity [floor 30]@{}"] < order["Megacity [floor 100]@{}"], "mdb lists the floors in floor order, 30 before 100")
end
room(800, "Desk", "Megacity Annex") ; contents({ { "a clerk", "monster", 1 } })
room(700, "Den", "Caves") ; contents({})
typed("mdb area megacity")
check(said("Megacity [floor 10]") and said("Megacity [floor 30]") and said("Megacity [floor 100]"), "mdb area <area> prints every level of it")
check(not said("Megacity Annex") and not said("Clerk"), "and not another area that merely has the name in its own")
typed("mdb area megacity ann")
check(said("Megacity Annex") and said("Clerk"), "which part of its name still finds")
typed("mdb area megacity [floor 30]")
check(said("Megacity [floor 30]") and not said("Megacity [floor 10]") and said("40s"), "mdb area with the whole section name prints that one")
typed("mdb area floor 10")
check(said("Megacity [floor 10]") and said("Megacity [floor 100]") and not said("floor 30"), "part of a name still finds what contains it")
typed("mdb area")
check(said("Caves") and said("Bat"), "mdb area alone is still where you stand")
view()
do
  local b = st("best")
  local f100, f30, f10, street, caves = b:find("+ Megacity [floor 100]", 1, true), b:find("+ Megacity [floor 30]", 1, true),
        b:find("+ Megacity [floor 10]", 1, true), b:find("+ Megacity@{}", 1, true), b:find("+ Caves", 1, true)
  check(f100 and f30 and f10 and street and caves and street < f10 and f10 < f30 and f30 < f100 and f100 < caves,
        "the Best tab keeps an area's levels together and in order, though the Caves pay better than three of them (" .. b .. ")")
end
typed("mdb fold Megacity [floor 30]")
check(has(st("best"), "- Megacity [floor 30]") and has(st("best"), "+ Megacity [floor 10]") and has(st("best"), "5000"), "a level opens on its own")
typed("mdb best megacity")
do
  local n = 0
  for _, l in ipairs(printed) do if l:find("Wiremouth guard", 1, true) then n = n + 1 end end
  check(n == 4 and not said("Bat"), "mdb best <area> ranks every level of it and nothing else (" .. n .. ")")
end
room(44560, "Central Plaza Open Core floor 10", "Megacity") ; contents({ { "Wiremouth guard", "monster", 1 } })
view()
check(has(st("here"), "Megacity [floor 10]") and has(st("here"), "~1000 xp, 10s") and has(st("area_head"), "Megacity [floor 10]"), "here and the Area tab are the floor you stand on (" .. st("here") .. ")")
-- other plugins still hear the AREA
typed("mdb tag wiremouth guard = avoid")
room(44402, "Floor 30 Hall", "Megacity") ; contents({ { "Wiremouth guard", "monster", 1 } })
emitted = {}
typed("mdb tag wiremouth guard = avoid")
do
  local d = decode(emitted[#emitted].data)
  check(d.areas.Megacity ~= nil and #d.areas.Megacity == 1 and d.areas.Megacity[1] == "wiremouth guard" and d.areas["Megacity [floor 30]"] == nil,
        "mdb.tagged names the area, each monster once, whatever its levels")
end

-- 55. the Sea of Chaos: every room is number 60494, and its level is the sea you opened
fresh_client()
check(on_cmd ~= nil, "the plugin watches the commands that go out")
on_cmd("setsea 60")
check(decode(PRIVATE.levels)["The Sea of Chaos"] == "sea 60", "'setsea 60' is remembered, for this character")
local function sea(layer, mobs)
  gmcp("Room.Info", encode({ num = 60494, name = layer, area = "The Sea of Chaos", exits = { n = 60494, d = 60494, s = 0 } }))
  contents(mobs)
end
sea("The surface", { { "a chaos eel", "monster", 1 } })
sea("The surface", { { "a chaos eel", "monster", 2 } })
sea("The shallows", { { "a chaos eel", "monster", 1 } })
tick()
check(book("The Sea of Chaos [sea 60]") ~= nil and book("The Sea of Chaos") == nil, "the sea is booked under the number it was opened with")
check(book("The Sea of Chaos [sea 60]")["chaos eel"].seen == 3, "each sea room is a visit although the room number never changes (seen " .. book("The Sea of Chaos [sea 60]")["chaos eel"].seen .. ")")
on_cmd("  SetSea 62 ") ; on_cmd("setsea") ; on_cmd("say setsea 99") ; on_cmd("setsea 6x")
sea("The surface", { { "a chaos eel", "monster", 1 } })
tick()
check(book("The Sea of Chaos [sea 62]") ~= nil and book("The Sea of Chaos [sea 62]")["chaos eel"].seen == 1 and book("The Sea of Chaos [sea 99]") == nil,
      "a new sea number starts a new section; only a real 'setsea <n>' counts")
on_disc()
fresh_client({ keep = true })
sea("The surface", { { "a chaos eel", "monster", 1 } })
tick()
check(book("The Sea of Chaos [sea 62]")["chaos eel"].seen == 2, "the sea number is still known after a restart")
fresh_client({ keep = true, new_char = true })
sea("The surface", { { "a chaos eel", "monster", 1 } })
tick()
check(book("The Sea of Chaos") ~= nil and book("The Sea of Chaos")["chaos eel"].seen == 1, "another character has not opened a sea: its eels go under the plain area until it does")

-- 56. any other area: the level set by hand
fresh_client()
typed("mdb level 5")
check(said("no room yet"), "mdb level needs a room")
room(1, "Entrance", "Dragon Isle") ; contents({ { "a drake", "monster", 1 } })
typed("mdb level")
check(said("you are in @{bold}Dragon Isle@{}") and said("no level set"), "mdb level shows where you are booked")
typed("mdb level Tier 2 (hard)!")
check(said("recording Dragon Isle under @{bold}Dragon Isle [Tier 2 hard]@{} from now on"), "mdb level <text> sets it, plain characters only")
room(2, "Cave", "Dragon Isle") ; contents({ { "a drake", "monster", 1 } })
tick()
check(book("Dragon Isle [Tier 2 hard]") ~= nil and book("Dragon Isle").drake.seen == 1, "from then on the area is booked under the level")
room(3, "Hoard floor 7", "Dragon Isle") ; contents({ { "a drake", "monster", 1 } })
tick()
check(book("Dragon Isle [Tier 2 hard]").drake.seen == 2 and book("Dragon Isle [floor 7]") == nil, "a level you set wins over a floor in the room's name")
typed("mdb level")
check(said("Dragon Isle [Tier 2 hard]") and said("'mdb level -' clears it"), "mdb level shows the level in force")
typed("mdb level -")
check(said("level cleared for Dragon Isle - recording under Dragon Isle [floor 7]"), "mdb level - clears it (and the room's floor shows again)")
typed("mdb level -")
check(said("no level is set for Dragon Isle"), "clearing twice says so")
typed("mdb level !!!")
check(said("a level is letters, digits"), "a level with nothing usable in it is refused")

-- ---------------------------------------------------------------- 0.9.0: the entry opens in the panel
-- 57. a clicked name opens its entry under its own row - in every tab - and prints nothing
arena({ { "a troll", "monster", 1 }, { "a goblin", "monster", 1 } })
combat("a troll", 0) ; vitals(700) ; CLOCK = CLOCK + 12 ; combat("a troll", 6)
death("a troll", "Keyser") ; xp(6200) ; line("coins (300): Taken.")
combat(nil) ; tick()
typed("mdb note troll = regenerates")
contents({ { "a troll", "monster", 1 }, { "a goblin", "monster", 1 } })
view()
check(not has(st("here"), "1 fight measured") and not has(st("area"), "1 fight measured"), "closed: only the summary shows")
typed("mdb view Arena|troll")
check(#printed == 0, "a click in the panel prints nothing to the output pane")
do
  local h = st("here")
  check(has(h, "1 fight measured: 12s and 6 rounds") and has(h, "1200 xp a kill") and has(h, "coins: ~300 a pickup") and has(h, "regenerates"),
        "here: the whole entry is under the troll (" .. h .. ")")
  check(has(h, "click=mapg path 10}Pit@{}") and has(h, "prompt=mdb note troll = }[note]"), "with its room link and the note/tag links")
  check(h:find("1 fight measured", 1, true) < h:find("Goblin", 1, true), "between the troll's row and the next monster's")
  check(has(h, "@{accent,bold,click=mdb view Arena|troll}Troll@{}"), "and the opened name is bold")
  local n = 0
  for _ in h:gmatch("Troll") do n = n + 1 end
  check(n == 1, "the entry's own header line is not repeated under the row (" .. n .. ")")
end
check(has(st("area"), "1 fight measured: 12s") and has(st("notes"), "1 fight measured: 12s"), "the Area and Notes tabs show it open too")
typed("mdb fold Arena")
check(has(st("best"), "  1 fight measured: 12s") or has(st("best"), "     1 fight measured: 12s"), "and so does the Best tab, under the troll's row (" .. st("best") .. ")")
typed("mdb view Arena|goblin")
check(not has(st("here"), "1 fight measured") and has(st("here"), "@{accent,bold,click=mdb view Arena|goblin}"), "opening another monster closes the first")
typed("mdb view Arena|troll") ; typed("mdb view Arena|troll")
check(not has(st("here"), "1 fight measured") and not has(st("here"), ",bold,click=mdb view"), "a second click on the open one closes it")
typed("mdb view Arena|dragon") ; typed("mdb view Nowhere|troll") ; typed("mdb view troll")
check(#printed == 0 and not has(st("here"), "1 fight measured"), "a view of something that is not in the book does nothing")
typed("mdb view Arena|troll")
typed("mdb here")
check(said("click=mdb show troll}Troll") and not said("1 fight measured"), "'mdb here' in the output pane is the summary, with links that print")
typed("mdb troll")
check(said("1 fight measured: 12s and 6 rounds"), "'mdb <name>' still prints the entry")
typed("mdb forget troll confirm")
view()
check(not has(st("area"), "1 fight measured") and not has(st("area"), "Troll"), "forgetting the opened monster leaves nothing behind")

-- ---------------------------------------------------------------- 0.10.0: the Items tab
-- 58. everything monsters have given, by area; a search narrows it and opens what it finds
local function item_box()
  for _, t in ipairs(panel.tabs) do
    if t.title == "Items" then for _, w in ipairs(t.widgets) do if w.type == "input" then return w end end end
  end
end
fresh_client()
view()
check(has(st("items"), "no items yet"), "an empty book says how items get there")
check(item_box() ~= nil and item_box().bind == "plugin.3s-compendium.itembox" and type(item_box().onSubmit) == "function", "the Items tab has a search box")
do
  local function loot(num, room_name, area, mob, lines)
    room(num, room_name, area)
    contents({ { mob, "monster", 1 } })
    death(mob, "Keyser")
    for _, l in ipairs(lines) do line(l) end
    tick()
  end
  loot(1, "Hall floor 10", "Megacity", "Wiremouth guard", { "coins (100): Taken.", "a rusty sword: Taken." })
  loot(1, "Hall floor 10", "Megacity", "Wiremouth guard", { "a rusty sword: Taken." })
  loot(1, "Hall floor 10", "Megacity", "Dagger Jack", { "a rusty sword: Taken.", "a dagger: Taken." })
  loot(2, "Hall floor 30", "Megacity", "Wiremouth captain", { "a Sword of flame: Taken.", "You bind 1 Faint soul from the remains." })
  loot(3, "Den", "Caves", "a bat", { "a bat wing: Taken." })
  room(4, "Pit", "Caves")                  -- and one left on the floor by a destroyed corpse
  floor({ { "a troll", 1 } }, {})
  death("a troll", "Keyser")
  floor({}, { { "A broadsword", 1 } })
  CLOCK = CLOCK + 3 ; tick()
  line("A broadsword: Taken.") ; tick()    -- seen on the floor and then picked up: still one sword
end
view()
do
  local it = st("items")
  check(has(it, "click=mdb ifold Caves}+ Caves@{}@{dim}  2 items") and has(it, "+ Megacity [floor 10]@{}@{dim}  3 items") and has(it, "+ Megacity [floor 30]@{}@{dim}  2 items"),
        "closed: one line an area, with how many different items (" .. it .. ")")
  check(not has(it, "sword") and not has(it, "Sword"), "and no item shows until an area is opened")
  check(it:find("+ Caves", 1, true) < it:find("+ Megacity [floor 10]", 1, true) and it:find("+ Megacity [floor 10]", 1, true) < it:find("+ Megacity [floor 30]", 1, true),
        "areas by name, an area's levels in order")
end
typed("mdb ifold Megacity [floor 10]")
check(#printed == 0, "opening an area prints nothing")
do
  local it = st("items")
  check(has(it, "- Megacity [floor 10]"), "the area is open")
  check(has(it, "Rusty sword @{dim}3x  from@{} @{accent,click=mdb view Megacity [floor 10]|wiremouth guard}Wiremouth guard@{}@{dim},@{} @{accent,click=mdb view Megacity [floor 10]|dagger jack}Dagger Jack@{}"),
        "an item is one line: how often in all, and from whom, most often first (" .. it .. ")")
  check(has(it, "Coins @{dim}1x  from@{}") and has(it, "Dagger @{dim}1x  from@{}"), "coins are an item like the others")
  check(it:find("  Coins", 1, true) < it:find("  Dagger", 1, true) and it:find("  Dagger", 1, true) < it:find("  Rusty sword", 1, true), "items by name")
  check(not has(it, "Sword of flame") and not has(it, "Bat wing"), "the other areas stay closed")
end
typed("mdb ifold Megacity [floor 10]")
check(has(st("items"), "+ Megacity [floor 10]") and not has(st("items"), "Rusty sword"), "a second click closes it")
-- the search box
item_box().onSubmit("  Sword ")
do
  local it = st("items")
  check(has(it, "search: @{bold}sword@{}@{dim}  3 items in 3 areas@{}  @{accent,click=mdb items -}[show all]@{}"), "a search says what it found (" .. it .. ")")
  check(has(it, "@{bold}Caves@{}") and has(it, "Broadsword @{dim}1x  from@{}") and has(it, "click=mdb view Caves|troll}Troll"), "what was left on the floor is found too, with its area")
  check(has(it, "@{bold}Megacity [floor 10]@{}") and has(it, "Rusty sword @{dim}3x") and has(it, "@{bold}Megacity [floor 30]@{}") and has(it, "Sword of flame @{dim}1x"),
        "every area with a match is shown opened")
  check(not has(it, "Dagger @{dim}") and not has(it, "Bat wing") and not has(it, "Coins") and not has(it, "Faint soul"), "and only the matching items")
  check(not has(it, "click=mdb ifold"), "a search result is not folded")
  check(state["plugin.3s-compendium.itembox"] == "sword", "the box keeps the search")
end
typed("mdb view Megacity [floor 30]|wiremouth captain")
check(has(st("items"), "taken after a kill: Faint soul") and has(st("items"), ",bold,click=mdb view Megacity [floor 30]|wiremouth captain}"), "a clicked monster opens under its item here as well")
typed("mdb view Megacity [floor 30]|wiremouth captain")
item_box().onSubmit("unicorn")
check(has(st("items"), "search: @{bold}unicorn@{}") and has(st("items"), "nothing a monster has given has that in its name"), "a search with no match says so")
typed("mdb items -")
check(said("the Items tab shows everything") and has(st("items"), "+ Caves") and not has(st("items"), "search:") and state["plugin.3s-compendium.itembox"] == "", "'mdb items -' (the [show all] link) clears it")
typed("mdb items soul")
check(said("the Items tab shows what has 'soul' in its name") and has(st("items"), "Faint soul @{dim}1x") and not has(st("items"), "sword"), "'mdb items <word>' searches from the command line")
item_box().onSubmit("")
check(not has(st("items"), "search:") and has(st("items"), "+ Megacity [floor 30]"), "an empty search shows everything")
typed("mdb drops coins")
check(said("Coins @{dim}1x taken@{}") and said("Wiremouth guard"), "mdb drops finds coins too")
-- more items than an opened area shows
fresh_client()
room(1, "Vault", "Bank") ; contents({ { "a teller", "monster", 1 } })
death("a teller", "Keyser")
for i = 1, 20 do line("gem " .. string.format("%02d", i) .. ": Taken.") end
tick()
room(1, "Vault", "Bank") ; contents({ { "a guard", "monster", 1 } })
death("a guard", "Keyser")
for i = 21, 40 do line("gem " .. string.format("%02d", i) .. ": Taken.") end
tick()
room(1, "Vault", "Bank") ; contents({ { "a clerk", "monster", 1 } })
death("a clerk", "Keyser")
line("gem 41: Taken.") ; line("gem 42: Taken.")
tick()
typed("mdb ifold Bank")
do
  local rows = 0
  for l in st("items"):gmatch("[^\n]+") do if l:find("  Gem ", 1, true) then rows = rows + 1 end end
  check(rows == 40 and has(st("items"), "... 2 more: search for part of the name"), "40 items under an area, the rest counted (" .. rows .. ")")
end

-- ---------------------------------------------------------------- 0.10.1: "<item>: Ok."
-- 59. Keyser's log of 22 Nov 2026, in its order: a kill whose items answer "Ok."
fresh_client()
room(500, "Lair", "Chaos Wastes") ; contents({ { "Kresh", "monster", 1 } })
death("Kresh", "Keyser")
for _, l in ipairs({
  "Keyser dealt the killing blow to Kresh.",
  "You salvage 1 Cracked Chaos Token from the remains.",
  "You've killed Kresh.",
  "HP:6197/6197 SP:303/310 PU:7751/7800(3900) CPC:10000/10000",
  "[G2N:60%][56%] E:none",
  "Stabby solemnly lays the foe to rest.",
  "The coffin is full! The corpse was not wrapped.",
  "[[ Tactical Enhancement Termination in 21 mins 2 secs ]]",
  "[[ You are Stabilized ]]",
  "[>      Reclamation Complete [590 PU].     <]",
  "[> Power Unit Recharge Complete. <]",
  "Kresh Coat: Ok.",
  "Kresh Claws: Ok.",
  "There is no corpse here.",
  "Your insidious inspiration fades.",
}) do line(l) end
tick()
local kr = book("Chaos Wastes").kresh
do
  local n = 0
  for _ in pairs(kr.take or {}) do n = n + 1 end
  check(n == 3, "three things were taken from Kresh, and nothing else in the log is one (" .. n .. ")")
end
check(kr.take["kresh coat"] ~= nil and kr.take["kresh coat"].name == "Kresh Coat" and kr.take["kresh claws"] ~= nil, "'Kresh Coat: Ok.' and 'Kresh Claws: Ok.' are items taken")
check(kr.take["cracked chaos token"] ~= nil and kr.coins == nil, "the salvage is there, and no coins were invented")
typed("mdb drops kresh c")
check(said("Kresh Claws @{dim}1x taken@{}") and said("Kresh Coat @{dim}1x taken@{}"), "mdb drops finds them")
typed("mdb ifold Chaos Wastes")
check(has(st("items"), "Kresh Coat @{dim}1x  from@{}") and has(st("items"), "click=mdb view Chaos Wastes|kresh}Kresh"), "and so does the Items tab")
CLOCK = CLOCK + 30
line("A rusty key: Ok.")
tick()
check(book("Chaos Wastes").kresh.take["rusty key"] == nil, "an 'Ok.' long after the kill is not booked")

print(pass .. " passed, " .. fail .. " failed")
os.exit(fail == 0 and 0 or 1)
