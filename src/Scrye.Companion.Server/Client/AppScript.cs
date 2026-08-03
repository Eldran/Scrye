namespace Scrye.Companion.Server.Client;

/// <summary>
/// The companion app's behaviour. Served as <c>/app.js</c> rather than inlined so the
/// service worker can cache it separately and so it can be parse-checked in isolation.
/// </summary>
internal static class AppScript
{
    internal const string Js = """
(() => {
'use strict';

const $ = id => document.getElementById(id);
const store = {
  // localStorage for both. The token is a credential, but this is a personal phone behind
  // a private tailnet, and forcing a re-type every launch is the bigger risk: it pushes
  // toward shorter, weaker tokens. Over the tailnet it is usually not needed at all.
  get token() { return localStorage.getItem('scrye.token') || ''; },
  set token(v) { localStorage.setItem('scrye.token', v); },
  get session() { return localStorage.getItem('scrye.session') || ''; },
  set session(v) { localStorage.setItem('scrye.session', v); },
};

// Set from /whoami: true when the tailnet proxy already vouched for us and no token is
// needed. Checked before connecting so the app never asks for a credential it will not use.
let tokenRequired = true;

let ws = null;
let sessionId = null;
let lastSeq = -1;
let state = {};                 // path -> {kind, text}
let atBottom = true;
let reconnectDelay = 1000;
let manualClose = false;

// ---- output rendering -------------------------------------------------------

const out = $('out');
const MAX_NODES = 3000;         // a phone will not scroll a 50k-line DOM; the desktop keeps history

out.addEventListener('scroll', () => {
  // Only autoscroll when the user is already at the bottom, so reading back through a
  // fight is not yanked away by every new line.
  atBottom = out.scrollHeight - out.scrollTop - out.clientHeight < 40;
});

function trim() {
  while (out.childNodes.length > MAX_NODES) out.removeChild(out.firstChild);
}

function scroll() {
  if (atBottom) out.scrollTop = out.scrollHeight;
}

function styleOf(st) {
  let css = '';
  if (st.fg) css += `color:${st.fg};`;
  if (st.bg && st.bg !== '#000000') css += `background:${st.bg};`;
  const f = (st.flags || '').toLowerCase();
  if (f.includes('bold')) css += 'font-weight:700;';
  if (f.includes('underline')) css += 'text-decoration:underline;';
  if (f.includes('italic')) css += 'font-style:italic;';
  return css;
}

function buildLine(line, table) {
  const div = document.createElement('div');
  for (const span of line.spans) {
    const st = table[span.s] || {};
    let el;
    if (span.link) {
      el = document.createElement('a');
      el.className = 'mxp';
      el.href = 'javascript:void 0';
      if (span.link.isUrl) {
        el.href = span.link.action;
        el.target = '_blank';
        el.rel = 'noopener noreferrer';
      } else {
        // MXP actions are authored by the MUD, so they are sent raw and never run
        // through the client's own command handling (design §7.4).
        el.addEventListener('click', ev => {
          ev.preventDefault();
          if (span.link.prompt) { $('cmd').value = span.link.action; $('cmd').focus(); }
          else sendRaw(span.link.action);
        });
      }
    } else {
      el = document.createElement('span');
    }
    el.textContent = span.text;
    const css = styleOf(st);
    if (css) el.style.cssText += css;
    div.appendChild(el);
  }
  return div;
}

function applyBatch(msg) {
  const table = msg.styles || [];
  const frag = document.createDocumentFragment();
  let promptLine = null;

  for (const line of msg.lines || []) {
    if (line.sequence > lastSeq) lastSeq = line.sequence;
    // Prompts are pinned below the output instead of appended, so the last one stays
    // visible rather than scrolling off — the single biggest readability win on a phone.
    if (line.prompt) { promptLine = line; continue; }
    frag.appendChild(buildLine(line, table));
  }

  out.appendChild(frag);
  trim();
  scroll();

  if (promptLine) {
    const p = $('prompt');
    p.innerHTML = '';
    p.appendChild(buildLine(promptLine, table));
    p.classList.add('show');
  }
}

function note(text, cls) {
  const d = document.createElement('div');
  d.className = cls || 'sys';
  d.textContent = text;
  out.appendChild(d);
  trim();
  scroll();
}

// ---- vitals -----------------------------------------------------------------

// Common GMCP/MIP shapes. The desktop publishes whatever the MUD sends, so the client
// looks for the usual suspects rather than demanding one spelling.
const VITALS = [
  { label:'HP', value:['char.vitals.hp','character.health','char.hp'],
                max:['char.vitals.maxhp','character.maxhealth','char.maxhp'] },
  { label:'MP', value:['char.vitals.mp','character.mana','char.mp'],
                max:['char.vitals.maxmp','character.maxmana','char.maxmp'] },
];

function num(paths) {
  for (const p of paths) {
    const v = state[p];
    if (v !== undefined) { const n = parseFloat(v.text); if (!isNaN(n)) return n; }
  }
  return null;
}

function renderVitals() {
  const host = $('vitals');
  let html = '';
  for (const v of VITALS) {
    const cur = num(v.value), max = num(v.max);
    if (cur === null) continue;
    const pct = max && max > 0 ? Math.max(0, Math.min(100, (cur / max) * 100)) : 100;
    const cls = pct < 25 ? ' bad' : pct < 50 ? ' warn' : '';
    html += `<span class="vital"><b>${v.label}</b>`
          + `<span class="meter${cls}"><i style="width:${pct}%"></i></span></span>`;
  }
  host.innerHTML = html;
}




// ---- Web Push ---------------------------------------------------------------
//
// §7.2: notifications while the app is closed, with no hosted component — the desktop
// itself is the VAPID application server. Everything here is opt-in and behind a button,
// because a page that asks for notification permission on load gets denied by reflex.

function isStandalone() {
  // Guarded: this runs at load, and an exception here would take the whole app down over
  // a cosmetic capability check.
  try {
    if (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) return true;
  } catch { /* ignore */ }
  return window.navigator.standalone === true;
}

async function pushState() {
  const box = $('notifystate');
  if (!('serviceWorker' in navigator) || !('PushManager' in window)) {
    // Two very different causes land here, and telling the user the wrong one wastes their
    // time. On iOS a *browser tab* looks exactly like this — the API only appears once the
    // app is on the home screen — so an un-installed iOS browser gets the install advice.
    // Everywhere else (Android grants push to an ordinary tab) a missing API means the
    // browser genuinely lacks it, and "add to home screen" would be a wild goose chase.
    const iOS = /iPad|iPhone|iPod/.test(navigator.userAgent) ||
                (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    box.textContent = (iOS && !isStandalone())
      ? 'Add Scrye to your home screen first — iOS only allows notifications for installed apps.'
      : 'Push is not supported by this browser.';
    $('notify').disabled = true;
    return;
  }
  if (Notification.permission === 'denied') {
    box.textContent = 'Notifications are blocked in system settings.';
    $('notify').disabled = true;
    return;
  }
  const reg = await navigator.serviceWorker.ready;
  const sub = await reg.pushManager.getSubscription();
  box.textContent = sub ? 'Notifications are on for this device.' : 'Notifications are off.';
  $('notify').textContent = sub ? 'Disable notifications' : 'Enable notifications';
  $('notify').disabled = false;
}

// applicationServerKey wants raw bytes, not the base64url string the server sends.
function b64urlToBytes(s) {
  const pad = '='.repeat((4 - (s.length % 4)) % 4);
  const raw = atob((s + pad).replace(/-/g, '+').replace(/_/g, '/'));
  return Uint8Array.from(raw, c => c.charCodeAt(0));
}

async function toggleNotifications() {
  const box = $('notifystate');
  try {
    const reg = await navigator.serviceWorker.ready;
    const existing = await reg.pushManager.getSubscription();

    if (existing) {
      send({ type:'push.unsubscribe', endpoint: existing.endpoint });
      await existing.unsubscribe();
      await pushState();
      return;
    }

    // Must be called from a user gesture — this is why it lives behind the button.
    const perm = await Notification.requestPermission();
    if (perm !== 'granted') { await pushState(); return; }

    const { key } = await (await fetch('/push-key', { cache:'no-store' })).json();
    const sub = await reg.pushManager.subscribe({
      userVisibleOnly: true,                 // required; iOS revokes silent subscriptions
      applicationServerKey: b64urlToBytes(key),
    });

    const j = sub.toJSON();
    send({ type:'push.subscribe', endpoint: sub.endpoint, p256dh: j.keys.p256dh, auth: j.keys.auth });
    await pushState();
  } catch (e) {
    box.textContent = 'Could not enable notifications: ' + (e && e.message ? e.message : e);
  }
}

// ---- chat / capture panes ---------------------------------------------------
//
// Driven entirely by the desktop's own trigger routing (`output.pane`). The filtering
// rules already exist there; duplicating them here as regexes would guarantee drift, and
// would miss gagged lines the desktop hides from main output but routes to a pane.

const panes = new Map();         // pane name -> { lines: [DocumentFragment-able nodes], unread }
let activePane = null;
const PANE_MAX = 500;

function paneState(name) {
  if (!panes.has(name)) panes.set(name, { nodes: [], unread: 0 });
  return panes.get(name);
}

function addPaneLines(name, msg) {
  const p = paneState(name);
  const table = msg.styles || [];
  const showing = activePane === name && $('chat').classList.contains('show');

  for (const line of msg.lines || []) {
    p.nodes.push(buildLine(line, table));
    if (p.nodes.length > PANE_MAX) p.nodes.shift();
    if (!showing) p.unread++;
  }

  if (activePane === null) activePane = name;
  if (activePane === name) renderPaneLines();
  renderPaneTabs();
  updateChatBadge();
}

function renderPaneTabs() {
  const host = $('chatpanes');
  host.replaceChildren();
  for (const [name, p] of panes) {
    const b = el('button', name === activePane ? 'on' : null);
    b.appendChild(document.createTextNode(name));
    if (p.unread > 0) b.appendChild(el('span', 'n', String(p.unread)));
    b.addEventListener('click', () => selectPane(name));
    host.appendChild(b);
  }
}

function renderPaneLines() {
  const host = $('chatlines');
  host.replaceChildren();
  const p = activePane && panes.get(activePane);
  if (!p || p.nodes.length === 0) {
    host.appendChild(el('div', null, '')).appendChild(
      el('span', 'w-note', activePane ? 'Nothing here yet.'
        : 'No capture panes yet — a trigger has to route lines to one.'));
    return;
  }
  // Nodes are reused rather than rebuilt: they were already styled once on arrival.
  for (const n of p.nodes) host.appendChild(n);
  host.scrollTop = host.scrollHeight;
}

function selectPane(name) {
  activePane = name;
  const p = panes.get(name);
  if (p) p.unread = 0;
  renderPaneTabs();
  renderPaneLines();
  updateChatBadge();
}

function updateChatBadge() {
  let total = 0;
  for (const [, p] of panes) total += p.unread;
  $('tab-chat').innerHTML = 'Chat' + (total ? ` <span class="badge">${total}</span>` : '');
}

function clearPanes() {
  panes.clear();
  activePane = null;
  renderPaneTabs();
  renderPaneLines();
  updateChatBadge();
}

// ---- HUD panels -------------------------------------------------------------
//
// The design's headline claim (§2): panels are *data*, so the phone renders the same
// PanelSpec the desktop does rather than a second UI built by hand. Colours and thresholds
// below mirror HudViewModel/BarListView deliberately — a gauge that reads "warning" on the
// desktop must not read "healthy" here.

const panels = new Map();        // panelId -> spec
const binders = new Map();       // state path -> [fn(value)]
const panelTab = new Map();      // panelId -> selected tab index

const GAUGE_HEALTHY = '#35c4d6', GAUGE_WARN = '#e0a830', GAUGE_CRIT = '#e05050';
const BAR_REFINED = '#46b45a', BAR_RAW = '#e0a020';

function bind(path, fn) {
  if (!path) return;
  if (!binders.has(path)) binders.set(path, []);
  binders.get(path).push(fn);
  fn(stateText(path));            // seed, exactly as BindText does
}

const stateText = p => (state[p] ? state[p].text : '');

// Mirrors BindNumber: a numeric string that is NOT a known state path is a literal
// (`max = 100`), otherwise it is a path to watch.
function bindNumber(pathOrLiteral, fn) {
  if (!pathOrLiteral) return;
  const asNum = parseFloat(pathOrLiteral);
  if (!isNaN(asNum) && !(pathOrLiteral in state)) { fn(asNum); return; }
  bind(pathOrLiteral, v => fn(parseFloat(v) || 0));
}

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function gaugeColour(ratio, colour, dim) {
  if (dim) {
    const base = colour || '#46b45a';
    const b = 0.30 + 0.70 * ratio;          // brightness 30% empty -> 100% full
    const n = parseInt(base.slice(1), 16);
    const r = Math.round(((n >> 16) & 255) * b);
    const g = Math.round(((n >> 8) & 255) * b);
    const bl = Math.round((n & 255) * b);
    return `rgb(${r},${g},${bl})`;
  }
  if (colour) return colour;
  return ratio >= 0.5 ? GAUGE_HEALTHY : ratio >= 0.25 ? GAUGE_WARN : GAUGE_CRIT;
}

function buildBarList(rows) {
  const host = el('div', 'barlist');
  for (const raw of (rows || '').split('\n')) {
    if (!raw.trim()) continue;
    const parts = raw.split('\t');
    // label \t caption \t value \t max \t refined — anything else is plain text, so
    // headers and "none" rows still show, matching BarListView.
    if (parts.length < 4) { host.appendChild(el('div', 'barrow', raw)); continue; }
    const [label, caption, v, m, ref] = parts;
    const max = parseFloat(m) || 0, val = parseFloat(v) || 0, refined = parseFloat(ref) || 0;
    const row = el('div', 'barrow');
    const top = el('div', 'top');
    top.appendChild(el('b', null, label));
    top.appendChild(el('span', null, caption));
    row.appendChild(top);
    const track = el('div', 'track');
    const pct = x => max > 0 ? Math.max(0, Math.min(100, (x / max) * 100)) : 0;
    const refPart = Math.min(refined, val);
    const g = el('i'); g.style.width = pct(refPart) + '%'; g.style.background = BAR_REFINED;
    const a = el('i'); a.style.width = pct(val - refPart) + '%'; a.style.background = BAR_RAW;
    track.appendChild(g); track.appendChild(a);
    row.appendChild(track);
    host.appendChild(row);
  }
  return host;
}

function buildGrid(text, palette, panelId, action) {
  const host = el('div', 'grid');
  const lines = (text || '').split('\n');
  for (let r = 0; r < lines.length; r++) {
    const row = el('div');
    const chars = [...lines[r]];
    for (let c = 0; c < chars.length; c++) {
      const ch = chars[c];
      const cell = el('span', null, ch);
      const colour = palette && palette[ch];
      if (colour) cell.style.color = colour;
      if (action) {
        // Coordinates go back verbatim; only the plugin knows what a cell means.
        cell.style.cursor = 'pointer';
        cell.dataset.c = String(c);
        cell.dataset.r = String(r);
        cell.addEventListener('click', () =>
          send({ type:'hud.cell', sessionId, panelId, action, col:c, row:r, ch }));
      }
      row.appendChild(cell);
    }
    host.appendChild(row);
  }
  return host;
}

function buildWidget(panelId, w, panelFg) {
  const type = (w.type || 'label').toLowerCase();
  const fg = w.color || panelFg;

  switch (type) {
    case 'button':
    case 'buttonrow': {
      const box = el('div', 'w-buttons');
      const kids = type === 'button' ? [w] : (w.children || []);
      for (const b of kids) {
        const btn = el('button', null, b.text || 'Button');
        if (b.action) btn.addEventListener('click', () =>
          send({ type:'hud.action', sessionId, panelId, action: b.action }));
        else btn.disabled = true;
        box.appendChild(btn);
      }
      return box;
    }

    case 'gauge':
    case 'progress': {
      const box = el('div', 'w-gauge');
      const cap = el('div', 'cap');
      cap.appendChild(el('span', null, w.text || ''));
      const readout = el('span', null, '');
      cap.appendChild(readout);
      box.appendChild(cap);
      const bar = el('div', 'bar');
      const fill = el('i');
      bar.appendChild(fill);
      box.appendChild(bar);

      let val = 0, max = 100;
      const paint = () => {
        const ratio = max > 0 ? Math.max(0, Math.min(1, val / max)) : 0;
        fill.style.width = (ratio * 100) + '%';
        // progress honours an explicit colour only; gauge falls back to the ramp.
        fill.style.background = type === 'progress'
          ? (w.color || GAUGE_HEALTHY)
          : gaugeColour(ratio, w.color, !!w.dim);
        readout.textContent = `${Math.round(val)}/${Math.round(max)}`;
      };
      bindNumber(w.value, v => { val = v; paint(); });
      bindNumber(w.max, v => { max = v <= 0 ? 1 : v; paint(); });
      paint();
      return box;
    }

    case 'value': {
      const e = el('div', 'w-label');
      if (fg) e.style.color = fg;
      bind(w.bind, v => { e.textContent = (w.text || '') + v; });
      return e;
    }

    case 'text': {
      const e = el('div', 'w-text');
      if (fg) e.style.color = fg;
      bind(w.bind, v => { e.textContent = v; });
      return e;
    }

    case 'barlist': {
      const host = el('div');
      bind(w.bind, v => { host.replaceChildren(buildBarList(v)); });
      return host;
    }

    case 'colorgrid': {
      const host = el('div');
      bind(w.bind, v => { host.replaceChildren(buildGrid(v, w.palette, panelId, w.action)); });
      return host;
    }

    case 'input': {
      const box = el('div', 'w-input');
      if (w.text) box.appendChild(el('div', 'w-label', w.text));

      const row = el('div', 'w-inputrow');
      const field = document.createElement('input');
      field.type = 'text';
      field.autocomplete = 'off';
      field.autocapitalize = 'off';
      field.spellcheck = false;
      field.enterKeyHint = 'send';

      const submit = () => {
        if (!w.action) return;
        send({ type:'hud.submit', sessionId, panelId, action: w.action, text: field.value });
      };

      const go = el('button', null, 'Set');
      if (w.action) go.addEventListener('click', submit); else go.disabled = true;
      field.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); submit(); } });

      // The bind path seeds the field and tracks it, matching the desktop. Skip the update
      // while the field has focus, so a state echo cannot overwrite what is being typed.
      bind(w.bind, v => { if (document.activeElement !== field) field.value = v; });

      row.appendChild(field);
      row.appendChild(go);
      box.appendChild(row);
      return box;
    }

    default: {                       // label
      const e = el('div', 'w-label', w.text || '');
      if (fg) e.style.color = fg;
      if (w.bind) bind(w.bind, v => { e.textContent = v; });
      return e;
    }
  }
}

function buildPanel(panelId, spec) {
  const box = el('div', 'panel');
  if (spec.background) box.style.background = spec.background;
  if (spec.accent) box.style.borderColor = spec.accent;

  const title = el('h2', null, spec.title || panelId);
  if (spec.accent) { title.style.color = spec.accent; title.style.borderBottomColor = spec.accent; }
  box.appendChild(title);

  const body = el('div', 'body');
  const tabs = spec.tabs || [];

  if (tabs.length > 0) {
    const strip = el('div', 'ptabs');
    let active = panelTab.get(panelId) ?? 0;
    if (active >= tabs.length) active = 0;
    tabs.forEach((t, i) => {
      const b = el('button', i === active ? 'on' : null, t.title || `Tab ${i + 1}`);
      b.addEventListener('click', () => { panelTab.set(panelId, i); renderPanels(); });
      strip.appendChild(b);
    });
    box.appendChild(strip);
    for (const w of tabs[active].widgets || []) body.appendChild(buildWidget(panelId, w, spec.foreground));
  } else {
    for (const w of spec.widgets || []) body.appendChild(buildWidget(panelId, w, spec.foreground));
  }

  box.appendChild(body);
  return box;
}

function renderPanels() {
  // Rebuild wholesale and re-register binders. The widget *set* is fixed once a panel is
  // built (only bound values change), so this only runs when panels themselves change or a
  // tab is switched — not on every state update.
  binders.clear();
  const host = $('panels');
  host.replaceChildren();

  if (panels.size === 0) {
    host.appendChild(el('div', 'w-note', 'No plugin panels for this session.'));
  } else {
    for (const [id, spec] of panels) host.appendChild(buildPanel(id, spec));
  }

  const badge = $('tab-panels');
  badge.innerHTML = 'Panels' + (panels.size ? ` <span class="badge">${panels.size}</span>` : '');
}

function applyStateToPanels(path) {
  const fns = binders.get(path);
  if (!fns) return;
  const v = stateText(path);
  for (const fn of fns) fn(v);
}

function showTab(which) {
  $('panels').classList.toggle('show', which === 'panels');
  $('chat').classList.toggle('show', which === 'chat');
  $('out').classList.toggle('hide', which !== 'out');
  $('tab-out').classList.toggle('on', which === 'out');
  $('tab-chat').classList.toggle('on', which === 'chat');
  $('tab-panels').classList.toggle('on', which === 'panels');

  // Opening Chat clears the badge for whatever pane you land on — you are looking at it.
  if (which === 'chat' && activePane) selectPane(activePane);

  // The command line stays available on every tab: reading a tell and replying should
  // not require switching back to Output first.
}

// ---- command pad ------------------------------------------------------------

const PAD = [
  ['NW','northwest'], ['N','north'], ['NE','northeast'], ['Up','up'],   ['Look','look'], ['Inv','inventory'],
  ['W','west'],       ['—',null],    ['E','east'],       ['Down','down'],['Kill','kill'], ['Flee','flee'],
  ['SW','southwest'], ['S','south'], ['SE','southeast'], ['Get','get all'],['Score','score'],['Rest','rest'],
];

function buildPad() {
  const pad = $('pad');
  for (const [label, cmd] of PAD) {
    const b = document.createElement('button');
    b.textContent = label;
    if (cmd === null) { b.disabled = true; b.style.visibility = 'hidden'; }
    else b.addEventListener('click', () => sendCommand(cmd));
    pad.appendChild(b);
  }
}

// ---- history ----------------------------------------------------------------

const history = [];
let historyIndex = -1;

function recall() {
  if (!history.length) return;
  historyIndex = historyIndex < 0 ? history.length - 1 : Math.max(0, historyIndex - 1);
  $('cmd').value = history[historyIndex];
  $('cmd').focus();
}

// ---- socket -----------------------------------------------------------------

function send(obj) {
  if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj));
}

function sendCommand(command) {
  if (!sessionId) return;
  send({ type:'command.send', sessionId, command });
}

// MXP link actions and pad presses both go out as plain commands; the desktop decides
// what they mean. The client never interprets command text itself.
const sendRaw = sendCommand;

function submit() {
  const text = $('cmd').value;
  if (!text) return;
  history.push(text);
  historyIndex = -1;
  $('cmd').value = '';
  sendCommand(text);
}

function handle(msg) {
  switch (msg.type) {
    case 'session.list': {
      const sel = $('session');
      sel.innerHTML = '';
      for (const s of msg.sessions) {
        const o = document.createElement('option');
        o.value = s.sessionId;
        o.textContent = (s.character || s.sessionId) + (s.connected ? '' : ' (offline)');
        sel.appendChild(o);
      }
      if (store.session && msg.sessions.some(s => s.sessionId === store.session)) {
        sel.value = store.session;
        subscribe(store.session);      // remembered from last time; go straight in
      } else {
        $('setup').classList.remove('hidden');
      }
      break;
    }
    case 'session.snapshot':
      // Adopt the id the snapshot names. Normally subscribe() already set it, but a
      // snapshot can also arrive as the answer to a resume, and a client that had not
      // yet set sessionId would go on sending commands addressed to null.
      if (msg.sessionId) { sessionId = msg.sessionId; store.session = msg.sessionId; }
      out.innerHTML = '';
      lastSeq = -1;
      state = {};
      for (const s of msg.state || []) state[s.path] = { kind:s.kind, text:s.text };
      panels.clear();
      for (const p of msg.panels || []) panels.set(p.panelId, p.spec);
      renderPanels();
      clearPanes();
      for (const p of msg.panes || []) addPaneLines(p.pane, p);
      // History is not "unread" — it is what you already missed being told about.
      for (const [, st] of panes) st.unread = 0;
      renderPaneTabs();
      updateChatBadge();
      applyBatch(msg.output);
      renderVitals();
      setWho(msg.session);
      $('setup').classList.add('hidden');
      break;
    case 'output.batch':
      applyBatch(msg);
      break;
    case 'output.pane':
      addPaneLines(msg.pane, msg);
      break;
    case 'state.update':
      if (msg.removed) delete state[msg.path];
      else state[msg.path] = { kind:msg.kind, text:msg.text };
      renderVitals();
      applyStateToPanels(msg.path);
      break;
    case 'hud.panel':
      panels.set(msg.panelId, msg.spec);
      renderPanels();
      break;
    case 'hud.panel.removed':
      panels.delete(msg.panelId);
      panelTab.delete(msg.panelId);
      renderPanels();
      break;
    case 'session.state':
      if (msg.sessionId === sessionId) setWho(msg);
      break;
    case 'error':
      note(`${msg.code}: ${msg.detail}`, 'err');
      if (msg.code === 'unknownSession') $('setup').classList.remove('hidden');
      break;
  }
}

function setWho(s) {
  $('who').textContent = (s.character || s.sessionId) + (s.connected ? '' : ' — offline');
}

function subscribe(id) {
  sessionId = id;
  store.session = id;
  send({ type:'session.subscribe', sessionId: id });
}

function connect() {
  manualClose = false;
  const token = store.token;
  if (tokenRequired && !token) { $('setup').classList.remove('hidden'); return; }

  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  // The tailnet identity header rides along with the handshake automatically; the token is
  // only appended when one exists, so a tailnet client sends no credential at all.
  const qs = token ? `?token=${encodeURIComponent(token)}` : '';
  ws = new WebSocket(`${scheme}://${location.host}/companion${qs}`);

  ws.onopen = () => {
    $('dot').className = 'on';
    reconnectDelay = 1000;
    $('err').textContent = '';
  };

  ws.onmessage = e => {
    let msg;
    try { msg = JSON.parse(e.data); } catch { return; }
    handle(msg);
  };

  ws.onclose = () => {
    $('dot').className = '';
    ws = null;
    if (manualClose) return;
    // Reconnect with backoff, then resume from the last sequence seen — the desktop
    // replays the gap, or sends a snapshot when it is too large (design §6).
    setTimeout(reconnect, reconnectDelay);
    reconnectDelay = Math.min(reconnectDelay * 2, 30000);
  };

  ws.onerror = () => { $('err').textContent = 'connection failed — check the token'; };
}

function reconnect() {
  connect();
  const waitThenResume = () => {
    if (!ws) return;
    if (ws.readyState !== 1) { setTimeout(waitThenResume, 100); return; }
    if (sessionId) send({ type:'session.resume', sessionId, lastReceivedSequence: lastSeq });
  };
  setTimeout(waitThenResume, 100);
}

// A phone suspends the socket when backgrounded. Coming back to the foreground is the
// moment to check and resume, rather than waiting for a timer to notice.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState !== 'visible') return;
  if (!ws || ws.readyState > 1) reconnect();
  else if (sessionId) send({ type:'session.resume', sessionId, lastReceivedSequence: lastSeq });
});

// ---- wiring -----------------------------------------------------------------

buildPad();
$('token').value = store.token;

$('go').addEventListener('click', () => {
  store.token = $('token').value.trim();
  const chosen = $('session').value;
  if (ws && ws.readyState === 1) {
    if (chosen) subscribe(chosen);
  } else {
    connect();
  }
});

$('send').addEventListener('click', submit);
$('up').addEventListener('click', recall);
$('cmd').addEventListener('keydown', e => {
  if (e.key === 'Enter') { e.preventDefault(); submit(); }
  if (e.key === 'ArrowUp') { e.preventDefault(); recall(); }
});
$('menu').addEventListener('click', () => $('setup').classList.toggle('hidden'));
$('notify').addEventListener('click', toggleNotifications);
pushState().catch(() => {});   // never let a capability probe break startup

$('tab-out').addEventListener('click', () => showTab('out'));
$('tab-chat').addEventListener('click', () => showTab('chat'));
$('tab-panels').addEventListener('click', () => showTab('panels'));
renderPanels();
renderPaneTabs();
renderPaneLines();

if ('serviceWorker' in navigator)
  navigator.serviceWorker.register('/sw.js').catch(() => {});

// Ask whether a token is needed before deciding what to show. Reaching the server over
// the tailnet means the proxy already identified us, so there is nothing to type.
fetch('/whoami', { cache: 'no-store' })
  .then(r => r.json())
  .then(w => {
    tokenRequired = w.needsToken;
    if (w.authorized) {
      $('err').textContent = '';
      if (w.login) note(`authenticated as ${w.login} via tailnet`);
      connect();
    } else if (store.token) {
      connect();
    } else {
      $('setup').classList.remove('hidden');
    }
  })
  .catch(() => { if (store.token) connect(); else $('setup').classList.remove('hidden'); });
})();
""";
}
