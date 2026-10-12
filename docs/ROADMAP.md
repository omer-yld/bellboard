# Roadmap

What's next for Bellboard, roughly in order. Ideas and votes are welcome in
[issues](https://github.com/omer-yld/bellboard/issues).

## Soon

- **Signed releases.** Code-sign the binaries (through [SignPath Foundation](https://signpath.org), which
  signs open-source projects for free) so SmartScreen stops warning.
- **winget.** `winget install omer-yld.Bellboard`, once releases are signed.
- **Layout templates.** Save a workspace's splits, shells and commands, and open it again with one command.
- **Diff view.** See what an agent changed in its worktree before you finish it.

## Later

- **Remote agents.** Run agents on a remote machine over SSH and follow them in Bellboard as if they were
  local.
- **Phone view.** Glance at who needs you, and answer simple prompts, from your phone.
- **Arm64 builds.**

## Not planned

- macOS and Linux versions: tmux and [cmux](https://cmux.com) already serve them well.
- Sandboxing agents: Bellboard organizes agents; it doesn't contain them. Run untrusted agents in a VM or
  a container.
