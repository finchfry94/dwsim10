import { create } from "zustand";
import { engine } from "../engine/api";
import { EngineError, transport } from "../engine/transport";
import type { Finding, FlowsheetSummary, Graph, GraphNode, History, Notification } from "../engine/types";
import type { ColorMode } from "../canvas/coloring";

export type EngineStatus = "starting" | "ready" | "stopped";

export interface LogEntry {
  id: number;
  time: Date;
  level: "info" | "warning" | "error" | "tip";
  text: string;
}

export type Alignment = "left" | "center" | "right" | "top" | "middle" | "bottom" | "spreadX" | "spreadY";

interface AppState {
  engineStatus: EngineStatus;
  flowsheet: FlowsheetSummary | null;
  graph: Graph | null;
  /** The object the inspector shows: the selection when exactly one object is selected. */
  selection: string | null;
  /** Every selected object. */
  selected: string[];
  history: History | null;
  /** What Copy took, as the engine's clipboard XML; pastes into any open flowsheet. */
  clipboard: string | null;
  /** The latest solve.check, or null before the first. */
  findings: Finding[] | null;
  bottomTab: "log" | "diagnostics" | "streams" | "sheet";
  palette: boolean;
  /** The analysis dialog, on one of its tabs, for a stream when opened from one. */
  analysis: { tab: "phase" | "binary"; streamId?: string } | null;
  report: boolean;
  /** The tools dialog, on a tool, with an object to apply it to when opened from one. */
  tools: { toolId?: string; objectId?: string } | null;
  reactions: boolean;
  /** The studies dialog, on its sensitivity or optimizer tab. */
  studies: "sensitivity" | "optimizer" | "scenarios" | null;
  scripts: boolean;
  dynamics: boolean;
  /** The running integration's progress, from dynamics/progress notifications. */
  dynamicsProgress: { seconds: number; total: number | null; text: string } | null;
  /** The running study's progress, from the engine's study/progress notifications. */
  studyProgress: { done: number; total: number; text: string } | null;
  shortcuts: boolean;
  /** Recently opened or saved files, newest first. */
  recent: string[];
  /** An object the canvas should bring into view; `seq` makes a repeat request count. */
  focus: { id: string; seq: number } | null;
  solving: boolean;
  busy: boolean;
  colorMode: ColorMode;
  showStreamTags: boolean;
  logs: LogEntry[];
  /** Bumped after anything that may change object data, so open views re-read it. */
  revision: number;
  /** The setup dialog: the new-simulation wizard, the setup editor, or closed. */
  setup: "new" | "edit" | null;
  /** Solve after every edit, as the Avalonia app does; off for large flowsheets. */
  autoSolve: boolean;

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
  /** Selects one object; `toggle` adds or removes it from the selection instead. */
  select(id: string | null, toggle?: boolean): void;
  setSelected(ids: string[]): void;
  selectAll(): void;
  undo(): Promise<void>;
  redo(): Promise<void>;
  copy(): Promise<void>;
  paste(): Promise<void>;
  duplicate(): Promise<void>;
  deleteSelected(): Promise<void>;
  moveMany(moves: { id: string; x: number; y: number }[]): Promise<void>;
  align(how: Alignment): Promise<void>;
  transform(change: { rotate?: number; flipH?: boolean; flipV?: boolean }): Promise<void>;
  cancelSolve(): Promise<void>;
  check(): Promise<void>;
  setBottomTab(tab: AppState["bottomTab"]): void;
  /** Selects an object and brings it into view. */
  focusObject(id: string): void;
  openPalette(open: boolean): void;
  openAnalysis(analysis: AppState["analysis"]): void;
  openReport(open: boolean): void;
  openTools(tools: AppState["tools"]): void;
  openReactions(open: boolean): void;
  openStudies(tab: AppState["studies"]): void;
  openScripts(open: boolean): void;
  openDynamics(open: boolean): void;
  openShortcuts(open: boolean): void;
  /** True when there are no unsaved changes, or the user agrees to lose them. */
  confirmDiscard(): boolean;
  openSetup(mode: "new" | "edit" | null): void;
  setAutoSolve(on: boolean): void;
  /** Call after any edit: re-reads the flowsheet, then solves if auto-solve is on (debounced). */
  afterEdit(): Promise<void>;
  setColorMode(mode: ColorMode): void;
  toggleStreamTags(): void;
  log(level: LogEntry["level"], text: string): void;
  clearLog(): void;
  refresh(): Promise<void>;
}

