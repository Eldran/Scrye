namespace Scrye.Companion.Server.Client;

/// <summary>
/// The manifest and service worker that make the companion an <b>installable</b> web app
/// rather than a page.
///
/// <para>This is not cosmetic. On iOS, Web Push is available only to a PWA the user has
/// added to the home screen — which is the entire reason §7.2 can promise notifications
/// with no hosted component. Manifest plus service worker are therefore part of the MVP,
/// not later polish (§8.1).</para>
///
/// <para>Android needs none of that for <em>notifications</em> — Chrome grants push to an
/// ordinary tab — but it is far stricter about what counts as <em>installable</em>, which
/// is why the icons below are rasters rather than the SVG this shipped with first.</para>
/// </summary>
internal static class PwaAssets
{
    /// <summary>
    /// <c>display: standalone</c> drops Safari's chrome, which matters more than it sounds:
    /// a MUD client fighting a browser toolbar for vertical space on a phone is unusable.
    /// <c>scope</c> and <c>start_url</c> are "/" because the app, the manifest and the
    /// WebSocket all share one origin — the same property that keeps CSP out of the picture.
    ///
    /// <para>The icons are PNG at exactly 192 and 512, and the SVG is deliberately <b>not</b>
    /// listed. iOS accepted a lone <c>image/svg+xml</c> entry with <c>"sizes": "any"</c>, so
    /// the gap went unnoticed; Chrome wants those two raster sizes to consider the app
    /// installable, and an SVG sized <c>any</c> has its own history of failing the check
    /// outright. Serving the SVG is still worth doing — it is the favicon — it just cannot
    /// be the thing installability rests on.</para>
    ///
    /// <para><c>purpose</c> is <c>"any maskable"</c>, which is a claim worth having checked
    /// rather than assumed: Android crops maskable icons to a circle of radius 40% of the
    /// width (76.8 px here) and the mark's furthest pixel sits 72.5 px from centre, so it
    /// survives the mask with about 4 px to spare. The dark field behind it is full-bleed,
    /// so whatever the launcher's mask shape, it only ever eats background.</para>
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
    { "src": "/icon-192.png", "sizes": "192x192", "type": "image/png", "purpose": "any maskable" },
    { "src": "/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any maskable" }
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
// Bumped to v2 when the icons became rasters: the activate handler deletes every cache
// whose name is not the current one, so the rename is what evicts the old asset set.
const SHELL = 'scrye-shell-v2';
const SHELL_URLS = ['/', '/app.js', '/manifest.webmanifest', '/icon.svg', '/icon-192.png'];

self.addEventListener('install', e => {
  e.waitUntil(caches.open(SHELL).then(c => c.addAll(SHELL_URLS)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', e => {
  e.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== SHELL).map(k => caches.delete(k))))
      .then(() => self.clients.claim()));
});

// A push arrives while the app is closed — the whole point of §7.2. iOS requires a
// *visible* notification for every push (userVisibleOnly), so there is deliberately no
// silent path here: showing nothing would get the subscription revoked.
self.addEventListener('push', e => {
  let data = { title: 'Scrye', body: '' };
  try { if (e.data) data = { ...data, ...e.data.json() }; }
  catch { if (e.data) data.body = e.data.text(); }

  e.waitUntil(self.registration.showNotification(data.title || 'Scrye', {
    body: data.body || '',
    // PNG, not the SVG: Chrome does not decode SVG for notification imagery, so the tell
    // would arrive with a blank slot where the mark should be. Safari was fine with it,
    // which is exactly why this survived unnoticed.
    icon: '/icon-192.png',
    badge: '/icon-192.png',
    tag: 'scrye',            // collapse bursts rather than stacking twenty tells
    renotify: true,
    data: { sessionId: data.sessionId || null },
  }));
});

// Tapping the notification should land in the running app if it is already open, rather
// than spawning a second window pointed at the same session.
self.addEventListener('notificationclick', e => {
  e.notification.close();
  e.waitUntil((async () => {
    const all = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const c of all) if ('focus' in c) return c.focus();
    if (self.clients.openWindow) return self.clients.openWindow('/');
  })());
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

    /// <summary>A monochrome mark — a stylised eye for "scrye". The SVG is the source of
    /// truth and is still served (favicon, notification badge); the two PNGs below are
    /// rasterised from it and are what the manifest points at.</summary>
    internal const string Icon = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 192 192">
  <rect width="192" height="192" fill="#0d0f12"/>
  <g fill="none" stroke="#46b45a" stroke-width="9" stroke-linejoin="round">
    <path d="M28 96C50 62 72 45 96 45s46 17 68 51c-22 34-44 51-68 51s-46-17-68-51z"/>
  </g>
  <circle cx="96" cy="96" r="21" fill="#46b45a"/>
</svg>
""";

    /// <summary>The 192 px raster, base64 in source so the client stays a single assembly
    /// with no asset pipeline — the same reason the SVG is inline. Whitespace inside the
    /// literal is ignored by <see cref="Convert.FromBase64String"/>.</summary>
    internal static readonly byte[] Icon192 = Convert.FromBase64String("""
    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
""");

    /// <summary>The 512 px raster. Chrome wants this size specifically: it is what the
    /// splash screen and the launcher icon are generated from.</summary>
    internal static readonly byte[] Icon512 = Convert.FromBase64String("""
    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
""");
}
