// One pane: what the agent is doing or asking, the answers, and a reply box.
import { call } from '../api.js';
import { can, findPane, inboxRow, canType, isAgentPane } from '../store.js';
import { h, icon, replace, liveTime, statusDot, STATUS_TEXT, toast, busy } from '../ui.js';
import { statusBlocks, finishSection, diffSection, composer, keyBar } from './shared.js';

export function paneView(id) {
  const head = h('div.pane-head');
  const promptBox = h('div');
  const screenBox = h('div');
  const toolsBox = h('div.pane-tools');
  const diffBox = h('div');
  const statusBox = h('div');
  const finishBox = h('div');
  const dock = h('div.dock');
  const body = h('div.pane', head, promptBox, screenBox, toolsBox, diffBox, statusBox, finishBox, dock);

  const v = { title: 'Pane', back: '#/', el: body, update, destroy };
  const keys = {};
  let screen = null;            // last agent.prompt / terminal.read result
  let loading = false;
  let refetchTimer = null;
  let dockMounted = null;
  let finishMounted = false;
  let infoLoaded = null;
  let gone = false;

  function changed(name, value) {
    const k = JSON.stringify(value);
    if (keys[name] === k) return false;
    keys[name] = k;
    return true;
  }

  function update() {
    const found = findPane(id);
    if (!found) {
      if (!gone) {
        gone = true;
        v.title = 'Pane closed';
        replace(body, h('div.empty', h('div.empty-mark', icon('terminal')), h('h2', { text: 'This pane is closed' }),
          h('p', { text: 'It was closed on the PC, or its agent finished.' }),
          h('a.btn.btn-quiet', { href: '#/', text: 'Back to the inbox' })));
      }
      return;
    }
    const { pane, ws } = found;
    const row = inboxRow(id);
    const agentish = isAgentPane(pane, ws);
    v.title = (ws.agentTask && agentish) ? ws.name : (pane.title || ws.name);
    v.back = '#/';

    if (changed('head', [row, pane.agent, pane.title, ws.name, ws.agentTask, ws.branch])) renderHead(pane, ws, row);
    if (changed('prompt', [row && row.prompt, can('respond')])) renderPrompt(row);
    if (changed('tools', [pane.kind, pane.feed])) {
      replace(toolsBox, pane.kind === 'terminal'
        ? h('a.btn.btn-quiet.btn-block', { href: '#/term/' + encodeURIComponent(id) }, icon('terminal'), h('span', { text: 'Open terminal' }))
        : h('p.muted', { text: 'Browser panes are only shown on the PC.' }));
    }
    if (changed('status', [ws.pills, ws.progress, ws.logs])) replace(statusBox, statusBlocks(ws));

    // Agent workspace: its changes (agent.info) and, with full access, Finish.
    if (ws.agentTask && infoLoaded !== ws.id) {
      infoLoaded = ws.id;
      call('agent.info', { workspace: ws.id }).then((info) => replace(diffBox, diffSection(info))).catch(() => replace(diffBox));
    }
    if (ws.agentTask && can('full') && !finishMounted) { finishMounted = true; replace(finishBox, finishSection(ws)); }
    if ((!ws.agentTask || !can('full')) && finishMounted) { finishMounted = false; replace(finishBox); }

    // Reply box and keys, for panes this device may type into.
    const typing = canType(pane, ws);
    if (typing !== dockMounted) {
      dockMounted = typing;
      replace(dock, typing ? [keyBar(id, refetchSoon), composer(id, refetchSoon)] : []);
      body.classList.toggle('has-dock', typing);
    }

    // The screen: refetch when what the agent reports changes.
    if (pane.kind === 'terminal' && changed('screenKey', [row && row.status, row && row.since, row && row.message, row && row.prompt && row.prompt.id])) {
      refetchSoon(screen ? 400 : 0);
    }
  }

  function renderHead(pane, ws, row) {
    const status = row ? row.status : (pane.agent ? pane.agent.status : null);
    const task = ws.agentTask && isAgentPane(pane, ws) ? ws.agentTask.task : (row ? row.title : (pane.title || ws.name));
    const branch = ws.agentTask ? ws.agentTask.branch : (pane.branch || ws.branch);
    replace(head,
      status ? h('div.pane-status', { class: 'status-' + status },
        status === 'waiting' ? h('span.ring', { attrs: { 'aria-hidden': 'true' } }) : statusDot(status),
        liveTime(row && row.since, (a) => (STATUS_TEXT[status] || status) + (a ? ` · ${a}` : '')),
        row && row.agent ? h('span.card-agent', { text: row.agent }) : null) : null,
      h('h2.pane-task', { text: task || 'Terminal' }),
      h('p.card-where',
        h('span.where-ws', { text: ws.name }),
        branch ? h('span.where-branch', icon('branch'), h('span', { text: branch })) : null,
        pane.feed ? h('span.faint', { text: 'Read-only feed' }) : null),
      row && row.message && !(row.prompt) ? h('p.pane-message', { text: row.message }) : null);
  }

  function renderPrompt(row) {
    const p = row && row.prompt;
    if (!p) { replace(promptBox); return; }
    const respond = can('respond');
    const note = h('input.field-input', { type: 'text', attrs: { placeholder: 'Tell the agent what to do instead (optional)', maxlength: 500, 'aria-label': 'Reason' } });
    const noteRow = h('div.deny-note', { hidden: true }, note,
      h('button.btn.btn-deny', { type: 'button', text: 'Deny with note', onclick: (e) => answer(e.currentTarget, 'deny', note.value.trim() || undefined) }));
    async function answer(btn, decision, message) {
      await busy(btn, async () => {
        await call('agent.answer', { pane: id, promptId: p.id, decision, message });
        toast(decision === 'deny' ? 'Denied' : decision === 'always' ? 'Allowed from now on' : 'Allowed');
      });
    }
    replace(promptBox, h('section.prompt-card',
      h('div.prompt-card-head', h('span.prompt-tool', { text: p.tool }), liveTime(p.since, (a) => (a ? `asked ${a === 'just now' ? 'just now' : a + ' ago'}` : ''))),
      h('pre.prompt-command', { text: p.summary }),
      p.detail ? h('details.prompt-detail', h('summary', { text: 'Details' }), h('pre', { text: p.detail })) : null,
      respond ? h('div.prompt-actions',
        h('button.btn.btn-allow.btn-lg', { type: 'button', onclick: (e) => answer(e.currentTarget, 'allow') }, icon('check'), h('span', { text: 'Allow' })),
        h('button.btn.btn-deny.btn-lg', { type: 'button', onclick: (e) => answer(e.currentTarget, 'deny') }, icon('x'), h('span', { text: 'Deny' })),
        p.canAlwaysAllow ? h('button.btn.btn-quiet', { type: 'button', text: 'Allow always', onclick: (e) => answer(e.currentTarget, 'always') }) : null,
        h('button.btn.btn-link', { type: 'button', text: 'Deny with a note…', onclick: (e) => { noteRow.hidden = false; e.currentTarget.hidden = true; note.focus(); } })) : h('p.muted', { text: 'This device can view only; answer on the PC.' }),
      respond ? noteRow : null));
  }

  function refetchSoon(delay = 700) {
    clearTimeout(refetchTimer);
    refetchTimer = setTimeout(fetchScreen, delay);
  }

  async function fetchScreen() {
    const found = findPane(id);
    if (!found || found.pane.kind !== 'terminal' || loading) return;
    loading = true;
    try {
      const agentish = isAgentPane(found.pane, found.ws);
      screen = agentish
        ? await call('agent.prompt', { pane: id })
        : { text: (await call('terminal.read', { pane: id, lines: 30 })).text, choices: [], question: null };
      renderScreen(found);
    } catch (e) {
      replace(screenBox, h('p.muted.screen-error', { text: e.message }));
    } finally {
      loading = false;
    }
  }

  function renderScreen(found) {
    const s = screen;
    const typing = canType(found.pane, found.ws);
    const pending = !!(inboxRow(id) && inboxRow(id).prompt);
    const choices = (s.choices || []);
    const refresh = () => h('button.icon-btn.small', { type: 'button', attrs: { 'aria-label': 'Refresh the screen', title: 'Refresh' }, onclick: () => fetchScreen() }, icon('refresh'));
    const asking = choices.length > 0 && !pending;
    const tail = h('pre.screen-text', { text: s.text || '(empty)' });
    // The choices already show the question; don't repeat the agent's message above them.
    body.classList.toggle('asking', asking);
    replace(screenBox,
      asking ? h('section.choices',
        h('div.choices-head', h('h2.choices-q', { text: s.question || 'The agent is asking' }), refresh()),
        h('div.choice-list', choices.map((c) => h('button.choice', {
          type: 'button',
          class: c.selected ? 'selected' : '',
          disabled: !typing,
          onclick: (e) => busy(e.currentTarget, async () => {
            try {
              await call('agent.answer', { pane: id, hash: s.hash, choice: c.key });
              toast('Answered ' + c.key);
              refetchSoon(800);
            } catch (err) {
              if (/changed/i.test(err.message)) { toast('The screen changed; here it is again.'); fetchScreen(); }
              else throw err;
            }
          }),
        }, h('span.choice-key', { text: c.key }), h('span.choice-label', { text: c.label })))),
        !typing ? h('p.muted', { text: 'This device can view only.' }) : null) : null,
      asking
        ? h('details.screen', h('summary', { text: 'Show the screen' }), tail)
        : h('section.screen', h('div.screen-head', h('h2.section-title', { text: 'Screen' }), refresh()), tail));
    // Keep the newest lines in view.
    requestAnimationFrame(() => { tail.scrollTop = tail.scrollHeight; });
  }

  function destroy() { clearTimeout(refetchTimer); }

  return v;
}
