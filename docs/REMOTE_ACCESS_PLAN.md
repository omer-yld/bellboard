# Plan: Bellboard Remote (phone view and remote access)

Status: **implemented** (branch `remote-access`), Phases 0–4 except the optional passkey step-up · Roadmap items:
**Phone view** and the groundwork for **Remote agents**. Wire protocol: `src/Bellboard.Remote/PROTOCOL.md`.

### What changed while building it
- **Default port 17821**, not a 47xxx port: WSL's mirrored networking reserves Linux's ephemeral range
  (32768–60999) on Windows, so ports there can fail to bind.
- **Spike S1 wasn't needed to ship:** the "away" policy is safe whether or not Claude Code shows its dialog
  while the hook waits (coming back to the desk cancels the wait). "Always" is available as an option.
- **The device store lives in the app**, not the sidecar: the app is authoritative for pairing codes, tokens,
  levels and push subscriptions; the sidecar keeps no files. Calls from the sidecar are recognised by the
  client process id of the pipe connection (`GetNamedPipeClientProcessId`) and must name their device; the app
  then re-checks that device's level. This catches sidecar bugs; it is not a boundary against a compromised
  sidecar, which runs as the user anyway.
- **Output mirroring needs no ring buffer:** each mirror holds the chunks that arrive while its snapshot is
  being taken, then forwards only newer ones (`TerminalMirror`); the snapshot request travels on the same
  WebView2 channel as the output, so it reflects exactly the chunks posted before it.
- **Clock skew:** state carries the PC's `now`, so "waiting 4m" is right even when the phone's clock is off.
- **Hooks read stdin as UTF-8** (found while testing: non-ASCII commands were garbled).
- **Size:** the self-contained zip grew by ~10–12 MB (87.5 MB total).

### Tests
- Unit: `tests/Bellboard.Tests/RemoteTests.cs` (prompt parser, pairing/lockout/expiry, levels, permission
  descriptions and hook output, pipe back-pressure, QR) and `WebPushTests.cs` (RFC 8291 vector, VAPID).
- End to end: `tests/remote-smoke.ps1` (43 checks: pairing, cookies, host/origin rules, levels, live terminal
  with contiguous seqs, level change and revocation closing sockets, lockout, sidecar restart, no token in
  logs), run by `build.ps1 -Smoke` and CI.
- The phone UI was driven with Playwright (iPhone 13 profile) against a live Bellboard: a real
  `bell hook claude PermissionRequest` was answered with **Allow** from the inbox card.
- Not yet verified on real devices: iOS/Android push delivery and notification actions, Tailscale Serve, LAN
  mode's certificate on a phone.


## 1. Goal

From a phone, or any browser away from the desk, you can:

1. **See who needs you.** The inbox, agent cards, notifications and how long each agent has waited.
2. **Answer simple prompts.** Approve, deny, choose option 1, 2 or 3, type a short reply, press Esc or Ctrl+C.
3. **Get pinged when the app is closed.** A push notification when an agent needs input or finishes.
4. **Look in when you need to.** A live terminal for any pane, read-only by default, with typing as an opt-in.
5. **Start and finish work.** Start a new agent on a repo and finish one (merge, PR, keep, discard), with Full access only.

Non-goals for v1: a hosted relay or cloud account, a native iOS or Android binary, running Bellboard itself
on another OS, and multi-user sharing.

## 2. Key decisions (recommended)

