# Security

## Reporting a vulnerability

Please report security problems privately through GitHub:
**[Report a vulnerability](https://github.com/omer-yld/bellboard/security/advisories/new)**
(the repository's *Security* tab). Don't open a public issue for them.

Include what an attacker could do, how to reproduce it, and the Bellboard version (`bell --version`).
You'll get an answer within a week. Fixes go out as a new release, and the advisory credits you unless
you'd rather it didn't.

Only the latest release gets security fixes.

## How Bellboard protects you

Bellboard runs your shells with your rights, so the main thing to protect is who can drive it.

- **The pipe.** The CLI, agent hooks and the MCP server talk to Bellboard over
  `\\.\pipe\bellboard-<user>`, created with `PipeOptions.CurrentUserOnly`: other Windows users, and
  processes of other users, can't connect. Programs running as you can, as with any terminal you can
  script.
- **The browser pane.** Bellboard's own pages are served from a virtual host only Bellboard maps; the
  terminal and start pages accept messages only from that host. Pages you open run in WebView2's
  sandbox like in Edge.
- **Updates.** `bell update` downloads only the zip that `latest.json` names and checks its SHA-256 before
  swapping files.
- **No network of its own.** Bellboard sends no telemetry and makes no requests unless you open a page,
  run `bell update`, or set `updateFeed`.

What's out of scope: anything that already runs as your user (it can drive Bellboard like you can), and
agents you start, which run with your rights by design.
