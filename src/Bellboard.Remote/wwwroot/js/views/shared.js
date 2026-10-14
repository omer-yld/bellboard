// Pieces shared by the workspace, pane and terminal screens.
import { call } from '../api.js';
import { h, icon, replace, toast, busy, confirmButton, ago } from '../ui.js';

export function progressBar(p) {
  const pct = Math.round(Math.max(0, Math.min(1, p.value || 0)) * 100);
  const fill = h('span.progress-fill');
  fill.style.width = pct + '%';
  return h('div.progress',
    h('div.progress-track', { attrs: { role: 'progressbar', 'aria-valuenow': pct, 'aria-valuemin': 0, 'aria-valuemax': 100 } }, fill),
    h('span.progress-label', { text: (p.label ? p.label + '  ' : '') + pct + '%' }));
}

const LOG_GLYPH = { success: '✓', warning: '!', error: '✕', progress: '…', info: '›' };

/** Status pills, progress and the latest log lines a workspace reports (`bell status/progress/log`). */
export function statusBlocks(ws) {
  const pills = ws.pills || [];
  const logs = ws.logs || [];
  if (!pills.length && !ws.progress && !logs.length) return null;
  return h('section.section.status-blocks',
    pills.length ? h('div.pills', pills.map(pill)) : null,
    ws.progress ? progressBar(ws.progress) : null,
    logs.length ? h('ol.logs', logs.map((l) => h('li.log', { class: 'log-' + l.level },
      h('span.log-glyph', { text: LOG_GLYPH[l.level] || '›', attrs: { 'aria-hidden': 'true' } }),
      h('span.log-text', { text: l.text }),
      l.at ? h('span.log-at', { text: ago(l.at) }) : null))) : null);
}

/** `bell status set … --color` takes a name (blue, green…) or a #hex colour. */
function pill(p) {
  const c = String(p.color || 'gray').toLowerCase();
  const el = h('span.pill', { text: p.text });
  if (/^#[0-9a-f]{3}([0-9a-f]{3})?$/.test(c)) el.style.color = c;
  else if (/^[a-z]+$/.test(c)) el.classList.add('pill-' + c);
  return el;
}

const KEYS = [
  { label: 'Esc', keys: ['esc'] },
  { label: 'Ctrl+C', keys: ['ctrl+c'], cls: 'key-danger' },
  { label: 'Tab', keys: ['tab'] },
  { label: '⇧Tab', keys: ['shift+tab'], aria: 'Shift Tab' },
  { label: '↑', keys: ['up'], aria: 'Up' },
  { label: '↓', keys: ['down'], aria: 'Down' },
  { label: '←', keys: ['left'], aria: 'Left' },
  { label: '→', keys: ['right'], aria: 'Right' },
  { label: 'Enter', keys: ['enter'], cls: 'key-enter' },
  { label: '1', text: '1' },
  { label: '2', text: '2' },
  { label: '3', text: '3' },
];

/** A horizontal row of keys that agents' prompts need and phone keyboards lack. */
export function keyBar(paneId, after) {
  return h('div.keybar', { attrs: { role: 'toolbar', 'aria-label': 'Keys' } }, KEYS.map((k) =>
    h('button.key', {
      type: 'button',
      class: k.cls,
      text: k.label,
      attrs: { 'aria-label': k.aria || k.label },
      onclick: (e) => busy(e.currentTarget, async () => {
        if (k.text) await call('terminal.send', { pane: paneId, text: k.text });
        else await call('terminal.send-key', { pane: paneId, keys: k.keys });
        if (navigator.vibrate) navigator.vibrate(8);
        if (after) after();
      }),
    })));
}

/** Text box + Send: types the text into the agent and presses Enter. */
export function composer(paneId, after) {
  const input = h('textarea.composer-input', {
    rows: 1,
    attrs: { 'aria-label': 'Reply to the agent', placeholder: 'Reply…', maxlength: 4000, enterkeyhint: 'send' },
  });
  const send = h('button.btn.btn-primary.composer-send', { type: 'submit', attrs: { 'aria-label': 'Send' } }, icon('send'));
  const grow = () => { input.style.height = 'auto'; input.style.height = Math.min(input.scrollHeight, 160) + 'px'; };
  input.addEventListener('input', grow);
  const form = h('form.composer', {
    onsubmit: (e) => {
      e.preventDefault();
      const text = input.value;
      if (!text.trim()) return;
      busy(send, async () => {
        await call('agent.answer', { pane: paneId, text, enter: true });
        input.value = '';
        grow();
        toast('Sent');
        if (after) after();
      });
    },
  }, input, send);
  input.addEventListener('keydown', (e) => {
    // Enter sends on hardware keyboards; Shift+Enter adds a line.
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing && window.matchMedia('(pointer: fine)').matches) {
      e.preventDefault();
      form.requestSubmit();
    }
  });
  return form;
}

