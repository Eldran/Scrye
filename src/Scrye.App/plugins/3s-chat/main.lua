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
--
-- 1.1.0 (3 Sep 2026): the pane is YOURS to shape. Every channel that has ever spoken is
-- listed on the Chat panel; a channel can be hidden from the pane ('chat hide <chan>' -
-- it is still logged, and a notify subscription on it still fires) and given its own
-- colour ('chat color <chan> <name|#hex>', or on the panel: click the channel, click a
-- colour). Both persist. The built-in map below stays as the default a channel starts with.

local PANE = "Chats"

-- ---------------- colour ----------------------------------------------------
-- Per-channel colours, restoring the original's CHANCOL map in an 80s palette.
-- These are literals rather than theme tokens on purpose: a channel's colour is
-- identity, not semantics -- "Party is green" should not change with the app theme.
--
-- The hues are OKLCH-stepped around the wheel and validated on the output surface
-- (#080A0C): every colour reads at >= 4.5:1 text contrast, and neighbouring hues are
-- separated by lightness as well as hue (worst adjacent pair ΔE 9.5 under simulated
-- colour-blindness, 16.3 with normal vision). The [channel] prefix on every line is
-- the identity fallback, so colour is never the only signal.
local CHANCOL = {
  tell    = "#4BE4FF",   -- electric cyan  (the one you must not miss)
  main    = "#FD2083",   -- hot magenta
  party   = "#93F64E",   -- neon lime
  newbie  = "#2EB88F",   -- deep aqua
  shout   = "#FFFFFF",   -- white
  admin   = "#CA90FB",   -- violet
  events  = "#DEB218",   -- gold
  viking  = "#DF6E1B",   -- deep orange
  whine   = "#7263FD",   -- indigo
  gamers  = "#F2A3C1",   -- soft pink (clearly apart from main's hot magenta)
  lottery = "#A0A7BB",   -- grey
  poll    = "#A0A7BB",   -- grey
}

-- The colours a channel may be given by name: the validated identity set from
-- docs/Plugin-Color-System.md (the CHANCOL hues plus the plugin accents), and the theme
-- tokens, which follow the app scheme. "#RRGGBB" is accepted as well, unvalidated - the
-- eye that picked it is the validator.
local NAMED = {
  cyan = "#4BE4FF", magenta = "#FD2083", lime = "#93F64E", aqua = "#2EB88F", white = "#FFFFFF",
  violet = "#CA90FB", gold = "#DEB218", orange = "#DF6E1B", indigo = "#7263FD", pink = "#F2A3C1",
  grey = "#A0A7BB", gray = "#A0A7BB", red = "#E7574E", blue = "#6288E1", green = "#5AAC47",
  teal = "#0B9DB3", rose = "#D855B8",
}
local NAMED_ORDER = { "cyan", "magenta", "lime", "aqua", "white", "violet", "gold", "orange",
  "indigo", "pink", "grey", "red", "blue", "green", "teal", "rose" }
local TOKENS = { dim = true, accent = true, info = true, success = true, warning = true, error = true, text = true }

-- "cyan" -> "#4BE4FF", "#abc123" -> "#ABC123", "dim" -> "dim", anything else -> nil
local function parse_color(v)
  v = tostring(v or ""):lower():gsub("^%s+", ""):gsub("%s+$", "")
  if NAMED[v] then return NAMED[v] end
  if TOKENS[v] then return v end
  local hex = v:match("^#(%x%x%x%x%x%x)$")
  if hex then return "#" .. hex:upper() end
  return nil
end

-- Per-channel settings, keyed by the channel's lowercased name: { show = bool, color =
-- string|nil, seen = n }. A channel is listed from the first line it ever sends; the
-- list and the settings both persist (scrye.store "channels", JSON).
local chans = {}
local chan_order = {}   -- names in first-seen order, for a stable panel

local function chan_key(chan) return (tostring(chan or ""):lower()) end

local function chan_of(chan, create)
  local k = chan_key(chan)
  local c = chans[k]
  if not c and create then
    c = { name = tostring(chan), show = true, color = nil, seen = 0 }
    chans[k] = c
    chan_order[#chan_order + 1] = k
  end
  return c
end

local function save_chans()
  local out = {}
  for _, k in ipairs(chan_order) do
    local c = chans[k]
    out[#out + 1] = { name = c.name, show = c.show, color = c.color, seen = c.seen }
  end
  local ok, json = pcall(scrye.json.encode, out)
  if ok and json then scrye.store.set("channels", json) end
end

do
  local raw = scrye.store.get("channels")
  local ok, list = pcall(scrye.json.decode, raw or "")
  if ok and type(list) == "table" then
    for _, e in ipairs(list) do
      if type(e) == "table" and type(e.name) == "string" and e.name ~= "" then
        local c = chan_of(e.name, true)
        c.show = (e.show ~= false)
        c.color = parse_color(e.color)          -- a bad value in the store is no colour
        c.seen = tonumber(e.seen) or 0
      end
    end
  end
end

-- the original's fallback: anything Viking-ish takes the Viking colour, else grey - unless
-- the channel has been given a colour of its own, which wins
local function chancol(chan)
  local c = (chan or ""):lower()
  local own = chans[c] and chans[c].color
  if own then return own end
  local hit = CHANCOL[c]
  if hit then return hit end
  if c:find("viking", 1, true) then return CHANCOL.viking end
  return "dim"           -- a theme token: unknown channels follow the scheme
end

local function default_col(chan)
  local c = (chan or ""):lower()
  return CHANCOL[c] or (c:find("viking", 1, true) and CHANCOL.viking) or "dim"
end

-- Escape text that came from the MUD before embedding it in markup. Without this a
-- player saying "mail me @{...}" would inject styling into your chat pane.
local function esc(s) return (tostring(s or ""):gsub("@", "@@")) end

local watches = {}    -- set: lowered-as-typed name -> true (stored as typed)
local notify_chans = {}   -- set: lowercased channel name -> true
local notify_tells = true -- tells notify by default; the whole point of notifications
local sound_on = true     -- the beep half of a notification, separable from the push half
                          -- ("chat sound off"): sitting at the PC you may want quiet, while
                          -- the phone should still buzz when you walk away

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
  local s = scrye.store.get("sound")
  if s == "0" then sound_on = false end
end

-- ---------------- companion notify report -----------------------------------
-- The plugin.<id>.notify convention: the Companion panel renders these rows with live
-- toggles, so "what will buzz my phone" is a UI instead of a memorised command. Format
-- per row: label \t detail \t on|off \t toggle-command. A state that is not literally
-- on/off makes the row informational (no button), which is what the channel and watch
-- lists want — they are collections, not switches.
local function publish_notify_state()
  local rows = {}
  rows[#rows + 1] = string.format("Tells\tsomeone addresses you directly\t%s\t%s",
    notify_tells and "on" or "off",
    "chat notify tells " .. (notify_tells and "off" or "on"))
  rows[#rows + 1] = string.format("Sound\tthe PC beep - the phone buzz is unaffected\t%s\t%s",
    sound_on and "on" or "off",
    "chat sound " .. (sound_on and "off" or "on"))

  -- Editable lists (Companion panel, API 1.10 convention): an "add" row carries a command
  -- TEMPLATE with {} for the typed text, and each existing entry is an "item" row whose
  -- command removes it. The commands are the same aliases you would type, so the panel
  -- never has to understand what a channel or a watched name is.
  local chans = {}
  for c in pairs(notify_chans) do chans[#chans + 1] = c end
  table.sort(chans)
  rows[#rows + 1] = string.format("Notify on channel\t%s\tadd\tchat notify {}",
    #chans > 0 and (#chans .. " channel" .. ((#chans == 1) and "" or "s")) or "none yet")
  for _, c in ipairs(chans) do
    -- NB 'chat notify <c>' only ADDS; removal is its own alias. Sending the add command
    -- again would silently do nothing, which as an ✕ button would be worse than no button.
    rows[#rows + 1] = string.format("%s\t\titem\tchat unnotify %s", c, c)
  end

  local w = {}
  for n in pairs(watches) do w[#w + 1] = n end
  table.sort(w)
  rows[#rows + 1] = string.format("Watch a name\t%s\tadd\tchat watch {}",
    #w > 0 and (#w .. " watched") or "nobody yet")
  for _, n in ipairs(w) do
    rows[#rows + 1] = string.format("%s\t\titem\tchat unwatch %s", n, n)
  end

  scrye.setState("plugin.3s-chat.notify", table.concat(rows, "\n"))
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

-- and always flush when the world goes away, so the tail is never lost (the channel
-- line counts ride along - they are not worth a disk write per line)
scrye.onDisconnect(function() if buffer_dirty then save_buffer() end ; save_chans() end)

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

local publish_panel   -- defined with the panel below; the feed redraws it on a new channel

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

  -- every channel that speaks is listed; the first line from a new one redraws the panel
  local cfg = chan_of(chan, true)
  cfg.seen = cfg.seen + 1
  if cfg.seen == 1 then save_chans() ; if publish_panel then publish_panel() end end

  -- watched lines carry a leading "*": the original tinted them blue, which a
  -- plain-text capture pane cannot do, but they still need to stand out in scrollback
  local entry = { stamp = stamp, mark = (why == "watch") and "*" or "", chan = chan, text = text }
  -- a hidden channel skips the pane and the replay buffer - the log and any notification
  -- it earned still happen, since hiding is about the pane's noise, not the message
  if cfg.show then
    scrye.capture(PANE, pane_line(entry))
    buffer[#buffer + 1] = entry
    while #buffer > BUFFER_MAX do table.remove(buffer, 1) end
    mark_buffer_dirty()
  end

  -- durable log, one file per plugin, with the full date+time the original wrote
  local full = ""
  do
    local ok_t, s = pcall(os.date, "%Y-%m-%d %H:%M:%S")
    if ok_t and type(s) == "string" then full = s end
  end
  scrye.log(string.format("[%s] [%s] %s%s", full, chan, entry.mark, text))

  if why then
    scrye.notify(string.format("[%s] %s", chan, text))
    if sound_on then scrye.sound("beep") end
  end
end)

-- ---------------- commands --------------------------------------------------

local function note_watching(t)
  scrye.print("watching: " .. (#t > 0 and table.concat(t, ", ") or "(nobody)"))
end

local function chat_watch(name, on)
  if on then watches[name] = true else watches[name] = nil end
  note_watching(save_watches())
  publish_notify_state()
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
  scrye.print("notify sound: " .. (sound_on and "on" or "off"))
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
    publish_notify_state()
  end,
}

-- The beep alone, independent of what notifies: "chat sound off" keeps the phone
-- buzzing and the chat pane marking, it just stops the PC speaker.
scrye.addAlias{
  pattern = "^chat sound$", regex = true,
  run = note_notifying,
}

scrye.addAlias{
  pattern = "^chat sound (on|off)$", regex = true,
  run = function(state)
    sound_on = (state:lower() == "on")
    scrye.store.set("sound", sound_on and "1" or "0")
    note_notifying()
    publish_notify_state()
  end,
}

scrye.addAlias{
  pattern = "^chat notify (?!tells(?:\\s|$))(.+)$", regex = true,
  run = function(chan)
    notify_chans[chan:lower()] = true
    save_notify_chans()
    note_notifying()
    publish_notify_state()
  end,
}

scrye.addAlias{
  pattern = "^chat unnotify (.+)$", regex = true,
  run = function(chan)
    notify_chans[chan:lower()] = nil
    save_notify_chans()
    note_notifying()
    publish_notify_state()
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

-- ---------------- channels: show / hide / colour ------------------------------

local function note_channels()
  if #chan_order == 0 then scrye.print("no channel has spoken yet") ; return end
  scrye.print("channels seen (chat show|hide <channel>, chat color <channel> <name|#hex|->):")
  for _, k in ipairs(chan_order) do
    local c = chans[k]
    scrye.print(string.format("  @{%s}[%s]@{}  %s  %s%s  (%d line%s)",
      chancol(c.name), esc(c.name),
      c.show and "shown" or "@{warning}hidden@{}",
      esc(chancol(c.name)), c.color and "" or " (default)",
      c.seen, c.seen == 1 and "" or "s"))
  end
end

local function chat_show(chan, on)
  local c = chan_of(chan, true)
  c.show = on
  save_chans()
  scrye.print(string.format("[%s] is now %s in the Chats pane%s", c.name,
    on and "shown" or "hidden", on and "" or " (still logged; a notify on it still fires)"))
  if publish_panel then publish_panel() end
end

local function chat_color(chan, value)
  local c = chan_of(chan, true)
  if value == "-" or value == "default" or value == "" then
    c.color = nil
    save_chans()
    scrye.print(string.format("@{%s}[%s]@{} back to its default colour (%s)", chancol(c.name), esc(c.name), chancol(c.name)))
  else
    local col = parse_color(value)
    if not col then
      scrye.print("not a colour: '" .. value .. "' - a name (" .. table.concat(NAMED_ORDER, ", ")
        .. "), a theme token (dim, accent, info, success, warning, error) or #RRGGBB")
      return
    end
    c.color = col
    save_chans()
    scrye.print(string.format("@{%s}[%s]@{} is now %s", col, esc(c.name), col))
  end
  if publish_panel then publish_panel() end
end

scrye.addAlias{ pattern = "^chat channels$", regex = true, run = note_channels }
scrye.addAlias{ pattern = "^chat show (.+)$", regex = true, run = function(chan) chat_show(chan, true) end }
scrye.addAlias{ pattern = "^chat hide (.+)$", regex = true, run = function(chan) chat_show(chan, false) end }
scrye.addAlias{ pattern = "^chat colou?r (.+?)\\s+(\\S+)$", regex = true,
  run = function(chan, value) chat_color(chan, value) end }
scrye.addAlias{ pattern = "^chat colou?rs$", regex = true, run = function()
  local parts = {}
  for _, n in ipairs(NAMED_ORDER) do parts[#parts + 1] = string.format("@{%s}%s@{}", NAMED[n], n) end
  scrye.print("colours by name: " .. table.concat(parts, " "))
  scrye.print("theme tokens: @{dim}dim@{} @{accent}accent@{} @{info}info@{} @{success}success@{} @{warning}warning@{} @{error}error@{}   or any #RRGGBB")
end }

-- ---------------- the panel ---------------------------------------------------
-- Two columns, the Trade tab's way: click a channel on the left to PICK it, then click a
-- colour on the right to paint it. Each channel line also carries its own hide/show
-- link. Every click is a plain click= (no menus): a menu listing sixteen colours ran
-- past the markup's 512-character spec cap and rendered as raw text (3 Sep, live), and
-- click= is what the companion can tap anyway.
local picked = nil    -- lowercased name of the channel the next colour applies to (session-only)

local function panel_channel_line(c)
  local k = chan_key(c.name)
  local is_picked = (picked == k)
  local toggle = c.show and ("@{dim,click=chat hide " .. c.name .. "}hide@{}")
                        or ("@{warning,click=chat show " .. c.name .. "}show@{}")
  return string.format("%s@{%s%s,click=chat pick %s}[%s]@{} %s%s",
    is_picked and "@{accent,bold}>@{}" or " ",
    chancol(c.name), is_picked and ",bold" or "", c.name, esc(c.name),
    toggle,
    notify_chans[k] and " @{accent}notify@{}" or "")
end

local function panel_colour_lines()
  local L = {}
  for _, n in ipairs(NAMED_ORDER) do
    L[#L + 1] = string.format("@{%s,click=chat paint %s}%s@{}", NAMED[n], n, n)
  end
  L[#L + 1] = ""
  for _, t in ipairs({ "dim", "accent", "info", "success", "warning", "error" }) do
    L[#L + 1] = string.format("@{%s,click=chat paint %s}%s@{}", t, t, t)
  end
  L[#L + 1] = ""
  L[#L + 1] = "@{dim,click=chat paint -}default@{}"
  return L
end

publish_panel = function()
  local L = {}
  if #chan_order == 0 then
    L[1] = "@{dim}no channel has@{}"
    L[2] = "@{dim}spoken yet@{}"
  else
    for _, k in ipairs(chan_order) do L[#L + 1] = panel_channel_line(chans[k]) end
  end
  scrye.setState("plugin.3s-chat.channels", table.concat(L, "\n"))
  scrye.setState("plugin.3s-chat.colours", table.concat(panel_colour_lines(), "\n"))
  local pc = picked and chans[picked]
  scrye.setState("plugin.3s-chat.picked",
    pc and string.format("picked: @{%s}[%s]@{} - click a colour", chancol(pc.name), esc(pc.name))
       or "click a channel, then a colour")
end

local function chat_pick(chan)
  local c = chan_of(chan, false)
  if not c then scrye.print("no channel called '" .. tostring(chan) .. "' has spoken yet") ; return end
  picked = chan_key(c.name)
  publish_panel()
end

local function chat_paint(value)
  local c = picked and chans[picked]
  if not c then scrye.print("pick a channel first (click one on the Chat panel, or 'chat pick <channel>')") ; return end
  chat_color(c.name, value)
end

scrye.addAlias{ pattern = "^chat pick (.+)$", regex = true, run = chat_pick }
scrye.addAlias{ pattern = "^chat paint (\\S+)$", regex = true, run = chat_paint }

scrye.addPanel{
  title = "Chat",
  width = 320,
  accent = "#4BE4FF",
  widgets = {
    { type = "label", bind = "plugin.3s-chat.picked", color = "dim" },
    { type = "row", widgets = {
        { type = "text", bind = "plugin.3s-chat.channels" },
        { type = "text", bind = "plugin.3s-chat.colours" },
    } },
    { type = "input", text = "chat", bind = "plugin.3s-chat.cmd",
      onSubmit = function(text)
        -- "hide gamers", "color party lime", "color party #1a2b3c": the same verbs,
        -- without typing 'chat ' - and the one way to reach a channel with a space in
        -- its name, since 'color' here takes the LAST word as the colour
        text = tostring(text or ""):gsub("^%s+", ""):gsub("%s+$", "")
        if text == "" then return end
        local verb, rest = text:match("^(%S+)%s*(.*)$")
        verb = (verb or ""):lower()
        if verb == "hide" and rest ~= "" then chat_show(rest, false)
        elseif verb == "show" and rest ~= "" then chat_show(rest, true)
        elseif (verb == "color" or verb == "colour") then
          local ch, v = rest:match("^(.-)%s+(%S+)$")
          if ch then chat_color(ch, v) else scrye.print("color <channel> <name|#hex|->") end
        else scrye.print("hide <channel> | show <channel> | color <channel> <name|#hex|->") end
        scrye.setState("plugin.3s-chat.cmd", "")
      end },
  },
}

-- ---------------- chat relayed from another open world -----------------------
-- API 1.10. A tell to a character on a different MUD is drawn inline in whichever tab
-- is in front, which means it scrolls away; putting it in the pane too is the whole
-- point of the pane. It is deliberately NOT run through the onChannel path above:
--
--   * no notify -- the world it came from already decided whether to buzz you, and
--     a second notification for one message is worse than none,
--   * no chat log -- another MUD's chat does not belong in this world's log file,
--   * no restore buffer -- replaying foreign lines into this world's pane on a later
--     session would be confusing, and they are still in their own world's history.
--
-- What it does get: the same timestamp, the same escaping, and the source world in
-- place of the channel colour block, so it reads as "from elsewhere" at a glance.
scrye.onRelay(function(world, chan, text)
  world = tostring(world or ""):gsub("[%z\1-\31]", " ")
  chan  = tostring(chan  or ""):gsub("[%z\1-\31]", " ")
  text  = tostring(text  or ""):gsub("[%z\1-\31]", " ")

  local ok, stripped = pcall(strip_banner, chan, text)
  if ok and stripped then text = stripped end

  local stamp = ""
  do
    local ok_t, sres = pcall(os.date, "%H:%M")
    if ok_t and type(sres) == "string" then stamp = sres end
  end

  -- "12:04 [Aardwolf] Bob: hi" for a tell, "12:04 [Aardwolf/Guild] ..." for a channel
  local label = is_tell(chan) and world or (world .. "/" .. chan)
  scrye.capture(PANE, string.format("%s@{dim}[%s]@{} %s",
    (stamp ~= "") and string.format("@{dim}%s @{}", esc(stamp)) or "",
    esc(label), esc(text)))
end)

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

-- seed the Companion panel's report with whatever the store restored
publish_notify_state()
publish_panel()
