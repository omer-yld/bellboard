// The latest State from Bellboard (PROTOCOL.md › Shapes) and lookups over it.

const subs = new Set();
export const store = {
  state: null,
  connection: 'offline',
};

// How far the PC's clock is ahead of this device's: "4m ago" is measured on the PC's clock.
export const clock = { skew: 0 };

export function setState(state) {
  store.state = state;
  const now = state && Date.parse(state.now);
  if (now && !Number.isNaN(now)) clock.skew = now - Date.now();
  for (const fn of subs) {
    try { fn(state); } catch (e) { console.error(e); }
  }
}

export function setConnection(status) {
  store.connection = status;
  for (const fn of subs) {
    try { fn(store.state); } catch (e) { console.error(e); }
  }
}

export function subscribe(fn) { subs.add(fn); return () => subs.delete(fn); }

const RANK = { view: 0, respond: 1, full: 2 };
export function level() { return (store.state && store.state.device && store.state.device.level) || 'view'; }
export function can(min) { return RANK[level()] >= RANK[min]; }

export function workspaces() { return (store.state && store.state.workspaces) || []; }
export function inbox() { return (store.state && store.state.inbox) || []; }
export function notifications() { return (store.state && store.state.notifications) || []; }

export function findWorkspace(id) { return workspaces().find((w) => w.id === id) || null; }

/** → {pane, ws} or null */
export function findPane(id) {
  for (const ws of workspaces()) {
    const pane = (ws.panes || []).find((p) => p.id === id);
    if (pane) return { pane, ws };
  }
  return null;
}

export function inboxRow(paneId) { return inbox().find((r) => r.pane === paneId) || null; }

/** Whether this device may type into the pane: respond → agent panes only, full → any terminal. */
export function canType(pane, ws) {
  if (!pane || pane.kind !== 'terminal' || pane.feed) return false;
  if (can('full')) return true;
  // Respond may type only while an agent runs in the pane (Bellboard checks the same).
  return can('respond') && !!pane.agent;
}

export function isAgentPane(pane, ws) {
  return !!(pane && pane.kind === 'terminal' && !pane.feed && (pane.agent || (ws && ws.agentTask)));
}

export function waitingCount() { return inbox().filter((r) => r.status === 'waiting').length; }
