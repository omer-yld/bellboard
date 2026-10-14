#!/usr/bin/env node
// A stand-in for bellboard-remote.exe, for working on the phone app without Windows.
// Implements the REST + WebSocket contract in src/Bellboard.Remote/PROTOCOL.md with fake data.
//   node tests/remote-mock/mock.mjs [--port 8787] [--level view|respond|full] [--host 0.0.0.0]
// Pairing code: ABCD-EFGH. No npm dependencies (Node 18+).
import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const WWW = path.join(repo, 'src', 'Bellboard.Remote', 'wwwroot');
const VENDOR = path.join(repo, 'src', 'Bellboard.App', 'web', 'vendor');

const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf('--' + name); return i >= 0 && args[i + 1] ? args[i + 1] : dflt; };
const PORT = Number(opt('port', 8787));
const HOST = opt('host', '127.0.0.1');
const LEVEL = opt('level', 'full');
const PAIR_CODE = 'ABCDEFGH';
const VERSION = '0.5.0-mock';

// ---------------------------------------------------------------- fake Bellboard state
const minutesAgo = (m) => new Date(Date.now() - m * 60000).toISOString();
const devices = new Map();       // token → {id, name, level}
let pairFailures = 0;
let dnd = false;
let nextNotice = 10;
let nextPrompt = 5;

const workspaces = [
  {
    id: 'w1', index: 1, name: 'login-fix', selected: true, cwd: 'C:\\code\\shop.worktrees\\fix-the-flaky-login-test', branch: 'bellboard/fix-the-flaky-login-test',
    ports: [5173], unread: 2, notice: 'Claude needs your permission to use Bash', muted: false, pinned: true, badge: 'PS',
    status: 'Claude Code · needs input',
    agentTask: { task: 'fix the flaky login test', agent: 'claude', branch: 'bellboard/fix-the-flaky-login-test', base: 'main' },
    pills: [{ key: 'tests', text: 'tests: 2 failing', color: 'red' }], progress: null,
    logs: [{ at: minutesAgo(9), level: 'info', text: 'Explore subagent finished: found 3 login tests' }, { at: minutesAgo(3), level: 'warning', text: 'retrying flaky test 3/5' }],
    panes: [{ id: 'p1', kind: 'terminal', title: '✳ Fix flaky login test', cwd: 'C:\\code\\shop.worktrees\\fix-the-flaky-login-test', focused: true, active: true, attention: true, agent: { kind: 'claude', status: 'waiting' }, feed: false, shell: 'pwsh', size: '120x32', branch: 'bellboard/fix-the-flaky-login-test' }],
  },
  {
    id: 'w2', index: 2, name: 'changelog', selected: false, cwd: '\\\\wsl.localhost\\Ubuntu\\home\\me\\api.worktrees\\write-the-changelog', branch: 'bellboard/write-the-changelog',
    ports: [], unread: 0, notice: null, muted: false, pinned: false, badge: 'WSL',
    status: 'Codex · needs input',
    agentTask: { task: 'write the changelog for 2.3', agent: 'codex', branch: 'bellboard/write-the-changelog', base: 'main' },
    pills: [], progress: null, logs: [],
    panes: [{ id: 'p2', kind: 'terminal', title: 'codex', cwd: '/home/me/api.worktrees/write-the-changelog', focused: true, active: true, attention: true, agent: { kind: 'codex', status: 'waiting' }, feed: false, shell: 'wsl', size: '100x30', branch: 'bellboard/write-the-changelog' }],
  },
  {
    id: 'w3', index: 3, name: 'retries', selected: false, cwd: 'C:\\code\\client.worktrees\\add-retries', branch: 'bellboard/add-retries',
    ports: [], unread: 0, notice: null, muted: false, pinned: false, badge: 'PS',
    status: 'Pi · working',
    agentTask: { task: 'add retries to the HTTP client with jittered backoff', agent: 'pi', branch: 'bellboard/add-retries', base: 'develop' },
    pills: [{ key: 'build', text: 'building', color: 'blue' }], progress: { value: 0.42, label: 'migrating' },
    logs: [{ at: minutesAgo(1), level: 'progress', text: 'running unit tests' }],
    panes: [{ id: 'p3', kind: 'terminal', title: 'pi', cwd: 'C:\\code\\client.worktrees\\add-retries', focused: true, active: true, attention: false, agent: { kind: 'pi', status: 'working' }, feed: false, shell: 'pwsh', size: '120x32', branch: 'bellboard/add-retries' }],
  },
  {
    id: 'w4', index: 4, name: 'Nightly build', selected: false, cwd: null, branch: null,
    ports: [], unread: 0, notice: null, muted: true, pinned: false, badge: 'CI',
    status: 'feed · done', agentTask: null, pills: [{ key: 'ci', text: 'all green', color: 'green' }], progress: null,
    logs: [{ at: minutesAgo(40), level: 'success', text: 'all 128 tests passed' }],
    panes: [{ id: 'p4', kind: 'terminal', title: 'Nightly build', cwd: null, focused: true, active: true, attention: false, agent: { kind: 'feed', status: 'done' }, feed: true, shell: 'feed', size: '100x30', branch: null }],
  },
  {
    id: 'w5', index: 5, name: 'api', selected: false, cwd: 'C:\\code\\api', branch: 'main',
    ports: [3000, 9229], unread: 0, notice: null, muted: false, pinned: false, badge: 'PS',
    status: null, agentTask: null, pills: [], progress: null, logs: [],
    panes: [
      { id: 'p5', kind: 'terminal', title: 'npm run dev', cwd: 'C:\\code\\api', focused: true, active: true, attention: false, agent: null, feed: false, shell: 'pwsh', size: '90x28', branch: 'main' },
      { id: 'p6', kind: 'browser', title: 'localhost:3000', cwd: null, focused: false, active: true, attention: false, agent: null, feed: false, shell: null, size: null, branch: null },
    ],
  },
];

