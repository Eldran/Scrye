namespace Scrye.Companion.Server;

/// <summary>
/// A single self-contained HTML page served at <c>/</c>, for exercising the protocol from a
/// browser.
///
/// <para><b>Why serve it rather than paste into a console:</b> a page's own
/// Content-Security-Policy governs whether it may open a WebSocket, and browser-internal
/// pages (<c>chrome://…</c>) restrict <c>connect-src</c> to their own origins. Serving this
/// from the companion host makes the page same-origin with the socket, so no CSP is
/// involved at all.</para>
///
/// <para>This is a <b>debug scope</b>, not the mobile UI: it shows decoded frames and raw
/// JSON so protocol mistakes are visible. The real client (design §8.1, an installable PWA)
/// replaces it. Kept as a C# string rather than a content file so the server stays a single
/// assembly with no build-time asset copying.</para>
/// </summary>
internal static class DebugClient
{
    internal const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Scrye companion — debug</title>
<style>
  :root { color-scheme: dark; }
  body { margin:0; font:13px/1.45 ui-monospace,Consolas,Menlo,monospace;
         background:#0d0f12; color:#c8ccd2; }
  header { display:flex; gap:8px; align-items:center; flex-wrap:wrap;
           padding:8px 10px; background:#151920; border-bottom:1px solid #222833; }
  input,select,button { font:inherit; background:#0d0f12; color:#c8ccd2;
                        border:1px solid #2a3140; border-radius:4px; padding:4px 7px; }
  button { cursor:pointer; } button:hover { border-color:#46b45a; }
  #dot { width:9px; height:9px; border-radius:50%; background:#7a3030; }
  #dot.on { background:#46b45a; }
  main { display:grid; grid-template-columns:1fr 1fr; height:calc(100vh - 92px); }
  section { overflow:auto; padding:8px 10px; }
  section + section { border-left:1px solid #222833; }
  h2 { margin:0 0 6px; font-size:11px; letter-spacing:.08em; text-transform:uppercase; color:#6c7686; }
  #out { white-space:pre-wrap; word-break:break-word; }
  #raw div { border-bottom:1px solid #1b212b; padding:3px 0; color:#8b93a1; font-size:11px; }
  #raw .t { color:#46b45a; }
  footer { display:flex; gap:8px; padding:8px 10px; background:#151920; border-top:1px solid #222833; }
  #cmd { flex:1; }
  .sys { color:#f0c040; }
  .err { color:#ff6b6b; }
  .prompt { color:#6c7686; }
</style>
</head>
<body>
<header>
  <span id="dot"></span>
  <input id="token" placeholder="token" size="30">
  <button id="connect">connect</button>
  <select id="session"></select>
  <button id="subscribe">subscribe</button>
  <button id="resume">resume</button>
  <span id="seq" class="prompt">seq —</span>
</header>

<main>
  <section><h2>output</h2><div id="out"></div></section>
  <section><h2>frames</h2><div id="raw"></div></section>
</main>

<footer>
  <input id="cmd" placeholder="command (Enter to send)">
  <button id="send">send</button>
</footer>

<script>
(() => {
  const $ = id => document.getElementById(id);
  let ws = null, styles = [], lastSeq = -1, sessionId = null;

  // Remember the token across reloads so debugging isn't a paste-fest. sessionStorage,
  // not localStorage: this is a credential and it should not outlive the tab.
  $('token').value = sessionStorage.getItem('scrye-token') || '';

  const esc = s => s.replace(/[&<>]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));

  function log(kind, text) {
    const d = document.createElement('div');
    d.innerHTML = '<span class="t">' + kind + '</span> ' + esc(text);
    $('raw').appendChild(d);
    $('raw').scrollTop = $('raw').scrollHeight;
  }

  function sys(text, cls) {
    const d = document.createElement('div');
    d.className = cls || 'sys';
    d.textContent = text;
    $('out').appendChild(d);
    $('out').scrollTop = $('out').scrollHeight;
  }

  // Render one line by resolving each span's style index against the frame's table —
  // this is the whole point of the style-table design, exercised for real.
  function renderLine(line, table) {
    const div = document.createElement('div');
    if (line.prompt) div.className = 'prompt';
    for (const span of line.spans) {
      const st = table[span.s] || {};
      const el = document.createElement('span');
      el.textContent = span.text;
      if (st.fg) el.style.color = st.fg;
      if (st.bg && st.bg !== '#000000') el.style.background = st.bg;
      if (st.flags && st.flags.toLowerCase().includes('bold')) el.style.fontWeight = '700';
      if (st.flags && st.flags.toLowerCase().includes('underline')) el.style.textDecoration = 'underline';
      if (st.flags && st.flags.toLowerCase().includes('italic')) el.style.fontStyle = 'italic';
      if (span.link) {                       // MXP link: tap to send, per §7.4 sent raw
        el.style.textDecoration = 'underline';
        el.style.cursor = 'pointer';
        el.title = span.link.hint || span.link.action;
        el.onclick = () => send({ type:'command.send', sessionId, command: span.link.action });
      }
      div.appendChild(el);
    }
    $('out').appendChild(div);
    $('out').scrollTop = $('out').scrollHeight;
  }

  function applyBatch(m) {
    const table = m.styles || [];
    for (const line of m.lines || []) {
      renderLine(line, table);
      if (line.sequence > lastSeq) lastSeq = line.sequence;
    }
    $('seq').textContent = 'seq ' + lastSeq;
  }

  function handle(m) {
    switch (m.type) {
      case 'session.list': {
        const sel = $('session');
        sel.innerHTML = '';
        for (const s of m.sessions) {
          const o = document.createElement('option');
          o.value = s.sessionId;
          o.textContent = s.sessionId + (s.connected ? '' : ' (offline)');
          sel.appendChild(o);
        }
        break;
      }
      case 'session.snapshot':
        $('out').innerHTML = '';
        lastSeq = -1;
        styles = [];
        applyBatch(m.output);
        sys('— snapshot: ' + (m.state?.length || 0) + ' state leaves, ' +
            (m.panels?.length || 0) + ' panels —');
        break;
      case 'output.batch':  applyBatch(m); break;
      case 'state.update':  break;                       // shown in the frames pane
      case 'session.state': break;
      case 'error':         sys('error: ' + m.code + ' — ' + m.detail, 'err'); break;
    }
  }

  function send(obj) {
    if (ws && ws.readyState === 1) { ws.send(JSON.stringify(obj)); log('→', JSON.stringify(obj)); }
  }

  $('connect').onclick = () => {
    if (ws) { ws.close(); ws = null; }
    const token = $('token').value.trim();
    sessionStorage.setItem('scrye-token', token);
    // Same origin as this page, so ws:// here and wss:// once TLS lands, with no edit.
    const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
    ws = new WebSocket(`${scheme}://${location.host}/companion?token=${encodeURIComponent(token)}`);
    ws.onopen    = () => { $('dot').className = 'on'; sys('connected'); };
    ws.onclose   = () => { $('dot').className = '';   sys('disconnected'); };
    ws.onerror   = () => sys('socket error — wrong token?', 'err');
    ws.onmessage = e => {
      const m = JSON.parse(e.data);
      log('←', e.data.length > 400 ? e.data.slice(0, 400) + '…' : e.data);
      handle(m);
    };
  };

  $('subscribe').onclick = () => {
    sessionId = $('session').value;
    if (sessionId) send({ type:'session.subscribe', sessionId });
  };

  // Proves the replay-vs-snapshot path (§6) without having to kill the socket.
  $('resume').onclick = () => {
    sessionId = $('session').value;
    if (sessionId) send({ type:'session.resume', sessionId, lastReceivedSequence: lastSeq });
  };

  const submit = () => {
    const command = $('cmd').value;
    if (!sessionId) { sys('subscribe to a session first', 'err'); return; }
    send({ type:'command.send', sessionId, command });
    $('cmd').value = '';
  };
  $('send').onclick = submit;
  $('cmd').onkeydown = e => { if (e.key === 'Enter') submit(); };
})();
</script>
</body>
</html>
""";
}
