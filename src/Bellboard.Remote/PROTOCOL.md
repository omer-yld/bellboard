# Bellboard Remote protocol

The contract between the phone app (PWA, `wwwroot/`), the sidecar (`bellboard-remote.exe`) and Bellboard
(`Bellboard.exe`, reached by the sidecar over the named pipe). All JSON, UTF-8, camelCase.

```
PWA ──HTTPS/WSS──▶ bellboard-remote.exe ──named pipe──▶ Bellboard.exe (CommandRouter)
```

## Auth

- A device pairs once: `POST /api/pair {code, name}` with the one-time code shown on the desktop (Settings ›
  Remote, or `bell remote pair`). The pairing URL is `https://<host>/#pair=<CODE>`; the PWA reads the
  fragment, asks for a device name (default from the user agent: "iPhone", "Android phone", "Chrome on
  Windows"…) and posts it. On success the response sets the `bb_device` cookie (`Secure` except on
  localhost, `HttpOnly`, `SameSite=Strict`, `Path=/api`, 400 days) and returns `{device}`.
- Every other `/api/*` call needs the cookie; without it (or after revocation) → `401 {"error":"not paired"}`.
- Requests that change something (`POST`, `DELETE`, the WebSocket upgrade) must carry an `Origin` equal to
  the page's own origin, else `403`.
- Errors: `{"error":"<message>"}` with 4xx/5xx.

## Permission levels

`view` < `respond` < `full`. A device's level is set on the desktop. Calls above the level fail with
`403 {"error":"needs <level> access"}` (REST) or `{"t":"result","ok":false,"error":"needs <level> access"}` (WS).