const inbox = new Map([
  ['p1', { pane: 'p1', workspace: 'w1', workspaceName: 'login-fix', title: 'fix the flaky login test', status: 'waiting', since: minutesAgo(4), message: 'Claude needs your permission to use Bash', where: 'Claude Code · login-fix · ⎇ bellboard/fix-the-flaky-login-test · p1', agent: 'Claude Code', feed: false,
    prompt: { id: 'q1', tool: 'Bash', summary: 'npm test -- --runInBand tests/login.spec.ts', detail: 'Run the login tests serially to find the flaky one\n\ncwd: C:\\code\\shop.worktrees\\fix-the-flaky-login-test', canAlwaysAllow: true, since: minutesAgo(4) } }],
  ['p2', { pane: 'p2', workspace: 'w2', workspaceName: 'changelog', title: 'write the changelog for 2.3', status: 'waiting', since: minutesAgo(12), message: 'Allow Codex to edit CHANGELOG.md?', where: 'Codex · changelog · ⎇ bellboard/write-the-changelog · p2', agent: 'Codex', feed: false, prompt: null }],
  ['p3', { pane: 'p3', workspace: 'w3', workspaceName: 'retries', title: 'add retries to the HTTP client with jittered backoff', status: 'working', since: minutesAgo(7), message: 'Implement retries with exponential backoff and jitter', where: 'Pi · retries · ⎇ bellboard/add-retries · p3', agent: 'Pi', feed: false, prompt: null }],
  ['p4', { pane: 'p4', workspace: 'w4', workspaceName: 'Nightly build', title: 'Nightly build', status: 'done', since: minutesAgo(40), message: 'all green', where: 'feed · Nightly build · p4', agent: null, feed: true, prompt: null }],
]);

const notices = [
  { id: 'n3', at: minutesAgo(4), workspace: 'w1', workspaceName: 'login-fix', pane: 'p1', title: 'Claude Code', body: 'Claude needs your permission to use Bash', source: 'claude', read: false },
  { id: 'n2', at: minutesAgo(12), workspace: 'w2', workspaceName: 'changelog', pane: 'p2', title: 'Codex', body: 'Allow Codex to edit CHANGELOG.md?', source: 'codex', read: false },
  { id: 'n1', at: minutesAgo(40), workspace: 'w4', workspaceName: 'Nightly build', pane: 'p4', title: 'Nightly build', body: 'all 128 tests passed', source: 'feed', read: true },
];