| Decision | Recommendation | Why |
|---|---|---|
| App type | **Installable PWA served by Bellboard itself** | No app store, no account, no build chain for two mobile platforms. Works on iOS 16.4+ (installed to the home screen) and Android, including Web Push. It also works as remote access from any laptop browser. A native shell (Capacitor) stays an option later and reuses the same web app. |
| Where the server runs | **A sidecar process `bellboard-remote.exe`** (new project `Bellboard.Remote`, ASP.NET Core Kestrel) that talks to the app over the existing named pipe | Keeps ASP.NET Core and the network attack surface out of `Bellboard.exe`. It runs only when Remote is enabled, and a crash can't take down the terminals. It reuses `CommandRouter`, so there's one API. `bell.exe` stays small and fast for hooks. |
| Reachability | **Bind to `127.0.0.1` by default and expose through Tailscale Serve** (one-click setup when `tailscale.exe` is found). LAN mode is an advanced option. | Tailscale gives a valid HTTPS certificate (`https://<pc>.<tailnet>.ts.net`), which PWAs, service workers and Web Push all require. Encryption and device identity come for free, nothing is open to the internet, and Bellboard runs no infrastructure. |
| Auth | **Device pairing with a QR code**, a long-lived per-device token, three permission levels, and revocation | Matches "no account". A phone is a device you own, not a user. |
| Terminal mirroring | **The xterm.js snapshot plus a sequenced output stream** (no server-side emulator) | Reuses the `serialize` addon Bellboard already loads. Sequence numbers make attaching race-free. |
| Prompt answers | **Structured first** (the agent's hook API), **keystrokes as the fallback** | Pressing Enter blindly (what the inbox's Approve does today) is risky when the user isn't looking at the screen. |
| Push | **Standard Web Push (VAPID, payload encrypted end to end)**, opt-in. Message text is opt-in too. | No Bellboard server is needed. The push service (FCM, Apple) sees only ciphertext and metadata. |
| Mobile UI stack | **Plain ES modules and vendored xterm.js, no bundler** | Matches `src/Bellboard.App/web/`, which has no Node toolchain. |

Alternative considered: hosting Kestrel inside `Bellboard.exe`. It's simpler (no pipe streaming), but it puts
ASP.NET Core, a network listener and the TLS code in the process that owns every shell, and it grows the
main app. `HttpListener` was rejected because LAN binding needs an admin URL ACL, and the installer is
per-user with no admin rights.

## 3. Architecture

```
 Phone (PWA)                     Windows PC
 ┌────────────┐  HTTPS + WSS   ┌──────────────────────┐  named pipe (NDJSON)  ┌──────────────────────────┐
 │ inbox      │◀──────────────▶│ bellboard-remote.exe │◀─────────────────────▶│ Bellboard.exe            │
 │ agent view │  (via Tailscale│  Kestrel             │  request/response     │  CommandRouter           │
 │ terminal   │   Serve or LAN)│  auth, devices       │  + NEW: subscriptions │  NEW: EventHub           │
 │ composer   │                │  permission filter   │    events.subscribe   │  NEW: per-pane OutputTap │
 └─────▲──────┘                │  Web Push sender     │    terminal.attach    │  TerminalPane → xterm.js │
       │ Web Push              │  static PWA files    │                       └──────────────────────────┘
       └──── FCM / Apple ◀─────┘ (outbound only, encrypted)
```

### 3.1 App side (`Bellboard.exe`)

**EventHub** (`Services/EventHub.cs`): a single place where state changes become events.
- `workspace.changed`: raised from `Workspace.Refresh()`, debounced to 150 ms per workspace. The payload is
  the same object `workspace.list` builds, factored into one `WorkspaceJson(ws)` helper.
- `inbox.changed`: recomputed with `Inbox.Build` when an agent's status or message changes (the
  `AgentHook` and `feed.status` paths in `CommandRouter`).
- `notice.posted` and `notice.read`: from `NotificationCenter.Post`, `MarkAllRead` and toast activation.
- `pane.closed`, `workspace.closed`, `dnd.changed`.
- `prompt.pending` and `prompt.resolved`: see §5.

**OutputTap** (per `TerminalPane`): live output streamed with sequence numbers.
- Give every chunk that `TerminalPane.PostOutput` sends a monotonically increasing `seq`. Keep a ring buffer
  (1 MB per pane, allocated only while a remote viewer is attached) of `(seq, text)`.
- Send the seq to the page as well: `{t:"o", d, s}`. `terminal.js` records `lastSeq` in the `write`
  callback, after xterm has parsed the chunk.
- **Snapshot without races:** post `{t:"snap", id}` on the same `PostWebMessage` channel as the output, so it
  is ordered after every chunk already sent. `terminal.js` runs `term.write('', () => …)` to drain pending
  writes, then replies with `{t:"snap", id, data: serializer.serialize({scrollback: N}), seq: lastSeq, cols,
  rows, alt: buffer.active.type === 'alternate'}`. The app then forwards ring-buffer chunks with
  `seq > snapshot.seq`. If the ring buffer has already dropped those chunks, it sends a new snapshot.
- Unloaded panes use `_reloadSnapshot + _preReady` as the snapshot, and panes not yet ready use the raw
  `_preReady`, so attaching never forces a WebView2 reload. Feeds work the same way.
