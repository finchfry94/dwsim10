import { useApp, type Alignment } from "../state/store";

/** Something the user can do from the keyboard, the command palette or the shortcut sheet. */
export interface Command {
  id: string;
  label: string;
  group: "File" | "Edit" | "Arrange" | "Solve" | "Results" | "Tools" | "View";
  /** Shortcuts, e.g. "Mod+Z" (Mod is Ctrl, or Cmd on a Mac); the first is the one shown. */
  keys?: string[];
  /** Runs while the focus is in a text field too (save, open, solve, the palette). */
  global?: boolean;
  enabled?: () => boolean;
  run: () => void | Promise<void>;
}

/** What the app shell provides: file dialogs and the theme live there, not in the store. */
export interface ShellActions {
  open: () => void;
  save: (saveAs?: boolean) => void;
  toggleTheme: () => void;
}

const hasFlowsheet = () => useApp.getState().flowsheet !== null;
const hasSelection = () => useApp.getState().selected.length > 0;
const several = (n: number) => () => useApp.getState().selected.length >= n;

export function buildCommands(shell: ShellActions): Command[] {
  const app = () => useApp.getState();
  const align = (how: Alignment, label: string, min = 2): Command => ({
    id: `align.${how}`,
    label,
    group: "Arrange",
    enabled: several(min),
    run: () => app().align(how),
  });
  return [
    { id: "file.new", label: "New flowsheet", group: "File", run: () => app().newFlowsheet() },
    { id: "file.open", label: "Open…", group: "File", keys: ["Mod+O"], global: true, run: shell.open },
    {
      id: "file.save",
      label: "Save",
      group: "File",
      keys: ["Mod+S"],
      global: true,
      enabled: hasFlowsheet,
      run: () => shell.save(),
    },
    {
      id: "file.saveAs",
      label: "Save as…",
      group: "File",
      keys: ["Mod+Shift+S"],
      global: true,
      enabled: hasFlowsheet,
      run: () => shell.save(true),
    },
    {
      id: "file.setup",
      label: "Compounds, property packages and units…",
      group: "File",
      enabled: hasFlowsheet,
      run: () => app().openSetup("edit"),
    },

    {
      id: "edit.undo",
      label: "Undo",
      group: "Edit",
      keys: ["Mod+Z"],
      enabled: () => !!app().history?.canUndo,
      run: () => app().undo(),
    },
    {
      id: "edit.redo",
      label: "Redo",
      group: "Edit",
      keys: ["Mod+Shift+Z", "Mod+Y"],
      enabled: () => !!app().history?.canRedo,
      run: () => app().redo(),
    },
    {
      id: "edit.copy",
      label: "Copy",
      group: "Edit",
      keys: ["Mod+C"],
      enabled: hasSelection,
      run: () => app().copy(),
    },
    {
      id: "edit.paste",
      label: "Paste",
      group: "Edit",
      keys: ["Mod+V"],
      enabled: () => hasFlowsheet() && !!app().clipboard,
      run: () => app().paste(),
    },
    {
      id: "edit.duplicate",
      label: "Duplicate",
      group: "Edit",
      keys: ["Mod+D"],
      enabled: hasSelection,
      run: () => app().duplicate(),
    },
    {
      id: "edit.delete",
      label: "Delete",
      group: "Edit",
      keys: ["Delete", "Backspace"],
      enabled: hasSelection,
      run: () => app().deleteSelected(),
    },
    {
      id: "edit.selectAll",
      label: "Select all",
      group: "Edit",
      keys: ["Mod+A"],
      enabled: hasFlowsheet,
      run: () => app().selectAll(),
    },

    {
      id: "arrange.rotate",
      label: "Rotate 90° clockwise",
      group: "Arrange",
      keys: ["R"],
      enabled: hasSelection,
      run: () => app().transform({ rotate: 90 }),
    },
    {
      id: "arrange.rotateBack",
      label: "Rotate 90° counter-clockwise",
      group: "Arrange",
      keys: ["Shift+R"],
      enabled: hasSelection,
      run: () => app().transform({ rotate: -90 }),
    },
    {
      id: "arrange.flipH",
      label: "Flip horizontally",
      group: "Arrange",
      keys: ["H"],
      enabled: hasSelection,
      run: () => app().transform({ flipH: true }),
    },
    {
      id: "arrange.flipV",
      label: "Flip vertically",
      group: "Arrange",
      keys: ["V"],
      enabled: hasSelection,
      run: () => app().transform({ flipV: true }),
    },
    align("left", "Align left edges"),
    align("center", "Align centres horizontally"),
    align("right", "Align right edges"),
    align("top", "Align top edges"),
    align("middle", "Align centres vertically"),
    align("bottom", "Align bottom edges"),
    align("spreadX", "Spread evenly across", 3),
    align("spreadY", "Spread evenly down", 3),
    {
      id: "arrange.layout",
      label: "Arrange automatically",
      group: "Arrange",
      enabled: hasFlowsheet,
      run: () => app().autoLayout(),
    },

    {
      id: "solve.run",
      label: "Solve",
      group: "Solve",
      keys: ["F5"],
      global: true,
      enabled: () => hasFlowsheet() && !app().solving,
      run: () => app().solve(),
    },
    {
      id: "solve.cancel",
      label: "Cancel solve",
      group: "Solve",
      keys: ["Escape"],
      global: true,
      enabled: () => app().solving,
      run: () => app().cancelSolve(),
    },
    {
      id: "solve.check",
      label: "Check the flowsheet",
      group: "Solve",
      keys: ["F7"],
      global: true,
      enabled: hasFlowsheet,
      run: () => app().check(),
    },
    {
      id: "solve.auto",
      label: "Turn auto-solve on or off",
      group: "Solve",
      run: () => app().setAutoSolve(!app().autoSolve),
    },

    {
      id: "results.streams",
      label: "Stream table",
      group: "Results",
      keys: ["Mod+Shift+T"],
      enabled: hasFlowsheet,
      run: () => app().setBottomTab("streams"),
    },
    {
      id: "results.phase",
      label: "Phase envelope…",
      group: "Results",
      enabled: hasFlowsheet,
      run: () => {
        const chosen = app().graph?.nodes.find(
          (n) => n.id === app().selection && n.type === "MaterialStream",
        );
        app().openAnalysis({ tab: "phase", streamId: chosen?.id });
      },
    },
    {
      id: "results.binary",
      label: "Binary envelope…",
      group: "Results",
      enabled: hasFlowsheet,
      run: () => app().openAnalysis({ tab: "binary" }),
    },
    {
      id: "setup.reactions",
      label: "Reactions…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openReactions(true),
    },
    {
      id: "tools.sensitivity",
      label: "Sensitivity analysis…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openStudies("sensitivity"),
    },
    {
      id: "tools.optimizer",
      label: "Optimizer…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openStudies("optimizer"),
    },
    {
      id: "tools.scenarios",
      label: "Compare scenarios…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openStudies("scenarios"),
    },
    {
      id: "tools.scripts",
      label: "Scripts…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openScripts(true),
    },
    {
      id: "tools.dynamics",
      label: "Dynamics…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openDynamics(true),
    },
    {
      id: "results.spreadsheet",
      label: "Spreadsheet",
      group: "Results",
      enabled: hasFlowsheet,
      run: () => app().setBottomTab("sheet"),
    },
    {
      id: "tools.open",
      label: "Tools…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openTools({ objectId: app().selection ?? undefined }),
    },
    {
      id: "tools.hydrates",
      label: "Hydrate formation…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openTools({ toolId: "hydrates", objectId: app().selection ?? undefined }),
    },
    {
      id: "tools.psv",
      label: "PSV sizing…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openTools({ toolId: "psv", objectId: app().selection ?? undefined }),
    },
    {
      id: "tools.separator",
      label: "Separator sizing…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openTools({ toolId: "separator", objectId: app().selection ?? undefined }),
    },
    {
      id: "tools.depressurization",
      label: "Vessel depressurization…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openTools({ toolId: "depressurization", objectId: app().selection ?? undefined }),
    },
    {
      id: "tools.criticalPoint",
      label: "True critical point…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openTools({ toolId: "criticalPoint", objectId: app().selection ?? undefined }),
    },
    {
      id: "tools.distillationCurves",
      label: "Distillation curves…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openTools({ toolId: "distillationCurves", objectId: app().selection ?? undefined }),
    },
    {
      id: "tools.mccabeThiele",
      label: "McCabe–Thiele…",
      group: "Tools",
      enabled: hasFlowsheet,
      run: () => app().openTools({ toolId: "mccabeThiele", objectId: app().selection ?? undefined }),
    },
    {
      id: "results.report",
      label: "Report…",
      group: "Results",
      keys: ["Mod+P"],
      global: true,
      enabled: hasFlowsheet,
      run: () => app().openReport(true),
    },
    {
      id: "view.palette",
      label: "Command palette",
      group: "View",
      keys: ["Mod+K"],
      global: true,
      run: () => app().openPalette(true),
    },
    {
      id: "view.shortcuts",
      label: "Keyboard shortcuts",
      group: "View",
      keys: ["?"],
      run: () => app().openShortcuts(true),
    },
    { id: "view.theme", label: "Switch light or dark theme", group: "View", run: shell.toggleTheme },
    {
      id: "view.tags",
      label: "Show or hide stream conditions",
      group: "View",
      enabled: hasFlowsheet,
      run: () => app().toggleStreamTags(),
    },
  ];
}

