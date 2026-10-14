// New agent (full access): a task, a repo and an agent; Bellboard makes the branch and worktree.
import { call } from '../api.js';
import { can } from '../store.js';
import { h, icon, replace, busy, toast, shortPath } from '../ui.js';

const AGENTS = [
  { id: 'claude', label: 'Claude Code' },
  { id: 'codex', label: 'Codex' },
  { id: 'opencode', label: 'OpenCode' },
  { id: 'pi', label: 'Pi' },
];

export function newAgentView() {
  if (!can('full')) {
    return {
      title: 'New agent', back: '#/workspaces',
      el: h('div.empty', h('div.empty-mark', icon('plus')), h('h2', { text: 'Needs full access' }),
        h('p', { text: 'Starting agents from this device needs full access. Change it on your PC in Settings › Remote › Devices.' })),
    };
  }

  const repoList = h('div.repo-list', { attrs: { role: 'radiogroup', 'aria-label': 'Repository' } }, h('p.muted', { text: 'Loading recent repositories…' }));
  const otherPath = h('input.field-input', { type: 'text', autocomplete: 'off', spellcheck: false, autocapitalize: 'off', attrs: { placeholder: 'C:\\code\\my-repo or \\\\wsl.localhost\\Ubuntu\\home\\me\\repo', 'aria-label': 'Repository path' } });
  const task = h('textarea.field-input.task-input', { rows: 3, attrs: { placeholder: 'Fix the flaky login test', maxlength: 2000, 'aria-label': 'Task' } });
  const base = h('input.field-input', { type: 'text', autocomplete: 'off', spellcheck: false, autocapitalize: 'off', attrs: { placeholder: 'Current branch', 'aria-label': 'Base branch' } });
  const error = h('p.form-error', { attrs: { role: 'alert' }, hidden: true });
  let repo = null;
  let agent = 'claude';

  const agentPick = h('div.segmented', { attrs: { role: 'radiogroup', 'aria-label': 'Agent' } }, AGENTS.map((a) => {
    const b = h('button.seg', { type: 'button', text: a.label, attrs: { role: 'radio', 'aria-checked': a.id === agent ? 'true' : 'false' } });
    b.addEventListener('click', () => {
      agent = a.id;
      for (const x of agentPick.children) x.setAttribute('aria-checked', x === b ? 'true' : 'false');
    });
    return b;
  }));

  function pickRepo(path, btn) {
    repo = path;
    for (const x of repoList.querySelectorAll('.repo')) x.setAttribute('aria-checked', x === btn ? 'true' : 'false');
    if (path) otherPath.value = '';
  }
  otherPath.addEventListener('input', () => { if (otherPath.value.trim()) pickRepo(null, null); });

  call('agent.recent-repos', {}).then((repos) => {
    if (!repos || !repos.length) {
      replace(repoList, h('p.muted', { text: 'No recent repositories. Type a path below.' }));
      return;
    }
    replace(repoList, repos.slice(0, 12).map((r, i) => {
      const b = h('button.repo', { type: 'button', attrs: { role: 'radio', 'aria-checked': 'false' } },
        h('span.repo-name', { text: r.name || shortPath(r.path) }), h('span.repo-path', { text: shortPath(r.path) }));
      b.addEventListener('click', () => pickRepo(r.path, b));
      if (i === 0) setTimeout(() => pickRepo(r.path, b));
      return b;
    }));
  }).catch((e) => replace(repoList, h('p.form-error', { text: e.message })));

  const submit = h('button.btn.btn-primary.btn-block', { type: 'submit' }, icon('plus'), h('span', { text: 'Start agent' }));
  const form = h('form.form', {
    onsubmit: (e) => {
      e.preventDefault();
      error.hidden = true;
      const path = otherPath.value.trim() || repo;
      if (!task.value.trim()) { show('Describe the task for the agent.'); task.focus(); return; }
      if (!path) { show('Pick a repository or type its path.'); return; }
      busy(submit, async () => {
        try {
          const r = await call('agent.new', { task: task.value.trim(), agent, repo: path, base: base.value.trim() || undefined });
          toast(`Started on ${r.branch}`);
          location.hash = r.pane ? '#/pane/' + encodeURIComponent(r.pane) : '#/ws/' + encodeURIComponent(r.workspace);
        } catch (err) { show(err.message); }
      });
    },
  },
  h('label.field', h('span.field-label', { text: 'Task' }), task),
  h('div.field', h('span.field-label', { text: 'Repository' }), repoList, otherPath),
  h('div.field', h('span.field-label', { text: 'Agent' }), agentPick),
  h('label.field', h('span.field-label', { text: 'Start from branch' }), base,
    h('span.field-hint', { text: 'Leave empty to branch from what the repo has checked out.' })),
  error,
  submit,
  h('p.field-hint', { text: 'The agent gets its own branch and worktree on your PC, and its own workspace in Bellboard.' }));

  function show(msg) { error.textContent = msg; error.hidden = false; }

  return { title: 'New agent', back: '#/workspaces', el: h('div.page', form) };
}
