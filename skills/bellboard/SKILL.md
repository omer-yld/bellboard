---
name: bellboard
description: Use when running inside the Bellboard terminal on Windows (BELLBOARD_PANE_ID is set) to open panes or workspaces, watch other terminals, report progress in the sidebar, notify the user, drive the built-in browser pane to check web changes, or start parallel agents in their own git worktrees.
---

# Bellboard

You are running in a pane of **Bellboard**, a Windows terminal multiplexer built for coding agents.
The `bell` CLI is on PATH in every pane, and `BELLBOARD_PANE_ID` identifies your pane. Commands
target your own pane and workspace unless you pass `--pane` / `--workspace`. If the `bellboard`
MCP server is connected, the same actions are available as tools (`mcp__bellboard__*`).

## Tell the user what is happening

```powershell
bell status set tests "running" --color blue      # pill on your workspace card (same key replaces it)
bell progress 0.4 --label "migrating"             # thin progress bar; `bell progress clear` removes it
bell log --level success "all 128 tests passed"   # latest line shows on the card
bell notify --title "Done" "Ready for review"     # ring + badge + toast: use when you need the user
```

Use `notify` only when the user should look (finished, blocked, needs a decision), not for routine progress.

## Run things beside you

```powershell
bell split right --cmd "npm run dev"              # a dev server in a pane next to you
bell read --pane p7 --lines 40                    # read another pane's output
bell send --pane p7 --enter "npm test"            # type into it
bell send-key --pane p7 ctrl+c                    # interrupt it
bell list                                         # panes, cwd, branch, listening ports
```

## Check web changes in a real browser

```powershell
bell browser open localhost:5173                  # opens/reuses a browser pane beside you
bell browser snapshot                             # interactive elements with refs: - button "Save" [ref=e7]
bell browser click e7
bell browser type e3 "ada@example.com" --submit
bell browser eval "document.title"
bell browser console                              # console errors after your change
bell browser screenshot -o shot.png
```

Prefer `snapshot` + refs over guessing CSS selectors. Re-run `snapshot` after the page changes.

## Parallel work in worktrees

```powershell
bell agent new "write tests for the auth module"  # new branch + worktree + workspace + agent
bell agent list
```

Each agent works in its own checkout (`<repo>.worktrees/<task>` on branch `bellboard/<task>`).
The user merges, opens a PR, keeps or discards it via "Finish agent" (or `bell agent finish`).
Commit your work in the worktree before you finish so it can be merged.

## Notes

- On Windows PowerShell 5.1, pipe text containing double quotes: `'say "hi"' | bell send --stdin --enter`.
- `bell help` lists every command; add `--json` for machine-readable output.
