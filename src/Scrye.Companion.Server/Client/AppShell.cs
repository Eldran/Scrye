namespace Scrye.Companion.Server.Client;

/// <summary>
/// The companion app's HTML shell. Markup and styling only — behaviour lives in
/// <see cref="AppScript"/>, served separately so the service worker can cache the two
/// independently and so the JS is parse-checkable on its own.
///
/// <para>Designed for a phone held one-handed: output fills the screen, the command line
/// sits above the thumb, and the pad below it. Everything is sized in <c>dvh</c> rather than
/// <c>vh</c> because iOS Safari's toolbars change the viewport as you scroll, and <c>vh</c>
/// leaves the input box hidden underneath them.</para>
/// </summary>
internal static class AppShell
{
    internal const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover, maximum-scale=1">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
<meta name="theme-color" content="#0d0f12">
<link rel="manifest" href="/manifest.webmanifest">
<link rel="apple-touch-icon" href="/icon.svg">
<title>Scrye</title>
<style>
  :root {
    color-scheme: dark;
    --bg:#0d0f12; --panel:#151920; --line:#222833; --dim:#6c7686;
    --fg:#c8ccd2; --accent:#46b45a; --warn:#f0c040; --bad:#ff6b6b;
    /* Semantic plugin theme tokens (Scrye.Core.Plugins.ThemeToken). A plugin that writes
       color = "warning" resolves here on the phone and against the desktop ThemeScheme on
       the desktop, so one spec reads correctly in both. Names must match ThemeToken. */
    --tok-panelalt:#1b212b; --tok-inset:#11151b; --tok-ok:#4cbb6c; --tok-info:#5aa8e0;
    /* Safe-area insets matter in standalone mode: without them the pad sits under
       the home indicator and the header under the notch. */
    --top: env(safe-area-inset-top, 0px);
    --bottom: env(safe-area-inset-bottom, 0px);
  }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
  html,body { margin:0; height:100%; overflow:hidden; background:var(--bg); color:var(--fg);
              font:15px/1.4 ui-monospace,"SF Mono",Menlo,Consolas,monospace; }

  #app { display:flex; flex-direction:column; height:100dvh; padding-top:var(--top); }

  /* ---- header ---- */
  header { display:flex; align-items:center; gap:8px; padding:6px 10px;
           background:var(--panel); border-bottom:1px solid var(--line); flex:0 0 auto; }
  #dot { width:8px; height:8px; border-radius:50%; background:var(--bad); flex:0 0 auto; }
  #dot.on { background:var(--accent); }
  #who { font-weight:600; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  #vitals { margin-left:auto; display:flex; gap:10px; align-items:center; font-size:13px; }
  .vital { display:flex; align-items:center; gap:5px; }
  .vital b { font-weight:600; color:var(--dim); font-size:11px; letter-spacing:.06em; }
  .meter { width:54px; height:7px; border-radius:4px; background:#232a35; overflow:hidden; }
  .meter i { display:block; height:100%; background:var(--accent); transition:width .25s ease; }
  .meter.warn i { background:var(--warn); } .meter.bad i { background:var(--bad); }
  #menu { background:none; border:0; color:var(--dim); font:inherit; padding:4px 6px; }

  /* ---- output ---- */
  #out { flex:1 1 auto; overflow-y:auto; overflow-x:hidden; padding:8px 10px;
         white-space:pre-wrap; word-break:break-word; -webkit-overflow-scrolling:touch; }
  #out div { min-height:1.4em; }
  #out .sys { color:var(--warn); } #out .err { color:var(--bad); }
  #out a.mxp { color:inherit; text-decoration:underline; text-underline-offset:2px; }

