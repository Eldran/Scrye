-- Harness for 3s-chat (1.1.1, 3 Sep 2026): the pane you shape. Channel lines are the
-- shape the MIP/GMCP feed hands scrye.onChannel - a channel name and the text after the
-- server's own banner.
--
-- What this file protects: a hidden channel stays out of the pane and the replay
-- buffer but is still logged and still notifies when subscribed; a colour given by
-- name, token or hex paints the [tag] and survives a reload; a bad colour is refused
-- and changes nothing; the built-in map stays the default; the panel lists every
-- channel that has spoken with click/menu markup that runs the same aliases; and the
-- 1.0 behaviours (watch, notify, banner strip, restore) still hold.
--
--   lua5.4 chat_test.lua      (from anywhere; tests/plugins/run.sh runs them all, as CI does)
--
-- Mutants killed (3 Sep): hide ignored by the pane (4), hide also silencing the log (1),
-- own colour not consulted (11), colour not persisted (2), hide not persisted (1), any
-- colour accepted (2), the hide link inverted (2), paint ignoring the pick (2).

local HERE = debug.getinfo(1, "S").source:match("^@(.*[/\\])") or "./"
local PLUGIN = HERE .. "../../src/Scrye.App/plugins/3s-chat/main.lua"

local pass, fail = 0, 0
local function check(ok, what) if ok then pass = pass + 1 else fail = fail + 1; print("FAIL: " .. what) end end

