// REST and the live WebSocket (/api/live). See PROTOCOL.md.

export class NotPairedError extends Error {
  constructor() { super('This phone is not paired'); this.notPaired = true; }
}

const listeners = new Map();
export function on(type, fn) {
  if (!listeners.has(type)) listeners.set(type, new Set());
  listeners.get(type).add(fn);
  return () => listeners.get(type).delete(fn);
}
function emit(type, data) {
  for (const fn of listeners.get(type) || []) {
    try { fn(data); } catch (e) { console.error(e); }
  }
}

async function rest(method, path, body) {
  let res;
  try {
    res = await fetch(path, {
      method,
      credentials: 'same-origin',
      cache: 'no-store',
      headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch (_) {
    throw new Error("Can't reach Bellboard. Check that the PC is on and connected.");
  }
  let data = null;
  try { data = await res.json(); } catch (_) { }
  if (res.status === 401) { emit('unpaired'); throw new NotPairedError(); }
  if (!res.ok) {
    const err = new Error((data && data.error) || `Request failed (${res.status})`);
    err.status = res.status;
    throw err;
  }
  return data;
}

export const api = {
  health: () => rest('GET', '/api/health'),
  pair: (code, name) => rest('POST', '/api/pair', { code, name }),
  logout: () => rest('POST', '/api/logout', {}),
  state: () => rest('GET', '/api/state'),
  call: (method, params) => rest('POST', '/api/call', { method, params: params || {} }),
  pushKey: () => rest('GET', '/api/push/key'),
  pushSubscribe: (sub) => rest('POST', '/api/push/subscribe', sub),
  pushUnsubscribe: () => rest('DELETE', '/api/push/subscribe'),
};

const BACKOFF = [1000, 2000, 5000, 10000];

/** The live channel: open only while the page is visible. */
class Live {
  constructor() {
    this.ws = null;
    this.status = 'offline';           // 'online' | 'connecting' | 'offline'
    this.nextId = 1;
    this.pending = new Map();          // id → {resolve, reject, timer}
    this.attached = new Map();         // pane → {scrollback, handler}
    this.failures = 0;
    this.retryTimer = null;
    this.pingTimer = null;
    this.wanted = false;
  }

  start() {
    this.wanted = true;
    this.failures = 0;
    if (this.listening) { this.connect(); return; }
    this.listening = true;
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') this.connect();
      else this.disconnect();
    });
    window.addEventListener('online', () => this.connect());
    window.addEventListener('pageshow', () => this.connect());
    this.connect();
  }

  stop() {
    this.wanted = false;
    this.disconnect();
  }

  setStatus(s) {
    if (this.status === s) return;
    this.status = s;
    emit('status', s);
  }

  connect() {
    if (!this.wanted || document.visibilityState !== 'visible') return;
    if (this.ws && (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)) return;
    clearTimeout(this.retryTimer);
    this.setStatus('connecting');
    const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/api/live`);
    this.ws = ws;
    ws.onopen = () => {
      if (this.ws !== ws) return;
      this.failures = 0;
      this.setStatus('online');
      this.raw({ t: 'hello', v: 1 });
      this.raw({ t: 'visible', on: true });
      for (const [pane, a] of this.attached) this.raw({ t: 'attach', pane, scrollback: a.scrollback });
      clearInterval(this.pingTimer);
      this.pingTimer = setInterval(() => this.raw({ t: 'ping' }), 25000);
    };
    ws.onmessage = (ev) => {
      let m;
      try { m = JSON.parse(ev.data); } catch (_) { return; }
      this.dispatch(m);
    };
    ws.onclose = () => {
      if (this.ws !== ws) return;
      this.ws = null;
      clearInterval(this.pingTimer);
      for (const [, p] of this.pending) { clearTimeout(p.timer); p.reject(new Error('Connection lost; try again.')); }
      this.pending.clear();
      this.setStatus('offline');
      this.scheduleRetry();
    };
    ws.onerror = () => { /* onclose follows */ };
  }

  async scheduleRetry() {
    if (!this.wanted || document.visibilityState !== 'visible') return;
    const delay = BACKOFF[Math.min(this.failures, BACKOFF.length - 1)];
    this.failures++;
    // A socket that keeps failing may mean this phone was unpaired: ask once in a while.
    if (this.failures === 2 || this.failures % 4 === 0) {
      try {
        const hlt = await api.health();
        if (hlt && hlt.paired === false) { this.wanted = false; emit('unpaired'); return; }
      } catch (_) { }
    }
    this.retryTimer = setTimeout(() => this.connect(), delay);
  }

  disconnect() {
    clearTimeout(this.retryTimer);
    clearInterval(this.pingTimer);
    const ws = this.ws;
    this.ws = null;
    if (ws) {
      try { if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ t: 'visible', on: false })); } catch (_) { }
      try { ws.close(1000); } catch (_) { }
    }
    for (const [, p] of this.pending) { clearTimeout(p.timer); p.reject(new Error('Connection closed')); }
    this.pending.clear();
    this.setStatus('offline');
  }

  raw(m) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(m));
      return true;
    }
    return false;
  }

  dispatch(m) {
    switch (m.t) {
      case 'state': { const { t, ...state } = m; emit('state', state); break; }
      case 'notice': emit('notice', m.notice); break;
      case 'result': {
        const p = this.pending.get(m.id);
        if (!p) break;
        this.pending.delete(m.id);
        clearTimeout(p.timer);
        if (m.ok) p.resolve(m.result);
        else p.reject(new Error(m.error || 'Failed'));
        break;
      }
      case 'snapshot': case 'out': case 'resize': case 'exit': case 'overflow': {
        const a = this.attached.get(m.pane);
        if (a) a.handler(m);
        break;
      }
      case 'error': emit('error', m.error); break;
      case 'revoked': this.wanted = false; emit('unpaired'); break;
      case 'pong': break;
    }
  }

  /** Call a method: over the socket when it's open, else over REST. */
  call(method, params) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      const id = this.nextId++;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('Bellboard took too long to answer')); }, 30000);
        this.pending.set(id, { resolve, reject, timer });
        this.raw({ t: 'call', id, method, params: params || {} });
      });
    }
    return api.call(method, params);
  }

  attach(pane, scrollback, handler) {
    this.attached.set(pane, { scrollback, handler });
    this.raw({ t: 'attach', pane, scrollback });
  }

  reattach(pane) {
    const a = this.attached.get(pane);
    if (a) this.raw({ t: 'attach', pane, scrollback: a.scrollback });
  }

  detach(pane) {
    if (!this.attached.delete(pane)) return;
    this.raw({ t: 'detach', pane });
  }
}

export const live = new Live();
export const call = (method, params) => live.call(method, params);