- Resizes are sent as `{resize, cols, rows}` events so the phone can follow the desktop size.

**Pipe protocol: subscriptions.** Today every request gets exactly one response.
- New methods return `{"id":n,"ok":true,"result":{"sub":"s1"}}`, then the server pushes lines **without an
  `id`**: `{"sub":"s1","event":"...","data":{...}}`. `unsubscribe` (or closing the pipe) ends them.
  - `events.subscribe {topics:[...]}`
  - `terminal.attach {pane, scrollback}`: one `snapshot`, then `output {seq, d}`, `resize`, `exit`.
    `terminal.detach {sub}` stops it.
- `PipeServer.Serve` gets a write lock, because responses and pushed events now share one stream, and a
  bounded outgoing queue per connection. When a slow client overflows its queue, its output subscriptions
  are dropped and it receives `{event:"overflow"}`, then resyncs with a new snapshot.
- `PipeClient` (in `Shared/Protocol.cs`) gets a background reader that routes id-less lines to
  subscription callbacks. The existing synchronous `Call` keeps working.
- Useful on their own: `bell events --follow` and `bell attach <pane>` (read-only tail) come almost free,
  and they're how this layer gets tested.

**Remote-facing methods.** These are new or tightened router methods the sidecar needs:
- `agent.answer {pane, promptId?, choice | text | keys}`: see §5.
- `terminal.read` already exists, and `agent.info` gives the diffstat for the agent view.
- `agent.recent-repos`: `MuxController.RecentDirs` plus the repo roots of existing agents, for the New
  agent screen.
- `remote.status`: whether the sidecar is running, the connected devices and the URL, for the status bar.

**Process management.** When `remote.enabled` is true, `MuxController` starts `bellboard-remote.exe` with
`BELLBOARD_PIPE` set, adds it to the existing kill-on-close job object, and restarts it with backoff if it
crashes. The sidecar exits when the pipe closes.

### 3.2 Sidecar (`src/Bellboard.Remote`, new project)

- **net9.0 with `Microsoft.AspNetCore.App`**, self-contained in the release zip next to `Bellboard.exe` and
  `bell.exe`. Measure the zip-size increase in PR 1. If it's more than about 25 MB, trim the sidecar
  (Kestrel trims well, unlike WPF).
- **Endpoints:**
  - `GET /` serves the PWA (`index.html`, `app.js`, `sw.js`, `manifest.webmanifest`, icons, vendored
    xterm). Files are embedded resources, so they're versioned with the build.
  - `POST /api/pair` exchanges a one-time code for a device token.
  - `GET /api/state` returns workspaces, the inbox, recent notifications, DND and the device's own
    permission level.
  - `GET /api/live` is a WebSocket for everything else (below).
  - `POST /api/push/subscribe` and `DELETE /api/push/subscribe` manage Web Push.
  - `GET /api/health` is for setup checks; it returns no data before auth.
- **WebSocket messages** (JSON, one per frame):
  - Client to server: `hello {version}`, `attach {pane, scrollback}`, `detach {pane}`,
    `call {id, method, params}` (checked against an allowlist, below), `ping`.
  - Server to client: `state {...}` (a full snapshot on connect, then `workspace`/`inbox`/`notice` deltas),
    `term.snapshot`, `term.out {pane, seq, d}`, `term.resize`, `result {id, ok, result|error}`, `pong`.
  - Output is coalesced to at most 30 frames per second per pane, and permessage-deflate is on.
- **Method allowlist by permission level.** The sidecar never forwards arbitrary pipe methods. Each level
  includes everything in the levels above it.

  | Level | Allowed |
  |---|---|
  | **View** | `workspace.list`, `agent.inbox`, `agent.list`, `agent.info`, `notification.list`, `sidebar.state`, `log.list`, `terminal.attach` (read-only), `terminal.read` |
  | **Respond** (default) | + `agent.answer`, `notification.clear`, `notification.dnd`, `workspace.mute`, plus `terminal.send` and `terminal.send-key` **only on panes that have an agent** (`Agent != null`), with a length cap of 4 KB |
  | **Full** | + `terminal.send` and `send-key` on any pane, `agent.new`, `agent.finish`, `workspace.new`, `pane.close`, `feed.*` |

  Never remote: `browser.eval`, `app.quit`, `dev.*`, `app.screenshot`, settings, hooks install, update.

