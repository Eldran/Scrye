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

  <div id="out"></div>
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
    <div id="err"></div>
  </div>
</div>

<script src="/app.js"></script>
</body>
</html>
""";
}