  /* Sticky prompt: the last prompt line stays visible above the input instead of
     scrolling away, which is the main thing that makes a MUD readable on a phone. */
  #prompt { flex:0 0 auto; padding:4px 10px; border-top:1px solid var(--line);
            background:#11151b; color:var(--dim); white-space:pre-wrap;
            word-break:break-word; display:none; }
  #prompt.show { display:block; }

  /* ---- input + pad ---- */
  footer { flex:0 0 auto; background:var(--panel); border-top:1px solid var(--line);
           padding:6px 8px calc(6px + var(--bottom)); }
  #row { display:flex; gap:6px; }
  #cmd { flex:1; min-width:0; font:inherit; padding:9px 10px; border-radius:8px;
         background:var(--bg); color:var(--fg); border:1px solid #2a3140; }
  #cmd:focus { outline:none; border-color:var(--accent); }
  #row button { flex:0 0 auto; padding:9px 12px; border-radius:8px; border:1px solid #2a3140;
                background:var(--bg); color:var(--fg); font:inherit; }

  #pad { display:grid; grid-template-columns:repeat(6,1fr); gap:5px; margin-top:6px; }
  #pad button { padding:11px 0; border-radius:8px; border:1px solid #2a3140;
                background:#10141a; color:var(--fg); font:inherit; font-size:13px; }
  #pad button:active { background:var(--accent); color:#0d0f12; }
  #pad button.wide { grid-column:span 2; }

  /* ---- tabs ---- */
  #tabs { display:flex; flex:0 0 auto; background:var(--panel); border-bottom:1px solid var(--line); }
  #tabs button { flex:1; padding:8px 0; background:none; border:0; border-bottom:2px solid transparent;
                 color:var(--dim); font:inherit; font-size:13px; }
  #tabs button.on { color:var(--fg); border-bottom-color:var(--accent); }
  #tabs .badge { display:inline-block; min-width:16px; margin-left:5px; padding:0 4px; border-radius:8px;
                 background:#2a3140; color:var(--fg); font-size:11px; }

  /* ---- chat view ---- */
  #chat { flex:1 1 auto; display:none; flex-direction:column; min-height:0; }
  #chat.show { display:flex; }
  #chatpanes { display:flex; gap:4px; padding:6px 8px; overflow-x:auto; flex:0 0 auto;
               border-bottom:1px solid var(--line); }
  #chatpanes button { flex:0 0 auto; padding:5px 10px; border-radius:14px; border:1px solid #2a3140;
                      background:var(--bg); color:var(--dim); font:inherit; font-size:12px;
                      white-space:nowrap; }
  #chatpanes button.on { color:var(--fg); border-color:var(--accent); }
  #chatpanes .n { margin-left:5px; padding:0 5px; border-radius:8px; background:var(--accent);
                  color:#0d0f12; font-size:10px; font-weight:700; }
  #chatlines { flex:1 1 auto; overflow-y:auto; padding:8px 10px; white-space:pre-wrap;
               word-break:break-word; -webkit-overflow-scrolling:touch; }
  #chatlines div { min-height:1.4em; }
  #chatempty { color:var(--dim); font-size:13px; font-style:italic; }

  /* ---- panels view ---- */
  #panels { flex:1 1 auto; overflow-y:auto; padding:8px; display:none; }
  #panels.show { display:block; }
  #out.hide { display:none; }
  .panel { border:1px solid var(--line); border-radius:10px; margin-bottom:10px; overflow:hidden;
           background:#10141a; }
  .panel > h2 { margin:0; padding:7px 10px; font-size:13px; font-weight:600;
                border-bottom:1px solid var(--line); }
  .panel .body { padding:9px 10px; display:flex; flex-direction:column; gap:8px; }
  .ptabs { display:flex; flex-wrap:wrap; gap:4px; padding:6px 8px 0; }
  .ptabs button { padding:5px 9px; border-radius:6px; border:1px solid #2a3140; background:var(--bg);
                  color:var(--dim); font:inherit; font-size:12px; }
  .ptabs button.on { color:var(--fg); border-color:var(--accent); }

  .w-label { font-size:13px; }
  .w-text { font-size:12px; white-space:pre-wrap; word-break:break-word; line-height:1.35; }
  /* click=/prompt= runs inside a text widget: inherit the run's colour, underline says tappable */
  .w-text a.mk { color:inherit; text-decoration:underline; text-underline-offset:2px;
                 -webkit-tap-highlight-color:rgba(90,255,154,.18); }
  .w-row { display:flex; gap:10px; flex-wrap:wrap; align-items:flex-start; }
  .w-row > * { flex:1 1 140px; min-width:0; }
  .w-gauge .cap { display:flex; justify-content:space-between; font-size:12px; margin-bottom:3px; }
  .w-gauge .cap span:last-child { color:var(--dim); }
  .bar { height:9px; border-radius:5px; background:#1c232c; overflow:hidden; }
  .bar i { display:block; height:100%; transition:width .25s ease; }
  .w-buttons { display:flex; gap:6px; flex-wrap:wrap; }
  .w-buttons button { flex:1 1 auto; min-width:72px; padding:9px 10px; border-radius:8px;
                      border:1px solid #2a3140; background:var(--bg); color:var(--fg); font:inherit;
                      font-size:13px; }
  .w-buttons button:active { background:var(--accent); color:#0d0f12; }
  .w-buttons button[disabled] { opacity:.45; }
  .barlist { display:flex; flex-direction:column; gap:4px; }
  .barrow { font-size:11px; }
  .barrow .top { display:flex; justify-content:space-between; gap:8px; }
  .barrow .top b { font-weight:600; }
  .barrow .top span { color:#9fb0c0; }
  .barrow .track { height:8px; border-radius:4px; background:#1c232c; overflow:hidden; display:flex; }
  .barrow .track i { display:block; height:100%; box-sizing:border-box; min-width:2px; }
  /* hairline of track between quality stages, so two adjacent stages of similar quality
     still read as two segments rather than one wide band (the desktop does the same) */
  .barrow .track i + i { border-left:1px solid #1c232c; }
  .grid { font-size:11px; line-height:1.05; white-space:pre; overflow-x:auto; }
  .grid span { display:inline-block; min-width:0.62em; }

  /* list / table widgets. A real <table> rather than the desktop's drawn columns: the browser
     already does column measurement, and on a narrow phone letting it wrap beats the desktop's
     fixed grid. Same data, layout appropriate to the device. */
  .w-table { width:100%; border-collapse:collapse; font-size:12px; }
  .w-table th { text-align:left; font-weight:600; color:var(--dim); font-size:11px;
                padding:0 8px 3px 0; border-bottom:1px solid var(--line); }
  .w-table td { padding:2px 8px 2px 0; vertical-align:top; word-break:break-word; }
  .w-table td:last-child, .w-table th:last-child { padding-right:0; }
  .w-table .a-r { text-align:right; }
  .w-table .a-c { text-align:center; }
  .w-table.dimtrail td:not(:first-child) { color:var(--dim); }
  .w-inputrow { display:flex; gap:6px; margin-top:4px; }
  .w-inputrow input { flex:1; min-width:0; font:inherit; font-size:13px; padding:7px 8px;
                      border-radius:7px; background:var(--bg); color:var(--fg);
                      border:1px solid #2a3140; }
  .w-inputrow input:focus { outline:none; border-color:var(--accent); }
  .w-inputrow button { flex:0 0 auto; padding:7px 12px; border-radius:7px; border:1px solid #2a3140;
                       background:var(--bg); color:var(--fg); font:inherit; font-size:13px; }
  .w-inputrow button[disabled] { opacity:.45; }
  .w-note { font-size:11px; color:var(--dim); font-style:italic; }

  /* ---- setup sheet ---- */
  #setup { position:fixed; inset:0; background:rgba(8,10,13,.96); display:flex;
           align-items:center; justify-content:center; padding:20px; z-index:10; }
  #setup.hidden { display:none; }
  #setup .card { width:100%; max-width:380px; background:var(--panel);
                 border:1px solid var(--line); border-radius:12px; padding:16px; }
  #setup h1 { margin:0 0 4px; font-size:17px; }
  #setup p { margin:0 0 14px; color:var(--dim); font-size:13px; line-height:1.5; }
  #setup label { display:block; font-size:12px; color:var(--dim); margin:10px 0 4px; }
  #setup input, #setup select { width:100%; font:inherit; padding:9px 10px; border-radius:8px;
                                background:var(--bg); color:var(--fg); border:1px solid #2a3140; }
  #setup button { width:100%; margin-top:14px; padding:11px; border-radius:8px; border:0;
                  background:var(--accent); color:#0d0f12; font:inherit; font-weight:600; }
  #err { color:var(--bad); font-size:13px; margin-top:10px; min-height:1.2em; }
</style>
</head>
<body>

<div id="app">
  <header>
    <span id="dot"></span>
    <span id="who">not connected</span>
    <div id="vitals"></div>
    <button id="menu" title="Settings">⋯</button>
  </header>

  <div id="tabs">
    <button id="tab-out" class="on">Output</button>
    <button id="tab-chat">Chat</button>
    <button id="tab-panels">Panels</button>
  </div>

  <div id="out"></div>
  <div id="chat">
    <div id="chatpanes"></div>
    <div id="chatlines"></div>
  </div>
  <div id="panels"></div>
  <div id="prompt"></div>

  <footer>
    <div id="row">
      <input id="cmd" placeholder="command" autocomplete="off" autocapitalize="off"
             autocorrect="off" spellcheck="false" enterkeyhint="send">
      <button id="up" title="Previous command">↑</button>
      <button id="send">Send</button>
    </div>
    <div id="pad"></div>
  </footer>
</div>

<div id="setup">
  <div class="card">
    <h1>Scrye Companion</h1>
    <p>Run <b>.companion</b> in Scrye on your PC to get a token. It changes each time the
       server starts.</p>
    <label for="token">Token</label>
    <input id="token" placeholder="paste token" autocomplete="off" autocapitalize="off"
           autocorrect="off" spellcheck="false">
    <label for="session">Session</label>
    <select id="session"><option value="">(connect first)</option></select>
    <button id="go">Connect</button>
    <button id="notify" style="margin-top:8px;background:#2a3140;color:var(--fg)">Enable notifications</button>
    <div id="notifystate" class="w-note" style="margin-top:6px"></div>
    <div id="err"></div>
  </div>
</div>

<script src="/app.js"></script>
</body>
</html>
""";
}