| Method | Level | Params → result |
|---|---|---|
| `workspace.list` | view | `{}` → `{selected, workspaces:[…]}` (same shape as `state.workspaces`) |
| `agent.inbox` | view | `{}` → `[InboxRow]` |
| `agent.info` | view | `{workspace}` → `{task, branch, base, worktree, ahead, diffstat, uncommitted:[…], repoBranch, repoDirty, gh, origin}` |
| `notification.list` | view | `{}` → `[Notice]` |
| `log.list` | view | `{workspace, limit?}` → `[{at, level, text}]` |
| `terminal.read` | view | `{pane, lines?}` → `{pane, text}` |
| `agent.prompt` | view | `{pane}` → `Prompt` (below) |
| `agent.answer` | respond | see "Answering prompts" |
| `terminal.send` | respond¹ | `{pane, text, enter?}` → `{pane, bytes}` (max 4096 chars) |
| `terminal.send-key` | respond¹ | `{pane, keys:["enter"\|"esc"\|"ctrl+c"\|"tab"\|"shift+tab"\|"up"\|"down"\|"left"\|"right"\|"backspace"\|…]}` |
| `notification.read` | respond | `{id}` or `{pane}` → `true` (marks one notice, or all of a pane's, read) |
| `notification.clear` | respond | `{}` → `true` (all read) |
| `notification.dnd` | respond | `{on}` → `{doNotDisturb}` |
| `workspace.mute` | respond | `{workspace, on}` → `{workspace, muted}` |
| `agent.recent-repos` | full | `{}` → `[{path, name}]` |
| `agent.new` | full | `{task, agent?:"claude"\|"codex"\|"opencode"\|"pi", repo, base?}` → `{workspace, pane, branch, worktree, base}` |
| `agent.finish` | full | `{workspace, mode:"merge"\|"pr"\|"keep"\|"discard", force?}` → `{result}` |
| `terminal.fit` | full | `{pane, cols, rows}` or `{pane, off:true}` → `{pane, cols, rows, fitted}` ("Phone fit": resize the shell to the phone while the desk is idle) |

¹ With `respond`, only panes that run an agent (`pane.agent != null`, or the pane of an agent workspace).
With `full`, any terminal.

## REST

| Request | Auth | Response |
|---|---|---|
| `GET /api/health` | none | `{ok:true, version, paired:<cookie valid>}` |
| `POST /api/pair` `{code, name}` | none | `{device:{id, name, level}}` + cookie. Wrong/expired code → `400 {"error":"…"}`; too many attempts → `429` |
| `POST /api/logout` | cookie | `{ok:true}`: forgets this device (revokes it) and clears the cookie |
| `GET /api/state` | cookie | `State` |
| `POST /api/call` `{method, params}` | cookie | the method's result, or `{error}` with 4xx. Used by the service worker (notification actions) and as a fallback when the socket is down. |
| `GET /api/push/key` | cookie | `{publicKey}` (VAPID, base64url) or `{publicKey:null}` when push is off on the desktop |
| `POST /api/push/subscribe` `PushSubscription.toJSON()` | cookie | `{ok:true}` |
| `DELETE /api/push/subscribe` | cookie | `{ok:true}` |
| `GET /api/live` (WebSocket upgrade) | cookie | the live channel below |

## Live channel (`/api/live`, WebSocket, text frames, one JSON object each)

Client → server:

| Message | Meaning |
|---|---|
| `{"t":"hello","v":1}` | first message; the server answers with `state` |
| `{"t":"call","id":7,"method":"…","params":{…}}` | → `{"t":"result","id":7,"ok":true,"result":…}` or `{"t":"result","id":7,"ok":false,"error":"…"}` |
| `{"t":"attach","pane":"p3","scrollback":2000}` | start mirroring a terminal |
| `{"t":"detach","pane":"p3"}` | stop |
| `{"t":"visible","on":true}` | the page is (not) on screen; pushes are held back while a device is visible |
| `{"t":"ping"}` | → `{"t":"pong"}` (send every 25 s) |

Server → client:

| Message | Meaning |
|---|---|
| `{"t":"state", …State}` | full state; after `hello` and then whenever something changes (at most ~5 per second) |
| `{"t":"notice","notice":Notice}` | a new notification (show an in-app banner) |
| `{"t":"snapshot","pane":"p3","data":"<VT>","cols":120,"rows":30,"seq":41}` | the terminal's screen and scrollback, serialized as escape sequences: write it into a fresh xterm of `cols`×`rows` |
| `{"t":"out","pane":"p3","d":"<VT>","seq":42}` | new output after the snapshot, in order; `seq` increases by 1 per message |
| `{"t":"resize","pane":"p3","cols":100,"rows":30}` | the desktop pane changed size |
| `{"t":"exit","pane":"p3"}` | the pane closed |
| `{"t":"overflow","pane":"p3"}` | the phone fell behind; send `attach` again to get a fresh snapshot |
| `{"t":"error","error":"…"}` | e.g. attach to an unknown pane |
| `{"t":"revoked"}` | the device was revoked; the socket closes, the PWA returns to pairing |

The socket is meant to be open only while the page is visible; reconnect with backoff (1 s, 2 s, 5 s, 10 s),
send `hello`, re-`attach` what was open.

## Shapes

```ts
type State = {
  version: string;                       // Bellboard version
  now: string;                           // the PC's clock (ISO); relative times ("4m") are measured against it
  device: { id: string; name: string; level: "view" | "respond" | "full" };
  dnd: boolean;                          // do not disturb
  unread: number;                        // unread notifications
  workspaces: Workspace[];
  inbox: InboxRow[];                     // sorted: needs input first (longest waiting first), working, done, idle
  notifications: Notice[];               // newest first, at most 50
};

type Workspace = {
  id: string; index: number; name: string; selected: boolean;
  cwd: string | null; branch: string | null; ports: number[];
  unread: number; notice: string | null; muted: boolean; pinned: boolean;
  badge: string;                         // "PS" | "CMD" | "WSL" | "GIT" | "SSH" | feed badge …
  status: string | null;                 // agent line shown in the sidebar, e.g. "Claude · needs input"
  agentTask: { task: string; agent: string; branch: string; base: string } | null;
  pills: { key: string; text: string; color: string | null }[];
  progress: { value: number; label: string | null } | null;    // 0..1
  logs: { at: string; level: "info" | "success" | "warning" | "error" | "progress"; text: string }[]; // last 5
  panes: Pane[];
};

type Pane = {
  id: string; kind: "terminal" | "browser"; title: string | null; cwd: string | null;
  focused: boolean; active: boolean; attention: boolean;
  agent: { kind: string; status: "idle" | "working" | "waiting" | "done" } | null;
  feed: boolean; shell: string | null;   // "pwsh", "wsl", "feed", …
  size: string | null;                   // "120x30"
  branch: string | null;
};

type InboxRow = {
  pane: string; workspace: string; workspaceName: string;
  title: string;                          // the task, or "Claude Code in api"
  status: "waiting" | "working" | "done" | "idle" | "running";
  since: string | null;                   // ISO time the status started
  message: string | null;                 // what the agent asked or said last
  where: string;                          // "Claude Code · api · ⎇ fix-login · p3"
  agent: string | null;                   // "Claude Code", "Codex", …
  feed: boolean;
  prompt: PendingPrompt | null;           // a structured permission request waiting for an answer
};

type PendingPrompt = {
  id: string;
  tool: string;                           // "Bash", "Edit", "mcp__github__create_issue" …
  summary: string;                        // one line: the command, the file path…
  detail: string | null;                  // more lines (diff excerpt, full command), may be long
  canAlwaysAllow: boolean;                // offer "Allow always"
  since: string;
};

type Notice = {
  id: string; at: string; workspace: string; workspaceName: string; pane: string;
  title: string; body: string; source: string; read: boolean;
};

type Prompt = {                           // agent.prompt
  pane: string;
  hash: string;                           // of `text`; pass back to agent.answer {choice}
  text: string;                           // the last ~30 lines of the terminal
  question: string | null;                // "Do you want to proceed?"
  choices: { key: string; label: string; selected: boolean }[];   // parsed numbered options
  pending: PendingPrompt | null;
};
```

## Answering prompts (`agent.answer`, respond)

- Structured permission request: `{pane, promptId, decision:"allow"|"deny"|"always", message?}`.
  → `{answered:true}`; fails if the prompt was already answered (e.g. on the desktop).
- Numbered choice on screen: `{pane, hash, choice:"2"}`. Bellboard re-reads the screen and types the
  choice only if it still matches `hash`; otherwise fails with `"the screen changed; look again"`.
- Free text: `{pane, text, enter:true}` (same as `terminal.send`).

## Push notifications

Web Push (RFC 8030/8291/8292). Payload (JSON, encrypted):

```json
{"title":"Claude Code · api","body":"Claude wants to run: npm test","pane":"p3","workspace":"w2",
 "kind":"prompt","promptId":"q4","tag":"p3","url":"/#/pane/p3"}
```

`kind` is `"prompt"` (a pending permission request: Android shows **Deny**, plus **Allow** when `canAllow` is
true, i.e. the desktop allows message text so the notification shows what it allows; the actions call
`POST /api/call {method:"agent.answer", params:{pane, promptId, decision}}`) or `"notice"`. `body` is a
generic line ("Needs your input") unless the desktop allows message text in pushes. `tag` = pane id, so a
newer push replaces the older one for the same agent.

## Pipe methods used only by the sidecar

Called over `\\.\pipe\bellboard-<user>` by `bellboard-remote.exe` (the app checks the caller is the sidecar
it started):

| Method | |
|---|---|
| `remote.hello {pid}` | → `{port, bind, hosts:[…], vapidPublic, vapidPrivate, push, pushText, version}` |
| `remote.pair {code, name, ip}` | → `{device:{id,name,level}, token}` |
| `remote.auth {token, ip}` | → `{id, name, level}` (also records "last seen") |
| `remote.logout {device}` | revoke |
| `remote.connected {devices:[id…]}` | which devices have a socket open (status bar) |
| `remote.push.subscribe {device, subscription}` / `remote.push.unsubscribe {device}` | |
| `remote.push.targets {}` | → `[{device, subscription}]` |
| `remote.push.gone {device}` | the push service said the subscription expired |
| `state.get {device}` | → `State` |
| `events.subscribe {topics:["state","notice","prompt","remote"]}` | pushes `{"sub","event","data"}` lines |
| `terminal.attach {pane, scrollback}` / `unsubscribe {sub}` | terminal mirroring over the pipe |

Every forwarded call carries `"remoteDevice":"<id>"` in its params; Bellboard looks up that device's
level and refuses methods above it, independently of the sidecar's own check.