// Fake screens for agent.prompt / terminal.read.
const screens = {
  p1: () => '● Running the login tests serially…\n\n Bash command\n\n   npm test -- --runInBand tests/login.spec.ts\n   Run the login tests serially to find the flaky one\n\n Do you want to proceed?\n ❯ 1. Yes\n   2. Yes, and don\'t ask again for npm test commands in C:\\code\\shop\n   3. No, and tell Claude what to do differently (esc)',
  p2: () => '• Proposed change to CHANGELOG.md (+24 -0)\n\n  Allow Codex to edit CHANGELOG.md?\n\n› 1. Yes, proceed (y)\n  2. Yes, and don\'t ask again for these files (a)\n  3. No, and tell Codex what to do differently (esc)',
  p3: () => 'pi › add retries to the HTTP client\n  ✓ read src/client.ts\n  ✓ edit src/client.ts (+38 -4)\n  … running npm test',
  p4: () => 'step 7/7: tests\nall 128 tests passed',
  p5: () => '> api@1.4.0 dev\n> node --watch server.js\n\nlistening on http://localhost:3000',
};
const sizes = { p1: [120, 32], p2: [100, 30], p3: [120, 32], p4: [100, 30], p5: [90, 28] };
const hashOf = (s) => crypto.createHash('sha256').update(s).digest('hex').slice(0, 16);

function unread() { return notices.filter((n) => !n.read).length; }
function findPane(id) {
  for (const ws of workspaces) { const p = ws.panes.find((x) => x.id === id); if (p) return { pane: p, ws }; }
  return null;
}
function stateFor(dev) {
  return {
    version: VERSION, device: { id: dev.id, name: dev.name, level: dev.level }, dnd, unread: unread(),
    workspaces, inbox: sortInbox(), notifications: notices.slice(0, 50),
  };
}
function sortInbox() {
  const rank = { waiting: 0, working: 1, running: 1, done: 2, idle: 3 };
  return [...inbox.values()].sort((a, b) => (rank[a.status] - rank[b.status]) || (Date.parse(a.since) - Date.parse(b.since)));
}
function setAgent(paneId, status, message) {
  const row = inbox.get(paneId);
  const f = findPane(paneId);
  if (row) { row.status = status; row.since = new Date().toISOString(); if (message !== undefined) row.message = message; }
  if (f && f.pane.agent) { f.pane.agent.status = status; f.pane.attention = status === 'waiting'; }
}

// ---------------------------------------------------------------- methods + permission levels
const RANK = { view: 0, respond: 1, full: 2 };
const LEVELS = {
  'workspace.list': 'view', 'agent.inbox': 'view', 'agent.info': 'view', 'notification.list': 'view', 'log.list': 'view',
  'terminal.read': 'view', 'agent.prompt': 'view',
  'agent.answer': 'respond', 'terminal.send': 'respond', 'terminal.send-key': 'respond', 'notification.read': 'respond',
  'notification.clear': 'respond', 'notification.dnd': 'respond', 'workspace.mute': 'respond',
  'agent.recent-repos': 'full', 'agent.new': 'full', 'agent.finish': 'full', 'terminal.fit': 'full',
};
class ApiError extends Error { constructor(status, msg) { super(msg); this.status = status; } }

function typable(dev, paneId) {
  const f = findPane(paneId);
  if (!f || f.pane.kind !== 'terminal') throw new ApiError(400, `no terminal pane '${paneId}'`);
  if (f.pane.feed) throw new ApiError(400, `${paneId} is a feed: it has no shell to type into`);
  if (dev.level !== 'full' && !f.pane.agent && !f.ws.agentTask) throw new ApiError(403, 'needs full access');
  return f;
}

