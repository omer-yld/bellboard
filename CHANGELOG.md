# Changelog

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
