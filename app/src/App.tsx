import { FilePlus2, FolderOpen } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { Panel, PanelGroup, PanelResizeHandle } from "react-resizable-panels";
import { FlowsheetCanvas } from "./canvas/FlowsheetCanvas";
import { SelectionBar } from "./canvas/SelectionBar";
import { buildCommands, commandFor, type ShellActions } from "./lib/commands";
import { CommandPalette, ShortcutSheet } from "./panels/CommandPalette";
import { AnalysisDialog } from "./panels/AnalysisDialog";
import { ReportDialog } from "./panels/ReportDialog";
import { ToolsDialog } from "./panels/ToolsDialog";
import { transport } from "./engine/transport";
import type { SampleFile } from "./engine/types";
import { pickFileToOpen, pickFileToSave } from "./panels/files";
import { Inspector } from "./panels/Inspector";
import { LogPanel } from "./panels/LogPanel";
import { Palette } from "./panels/Palette";
import { PathDialog } from "./panels/PathDialog";
import { SetupDialog } from "./panels/SetupDialog";
import { Toolbar } from "./panels/Toolbar";
import { connectNotifications, useApp } from "./state/store";

type Theme = "light" | "dark";

function initialTheme(): Theme {
  try {
    const saved = localStorage.getItem("dwsim.theme");
    if (saved === "light" || saved === "dark") return saved;
  } catch {
    /* storage unavailable */
  }
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

export function App() {
  const flowsheet = useApp((s) => s.flowsheet);
  const setup = useApp((s) => s.setup);
  const [dialog, setDialog] = useState<"open" | "save" | null>(null);
  const [theme, setTheme] = useState<Theme>(initialTheme);

  useEffect(() => connectNotifications(), []);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    try {
      localStorage.setItem("dwsim.theme", theme);
    } catch {
      /* storage unavailable */
    }
  }, [theme]);

  const open = async () => {
    if (transport.kind === "http") return setDialog("open");
    const path = await pickFileToOpen();
    if (path) await useApp.getState().open(path);
  };

  const save = async (saveAs = false) => {
    const fs = useApp.getState().flowsheet;
    if (!fs) return;
    if (fs.path && !saveAs) return useApp.getState().save();
    if (transport.kind === "http") return setDialog("save");
    const path = await pickFileToSave(fs.path);
    if (path) await useApp.getState().save(path);
  };

  // The commands call the latest open/save through a ref, so the list is built once.
  const shell = useRef<ShellActions>({ open: () => {}, save: () => {}, toggleTheme: () => {} });
  shell.current = {
    open: () => void open(),
    save: (saveAs) => void save(saveAs),
    toggleTheme: () => setTheme((t) => (t === "dark" ? "light" : "dark")),
  };
  const commands = useMemo(
    () =>
      buildCommands({
        open: () => shell.current.open(),
        save: (saveAs) => shell.current.save(saveAs),
        toggleTheme: () => shell.current.toggleTheme(),
      }),
    [],
  );

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const { palette, shortcuts, setup: setupOpen, analysis, report, tools } = useApp.getState();
      if (palette || shortcuts || setupOpen || analysis || report || tools || e.defaultPrevented) return; // a dialog has the keyboard
      // React Flow deletes the selection itself, connections included.
      if ((e.key === "Delete" || e.key === "Backspace") && !(e.target instanceof HTMLInputElement)) return;
      const command = commandFor(e, commands);
      if (!command) return;
      e.preventDefault();
      void command.run();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [commands]);

  // Closing the window with unsaved changes asks first.
  useEffect(() => {
    const onUnload = (e: BeforeUnloadEvent) => {
      if (!useApp.getState().history?.modified) return;
      e.preventDefault();
      e.returnValue = "";
    };
    window.addEventListener("beforeunload", onUnload);
    return () => window.removeEventListener("beforeunload", onUnload);
  }, []);

  return (
    <div className="app">
      <Toolbar
        onOpen={() => void open()}
        onSave={(saveAs) => void save(saveAs)}
        theme={theme}
        onToggleTheme={() => setTheme(theme === "dark" ? "light" : "dark")}
      />
      <PanelGroup direction="horizontal" className="workspace" autoSaveId="dwsim.layout.h">
        <Panel defaultSize={15} minSize={10} maxSize={30}>
          <Palette />
        </Panel>
        <PanelResizeHandle className="resize-handle" />
        <Panel minSize={30}>
          <PanelGroup direction="vertical" autoSaveId="dwsim.layout.v">
            <Panel minSize={30}>
              <main className="canvas-area">
                {flowsheet ? (
                  <>
                    <FlowsheetCanvas />
                    <SelectionBar />
                  </>
                ) : (
                  <Welcome onOpen={() => void open()} />
                )}
              </main>
            </Panel>
            <PanelResizeHandle className="resize-handle resize-handle-h" />
            <Panel defaultSize={22} minSize={8}>
              <LogPanel />
            </Panel>
          </PanelGroup>
        </Panel>
        <PanelResizeHandle className="resize-handle" />
        <Panel defaultSize={25} minSize={15} maxSize={45}>
          <Inspector />
        </Panel>
      </PanelGroup>
      <CommandPalette commands={commands} />
      <AnalysisDialog />
      <ReportDialog />
      <ToolsDialog />
      <ShortcutSheet commands={commands} />
      {setup && flowsheet && <SetupDialog mode={setup} onClose={() => useApp.getState().openSetup(null)} />}
      {dialog && (
        <PathDialog
          mode={dialog}
          initial={dialog === "save" ? (flowsheet?.path ?? "") : ""}
          onClose={() => setDialog(null)}
          onSubmit={(path) => {
            setDialog(null);
            void (dialog === "open" ? useApp.getState().open(path) : useApp.getState().save(path));
          }}
        />
      )}
    </div>
  );
}