function callMethod(dev, method, p = {}) {
  const need = LEVELS[method];
  if (!need) throw new ApiError(400, `unknown method '${method}'`);
  if (RANK[dev.level] < RANK[need]) throw new ApiError(403, `needs ${need} access`);
  switch (method) {
    case 'workspace.list': return { selected: 'w1', workspaces };
    case 'agent.inbox': return sortInbox();
    case 'notification.list': return notices;
    case 'log.list': return (workspaces.find((w) => w.id === p.workspace) || { logs: [] }).logs;
    case 'agent.info': {
      const ws = workspaces.find((w) => w.id === p.workspace);
      if (!ws || !ws.agentTask) throw new ApiError(400, `${p.workspace} isn't an agent workspace`);
      return { workspace: ws.id, task: ws.agentTask.task, branch: ws.agentTask.branch, base: ws.agentTask.base, worktree: ws.cwd,
        ahead: ws.id === 'w1' ? 2 : 0, diffstat: ws.id === 'w1' ? '3 files changed, 41 insertions(+), 9 deletions(-)' : '',
        uncommitted: ws.id === 'w1' ? ['tests/login.spec.ts', 'src/auth/session.ts'] : [], repoBranch: 'main', repoDirty: false, gh: true, origin: true };
    }
    case 'terminal.read': {
      if (!findPane(p.pane)) throw new ApiError(400, `no pane '${p.pane}'`);
      return { pane: p.pane, text: (screens[p.pane] || (() => ''))() };
    }
    case 'agent.prompt': {
      const f = findPane(p.pane);
      if (!f) throw new ApiError(400, `no pane '${p.pane}'`);
      const text = (screens[p.pane] || (() => ''))();
      const row = inbox.get(p.pane);
      const choices = [];
      let question = null;
      if (row && row.status === 'waiting') {
        for (const line of text.split('\n')) {
          const m = line.match(/^\s*([❯›>])?\s*(\d)\.\s+(.*)$/);
          if (m) choices.push({ key: m[2], label: m[3].trim(), selected: !!m[1] });
          else if (/\?\s*$/.test(line.trim())) question = line.trim();
        }
      }
      return { pane: p.pane, hash: hashOf(text), text, question, choices, pending: row ? row.prompt : null };
    }
    case 'agent.answer': {
      const row = inbox.get(p.pane);
      if (p.promptId) {
        if (!row || !row.prompt || row.prompt.id !== p.promptId) throw new ApiError(409, 'that prompt was already answered');
        row.prompt = null;
        setAgent(p.pane, p.decision === 'deny' ? 'working' : 'working', p.decision === 'deny' ? 'Denied from the phone' : `Running: npm test`);
        streamLine(p.pane, `\x1b[32m✓ ${p.decision} from phone\x1b[0m`);
        schedulePromptAgain(p.pane);
        broadcastState();
        return { answered: true };
      }
      if (p.choice) {
        const text = (screens[p.pane] || (() => ''))();
        if (p.hash !== hashOf(text)) throw new ApiError(409, 'the screen changed; look again');
        typable(dev, p.pane);
        setAgent(p.pane, 'working', `Chose ${p.choice}`);
        streamLine(p.pane, `chose ${p.choice}`);
        broadcastState();
        return { answered: true };
      }
      if (typeof p.text === 'string') {
        typable(dev, p.pane);
        streamLine(p.pane, '> ' + p.text);
        if (row && row.status !== 'working') { setAgent(p.pane, 'working', p.text.slice(0, 160)); broadcastState(); }
        return { pane: p.pane, bytes: p.text.length + (p.enter ? 1 : 0) };
      }
      throw new ApiError(400, 'nothing to answer with');
    }
    case 'terminal.send': {
      typable(dev, p.pane);
      if (String(p.text || '').length > 4096) throw new ApiError(400, 'text is longer than 4096 characters');
      echo(p.pane, String(p.text || '') + (p.enter ? '\r\n' : ''));
      return { pane: p.pane, bytes: String(p.text || '').length };
    }
    case 'terminal.send-key': {
      typable(dev, p.pane);
      if (!Array.isArray(p.keys) || !p.keys.length) throw new ApiError(400, 'no keys given');
      echo(p.pane, `\x1b[2m[${p.keys.join(' ')}]\x1b[0m` + (p.keys.includes('enter') ? '\r\n' : ''));
      return { pane: p.pane, keys: p.keys.length };
    }
    case 'notification.read': {
      for (const n of notices) if (n.id === p.id || n.pane === p.pane) n.read = true;
      broadcastState();
      return true;
    }
    case 'notification.clear': for (const n of notices) n.read = true; broadcastState(); return true;
    case 'notification.dnd': dnd = typeof p.on === 'boolean' ? p.on : !dnd; broadcastState(); return { doNotDisturb: dnd };
    case 'workspace.mute': {
      const ws = workspaces.find((w) => w.id === p.workspace);
      if (!ws) throw new ApiError(400, `no workspace '${p.workspace}'`);
      ws.muted = typeof p.on === 'boolean' ? p.on : !ws.muted;
      broadcastState();
      return { workspace: ws.id, muted: ws.muted };
    }
    case 'agent.recent-repos': return [
      { path: 'C:\\code\\shop', name: 'shop' }, { path: 'C:\\code\\client', name: 'client' },
      { path: '\\\\wsl.localhost\\Ubuntu\\home\\me\\api', name: 'api' },
    ];
    case 'agent.new': {
      if (!p.task) throw new ApiError(400, 'a task is required');
      const n = workspaces.length + 1;
      const slug = String(p.task).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 40);
      const branch = 'bellboard/' + slug;
      const id = 'w' + (n + 10), pane = 'p' + (n + 10);
      const kind = p.agent || 'claude';
      const label = { claude: 'Claude Code', codex: 'Codex', opencode: 'OpenCode', pi: 'Pi' }[kind] || kind;
      workspaces.push({ id, index: n, name: slug.slice(0, 18), selected: false, cwd: String(p.repo) + '.worktrees\\' + slug, branch, ports: [], unread: 0, notice: null, muted: false, pinned: false, badge: 'PS',
        status: `${label} · working`, agentTask: { task: p.task, agent: kind, branch, base: p.base || 'main' }, pills: [], progress: null, logs: [],
        panes: [{ id: pane, kind: 'terminal', title: kind, cwd: null, focused: true, active: true, attention: false, agent: { kind, status: 'working' }, feed: false, shell: 'pwsh', size: '120x32', branch }] });
      inbox.set(pane, { pane, workspace: id, workspaceName: slug.slice(0, 18), title: p.task, status: 'working', since: new Date().toISOString(), message: p.task, where: `${label} · ${slug}`, agent: label, feed: false, prompt: null });
      screens[pane] = () => `${kind} › ${p.task}\n  … thinking`;
      sizes[pane] = [120, 32];
      broadcastState();
      return { workspace: id, pane, branch, worktree: String(p.repo) + '.worktrees\\' + slug, base: p.base || 'main' };
    }
    case 'agent.finish': {
      const i = workspaces.findIndex((w) => w.id === p.workspace);
      if (i < 0 || !workspaces[i].agentTask) throw new ApiError(400, `${p.workspace} isn't an agent workspace`);
      const ws = workspaces[i];
      if (p.mode === 'discard' && ws.id === 'w1' && !p.force) throw new ApiError(400, `${ws.agentTask.branch} has 2 commits that aren't merged; pass force to discard them`);
      workspaces.splice(i, 1);
      for (const pn of ws.panes) { inbox.delete(pn.id); endAttachments(pn.id); }
      broadcastState();
      const msg = { merge: `Merged ${ws.agentTask.branch} into ${ws.agentTask.base}`, pr: 'Opened https://github.com/me/shop/pull/42', keep: `Kept ${ws.agentTask.branch}`, discard: `Discarded ${ws.agentTask.branch}` }[p.mode];
      if (!msg) throw new ApiError(400, 'mode must be merge, pr, keep or discard');
      return { result: msg };
    }
    case 'terminal.fit': {
      const f = typable(dev, p.pane);
      const orig = sizes[p.pane] || [100, 30];
      if (p.off) {
        f.pane.size = orig.join('x');
        sendResize(p.pane, orig[0], orig[1]);
        return { pane: p.pane, cols: orig[0], rows: orig[1], fitted: false };
      }
      const cols = Math.max(20, Math.min(300, Number(p.cols) || 0)), rows = Math.max(5, Math.min(200, Number(p.rows) || 0));
      f.pane.size = `${cols}x${rows}`;
      sendResize(p.pane, cols, rows);
      return { pane: p.pane, cols, rows, fitted: true };
    }
  }
  throw new ApiError(400, `unknown method '${method}'`);
}

