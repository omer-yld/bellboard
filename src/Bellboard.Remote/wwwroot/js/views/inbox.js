// Inbox: who needs you first, then who's working, then the rest.
import { call } from '../api.js';
import { store, can, inbox, findWorkspace } from '../store.js';
import { h, icon, replace, liveTime, statusDot, STATUS_TEXT, toast, busy } from '../ui.js';

export function inboxView() {
  const list = h('div.inbox');
  const dnd = h('button.icon-btn', {
    type: 'button',
    attrs: { 'aria-label': 'Do not disturb', 'aria-pressed': 'false', title: 'Do not disturb' },
    onclick: () => busy(dnd, async () => {
      const on = !(store.state && store.state.dnd);
      const r = await call('notification.dnd', { on });
      toast(r && r.doNotDisturb ? 'Do not disturb is on' : 'Do not disturb is off');
    }),
  }, icon('moon'));

  const v = { title: 'Inbox', el: list, actions: can('respond') ? [dnd] : [], update };
  let last = '';

  function update(state) {
    dnd.setAttribute('aria-pressed', state.dnd ? 'true' : 'false');
    dnd.classList.toggle('on', !!state.dnd);
    const key = JSON.stringify([state.inbox, state.dnd, state.device && state.device.level,
      (state.workspaces || []).map((w) => [w.id, w.branch, w.name])]);
    if (key === last) return;
    last = key;
    render(state);
  }

  function render(state) {
    const rows = inbox();
    if (!rows.length) {
      replace(list, empty(state));
      return;
    }
    const waiting = rows.filter((r) => r.status === 'waiting');
    const working = rows.filter((r) => r.status === 'working' || r.status === 'running');
    const rest = rows.filter((r) => r.status !== 'waiting' && r.status !== 'working' && r.status !== 'running');
    replace(list,
      state.dnd ? h('p.note', icon('moon'), h('span', { text: 'Do not disturb is on: no pushes or toasts until you turn it off.' })) : null,
      waiting.length
        ? section(waiting.length === 1 ? 'Needs you' : `${waiting.length} need you`, waiting.map(waitingCard), 'section-waiting')
        : h('p.all-clear', icon('check'), h('span', { text: 'Nobody is waiting on you.' })),
      working.length ? section('Working', working.map(row)) : null,
      rest.length ? section('Done and idle', rest.map(row)) : null);
  }

  return v;
}

function section(title, items, cls) {
  return h('section.section' + (cls ? '.' + cls : ''), h('h2.section-title', { text: title }), h('div.section-body', items));
}

function where(r) {
  const ws = findWorkspace(r.workspace);
  const parts = [h('span.where-ws', { text: r.workspaceName || (ws && ws.name) || r.workspace })];
  const branch = ws && (ws.agentTask ? ws.agentTask.branch : ws.branch);
  if (branch) parts.push(h('span.where-branch', icon('branch'), h('span', { text: branch })));
  return h('p.card-where', parts);
}

function waitingCard(r) {
  const href = '#/pane/' + encodeURIComponent(r.pane);
  const p = r.prompt;
  const main = h('a.card-main', { href },
    h('div.card-head',
      h('span.ring', { attrs: { 'aria-hidden': 'true' } }),
      liveTime(p ? p.since : r.since, (a) => (a ? `Needs input · ${a}` : 'Needs input')),
      r.agent ? h('span.card-agent', { text: r.agent }) : null),
    h('h3.card-title', { text: r.title }),
    where(r),
    p
      ? h('div.prompt-peek', h('span.prompt-tool', { text: p.tool }), h('code.prompt-summary', { text: p.summary }))
      : (r.message ? h('p.card-message', { text: r.message }) : null));

  const actions = h('div.card-actions');
  if (p && can('respond') && !r.feed) {
    const answer = (decision, label) => (e) => busy(e.currentTarget, async () => {
      await call('agent.answer', { pane: r.pane, promptId: p.id, decision });
      toast(label);
    });
    actions.append(
      h('button.btn.btn-allow', { type: 'button', onclick: answer('allow', 'Allowed') }, icon('check'), h('span', { text: 'Allow' })),
      h('button.btn.btn-deny', { type: 'button', onclick: answer('deny', 'Denied') }, icon('x'), h('span', { text: 'Deny' })));
    if (p.canAlwaysAllow) actions.append(h('button.btn.btn-quiet', { type: 'button', text: 'Allow always', onclick: answer('always', 'Allowed from now on') }));
  } else {
    actions.append(h('a.btn.btn-primary', { href }, h('span', { text: r.feed ? 'View' : 'Answer' }), icon('chevron')));
  }
  return h('article.card.card-waiting', main, actions);
}

function row(r) {
  return h('a.row', { href: '#/pane/' + encodeURIComponent(r.pane) },
    statusDot(r.status),
    h('div.row-body',
      h('div.row-top', h('span.row-title', { text: r.title }),
        liveTime(r.since, (a) => (STATUS_TEXT[r.status] || r.status) + (a ? ` · ${a}` : ''))),
      r.message ? h('p.row-sub', { text: r.message }) : h('p.row-sub', { text: r.where })),
    icon('chevron', 'row-chevron'));
}

function empty(state) {
  const hasWs = state && (state.workspaces || []).length > 0;
  return h('div.empty',
    h('div.empty-mark', icon('inbox')),
    h('h2', { text: 'No agents yet' }),
    h('p', {
      text: hasWs
        ? 'Your workspaces are open, but no agent has reported in. Start one on your PC with Ctrl+Shift+A, or run “bell hooks install” so running agents show up here.'
        : 'Start an agent on your PC with Ctrl+Shift+A (New agent). It shows up here when it’s working, and rings when it needs you.',
    }),
    can('full') ? h('a.btn.btn-primary', { href: '#/new' }, icon('plus'), h('span', { text: 'New agent' })) : null,
    hasWs ? h('a.btn.btn-quiet', { href: '#/workspaces', text: 'See workspaces' }) : null);
}
