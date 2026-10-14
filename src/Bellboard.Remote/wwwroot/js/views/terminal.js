// Live terminal: the desktop pane mirrored with xterm.js, read-only until you choose Type.
import { live, call } from '../api.js';
import { can, findPane, canType } from '../store.js';
import { h, icon, replace, toast, busy } from '../ui.js';
import { keyBar } from './shared.js';

let xtermLoading = null;
function loadXterm() {
  if (window.Terminal) return Promise.resolve();
  if (xtermLoading) return xtermLoading;
  const script = (src) => new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = src;
    s.onload = resolve;
    s.onerror = () => reject(new Error('Could not load the terminal'));
    document.head.appendChild(s);
  });
  if (!document.querySelector('link[href="/vendor/xterm.css"]')) {
    document.head.appendChild(h('link', { rel: 'stylesheet', href: '/vendor/xterm.css' }));
  }
  xtermLoading = script('/vendor/xterm.js').then(() => script('/vendor/addon-unicode11.js')).catch((e) => { xtermLoading = null; throw e; });
  return xtermLoading;
}

const SCROLLBACK = 2000;
const MIN_FONT = 6, MAX_FONT = 28, READABLE_FONT = 13;

export function terminalView(id) {
  const scroller = h('div.term-scroll');
  const hostEl = h('div.term-host');
  scroller.append(hostEl);
  const overlay = h('div.term-overlay', { hidden: true });
  const dock = h('div.term-dock');
  const el = h('div.term-wrap', scroller, overlay, dock);

  const typeBtn = h('button.icon-btn', { type: 'button', attrs: { 'aria-label': 'Type', 'aria-pressed': 'false', title: 'Type' }, onclick: () => setTyping(!typing) }, icon('keyboard'));
  const fitBtn = h('button.icon-btn', { type: 'button', attrs: { 'aria-label': 'Fit to phone', 'aria-pressed': 'false', title: 'Fit to phone' }, onclick: () => toggleFit() }, icon('fit'));
  const zoomOut = h('button.icon-btn', { type: 'button', attrs: { 'aria-label': 'Smaller text', title: 'Smaller' }, onclick: () => zoom(1 / 1.15) }, icon('zoomOut'));
  const zoomIn = h('button.icon-btn', { type: 'button', attrs: { 'aria-label': 'Larger text', title: 'Larger' }, onclick: () => zoom(1.15) }, icon('zoomIn'));

  const v = { title: 'Terminal', back: '#/pane/' + encodeURIComponent(id), immersive: true, el, actions: [zoomOut, zoomIn], update, destroy };

  let term = null;
  let seq = -1;
  let awaiting = true;           // ignore output until a snapshot arrives
  let typing = false;
  let fitFont = true;            // font follows the phone's width
  let fitted = false;            // "Fit to phone": the shell is sized for this phone
  let cellRatio = { w: 0.6, h: 1.2 };   // cell size per px of font size, measured after opening
  let destroyed = false;
  let sendBuf = '';
  let sendTimer = null;
  let allowed = false;

  function update() {
    const found = findPane(id);
    if (!found) { if (term) showOverlay('This pane is closed.'); return; }
    v.title = found.pane.title || found.ws.name;
    const may = canType(found.pane, found.ws);
    if (may !== allowed) {
      allowed = may;
      v.actions = [zoomOut, zoomIn, can('full') ? fitBtn : null, may ? typeBtn : null].filter(Boolean);
      if (!may && typing) setTyping(false);
    }
  }

  function showOverlay(text, action) {
    replace(overlay, h('p', { text }), action || null);
    overlay.hidden = false;
  }

  // Cell size per px of font size, measured once with a hidden run of the terminal's font
  // (xterm's own dimensions update a frame after a font change, which is too late to fit with).
  function measure() {
    if (!term) return;
    const probe = h('span', { text: 'W'.repeat(50) });
    Object.assign(probe.style, { position: 'absolute', visibility: 'hidden', whiteSpace: 'pre', fontFamily: term.options.fontFamily, fontSize: '100px', lineHeight: 'normal' });
    document.body.appendChild(probe);
    const r = probe.getBoundingClientRect();
    probe.remove();
    if (r.width > 0) cellRatio = { w: r.width / 50 / 100, h: (r.height / 100) * (term.options.lineHeight || 1) };
  }

  function applyFitFont() {
    if (!term || !fitFont) return;
    const avail = scroller.clientWidth - 8;
    if (avail <= 0) return;
    const f = Math.max(MIN_FONT, Math.min(MAX_FONT, Math.floor((avail / (term.cols * cellRatio.w)) * 10) / 10));
    if (Math.abs(f - term.options.fontSize) >= 0.1) term.options.fontSize = f;
    // xterm rounds cells to device pixels: check what it actually drew a couple of frames later and correct.
    requestAnimationFrame(() => requestAnimationFrame(correctFit));
  }

  let corrections = 0;
  function correctFit() {
    const screen = hostEl.querySelector('.xterm-screen');
    if (!term || !fitFont || !screen || !screen.offsetWidth) return;
    const f = term.options.fontSize;
    cellRatio.w = screen.offsetWidth / term.cols / f;
    const avail = scroller.clientWidth - 8;
    if (screen.offsetWidth > avail + 1 && f > MIN_FONT && corrections < 3) {
      corrections++;
      term.options.fontSize = Math.max(MIN_FONT, Math.floor(f * (avail / screen.offsetWidth) * 10) / 10);
      requestAnimationFrame(() => requestAnimationFrame(correctFit));
    } else {
      corrections = 0;
    }
  }

  function zoom(factor) {
    if (!term) return;
    fitFont = false;
    term.options.fontSize = Math.max(MIN_FONT, Math.min(MAX_FONT, Math.round(term.options.fontSize * factor * 10) / 10));
  }

  function onMessage(m) {
    if (!term || destroyed) return;
    switch (m.t) {
      case 'snapshot':
        overlay.hidden = true;
        term.reset();
        if (m.cols && m.rows && (m.cols !== term.cols || m.rows !== term.rows)) term.resize(m.cols, m.rows);
        term.write(m.data || '', () => term.scrollToBottom());
        seq = m.seq;
        awaiting = false;
        requestAnimationFrame(applyFitFont);
        break;
      case 'out':
        if (awaiting) return;
        if (m.seq !== seq + 1) { resync(); return; }
        seq = m.seq;
        term.write(m.d);
        break;
      case 'resize':
        if (m.cols && m.rows) { term.resize(m.cols, m.rows); requestAnimationFrame(applyFitFont); }
        break;
      case 'exit':
        awaiting = true;
        showOverlay('This pane closed on the PC.', h('a.btn.btn-quiet', { href: '#/', text: 'Back to the inbox' }));
        break;
      case 'overflow':
        resync();
        break;
    }
  }

  function resync() {
    awaiting = true;
    live.reattach(id);
  }

  function setTyping(on) {
    if (!term) return;
    typing = on && allowed;
    term.options.disableStdin = !typing;
    term.options.cursorBlink = typing;
    if (term.textarea) {
      if (typing) term.textarea.removeAttribute('inputmode');
      else term.textarea.setAttribute('inputmode', 'none');
    }
    typeBtn.setAttribute('aria-pressed', typing ? 'true' : 'false');
    typeBtn.classList.toggle('on', typing);
    replace(dock, typing ? keyBar(id) : null);
    el.classList.toggle('typing', typing);
    if (typing) term.focus(); else if (term.textarea) term.textarea.blur();
  }

  function queueSend(d) {
    sendBuf += d;
    if (sendTimer) return;
    sendTimer = setTimeout(() => {
      const text = sendBuf;
      sendBuf = '';
      sendTimer = null;
      call('terminal.send', { pane: id, text }).catch((e) => toast(e.message, 'error'));
    }, 25);
  }

  async function toggleFit() {
    if (!term) return;
    await busy(fitBtn, async () => {
      if (fitted) {
        await call('terminal.fit', { pane: id, off: true });
        fitted = false;
        fitFont = true;
      } else {
        const cols = Math.max(20, Math.floor((scroller.clientWidth - 8) / (READABLE_FONT * cellRatio.w)));
        const rows = Math.max(8, Math.floor((scroller.clientHeight - 8) / (READABLE_FONT * cellRatio.h)));
        const r = await call('terminal.fit', { pane: id, cols, rows });
        fitted = !!(r && r.fitted);
        if (!fitted) toast('Your PC is in use. Phone fit works while the desk is idle.');
        else { fitFont = true; toast(`Sized for this phone: ${r.cols}×${r.rows}`); }
      }
      fitBtn.setAttribute('aria-pressed', fitted ? 'true' : 'false');
      fitBtn.classList.toggle('on', fitted);
    });
  }

  // Pinch to zoom the text; one finger pans the scroll area.
  let pinch = null;
  scroller.addEventListener('touchstart', (e) => {
    if (e.touches.length === 2 && term) {
      pinch = { d: dist(e.touches), f: term.options.fontSize };
    }
  }, { passive: true });
  scroller.addEventListener('touchmove', (e) => {
    if (!pinch || e.touches.length !== 2 || !term) return;
    e.preventDefault();
    const f = Math.max(MIN_FONT, Math.min(MAX_FONT, Math.round(pinch.f * dist(e.touches) / pinch.d * 2) / 2));
    if (f !== term.options.fontSize) { fitFont = false; term.options.fontSize = f; }
  }, { passive: false });
  scroller.addEventListener('touchend', () => { pinch = null; }, { passive: true });
  function dist(t) { return Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY); }

  const onResize = () => requestAnimationFrame(applyFitFont);
  window.addEventListener('resize', onResize);
  window.addEventListener('orientationchange', onResize);

  showOverlay('Loading the terminal…');
  loadXterm().then(() => {
    if (destroyed) return;
    const found = findPane(id);
    const size = found && found.pane.size ? found.pane.size.split('x').map(Number) : [80, 24];
    term = new window.Terminal({
      cols: size[0] || 80,
      rows: size[1] || 24,
      fontSize: 10,
      fontFamily: '"JetBrains Mono", "Cascadia Mono", "SF Mono", Menlo, Consolas, "Roboto Mono", monospace',
      lineHeight: 1.1,
      scrollback: SCROLLBACK + 1000,
      disableStdin: true,
      cursorBlink: false,
      allowProposedApi: true,
      convertEol: false,
      theme: { background: '#14161B', foreground: '#C9CED8', cursor: '#F5A524', selectionBackground: '#3a4458' },
    });
    try {
      term.loadAddon(new window.Unicode11Addon.Unicode11Addon());
      term.unicode.activeVersion = '11';
    } catch (_) { }
    term.open(hostEl);
    if (term.textarea) {
      term.textarea.setAttribute('inputmode', 'none');
      term.textarea.setAttribute('aria-label', 'Terminal input');
    }
    term.onData((d) => { if (typing) queueSend(d); });
    term.onBinary((d) => { if (typing) queueSend(d); });
    measure();
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(() => { measure(); applyFitFont(); });
    requestAnimationFrame(applyFitFont);
    showOverlay('Connecting to the pane…');
    live.attach(id, SCROLLBACK, onMessage);
    update();
  }).catch((e) => showOverlay(e.message));

  function destroy() {
    destroyed = true;
    live.detach(id);
    window.removeEventListener('resize', onResize);
    window.removeEventListener('orientationchange', onResize);
    clearTimeout(sendTimer);
    if (sendBuf) call('terminal.send', { pane: id, text: sendBuf }).catch(() => { });
    if (fitted) call('terminal.fit', { pane: id, off: true }).catch(() => { });
    if (term) { try { term.dispose(); } catch (_) { } term = null; }
  }

  return v;
}
