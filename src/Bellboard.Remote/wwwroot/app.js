// Bellboard phone app: boot, routing and the app frame (top bar, tab bar).
import { api, live, on } from './js/api.js';
import { store, setState, setConnection, subscribe, waitingCount } from './js/store.js';
import { h, icon, replace, banner, toast } from './js/ui.js';
import { inboxView } from './js/views/inbox.js';
import { workspacesView, workspaceView } from './js/views/workspaces.js';
import { paneView } from './js/views/pane.js';
import { terminalView } from './js/views/terminal.js';
import { notificationsView } from './js/views/notifications.js';
import { newAgentView } from './js/views/newagent.js';
import { deviceView } from './js/views/device.js';
import { pairView } from './js/views/pair.js';

const root = document.getElementById('app');
let current = null;        // the mounted view
let paired = false;
let frame = null;          // {bar, title, back, actions, conn, main, tabs}

const TABS = [
  { href: '#/', label: 'Inbox', icon: 'inbox', match: (r) => r === '' || r === 'pane', badge: () => waitingCount() },
  { href: '#/workspaces', label: 'Workspaces', icon: 'grid', match: (r) => r === 'workspaces' || r === 'ws' || r === 'new' },
  { href: '#/notifications', label: 'Alerts', icon: 'bell', match: (r) => r === 'notifications', badge: () => (store.state && store.state.unread) || 0 },
  { href: '#/device', label: 'Device', icon: 'phone', match: (r) => r === 'device' },
];

function parseHash() {
  const raw = location.hash.replace(/^#/, '');
  if (raw.startsWith('pair=')) return { name: 'pair', code: decodeURIComponent(raw.slice(5)) };
  const parts = raw.replace(/^\//, '').split('/').map(decodeURIComponent);
  return { name: parts[0] || '', id: parts[1] || null };
}

function viewFor(route) {
  switch (route.name) {
    case '': return inboxView();
    case 'workspaces': return workspacesView();
    case 'ws': return workspaceView(route.id);
    case 'pane': return paneView(route.id);
    case 'term': return terminalView(route.id);
    case 'notifications': return notificationsView();
    case 'new': return newAgentView();
    case 'device': return deviceView({ onSignedOut: unpaired });
    default: return inboxView();
  }
}

function buildFrame() {
  const back = h('a.bar-back', { href: '#/', attrs: { 'aria-label': 'Back' } }, icon('back'));
  const title = h('h1.bar-title');
  const actions = h('div.bar-actions');
  const conn = h('span.conn', { attrs: { role: 'status' } });
  const bar = h('header.appbar', back, h('div.bar-heading', title, conn), actions);
  const main = h('main.view', { attrs: { id: 'main' } });
  const tabs = h('nav.tabbar', { attrs: { 'aria-label': 'Sections' } }, TABS.map((t) => {
    const badge = h('span.tab-badge', { hidden: true });
    const a = h('a.tab', { href: t.href }, h('span.tab-icon', icon(t.icon), badge), h('span.tab-label', { text: t.label }));
    a._tab = t;
    a._badge = badge;
    return a;
  }));
  replace(root, bar, main, tabs);
  frame = { bar, title, back, actions, conn, main, tabs };
}

function renderChrome() {
  if (!frame || !current) return;
  const route = parseHash();
  frame.title.textContent = current.title || 'Bellboard';
  frame.back.hidden = !current.back;
  if (current.back) frame.back.setAttribute('href', current.back);
  replace(frame.actions, current.actions || []);
  root.classList.toggle('immersive', !!current.immersive);
  root.classList.toggle('has-back', !!current.back);
  for (const a of frame.tabs.children) {
    const active = a._tab.match(route.name);
    a.classList.toggle('active', active);
    if (active) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
    const n = a._tab.badge ? a._tab.badge() : 0;
    a._badge.hidden = !n;
    a._badge.textContent = n > 99 ? '99+' : String(n);
  }
  const c = store.connection;
  frame.conn.className = 'conn conn-' + c;
  frame.conn.textContent = c === 'online' ? '' : c === 'connecting' ? 'Connecting…' : 'Offline';
  frame.conn.setAttribute('aria-label', c === 'online' ? 'Connected' : frame.conn.textContent);
}

function route() {
  const r = parseHash();
  if (r.name === 'pair' && paired) {
    // Already paired: a stale pairing link just opens the app.
    history.replaceState(null, '', '#/');
    return route();
  }
  if (!paired) return showPair(r.name === 'pair' ? r.code : '');
  if (!frame) buildFrame();
  if (current && current.destroy) current.destroy();
  current = viewFor(r);
  replace(frame.main, current.el);
  frame.main.scrollTop = 0;
  window.scrollTo(0, 0);
  if (store.state && current.update) current.update(store.state);
  renderChrome();
}

function showPair(code) {
  if (current && current.destroy) current.destroy();
  current = null;
  frame = null;
  root.classList.remove('immersive', 'has-back');
  const v = pairView(code, async () => {
    history.replaceState(null, '', '#/');
    await startPaired();
  });
  replace(root, v.el);
}

async function startPaired() {
  paired = true;
  try {
    setState(await api.state());
  } catch (e) {
    if (e.notPaired) return;
    toast(e.message, 'error');
  }
  live.start();
  route();
}

function unpaired() {
  if (!paired && !frame) { showPair(''); return; }
  paired = false;
  live.stop();
  setState(null);
  if (navigator.clearAppBadge) navigator.clearAppBadge().catch(() => { });
  showPair('');
}

// ---- wiring ----
on('state', (s) => setState(s));
on('status', (s) => setConnection(s));
on('unpaired', () => unpaired());
on('error', (msg) => toast(msg, 'error'));
on('notice', (n) => {
  if (!n || document.visibilityState !== 'visible') return;
  const here = parseHash();
  if (here.name === 'pane' && here.id === n.pane) return;
  banner(n.title, n.body, () => { location.hash = '#/pane/' + encodeURIComponent(n.pane); });
});

subscribe((state) => {
  if (current && current.update && state) current.update(state);
  renderChrome();
  if (state && navigator.setAppBadge) {
    const n = waitingCount();
    (n ? navigator.setAppBadge(n) : navigator.clearAppBadge()).catch(() => { });
  }
});

window.addEventListener('hashchange', route);

// The service worker hands notification taps to an open window this way.
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.addEventListener('message', (ev) => {
    const m = ev.data || {};
    if (m.type === 'open' && typeof m.url === 'string' && m.url.startsWith('/#')) location.hash = m.url.slice(1);
  });
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js', { scope: '/' }).catch(() => { });
  });
}

async function boot() {
  const r = parseHash();
  let health = null;
  try { health = await api.health(); } catch (_) { }
  if (!health) {
    replace(root, h('div.boot',
      h('img', { src: '/icon.svg', alt: '', width: 56, height: 56 }),
      h('p', { text: "Can't reach Bellboard." }),
      h('p.muted', { text: 'Check that your PC is on, Bellboard is running and remote access is enabled in Settings › Remote.' }),
      h('button.btn.btn-primary', { type: 'button', text: 'Try again', onclick: () => boot() })));
    return;
  }
  if (!health.paired) { paired = false; showPair(r.name === 'pair' ? r.code : ''); return; }
  await startPaired();
}

boot();
