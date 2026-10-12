// Bellboard terminal page: xterm.js with the WebGL renderer, bridged to a ConPTY in the host over WebView2 messaging.
(() => {
  'use strict';
  const host = window.chrome && window.chrome.webview;
  const post = (m) => { if (host) host.postMessage(m); };
  const el = document.getElementById('term');
  let term, fit, serializer, search, webgl = null;
  const perf = { script: Math.round(performance.now()) };

  // Keep in sync with MainWindow.HandleShortcut.
  const CTRL_SHIFT = new Set(['Space', 'KeyT', 'KeyW', 'KeyD', 'KeyE', 'KeyL', 'KeyU', 'KeyI', 'KeyR', 'KeyB', 'KeyF', 'KeyP', 'KeyN', 'KeyA', 'KeyZ',
    'Tab', 'Enter', 'Equal', 'BracketLeft', 'BracketRight', 'PageUp', 'PageDown']);
  function isAppShortcut(e) {
    const c = e.code;
    if (e.ctrlKey && e.shiftKey && !e.altKey && CTRL_SHIFT.has(c)) return true;
    if (e.ctrlKey && !e.shiftKey && !e.altKey && (c === 'Tab' || c === 'Comma' || /^Digit[1-9]$/.test(c))) return true;
    if (e.ctrlKey && e.altKey && c.startsWith('Arrow')) return true;           // focus / resize panes
    return false;
  }

  function onKey(e) {
    if (isAppShortcut(e)) {
      if (e.type === 'keydown') {
        e.preventDefault();
        post({ t: 'key', code: e.code, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey });
      }
      return false;
    }
    if (e.type !== 'keydown') return true;
    const plainCtrl = e.ctrlKey && !e.altKey && !e.metaKey;
    // Ctrl+C copies when there is a selection, otherwise it is ^C.
    if (plainCtrl && e.code === 'KeyC' && (e.shiftKey || term.hasSelection())) {
      if (term.hasSelection()) { post({ t: 'copy', d: term.getSelection() }); term.clearSelection(); }
      e.preventDefault();
      return false;
    }
    if (plainCtrl && e.code === 'KeyV') {
      e.preventDefault();
      post({ t: 'paste-request' });
      return false;
    }
    if (plainCtrl && !e.shiftKey && (e.key === '=' || e.key === '+' || e.key === '-' || e.key === '0')) {
      const base = window.__bellboardFontSize || 14;
      term.options.fontSize = e.key === '0' ? base : Math.max(8, Math.min(32, term.options.fontSize + (e.key === '-' ? -1 : 1)));
      safeFit();
      e.preventDefault();
      return false;
    }
    return true;
  }

  // Settings changed while running: update the live terminal.
  function applyConfig(cfg) {
    window.__bellboardFontSize = cfg.fontSize;
    document.body.style.setProperty('--bg', cfg.theme.background);
    term.options.fontFamily = cfg.fontFamily;
    term.options.fontSize = cfg.fontSize;
    term.options.lineHeight = cfg.lineHeight;
    term.options.cursorStyle = cfg.cursorStyle || 'block';
    term.options.cursorBlink = cfg.cursorBlink !== false;
    term.options.scrollback = cfg.scrollback;
    term.options.theme = cfg.theme;
    safeFit();
    remeasureWhenFontsLoad(cfg);
  }

  // A bundled web font may finish loading after xterm measured its cells: measure again once it's there.
  function remeasureWhenFontsLoad(cfg) {
    if (!document.fonts || !/JetBrains Mono/i.test(cfg.fontFamily)) return;
    document.fonts.load(`${cfg.fontSize}px "JetBrains Mono"`).then(() => {
      const family = term.options.fontFamily;
      term.options.fontFamily = 'monospace';
      term.options.fontFamily = family;
      safeFit();
    }).catch(() => { });
  }

  function safeFit() {
    try { if (el.clientWidth > 0 && el.clientHeight > 0) fit.fit(); } catch (_) { }
  }

  function create(cfg) {
    perf.config = Math.round(performance.now());
    window.__bellboardFontSize = cfg.fontSize;
    document.body.style.setProperty('--bg', cfg.theme.background);
    term = new Terminal({
      allowProposedApi: true,
      fontFamily: cfg.fontFamily,
      fontSize: cfg.fontSize,
      lineHeight: cfg.lineHeight,
      scrollback: cfg.scrollback,
      theme: cfg.theme,
      cursorStyle: cfg.cursorStyle || 'block',
      cursorBlink: cfg.cursorBlink !== false,
      drawBoldTextInBrightColors: true,
      rightClickSelectsWord: false,
      fastScrollModifier: 'alt',
      windowsPty: { backend: 'conpty', buildNumber: cfg.buildNumber },
    });
    fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    serializer = new SerializeAddon.SerializeAddon();
    term.loadAddon(serializer);
    term.loadAddon(new Unicode11Addon.Unicode11Addon());
    term.unicode.activeVersion = '11';
    // Click opens links in the Bellboard browser pane; Shift+click opens the system browser.
    term.loadAddon(new WebLinksAddon.WebLinksAddon((ev, uri) => post({ t: 'link', url: uri, external: !!ev.shiftKey })));
    search = new SearchAddon.SearchAddon();
    term.loadAddon(search);
    search.onDidChangeResults(({ resultIndex, resultCount }) => showCount(resultIndex, resultCount));
    term.open(el);
    remeasureWhenFontsLoad(cfg);
    perf.opened = Math.round(performance.now());
    try {
      webgl = new WebglAddon.WebglAddon();
      webgl.onContextLoss(() => { webgl.dispose(); webgl = null; });
      term.loadAddon(webgl);
    } catch (_) { webgl = null; }
    perf.webgl = Math.round(performance.now());

    term.onData((d) => post({ t: 'i', d }));
    term.onBinary((d) => post({ t: 'i', d, bin: true }));
    term.onResize(({ cols, rows }) => post({ t: 'r', cols, rows }));
    term.attachCustomKeyEventHandler(onKey);
    term.textarea.addEventListener('focus', () => post({ t: 'focus' }));
    el.addEventListener('mousedown', () => post({ t: 'focus' }));
    // Right click: copy the selection, or paste (Windows Terminal behaviour).
    el.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      if (term.hasSelection()) { post({ t: 'copy', d: term.getSelection() }); term.clearSelection(); }
      else post({ t: 'paste-request' });
    });
    new ResizeObserver(() => safeFit()).observe(el);
    safeFit();
    perf.ready = Math.round(performance.now());
    post({ t: 'ready', cols: term.cols, rows: term.rows, renderer: webgl ? 'webgl' : 'dom', perf });
  }

  if (host) {
    host.addEventListener('message', (ev) => {
      const m = ev.data;
      switch (m.t) {
        case 'config':
          if (!term) create(m);
          else applyConfig(m);
          break;
        case 'o':
          term.write(m.d);
          break;
        case 'paste':
          term.paste(m.d);
          term.focus();
          break;
        case 'focus':
          if (term && findBar.hidden) term.focus();
          break;
        case 'find':
          openFind();
          break;
      }
    });
  }

  // ---- Find bar (Ctrl+Shift+F) ----
  const findBar = document.getElementById('find');
  const findInput = document.getElementById('find-q');
  const findCount = document.getElementById('find-count');
  const opts = { caseSensitive: false, wholeWord: false, regex: false };
  const decorations = {
    matchBackground: '#3a4458', matchBorder: '#3a4458', matchOverviewRuler: '#8a909b',
    activeMatchBackground: '#4c8dff', activeMatchBorder: '#8fb6ff', activeMatchColorOverviewRuler: '#4c8dff',
  };
  const searchOptions = (incremental) => ({ ...opts, incremental, decorations });

  function showCount(index, count) {
    if (!findInput.value) { findCount.textContent = ''; findCount.classList.remove('none'); return; }
    findCount.textContent = count === 0 ? 'No results' : (index >= 0 ? `${index + 1} of ${count}` : `${count} found`);
    findCount.classList.toggle('none', count === 0);
  }
  function runFind(direction, incremental) {
    const q = findInput.value;
    if (!q) { search.clearDecorations(); showCount(-1, 0); return; }
    try {
      if (direction < 0) search.findPrevious(q, searchOptions(false));
      else search.findNext(q, searchOptions(incremental));
    } catch (_) { findCount.textContent = 'Invalid regex'; findCount.classList.add('none'); }
  }
  function openFind() {
    if (!term) return;
    findBar.hidden = false;
    const sel = term.getSelection();
    if (sel && !sel.includes('\n')) findInput.value = sel;
    findInput.focus();
    findInput.select();
    if (findInput.value) runFind(1, true);
  }
  function closeFind() {
    findBar.hidden = true;
    search.clearDecorations();
    term.focus();
  }
  function toggle(id, key) {
    opts[key] = !opts[key];
    document.getElementById(id).classList.toggle('on', opts[key]);
    runFind(1, true);
  }
  findInput.addEventListener('input', () => runFind(1, true));
  findInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); runFind(e.shiftKey ? -1 : 1, false); }
    else if (e.key === 'Escape') { e.preventDefault(); closeFind(); }
    else if (e.key === 'F3') { e.preventDefault(); runFind(e.shiftKey ? -1 : 1, false); }
    else if (e.altKey && e.code === 'KeyC') { e.preventDefault(); toggle('find-case', 'caseSensitive'); }
    else if (e.altKey && e.code === 'KeyW') { e.preventDefault(); toggle('find-word', 'wholeWord'); }
    else if (e.altKey && e.code === 'KeyR') { e.preventDefault(); toggle('find-regex', 'regex'); }
  });
  document.getElementById('find-next').onclick = () => runFind(1, false);
  document.getElementById('find-prev').onclick = () => runFind(-1, false);
  document.getElementById('find-close').onclick = closeFind;
  document.getElementById('find-case').onclick = () => toggle('find-case', 'caseSensitive');
  document.getElementById('find-word').onclick = () => toggle('find-word', 'wholeWord');
  document.getElementById('find-regex').onclick = () => toggle('find-regex', 'regex');
  findBar.addEventListener('mousedown', () => post({ t: 'focus' }));

  // Called by the host through ExecuteScriptAsync.
  window.bellboard = {
    /** Unfocused panes in a split fade back so the focused one stands out. */
    setDim(on) {
      document.getElementById('term').style.opacity = on ? '0.62' : '';
    },
    read(lines) {
      const b = term.buffer.active;
      const out = [];
      let start, end;
      if (lines > 0) { end = b.baseY + b.cursorY + 1; start = Math.max(0, end - lines); }
      else { start = b.viewportY; end = b.viewportY + term.rows; }
      for (let i = start; i < end; i++) {
        const line = b.getLine(i);
        out.push(line ? line.translateToString(true) : '');
      }
      while (out.length && out[out.length - 1] === '') out.pop();
      return out.join('\n');
    },
    serialize(rows, includeAlt) {
      try { return serializer.serialize({ scrollback: rows, excludeAltBuffer: !includeAlt, excludeModes: !includeAlt }); }
      catch (_) { return null; }
    },
    info() { return { cols: term.cols, rows: term.rows, renderer: webgl ? 'webgl' : 'dom' }; },
  };

  post({ t: 'hello' });
})();