export const isMac = (): boolean =>
  typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);

/** "Mod+Shift+Z" as the platform shows it: "Ctrl+Shift+Z", or "⌘⇧Z" on a Mac. */
export function keyLabel(keys: string): string {
  if (!isMac()) return keys.replace("Mod", "Ctrl");
  return keys
    .replace("Mod+", "⌘")
    .replace("Shift+", "⇧")
    .replace("Alt+", "⌥")
    .replace("Escape", "Esc")
    .replace("Backspace", "⌫");
}

/** Whether a key event is this shortcut. Letters match either case; "?" is a shifted key itself. */
export function matches(
  e: Pick<KeyboardEvent, "key" | "ctrlKey" | "metaKey" | "shiftKey" | "altKey">,
  keys: string,
): boolean {
  const parts = keys.split("+");
  const key = parts[parts.length - 1]!;
  const want = new Set(parts.slice(0, -1));
  const mod = isMac() ? e.metaKey : e.ctrlKey;
  if (want.has("Mod") !== mod) return false;
  if (want.has("Alt") !== e.altKey) return false;
  // A typed symbol such as "?" needs Shift on most layouts; it is part of the key, not a modifier.
  if (key.length === 1 && !/[a-z0-9]/i.test(key)) return e.key === key;
  if (want.has("Shift") !== e.shiftKey) return false;
  return key.length === 1 ? e.key.toLowerCase() === key.toLowerCase() : e.key === key;
}

/** True when the keystroke is meant for a text field, not for the app. */
export function typingIn(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName);
}

/** Finds the command a key event triggers, if any is enabled. */
export function commandFor(e: KeyboardEvent, commands: Command[]): Command | undefined {
  const typing = typingIn(e.target);
  return commands.find(
    (c) => (!typing || c.global) && (c.enabled?.() ?? true) && (c.keys ?? []).some((k) => matches(e, k)),
  );
}

/** Subsequence match, scored so that matches at word starts and in a row rank first; null when no match. */
export function fuzzyScore(text: string, query: string): number | null {
  const t = text.toLowerCase();
  const q = query.toLowerCase().trim();
  if (!q) return 0;
  let score = 0;
  let from = 0;
  let previous = -2;
  for (const ch of q) {
    if (ch === " ") continue;
    const i = t.indexOf(ch, from);
    if (i < 0) return null;
    score += i === previous + 1 ? 3 : 1;
    if (i === 0 || /[\s\-_(]/.test(t[i - 1] ?? "")) score += 2;
    previous = i;
    from = i + 1;
  }
  return score - t.length * 0.01;
}
