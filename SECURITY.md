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
  run `bell update`, set `updateFeed`, or turn on Remote push notifications.

## Bellboard Remote

Remote access to Bellboard is remote access to your shells, so it is off by default and built to need
both a private network and a paired device.

- **Nothing listens until you turn it on.** Then `bellboard-remote.exe` (a separate process, started and
  stopped by Bellboard) listens on `127.0.0.1` only. Tailscale Serve, or your own tunnel, publishes it with
  HTTPS. LAN mode listens on every interface with a self-signed certificate whose fingerprint Settings shows.
- **Pairing.** A one-time 8-character code (about 40 bits), made only on the desktop, valid for 2 minutes and
  for one device. Five wrong codes lock pairing for 10 minutes. Pairing shows a desktop notification.
- **Device keys.** Each device gets a random 256-bit token in a `Secure`, `HttpOnly`, `SameSite=Strict`
  cookie scoped to `/api`. Bellboard keeps only its SHA-256, in `%LOCALAPPDATA%\Bellboard\remote.dat`,
  encrypted with DPAPI for your Windows user, together with the push keys. Revoking a device (Settings ›
  Remote or `bell remote revoke`) closes its connections at once.
- **Levels.** View, Respond (the default) and Full. Respond can type into a pane only while an agent runs
  there (its hooks report it); note that steering an agent is powerful in itself, since agents run commands. The sidecar checks the level before it forwards a call,
  and Bellboard checks it again; Respond may type only into panes that run an agent. Quitting the app,
  scripting the browser pane, settings and anything else not listed in `RemotePolicy` can't be called
  remotely at any level.
- **Web protections.** Requests must name a host Bellboard listed (localhost, the Tailscale name, this PC
  on the LAN), which stops DNS rebinding. Anything that changes something, and the WebSocket, must come from
  the page's own exact origin (scheme, host and port). Strict Content-Security-Policy (scripts from the app only), no framing, no caching
  of API responses, and rate limits per device and per address.
- **Permission requests.** A phone only answers what it's asked; nothing is allowed by a timeout, and coming
  back to the desk hands open questions back to the terminal.
- **What's visible.** The status bar shows connected phones; every remote action goes to the workspace's log
  and `%LOCALAPPDATA%\Bellboard\remote.log`. Whatever is on a terminal (secrets included) reaches the phone
  you mirror it on.

LAN mode uses a self-signed certificate: the browser warns once, and only comparing the fingerprint Settings
shows tells your certificate from an impostor's. Prefer Tailscale, which has real certificates.

Out of scope for Remote: a phone that is unlocked and in someone else's hands (revoke it), and a compromised
`bellboard-remote.exe`, which runs as you like Bellboard itself.

What's out of scope: anything that already runs as your user (it can drive Bellboard like you can), and
agents you start, which run with your rights by design.
