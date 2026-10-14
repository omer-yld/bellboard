// Pairing: exchange the one-time code from the desktop for this device's key (a cookie).
import { api } from '../api.js';
import { h } from '../ui.js';

export function defaultDeviceName() {
  const ua = navigator.userAgent || '';
  if (/iPhone/.test(ua)) return 'iPhone';
  if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'iPad';
  if (/Android/.test(ua)) return /Mobile/.test(ua) ? 'Android phone' : 'Android tablet';
  const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
  const os = /Windows/.test(ua) ? 'Windows' : /Mac OS X/.test(ua) ? 'Mac' : /Linux/.test(ua) ? 'Linux' : '';
  return os ? `${browser} on ${os}` : browser;
}

/** Codes are shown as ABCD-EFGH; accept any case, spaces or a missing dash. */
export function normalizeCode(s) {
  const c = String(s || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
  return c.length === 8 ? c.slice(0, 4) + '-' + c.slice(4) : c;
}

export function pairView(initialCode, onPaired) {
  const fromLink = !!initialCode;
  const code = h('input.field-input.code-input', {
    type: 'text', value: normalizeCode(initialCode), autocomplete: 'one-time-code', autocapitalize: 'characters',
    spellcheck: false, attrs: { 'aria-label': 'Pairing code', placeholder: 'ABCD-EFGH', maxlength: 12, inputmode: 'text' },
  });
  const name = h('input.field-input', {
    type: 'text', value: defaultDeviceName(), autocomplete: 'off', attrs: { 'aria-label': 'Device name', maxlength: 40 },
  });
  const error = h('p.form-error', { attrs: { role: 'alert' }, hidden: true });
  const submit = h('button.btn.btn-primary.btn-block', { type: 'submit', text: 'Pair this device' });

  code.addEventListener('input', () => {
    const pos = code.selectionStart;
    const before = code.value;
    const v = before.toUpperCase().replace(/[^A-Z0-9-]/g, '');
    if (v !== before) { code.value = v; try { code.setSelectionRange(pos, pos); } catch (_) { } }
  });

  const form = h('form.pair-form', {
    attrs: { novalidate: true },
    onsubmit: async (e) => {
      e.preventDefault();
      error.hidden = true;
      const c = normalizeCode(code.value);
      const n = name.value.trim() || defaultDeviceName();
      if (c.replace('-', '').length < 8) { showError('Enter the 8-character code shown on your PC.'); code.focus(); return; }
      submit.disabled = true;
      submit.textContent = 'Pairing…';
      try {
        await api.pair(c, n);
        onPaired();
      } catch (err) {
        showError(err.status === 429
          ? 'Too many wrong codes. Pairing is locked for a few minutes; try again later with a new code.'
          : err.message || 'Pairing failed.');
      } finally {
        submit.disabled = false;
        submit.textContent = 'Pair this device';
      }
    },
  },
  h('label.field', h('span.field-label', { text: 'Pairing code' }), code),
  h('label.field', h('span.field-label', { text: 'Name this device' }), name,
    h('span.field-hint', { text: 'Shown in Bellboard’s device list on your PC.' })),
  error,
  submit);

  function showError(msg) { error.textContent = msg; error.hidden = false; }

  const el = h('div.pair',
    h('div.pair-mark', h('img', { src: '/icon.svg', alt: '', width: 72, height: 72 })),
    h('h1.pair-title', { text: 'Pair with Bellboard' }),
    h('p.pair-lead', {
      text: fromLink
        ? 'Check the code matches the one on your PC, then pair.'
        : 'On your PC, open Bellboard › Settings › Remote and choose Pair a device. Scan the QR code, or type the code here.',
    }),
    form,
    h('p.pair-foot', { text: 'Each code works once and expires after two minutes. You can revoke this device from the PC at any time.' }));

  if (!fromLink) setTimeout(() => code.focus(), 50);
  return { el };
}
