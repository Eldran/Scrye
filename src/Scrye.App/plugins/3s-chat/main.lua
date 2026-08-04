-- 3S Chat — Scrye port of the MUSHclient ThreeS_Chat plugin.
--
-- Chat channels and tells (native MIP feed via scrye.onChannel) are routed into
-- the "Chats" capture pane as "[Chan] text" lines (tells arrive as channel
-- "Tell"). The duplicate in-text channel banner is stripped, exactly as the
-- original did. "chat watch <name>" names trigger scrye.notify + a beep when
-- they appear in a message; the watch list persists via scrye.store.
--
-- NOTE: dropped/simplified vs. the MUSHclient original:
--   * Miniwindow drawing (WindowCreate/WindowText/hotspots/buttons) — replaced
--     by the native "Chats" capture pane.
--   * chatwin (show/hide), chatsize (resize) — the pane is managed by Scrye.
--   * chatup / chatdown / chatend (paging) — the pane scrolls natively.
--   * Ring buffer + buffer save/restore — RESTORED: the last 100 lines are kept in
--     scrye.store and replayed into the pane at load, so history survives a restart.
--   * io log file (3s_chat.log) — RESTORED as scrye.log(), which appends to
--     %APPDATA%/Scrye/logs/plugins/3s-chat.log with a full date+time stamp.
--   * Per-line HH:MM timestamps — restored: each pane line is prefixed with the
--     arrival time (HH:MM) so tells/notifications are timestamped regardless of
--     the world's global ".ts" toggle.
--   * Channel colour map — RESTORED via inline colour markup (plugin API 1.2): the
--     [Chan] tag carries the channel's colour, watch hits get an accent "*", and the
--     timestamp is dimmed. Colours are neon rather than the original's muted set.
--     MUD text is escaped before it reaches the pane, so a message containing "@"
--     cannot inject markup.
--   * "chat clear" is kept as a no-op that just prints a note (pane content is
--     managed by Scrye).
--
-- Notifications: scrye.notify() reaches the desktop toast AND, when the mobile
-- companion is running, the phone as a Web Push. Three things fire it:
--   * tells        — always, unless turned off with "chat notify tells off"
--   * channels     — opt-in per channel via "chat notify <channel>"
--   * watch names  — as before, "chat watch <name>"
-- Both lists persist via scrye.store.

local PANE = "Chats"

-- ---------------- colour ----------------------------------------------------
-- Per-channel colours, restoring the original's CHANCOL map in an 80s palette.
-- These are literals rather than theme tokens on purpose: a channel's colour is
-- identity, not semantics -- "Party is green" should not change with the app theme.
local CHANCOL = {
  tell    = "#21E6FF",   -- electric cyan  (the one you must not miss)
  main    = "#FF2E88",   -- hot magenta
  party   = "#B6FF3C",   -- neon lime
  newbie  = "#2EE6C5",   -- aqua
  shout   = "#FFFFFF",   -- white
  admin   = "#C77DFF",   -- violet
  events  = "#FFD028",   -- gold
  viking  = "#FF8A3D",   -- neon orange
  whine   = "#7B5CFF",   -- indigo
  gamers  = "#FF5CA8",   -- rose
  lottery = "#A0A8C0",   -- grey
  poll    = "#A0A8C0",   -- grey
}

-- the original's fallback: anything Viking-ish takes the Viking colour, else grey
local function chancol(chan)
  local c = (chan or ""):lower()
  local hit = CHANCOL[c]
  if hit then return hit end
  if c:find("viking", 1, true) then return CHANCOL.viking end
  return "dim"           -- a theme token: unknown channels follow the scheme
end

-- Escape text that came from the MUD before embedding it in markup. Without this a
-- player saying "mail me @{...}" would inject styling into your chat pane.
local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end

local watches = {}    -- set: lowered-as-typed name -> true (stored as typed)
local notify_chans = {}   -- set: lowercased channel name -> true
local notify_tells = true -- tells notify by default; the whole point of notifications

-- ---------------- watch persistence (scrye.store, newline-joined) -----------