function schedulePromptAgain(paneId) {
  // Bring a new prompt back a little later so the flow can be tried again.
  setTimeout(() => {
    const row = inbox.get(paneId);
    if (!row) return;
    row.prompt = { id: 'q' + (nextPrompt++), tool: 'Edit', summary: 'src/auth/session.ts', detail: '@@ -12,7 +12,9 @@\n-  await page.click(login)\n+  await page.waitForSelector(login)\n+  await page.click(login)', canAlwaysAllow: false, since: new Date().toISOString() };
    setAgent(paneId, 'waiting', 'Claude wants to edit src/auth/session.ts');
    postNotice(paneId, 'Claude Code', 'Claude wants to edit src/auth/session.ts');
  }, 20000);
}

function postNotice(paneId, title, body) {
  const f = findPane(paneId);
  const n = { id: 'n' + (nextNotice++), at: new Date().toISOString(), workspace: f ? f.ws.id : '', workspaceName: f ? f.ws.name : '', pane: paneId, title, body, source: 'claude', read: false };
  notices.unshift(n);
  for (const c of clients) c.send({ t: 'notice', notice: n });
  broadcastState();
}

// ---------------------------------------------------------------- fake terminals
const seqs = new Map();          // pane → last seq sent
function attachments(paneId) { return [...clients].filter((c) => c.attached.has(paneId)); }
function sendOut(paneId, d) {
  const seq = (seqs.get(paneId) || 0) + 1;
  seqs.set(paneId, seq);
  for (const c of attachments(paneId)) c.send({ t: 'out', pane: paneId, d, seq });
}
function streamLine(paneId, text) { sendOut(paneId, text + '\r\n'); }
function echo(paneId, text) { sendOut(paneId, text); }
function sendResize(paneId, cols, rows) { for (const c of attachments(paneId)) c.send({ t: 'resize', pane: paneId, cols, rows }); }
function endAttachments(paneId) { for (const c of attachments(paneId)) { c.send({ t: 'exit', pane: paneId }); c.attached.delete(paneId); } }
function snapshot(paneId) {
  const lines = [];
  for (let i = 0; i < 40; i++) lines.push(`\x1b[2m${String(i + 1).padStart(3)}\x1b[0m history line ${i + 1} of ${paneId} \x1b[36m${'─'.repeat(20)}\x1b[0m`);
  lines.push('', ...(screens[paneId] || (() => ''))().split('\n'));
  return lines.join('\r\n');
}