### 3.3 PWA (`src/Bellboard.Remote/wwwroot`)

Built mobile-first, in Bellboard's dark palette (light follows the system), with large touch targets.

| Screen | Contents |
|---|---|
| **Inbox** (home) | Agents sorted the way `Inbox.Build` sorts them: needs you first, longest waiting first. Each card shows the task, agent, workspace, ⎇ branch, "needs input · 4m", the agent's message, subagents, and quick actions (**Allow**, **Deny**, **Reply**, **Open**). Pull to refresh, plus a DND toggle. |
| **Agent** | Status header; the prompt card with choice buttons (§5); the last log lines and status pills; diffstat from `agent.info` (files changed, ahead by N); the terminal tail; a composer with a send button and a key bar (Esc, Ctrl+C, Tab, Shift+Tab, ↑, ↓, Enter, 1, 2, 3). With Full access: **Finish** (merge, PR, keep, discard). |
| **Workspaces** | The sidebar as a list: shell badge, name, folder, branch, ports, unread badge, progress bar, last log line. Tapping one opens its panes. |
| **Terminal** | xterm.js (the same vendored build as the desktop) at the **desktop's cols×rows**, fit to width, with pinch to zoom and horizontal panning. Landscape is recommended for wide panes. Read-only until you tap **Type**, which brings up the key bar. Find, and copy a selection. |
| **Notifications** | `notification.list`, unread first, with mark-all-read. Tapping one jumps to its pane. |
| **New agent** (Full) | Repo picker (recent repos), task text, agent kind, base branch. Calls `agent.new`. |
| **Device** | Device name, permission level (read-only on the phone), push on/off, sign out. |

- **Service worker:** caches the app shell for an instant open, receives pushes, and opens the right
  screen when a notification is tapped (`/#/agent/<pane>`). It never caches API data.
- **Live connection:** the WebSocket connects while the page is visible and closes when it's hidden.
  Reconnects back off and resync with `state` and new snapshots. Pushes cover the time in between.
- **Terminal size policy:** v1 mirrors the desktop and never resizes the PTY. Phase 4 adds an opt-in
  **Phone fit**: while the phone is attached and the desktop has been idle for more than 60 s, the pane is
  resized to the phone's size, and the desktop pane shows a "Sized for phone · Reclaim" bar. Agent TUIs
  redraw on resize. The desktop always wins as soon as you type on it.

### 3.4 Desktop UI

- **Settings › Remote** (a new page in `SettingsWindow`):
  - An **Enable remote access** toggle, which starts or stops the sidecar.
  - **Reach it via:** Tailscale (detected, with **Set up** that runs `tailscale serve --bg --https=443
    http://127.0.0.1:<port>` after confirmation and shows the resulting `https://….ts.net` URL), Local
    network (advanced), or "I'll use my own tunnel" (localhost only).
  - **Pair a device:** shows a QR code of `https://<url>/#pair=<code>` and the 8-character code. The code
    lasts 2 minutes, works once, and is not shown again.
  - **Devices:** name, permission level (a dropdown), paired date, last seen, last IP, and **Revoke**.
  - **Push notifications:** on or off, and "Include the agent's message text" (off by default: pushes say
    "Claude Code needs input in api-server" without the prompt contents).
  - **Answer prompts from the phone:** Off, Only when away (the default; see §5), or Always.
- **Status bar:** a phone glyph with a count while a device is connected. Hovering lists the devices;
  clicking opens Settings › Remote.
- **Audit trail:** every remote action that changes something adds a workspace log line, for example
  `› allowed Bash from Pixel 8 (remote)`. All of them also go to `%LOCALAPPDATA%\Bellboard\remote.log`.
- **CLI:** `bell remote status|enable|disable|pair|devices|revoke <id>|url`.

## 4. Security model

Remote access to Bellboard is remote shell access to the PC, so this design is the core of the feature.

1. **Off by default.** Nothing listens until you enable it. The sidecar binds `127.0.0.1` unless you choose
   LAN mode.
2. **Transport.** HTTPS is required everywhere except `localhost`. Tailscale Serve terminates TLS with a
   real certificate. LAN mode generates a self-signed certificate once (ECDSA P-256, stored with DPAPI), and
   the pairing QR carries its SHA-256 fingerprint so the user can check it. Document that iOS PWAs work
   poorly with self-signed certificates and recommend Tailscale.
