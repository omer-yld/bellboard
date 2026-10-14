// Workspaces: the desktop sidebar as a list, and one workspace's panes.
import { can, workspaces, findWorkspace, inboxRow } from '../store.js';
import { h, icon, replace, statusDot, STATUS_TEXT, shortPath } from '../ui.js';
import { finishSection, statusBlocks, progressBar } from './shared.js';

export function workspacesView() {
  const list = h('div.ws-list');
  const v = {
    title: 'Workspaces',
    el: list,
    actions: can('full') ? [h('a.icon-btn', { href: '#/new', attrs: { 'aria-label': 'New agent', title: 'New agent' } }, icon('plus'))] : [],
    update,
  };
  let last = '';
  function update(state) {
    const key = JSON.stringify(state.workspaces);
    if (key === last) return;
    last = key;
    const all = workspaces();
    if (!all.length) {
      replace(list, h('div.empty', h('div.empty-mark', icon('grid')), h('h2', { text: 'No workspaces' }),
        h('p', { text: 'Bellboard has no open workspaces right now.' })));
      return;
    }
    replace(list, all.map(wsRow));
  }
  return v;
}

function agentStatus(ws) {
  const agentPane = (ws.panes || []).find((p) => p.agent);
  return agentPane ? agentPane.agent.status : (ws.agentTask ? 'running' : null);
}

function wsRow(ws) {
  const st = agentStatus(ws);
  const last = ws.logs && ws.logs.length ? ws.logs[ws.logs.length - 1] : null;
  return h('a.ws-row' + (st === 'waiting' ? '.ringing' : ''), { href: '#/ws/' + encodeURIComponent(ws.id) },
    h('span.badge', { text: ws.badge || '' , class: 'badge-' + String(ws.badge || '').toLowerCase() }),
    h('div.ws-body',
      h('div.ws-top',
        h('span.ws-name', { text: ws.name }),
        ws.pinned ? h('span.ws-flag', { text: 'Pinned' }) : null,
        ws.muted ? h('span.ws-flag', { text: 'Muted' }) : null,
        ws.unread ? h('span.count', { text: String(ws.unread), attrs: { 'aria-label': `${ws.unread} unread` } }) : null),
      ws.agentTask ? h('p.ws-task', { text: ws.agentTask.task }) : null,
      h('p.ws-meta',
        ws.branch ? h('span.where-branch', icon('branch'), h('span', { text: ws.branch })) : null,
        ws.cwd && !ws.agentTask ? h('span.ws-cwd', { text: shortPath(ws.cwd) }) : null,
        (ws.ports || []).map((p) => h('span.port', { text: ':' + p }))),
      st ? h('p.ws-status', statusDot(st), h('span', { text: ws.status || STATUS_TEXT[st] || st })) : null,
      ws.progress ? progressBar(ws.progress) : null,
      last ? h('p.ws-log', { class: 'log-' + last.level, text: last.text }) : null),
    icon('chevron', 'row-chevron'));
}

export function workspaceView(id) {
  const body = h('div.ws-detail');
  const finish = h('div');
  const v = { title: 'Workspace', back: '#/workspaces', el: h('div', body, finish), update };
  let last = '';
  let finishMounted = false;

  function update(state) {
    const ws = findWorkspace(id);
    if (!ws) {
      v.title = 'Workspace';
      replace(body, h('div.empty', h('h2', { text: 'This workspace is closed' }), h('a.btn.btn-quiet', { href: '#/workspaces', text: 'Back to workspaces' })));
      replace(finish);
      return;
    }
    v.title = ws.name;
    const key = JSON.stringify([ws, state.inbox, state.device]);
    if (key === last) return;
    last = key;
    replace(body,
      ws.agentTask ? h('div.task-head',
        h('p.task-text', { text: ws.agentTask.task }),
        h('p.ws-meta', h('span.where-branch', icon('branch'), h('span', { text: ws.agentTask.branch })), h('span.faint', { text: 'from ' + ws.agentTask.base }))) : null,
      !ws.agentTask && (ws.cwd || ws.branch) ? h('p.ws-meta.ws-meta-pad',
        ws.branch ? h('span.where-branch', icon('branch'), h('span', { text: ws.branch })) : null,
        ws.cwd ? h('span.ws-cwd', { text: shortPath(ws.cwd) }) : null) : null,
      statusBlocks(ws),
      h('section.section', h('h2.section-title', { text: (ws.panes || []).length === 1 ? 'Pane' : 'Panes' }),
        h('div.section-body', (ws.panes || []).map((p) => paneRow(p)))));
    if (ws.agentTask && can('full') && !finishMounted) {
      finishMounted = true;
      replace(finish, finishSection(ws));
    } else if (!ws.agentTask || !can('full')) {
      finishMounted = false;
      replace(finish);
    }
  }
  return v;
}

function paneRow(p) {
  const r = inboxRow(p.id);
  const st = p.agent ? p.agent.status : (r ? r.status : null);
  const kind = p.kind === 'browser' ? 'Browser' : p.feed ? 'Feed' : (p.shell || 'Terminal');
  const title = p.title || (p.kind === 'browser' ? 'Browser' : kind);
  const sub = [kind, p.size, p.agent ? p.agent.kind : null].filter(Boolean).join('  ');
  if (p.kind === 'browser') {
    return h('div.row.row-static', h('span.dot.dot-idle'), h('div.row-body',
      h('div.row-top', h('span.row-title', { text: title })), h('p.row-sub', { text: 'Browser panes are only shown on the PC' })));
  }
  return h('a.row', { href: '#/pane/' + encodeURIComponent(p.id) },
    statusDot(st || (p.attention ? 'waiting' : 'idle')),
    h('div.row-body',
      h('div.row-top', h('span.row-title', { text: title }), st ? h('span.row-status', { text: STATUS_TEXT[st] || st }) : null),
      h('p.row-sub', { text: sub })),
    icon('chevron', 'row-chevron'));
}
