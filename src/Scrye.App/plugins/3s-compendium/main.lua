-- 3S Compendium - a personal hunting log that fills itself in.
--
-- Nothing is typed in and nothing is sent to the MUD: the plugin listens to feeds the
-- client already has and keeps one entry per monster per area.
--
--   Room.Info / map.room   where you are (room number, room name, area)
--   Room.Contents          who is in the room (type "monster": name and count)
--   Room.Death             an npc died here (killer, name)
--   Char.Combat            who is fighting you (attacker, rounds) - 0.2.0
--   Char.Vitals            your hit points while it does - 0.2.0
--   Char.XP                your experience, read around each kill - 0.2.0
--
-- An entry is keyed by the monster's name with the article and the server's tags stripped
-- ("Wiremouth {angry}" and "the wiremouth" are one monster), inside the area it was met
-- in - two monsters that share a name in different areas stay apart, and a wanderer gets
-- an entry per area it was seen in ('mdb <name>' shows them all).
--
-- A SIGHTING is one visit to a room with the monster in it: walking into a room of three
-- orcs is one sighting with a group of 3, however often the server refreshes the list
-- while you stand there. Leave and come back and it is a second sighting.
--
-- A KILL is a Room.Death with npc set, whoever dealt the blow; YOURS is the share whose
-- killer is your character. No GMCP package states your name, so it is learned the way
-- the farmer learns it - a "You dealt the killing blow to X." line paired with the
-- Room.Death for X names you - or told with 'mdb me <name>'. Since 0.7.1 it is also read
-- off any chat line you send (Comm.Channel.Text with "outgoing": 1 carries your name).
--
-- A FIGHT (0.2.0) is a monster that Char.Combat named as your attacker. When it dies its
-- entry gains the fight's length (seconds and the server's own round count), the lowest
-- your hit points fell while it was on you, and what the kill was worth: Char.XP a second
-- after the blow minus Char.XP when the fight - or the kill before it - began, since every
-- hit pays and the kill's own award lands just after the blow. The hit points you lost
-- are charged to a monster only when it fought you alone; with two on you nobody can say
-- whose blow it was. A fight that ends with the monster alive (it fled, you did, you
-- walked on) is counted as unfinished. In a party the XP is your share, and a monster
-- somebody else was tanking never shows as your attacker - it is a kill with no fight.
--
-- PER CHARACTER (0.3.0). Where a monster lives and how often it has died are facts about
-- the monster, and every character adds to them. How long a fight takes, what a kill pays,
-- what it costs in hit points and which kills were yours are facts about whoever fought:
-- they are kept per character inside the entry (by = { <name> = ... }), and the panel and
-- the commands show the numbers of the character you are playing. 'mdb <name>' lists the
-- others' beneath your own. Until your name is known your fights are kept unnamed, and
-- they become yours the moment it is learned or set.
--
-- YOURS (0.4.0). Everything above is observed and the feed keeps rewriting it. A NOTE and
-- TAGS are yours: free text and a few words ("avoid", "quest", "good-xp") on an entry,
-- never touched by the feed, shared by all your characters, shown wherever the monster
-- is. 'mdb export' writes the whole book to a file in Scrye's exports folder and 'mdb
-- import <file>' merges one in - a backup, a move to another machine, or a friend's book.
-- The merge never adds counts together: it keeps the larger of each, so importing the
-- same file twice, or your own backup over a newer book, changes nothing. A character's
-- fight numbers come across whole, under that character's name. What a file says is not
-- trusted: names that could not have come from this plugin are skipped, not shown.
--
-- ON THE FLOOR (0.5.0, corrected in 0.5.1). A monster's loot is in its corpse, where no
-- feed shows it. But sometimes the corpse is destroyed and what it carried lands on the
-- floor, and Room.Contents lists the room's items as well as its monsters. So a kill is
-- watched: across the Room.Contents in which the dead monster's count falls by one,
-- whatever grew on the floor - corpses aside - fell from it. That is read only when it is
-- unambiguous (one death at a time), and it is a record of what HAS been seen, never a
-- drop rate: most kills leave everything in the corpse and show nothing here. Coins of any
-- pile size are one line. Something picked up faster than the server reports it is missed.
--
-- LEVELS OF AN AREA (0.8.0). Some areas are several hunting grounds under one name: the
-- Megacity's floors hold bigger monsters the higher you go, and a Sea of Chaos is as hard
-- as the number it was opened with. Clumped together their averages mean nothing, so such
-- an area is kept as one book section per level, written "Megacity [floor 10]":
--   * a room whose NAME says "floor <n>" is on that floor - any area, nothing to set up;
--   * the Sea of Chaos takes the number of the last 'setsea <n>' that went out ("sea 62");
--   * 'mdb level <text>' sets the level of the area you stand in by hand, for anywhere
--     else that works this way, and 'mdb level -' clears it.
-- A level set by command or by hand is this character's (private store) and stays until
-- it is changed; the sections themselves are shared like the rest of the book, since
-- floor 30 or sea 62 holds the same monsters for every character. Rooms that name no
-- floor stay under the plain area name. To other plugins the area is still the area:
-- mdb.tagged answers with "Megacity", every level's monsters together.
--
-- TAKEN (0.7.0). What you actually get from a kill is only ever in the text, so three
-- kinds of line are read (Keyser's log, 21 Nov 2026) and booked to the monster that died
-- last, when that was in this room and no more than 20 seconds ago:
--   coins (1600): Taken.                              coins, with the amount
--   <anything else>: Taken.                           an item picked up
--   You bind 1 Faint soul from the remains.           \ what a guild or the crafting
--   You salvage 1 Hyper-charged Science Token from the remains.   / system takes from it
-- So a monster's entry shows what a pickup of its coins is worth on average and what has
-- been taken after killing it. It is the last kill's by timing, nothing more: pick up
-- something unrelated within those seconds and it is booked too ('mdb forget' is the
-- remedy, or ignore it - the counts make a stray obvious).
--
-- OTHER PLUGINS (0.5.0). A room in an entry is a link: click for the mapper's route to it
-- ('mapg path'), right-click to walk there ('mapg go') - the compendium still sends
-- nothing itself. And it answers one question on the event bus, so a bot can respect
-- your tags without reading your store:
--   mdb.query.tagged { tag }            ->  mdb.tagged { tag, areas = { <area> = { <key>, ... } } }
-- The same answer is broadcast for a tag whenever an entry gains or loses it.
--
-- The book is kept per MUD (scrye.shared, API 1.14) so every character writes the same
-- one; on an older host it falls back to the private store. Which character you are is
-- kept in the private store, which is this world's alone. One key per area, written a
-- few seconds after the last change (and at once on disconnect), never per sighting.
--
--   mdb                 the areas in the book
--   mdb here            the monsters in this room
--   mdb area [name]     an area's monsters (this one when no name is given)
--   mdb <name>          every entry whose name contains <name> (in the panel, click a name:
--                       its entry opens under it, and closes on a second click)
--   mdb level [text|-]  show, set or clear the level of the area you stand in
--   mdb best [area]     what pays: the monsters with a measured kill, best XP first
--                       (the Best tab is the same by area: click an area to open or close it)
--   mdb drops <item>    which monsters have given it (taken after a kill, or left on the floor)
--   mdb note <name> = <text>   your note on it ('=' with nothing after it clears)
--   mdb tag <name> = <words>   your tags on it (likewise)     mdb tagged [tag]
--   mdb export          the book to a file      mdb import [file]   merge one in
--   mdb ignore <name>   never record it (your own followers are monster-typed too)
--   mdb unignore <name> record it again          mdb ignore   lists what is ignored
--   mdb forget <name>   delete its entries (asks for 'mdb forget <name> confirm')
--   mdb me [name]       show or set which killer is you

local P = "plugin." .. scrye.id .. "."
local TAG = "@{#7FA36B,bold}[mdb]@{} "
local ST = scrye.shared or scrye.store

local SAVE_DELAY = 5      -- seconds of quiet before the changed areas are written
local PAIR_SECS = 4       -- a killing-blow line and its Room.Death are this close together
local ROOM_CAP = 40       -- rooms remembered per entry; the least-visited one makes way
local SEA = "The Sea of Chaos"   -- the area whose level is the number of 'setsea <n>'
local UNKNOWN = "unknown" -- the area of a monster met before any room was announced
local FIGHT_GRACE = 2     -- a Room.Death may trail the end of combat by this much
local FIGHT_STALE = 10    -- no Char.Combat for this long: the fight is over
local XP_SETTLE = 1       -- the kill's own XP award lands just after the blow
local BEST_ROWS = 30      -- monsters shown under one opened area on the Best tab
local NOTE_MAX = 200      -- characters in a note
local TAG_MAX = 8         -- tags on an entry
local NOTES_ROWS = 40     -- entries on the Notes tab
local LOOT_WAIT = 2        -- seconds after a death before its drops are read
local LOOT_CAP = 20       -- different items remembered per entry
local TAKE_SECS = 20      -- a line about taking something belongs to a kill this recent
local SNAP_KEEP = 8       -- Room.Contents snapshots kept for the room you stand in
local EXPORT_FORMAT, EXPORT_VER = "3s-compendium", 1
local NO_EXPORTS = "this needs Scrye 1.10.0 or later (plugin API 1.21, scrye.exports) - update the client"

local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end
local function note(s) scrye.print(TAG .. s) end
local function now() return os.time() end
local function trim(s) return (tostring(s or ""):gsub("^%s+", ""):gsub("%s+$", "")) end
local function N(x) return tonumber(x) or 0 end

-- "Wiremouth {angry}" / "the wiremouth" / "A Wiremouth [2]"  ->  "wiremouth"
local function mob_key(name)
  local k = tostring(name or ""):gsub("%b[]", ""):gsub("%b{}", ""):gsub("%b()", "")
  k = trim(k:lower())
  k = k:gsub("^the ", ""):gsub("^an? ", "")
  return (k:gsub("%s+", " "))
end

-- the name as the book shows it: tags and article off, first letter up
local function mob_label(name)
  local n = tostring(name or ""):gsub("%b[]", ""):gsub("%b{}", ""):gsub("%b()", "")
  n = trim(n):gsub("^[Tt]he ", ""):gsub("^[Aa]n? ", ""):gsub("%s+", " ")
  if n == "" then return "?" end
  return n:sub(1, 1):upper() .. n:sub(2)
end

-- ---------------------------------------------------------------- the book
local BOOK = {}           -- area -> key -> entry
local IGN = {}            -- key -> true: never recorded
local ME = ""             -- your character's name, as Room.Death's killer spells it
local NOBODY = "?"        -- the character whose name is not known yet
local NONE = {}           -- what a character who never fought it has: nothing (read-only)
-- the fields that belong to whoever fought, not to the monster
local OWN = { "kills", "fights", "secs", "rounds", "xp", "xpn", "dmg", "dmgn", "open" }
local dirty = {}          -- area -> true: changed since the last write
local save_timer = nil

local HERE = { num = nil, name = "", area = "" }
local ROSTER = {}         -- the room's monsters now: { key=, name=, count= }, in feed order
local COUNTED = {}        -- key -> true: already a sighting during this visit
local view_dirty = false

-- this character's numbers for an entry: my() to read, mine() to write
local function who() return ME ~= "" and ME or NOBODY end
local function my(e) return (type(e.by) == "table" and e.by[who()]) or NONE end
local function mine(e)
  if type(e.by) ~= "table" then e.by = {} end
  local c = e.by[who()]
  if not c then c = {} ; e.by[who()] = c end
  return c
end

-- one character's numbers added into another's (the unnamed fights becoming yours)
local function merge_own(into, from)
  for _, k in ipairs(OWN) do
    if tonumber(from[k]) then into[k] = (tonumber(into[k]) or 0) + from[k] end
  end
  if tonumber(from.low) then into.low = math.min(tonumber(into.low) or 100, from.low) end
  if from.xplast ~= nil and into.xplast == nil then into.xplast = from.xplast end
end

local LEVELS = {}         -- area -> level label set by 'setsea' or 'mdb level' (this character's)

-- "Megacity [floor 10]" -> "Megacity": the area a section belongs to
local function base_of(zone) return (tostring(zone):match("^(.-) %[[^%[%]]*%]$")) or tostring(zone) end

-- names in their natural order: "floor 9" before "floor 10"
local function natural(s) return (tostring(s):lower():gsub("%d+", function(d) return string.format("%09d", tonumber(d)) end)) end

-- The book section for the room you stand in: the area, and its level when it has one -
-- the level set for the area, else the floor the room's own name states.
local function area_now()
  if not (HERE.num and HERE.area ~= "") then return UNKNOWN end
  local level = LEVELS[HERE.area]
  if not level then
    local n = tostring(HERE.name):lower():match("floor%s+(%d+)")
    if n then level = "floor " .. tonumber(n) end
  end
  return level and (HERE.area .. " [" .. level .. "]") or HERE.area
end

local function load()
  -- Which character this is lives in the PRIVATE store. 0.1.0 and 0.2.0 kept it in the
  -- shared one, where a second character would have read the first one's name: the first
  -- character to load 0.3.0 takes it over, and it is gone from the shared store.
  ME = tostring(scrye.store.get("me") or "")
  if ST ~= scrye.store then
    local old = ST.get("me")
    if old ~= nil then
      if ME == "" and tostring(old) ~= "" then ME = tostring(old) ; scrye.store.set("me", ME) end
      ST.delete("me")
    end
  end
  do
    local okl, lv = pcall(scrye.json.decode, scrye.store.get("levels") or "")
    if okl and type(lv) == "table" then
      for a, l in pairs(lv) do if type(l) == "string" and l ~= "" then LEVELS[tostring(a)] = l end end
    end
  end
  local keys = {}
  local okk, ks = pcall(ST.keys)
  if okk and type(ks) == "table" then keys = ks end
  for _, k in ipairs(keys) do
    local area = tostring(k):match("^area:(.+)$")
    if area then
      local ok, t = pcall(scrye.json.decode, ST.get(k) or "")
      if ok and type(t) == "table" then
        BOOK[area] = {}
        for key, e in pairs(t) do
          if type(e) == "table" then
            e.name = tostring(e.name or mob_label(key))
            e.seen = tonumber(e.seen) or 0
            e.kills = tonumber(e.kills) or 0
            e.max = tonumber(e.max) or 0
            if type(e.rooms) ~= "table" then e.rooms = {} end
            if type(e.by) ~= "table" then e.by = {} end
            -- an entry written before 0.3.0 has one character's numbers on the monster
            -- itself: they move to whoever loads it first
            local old = { kills = tonumber(e.mine), fights = e.fights, secs = e.secs, rounds = e.rounds, xp = e.xp,
                          xpn = e.xpn, dmg = e.dmg, dmgn = e.dmgn, open = e.open, low = e.low, xplast = e.xplast }
            if (old.kills or 0) == 0 then old.kills = nil end
            if next(old) ~= nil then
              merge_own(mine(e), old)
              dirty[area] = true
            end
            e.mine, e.fights, e.secs, e.rounds, e.xp, e.xpn = nil, nil, nil, nil, nil, nil
            e.dmg, e.dmgn, e.open, e.low, e.xplast = nil, nil, nil, nil, nil
            BOOK[area][tostring(key)] = e
          end
        end
      end
    end
  end
  local ok, ig = pcall(scrye.json.decode, ST.get("ignore") or "")
  if ok and type(ig) == "table" then for _, k in ipairs(ig) do IGN[tostring(k)] = true end end
end

local function save_now()
  save_timer = nil
  local batch, n = {}, 0
  for area in pairs(dirty) do
    local t = BOOK[area]
    if t and next(t) ~= nil then
      batch["area:" .. area] = scrye.json.encode(t)
      n = n + 1
    else
      BOOK[area] = nil
      ST.delete("area:" .. area)
    end
  end
  dirty = {}
  if n == 0 then return end
  if ST.setMany then ST.setMany(batch) else for k, v in pairs(batch) do ST.set(k, v) end end
end

local function touch(area)
  dirty[area] = true
  view_dirty = true
  if not save_timer then save_timer = scrye.after(SAVE_DELAY, save_now) end
end

local function save_ignore()
  local list = {}
  for k in pairs(IGN) do list[#list + 1] = k end
  table.sort(list)
  ST.set("ignore", scrye.json.encode(list))
end

local function entry(area, key, name)
  BOOK[area] = BOOK[area] or {}
  local e = BOOK[area][key]
  if not e then
    e = { name = mob_label(name), seen = 0, kills = 0, max = 0, rooms = {}, by = {} }
    BOOK[area][key] = e
  end
  return e
end

-- one more visit to this room with the monster in it
local function room_seen(e)
  if not HERE.num then return end
  local rk = "r" .. HERE.num
  local r = e.rooms[rk]
  if r then
    r.n = (tonumber(r.n) or 0) + 1
    if HERE.name ~= "" then r.name = HERE.name end
    return
  end
  local count, least, least_n = 0, nil, nil
  for k, v in pairs(e.rooms) do
    count = count + 1
    local vn = tonumber(v.n) or 0
    if not least_n or vn < least_n or (vn == least_n and k < least) then least, least_n = k, vn end
  end
  if count >= ROOM_CAP and least then e.rooms[least] = nil end
  e.rooms[rk] = { n = 1, name = HERE.name }
end

-- Every monster in the roster not yet counted during this visit becomes a sighting; the
-- largest group is kept up to date on every refresh (a fourth orc may walk in).
local function record_roster()
  if not HERE.num then return end   -- no room yet: the roster waits for one (see on_room)
  local area = area_now()
  for _, m in ipairs(ROSTER) do
    if not IGN[m.key] then
      local e = entry(area, m.key, m.name)
      if not COUNTED[m.key] then
        COUNTED[m.key] = true
        e.seen = e.seen + 1
        e.first = e.first or now()
        e.last = now()
        room_seen(e)
        touch(area)
      end
      if m.count > e.max then e.max = m.count ; touch(area) end
    end
  end
end

-- ---------------------------------------------------------------- fights
-- Char.Combat names ONE attacker per message and counts rounds per attacker from 0; a
-- second monster joining arrives as its own name at rounds 0. The end of combat is the
-- empty payload. It runs about once a second for as long as you fight, so the handler
-- only keeps a few numbers in memory: the book is touched when a fight is decided.
local XP = { xp = nil, base = nil }   -- base: Char.XP when the fight, or the last kill, began
local HP = { cur = nil, max = nil }
local F = { active = false, at = 0, ended = nil, A = {} }   -- A: key -> the attacker's fight so far

local function hp_pct()
  if not HP.cur or not HP.max or HP.max <= 0 then return nil end
  return HP.cur / HP.max * 100
end

-- the fight ended and this one was still standing
local function unfinished(key)
  local a = F.A[key]
  F.A[key] = nil
  if not a or IGN[key] then return end
  local e = entry(a.area, key, a.name)
  local c = mine(e)
  c.open = N(c.open) + 1
  e.first = e.first or now()
  if a.low then c.low = math.min(N(c.low) > 0 and c.low or 100, math.floor(a.low)) end
  touch(a.area)
end

local function fight_over()
  F.active = false
  F.ended = now()
end

local function on_combat(json)
  local ok, c = pcall(scrye.json.decode, json or "")
  if not ok or type(c) ~= "table" then return end
  local name = tostring(c.attacker or "")
  local hp = c.attacker_hp
  local dead = hp == 0 or hp == "0" or tostring(hp or ""):lower() == "dead"
  local rounds = tonumber(c.rounds) or 0
  -- a named attacker is a fight; a terminal that keeps the name at 0 rounds with its
  -- health dead is the end, as the empty payload is
  if name == "" or (dead and rounds <= 0) then
    if F.active then fight_over() end
    return
  end
  local key = mob_key(name)
  if key == "" then return end
  if not F.active then
    -- a new fight: whoever the last one left standing got away
    for k in pairs(F.A) do if k ~= key then unfinished(k) end end
    F.active = true
    F.ended = nil
    if XP.xp then XP.base = XP.xp end
  end
  F.at = now()
  if IGN[key] then return end
  local a = F.A[key]
  if not a then
    a = { t0 = now(), rounds = 0, loss = 0, low = hp_pct(), solo = true, area = area_now(), name = name }
    F.A[key] = a
    local n = 0
    for _ in pairs(F.A) do n = n + 1 end
    if n > 1 then for _, o in pairs(F.A) do o.solo = false end end
  end
  if rounds < a.rounds then a.solo = false end   -- the count restarted: a second one of the same name
  a.rounds = math.max(a.rounds, rounds)
end

local function on_vitals(json)
  local ok, v = pcall(scrye.json.decode, json or "")
  if not ok or type(v) ~= "table" then return end
  local hp = tonumber(v.hp)
  if not hp then return end
  local prev = HP.cur
  HP.cur = hp
  HP.max = tonumber(v.maxhp) or HP.max
  if not F.active then return end
  local pct = hp_pct()
  for _, a in pairs(F.A) do
    if prev and hp < prev then a.loss = a.loss + (prev - hp) end
    if pct and (not a.low or pct < a.low) then a.low = pct end
  end
end

local function on_xp(json)
  local ok, d = pcall(scrye.json.decode, json or "")
  if not ok or type(d) ~= "table" or tonumber(d.xp) == nil then return end
  XP.xp = tonumber(d.xp)
end

-- the monster you were fighting died: the fight's numbers go into its entry
local ASKED = {}          -- killer names already asked about this session ("is that you?")

local function fight_won(area, key, e)
  local a = F.A[key]
  if not a then return false end
  F.A[key] = nil
  local c = mine(e)
  c.fights = N(c.fights) + 1
  c.secs = N(c.secs) + math.max(0, now() - a.t0)
  c.rounds = N(c.rounds) + a.rounds
  if a.low then c.low = math.min(N(c.low) > 0 and c.low or 100, math.floor(a.low)) end
  if a.solo and HP.cur then
    c.dmg = N(c.dmg) + a.loss
    c.dmgn = N(c.dmgn) + 1
  end
  local base = XP.base
  if XP.xp and base then
    XP.base = nil   -- a second kill inside the settle is not charged with this one's XP
    scrye.after(XP_SETTLE, function()
      local gain = XP.xp - base
      if gain >= 0 then
        local cc = mine(e)   -- asked for now: the name may have been learned since the blow
        cc.xp = N(cc.xp) + gain
        cc.xpn = N(cc.xpn) + 1
        cc.xplast = gain
        touch(area)
      end
      if not XP.base then XP.base = XP.xp end   -- the next kill counts from here
    end)
  end
  return true
end

-- once a second: a fight that went quiet is over, and what it left standing is unfinished
local function fight_tick()
  if F.active and now() - F.at >= FIGHT_STALE then fight_over() end
  if not F.active and F.ended and now() - F.ended >= FIGHT_GRACE then
    F.ended = nil
    local keys = {}
    for k in pairs(F.A) do keys[#keys + 1] = k end
    for _, k in ipairs(keys) do unfinished(k) end
  end
end

-- ---------------------------------------------------------------- drops
local SNAPS = {}          -- this room's lists as they arrived: { t=, items={key={n,name}}, mobs={key=n} }
local BUILD = nil         -- a paged list being put together
local WATCH = {}          -- deaths waiting for their drops: { key=, area=, room=, at= }

local function is_corpse(name)
  local low = name:lower()
  return low:find(" corpse of ", 1, true) ~= nil or low:find(" remains of ", 1, true) ~= nil
      or low:find("^corpse of ") ~= nil or low:find("^remains of ") ~= nil
end

-- "A really large pile of gold coins" and "Some silver coins" are one thing: coins
local function item_key(name)
  local k = mob_key(name)
  if k:find("coins?$") then return "coins", "Coins" end
  return k, mob_label(name)
end

local function snap_item(it)
  local name = tostring(it.name or "")
  if name == "" then return end
  local n = tonumber(it.count) or 1
  if is_corpse(name) then return end   -- the corpse is where the loot usually is, not loot
  local key, label = item_key(name)
  if key == "" then return end
  local have = BUILD.items[key]
  if have then have.n = have.n + n else have = { n = n, name = label } ; BUILD.items[key] = have end
  -- a pile of coins grows by changing its NAME ("large" to "really large"), not its count
  if key == "coins" then have.raw = (have.raw or "") .. "|" .. name:lower() end
end

-- what one watched kill left on the floor: every item that grew across the list in which
-- the dead monster's count fell by one (with or without a corpse appearing)
local function drops_resolve(w)
  if HERE.num ~= w.room then return end
  for _, o in ipairs(WATCH) do
    if o ~= w and math.abs(o.at - w.at) <= LOOT_WAIT + 1 then return end   -- two deaths at once: whose is whose?
  end
  local a, b
  for i = #SNAPS, 2, -1 do
    local after, before = SNAPS[i], SNAPS[i - 1]
    if after.t >= w.at - 1 and after.t <= w.at + LOOT_WAIT + 1
       and (before.mobs[w.key] or 0) - (after.mobs[w.key] or 0) == 1 then
      a, b = after, before
      break
    end
  end
  if not a then return end
  local e = BOOK[w.area] and BOOK[w.area][w.key]
  if not e then return end
  if type(e.loot) ~= "table" then e.loot = {} end
  e.lootn = N(e.lootn) + 1
  local kinds = 0
  for _ in pairs(e.loot) do kinds = kinds + 1 end
  for key, it in pairs(a.items) do
    local was = b.items[key]
    if it.n > (was and was.n or 0) or (it.raw and was and it.raw ~= was.raw) then
      local l = e.loot[key]
      if l then l.n = N(l.n) + 1
      elseif kinds < LOOT_CAP then e.loot[key] = { n = 1, name = it.name } ; kinds = kinds + 1 end
    end
  end
  touch(w.area)
end

local function drops_tick()
  local t = now()
  local due = {}
  for _, w in ipairs(WATCH) do if t - w.at >= LOOT_WAIT then due[#due + 1] = w end end
  for _, w in ipairs(due) do drops_resolve(w) end
  for i = #WATCH, 1, -1 do if t - WATCH[i].at >= LOOT_WAIT then table.remove(WATCH, i) end end
end

-- ---------------------------------------------------------------- taken (text)
local LAST_KILL = nil     -- { key=, area=, room=, at= }: the monster a "Taken." line belongs to

local function last_kill_entry()
  local k = LAST_KILL
  if not k or now() - k.at > TAKE_SECS or k.room ~= HERE.num then return nil end
  local e = BOOK[k.area] and BOOK[k.area][k.key]
  if not e then return nil end
  return e, k.area
end

local function took_item(name, qty)
  local e, area = last_kill_entry()
  if not e then return end
  local key = mob_key(name)
  if key == "" then return end
  if type(e.take) ~= "table" then e.take = {} end
  local t = e.take[key]
  if not t then
    local kinds = 0
    for _ in pairs(e.take) do kinds = kinds + 1 end
    if kinds >= LOOT_CAP then return end
    t = { name = mob_label(name), n = 0, q = 0 }
    e.take[key] = t
  end
  t.n = N(t.n) + 1
  t.q = N(t.q) + math.max(1, math.floor(tonumber(qty) or 1))
  touch(area)
end

-- "<thing>: Taken." - coins carry their amount in brackets
local function on_taken(what)
  what = trim(what)
  local amount = what:lower():match("^coins?%s*%((%d+)%)$")
  if amount then
    local e, area = last_kill_entry()
    if not e then return end
    e.coins = N(e.coins) + tonumber(amount)
    e.coinn = N(e.coinn) + 1
    touch(area)
    return
  end
  if what ~= "" and #what <= 80 then took_item(what, 1) end
end

-- "You bind 1 Faint soul from the remains." / "You salvage 2 ... from the remains."
local function on_remains(qty, what) took_item(what, qty) end

-- ---------------------------------------------------------------- the bus
-- every entry carrying the tag, by area: what a bot needs to leave them alone
local function emit_tagged(tag)
  if not scrye.emit then return end
  local areas = {}
  for area, t in pairs(BOOK) do
    local keys = {}
    for key, e in pairs(t) do
      for _, x in ipairs(type(e.tags) == "table" and e.tags or {}) do
        if x == tag then keys[#keys + 1] = key end
      end
    end
    -- a listener knows areas, not this book's sections: every level's keys under the area
    if #keys > 0 then
      local base = base_of(area)
      local into = areas[base] or {}
      local seen = {}
      for _, k in ipairs(into) do seen[k] = true end
      for _, k in ipairs(keys) do if not seen[k] then into[#into + 1] = k end end
      table.sort(into)
      areas[base] = into
    end
  end
  scrye.emit("mdb.tagged", scrye.json.encode({ tag = tag, areas = areas }))
end

-- ---------------------------------------------------------------- the feeds
-- 'arrival' is true for GMCP's own Room.Info, which the server sends on a room CHANGE and
-- at no other time - so it is a move even when the number is the one you already stood on.
-- That matters in the Sea of Chaos, where every room is number 60494: without it the whole
-- sea would be one visit. The mapper's map.room copy of the same room is not an arrival.
local function on_room(json, arrival)
  local ok, r = pcall(scrye.json.decode, json or "")
  if not ok or type(r) ~= "table" then return end
  local num = tonumber(r.num)
  if not num then return end
  local first = (HERE.num == nil)
  local moved = (num ~= HERE.num) or arrival == true
  HERE.num = num
  HERE.name = tostring(r.name or HERE.name or "")
  HERE.area = tostring(r.area or "")
  if first then
    -- A plugin loaded mid-session is replayed Room.Contents and Room.Info in no promised
    -- order: a roster that arrived before the room belongs to this room.
    record_roster()
  elseif moved then
    ROSTER = {}
    COUNTED = {}
    SNAPS, BUILD, WATCH = {}, nil, {}   -- another room's floor: nothing to compare with
  end
  view_dirty = true
end

local function on_contents(json)
  local ok, info = pcall(scrye.json.decode, json or "")
  if not ok or type(info) ~= "table" then return end
  -- A crowded room arrives paged: page 1 (or an unpaged list) starts the roster afresh,
  -- later pages add to it.
  local page = tonumber(info.page) or 1
  if page <= 1 then
    ROSTER = {}
    BUILD = { t = now(), items = {}, mobs = {} }
  end
  if BUILD and type(info.items) == "table" then
    for _, it in ipairs(info.items) do
      local kind = type(it) == "table" and tostring(it.type or ""):lower() or ""
      if kind == "item" then snap_item(it)
      elseif kind == "monster" then
        local k = mob_key(it.name)
        if k ~= "" then BUILD.mobs[k] = (BUILD.mobs[k] or 0) + (tonumber(it.count) or 1) end
      end
    end
  end
  -- the list is whole when it is not paged, or this is its last page
  if BUILD and page >= (tonumber(info.pages) or 1) then
    SNAPS[#SNAPS + 1] = BUILD
    BUILD = nil
    if #SNAPS > SNAP_KEEP then table.remove(SNAPS, 1) end
  end
  if type(info.items) == "table" then
    local at = {}
    for i, m in ipairs(ROSTER) do at[m.key] = i end
    for _, it in ipairs(info.items) do
      if type(it) == "table" and tostring(it.type or ""):lower() == "monster" then
        local key = mob_key(it.name)
        if key ~= "" then
          local n = tonumber(it.count) or 1
          if at[key] then
            ROSTER[at[key]].count = ROSTER[at[key]].count + n
          else
            ROSTER[#ROSTER + 1] = { key = key, name = mob_label(it.name), count = n }
            at[key] = #ROSTER
          end
        end
      end
    end
  end
  record_roster()
  view_dirty = true
end

-- kills: Room.Death and the killing-blow line, paired to learn which killer is you
local RECENT = {}         -- { src = "gmcp"|"text", key=, at=, killer=, area= }

local function take_recent(src, key)
  local t = now()
  for i = #RECENT, 1, -1 do
    local r = RECENT[i]
    if t - r.at > PAIR_SECS then table.remove(RECENT, i)
    elseif r.src == src and r.key == key then table.remove(RECENT, i) ; return r end
  end
  return nil
end

local function set_me(name, how)
  name = trim(name)
  if name == "" or name == ME then return end
  ME = name
  scrye.store.set("me", ME)
  -- the fights of the character with no name were this one's
  local adopted = 0
  for area, t in pairs(BOOK) do
    for _, e in pairs(t) do
      local anon = type(e.by) == "table" and e.by[NOBODY]
      if anon then
        e.by[NOBODY] = nil
        merge_own(mine(e), anon)
        adopted = adopted + 1
        touch(area)
      end
    end
  end
  note("your character is " .. esc(ME) .. " (" .. how .. ") - kills by that name count as yours"
       .. (adopted > 0 and (", and the fights recorded before it was known are now " .. esc(ME) .. "'s") or ""))
end

local function credit_mine(area, key)
  local e = BOOK[area] and BOOK[area][key]
  if not e then return end
  local c = mine(e)
  c.kills = math.min(e.kills, N(c.kills) + 1)
  touch(area)
end

local function on_death(json)
  local ok, d = pcall(scrye.json.decode, json or "")
  if not ok or type(d) ~= "table" then return end
  -- npc arrives as 1 / 0; a player's death is not a kill
  if d.npc == false or tonumber(d.npc) == 0 then return end
  local key = mob_key(d.name)
  if key == "" then return end
  if IGN[key] then LAST_KILL = nil ; return end   -- what is taken next is the ignored one's: nobody's
  local killer = tostring(d.killer or "")
  local area = area_now()
  local e = entry(area, key, d.name)
  e.kills = e.kills + 1
  e.lastkill = now()
  e.first = e.first or now()
  local is_me = (ME ~= "" and killer == ME)
  if is_me then local c = mine(e) ; c.kills = N(c.kills) + 1 end
  touch(area)
  if HERE.num then WATCH[#WATCH + 1] = { key = key, area = area, room = HERE.num, at = now() } end
  LAST_KILL = { key = key, area = area, room = HERE.num, at = now() }
  local fought = fight_won(area, key, e)
  -- The killing-blow line names you only when it says "You": where it prints your own
  -- name instead ("Keyser dealt the killing blow to Wiremouth.") nothing can tell you from
  -- a party member - so it is asked, once for each name, with the answer one click away.
  if fought and ME == "" and killer ~= "" and not ASKED[killer] and not killer:find("[%c%s@{}]") then
    ASKED[killer] = true
    note(esc(killer) .. " killed what you were fighting. If that is you: @{accent,click=mdb me " .. killer .. "}mdb me "
         .. esc(killer) .. "@{} - then your fight numbers are kept under that name")
  end
  -- one fewer in the room, so 'here' does not show the dead until the server's refresh
  for i, m in ipairs(ROSTER) do
    if m.key == key then
      m.count = m.count - 1
      if m.count <= 0 then table.remove(ROSTER, i) end
      break
    end
  end
  if is_me then return end
  if take_recent("text", key) then
    -- "You dealt the killing blow" came first: this killer is you
    credit_mine(area, key)
    set_me(killer, "learned from a kill")
  else
    RECENT[#RECENT + 1] = { src = "gmcp", key = key, at = now(), killer = killer, area = area }
  end
end

local function on_blow(victim)
  local key = mob_key(tostring(victim or ""):gsub("%.$", ""))
  if key == "" or IGN[key] then return end
  local m = take_recent("gmcp", key)
  if m then
    -- Room.Death counted the kill under a name not yet known to be yours
    credit_mine(m.area, key)
    set_me(m.killer, "learned from a kill")
  elseif ME == "" then
    RECENT[#RECENT + 1] = { src = "text", key = key, at = now() }
  end
end

-- ---------------------------------------------------------------- the views
local function ago(t)
  t = tonumber(t)
  if not t then return "never" end
  local s = math.max(0, now() - t)
  if s < 60 then return "just now" end
  if s < 3600 then return math.floor(s / 60) .. "m ago" end
  if s < 86400 then return math.floor(s / 3600) .. "h ago" end
  return math.floor(s / 86400) .. "d ago"
end

local function pad(s, w)
  s = tostring(s)
  if #s > w then return s:sub(1, w - 1) .. "~" end
  return s .. string.rep(" ", w - #s)
end
local function lpad(s, w) s = tostring(s) ; return string.rep(" ", math.max(0, w - #s)) .. s end

local function big(n)
  n = N(n)
  if n >= 1e9 then return string.format("%.1fG", n / 1e9) end
  if n >= 1e6 then return string.format("%.1fM", n / 1e6) end
  if n >= 1e4 then return string.format("%.1fk", n / 1e3) end
  return tostring(math.floor(n + 0.5))
end

local function dur(s)
  s = math.floor(N(s) + 0.5)
  if s < 60 then return s .. "s" end
  return math.floor(s / 60) .. "m" .. string.format("%02d", s % 60) .. "s"
end

-- these read ONE CHARACTER's numbers (my(e), or another's from e.by)
local function avg_xp(c) return N(c.xpn) > 0 and N(c.xp) / c.xpn or nil end
local function avg_secs(c) return N(c.fights) > 0 and N(c.secs) / c.fights or nil end
local function low_text(c)
  local low = N(c.low)
  if low <= 0 then return nil end
  local colour = low < 25 and "error" or (low < 50 and "warning" or "dim")
  return "@{" .. colour .. "}low " .. low .. "%@{}"
end

-- "~10.6k xp, 18s, low 34%" - what a fight with it has been like, or nil when none was measured
local function fight_brief(c)
  local parts = {}
  local x, s = avg_xp(c), avg_secs(c)
  if x then parts[#parts + 1] = "~" .. big(x) .. " xp" end
  if s then parts[#parts + 1] = dur(s) end
  local l = low_text(c)
  if l then parts[#parts + 1] = l end
  if #parts == 0 then return nil end
  return table.concat(parts, ", ")
end

-- "avoid quest" -> the tags as the views show them; the ones that warn are red
local WARN = { avoid = true, danger = true, deadly = true, aggro = true }
local function tags_text(e)
  if type(e.tags) ~= "table" or #e.tags == 0 then return nil end
  local parts = {}
  for _, t in ipairs(e.tags) do parts[#parts + 1] = "@{" .. (WARN[t] and "error" or "info") .. "}" .. esc(t) .. "@{}" end
  return table.concat(parts, " ")
end
-- the tags and the note on one line, or nil when the entry has neither
local function yours_text(e)
  local t = tags_text(e)
  local n = (type(e.note) == "string" and e.note ~= "") and esc(e.note) or nil
  if not t and not n then return nil end
  return (t or "") .. ((t and n) and "  " or "") .. (n or "")
end

local function avg_coins(e) return N(e.coinn) > 0 and N(e.coins) / e.coinn or nil end

-- IN THE PANEL (0.9.0) a monster's name opens its entry right under its own row, and a
-- second click closes it - 'mdb view <section>|<key>', silent. One monster is open at a
-- time, in whichever tab it shows. In the output pane a name still runs 'mdb show <name>'
-- and prints. PANEL says which of the two is being drawn.
local PANEL = false       -- true while refresh() builds the tabs
local SEL = nil           -- { area=, key= }: the monster opened in the panel
local entry_lines         -- defined with the commands, used by the tabs

local function is_sel(area, key) return SEL ~= nil and SEL.area == area and SEL.key == key end

-- a monster's name as a link; 'shown' is the (possibly shortened) text to display
local function link(e, area, shown)
  shown = shown or e.name
  local key = mob_key(e.name)
  if PANEL and area and not area:find("|", 1, true) then
    return "@{accent" .. (is_sel(area, key) and ",bold" or "") .. ",click=mdb view " .. area .. "|" .. key .. "}" .. esc(shown) .. "@{}"
  end
  return "@{accent,click=mdb show " .. e.name:lower() .. "}" .. esc(shown) .. "@{}"
end

-- the opened monster's entry, to go under its row: the entry without its header line
local function detail(out, area, e, indent)
  if not (PANEL and is_sel(area, mob_key(e.name))) then return end
  local lines = entry_lines(area, e)
  for i = 2, #lines do out[#out + 1] = (indent or "") .. lines[i] end
end

local function kills_text(e)
  if e.kills == 0 then return "@{dim}not killed@{}" end
  local s = e.kills .. (e.kills == 1 and " kill" or " kills")
  if ME ~= "" then s = s .. " (" .. N(my(e).kills) .. " yours)" end
  return s
end

local function sorted_entries(area)
  local list = {}
  for key, e in pairs(BOOK[area] or {}) do list[#list + 1] = { key = key, e = e } end
  table.sort(list, function(a, b)
    if a.e.kills ~= b.e.kills then return a.e.kills > b.e.kills end
    if a.e.seen ~= b.e.seen then return a.e.seen > b.e.seen end
    return a.key < b.key
  end)
  return list
end

local function here_lines()
  local out = {}
  if not HERE.num then return { "@{dim}waiting for Room.Info@{}" } end
  out[#out + 1] = "@{bold}" .. esc(HERE.name ~= "" and HERE.name or ("room " .. HERE.num)) .. "@{}"
                  .. "@{dim}  " .. esc(area_now()) .. "@{}"
  if #ROSTER == 0 then out[#out + 1] = "@{dim}no monsters here@{}" ; return out end
  local area = area_now()
  for _, m in ipairs(ROSTER) do
    local head = (m.count > 1 and (m.count .. " x ") or "")
    if IGN[m.key] then
      out[#out + 1] = "@{dim}" .. esc(head .. m.name) .. "  (ignored)@{}"
    else
      local e = BOOK[area] and BOOK[area][m.key]
      if not e then
        out[#out + 1] = esc(head .. m.name)
      else
        out[#out + 1] = head .. link(e, area)
        if PANEL and is_sel(area, m.key) then
          detail(out, area, e)   -- opened: the whole entry instead of the summary
        else
          local newly = (e.seen <= 1 and e.kills == 0)
          out[#out + 1] = "   " .. (newly and "@{success}new@{}" or ("seen " .. e.seen .. "x")) .. ", " .. kills_text(e)
            .. (e.lastkill and ("@{dim}, last " .. ago(e.lastkill) .. "@{}") or "")
          local fb = fight_brief(my(e))
          if avg_coins(e) then fb = (fb and (fb .. ", ") or "") .. "~" .. big(avg_coins(e)) .. " coins" end
          if fb then out[#out + 1] = "   " .. fb end
          local yt = yours_text(e)
          if yt then out[#out + 1] = "   " .. yt end
        end
      end
    end
  end
  return out
end

local function area_lines(area)
  local list = sorted_entries(area)
  if #list == 0 then return { "@{dim}nothing recorded in " .. esc(area) .. " yet@{}" } end
  -- seen and kills, then what a kill pays and how long it takes; the group size and your
  -- own share are on the entry ('mdb <name>')
  local out = { "@{dim}" .. pad("Monster", 22) .. lpad("Seen", 5) .. lpad("Kills", 6)
                .. lpad("XP", 8) .. lpad("Time", 7) .. "@{}" }
  for _, it in ipairs(list) do
    local e = it.e
    local name = #e.name > 22 and (e.name:sub(1, 21) .. "~") or e.name
    local x, s = avg_xp(my(e)), avg_secs(my(e))
    out[#out + 1] = link(e, area, name)
      .. string.rep(" ", 22 - #name) .. lpad(e.seen, 5) .. lpad(e.kills, 6)
      .. lpad(x and big(x) or "-", 8) .. lpad(s and dur(s) or "-", 7)
      .. (tags_text(e) and ("  " .. tags_text(e)) or "")
    detail(out, area, e)
  end
  return out
end

-- every entry that carries a note or a tag, by area and name; 'tag' narrows it to one tag
local function notes_lines(tag, rows)
  local list = {}
  for area, t in pairs(BOOK) do
    for key, e in pairs(t) do
      local has = false
      if tag then
        for _, x in ipairs(type(e.tags) == "table" and e.tags or {}) do if x == tag then has = true end end
      else
        has = yours_text(e) ~= nil
      end
      if has then list[#list + 1] = { area = area, key = key, e = e } end
    end
  end
  if #list == 0 then
    return { tag and ("@{dim}nothing is tagged '" .. esc(tag) .. "'@{}")
                 or "@{dim}no notes or tags yet - 'mdb note <name> = <text>' and 'mdb tag <name> = <words>' add them@{}" }
  end
  table.sort(list, function(p, q)
    if p.area ~= q.area then return p.area < q.area end
    return p.key < q.key
  end)
  local out = {}
  for i = 1, math.min(rows or #list, #list) do
    local it = list[i]
    out[#out + 1] = link(it.e, it.area) .. "@{dim}  " .. esc(it.area) .. "@{}"
    if PANEL and is_sel(it.area, it.key) then detail(out, it.area, it.e)
    else out[#out + 1] = "   " .. yours_text(it.e) end
  end
  if rows and #list > rows then out[#out + 1] = "@{dim}... " .. (#list - rows) .. " more: 'mdb tagged' prints them all@{}" end
  return out
end

-- What pays FOR THIS CHARACTER: every monster with a measured kill, best XP per kill first. XP/min is over
-- the time spent FIGHTING it - not finding it - so it flatters a monster that is rare.
local function best_lines(area, rows)
  local list = {}
  for a, t in pairs(BOOK) do
    if not area or area[a] then
      for key, e in pairs(t) do
        if avg_xp(my(e)) then list[#list + 1] = { area = a, key = key, e = e, x = avg_xp(my(e)) } end
      end
    end
  end
  if #list == 0 then
    return { "@{dim}no kill has been measured" .. (area and " there" or "") .. (ME ~= "" and (" for " .. esc(ME)) or "")
             .. " yet - it takes Char.XP and a fight of your own@{}" }
  end
  table.sort(list, function(p, q)
    if p.x ~= q.x then return p.x > q.x end
    if p.key ~= q.key then return p.key < q.key end
    return p.area < q.area
  end)
  local out = { "@{dim}" .. pad("Monster", 22) .. lpad("XP", 8) .. lpad("Time", 7) .. lpad("XP/min", 8) .. "  Area@{}" }
  for i = 1, math.min(rows or #list, #list) do
    local it = list[i]
    local e = it.e
    local name = #e.name > 22 and (e.name:sub(1, 21) .. "~") or e.name
    local s = avg_secs(my(e))
    local rate = (s and s > 0) and big(it.x / s * 60) or "-"
    out[#out + 1] = link(e, nil, name)
      .. string.rep(" ", 22 - #name) .. lpad(big(it.x), 8) .. lpad(s and dur(s) or "-", 7) .. lpad(rate, 8)
      .. "@{dim}  " .. esc(it.area) .. "@{}"
  end
  if rows and #list > rows then out[#out + 1] = "@{dim}... " .. (#list - rows) .. " more: 'mdb best' prints them all@{}" end
  return out
end

-- The Best tab (0.6.0): one line per area - how many monsters have a measured kill there
-- and the best of them - and a click on the area's name opens it into its monsters, best
-- XP first; another click closes it. Which areas are open is kept for the session only.
local OPEN = {}           -- area -> true: shown opened on the Best tab

local function best_tab_lines()
  local areas = {}
  for a, t in pairs(BOOK) do
    local list = {}
    for key, e in pairs(t) do
      local x = avg_xp(my(e))
      if x then list[#list + 1] = { key = key, e = e, x = x } end
    end
    if #list > 0 then
      table.sort(list, function(p, q) if p.x ~= q.x then return p.x > q.x end return p.key < q.key end)
      areas[#areas + 1] = { area = a, list = list, top = list[1].x }
    end
  end
  if #areas == 0 then return best_lines(nil, nil) end   -- its "nothing measured yet" line
  -- an area's levels stay together, in their own order (floor 10, 20, 30...); the areas
  -- themselves by the best monster any of their levels holds
  local top = {}
  for _, a in ipairs(areas) do
    local b = base_of(a.area)
    if not top[b] or a.top > top[b] then top[b] = a.top end
  end
  table.sort(areas, function(p, q)
    local pb, qb = base_of(p.area), base_of(q.area)
    if pb ~= qb then
      if top[pb] ~= top[qb] then return top[pb] > top[qb] end
      return pb < qb
    end
    return natural(p.area) < natural(q.area)
  end)
  local out = {}
  for _, a in ipairs(areas) do
    local open = OPEN[a.area] == true
    out[#out + 1] = "@{accent,bold,click=mdb fold " .. a.area .. "}" .. (open and "- " or "+ ") .. esc(a.area) .. "@{}"
      .. "@{dim}  " .. #a.list .. (#a.list == 1 and " monster" or " monsters") .. ", best " .. big(a.top) .. " xp@{}"
    if open then
      out[#out + 1] = "@{dim}  " .. pad("Monster", 20) .. lpad("XP", 7) .. lpad("Time", 7) .. lpad("XP/min", 7) .. lpad("Coins", 7) .. "@{}"
      for i = 1, math.min(BEST_ROWS, #a.list) do
        local it = a.list[i]
        local e = it.e
        local name = #e.name > 20 and (e.name:sub(1, 19) .. "~") or e.name
        local s = avg_secs(my(e))
        out[#out + 1] = "  " .. link(e, a.area, name)
          .. string.rep(" ", 20 - #name) .. lpad(big(it.x), 7) .. lpad(s and dur(s) or "-", 7)
          .. lpad((s and s > 0) and big(it.x / s * 60) or "-", 7)
          .. lpad(avg_coins(e) and big(avg_coins(e)) or "-", 7)
        detail(out, a.area, e, "  ")
      end
      if #a.list > BEST_ROWS then
        out[#out + 1] = "@{dim}  ... " .. (#a.list - BEST_ROWS) .. " more: 'mdb best " .. esc(a.area) .. "' prints them all@{}"
      end
    end
  end
  return out
end

local function totals()
  local areas, mobs, kills = 0, 0, 0
  for _, t in pairs(BOOK) do
    areas = areas + 1
    for _, e in pairs(t) do mobs = mobs + 1 ; kills = kills + e.kills end
  end
  return areas, mobs, kills
end

local function refresh()
  view_dirty = false
  PANEL = true
  local areas, mobs, kills = totals()
  scrye.setState(P .. "summary", mobs .. (mobs == 1 and " monster" or " monsters") .. " in " .. areas
    .. (areas == 1 and " area, " or " areas, ") .. kills .. (kills == 1 and " kill" or " kills"))
  scrye.setState(P .. "here", table.concat(here_lines(), "\n"))
  local area = area_now()
  scrye.setState(P .. "area_head", HERE.num and area or "")
  scrye.setState(P .. "area", HERE.num and table.concat(area_lines(area), "\n") or "@{dim}waiting for Room.Info@{}")
  scrye.setState(P .. "best", table.concat(best_tab_lines(), "\n"))
  scrye.setState(P .. "notes", table.concat(notes_lines(nil, NOTES_ROWS), "\n"))
  PANEL = false
end

-- ---------------------------------------------------------------- commands
local function print_lines(lines) for _, l in ipairs(lines) do scrye.print(TAG .. l) end end

-- The book sections a name means, in natural order: the one section with exactly that
-- name, else every level of the area with exactly that name ("megacity" is all its
-- floors), else every section whose name contains the words. No name: where you stand.
local function find_zones(what)
  what = trim(what):lower()
  if what == "" then return HERE.num and { area_now() } or {} end
  local exact, of_area, partial = {}, {}, {}
  for zone in pairs(BOOK) do
    local low = zone:lower()
    if low == what then exact[#exact + 1] = zone end
    if base_of(zone):lower() == what then of_area[#of_area + 1] = zone end
    if low:find(what, 1, true) then partial[#partial + 1] = zone end
  end
  local list = (#of_area > #exact and of_area) or (#exact > 0 and exact) or partial
  table.sort(list, function(a, b) return natural(a) < natural(b) end)
  return list
end

-- everything the book holds on one monster, a line each: the header, then its numbers.
-- Printed by 'mdb <name>', and shown under the monster's row in the panel when it is clicked.
entry_lines = function(area, e)
  local out = {}
  local function note(line) out[#out + 1] = line end
  note("@{bold}" .. esc(e.name) .. "@{}@{dim}  " .. esc(area) .. "@{}")
  note("   seen " .. e.seen .. "x" .. (e.max > 1 and (", up to " .. e.max .. " together") or "")
       .. "@{dim}, first " .. ago(e.first) .. ", last " .. ago(e.last) .. "@{}")
  note("   " .. kills_text(e) .. (e.lastkill and ("@{dim}, last " .. ago(e.lastkill) .. "@{}") or ""))
  local c = my(e)
  if N(c.fights) > 0 then
    note("   " .. c.fights .. (c.fights == 1 and " fight" or " fights") .. " measured: " .. dur(avg_secs(c)) .. " and "
         .. string.format("%.0f", N(c.rounds) / c.fights) .. " rounds on average")
  end
  if avg_xp(c) then
    local s = avg_secs(c)
    note("   " .. big(avg_xp(c)) .. " xp a kill" .. "@{dim} (last " .. big(c.xplast) .. ", over " .. c.xpn
         .. (c.xpn == 1 and " kill" or " kills") .. ")@{}"
         .. ((s and s > 0) and (", " .. big(avg_xp(c) / s * 60) .. " xp/min of fighting") or ""))
  end
  if N(c.dmgn) > 0 or N(c.low) > 0 then
    local parts = {}
    if N(c.dmgn) > 0 then parts[#parts + 1] = big(N(c.dmg) / c.dmgn) .. " hp lost a kill (fought alone, " .. c.dmgn .. "x)" end
    if N(c.low) > 0 then parts[#parts + 1] = "your lowest: " .. (low_text(c):gsub("low ", "")) end
    note("   " .. table.concat(parts, ", "))
  end
  if N(c.open) > 0 then
    note("   @{warning}" .. c.open .. (c.open == 1 and " fight" or " fights") .. " left unfinished@{}")
  end
  -- your other characters, one line each: the same monster is a different fight for them
  local others = {}
  for name in pairs(type(e.by) == "table" and e.by or {}) do
    if name ~= who() then others[#others + 1] = name end
  end
  table.sort(others)
  for _, name in ipairs(others) do
    local o = e.by[name]
    local parts = {}
    if N(o.kills) > 0 then parts[#parts + 1] = o.kills .. (o.kills == 1 and " kill" or " kills") end
    if avg_secs(o) then parts[#parts + 1] = dur(avg_secs(o)) .. " a fight" end
    if avg_xp(o) then parts[#parts + 1] = big(avg_xp(o)) .. " xp" end
    if N(o.low) > 0 then parts[#parts + 1] = "low " .. o.low .. "%" end
    if N(o.open) > 0 then parts[#parts + 1] = o.open .. " unfinished" end
    if #parts > 0 then
      note("   @{dim}" .. esc(name == NOBODY and "(unnamed)" or name) .. ": " .. table.concat(parts, ", ") .. "@{}")
    end
  end
  if N(e.lootn) > 0 then
    local items = {}
    for k, l in pairs(type(e.loot) == "table" and e.loot or {}) do items[#items + 1] = { k = k, n = N(l.n), name = tostring(l.name or k) } end
    table.sort(items, function(p, q) if p.n ~= q.n then return p.n > q.n end return p.k < q.k end)
    -- only what was seen: a kill that left nothing on the floor says nothing about its corpse
    if #items > 0 then
      local parts = {}
      for i = 1, math.min(8, #items) do parts[#parts + 1] = esc(items[i].name) .. " @{dim}" .. items[i].n .. "x@{}" end
      note("   left on the floor: " .. table.concat(parts, ", ") .. (#items > 8 and ("@{dim}, +" .. (#items - 8) .. " more@{}") or "")
           .. "@{dim} (" .. e.lootn .. (e.lootn == 1 and " kill" or " kills") .. " watched)@{}")
    end
  end
  if avg_coins(e) then
    note("   coins: ~" .. big(avg_coins(e)) .. " a pickup@{dim} (" .. e.coinn .. "x, " .. big(e.coins) .. " in all)@{}")
  end
  do
    local items = {}
    for k, t in pairs(type(e.take) == "table" and e.take or {}) do
      items[#items + 1] = { k = k, n = N(t.n), q = N(t.q), name = tostring(t.name or k) }
    end
    if #items > 0 then
      table.sort(items, function(p, q) if p.n ~= q.n then return p.n > q.n end return p.k < q.k end)
      local parts = {}
      for i = 1, math.min(8, #items) do
        local it = items[i]
        parts[#parts + 1] = esc(it.name) .. " @{dim}" .. it.n .. "x" .. (it.q > it.n and (" (" .. it.q .. " in all)") or "") .. "@{}"
      end
      note("   taken after a kill: " .. table.concat(parts, ", ") .. (#items > 8 and ("@{dim}, +" .. (#items - 8) .. " more@{}") or ""))
    end
  end
  local yt = yours_text(e)
  if yt then note("   " .. yt) end
  -- prompt= puts the command in the input box, ready for the text
  note("   @{accent,prompt=mdb note " .. e.name:lower() .. " = }[note]@{} @{accent,prompt=mdb tag "
       .. e.name:lower() .. " = }[tag]@{}")
  local rooms = {}
  for k, r in pairs(e.rooms) do rooms[#rooms + 1] = { k = k, n = tonumber(r.n) or 0, name = tostring(r.name or "") } end
  table.sort(rooms, function(a, b) if a.n ~= b.n then return a.n > b.n end return a.k < b.k end)
  for i = 1, math.min(5, #rooms) do
    local r = rooms[i]
    -- the mapper's commands: click for the route, the menu walks it
    local num = r.k:sub(2)
    note("   @{dim}" .. lpad(r.n, 3) .. "x@{}  @{accent,menu=Walk there|mapg go " .. num .. ";Show the route|mapg path " .. num
         .. ",click=mapg path " .. num .. "}" .. esc(r.name ~= "" and r.name or "?") .. "@{}@{dim}  #" .. num .. "@{}")
  end
  if #rooms > 5 then note("   @{dim}... and " .. (#rooms - 5) .. " more rooms@{}") end
  return out
end

local function print_entry(area, e)
  for _, l in ipairs(entry_lines(area, e)) do note(l) end
end

local function search(what)
  what = mob_key(what)
  if what == "" then return {} end
  local hits = {}
  for area, t in pairs(BOOK) do
    for key, e in pairs(t) do
      if key:find(what, 1, true) then hits[#hits + 1] = { area = area, key = key, e = e } end
    end
  end
  table.sort(hits, function(a, b)
    local ax, bx = (a.key == what), (b.key == what)   -- an exact name before a partial one
    if ax ~= bx then return ax end
    if a.key ~= b.key then return a.key < b.key end
    return a.area < b.area
  end)
  return hits
end

local function cmd_show(what)
  local hits = search(what)
  if #hits == 0 then note("nothing in the book matches '" .. esc(trim(what)) .. "'") ; return end
  local exact = {}
  for _, h in ipairs(hits) do if h.key == mob_key(what) then exact[#exact + 1] = h end end
  if #exact > 0 then hits = exact end
  if #hits > 8 then
    note(#hits .. " entries match - the first 8 by name:")
    for i = 1, 8 do
      local h = hits[i]
      note("   " .. link(h.e) .. "@{dim}  " .. esc(h.area) .. "@{}  seen " .. h.e.seen .. "x, " .. kills_text(h.e))
    end
    return
  end
  for _, h in ipairs(hits) do print_entry(h.area, h.e) end
end

local function cmd_areas()
  local list = {}
  for area, t in pairs(BOOK) do
    local mobs, kills = 0, 0
    for _, e in pairs(t) do mobs = mobs + 1 ; kills = kills + e.kills end
    list[#list + 1] = { area = area, mobs = mobs, kills = kills }
  end
  if #list == 0 then note("the book is empty - it fills in as you walk and fight") ; return end
  table.sort(list, function(a, b) return natural(a.area) < natural(b.area) end)
  for _, a in ipairs(list) do
    note("@{accent,click=mdb area " .. a.area .. "}" .. esc(a.area) .. "@{}@{dim}  " .. a.mobs
         .. (a.mobs == 1 and " monster, " or " monsters, ") .. a.kills .. (a.kills == 1 and " kill" or " kills") .. "@{}")
  end
end

local function cmd_ignore(what, on)
  local key = mob_key(what)
  if key == "" then return end
  if on then
    if IGN[key] then note("'" .. esc(key) .. "' is already ignored") ; return end
    IGN[key] = true
    save_ignore()
    local had = false
    for _, t in pairs(BOOK) do if t[key] then had = true end end
    note("ignoring '" .. esc(key) .. "' from now on"
         .. (had and (" - what the book already holds stays ('mdb forget " .. esc(key) .. "' removes it)") or ""))
  else
    if not IGN[key] then note("'" .. esc(key) .. "' is not ignored") ; return end
    IGN[key] = nil
    save_ignore()
    note("recording '" .. esc(key) .. "' again")
  end
  view_dirty = true
end

local function cmd_forget(what, confirmed)
  local key = mob_key(what)
  local areas = {}
  for area, t in pairs(BOOK) do if t[key] then areas[#areas + 1] = area end end
  if #areas == 0 then note("no entry is named exactly '" .. esc(key) .. "'") ; return end
  table.sort(areas)
  if not confirmed then
    note("this deletes '" .. esc(key) .. "' from " .. esc(table.concat(areas, ", ")) .. " - '@{accent,click=mdb forget "
         .. key .. " confirm}mdb forget " .. esc(key) .. " confirm@{}' does it")
    return
  end
  local tags = {}
  for _, area in ipairs(areas) do
    for _, x in ipairs(type(BOOK[area][key].tags) == "table" and BOOK[area][key].tags or {}) do tags[x] = true end
    BOOK[area][key] = nil
    touch(area)
  end
  for x in pairs(tags) do emit_tagged(x) end   -- a bot that was avoiding it hears that it is gone
  COUNTED[key] = true   -- still standing next to it: not a fresh sighting until the next visit
  note("forgot '" .. esc(key) .. "' (" .. #areas .. (#areas == 1 and " area)" or " areas)"))
end

-- ---------------------------------------------------------------- notes and tags
-- The entry a note or a tag is meant for: the exact name where you stand, else the only
-- area that has it, else the only entry the words match. nil + why when it is not clear.
local function pick(what)
  local key = mob_key(what)
  if key == "" then return nil, "which monster?" end
  local areas = {}
  for area, t in pairs(BOOK) do if t[key] then areas[#areas + 1] = area end end
  table.sort(areas)
  if #areas == 1 then return BOOK[areas[1]][key], areas[1] end
  if #areas > 1 then
    local here = HERE.num and area_now() or nil
    if here and BOOK[here] and BOOK[here][key] then return BOOK[here][key], here end
    return nil, "'" .. esc(key) .. "' is in " .. esc(table.concat(areas, ", ")) .. " - stand in the one you mean"
  end
  local hits = search(what)
  if #hits == 1 then return hits[1].e, hits[1].area end
  if #hits == 0 then return nil, "nothing in the book matches '" .. esc(trim(what)) .. "'" end
  return nil, #hits .. " entries match '" .. esc(trim(what)) .. "' - give the whole name"
end

local function clean_note(text)
  text = trim(tostring(text or ""):gsub("%c", " "))
  if #text > NOTE_MAX then text = text:sub(1, NOTE_MAX) end
  return text
end

-- "Avoid, quest  good-xp" -> { "avoid", "good-xp", "quest" }
local function clean_tags(text)
  local seen, list = {}, {}
  for w in tostring(text or ""):lower():gmatch("[%w%-]+") do
    w = w:sub(1, 20)
    if not seen[w] and #list < TAG_MAX then seen[w] = true ; list[#list + 1] = w end
  end
  table.sort(list)
  return list
end

-- 'mdb note <name> = <text>' and 'mdb tag <name> = <words>'; no '=' shows what is there
local function cmd_yours(kind, arg)
  local name, value = arg:match("^(.-)%s*=%s*(.*)$")
  local e, area = pick(name or arg)
  if not e then note(area) ; return end
  if not name then
    note("@{bold}" .. esc(e.name) .. "@{}@{dim}  " .. esc(area) .. "@{}  " .. (yours_text(e) or "@{dim}no note, no tags@{}"))
    return
  end
  if kind == "note" then
    local text = clean_note(value)
    e.note = text ~= "" and text or nil
    note(text ~= "" and ("noted on " .. esc(e.name) .. " (" .. esc(area) .. "): " .. esc(text))
                     or ("note removed from " .. esc(e.name) .. " (" .. esc(area) .. ")"))
  else
    local tags = clean_tags(value)
    local changed = {}   -- every tag this entry gains or loses: listeners hear the new list for each
    for _, t in ipairs(type(e.tags) == "table" and e.tags or {}) do changed[t] = true end
    for _, t in ipairs(tags) do changed[t] = not changed[t] or nil end
    e.tags = #tags > 0 and tags or nil
    for t in pairs(changed) do emit_tagged(t) end
    note(#tags > 0 and (esc(e.name) .. " (" .. esc(area) .. ") is tagged " .. tags_text(e))
                    or ("tags removed from " .. esc(e.name) .. " (" .. esc(area) .. ")"))
  end
  touch(area)
end

local function cmd_tagged(arg)
  local tag = clean_tags(arg)[1]
  if tag then print_lines(notes_lines(tag, nil)) ; return end
  local count, list = {}, {}
  for _, t in pairs(BOOK) do
    for _, e in pairs(t) do
      for _, x in ipairs(type(e.tags) == "table" and e.tags or {}) do
        if not count[x] then list[#list + 1] = x end
        count[x] = (count[x] or 0) + 1
      end
    end
  end
  if #list == 0 then note("no tags yet - 'mdb tag <name> = <words>' adds them") ; return end
  table.sort(list)
  for _, x in ipairs(list) do
    note("@{accent,click=mdb tagged " .. x .. "}" .. esc(x) .. "@{}@{dim}  " .. count[x] .. "@{}")
  end
end

-- 'mdb drops <item>': who has given it - taken after a kill, or left on the floor
local function cmd_drops(what)
  what = mob_key(what)
  if what == "" then note("mdb drops <item> - part of the name is enough") ; return end
  local hits = {}
  for area, t in pairs(BOOK) do
    for _, e in pairs(t) do
      for k, l in pairs(type(e.loot) == "table" and e.loot or {}) do
        if k:find(what, 1, true) then
          hits[#hits + 1] = { area = area, e = e, item = tostring(l.name or k), n = N(l.n), how = "floor" }
        end
      end
      for k, l in pairs(type(e.take) == "table" and e.take or {}) do
        if k:find(what, 1, true) then
          hits[#hits + 1] = { area = area, e = e, item = tostring(l.name or k), n = N(l.n), how = "taken" }
        end
      end
    end
  end
  if #hits == 0 then
    note("nothing in the book has given '" .. esc(what) .. "' - neither taken after a kill nor left on the floor") ; return
  end
  table.sort(hits, function(p, q)
    if p.n ~= q.n then return p.n > q.n end
    if p.e.name ~= q.e.name then return p.e.name < q.e.name end
    return p.area < q.area
  end)
  for i = 1, math.min(15, #hits) do
    local h = hits[i]
    note(esc(h.item) .. " @{dim}" .. h.n .. "x " .. h.how .. "@{}  " .. link(h.e) .. "@{dim}  " .. esc(h.area) .. "@{}")
  end
  if #hits > 15 then note("@{dim}... " .. (#hits - 15) .. " more - give more of the name@{}") end
end

-- ---------------------------------------------------------------- export and import
local function stamp()
  local ok, s = pcall(os.date, "%Y%m%d-%H%M")
  return (ok and type(s) == "string") and s or tostring(now())
end

local function cmd_export()
  if not scrye.exports then note(NO_EXPORTS) ; return end
  local _, mobs = totals()
  if mobs == 0 then note("nothing to export - the book is empty") ; return end
  local name = "3s-compendium-" .. stamp() .. ".json"
  local path, err = scrye.exports.write(name, scrye.json.encode({
    format = EXPORT_FORMAT, ver = EXPORT_VER, exported = now(), areas = BOOK }))
  if not path then note("could not write " .. esc(name) .. ": " .. esc(tostring(err))) ; return end
  note(mobs .. (mobs == 1 and " monster" or " monsters") .. " exported: " .. esc(path))
  note("  'mdb import " .. esc(name) .. "' merges it into a book - yours on another machine, or a friend's")
end

-- What a file may contribute. Names end up inside click= links, so a name that could
-- close the link or open another ('@', braces, control characters) is not taken at all -
-- and a clicked name runs 'mdb show <name>', so a monster called "forget troll confirm"
-- is looked up, not obeyed. Numbers are whole and not negative.
local function plain(s, max)
  return type(s) == "string" and s ~= "" and #s <= max and not s:find("[%c@{}]")
end
local function count_of(v)
  v = tonumber(v)
  if not v or v ~= v or v < 0 or v > 1e15 then return nil end
  return math.floor(v)
end
local function bigger(a, b) a, b = count_of(a), count_of(b) ; if a and b then return math.max(a, b) end return a or b end
local function smaller(a, b) a, b = count_of(a), count_of(b) ; if a and b then return math.min(a, b) end return a or b end

local function clean_bucket(b)
  if type(b) ~= "table" then return nil end
  local c = {}
  for _, k in ipairs(OWN) do c[k] = count_of(b[k]) end
  c.xplast = count_of(b.xplast)
  local low = count_of(b.low)
  if low and low >= 1 and low <= 100 then c.low = low end
  if next(c) == nil then return nil end
  return c
end

-- one entry from a file into the book; returns "new", "changed" or "same"
local function merge_entry(area, key, x)
  local fresh = not (BOOK[area] and BOOK[area][key])
  -- the name shown is the file's only when it is plain text that spells this very key
  local e = entry(area, key, (plain(x.name, 60) and mob_key(x.name) == key) and x.name or key)
  local before = scrye.json.encode(e)
  e.seen = bigger(e.seen, x.seen) or 0
  e.kills = bigger(e.kills, x.kills) or 0
  e.max = bigger(e.max, x.max) or 0
  e.first = smaller(e.first, x.first)
  e.last = bigger(e.last, x.last)
  e.lastkill = bigger(e.lastkill, x.lastkill)
  if type(x.rooms) == "table" then
    for rk, r in pairs(x.rooms) do
      if type(rk) == "string" and rk:match("^r%d+$") and #rk <= 12 and type(r) == "table" and count_of(r.n) then
        local mine_r = e.rooms[rk]
        if mine_r then mine_r.n = bigger(mine_r.n, r.n)
        else e.rooms[rk] = { n = count_of(r.n), name = (type(r.name) == "string" and not r.name:find("%c")) and r.name:sub(1, 80) or "" } end
      end
    end
    -- over the cap: the least visited make way, as they do when you walk
    local list = {}
    for rk, r in pairs(e.rooms) do list[#list + 1] = { k = rk, n = tonumber(r.n) or 0 } end
    if #list > ROOM_CAP then
      table.sort(list, function(p, q) if p.n ~= q.n then return p.n > q.n end return p.k < q.k end)
      for i = ROOM_CAP + 1, #list do e.rooms[list[i].k] = nil end
    end
  end
  -- a character's fight numbers come across whole: whichever side has fought it more
  if type(x.by) == "table" then
    for who_, b in pairs(x.by) do
      local c = clean_bucket(b)
      if c and who_ ~= NOBODY and plain(who_, 30) and not tostring(who_):find("%s") then
        local have = e.by[who_]
        if not have or N(c.fights) > N(have.fights)
           or (N(c.fights) == N(have.fights) and N(c.kills) > N(have.kills)) then
          e.by[who_] = c
        end
      end
    end
  end
  -- drops: the larger count of each, like everything else the monster owns
  if type(x.loot) == "table" and count_of(x.lootn) then
    if type(e.loot) ~= "table" then e.loot = {} end
    e.lootn = bigger(e.lootn, x.lootn)
    local kinds = 0
    for _ in pairs(e.loot) do kinds = kinds + 1 end
    for k, l in pairs(x.loot) do
      if plain(k, 80) and mob_key(k) == k and type(l) == "table" and count_of(l.n) then
        local have = e.loot[k]
        if have then have.n = bigger(have.n, l.n)
        elseif kinds < LOOT_CAP then
          e.loot[k] = { n = count_of(l.n), name = (plain(l.name, 80) and mob_key(l.name) == k) and mob_label(l.name) or mob_label(k) }
          kinds = kinds + 1
        end
      end
    end
  end
  -- coins and what was taken: the larger of each
  if count_of(x.coinn) and count_of(x.coins) and N(x.coinn) > N(e.coinn) then
    e.coins, e.coinn = count_of(x.coins), count_of(x.coinn)   -- the pair together, so the average stays a real one
  end
  if type(x.take) == "table" then
    if type(e.take) ~= "table" then e.take = {} end
    local kinds = 0
    for _ in pairs(e.take) do kinds = kinds + 1 end
    for k, l in pairs(x.take) do
      if plain(k, 80) and mob_key(k) == k and type(l) == "table" and count_of(l.n) then
        local have = e.take[k]
        if have then
          have.n = bigger(have.n, l.n) ; have.q = bigger(have.q, l.q)
        elseif kinds < LOOT_CAP then
          e.take[k] = { n = count_of(l.n), q = count_of(l.q) or count_of(l.n),
                        name = (plain(l.name, 80) and mob_key(l.name) == k) and mob_label(l.name) or mob_label(k) }
          kinds = kinds + 1
        end
      end
    end
    if next(e.take) == nil then e.take = nil end
  end
  -- yours wins: a file's note fills an empty one, its tags join yours
  if (e.note == nil or e.note == "") and type(x.note) == "string" then
    local text = clean_note(x.note)
    if text ~= "" then e.note = text end
  end
  if type(x.tags) == "table" then
    local all = {}
    for _, t in ipairs(type(e.tags) == "table" and e.tags or {}) do all[#all + 1] = t end
    for _, t in ipairs(x.tags) do if type(t) == "string" then all[#all + 1] = t end end
    local tags = clean_tags(table.concat(all, " "))
    e.tags = #tags > 0 and tags or nil
  end
  if fresh then return "new" end
  return scrye.json.encode(e) == before and "same" or "changed"
end

local function cmd_import(name)
  if not scrye.exports then note(NO_EXPORTS) ; return end
  name = trim(name)
  if name == "" then
    local files = {}
    for _, f in ipairs(scrye.exports.list()) do
      if f:lower():match("^3s%-compendium.*%.json$") then files[#files + 1] = f end
    end
    if #files == 0 then
      note("no 3s-compendium-*.json in the exports folder - 'mdb export' writes one")
    else
      note("books in the exports folder (newest first) - 'mdb import <file>' merges one into yours:")
      for i = 1, math.min(#files, 15) do
        note("  @{accent,prompt=mdb import " .. files[i] .. "}" .. esc(files[i]) .. "@{}")
      end
    end
    return
  end
  local text, err = scrye.exports.read(name)
  if not text then note("could not read " .. esc(name) .. ": " .. esc(tostring(err))) ; return end
  local ok, data = pcall(scrye.json.decode, text)
  if not ok or type(data) ~= "table" or data.format ~= EXPORT_FORMAT or type(data.areas) ~= "table" then
    note(esc(name) .. " is not a book exported by 'mdb export'") ; return
  end
  if (tonumber(data.ver) or 0) > EXPORT_VER then
    note(esc(name) .. " was exported by a newer compendium (format " .. esc(tostring(data.ver)) .. ") - update the plugin first") ; return
  end
  local new, changed, same, skipped = 0, 0, 0, 0
  for area, t in pairs(data.areas) do
    if plain(area, 60) and area == trim(area) and type(t) == "table" then
      for key, x in pairs(t) do
        if type(x) == "table" and plain(key, 60) and mob_key(key) == key and not IGN[key] then
          local what = merge_entry(area, key, x)
          if what == "new" then new = new + 1 elseif what == "changed" then changed = changed + 1 else same = same + 1 end
          if what ~= "same" then touch(area) end
        else
          skipped = skipped + 1
        end
      end
    else
      skipped = skipped + 1
    end
  end
  if new + changed > 0 then   -- an import can bring tags: say where every tag in use now stands
    local tags = {}
    for _, t in pairs(BOOK) do
      for _, e in pairs(t) do for _, x in ipairs(type(e.tags) == "table" and e.tags or {}) do tags[x] = true end end
    end
    for x in pairs(tags) do emit_tagged(x) end
  end
  note(esc(name) .. ": " .. new .. " new, " .. changed .. " updated, " .. same .. " already the same"
       .. (skipped > 0 and (", " .. skipped .. " skipped (ignored by you, or not a name this plugin writes)") or ""))
end

-- ---------------------------------------------------------------- levels
local function set_level(area, label)
  if LEVELS[area] == label then return false end
  LEVELS[area] = label
  scrye.store.set("levels", scrye.json.encode(LEVELS))
  view_dirty = true
  return true
end

-- 'mdb level' / 'mdb level <text>' / 'mdb level -'
local function cmd_level(arg)
  if not (HERE.num and HERE.area ~= "") then note("no room yet (waiting for Room.Info)") ; return end
  local area = HERE.area
  if arg == "" then
    note("you are in @{bold}" .. esc(area_now()) .. "@{}" .. (LEVELS[area] and "@{dim} - level set for this area, 'mdb level -' clears it@{}"
         or "@{dim} - no level set ('mdb level <text>' sets one; a room named \"... floor <n>\" needs none)@{}"))
    local n = 0
    for a, l in pairs(LEVELS) do if a ~= area then n = n + 1 ; note("@{dim}   " .. esc(a) .. ": " .. esc(l) .. "@{}") end end
    return
  end
  if arg == "-" then
    if set_level(area, nil) then note("level cleared for " .. esc(area) .. " - recording under " .. esc(area_now()))
    else note("no level is set for " .. esc(area)) end
    return
  end
  local label = trim(arg:gsub("[^%w%-%. ]", "")):sub(1, 20)
  if label == "" then note("a level is letters, digits, '-', '.' and spaces") ; return end
  set_level(area, label)
  note("recording " .. esc(area) .. " under @{bold}" .. esc(area_now()) .. "@{} from now on")
end

-- the sea you open is the sea you are in: 'setsea 62' makes the Sea of Chaos "sea 62"
local function on_command(cmd)
  local n = tostring(cmd or ""):lower():match("^%s*setsea%s+(%d+)%s*$")
  if n then set_level(SEA, "sea " .. tonumber(n)) end
end

local HELP = {
  "mdb                 the areas in the book",
  "mdb here            the monsters in this room",
  "mdb area [name]     an area's monsters (this one when no name is given)",
  "mdb <name>          every entry whose name contains <name>",
  "mdb level [text|-]  the level of the area you stand in: show, set, or clear",
  "mdb best [area]     what pays: measured kills, best XP first",
  "mdb drops <item>    which monsters have given it (taken after a kill, or left on the floor)",
  "mdb note <name> = <text>   your note on it (nothing after '=' clears it)",
  "mdb tag <name> = <words>   your tags on it; 'avoid' and 'danger' show red",
  "mdb tagged [tag]    everything with that tag (no tag: the tags in use)",
  "mdb export          write the book to a file in the exports folder",
  "mdb import [file]   merge a file into the book (no file: what is there)",
  "mdb ignore [name]   never record it (no name: what is ignored)",
  "mdb unignore <name> record it again",
  "mdb forget <name>   delete its entries",
  "mdb me [name]       show or set which killer is you",
}

local function dispatch(rest)
  rest = trim(rest)
  local word, arg = rest:match("^(%S+)%s*(.*)$")
  word = (word or ""):lower()
  arg = trim(arg)
  if rest == "" then cmd_areas()
  elseif word == "help" and arg == "" then print_lines(HELP)
  elseif word == "here" and arg == "" then print_lines(here_lines())
  elseif word == "area" then
    local zones = find_zones(arg)
    if #zones == 0 then note(arg == "" and "no room yet (waiting for Room.Info)" or ("no area in the book matches '" .. esc(arg) .. "'")) ; return end
    for _, zone in ipairs(zones) do
      note("@{bold}" .. esc(zone) .. "@{}")
      print_lines(area_lines(zone))
    end
  elseif word == "fold" and arg ~= "" then
    -- what a click on an area's name on the Best tab runs: open it, or close it. Silent.
    if BOOK[arg] then OPEN[arg] = (not OPEN[arg]) or nil ; view_dirty = true end
  elseif word == "best" then
    local only = nil
    if arg ~= "" then
      local zones = find_zones(arg)
      if #zones == 0 then note("no area in the book matches '" .. esc(arg) .. "'") ; return end
      only = {}
      for _, z in ipairs(zones) do only[z] = true end
    end
    print_lines(best_lines(only, nil))
  elseif word == "level" then cmd_level(arg)
  elseif (word == "note" or word == "tag") and arg ~= "" then cmd_yours(word, arg)
  elseif word == "tagged" then cmd_tagged(arg)
  elseif word == "drops" then cmd_drops(arg)
  elseif word == "export" and arg == "" then cmd_export()
  elseif word == "import" then cmd_import(arg)
  elseif word == "ignore" and arg == "" then
    local list = {}
    for k in pairs(IGN) do list[#list + 1] = k end
    table.sort(list)
    note(#list == 0 and "nothing is ignored" or ("ignored: " .. esc(table.concat(list, ", "))))
  elseif word == "ignore" then cmd_ignore(arg, true)
  elseif word == "unignore" and arg ~= "" then cmd_ignore(arg, false)
  elseif word == "forget" and arg ~= "" then
    local name = arg:match("^(.-)%s+confirm$")
    cmd_forget(name or arg, name ~= nil)
  elseif word == "me" then
    if arg == "" then note(ME ~= "" and ("your character is " .. esc(ME)) or "your character's name is not known yet - it is learned from your first kill, or 'mdb me <name>' sets it")
    else set_me(arg, "set by you") ; view_dirty = true end
  elseif word == "view" and arg ~= "" then
    -- what a clicked name in the panel runs: open that monster's entry under its row, or
    -- close it if it is the one open. Prints nothing.
    local area, key = arg:match("^(.-)|(.+)$")
    if area and BOOK[area] and BOOK[area][key] then
      SEL = (not is_sel(area, key)) and { area = area, key = key } or nil
      view_dirty = true
    end
  elseif word == "show" and arg ~= "" then cmd_show(arg)   -- what a clicked name runs: never read as a subcommand
  else cmd_show(rest) end
  if view_dirty then refresh() end
end

scrye.addAlias{ pattern = "^mdb$", regex = true, run = function() dispatch("") end }
scrye.addAlias{ pattern = "^mdb (.+)$", regex = true, run = function(rest) dispatch(rest) end }

-- ---------------------------------------------------------------- wiring
scrye.onGmcp("Room.Info", function(json) on_room(json, true) end)
scrye.on("map.room", function(data) on_room(data, false) end)
scrye.on("mdb.query.tagged", function(data)
  local ok, q = pcall(scrye.json.decode, data or "")
  local tag = ok and type(q) == "table" and clean_tags(q.tag)[1] or nil
  if tag then emit_tagged(tag) end
end)   -- the mapper's copy; the same room twice is harmless
scrye.onGmcp("Room.Contents", on_contents)
scrye.onGmcp("Room.Death", on_death)
scrye.onGmcp("Char.Combat", on_combat)
scrye.onGmcp("Char.Vitals", on_vitals)
scrye.onGmcp("Char.XP", on_xp)
-- Your own name, from the one package that states it (0.7.1, Keyser's capture of 21 Nov
-- 2026): a chat line you sent arrives as Comm.Channel.Text with "outgoing": 1 and you as
-- its talker. One tell and the plugin knows who is playing - and it is believed over a
-- name that was guessed, inherited or typed, since nobody else can send your messages.
scrye.onGmcp("Comm.Channel.Text", function(json)
  local ok, d = pcall(scrye.json.decode, json or "")
  if not ok or type(d) ~= "table" or tonumber(d.outgoing) ~= 1 then return end
  local talker = trim(d.talker)
  if talker == "" or talker == ME or #talker > 30 or talker:find("[%c%s@{}]") then return end
  set_me(talker, "you sent a message as " .. talker)
  view_dirty = true
end)
scrye.addTrigger{ pattern = [[^You dealt the killing blow to (.+)\.]], regex = true, run = on_blow }
scrye.addTrigger{ pattern = [[^(.+): Taken\.\s*$]], regex = true, run = on_taken }
scrye.addTrigger{ pattern = [[^You (?:bind|salvage) (\d+) (.+) from the remains\.]], regex = true, run = on_remains }

if scrye.onCommand then scrye.onCommand(on_command) end
if scrye.onDisconnect then scrye.onDisconnect(function() if next(dirty) ~= nil then save_now() end end) end
scrye.every(1, function()
  fight_tick()
  drops_tick()
  if view_dirty then refresh() end
end)

scrye.addPanel{
  title = "Compendium",
  width = 440,
  accent = "#7FA36B",          -- signature: field-guide green
  tabs = {
    { title = "Here", widgets = {
        { type = "value", text = "", bind = P .. "summary", color = "dim" },
        { type = "text", bind = P .. "here" },
    } },
    { title = "Area", widgets = {
        { type = "label", bind = P .. "area_head", color = "dim" },
        { type = "text", bind = P .. "area" },
    } },
    { title = "Best", widgets = {
        { type = "label", text = "XP a kill, by area - click an area to open it", color = "dim" },
        { type = "text", bind = P .. "best" },
    } },
    { title = "Notes", widgets = { { type = "text", bind = P .. "notes" } } },
  },
}

load()
refresh()