3. **Pairing.** A one-time code: 40 bits of entropy, valid for 2 minutes, used once, created only from the
   desktop (Settings or `bell remote pair`). Five wrong attempts lock pairing for 10 minutes. A successful
   pairing creates a device record and shows a desktop toast ("Pixel 8 was paired · Revoke").
4. **Device tokens.** 256-bit random tokens. Only their SHA-256 hashes are stored, in
   `%APPDATA%\Bellboard\remote-devices.json`, encrypted with DPAPI (CurrentUser). The token reaches the
   phone as a `Secure; HttpOnly; SameSite=Strict` cookie scoped to `/api`. It's checked on every request and
   on the WebSocket upgrade, and revocation closes that device's sockets at once. Tokens are never shown in
   the UI or written to logs.
5. **Browser-side protections.** The `Origin` header must match the configured URL on the WebSocket upgrade
   and on every `POST`/`DELETE`. A strict CSP is set (`default-src 'self'`, no inline scripts), along with
   `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer` and `Cache-Control: no-store` on `/api`.
6. **Least privilege.** The per-device permission level is enforced in the sidecar *and* re-checked in the
   app: remote calls arrive with `"remoteDevice":"<id>"`, only from the sidecar process the app started, and
   `CommandRouter` looks up that device's level and refuses methods above it (defense in depth against sidecar
   bugs).
7. **Step-up for dangerous actions.** Full-level calls (`agent.finish --merge`, typing into non-agent panes)
   need a recent unlock. In v1 that's a confirmation screen. Later it's a WebAuthn passkey on the phone
   (Face ID or a fingerprint) registered at pairing.
8. **Rate limits.** 20 calls per second per device. `terminal.send` is limited to 4 KB per call and 64 KB
   per minute for Respond.
9. **Visibility.** The status bar shows when a device is connected, every remote action is logged, and
   pairing triggers a desktop toast.
10. **Data exposure.** Whatever is on the terminal reaches the phone, secrets included. Say so in the
    Settings copy. The sidecar caches no terminal data on disk, and the service worker caches no API
    responses.
11. **Update SECURITY.md** with the new surface, and add a threat-model section: a stolen phone (revoke it;
    the token lives in the browser's cookie jar), a malicious page on the LAN (blocked by `Origin`, CSRF and
    the cookie scope), and a compromised tailnet node (still needs a device token).

## 5. Answering prompts

Today the inbox's **Approve** sends `\r`, which takes whatever option is highlighted. That's acceptable
when you're looking at the screen, but not from a phone. Prompts get two layers.

**A. Structured (Claude Code).** Add a `PermissionRequest` hook to `bell hooks install`, run as `bell hook
claude PermissionRequest`. It gets `tool_name` and `tool_input` from the hook payload and calls
`agent.permission` on the pipe with a long timeout (not the 5 s used for other hooks).
- When the policy says the desktop should answer (**Off**, or **Only when away** while you're at the desk:
  the Bellboard window was active in the last N minutes), the app replies at once with "no decision". The
  hook exits with no output and Claude Code shows its normal dialog, so nothing changes at the desk.
- When you're away, the app records a **pending prompt** with an id, the tool, a short summary of the input
  (the command for Bash, the path for Edit and Write) and its suggestions. It raises `prompt.pending`, which
  updates the card, the inbox and the push ("Claude wants to run `npm test` · Allow / Deny"), and waits.
  - The answer from the phone, or the desktop's own card, resolves the hook with allow or deny (plus an
    optional message for deny) in Claude Code's decision format.
  - If no one answers before the hook timeout, it falls back to "no decision" and the TUI dialog appears.
    Nothing is ever allowed by timeout.
- `PreToolUse` is **not** used for this. It fires for every tool call, including ones already allowed, which
  would add a pipe round trip to every call.

What Claude Code's hooks reference (code.claude.com/docs/en/hooks) confirms:
- **Input:** `tool_name`, `tool_input`, optional `permission_suggestions` and `mcp_server`, plus the common
  fields (`session_id`, `transcript_path`, `cwd`, `permission_mode`).