let logId = 0;
const MAX_LOG = 500;

/** Edits closer together than this share one solve. */
const AUTO_SOLVE_DELAY_MS = 400;
/** The JSON-RPC code of a cancelled request. */
const CANCELLED = -32800;
let solveRequest: string | undefined;
const MAX_RECENT = 8;

function readRecent(): string[] {
  try {
    const list = JSON.parse(localStorage.getItem("dwsim.recent") ?? "[]");
    return Array.isArray(list) ? list.filter((p): p is string => typeof p === "string") : [];
  } catch {
    return [];
  }
}

function remember(path: string, recent: string[]): string[] {
  const next = [path, ...recent.filter((p) => p !== path)].slice(0, MAX_RECENT);
  try {
    localStorage.setItem("dwsim.recent", JSON.stringify(next));
  } catch {
    /* storage unavailable */
  }
  return next;
}

/** New positions that line the nodes up, or spread them evenly. */
export function aligned(nodes: GraphNode[], how: Alignment): { id: string; x: number; y: number }[] {
  if (nodes.length < 2) return [];
  const left = Math.min(...nodes.map((n) => n.x));
  const right = Math.max(...nodes.map((n) => n.x + n.width));
  const top = Math.min(...nodes.map((n) => n.y));
  const bottom = Math.max(...nodes.map((n) => n.y + n.height));
  const spread = (axis: "x" | "y") => {
    const size = axis === "x" ? "width" : "height";
    const sorted = [...nodes].sort((a, b) => a[axis] + a[size] / 2 - (b[axis] + b[size] / 2));
    const first = sorted[0]!;
    const last = sorted[sorted.length - 1]!;
    const from = first[axis] + first[size] / 2;
    const step = (last[axis] + last[size] / 2 - from) / (sorted.length - 1);
    return sorted.map((n, i) => ({
      id: n.id,
      x: axis === "x" ? Math.round(from + i * step - n.width / 2) : n.x,
      y: axis === "y" ? Math.round(from + i * step - n.height / 2) : n.y,
    }));
  };
  return nodes.length > 2 && how === "spreadX"
    ? spread("x")
    : nodes.length > 2 && how === "spreadY"
      ? spread("y")
      : nodes.map((n) => ({
          id: n.id,
          x:
            how === "left"
              ? left
              : how === "right"
                ? right - n.width
                : how === "center"
                  ? Math.round((left + right) / 2 - n.width / 2)
                  : n.x,
          y:
            how === "top"
              ? top
              : how === "bottom"
                ? bottom - n.height
                : how === "middle"
                  ? Math.round((top + bottom) / 2 - n.height / 2)
                  : n.y,
        }));
}
let autoSolveTimer: ReturnType<typeof setTimeout> | undefined;
/** An edit arrived while a solve ran; solve once more when it finishes. */
let solveAgain = false;