const FINISH = [
  { mode: 'merge', label: 'Merge', verb: 'Merge now', what: (t) => `Merges ${t.branch} into ${t.base} in your repo’s checkout, then closes the workspace. Stops if that checkout has uncommitted changes or the merge conflicts.` },
  { mode: 'pr', label: 'Pull request', verb: 'Open the PR', what: (t) => `Pushes ${t.branch} and opens a pull request into ${t.base} with gh, then closes the workspace.` },
  { mode: 'keep', label: 'Keep branch', verb: 'Close and keep', what: (t) => `Closes the workspace and keeps ${t.branch} and its worktree for later.` },
  { mode: 'discard', label: 'Discard', verb: 'Discard for good', what: (t) => `Deletes the worktree and the branch ${t.branch}. Commits that aren’t merged anywhere are lost.`, danger: true },
];

/** Finish agent (full access): pick an outcome, read what it does, tap again to do it. */
export function finishSection(ws) {
  const t = ws.agentTask;
  const info = h('div.finish-info');
  const result = h('p.finish-result', { attrs: { role: 'status' }, hidden: true });
  const options = h('div.finish-options');
  let chosen = null;

  function choose(f) {
    chosen = f;
    for (const b of options.children) b.classList.toggle('selected', b._mode === f.mode);
    const go = confirmButton(f.verb, `Tap again to ${f.verb.toLowerCase()}`, (btn) => run(btn, false), 'btn ' + (f.danger ? 'btn-danger' : 'btn-primary'));
    replace(info, h('p.finish-what', { text: f.what(t) }), go);
  }

  async function run(btn, force) {
    result.hidden = true;
    await busy(btn, async () => {
      try {
        const r = await call('agent.finish', { workspace: ws.id, mode: chosen.mode, force });
        result.textContent = (r && r.result) || 'Done';
        result.className = 'finish-result ok';
        result.hidden = false;
        toast('Finished');
      } catch (e) {
        result.textContent = e.message;
        result.className = 'finish-result err';
        result.hidden = false;
        if (chosen.mode === 'discard' && !force) {
          replace(info, h('p.finish-what', { text: chosen.what(t) }),
            confirmButton('Discard anyway', 'Tap again: unmerged work is lost', (b2) => run(b2, true), 'btn btn-danger'));
        }
      }
    });
  }

  for (const f of FINISH) {
    const b = h('button.chip', { type: 'button', text: f.label, onclick: () => choose(f) });
    b._mode = f.mode;
    options.append(b);
  }

  return h('section.section.finish',
    h('h2.section-title', icon('flag'), h('span', { text: 'Finish this agent' })),
    h('div.section-body.finish-body', options, info, result));
}

/** "+12 −3 in 4 files · 2 commits ahead" and the uncommitted files, from agent.info. */
export function diffSection(info) {
  if (!info) return null;
  return h('section.section.diff',
    h('h2.section-title', { text: 'Changes' }),
    h('div.section-body.diff-body',
      h('p.diff-line',
        h('strong', { text: info.ahead ? `${info.ahead} commit${info.ahead === 1 ? '' : 's'} ahead of ${info.base}` : `No commits ahead of ${info.base}` }),
        info.diffstat ? h('span.diffstat', { text: info.diffstat }) : null),
      info.uncommitted && info.uncommitted.length
        ? h('details.uncommitted', h('summary', { text: `${info.uncommitted.length} uncommitted file${info.uncommitted.length === 1 ? '' : 's'}` }),
          h('ul', info.uncommitted.slice(0, 50).map((f) => h('li', h('code', { text: String(f) })))))
        : null));
}
