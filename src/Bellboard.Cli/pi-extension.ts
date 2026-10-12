// Bellboard for pi: shows what pi is doing on its Bellboard workspace card and in the inbox (working,
// waiting for your answer, done), and lets Bellboard resume the session after a restart.
// Installed by `bell hooks install`; does nothing outside a Bellboard pane. Remove with `bell hooks uninstall`.
import { spawn } from "node:child_process";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

const BELL: string = "__BELL__";

function report(event: string, payload: Record<string, unknown>): void {
  if (!process.env.BELLBOARD_PANE_ID && !process.env.WMUX_PANE_ID) return;
  try {
    const child = spawn(BELL, ["hook", "pi", event], { stdio: ["pipe", "ignore", "ignore"], windowsHide: true });
    child.on("error", () => {});
    child.stdin?.on("error", () => {});
    child.stdin?.end(JSON.stringify(payload));
  } catch {
    // Reporting must never get in pi's way.
  }
}

function sessionId(ctx: { sessionManager: { getSessionId(): string } }): string | undefined {
  try {
    return ctx.sessionManager.getSessionId();
  } catch {
    return undefined;
  }
}

function lastAssistantText(messages: unknown): string | undefined {
  if (!Array.isArray(messages)) return undefined;
  for (let i = messages.length - 1; i >= 0; i--) {
    const m = messages[i] as { role?: string; content?: unknown };
    if (m?.role !== "assistant") continue;
    if (typeof m.content === "string" && m.content.trim()) return m.content;
    if (Array.isArray(m.content)) {
      const text = m.content
        .map((c: { type?: string; text?: unknown }) => (c?.type === "text" && typeof c.text === "string" ? c.text : ""))
        .join(" ")
        .trim();
      if (text) return text;
    }
  }
  return undefined;
}

export default function (pi: ExtensionAPI) {
  let lastReply: string | undefined;
  pi.on("session_start", (event, ctx) => {
    report("session_start", { session_id: sessionId(ctx), cwd: ctx.cwd, reason: event.reason });
  });
  pi.on("before_agent_start", (event, ctx) => {
    report("agent_start", { session_id: sessionId(ctx), prompt: event.prompt });
  });
  pi.on("ui_prompt_start", (event, ctx) => {
    report("ui_prompt_start", { session_id: sessionId(ctx), message: event.title });
  });
  pi.on("ui_prompt_end", (_event, ctx) => {
    report("ui_prompt_end", { session_id: sessionId(ctx) });
  });
  pi.on("agent_end", (event) => {
    lastReply = lastAssistantText(event.messages);
  });
  pi.on("agent_settled", (event, ctx) => {
    report("agent_settled", { session_id: sessionId(ctx), aborted: event.aborted, last_assistant_message: lastReply });
    lastReply = undefined;
  });
  pi.on("session_shutdown", (_event, ctx) => {
    report("session_shutdown", { session_id: sessionId(ctx) });
  });
}
