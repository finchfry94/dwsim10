import type { Notification, SampleFile } from "./types";

/** Why an engine call failed: the shape `dwsim-bridge`'s BridgeError serializes to. */
export class EngineError extends Error {
  constructor(
    readonly kind: "spawn" | "disconnected" | "timeout" | "rpc" | "transport",
    message: string,
    readonly code?: number,
  ) {
    super(message);
    this.name = "EngineError";
  }

  static from(value: unknown): EngineError {
    if (value instanceof EngineError) return value;
    if (value && typeof value === "object" && "message" in value) {
      const v = value as { kind?: EngineError["kind"]; message: string; code?: number };
      return new EngineError(v.kind ?? "rpc", v.message, v.code);
    }
    return new EngineError("transport", String(value));
  }
}

/** How the frontend reaches the engine: Tauri commands in the app, HTTP in a browser. */
export interface Transport {
  readonly kind: "tauri" | "http";
  call<T>(method: string, params?: Record<string, unknown>): Promise<T>;
  /** Subscribes to engine notifications; returns the unsubscribe function. */
  listen(handler: (n: Notification) => void): () => void;
  samples(): Promise<SampleFile[]>;
}

export const isTauri = (): boolean => typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

function tauriTransport(): Transport {
  return {
    kind: "tauri",
    async call<T>(method: string, params: Record<string, unknown> = {}) {
      const { invoke } = await import("@tauri-apps/api/core");
      try {
        return await invoke<T>("engine_call", { method, params });
      } catch (e) {
        throw EngineError.from(e);
      }
    },
    listen(handler) {
      let stop: (() => void) | undefined;
      let stopped = false;
      void import("@tauri-apps/api/event").then(({ listen }) =>
        listen<Notification>("engine://notification", (e) => handler(e.payload)).then((unlisten) => {
          if (stopped) unlisten();
          else stop = unlisten;
        }),
      );
      return () => {
        stopped = true;
        stop?.();
      };
    },
    async samples() {
      const { invoke } = await import("@tauri-apps/api/core");
      return invoke<SampleFile[]>("reference_flowsheets");
    },
  };
}

function httpTransport(base = ""): Transport {
  return {
    kind: "http",
    async call<T>(method: string, params: Record<string, unknown> = {}) {
      let body: { result?: T; error?: unknown };
      try {
        const res = await fetch(`${base}/rpc`, {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ method, params }),
        });
        body = await res.json();
      } catch (e) {
        throw new EngineError("transport", `The engine server is not reachable (${String(e)}).`);
      }
      if (body.error !== undefined) throw EngineError.from(body.error);
      return body.result as T;
    },
    listen(handler) {
      const source = new EventSource(`${base}/events`);
      source.onmessage = (e) => handler(JSON.parse(e.data) as Notification);
      return () => source.close();
    },
    async samples() {
      const res = await fetch(`${base}/samples`);
      return res.ok ? ((await res.json()) as SampleFile[]) : [];
    },
  };
}

export const transport: Transport = isTauri() ? tauriTransport() : httpTransport();
