import { create } from "zustand";
import { engine } from "../engine/api";
import { EngineError, transport } from "../engine/transport";
import type { FlowsheetSummary, Graph, Notification } from "../engine/types";
import type { ColorMode } from "../canvas/coloring";

export type EngineStatus = "starting" | "ready" | "stopped";

export interface LogEntry {
  id: number;
  time: Date;
  level: "info" | "warning" | "error" | "tip";
  text: string;
}

interface AppState {
  engineStatus: EngineStatus;
  flowsheet: FlowsheetSummary | null;
  graph: Graph | null;
  selection: string | null;
  solving: boolean;
  busy: boolean;
  colorMode: ColorMode;
  showStreamTags: boolean;
  logs: LogEntry[];
  /** Bumped after anything that may change object data, so open views re-read it. */
  revision: number;

  newFlowsheet(): Promise<void>;
  open(path: string): Promise<void>;
  save(path?: string): Promise<void>;
  solve(): Promise<void>;
  autoLayout(): Promise<void>;
  addObject(type: string, x: number, y: number): Promise<void>;
  moveObject(id: string, x: number, y: number): Promise<void>;
  connect(source: string, sourcePort: string, target: string, targetPort: string): Promise<void>;
  deleteObject(id: string): Promise<void>;
  rename(id: string, tag: string): Promise<void>;
  select(id: string | null): void;
  setColorMode(mode: ColorMode): void;
  toggleStreamTags(): void;
  log(level: LogEntry["level"], text: string): void;
  clearLog(): void;
  refresh(): Promise<void>;
}

let logId = 0;
const MAX_LOG = 500;

export const useApp = create<AppState>((set, get) => {
  /** Runs an engine action, logging a failure instead of throwing into the UI. */
  async function attempt<T>(what: string, action: () => Promise<T>): Promise<T | undefined> {
    set({ busy: true });
    try {
      return await action();
    } catch (e) {
      get().log("error", `${what}: ${EngineError.from(e).message}`);
      return undefined;
    } finally {
      set({ busy: false });
    }
  }

  const requireFlowsheet = () => {
    const fs = get().flowsheet;
    if (!fs) throw new EngineError("rpc", "No flowsheet is open.");
    return fs.id;
  };

  async function load(summary: FlowsheetSummary | undefined) {
    if (!summary) return;
    const previous = get().flowsheet;
    const graph = await engine.graph(summary.id);
    set((s) => ({ flowsheet: summary, graph, selection: null, revision: s.revision + 1 }));
    if (previous && previous.id !== summary.id) void engine.closeFlowsheet(previous.id).catch(() => {});
  }

  return {
    engineStatus: "starting",
    flowsheet: null,
    graph: null,
    selection: null,
    solving: false,
    busy: false,
    colorMode: "status",
    showStreamTags: false,
    logs: [],
    revision: 0,

    async newFlowsheet() {
      await load(await attempt("New flowsheet", () => engine.newFlowsheet()));
    },

    async open(path) {
      const summary = await attempt("Open", () => engine.openFlowsheet(path));
      await load(summary);
      if (summary) get().log("info", `Opened ${summary.name} (${summary.path ?? path}).`);
    },

    async save(path) {
      const result = await attempt("Save", () => engine.saveFlowsheet(requireFlowsheet(), path));
      if (result) {
        get().log("info", `Saved to ${result.path}.`);
        set((s) => (s.flowsheet ? { flowsheet: { ...s.flowsheet, path: result.path } } : {}));
      }
    },

    async solve() {
      const id = get().flowsheet?.id;
      if (!id || get().solving) return;
      set({ solving: true });
      try {
        const result = await attempt("Solve", () => engine.solve(id));
        if (result && !result.ok) result.errors.forEach((e) => get().log("error", e));
        await get().refresh();
      } finally {
        set({ solving: false });
      }
    },

    async autoLayout() {
      const graph = await attempt("Auto layout", () => engine.autoLayout(requireFlowsheet()));
      if (graph) set({ graph });
    },

    async addObject(type, x, y) {
      const node = await attempt("Add object", () => engine.addObject(requireFlowsheet(), type, x, y));
      if (node) {
        await get().refresh();
        set({ selection: node.id });
      }
    },

    async moveObject(id, x, y) {
      const fsId = get().flowsheet?.id;
      if (!fsId) return;
      // Optimistic: the canvas already shows the node where it was dropped.
      set((s) => ({
        graph: s.graph && { ...s.graph, nodes: s.graph.nodes.map((n) => (n.id === id ? { ...n, x, y } : n)) },
      }));
      await attempt("Move", () => engine.moveObject(fsId, id, x, y));
    },

    async connect(source, sourcePort, target, targetPort) {
      const graph = await attempt("Connect", () =>
        engine.connect(requireFlowsheet(), source, sourcePort, target, targetPort),
      );
      if (graph) set({ graph });
    },

    async deleteObject(id) {
      const graph = await attempt("Delete", () => engine.deleteObject(requireFlowsheet(), id));
      if (graph) set((s) => ({ graph, selection: s.selection === id ? null : s.selection }));
    },

    async rename(id, tag) {
      const node = await attempt("Rename", () => engine.rename(requireFlowsheet(), id, tag));
      if (node)
        set((s) => ({
          graph: s.graph && {
            ...s.graph,
            nodes: s.graph.nodes.map((n) => (n.id === id ? { ...n, tag: node.tag } : n)),
          },
        }));
    },

    select: (selection) => set({ selection }),
    setColorMode: (colorMode) => set({ colorMode }),
    toggleStreamTags: () => set((s) => ({ showStreamTags: !s.showStreamTags })),

    log(level, text) {
      set((s) => ({ logs: [...s.logs, { id: ++logId, time: new Date(), level, text }].slice(-MAX_LOG) }));
    },

    clearLog: () => set({ logs: [] }),

    async refresh() {
      const id = get().flowsheet?.id;
      if (!id) return;
      const graph = await attempt("Refresh", () => engine.graph(id));
      if (graph) set((s) => ({ graph, revision: s.revision + 1 }));
    },
  };
});

/** Routes engine notifications into the store. Call once at startup; returns the unsubscribe. */
export function connectNotifications(): () => void {
  const { log } = useApp.getState();
  const handle = (n: Notification) => {
    const p = n.params;
    switch (n.method) {
      case "engine/ready":
        useApp.setState({ engineStatus: "ready" });
        break;
      case "engine/exited":
        useApp.setState({ engineStatus: "stopped", flowsheet: null, graph: null, selection: null });
        log("error", "The engine stopped. Open the flowsheet again to continue; unsaved changes are lost.");
        break;
      case "engine/log":
        if (p.flowsheetId === useApp.getState().flowsheet?.id)
          log((p.level as LogEntry["level"]) ?? "info", String(p.text));
        break;
      case "solve/started":
        useApp.setState({ solving: true });
        break;
      case "solve/finished":
        useApp.setState({ solving: false });
        break;
    }
  };
  const stop = transport.listen(handle);
  // The sidecar may have been ready before we subscribed; a ping settles the status either way.
  engine
    .info()
    .then(() => useApp.setState({ engineStatus: "ready" }))
    .catch((e) => {
      useApp.setState({ engineStatus: "stopped" });
      log("error", `Engine unavailable: ${EngineError.from(e).message}`);
    });
  return stop;
}