- **Output:** `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"|"deny",
  "message"?, "interrupt"?, "updatedInput"?, "updatedPermissions"?}}}`. An allow never overrides a matching
  deny rule. `permission_suggestions` can become an **Allow always** button through `updatedPermissions`.
- **Timeout:** the default for command hooks is 600 s, set per handler with `"timeout"` in the hook entry.
  `bell hooks install` should set it explicitly (for example 900 s) so the wait doesn't depend on defaults.
- The existing `Notification` hook carries `notification_type` (`permission_prompt`, `idle_prompt`,
  `elicitation_dialog`, …), but `permission_prompt` fires only after the prompt has waited about 6 s.
  PermissionRequest is immediate, so it drives the phone. `notification_type` is still useful for better
  card text.
- `Stop` carries `last_assistant_message`. Use it for the "done" card and push text instead of the pane
  title, which is what `AgentHook` uses today.

**Not documented, and the first thing to test (spike S1 in Phase 0):** whether the TUI permission dialog is
shown *while* a PermissionRequest hook is still running. The docs say hooks and a `--permission-prompt-tool`
host run in parallel, with the first decision winning.
- **If the dialog shows in parallel**, the design gets simpler: always route to the phone, and whoever answers
  first wins (desktop or phone). When the desktop answers in the TUI, the hook's pending prompt is cancelled.
  Detect that with the next `PostToolUse` or `UserPromptSubmit` event, or when the pane leaves "waiting".
- **If the dialog waits for the hook**, keep the away-only policy above so the desk is never blocked.