let tick = 0;
setInterval(() => {
  tick++;
  for (const id of ['p3', 'p5']) {
    if (!findPane(id)) continue;
    const t = new Date().toLocaleTimeString();
    sendOut(id, id === 'p3'
      ? `\x1b[34m…\x1b[0m test ${tick}: \x1b[32mok\x1b[0m retry backoff ${Math.round(Math.random() * 900)}ms  ${t}\r\n`
      : `GET /api/orders 200 ${Math.round(Math.random() * 40)}ms  ${t}\r\n`);
  }
}, 900);

// ---------------------------------------------------------------- HTTP
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.webmanifest': 'application/manifest+json', '.json': 'application/json' };
const CSP = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'";

function cookies(req) {
  const out = {};
  for (const part of String(req.headers.cookie || '').split(';')) {
    const i = part.indexOf('=');
    if (i > 0) out[part.slice(0, i).trim()] = decodeURIComponent(part.slice(i + 1).trim());
  }
  return out;
}
function device(req) { return devices.get(cookies(req).bb_device) || null; }
function json(res, status, body, extra = {}) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', ...extra });
  res.end(JSON.stringify(body));
}
function readBody(req) {
  return new Promise((resolve) => {
    let s = '';
    req.on('data', (c) => { s += c; if (s.length > 1e6) req.destroy(); });
    req.on('end', () => { try { resolve(s ? JSON.parse(s) : {}); } catch (_) { resolve(null); } });
  });
}
function sameOrigin(req) {
  const o = req.headers.origin;
  return !o || o === `http://${req.headers.host}` || o === `https://${req.headers.host}`;
}

