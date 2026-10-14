// Bellboard service worker: an offline-capable app shell and push notifications.
// API responses are never cached.
'use strict';

const VERSION = 'bellboard-shell-v1';
const SHELL = [
  '/',
  '/index.html',
  '/app.css',
  '/app.js',
  '/js/api.js',
  '/js/store.js',
  '/js/ui.js',
  '/js/views/inbox.js',
  '/js/views/workspaces.js',
  '/js/views/pane.js',
  '/js/views/shared.js',
  '/js/views/terminal.js',
  '/js/views/notifications.js',
  '/js/views/newagent.js',
  '/js/views/device.js',
  '/js/views/pair.js',
  '/manifest.webmanifest',
  '/icon.svg',
  '/icon-192.png',
  '/icon-512.png',
  '/apple-touch-icon.png',
  '/badge-96.png',
  '/vendor/xterm.js',
  '/vendor/xterm.css',
  '/vendor/addon-unicode11.js',
];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(VERSION)
    .then((c) => Promise.all(SHELL.map((u) => c.add(new Request(u, { cache: 'reload' })).catch(() => { }))))
    .then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(caches.keys()
    .then((keys) => Promise.all(keys.filter((k) => k.startsWith('bellboard-shell-') && k !== VERSION).map((k) => caches.delete(k))))
    .then(() => self.clients.claim()));
});

// Network first (a new Bellboard version brings a new app), the cached shell when offline.
self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin || url.pathname.startsWith('/api/')) return;
  event.respondWith((async () => {
    try {
      const res = await fetch(req);
      if (res.ok && res.type === 'basic') {
        const copy = res.clone();
        caches.open(VERSION).then((c) => c.put(req, copy)).catch(() => { });
      }
      return res;
    } catch (_) {
      const cached = await caches.match(req, { ignoreSearch: true })
        || (req.mode === 'navigate' ? await caches.match('/index.html') : undefined);
      return cached || new Response('Offline', { status: 503, headers: { 'Content-Type': 'text/plain' } });
    }
  })());
});

// ---- push ----
self.addEventListener('push', (event) => {
  let p = {};
  try { p = event.data ? event.data.json() : {}; } catch (_) { p = { title: 'Bellboard', body: event.data ? event.data.text() : '' }; }
  const title = p.title || 'Bellboard';
  const opts = {
    body: p.body || '',
    tag: p.tag || p.pane || 'bellboard',
    renotify: true,
    icon: '/icon-192.png',
    badge: '/badge-96.png',
    data: { url: p.url || (p.pane ? '/#/pane/' + encodeURIComponent(p.pane) : '/#/'), pane: p.pane, promptId: p.promptId, kind: p.kind },
    timestamp: Date.now(),
  };
  if (p.kind === 'prompt' && p.promptId && p.pane) {
    // Allow only from a notification that shows what is being allowed (the desktop's "include message text");
    // otherwise only Deny, and tapping the notification opens the request with its details.
    opts.actions = p.canAllow
      ? [{ action: 'allow', title: 'Allow' }, { action: 'deny', title: 'Deny' }]
      : [{ action: 'deny', title: 'Deny' }];
    opts.requireInteraction = true;
  }
  event.waitUntil(self.registration.showNotification(title, opts));
});

self.addEventListener('notificationclick', (event) => {
  const n = event.notification;
  const d = n.data || {};
  n.close();
  if ((event.action === 'allow' || event.action === 'deny') && d.promptId) {
    event.waitUntil(answer(d, event.action));
    return;
  }
  event.waitUntil(openApp(d.url || '/#/'));
});

async function answer(d, decision) {
  try {
    const res = await fetch('/api/call', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ method: 'agent.answer', params: { pane: d.pane, promptId: d.promptId, decision } }),
    });
    if (res.ok) return;
    let msg = 'Bellboard couldn’t take the answer';
    try { msg = (await res.json()).error || msg; } catch (_) { }
    await self.registration.showNotification('Not answered', { body: msg, tag: d.pane || 'bellboard', icon: '/icon-192.png', badge: '/badge-96.png', data: { url: d.url } });
  } catch (_) {
    await openApp(d.url || '/#/');
  }
}

async function openApp(url) {
  const all = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
  for (const c of all) {
    if (new URL(c.url).origin === self.location.origin) {
      c.postMessage({ type: 'open', url });
      return c.focus();
    }
  }
  return self.clients.openWindow(url);
}
