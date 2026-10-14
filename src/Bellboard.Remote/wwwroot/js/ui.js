// DOM helpers. Server data only ever reaches the page through textContent and attributes, never innerHTML.

import { clock } from './store.js';

/**
 * h('div.card.waiting', {onclick, dataset:{pane:'p3'}, attrs:{role:'button'}}, child, 'text', [more])
 * Children: nodes, strings (become text), arrays, null/false (skipped).
 */
export function h(spec, props, ...children) {
  const [tag, ...classes] = spec.split('.');
  const el = document.createElement(tag || 'div');
  if (classes.length) el.className = classes.join(' ');
  if (props && (typeof props !== 'object' || props instanceof Node || Array.isArray(props))) {
    children.unshift(props);
    props = null;
  }
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null || v === false) continue;
      if (k === 'class') el.className += (el.className ? ' ' : '') + v;
      else if (k === 'dataset') Object.assign(el.dataset, v);
      else if (k === 'attrs') for (const [a, av] of Object.entries(v)) { if (av != null && av !== false) el.setAttribute(a, av === true ? '' : String(av)); }
      else if (k === 'style') Object.assign(el.style, v);
      else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
      else if (k === 'text') el.textContent = v;
      else if (k in el) el[k] = v;
      else el.setAttribute(k, v === true ? '' : String(v));
    }
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const c of children) {
    if (c == null || c === false || c === true) continue;
    if (Array.isArray(c)) append(el, c);
    else if (c instanceof Node) el.appendChild(c);
    else el.appendChild(document.createTextNode(String(c)));
  }
}

export function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); return el; }
export function replace(el, ...children) { clear(el); append(el, children); return el; }

// ---- icons: small inline SVGs, drawn on a 24 grid, stroked with currentColor ----
const ICONS = {
  inbox: 'M4 13h4l1.5 3h5L16 13h4M5.5 5h13L21 13v5.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5V13z',
  grid: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
  bell: 'M6 16.5c1-1 1.2-2.4 1.2-5.2C7.2 8 9.3 5.5 12 5.5s4.8 2.5 4.8 5.8c0 2.8.2 4.2 1.2 5.2zM10 19.5a2 2 0 0 0 4 0M12 3.2v2.3',
  phone: 'M8 3h8a1.5 1.5 0 0 1 1.5 1.5v15A1.5 1.5 0 0 1 16 21H8a1.5 1.5 0 0 1-1.5-1.5v-15A1.5 1.5 0 0 1 8 3zM11 18h2',
  back: 'M15 5l-7 7 7 7',
  chevron: 'M9 6l6 6-6 6',
  terminal: 'M4 5h16a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1zM7 9.5l3 2.5-3 2.5M12.5 15h4',
  refresh: 'M19 12a7 7 0 1 1-2.05-4.95M19 5v4h-4',
  send: 'M5 12h13M13 6l6 6-6 6',
  moon: 'M19 14.5A7.5 7.5 0 0 1 9.5 5a7.5 7.5 0 1 0 9.5 9.5z',
  plus: 'M12 5v14M5 12h14',
  branch: 'M7 4v11M7 15a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5zM17 4a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5zM17 9c0 4-4 4-10 6',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  x: 'M6 6l12 12M18 6L6 18',
  keyboard: 'M3.5 6.5h17v11h-17zM7 10h.01M10 10h.01M13 10h.01M16 10h.01M8 14h8',
  fit: 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5',
  zoomIn: 'M10.5 4a6.5 6.5 0 1 1 0 13 6.5 6.5 0 0 1 0-13zM15.5 15.5L20 20M10.5 8v5M8 10.5h5',
  zoomOut: 'M10.5 4a6.5 6.5 0 1 1 0 13 6.5 6.5 0 0 1 0-13zM15.5 15.5L20 20M8 10.5h5',
  flag: 'M5 21V4M5 4h11l-2 4 2 4H5',
};

export function icon(name, cls) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('class', 'icon' + (cls ? ' ' + cls : ''));
  const p = document.createElementNS(ns, 'path');
  p.setAttribute('d', ICONS[name] || '');
  svg.appendChild(p);
  return svg;
}

// ---- time ----
export function ago(iso) {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const s = Math.max(0, (Date.now() + clock.skew - t) / 1000);
  if (s < 45) return 'just now';
  const m = s / 60;
  if (m < 60) return `${Math.round(m)}m`;
  const hr = m / 60;
  if (hr < 24) return `${Math.floor(hr)}h ${Math.floor(m % 60)}m`;
  return `${Math.floor(hr / 24)}d`;
}

/** An element whose text is re-computed every 20 s while it is in the document. */
export function liveTime(iso, fmt = (a) => a) {
  const el = h('span.ago', { dataset: { since: iso || '' } });
  el._fmt = fmt;
  el.textContent = fmt(ago(iso));
  return el;
}

setInterval(() => {
  for (const el of document.querySelectorAll('.ago[data-since]')) {
    if (el.dataset.since) el.textContent = (el._fmt || ((a) => a))(ago(el.dataset.since));
  }
}, 20000);

// ---- status ----
export const STATUS_TEXT = {
  waiting: 'Needs input', working: 'Working', done: 'Done', idle: 'Idle', running: 'Running',
};

export function statusDot(status) {
  return h('span.dot', { class: 'dot-' + (status || 'idle'), attrs: { 'aria-hidden': 'true' } });
}

// ---- toast and banner ----
let toastTimer;
export function toast(text, kind) {
  const el = document.getElementById('toast');
  el.textContent = text;
  el.className = 'toast' + (kind ? ' toast-' + kind : '');
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, kind === 'error' ? 5000 : 2600);
}

let bannerTimer;
export function banner(title, body, onTap) {
  const el = document.getElementById('banner');
  replace(el, h('div.banner-inner', {
    attrs: { role: 'button', tabindex: '0' },
    onclick: () => { el.hidden = true; onTap && onTap(); },
    onkeydown: (e) => { if (e.key === 'Enter') { el.hidden = true; onTap && onTap(); } },
  }, icon('bell', 'banner-icon'), h('div.banner-text', h('strong', { text: title }), body ? h('span', { text: body }) : null)));
  el.hidden = false;
  clearTimeout(bannerTimer);
  bannerTimer = setTimeout(() => { el.hidden = true; }, 6000);
}

/** A button that needs a second tap to act: the first tap arms it and shows `armedLabel`. */
export function confirmButton(label, armedLabel, action, cls = 'btn') {
  const b = h('button.' + cls.split(' ').join('.'), { type: 'button', text: label });
  let armed = false, timer;
  b.addEventListener('click', async () => {
    if (!armed) {
      armed = true;
      b.textContent = armedLabel;
      b.classList.add('armed');
      timer = setTimeout(() => { armed = false; b.textContent = label; b.classList.remove('armed'); }, 5000);
      return;
    }
    clearTimeout(timer);
    armed = false;
    b.classList.remove('armed');
    b.textContent = label;
    await action(b);
  });
  return b;
}

/** Disable a button while an async action runs; report failures as a toast. */
export async function busy(btn, fn) {
  if (btn) btn.disabled = true;
  try { return await fn(); }
  catch (e) { toast(e.message || String(e), 'error'); return undefined; }
  finally { if (btn) btn.disabled = false; }
}

export function shortPath(p) {
  if (!p) return '';
  const s = String(p).replace(/\\\\wsl(\.localhost|\$)\\[^\\]+/i, '').replace(/\\/g, '/');
  const parts = s.split('/').filter(Boolean);
  return parts.length > 3 ? '…/' + parts.slice(-2).join('/') : s;
}
