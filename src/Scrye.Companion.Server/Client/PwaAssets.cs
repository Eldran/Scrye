namespace Scrye.Companion.Server.Client;

/// <summary>
/// The manifest and service worker that make the companion an <b>installable</b> web app
/// rather than a page.
///
/// <para>This is not cosmetic. On iOS, Web Push is available only to a PWA the user has
/// added to the home screen — which is the entire reason §7.2 can promise notifications
/// with no hosted component. Manifest plus service worker are therefore part of the MVP,
/// not later polish (§8.1).</para>
/// </summary>
internal static class PwaAssets
{
    /// <summary>
    /// <c>display: standalone</c> drops Safari's chrome, which matters more than it sounds:
    /// a MUD client fighting a browser toolbar for vertical space on a phone is unusable.
    /// <c>scope</c> and <c>start_url</c> are "/" because the app, the manifest and the
    /// WebSocket all share one origin — the same property that keeps CSP out of the picture.
    /// </summary>
    internal const string Manifest = """
{
  "name": "Scrye Companion",
  "short_name": "Scrye",
  "description": "Mobile companion for the Scrye MUD client",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "orientation": "portrait",
  "background_color": "#0d0f12",
  "theme_color": "#0d0f12",
  "icons": [
    { "src": "/icon.svg", "sizes": "any", "type": "image/svg+xml", "purpose": "any maskable" }
  ]
}
""";

    /// <summary>
    /// Deliberately minimal. It caches the app shell so the UI paints instantly and survives
    /// a brief network blip, and does nothing else.
    ///
    /// <para>It must <b>never</b> touch <c>/companion</c>: WebSocket traffic does not go
    /// through <c>fetch</c>, and MUD output is live state that would be actively harmful to
    /// serve from a cache. The shell is fetched network-first so a rebuilt desktop does not
    /// leave a stale client pinned forever — a real hazard when the protocol is still
    /// changing under it.</para>
    /// </summary>
    internal const string ServiceWorker = """
const SHELL = 'scrye-shell-v1';
const SHELL_URLS = ['/', '/app.js', '/manifest.webmanifest', '/icon.svg'];

self.addEventListener('install', e => {
  e.waitUntil(caches.open(SHELL).then(c => c.addAll(SHELL_URLS)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', e => {
  e.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== SHELL).map(k => caches.delete(k))))
      .then(() => self.clients.claim()));
});

self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  // Live data and the socket are never cached.
  if (url.pathname.startsWith('/companion')) return;
  if (e.request.method !== 'GET') return;

  // Network-first: a stale shell against a newer protocol is worse than a slow paint.
  e.respondWith(
    fetch(e.request)
      .then(res => {
        const copy = res.clone();
        caches.open(SHELL).then(c => c.put(e.request, copy)).catch(() => {});
        return res;
      })
      .catch(() => caches.match(e.request).then(hit => hit || Response.error())));
});
""";

    /// <summary>A monochrome SVG mark — a stylised eye for "scrye". Inline SVG rather than
    /// PNG files so the whole client stays a single assembly with no asset pipeline.</summary>
    internal const string Icon = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 192 192">
  <rect width="192" height="192" fill="#0d0f12"/>
  <g fill="none" stroke="#46b45a" stroke-width="9" stroke-linejoin="round">
    <path d="M28 96C50 62 72 45 96 45s46 17 68 51c-22 34-44 51-68 51s-46-17-68-51z"/>
  </g>
  <circle cx="96" cy="96" r="21" fill="#46b45a"/>
</svg>
""";
}