function readAutoSolve(): boolean {
  try {
    return localStorage.getItem("dwsim.autoSolve") !== "false";
  } catch {
    return true;
  }
}

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

  /** Takes a graph from the engine: its history, and a selection pruned to what still exists. */
  function setGraph(graph: Graph, selected?: string[]) {
    set((s) => {
      const ids = new Set(graph.nodes.map((n) => n.id));
      const keep = (selected ?? s.selected).filter((id) => ids.has(id));
      return {
        graph,
        history: graph.history ?? s.history,
        selected: keep,
        selection: keep.length === 1 ? keep[0]! : null,
      };
    });
  }

  async function load(summary: FlowsheetSummary | undefined) {
    if (!summary) return;
    const previous = get().flowsheet;
    const graph = await engine.graph(summary.id);
    set((s) => ({
      flowsheet: summary,
      graph,
      history: graph.history ?? null,
      selection: null,
      selected: [],
      findings: null,
      revision: s.revision + 1,
    }));
    if (previous && previous.id !== summary.id) void engine.closeFlowsheet(previous.id).catch(() => {});
  }

  /** An edit that answers with the graph: take it, re-read open views, solve if auto-solve is on. */
  async function graphEdit(
    what: string,
    action: (id: string) => Promise<Graph>,
    selected?: (g: Graph) => string[],
  ) {
    const id = get().flowsheet?.id;
    if (!id) return;
    const graph = await attempt(what, () => action(id));
    if (!graph) return;
    setGraph(graph, selected?.(graph));
    await get().afterEdit();
  }

  return {
    engineStatus: "starting",
    flowsheet: null,
    graph: null,
    selection: null,
    selected: [],
    history: null,
    clipboard: null,
    findings: null,
    bottomTab: "log",
    analysis: null,
    report: false,
    tools: null,
    reactions: false,
    studies: null,
    studyProgress: null,
    scripts: false,
    dynamics: false,
    dynamicsProgress: null,
    palette: false,
    shortcuts: false,
    recent: readRecent(),
    focus: null,
    solving: false,
    busy: false,
    colorMode: "status",
    showStreamTags: false,
    logs: [],
    revision: 0,
    setup: null,
    autoSolve: readAutoSolve(),

    async newFlowsheet() {
      if (!get().confirmDiscard()) return;
      const summary = await attempt("New flowsheet", () => engine.newFlowsheet());
      await load(summary);
      // A new flowsheet cannot solve anything until it has compounds and a property package.
      if (summary) set({ setup: "new" });
    },

    async open(path) {
      if (!get().confirmDiscard()) return;
      const summary = await attempt("Open", () => engine.openFlowsheet(path));
      await load(summary);
      if (summary) {
        get().log("info", `Opened ${summary.name} (${summary.path ?? path}).`);
        set((s) => ({ recent: remember(summary.path ?? path, s.recent) }));
      }
    },

    async save(path) {
      const result = await attempt("Save", () => engine.saveFlowsheet(requireFlowsheet(), path));
      if (result) {
        get().log("info", `Saved to ${result.path}.`);
        set((s) => ({
          ...(s.flowsheet ? { flowsheet: { ...s.flowsheet, path: result.path } } : {}),
          history: s.history && { ...s.history, modified: false },
          recent: remember(result.path, s.recent),
        }));
      }
    },

    async solve() {
      const id = get().flowsheet?.id;
      if (!id || get().solving) return;
      set({ solving: true });
      const request = `solve-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
      solveRequest = request;
      try {
        const result = await engine.solve(id, request);
        if (result.cancelled) get().log("warning", "Solve cancelled.");
        else if (!result.ok) result.errors.forEach((e) => get().log("error", e));
      } catch (e) {
        const error = EngineError.from(e);
        if (error.code === CANCELLED) get().log("warning", "Solve cancelled.");
        else get().log("error", `Solve: ${error.message}`);
      } finally {
        solveRequest = undefined;
        await get().refresh();
        set({ solving: false });
        if (solveAgain) {
          solveAgain = false;
          void get().solve();
        }
      }
    },

    async autoLayout() {
      await graphEdit("Auto layout", (id) => engine.autoLayout(id));
    },

    async addObject(type, x, y) {
      const node = await attempt("Add object", () => engine.addObject(requireFlowsheet(), type, x, y));
      if (node) {
        await get().refresh();
        set({ selection: node.id, selected: [node.id] });
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
      await get().refresh();
    },

    async connect(source, sourcePort, target, targetPort) {
      const graph = await attempt("Connect", () =>
        engine.connect(requireFlowsheet(), source, sourcePort, target, targetPort),
      );
      if (graph) setGraph(graph);
    },

    async deleteObject(id) {
      await graphEdit("Delete", (fs) => engine.deleteObject(fs, id));
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

    select(id, toggle = false) {
      if (id === null) return set({ selection: null, selected: [] });
      const current = get().selected;
      const selected = toggle
        ? current.includes(id)
          ? current.filter((x) => x !== id)
          : [...current, id]
        : [id];
      set({ selected, selection: selected.length === 1 ? selected[0]! : null });
    },

    setSelected(ids) {
      const same = ids.length === get().selected.length && ids.every((id) => get().selected.includes(id));
      if (!same) set({ selected: ids, selection: ids.length === 1 ? ids[0]! : null });
    },

    selectAll() {
      get().setSelected((get().graph?.nodes ?? []).map((n) => n.id));
    },

    async undo() {
      await graphEdit("Undo", (id) => engine.undo(id));
    },

    async redo() {
      await graphEdit("Redo", (id) => engine.redo(id));
    },

    async copy() {
      const id = get().flowsheet?.id;
      const ids = get().selected;
      if (!id || ids.length === 0) return;
      const result = await attempt("Copy", () => engine.copy(id, ids));
      if (!result) return;
      set({ clipboard: result.text });
      get().log("info", `Copied ${result.count} object${result.count === 1 ? "" : "s"}.`);
    },

    async paste() {
      const text = get().clipboard;
      if (!text) return;
      await graphEdit(
        "Paste",
        (id) => engine.paste(id, text),
        (g) => g.pasted ?? [],
      );
    },

    async duplicate() {
      await get().copy();
      await get().paste();
    },

    async deleteSelected() {
      const ids = get().selected;
      if (ids.length === 0) return;
      await graphEdit(
        "Delete",
        (id) => engine.deleteMany(id, ids),
        () => [],
      );
    },

    async moveMany(moves) {
      if (moves.length === 0) return;
      const at = new Map(moves.map((m) => [m.id, m]));
      // Optimistic: the canvas already shows the nodes where they were dropped.
      set((s) => ({
        graph: s.graph && {
          ...s.graph,
          nodes: s.graph.nodes.map((n) => {
            const m = at.get(n.id);
            return m ? { ...n, x: m.x, y: m.y } : n;
          }),
        },
      }));
      const id = get().flowsheet?.id;
      if (!id) return;
      const graph = await attempt("Move", () => engine.moveMany(id, moves));
      if (graph) setGraph(graph);
    },

    async align(how) {
      const chosen = new Set(get().selected);
      const nodes = (get().graph?.nodes ?? []).filter((n) => chosen.has(n.id));
      await get().moveMany(aligned(nodes, how));
    },

    async transform(change) {
      const ids = get().selected;
      if (ids.length === 0) return;
      await graphEdit("Rotate", (id) => engine.transform(id, ids, change));
    },

    async cancelSolve() {
      if (solveRequest) await engine.cancel(solveRequest).catch(() => {});
    },

    async check() {
      const id = get().flowsheet?.id;
      if (!id) return;
      const findings = await attempt("Check", () => engine.check(id));
      if (findings) set({ findings, bottomTab: "diagnostics" });
    },

    setBottomTab: (bottomTab) => set({ bottomTab }),
    focusObject(id) {
      get().select(id);
      set((s) => ({ focus: { id, seq: (s.focus?.seq ?? 0) + 1 } }));
    },
    openPalette: (palette) => set({ palette }),
    openAnalysis: (analysis) => set({ analysis }),
    openReport: (report) => set({ report }),
    openTools: (tools) => set({ tools }),
    openReactions: (reactions) => set({ reactions }),
    openStudies: (studies) => set({ studies }),
    openScripts: (scripts) => set({ scripts }),
    openDynamics: (dynamics) => set({ dynamics }),
    openShortcuts: (shortcuts) => set({ shortcuts }),

    confirmDiscard() {
      if (!get().history?.modified) return true;
      return window.confirm(`${get().flowsheet?.name ?? "The flowsheet"} has unsaved changes. Discard them?`);
    },
    openSetup: (setup) => set({ setup }),

    setAutoSolve(autoSolve) {
      set({ autoSolve });
      try {
        localStorage.setItem("dwsim.autoSolve", String(autoSolve));
      } catch {
        /* storage unavailable */
      }
    },

    async afterEdit() {
      await get().refresh();
      if (!get().autoSolve) return;
      clearTimeout(autoSolveTimer);
      autoSolveTimer = setTimeout(() => {
        if (get().solving) solveAgain = true;
        else void get().solve();
      }, AUTO_SOLVE_DELAY_MS);
    },
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
      if (graph) {
        setGraph(graph);
        set((s) => ({ revision: s.revision + 1 }));
      }
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
        useApp.setState({
          engineStatus: "stopped",
          flowsheet: null,
          graph: null,
          selection: null,
          selected: [],
          history: null,
        });
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
      case "dynamics/progress":
        if (p.flowsheetId === useApp.getState().flowsheet?.id)
          useApp.setState({
            dynamicsProgress: {
              seconds: Number(p.seconds),
              total: p.total == null ? null : Number(p.total),
              text: String(p.text ?? ""),
            },
          });
        break;
      case "study/progress":
        if (p.flowsheetId === useApp.getState().flowsheet?.id)
          useApp.setState({
            studyProgress: { done: Number(p.done), total: Number(p.total), text: String(p.text ?? "") },
          });
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