const VAPID_PUBLIC = (() => {
  const { publicKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  const jwk = publicKey.export({ format: 'jwk' });
  const raw = Buffer.concat([Buffer.from([4]), Buffer.from(jwk.x, 'base64url'), Buffer.from(jwk.y, 'base64url')]);
  return raw.toString('base64url');
})();

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  const p = url.pathname;
  if (p.startsWith('/api/')) {
    if (req.method !== 'GET' && !sameOrigin(req)) return json(res, 403, { error: 'cross-origin request refused' });
    const dev = device(req);
    try {
      if (p === '/api/health' && req.method === 'GET') return json(res, 200, { ok: true, version: VERSION, paired: !!dev });
      if (p === '/api/pair' && req.method === 'POST') {
        const b = await readBody(req);
        if (pairFailures >= 5) return json(res, 429, { error: 'too many attempts; pairing is locked for 10 minutes' });
        const code = String((b && b.code) || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
        if (code !== PAIR_CODE) { pairFailures++; return json(res, 400, { error: 'That code is wrong or has expired. Make a new one on your PC.' }); }
        pairFailures = 0;
        const token = crypto.randomBytes(32).toString('base64url');
        const d = { id: 'd' + (devices.size + 1), name: String((b && b.name) || 'Phone').slice(0, 40), level: LEVEL };
        devices.set(token, d);
        return json(res, 200, { device: d }, { 'Set-Cookie': `bb_device=${token}; Path=/api; HttpOnly; SameSite=Strict; Max-Age=34560000` });
      }
      if (!dev) return json(res, 401, { error: 'not paired' });
      if (p === '/api/logout' && req.method === 'POST') {
        devices.delete(cookies(req).bb_device);
        return json(res, 200, { ok: true }, { 'Set-Cookie': 'bb_device=; Path=/api; HttpOnly; SameSite=Strict; Max-Age=0' });
      }
      if (p === '/api/state' && req.method === 'GET') return json(res, 200, stateFor(dev));
      if (p === '/api/call' && req.method === 'POST') {
        const b = await readBody(req);
        if (!b || typeof b.method !== 'string') return json(res, 400, { error: 'expected {method, params}' });
        return json(res, 200, callMethod(dev, b.method, b.params || {}) ?? null);
      }
      if (p === '/api/push/key' && req.method === 'GET') return json(res, 200, { publicKey: VAPID_PUBLIC });
      if (p === '/api/push/subscribe' && req.method === 'POST') { const b = await readBody(req); dev.push = b; console.log('push subscription', dev.id, b && b.endpoint); return json(res, 200, { ok: true }); }
      if (p === '/api/push/subscribe' && req.method === 'DELETE') { dev.push = null; return json(res, 200, { ok: true }); }
      return json(res, 404, { error: 'not found' });
    } catch (e) {
      return json(res, e.status || 500, { error: e.message });
    }
  }
  // Static files.
  let file;
  if (p.startsWith('/vendor/')) file = path.join(VENDOR, path.basename(p));
  else file = path.join(WWW, p === '/' ? 'index.html' : path.normalize(p).replace(/^([/\\])+/, ''));
  if (!file.startsWith(WWW) && !file.startsWith(VENDOR)) { res.writeHead(404); return res.end(); }
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404, { 'Content-Type': 'text/plain' }); return res.end('not found'); }
    res.writeHead(200, {
      'Content-Type': MIME[path.extname(file)] || 'application/octet-stream',
      'Content-Security-Policy': CSP, 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer',
      'Cache-Control': 'no-cache', ...(p === '/sw.js' ? { 'Service-Worker-Allowed': '/' } : {}),
    });
    res.end(data);
  });
});

