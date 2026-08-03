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
      out.innerHTML = '';
      lastSeq = -1;
      state = {};
      for (const s of msg.state || []) state[s.path] = { kind:s.kind, text:s.text };
      applyBatch(msg.output);
      renderVitals();
      setWho(msg.session);
      $('setup').classList.add('hidden');
      break;
    case 'output.batch':
      applyBatch(msg);
      break;
    case 'output.pane':
      // Chat and other routed panes: not shown in this build, but they arrive here and
      // the sequence numbering is per pane, ready for a dedicated view.
      break;
    case 'state.update':
      if (msg.removed) delete state[msg.path];
      else state[msg.path] = { kind:msg.kind, text:msg.text };
      renderVitals();
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