**B. Keystrokes (all agents: Codex, Pi, OpenCode, and Claude when the hook isn't installed).** The phone
reads the last 30 lines with `terminal.read` and finds a numbered choice menu with a small parser in
`Core/PromptParser.cs`:
- It matches lines like `❯ 1. Yes`, `2. Yes, and don't ask again`, `3. No, and tell Claude what to do
  differently (esc)`, and Codex and Pi equivalents (unit-tested against recorded screens).
- Each choice becomes a button that sends its digit (and Enter if the agent needs it). If nothing parses, the
  phone shows the raw tail with the key bar. That still works, it's just less convenient.
- The answer is sent only if the screen hasn't changed since the phone read it: `agent.answer` carries the
  read's hash, and the app re-reads and compares before typing. This prevents answering a different prompt
  than the one shown.

**Desktop parity.** The inbox's **Approve** button switches to the same structured path when a pending
prompt exists, and to the parsed choice otherwise. The "blind Enter" behavior goes away.

## 6. Push notifications

- **Keys:** a VAPID key pair (P-256) is generated on first enable and stored with DPAPI. The public key is
  served to the PWA.
- **Subscriptions:** each device stores its `PushSubscription` (endpoint, `p256dh`, `auth`) in its device
  record. Revoking the device deletes it.
- **Sending:** in the sidecar, from `notice.posted` and `prompt.pending` events. A notification is sent only
  when no WebSocket from that device has been visible in the last 30 s, and never in do-not-disturb or for
  muted workspaces. That's the same rule as toasts in `NotificationCenter.Post`.
- **Encryption:** RFC 8291 (aes128gcm) and RFC 8292 (VAPID JWT). Use a small, audited dependency (the
  `WebPush` NuGet package), or implement it in about 200 lines with `System.Security.Cryptography`
  (`ECDiffieHellman`, `AesGcm`, HKDF), tested against the RFC 8291 test vectors. Prefer implementing it,
  since the project keeps dependencies minimal.
- **Payload:** `{title, body?, pane, kind, promptId?}`. The body is included only if "Include message text"
  is on. Use `tag` = pane id so a newer push replaces the older one for that agent.
- **Actions:** on Android, notifications get **Allow** and **Deny** buttons for structured prompts. They
  post to `/api/live` through the service worker with the device cookie. iOS web push has no action buttons,
  so tapping opens the prompt card.
- **Privacy note in the README:** the only outbound requests are to the push endpoint the phone's browser
  gave (Google FCM or Apple), and only when push is on.

## 7. Delivery plan

Each PR is shippable and tested. Estimates are for one developer who knows the codebase.

### Phase 0: Foundations (about 1 week)
0. **Spike S1 (half a day, no PR):** install a test `PermissionRequest` hook that sleeps 60 s and then
   allows. Check whether Claude Code's TUI dialog appears during the sleep, what happens when you answer in
   the TUI first, and what happens at the timeout. The result picks between the two policies in §5A.
1. **PR: `WorkspaceJson`/`InboxJson` refactor and EventHub.** Factor the JSON builders out of
   `CommandRouter` and add `EventHub` with debounced `workspace.changed`, `inbox.changed` and `notice.*`.
   Unit tests for debounce and payloads.
2. **PR: pipe subscriptions.** `events.subscribe`, a server-side write lock and bounded queue, a `PipeClient`
   reader thread, and `bell events --follow [--json]`. Tests: CliTests round trip; slow consumer → overflow.
3. **PR: OutputTap and `terminal.attach`.** Sequenced output, the ring buffer, the `snap` handshake in
   `terminal.js`, unloaded and not-ready snapshots, and `bell attach <pane>` (read-only). Smoke test: attach,
   `bell send "echo hi"`, the stream contains `hi`, and seqs are contiguous after the snapshot.

### Phase 1: Phone view, read and respond, on Tailscale or localhost (about 2 to 3 weeks)
4. **PR: `Bellboard.Remote` skeleton.** Kestrel on 127.0.0.1, an embedded `wwwroot`, a pipe bridge,
   `/api/health`, build and package integration (`build.ps1`, `release.yml`, `install.ps1`), and the
   sidecar lifecycle in `MuxController` (job object, restart with backoff). Record the zip-size change in the
   PR.
5. **PR: pairing and devices.** One-time codes, device store (DPAPI), cookie auth, `Origin`/CSRF checks,
   rate limits, `bell remote pair|devices|revoke`, and the Settings › Remote page with the QR code (encoded
   in-process; QRCoder is MIT, or a tiny vendored encoder). Tests: an expired, reused or brute-forced code
   fails; a revoked token closes its sockets.
6. **PR: permission levels.** The sidecar allowlist plus the `origin/level` re-check in `CommandRouter`.
   Table-driven tests over every method × level.
7. **PR: PWA v1.** The Inbox, Agent, Workspaces and Notifications screens, the live WebSocket with resync,
   the composer and key bar, the keystroke prompt parser (§5B), and the manifest and service worker for
   installing. Manual test matrix: iPhone Safari (installed PWA), Android Chrome, desktop Chrome.
8. **PR: Tailscale setup.** Detect `tailscale.exe`, read `tailscale status --json` for the MagicDNS name,
   run `tailscale serve` after confirmation, and show the URL. Docs: README section and SECURITY.md update.

### Phase 2: Live terminal on the phone (about 1 to 2 weeks)
9. **PR: terminal screen.** xterm.js attach, fit-to-width plus pinch zoom and pan, read-only by default, a
   **Type** mode with the key bar, coalesced output, and resync on overflow or reconnect.
10. **PR: desktop indicators and audit log.** The status bar phone glyph, remote action log lines, and
    `remote.log`.

### Phase 3: Push and structured approvals (about 2 weeks)
11. **PR: Web Push.** VAPID, RFC 8291 encryption (with test vectors), subscribe and unsubscribe, the
    visibility rule, DND and mute, the message-text setting, and service worker handlers including Android
    actions.
12. **PR: `PermissionRequest` hook and pending prompts.** Hook install and uninstall, `agent.permission`
    with a long wait, away detection, `prompt.pending` and `prompt.resolved`, prompt cards on the phone and
    the desktop, and inbox Approve moved off "blind Enter". Tests: allow, deny, timeout falls back to the
    TUI, and the desktop answers first.

### Phase 4: Full access and polish (about 1 to 2 weeks)
13. **PR: New agent and Finish from the phone** (Full level), with the confirmation step-up.
14. **PR: Phone fit** (the opt-in PTY resize while the desktop is idle).
15. **PR: LAN mode** (self-signed certificate, fingerprint in the QR code, firewall rule guidance).
16. **Optional: passkey step-up** (WebAuthn) for Full actions.

### Later (separate plans)
- **Claude Code conversation view:** render the session's `transcript_path` JSONL as a chat on the phone,
  which reads better than a TUI on a small screen.
- **Native wrapper:** Capacitor around the same PWA, if PWA limits get in the way (iOS push actions,
  background reconnect).
- **Remote agents** (roadmap): the same sidecar protocol pointed the other way, with a headless
  `bellboard-remote` on a Linux box reporting into Bellboard as feeds.
- **Relay:** an end-to-end encrypted relay (Noise) for people who can't use Tailscale. It needs hosted
  infrastructure, so only if users ask for it.

**Total for Phases 0 to 3, the useful product:** about 6 to 8 weeks of focused work.

## 8. Testing

- **Unit (`tests/Bellboard.Tests`):** EventHub debounce; the ring buffer and seq gaps; the snapshot-then-
  stream merge; the pipe subscription framing; pairing codes (expiry, reuse, lockout); token hashing; the
  permission matrix (every method × level); `PromptParser` against recorded screens from Claude Code,
  Codex and Pi; Web Push encryption against the RFC 8291 vectors; the `PermissionRequest` decision mapping.
- **Smoke (`tests/smoke.ps1`):** start an isolated instance (`BELLBOARD_PIPE`, `BELLBOARD_HOME`), run `bell
  remote enable --port 0`, pair with `bell remote pair --json`, `curl` `/api/state` with the cookie, open
  the WebSocket from a PowerShell `ClientWebSocket`, attach, `bell send`, and check the output arrives. Then
  revoke the device, check the socket closes and `/api/state` returns 401, and kill the sidecar to check it
  restarts.
- **Security checks:** a cross-origin `POST` is rejected; a WebSocket from a foreign Origin is rejected; a
  Respond-level device can't `send` to a non-agent pane; pairing brute force locks; no token appears in the
  logs (grep the log in the smoke test).
- **Manual:** a device matrix (iOS Safari as an installed PWA, Android Chrome, a desktop browser), a flaky
  network (switching from Wi-Fi to cellular through Tailscale), 10 agents streaming at once, a 50k-line
  scrollback snapshot, and battery and data use over 1 hour connected.
- **Performance budgets:** attach to first frame under 500 ms on the LAN for 10k lines of scrollback; no
  measurable change to desktop terminal throughput while a phone is attached (benchmark `cat` of a 50 MB
  file with and without a viewer); a sidecar idle memory target of about 40 MB.

## 9. Files touched (summary)

| Area | Files |
|---|---|
| App | `Ipc/PipeServer.cs` (subscriptions, write lock), `Ipc/CommandRouter.cs` (JSON helpers, new methods, level check), `Panes/TerminalPane.cs` (OutputTap, snap), `web/terminal.js` (seq, `snap`), `Services/EventHub.cs` (new), `Services/NotificationCenter.cs` (events), `Core/MuxController.cs` (sidecar lifecycle), `Core/PromptParser.cs` (new), `Services/Config.cs` (`remote.*`), `SettingsWindow.xaml(.cs)` (Remote page), `MainWindow.xaml` (status bar glyph) |
| Shared | `Shared/Protocol.cs` (`PipeClient` subscriptions, new constants) |
| CLI | `Program.cs` (`events`, `attach`, `remote …`), `Hooks.cs` (`PermissionRequest`), `McpServer.cs` (optional: `wait_for_event` tool) |
| New | `src/Bellboard.Remote/` (Kestrel host, auth, devices, push, `wwwroot/` PWA) |
| Build | `Bellboard.sln`, `build.ps1`, `.github/workflows/*.yml`, `packaging/install.ps1` (firewall note for LAN mode only), `THIRD_PARTY_NOTICES.md` |
| Docs | `README.md` (Remote section, privacy text), `SECURITY.md`, `docs/ROADMAP.md`, `CHANGELOG.md` |

## 10. Open questions

1. **Tailscale-first:** is it acceptable for the recommended path to depend on Tailscale (free for personal
   use), with LAN and own-tunnel modes as the alternatives?
2. **Sidecar size:** is roughly +15 to 25 MB in the release zip for ASP.NET Core acceptable? If not, the
   fallback is a minimal WebSocket and HTTP server on raw `TcpListener` with `SslStream`. That's more code
   to own and to audit.
3. **Default permission level** for new devices: Respond (proposed) or View.
4. **Away detection:** "Bellboard window not active for 5 minutes, or the screen locked" (proposed), or an
   explicit **Away** toggle on the phone and in the tray, or both.
5. **Claude Code hook behavior:** spike S1 settles whether the TUI dialog runs in parallel with a pending
   `PermissionRequest` hook. The rest of the hook contract is confirmed (§5A).
