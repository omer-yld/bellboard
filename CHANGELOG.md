# Changelog

## Unreleased

- **Bellboard Remote:** your agents on your phone, as an installable web app served by Bellboard: the inbox,
  **Allow** / **Deny** for Claude Code permission requests (a new `PermissionRequest` hook; run `bell hooks
  install` again), numbered choices as buttons, replies and keys, live terminals with **Fit to phone**, push
  notifications, and new / finish agent with full access. Off by default; published privately with
  `bell remote tailscale` (or your own tunnel, or the LAN); devices pair with a QR code and get a level (view,
  respond, full). Settings › Remote, `bell remote …`, and a status bar item while a phone is connected.
- `bell events` follows notifications, permission requests and state as JSON lines; `bell attach <pane>`
  mirrors a pane read-only in another terminal.
- The inbox's **Approve** answers a pending permission request directly, or chooses "Yes" on the agent's menu,
  instead of pressing Enter on whatever is highlighted.
- Agent cards show what Claude said last (`last_assistant_message`).
- Hooks read their input as UTF-8 (non-ASCII text in prompts and commands was garbled).

## 0.4.0 — first public release

Bellboard runs many AI coding agents side by side on Windows.

- **Agents:** a git worktree and branch per task (`bell agent new`, `Ctrl+Shift+A`) for Claude Code, Codex,
  OpenCode and Pi, in Windows or WSL repos; status from hooks (working, needs you, done), subagents,
  session resume after a restart; **Finish agent** merges, opens a pull request, keeps or discards.
- **Attention:** pulsing rings, unread badges, an inbox of who needs you, taskbar flash and badge, Windows
  notifications with Jump and Mark read, do not disturb and per-workspace mute.
- **Layout:** workspaces in a sidebar you can hide; nested splits and tabs; drag tabs and panes to split,
  join or move them between workspaces; flip and swap splits; tabs that shrink to fit with a `+N` list;
  pinned workspaces and tabs.
- **Shells:** PowerShell 7, Windows PowerShell, cmd, Git Bash, WSL (bash and zsh) and SSH, with folder and
  branch tracking; new tabs open in the shell you're in.
- **Browser pane:** WebView2 (Chromium) with a start page listing your dev servers, scriptable by agents.
- **For agents and scripts:** the `bell` CLI, a named-pipe JSON API (current user only), an MCP server
  with 24 tools, a Claude Code skill, status pills, progress, logs and read-only feeds.
- **Install:** a self-contained zip with a per-user installer; `bell update` and `bell doctor`.
