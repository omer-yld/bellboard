// This device: its name and access, push notifications, signing out.
import { api } from '../api.js';
import { h, replace, toast, busy, confirmButton } from '../ui.js';

const LEVELS = {
  view: { name: 'View', text: 'See agents, alerts and terminals. Answering happens on the PC.' },
  respond: { name: 'Respond', text: 'Answer prompts and type into agents. Other terminals are view-only.' },
  full: { name: 'Full', text: 'Everything: type into any terminal, start and finish agents.' },
};

function standalone() {
  return window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
}
function isIOS() {
  return /iPhone|iPad|iPod/.test(navigator.userAgent) || (/Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1);
}

function urlBase64ToUint8Array(b64) {
  const pad = '='.repeat((4 - (b64.length % 4)) % 4);
  const raw = atob((b64 + pad).replace(/-/g, '+').replace(/_/g, '/'));
  return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}

async function currentSubscription() {
  if (!('serviceWorker' in navigator) || !('PushManager' in window)) return null;
  const reg = await navigator.serviceWorker.getRegistration('/');
  return reg ? reg.pushManager.getSubscription() : null;
}

export function deviceView({ onSignedOut }) {
  const info = h('section.section.device-info');
  const push = h('section.section');
  const signOut = h('section.section');
  const v = { title: 'This device', el: h('div.page', info, push, signOut), update };
  let last = '';

  function update(state) {
    const key = JSON.stringify([state.device, state.version]);
    if (key === last) return;
    last = key;
    const d = state.device || {};
    const lv = LEVELS[d.level] || LEVELS.view;
    replace(info,
      h('h2.device-name', { text: d.name || 'This device' }),
      h('div.level', h('span.level-badge', { class: 'level-' + (d.level || 'view'), text: lv.name + ' access' }), h('p.level-text', { text: lv.text })),
      h('p.field-hint', { text: 'Change access or revoke this device on your PC: Settings › Remote › Devices.' }),
      state.version ? h('p.faint', { text: 'Bellboard ' + state.version }) : null);
  }

  // ---- push ----
  async function renderPush() {
    const supported = 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
    const head = h('h2.section-title', { text: 'Notifications' });
    if (!supported) {
      replace(push, head, h('p.muted', {
        text: isIOS() && !standalone()
          ? 'On iPhone and iPad, notifications work once Bellboard is on your Home Screen: tap Share, then Add to Home Screen, and open it from there.'
          : 'This browser can’t receive push notifications.',
      }));
      return;
    }
    let key = null;
    try { key = (await api.pushKey()).publicKey; } catch (_) { }
    if (!key) {
      replace(push, head, h('p.muted', { text: 'Push notifications are turned off on your PC (Settings › Remote).' }));
      return;
    }
    const sub = await currentSubscription().catch(() => null);
    const on = !!sub && Notification.permission === 'granted';
    const toggle = h('button.switch', {
      type: 'button',
      attrs: { role: 'switch', 'aria-checked': on ? 'true' : 'false', 'aria-label': 'Push notifications' },
      onclick: (e) => busy(e.currentTarget, async () => {
        if (on) await disablePush(); else await enablePush(key);
        renderPush();
      }),
    }, h('span.switch-knob'));
    replace(push, head,
      h('div.switch-row', h('div', h('p.switch-label', { text: 'Push when an agent needs me' }),
        h('p.field-hint', { text: 'Sent only while this app is closed, and never in do not disturb.' })), toggle),
      Notification.permission === 'denied' ? h('p.form-error', { text: 'Notifications are blocked for this site. Allow them in your browser’s site settings.' }) : null,
      isIOS() && !standalone() ? h('p.field-hint', { text: 'On iPhone, add Bellboard to your Home Screen first (Share › Add to Home Screen).' }) : null);
  }

  async function enablePush(key) {
    const perm = await Notification.requestPermission();
    if (perm !== 'granted') throw new Error('Notifications weren’t allowed.');
    const reg = await navigator.serviceWorker.register('/sw.js', { scope: '/' });
    await navigator.serviceWorker.ready;
    let sub = await reg.pushManager.getSubscription();
    if (!sub) sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: urlBase64ToUint8Array(key) });
    await api.pushSubscribe(sub.toJSON());
    toast('Push notifications are on');
  }

  async function disablePush() {
    const sub = await currentSubscription();
    if (sub) await sub.unsubscribe().catch(() => { });
    await api.pushUnsubscribe();
    toast('Push notifications are off');
  }

  replace(signOut,
    h('h2.section-title', { text: 'Sign out' }),
    h('p.muted', { text: 'Removes this device from Bellboard. To use it again, pair it with a new code.' }),
    confirmButton('Sign out of this device', 'Tap again to sign out', async (b) => {
      await busy(b, async () => {
        await disablePush().catch(() => { });
        await api.logout();
        onSignedOut();
      });
    }, 'btn btn-danger btn-block'));

  renderPush();
  return v;
}

