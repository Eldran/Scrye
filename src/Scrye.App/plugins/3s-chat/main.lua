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
--   * Ring buffer + buffer save/restore — the pane keeps its own scrollback.
--   * io log file (3s_chat.log) — Scrye's session logger already logs.
--   * Per-line HH:MM timestamps — restored: each pane line is prefixed with the
--     arrival time (HH:MM) so tells/notifications are timestamped regardless of
--     the world's global ".ts" toggle.
--   * Channel colour map — the pane is single-style; the [Chan] tag remains.
--   * "chat clear" is kept as a no-op that just prints a note (pane content is
--     managed by Scrye).

local PANE = "Chats"

local watches = {}   -- set: lowered-as-typed name -> true (stored as typed)

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

-- ---------------- chat feed -------------------------------------------------

scrye.onChannel(function(chan, text)
  chan = tostring(chan or ""):gsub("[%z\1-\31]", " ")
  text = tostring(text or ""):gsub("[%z\1-\31]", " ")
  local ok, stripped = pcall(strip_banner, chan, text)
  if ok and stripped then text = stripped end

  -- prepend an HH:MM timestamp so you can see when each tell/message arrived
  -- (chat-pane specific, independent of the global ".ts" toggle)
  local stamp = ""
  do
    local ok_t, s = pcall(os.date, "%H:%M")
    if ok_t and type(s) == "string" then stamp = s .. " " end
  end

  scrye.capture(PANE, string.format("%s[%s] %s", stamp, chan, text))

  for name in pairs(watches) do
    if text:lower():find(name:lower(), 1, true) then
      scrye.notify(string.format("[%s] %s", chan, text))
      scrye.sound("beep")
      break
    end
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

-- Kept as a no-op: the pane's scrollback is managed by Scrye itself.
scrye.addAlias{
  pattern = "^chat clear$", regex = true,
  run = function()
    scrye.print("the Chats pane is managed by Scrye; clear it from the pane itself.")
  end,
}