local function save_watches()
  local t = {}
  for n in pairs(watches) do t[#t + 1] = n end
  scrye.store.set("watches", table.concat(t, "\n"))
  return t
end

do
  local w = scrye.store.get("watches")
  if w then
    for n in w:gmatch("[^\n]+") do watches[n] = true end
  end
end

-- ---------------- notify channel persistence --------------------------------

local function save_notify_chans()
  local t = {}
  for c in pairs(notify_chans) do t[#t + 1] = c end
  table.sort(t)
  scrye.store.set("notify_chans", table.concat(t, "\n"))
  return t
end

do
  local c = scrye.store.get("notify_chans")
  if c then
    for n in c:gmatch("[^\n]+") do notify_chans[n] = true end
  end
  -- stored as "0"/"1" so an unset value keeps the default rather than reading as off
  local t = scrye.store.get("notify_tells")
  if t == "0" then notify_tells = false end
end

-- A tell is the case notifications exist for: someone addressed you personally.
-- The MIP feed names the channel "Tell"; be lenient about exact spelling.
local function is_tell(chan)
  local c = tostring(chan or ""):lower()
  -- whole words only: a substring test also matched channels like "Stellar" or
  -- "Intellect", which would then notify unconditionally with no way to opt out
  for w in c:gmatch("%a+") do if w == "tell" or w == "tells" then return true end end
  return false
end

-- ---------------- banner stripping (verbatim from original) -----------------
-- the mud embeds its own channel banner in the text; our [Chan] tag
-- already says the channel, so strip the duplicate
local function strip_banner(chan, text)
  -- banner style:  -~* Viking *~-  /  -^^* Viking Announce *^^-
  local rest = text:match("^%-[%~%^]+%*%s*.-%s*%*[%~%^]+%-%s*(.*)$")
  if rest and #rest > 0 then return rest end
  -- bracket style:  [PARTY] / [Viking-Trade]  (only when it names the channel)
  local br, rest2 = text:match("^%[([%w%-%s]+)%]%s*(.*)$")
  if br and #rest2 > 0 then
    local a = br:lower():gsub("%W", "")
    local c = chan:lower():gsub("%W", "")
    if a ~= "" and c ~= "" and (a == c or a:find(c, 1, true) or c:find(a, 1, true)) then
      return rest2
    end
  end
  return text
end

-- ---------------- scrollback buffer (survives restarts) ---------------------
-- The capture pane starts empty every run, so we keep the same 100-line tail the
-- MUSHclient version kept and replay it at load. Stored as "stamp\tmark\tchan\ttext".

local BUFFER_MAX = 100
local buffer = {}          -- { {stamp=, mark=, chan=, text=}, ... } oldest first
local buffer_dirty = false

local function pane_line(e)
  local stamp = (e.stamp ~= "") and string.format("@{dim}%s @{}", esc(e.stamp)) or ""
  local mark  = (e.mark ~= nil and e.mark ~= "") and string.format("@{accent,bold}%s@{}", esc(e.mark)) or ""
  return string.format("%s%s@{%s}[%s]@{} %s",
    stamp, mark, chancol(e.chan), esc(e.chan), esc(e.text))
end

local function save_buffer()
  buffer_dirty = false
  local out = {}
  for _, e in ipairs(buffer) do
    -- strip the field separator and newlines so a round trip is lossless
    local function clean(x) return (tostring(x):gsub("[\t\r\n]", " ")) end
    out[#out + 1] = table.concat({ clean(e.stamp), clean(e.mark or ""), clean(e.chan), clean(e.text) }, "\t")
  end
  scrye.store.set("buffer", table.concat(out, "\n"))
end

-- coalesced write-through: chat is bursty, so flush 5 s after the last line
local function mark_buffer_dirty()
  if buffer_dirty then return end
  buffer_dirty = true
  scrye.after(5, function() if buffer_dirty then save_buffer() end end)
end

-- and always flush when the world goes away, so the tail is never lost
scrye.onDisconnect(function() if buffer_dirty then save_buffer() end end)

do
  local b = scrye.store.get("buffer")
  if b and b ~= "" then
    for line in b:gmatch("[^\n]+") do
      local stamp, mark, chan, text = line:match("^(.-)\t(.-)\t(.-)\t(.*)$")
      if chan and chan ~= "" then
        buffer[#buffer + 1] = { stamp = stamp, mark = mark, chan = chan, text = text }
      end
    end
    if #buffer > 0 then
      scrye.capture(PANE, string.format("@{dim}---- %d line%s from the previous session ----@{}",
        #buffer, (#buffer == 1) and "" or "s"))
      for _, e in ipairs(buffer) do scrye.capture(PANE, pane_line(e)) end
      scrye.capture(PANE, "@{dim}---- end of restored history ----@{}")
    end
  end
end

-- ---------------- chat feed -------------------------------------------------

scrye.onChannel(function(chan, text)
  chan = tostring(chan or ""):gsub("[%z\1-\31]", " ")
  text = tostring(text or ""):gsub("[%z\1-\31]", " ")
  local ok, stripped = pcall(strip_banner, chan, text)
  if ok and stripped then text = stripped end

  -- Decide once whether this line is worth interrupting someone for, then notify at
  -- most once — a tell from a watched name on a notified channel is still one buzz.
  local why = nil
  if notify_tells and is_tell(chan) then
    why = "tell"
  elseif notify_chans[chan:lower()] then
    why = "channel"
  else
    for name in pairs(watches) do
      if text:lower():find(name:lower(), 1, true) then why = "watch"; break end
    end
  end

  -- prepend an HH:MM timestamp so you can see when each tell/message arrived
  -- (chat-pane specific, independent of the global ".ts" toggle)
  local stamp = ""
  do
    local ok_t, s = pcall(os.date, "%H:%M")
    if ok_t and type(s) == "string" then stamp = s end
  end

  -- watched lines carry a leading "*": the original tinted them blue, which a
  -- plain-text capture pane cannot do, but they still need to stand out in scrollback
  local entry = { stamp = stamp, mark = (why == "watch") and "*" or "", chan = chan, text = text }
  scrye.capture(PANE, pane_line(entry))

  buffer[#buffer + 1] = entry
  while #buffer > BUFFER_MAX do table.remove(buffer, 1) end
  mark_buffer_dirty()

  -- durable log, one file per plugin, with the full date+time the original wrote
  local full = ""
  do
    local ok_t, s = pcall(os.date, "%Y-%m-%d %H:%M:%S")
    if ok_t and type(s) == "string" then full = s end
  end
  scrye.log(string.format("[%s] [%s] %s%s", full, chan, entry.mark, text))

  if why then
    scrye.notify(string.format("[%s] %s", chan, text))
    scrye.sound("beep")
  end
end)

-- ---------------- commands --------------------------------------------------

local function note_watching(t)
  scrye.print("watching: " .. (#t > 0 and table.concat(t, ", ") or "(nobody)"))
end

local function chat_watch(name, on)
  if on then watches[name] = true else watches[name] = nil end
  note_watching(save_watches())
end

scrye.addAlias{
  pattern = "^chat watch (.+)$", regex = true,
  run = function(name) chat_watch(name, true) end,
}

scrye.addAlias{
  pattern = "^chat unwatch (.+)$", regex = true,
  run = function(name) chat_watch(name, false) end,
}

scrye.addAlias{
  pattern = "^chat watched$", regex = true,
  run = function()
    local t = {}
    for n in pairs(watches) do t[#t + 1] = n end
    note_watching(t)
  end,
}

-- ---------------- notify commands -------------------------------------------

local function note_notifying()
  local t = {}
  for c in pairs(notify_chans) do t[#t + 1] = c end
  table.sort(t)
  scrye.print("notify tells: " .. (notify_tells and "on" or "off"))
  scrye.print("notify channels: " .. (#t > 0 and table.concat(t, ", ") or "(none)"))
  local w = {}
  for n in pairs(watches) do w[#w + 1] = n end
  scrye.print("watch names: " .. (#w > 0 and table.concat(w, ", ") or "(nobody)"))
end

scrye.addAlias{
  pattern = "^chat notify$", regex = true,
  run = note_notifying,
}

-- bare `chat notify tells` reports the current setting rather than falling through to
-- the channel-subscribe alias below (which used to store a phantom "tells" channel)
scrye.addAlias{
  pattern = "^chat notify tells$", regex = true,
  run = note_notifying,
}

scrye.addAlias{
  pattern = "^chat notify tells (on|off)$", regex = true,
  run = function(state)
    notify_tells = (state:lower() == "on")
    scrye.store.set("notify_tells", notify_tells and "1" or "0")
    note_notifying()
  end,
}

scrye.addAlias{
  pattern = "^chat notify (?!tells(?:\\s|$))(.+)$", regex = true,
  run = function(chan)
    notify_chans[chan:lower()] = true
    save_notify_chans()
    note_notifying()
  end,
}

scrye.addAlias{
  pattern = "^chat unnotify (.+)$", regex = true,
  run = function(chan)
    notify_chans[chan:lower()] = nil
    save_notify_chans()
    note_notifying()
  end,
}

-- The pane itself is host-owned, but the saved history is ours to clear.
scrye.addAlias{
  pattern = "^chat clear$", regex = true,
  run = function()
    buffer = {}
    save_buffer()
    scrye.print("saved chat history cleared (the Chats pane itself is managed by Scrye - "
      .. "clear it from the pane).")
  end,
}

-- ---------------- commands the pane makes unnecessary ------------------------
-- These were window controls in MUSHclient. Consume them with an explanation rather
-- than letting them fall through to the MUD as unrecognised commands.
local WINDOW_CMDS = {
  { "^chatwin$",             "the Chats pane is managed by Scrye - show or hide it from the HUD." },
  { "^chatup$",              "the Chats pane scrolls natively - use the mouse wheel or Page Up." },
  { "^chatdown$",            "the Chats pane scrolls natively - use the mouse wheel or Page Down." },
  { "^chatend$",             "the Chats pane follows new lines automatically." },
  { "^chatsize(?: .*)?$",    "the Chats pane is sized by the HUD layout, not by the plugin." },
}
for _, c in ipairs(WINDOW_CMDS) do
  local msg = c[2]
  scrye.addAlias{ pattern = c[1], regex = true, run = function() scrye.print(msg) end }
end