-- ---- a small JSON codec (the channel settings round-trip through scrye.json) ----
local function encode(v)
  local t = type(v)
  if t == "number" then return tostring(v) end
  if t == "boolean" then return tostring(v) end
  if t == "nil" then return "null" end
  if t == "string" then return '"' .. v:gsub('[\\"]', '\\%0'):gsub('\n', '\\n') .. '"' end
  local n = 0
  for _ in pairs(v) do n = n + 1 end
  if n == #v and n > 0 then
    local out = {}
    for _, x in ipairs(v) do out[#out + 1] = encode(x) end
    return "[" .. table.concat(out, ",") .. "]"
  end
  if n == 0 then return "[]" end
  local out = {}
  for k, val in pairs(v) do out[#out + 1] = encode(tostring(k)) .. ":" .. encode(val) end
  return "{" .. table.concat(out, ",") .. "}"
end
local decode
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
        ws() ; local k = value() ; ws() ; pos = pos + 1
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
        if ch == "\\" then out[#out + 1] = src:sub(i + 1, i + 1) ; i = i + 2
        elseif ch == '"' then pos = i + 1 ; return table.concat(out)
        else out[#out + 1] = ch ; i = i + 1 end
      end
    else
      local lit = src:match("^%-?%d+%.?%d*", pos)
      if lit then pos = pos + #lit ; return tonumber(lit) end
      lit = src:match("^%a+", pos) ; pos = pos + #(lit or "")
      if lit == "true" then return true elseif lit == "false" then return false end
      return nil
    end
  end
  decode = function(s)
    if type(s) ~= "string" then return nil end
    src, pos = s, 1
    local ok, v = pcall(value)
    return ok and v or nil
  end
end

-- ---- the client ----
local printed, captured, logged, notified, sounds, aliases, chan_hooks, relay_hooks, disc_hooks
local store, state, panel, timers, next_id
local function fresh_client(keep_store)
  printed, captured, logged, notified, sounds = {}, {}, {}, {}, {}
  aliases, chan_hooks, relay_hooks, disc_hooks = {}, {}, {}, {}
  state, panel, timers, next_id = {}, nil, {}, 0
  if not keep_store then store = {} end
  scrye = {
    id = "3s-chat",
    print = function(x) printed[#printed + 1] = tostring(x) end,
    capture = function(pane, text) captured[#captured + 1] = { pane = pane, text = tostring(text) } end,
    log = function(x) logged[#logged + 1] = tostring(x) end,
    notify = function(x) notified[#notified + 1] = tostring(x) end,
    sound = function(x) sounds[#sounds + 1] = tostring(x) end,
    json = { encode = encode, decode = decode },
    store = { get = function(k) return store[k] end, set = function(k, v) store[k] = tostring(v) end,
              delete = function(k) store[k] = nil end },
    onChannel = function(fn) chan_hooks[#chan_hooks + 1] = fn end,
    onRelay = function(fn) relay_hooks[#relay_hooks + 1] = fn end,
    onDisconnect = function(fn) disc_hooks[#disc_hooks + 1] = fn end,
    addAlias = function(a) aliases[#aliases + 1] = a end,
    addPanel = function(pn) panel = pn end,
    setState = function(k, v) state[k] = v end,
    getState = function(k) return state[k] end,
    after = function(_, fn) next_id = next_id + 1; timers[next_id] = fn; return next_id end,
    cancel = function(id) timers[id] = nil end,
  }
end
local function load_plugin() dofile(PLUGIN) end
local function chat(chan, text) for _, fn in ipairs(chan_hooks) do fn(chan, text) end end
local function alias(cmd)
  -- the plugin registers Lua-regex-looking patterns for the .NET engine; drive them by
  -- matching the few shapes it uses
  for _, a in ipairs(aliases) do
    local pat = a.pattern
    local lua = pat:gsub("%(%?!tells%(%?:\\s|%$%)%)", ""):gsub("\\S", "%%S"):gsub("\\s", "%%s")
                  :gsub("%(%.%+%?%)", "(.-)"):gsub("%(%.%+%)", "(.+)"):gsub("%(on|off%)", "(%%a+)")
    -- Lua has no '?' on a group: expand "colou?r" by hand
    lua = lua:gsub("colou%?r", "colou?r")
    local ok, a1, a2 = pcall(string.match, cmd, lua)
    if ok and a1 then
      if pat:find("%(%?!tells") and cmd:match("^chat notify tells") then a1 = nil end
      if a1 then a.run(a1, a2) ; return true end
    end
  end
  return false
end
local function fire_timers() local t = timers ; timers = {} ; for _, fn in pairs(t) do fn() end end
local function said(pat) for _, l in ipairs(printed) do if l:find(pat, 1, true) then return true end end return false end
local function pane_has(pat) for _, c in ipairs(captured) do if c.text:find(pat, 1, true) then return true end end return false end
local function panel_text() return tostring(state["plugin.3s-chat.channels"] or "") end

-- ============================================================
-- 1. the 1.0 behaviours still hold
fresh_client() ; load_plugin()
check(panel ~= nil and panel.title == "Chat", "a Chat panel is registered")
check(panel ~= nil and panel.solo == true, "the Chat panel stays out of books (solo, host API 1.22)")
check(tostring(state["plugin.3s-chat.channels"]):find("no channel has", 1, true) ~= nil, "before any chat the panel says so")
chat("Party", "-~* Party *~- Goran: hello")
check(pane_has("@{#93F64E}[Party]@{} Goran: hello"), "a Party line lands in the pane in Party's default colour, banner stripped")
check(#logged == 1 and logged[1]:find("[Party] Goran: hello", 1, true), "...and in the log")
chat("Tell", "Stabby tells you: hi")
check(#notified == 1, "a tell notifies")
check(pane_has("@{#4BE4FF}[Tell]@{}"), "...in the tell colour")

-- 2. every channel that speaks is listed on the panel in its colour, with a click that
--    PICKS it and a hide/show link; the colour column paints the picked channel
local function chan_col_text() return tostring(state["plugin.3s-chat.channels"] or "") end
local function colour_col_text() return tostring(state["plugin.3s-chat.colours"] or "") end
check(chan_col_text():find("@{#93F64E,click=chat pick Party}[Party]@{}", 1, true) ~= nil, "panel: Party wears its colour and a click picks it")
check(chan_col_text():find("@{dim,click=chat hide Party}hide@{}", 1, true) ~= nil, "panel: ...with a hide link beside it")
check(colour_col_text():find("@{#DEB218,click=chat paint gold}gold@{}", 1, true) ~= nil, "panel: the colour column offers gold in gold")
check(colour_col_text():find("@{dim,click=chat paint -}default@{}", 1, true) ~= nil, "panel: ...and default")
check(tostring(state["plugin.3s-chat.picked"]):find("click a channel, then a colour", 1, true) ~= nil, "panel: the hint before a pick")
-- every markup spec on the panel is under the 512-character cap that rendered 1.1.0's
-- menus as raw text (3 Sep, live)
do
  local longest = 0
  for spec in (chan_col_text() .. "\n" .. colour_col_text()):gmatch("@{([^}]*)}") do
    if #spec > longest then longest = #spec end
  end
  check(longest <= 200, "panel: the longest markup spec is well under the 512 cap (" .. longest .. ")")
end
clear = function() printed = {} end
clear() ; alias("chat paint gold")
check(said("pick a channel first"), "paint with nothing picked says so")
alias("chat pick Party")
check(tostring(state["plugin.3s-chat.picked"]):find("picked: @{#93F64E}[Party]@{}", 1, true) ~= nil, "pick: the hint names the picked channel")
check(chan_col_text():find("@{accent,bold}>@{}@{#93F64E,bold,click=chat pick Party}", 1, true) ~= nil, "pick: the picked channel is marked and bold")
alias("chat paint gold")
captured = {}
chat("Party", "Goran: painted")
check(pane_has("@{#DEB218}[Party]@{}"), "paint: the picked channel takes the colour")
check(chan_col_text():find("@{#DEB218,bold,click=chat pick Party}", 1, true) ~= nil, "paint: ...and the panel redraws in it")
alias("chat paint -")
-- the pick is what gets painted, not the first channel in the list
alias("chat pick Tell")
alias("chat paint lime")
captured = {}
chat("Tell", "Stabby tells you: lime?") ; chat("Party", "Goran: still default?")
check(pane_has("@{#93F64E}[Tell]@{}") and pane_has("@{#93F64E}[Party]@{}") and not pane_has("@{#4BE4FF}[Tell]@{}"),
  "paint: the PICKED channel (Tell, second in the list) is painted, Party keeps its default")
alias("chat paint -") ; alias("chat pick Party")
clear() ; alias("chat pick Nobody")
check(said("no channel called 'Nobody'"), "pick: an unknown channel is refused")
clear() ; alias("chat channels")
check(said("[Party]") and said("[Tell]") and said("shown"), "chat channels lists both, shown")

-- 3. HIDE: the channel leaves the pane and the replay buffer; the log and a subscribed
--    notify are untouched
captured, logged, notified = {}, {}, {}
alias("chat hide Party")
check(said("[Party] is now hidden"), "hide: says so")
chat("Party", "Goran: still talking")
check(#captured == 0, "hide: the line is NOT captured")
check(#logged == 1, "hide: ...but it is logged")
alias("chat notify party")
notified = {}
chat("Party", "Goran: notify me")
check(#notified == 1, "hide: a subscribed notify still fires on a hidden channel")
check(chan_col_text():find("@{warning,click=chat show Party}show@{}", 1, true) ~= nil, "hide: the panel's link now SHOWS it")
fire_timers()                                  -- the buffer flush
check(not tostring(store["buffer"] or ""):find("still talking", 1, true), "hide: hidden lines are not in the replay buffer")
alias("chat unnotify party")
alias("chat show Party")
captured = {}
chat("Party", "Goran: back")
check(#captured == 1, "show: the line is captured again")

-- 4. COLOUR: by name, by token, by hex; a bad value refused; reset to default
alias("chat color Party gold")
captured = {}
chat("Party", "Goran: golden")
check(pane_has("@{#DEB218}[Party]@{}"), "color: 'gold' paints the tag #DEB218")
check(chan_col_text():find("@{#DEB218,bold,click=chat pick Party}", 1, true) ~= nil, "color: the panel wears it (still picked, so bold)")
alias("chat colour Party #1a2b3c")
captured = {}
chat("Party", "Goran: hex")
check(pane_has("@{#1A2B3C}[Party]@{}"), "colour: a hex is accepted (and upper-cased), British spelling too")
alias("chat color Party info")
captured = {}
chat("Party", "Goran: token")
check(pane_has("@{info}[Party]@{}"), "color: a theme token is accepted")
clear() ; captured = {}
alias("chat color Party purple-ish")
check(said("not a colour"), "color: a bad value is refused with the choices")
chat("Party", "Goran: unchanged")
check(pane_has("@{info}[Party]@{}"), "color: ...and changes nothing")
alias("chat color Party -")
captured = {}
chat("Party", "Goran: default again")
check(pane_has("@{#93F64E}[Party]@{}"), "color: '-' puts the default back")

-- 4b. a channel with no built-in colour takes 'dim', and can be given one
chat("Gossip", "Bob: psst")
check(pane_has("@{dim}[Gossip]@{}"), "an unknown channel is dim by default")
alias("chat color Gossip rose")
captured = {}
chat("Gossip", "Bob: again")
check(pane_has("@{#D855B8}[Gossip]@{}"), "...and takes a colour like any other")

-- 4c. a channel with a space in its name is reachable by command: the colour is the LAST word
chat("Viking Trade", "Goran: selling")
alias("chat color Viking Trade teal")
captured = {}
chat("Viking Trade", "Goran: teal?")
check(pane_has("@{#0B9DB3}[Viking Trade]@{}"), "a two-word channel is coloured by command (last word = colour)")
alias("chat hide Viking Trade")
captured = {}
chat("Viking Trade", "Goran: hidden?")
check(#captured == 0, "...and hidden by command")
alias("chat show Viking Trade")

-- 5. it all survives a reload: hidden stays hidden, the colour stays, the list stays
alias("chat hide Tell")
alias("chat color Party cyan")
for _, fn in ipairs(disc_hooks) do fn() end     -- a disconnect flushes the counts
fresh_client(true) ; load_plugin()
check(chan_col_text():find("[Party]", 1, true) ~= nil and chan_col_text():find("[Gossip]", 1, true) ~= nil,
  "reload: the channel list is back")
captured = {}
chat("Tell", "Stabby tells you: quiet")
check(#captured == 0, "reload: Tell is still hidden")
chat("Party", "Goran: cyan?")
check(pane_has("@{#4BE4FF}[Party]@{}"), "reload: Party is still cyan")
chat("Gossip", "Bob: rose?")
check(pane_has("@{#D855B8}[Gossip]@{}"), "reload: Gossip is still rose")
alias("chat show Tell") ; alias("chat color Party -")

-- 6. the panel's input box takes the verbs without 'chat '
local box
for _, w in ipairs(panel.widgets) do if w.type == "input" then box = w end end
check(box ~= nil, "the panel has a command box")
box.onSubmit("hide Gossip")
captured = {}
chat("Gossip", "Bob: hidden via the box")
check(#captured == 0, "box: 'hide Gossip' hides it")
box.onSubmit("color Gossip lime")
box.onSubmit("color Viking Trade rose")
captured = {}
chat("Viking Trade", "Goran: box rose")
check(pane_has("@{#D855B8}[Viking Trade]@{}"), "box: a two-word channel via the box")
box.onSubmit("show Gossip")
captured = {}
chat("Gossip", "Bob: lime via the box")
check(pane_has("@{#93F64E}[Gossip]@{}"), "box: 'color Gossip lime' then 'show Gossip'")
check(state["plugin.3s-chat.cmd"] == "", "box: the field is cleared after a command")

-- 7. a corrupt store is no store: the plugin still loads with every channel shown
store["channels"] = "not json"
fresh_client(true) ; load_plugin()
captured = {}
chat("Party", "Goran: after corruption")
check(#captured == 1 and pane_has("@{#93F64E}[Party]@{}"), "a corrupt channel store loads as empty, defaults hold")

-- 8. the server's own word-wrap inside a tell - newline plus indent - is one space in
--    the pane, not a gap (15 Sep: "andra          bitarna")
captured = {}
chat("tell", "To Rocky: nods, hitta alla 3 delarna andra\n          bitarna i ladan")
check(pane_has("delarna andra bitarna i ladan"), "a wrapped tell is joined with a single space")
check(not pane_has("andra  "), "...and the server's indent is gone")

print(string.format("chat_test: %d passed, %d failed", pass, fail))
if fail > 0 then os.exit(1) end
