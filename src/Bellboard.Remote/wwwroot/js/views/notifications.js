// Alerts: Bellboard's notifications, newest first.
import { call } from '../api.js';
import { can, notifications } from '../store.js';
import { h, icon, replace, liveTime, toast, busy } from '../ui.js';

export function notificationsView() {
  const list = h('div.notices');
  const markAll = h('button.btn.btn-link', {
    type: 'button', text: 'Mark all read',
    onclick: (e) => busy(e.currentTarget, async () => { await call('notification.clear', {}); toast('All read'); }),
  });
  const v = { title: 'Alerts', el: list, actions: [], update };
  let last = '';

  function update(state) {
    v.actions = can('respond') && state.unread ? [markAll] : [];
    const key = JSON.stringify(state.notifications);
    if (key === last) return;
    last = key;
    const items = notifications();
    if (!items.length) {
      replace(list, h('div.empty', h('div.empty-mark', icon('bell')), h('h2', { text: 'No alerts' }),
        h('p', { text: 'When an agent finishes or needs you, it shows up here and on your PC.' })));
      return;
    }
    replace(list, items.map(item));
  }

  function item(n) {
    return h('a.notice' + (n.read ? '' : '.unread'), {
      href: '#/pane/' + encodeURIComponent(n.pane),
      onclick: () => { if (!n.read && can('respond')) call('notification.read', { id: n.id }).catch(() => { }); },
    },
    h('span.notice-dot', { attrs: { 'aria-label': n.read ? 'Read' : 'Unread' } }),
    h('div.notice-body',
      h('div.notice-top', h('span.notice-title', { text: n.title }), liveTime(n.at)),
      n.body ? h('p.notice-text', { text: n.body }) : null,
      h('p.notice-where', { text: n.workspaceName || n.workspace })));
  }

  return v;
}