// ---------------------------------------------------------------- WebSocket (RFC 6455, text frames only)
const clients = new Set();
class Client {
  constructor(sock, dev) {
    this.sock = sock; this.dev = dev; this.attached = new Set(); this.buf = Buffer.alloc(0); this.visible = true;
    sock.on('data', (d) => this.onData(d));
    sock.on('close', () => clients.delete(this));
    sock.on('error', () => clients.delete(this));
  }
  send(obj) {
    if (this.sock.destroyed) return;
    const payload = Buffer.from(JSON.stringify(obj));
    let head;
    if (payload.length < 126) head = Buffer.from([0x81, payload.length]);
    else if (payload.length < 65536) { head = Buffer.alloc(4); head[0] = 0x81; head[1] = 126; head.writeUInt16BE(payload.length, 2); }
    else { head = Buffer.alloc(10); head[0] = 0x81; head[1] = 127; head.writeBigUInt64BE(BigInt(payload.length), 2); }
    this.sock.write(Buffer.concat([head, payload]));
  }
  onData(d) {
    this.buf = Buffer.concat([this.buf, d]);
    for (;;) {
      if (this.buf.length < 2) return;
      const op = this.buf[0] & 0x0f;
      let len = this.buf[1] & 0x7f, off = 2;
      if (len === 126) { if (this.buf.length < 4) return; len = this.buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (this.buf.length < 10) return; len = Number(this.buf.readBigUInt64BE(2)); off = 10; }
      const masked = (this.buf[1] & 0x80) !== 0;
      const need = off + (masked ? 4 : 0) + len;
      if (this.buf.length < need) return;
      const mask = masked ? this.buf.subarray(off, off + 4) : null;
      const data = Buffer.from(this.buf.subarray(off + (masked ? 4 : 0), need));
      if (mask) for (let i = 0; i < data.length; i++) data[i] ^= mask[i & 3];
      this.buf = this.buf.subarray(need);
      if (op === 0x8) { try { this.sock.end(Buffer.from([0x88, 0])); } catch (_) { } clients.delete(this); return; }
      if (op === 0x9) { this.sock.write(Buffer.concat([Buffer.from([0x8a, data.length]), data])); continue; }
      if (op === 0x1) { let m; try { m = JSON.parse(data.toString('utf8')); } catch (_) { continue; } this.onMessage(m); }
    }
  }
  onMessage(m) {
    if (![...devices.values()].includes(this.dev)) { this.send({ t: 'revoked' }); this.sock.end(); return; }
    switch (m.t) {
      case 'hello': this.send({ t: 'state', ...stateFor(this.dev) }); break;
      case 'ping': this.send({ t: 'pong' }); break;
      case 'visible': this.visible = !!m.on; break;
      case 'call':
        try { this.send({ t: 'result', id: m.id, ok: true, result: callMethod(this.dev, m.method, m.params || {}) ?? null }); }
        catch (e) { this.send({ t: 'result', id: m.id, ok: false, error: e.message }); }
        break;
      case 'attach': {
        const f = findPane(m.pane);
        if (!f || f.pane.kind !== 'terminal') { this.send({ t: 'error', error: `no terminal pane '${m.pane}'` }); break; }
        this.attached.add(m.pane);
        const [cols, rows] = (f.pane.size || '100x30').split('x').map(Number);
        this.send({ t: 'snapshot', pane: m.pane, data: snapshot(m.pane), cols, rows, seq: seqs.get(m.pane) || 0 });
        break;
      }
      case 'detach': this.attached.delete(m.pane); break;
    }
  }
}

server.on('upgrade', (req, sock) => {
  const url = new URL(req.url, 'http://x');
  const dev = device(req);
  const fail = (code, text) => { sock.end(`HTTP/1.1 ${code} ${text}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`); };
  if (url.pathname !== '/api/live') return fail(404, 'Not Found');
  if (!sameOrigin(req)) return fail(403, 'Forbidden');
  if (!dev) return fail(401, 'Unauthorized');
  const key = req.headers['sec-websocket-key'];
  const accept = crypto.createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  sock.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
  sock.setNoDelay(true);
  clients.add(new Client(sock, dev));
});

let stateTimer = null;
function broadcastState() {
  clearTimeout(stateTimer);
  stateTimer = setTimeout(() => { for (const c of clients) c.send({ t: 'state', ...stateFor(c.dev) }); }, 150);
}

server.listen(PORT, HOST, () => {
  console.log(`Bellboard Remote mock on http://${HOST}:${PORT}  (level: ${LEVEL})`);
  console.log(`Pair: http://${HOST === '0.0.0.0' ? 'localhost' : HOST}:${PORT}/#pair=ABCD-EFGH`);
});