function Welcome({ onOpen }: { onOpen: () => void }) {
  const [samples, setSamples] = useState<SampleFile[]>([]);
  const engineStatus = useApp((s) => s.engineStatus);
  const busy = useApp((s) => s.busy);
  const recent = useApp((s) => s.recent);
  useEffect(() => {
    transport
      .samples()
      .then(setSamples)
      .catch(() => setSamples([]));
  }, []);
  return (
    <div className="welcome">
      <h1>Process simulation</h1>
      <p className="muted">Start a new flowsheet, or open a .dwxmz / .dwxml file.</p>
      <div className="flex gap-2">
        <button
          className="btn btn-primary"
          onClick={() => void useApp.getState().newFlowsheet()}
          disabled={engineStatus !== "ready" || busy}
        >
          <FilePlus2 size={16} /> New flowsheet
        </button>
        <button className="btn" onClick={onOpen} disabled={engineStatus !== "ready" || busy}>
          <FolderOpen size={16} /> Open…
        </button>
      </div>
      {recent.length > 0 && (
        <div className="welcome-samples">
          <h2 className="section-title">Recent</h2>
          <ul className="recent-files">
            {recent.map((path) => (
              <li key={path}>
                <button
                  title={path}
                  disabled={engineStatus !== "ready" || busy}
                  onClick={() => void useApp.getState().open(path)}
                >
                  {path.split(/[\\/]/).pop()} <span className="muted">— {path}</span>
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
      {samples.length > 0 && (
        <div className="welcome-samples">
          <h2 className="section-title">Reference flowsheets</h2>
          <div className="sample-grid">
            {samples.map((s) => (
              <button
                key={s.path}
                className="sample"
                disabled={engineStatus !== "ready" || busy}
                onClick={() => void useApp.getState().open(s.path)}
              >
                {s.name.replace(/([a-z])([A-Z])/g, "$1 $2")}
              </button>
            ))}
          </div>
        </div>
      )}
      {engineStatus === "starting" && (
        <p className="muted">Starting the engine and loading compound databases…</p>
      )}
    </div>
  );
}
