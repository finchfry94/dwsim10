import {
  ChartSpline,
  Command,
  FileText,
  FilePlus2,
  FlaskConical,
  FolderOpen,
  LayoutGrid,
  ListChecks,
  Moon,
  Play,
  Redo2,
  Save,
  SaveAll,
  Square,
  Sun,
  Tags,
  Undo2,
  Wrench,
} from "lucide-react";
import { keyLabel } from "../lib/commands";
import { COLOR_MODES, type ColorMode } from "../canvas/coloring";
import { useApp } from "../state/store";

export function Toolbar({
  onOpen,
  onSave,
  theme,
  onToggleTheme,
}: {
  onOpen: () => void;
  onSave: (saveAs?: boolean) => void;
  theme: "light" | "dark";
  onToggleTheme: () => void;
}) {
  const flowsheet = useApp((s) => s.flowsheet);
  const solving = useApp((s) => s.solving);
  const colorMode = useApp((s) => s.colorMode);
  const showTags = useApp((s) => s.showStreamTags);
  const engineStatus = useApp((s) => s.engineStatus);
  const autoSolve = useApp((s) => s.autoSolve);
  const history = useApp((s) => s.history);
  const busy = useApp((s) => s.busy);
  const {
    newFlowsheet,
    solve,
    cancelSolve,
    check,
    undo,
    redo,
    autoLayout,
    setColorMode,
    toggleStreamTags,
    openSetup,
    setAutoSolve,
    openPalette,
  } = useApp.getState();
  const has = flowsheet !== null;

  return (
    <header className="toolbar">
      <div className="brand">DWSIM</div>
      <div className="toolbar-group">
        <button className="btn" onClick={() => void newFlowsheet()} title="New flowsheet">
          <FilePlus2 size={16} /> New
        </button>
        <button className="btn" onClick={onOpen} title="Open flowsheet (Ctrl+O)">
          <FolderOpen size={16} /> Open
        </button>
        <button
          className="btn"
          onClick={() => onSave()}
          disabled={!has}
          title={`Save (${keyLabel("Mod+S")})`}
        >
          <Save size={16} /> Save
          {history?.modified && <span className="dirty-dot" aria-label="unsaved changes" />}
        </button>
        <button
          className="btn btn-icon"
          onClick={() => onSave(true)}
          disabled={!has}
          title={`Save as… (${keyLabel("Mod+Shift+S")})`}
          aria-label="Save as"
        >
          <SaveAll size={16} />
        </button>
      </div>
      <div className="toolbar-group">
        <button
          className="btn btn-icon"
          onClick={() => void undo()}
          disabled={!history?.canUndo || busy || solving}
          title={history?.undo ? `Undo ${history.undo} (${keyLabel("Mod+Z")})` : "Nothing to undo"}
          aria-label="Undo"
          data-testid="undo"
        >
          <Undo2 size={16} />
        </button>
        <button
          className="btn btn-icon"
          onClick={() => void redo()}
          disabled={!history?.canRedo || busy || solving}
          title={history?.redo ? `Redo ${history.redo} (${keyLabel("Mod+Shift+Z")})` : "Nothing to redo"}
          aria-label="Redo"
          data-testid="redo"
        >
          <Redo2 size={16} />
        </button>
      </div>
      <div className="toolbar-group">
        <button
          className="btn"
          onClick={() => openSetup("edit")}
          disabled={!has}
          title="Compounds, property package and units"
        >
          <FlaskConical size={16} /> Setup
        </button>
      </div>
      <div className="toolbar-group">
        {solving ? (
          <button
            className="btn btn-danger"
            onClick={() => void cancelSolve()}
            title="Stop solving (Esc)"
            data-testid="cancel-solve"
          >
            <Square size={14} /> Cancel
          </button>
        ) : (
          <button
            className="btn btn-primary"
            onClick={() => void solve()}
            disabled={!has}
            title="Solve (F5)"
            data-testid="solve"
          >
            <Play size={16} /> Solve
          </button>
        )}
        <button
          className="btn"
          onClick={() => void check()}
          disabled={!has || solving}
          title="Check the flowsheet for problems (F7)"
          data-testid="check"
        >
          <ListChecks size={16} /> Check
        </button>
        <label className="toggle toolbar-toggle" title="Solve automatically after every edit">
          <input
            type="checkbox"
            checked={autoSolve}
            onChange={(e) => setAutoSolve(e.target.checked)}
            aria-label="Auto-solve"
          />
          Auto
        </label>
        <button
          className="btn"
          onClick={() => useApp.getState().openAnalysis({ tab: "phase" })}
          disabled={!has}
          title="Phase and binary envelopes"
          data-testid="analysis"
        >
          <ChartSpline size={16} /> Analysis
        </button>
        <button
          className="btn"
          onClick={() => {
            const { selection, openTools } = useApp.getState();
            openTools({ objectId: selection ?? undefined });
          }}
          disabled={!has}
          title="Sizing, hydrates, critical point, blowdown, distillation curves, McCabe–Thiele"
          data-testid="tools"
        >
          <Wrench size={16} /> Tools
        </button>
        <button
          className="btn"
          onClick={() => useApp.getState().openReport(true)}
          disabled={!has}
          title={`Report (${keyLabel("Mod+P")})`}
          data-testid="report"
        >
          <FileText size={16} /> Report
        </button>
        <button
          className="btn"
          onClick={() => void autoLayout()}
          disabled={!has}
          title="Arrange objects automatically"
        >
          <LayoutGrid size={16} /> Layout
        </button>
      </div>
      <div className="toolbar-group">
        <label className="toolbar-label" htmlFor="color-mode">
          Streams
        </label>
        <select
          id="color-mode"
          className="select"
          value={colorMode}
          onChange={(e) => setColorMode(e.target.value as ColorMode)}
          disabled={!has}
        >
          {COLOR_MODES.map((m) => (
            <option key={m.mode} value={m.mode}>
              {m.label}
            </option>
          ))}
        </select>
        <button
          className={`btn btn-icon ${showTags ? "is-on" : ""}`}
          onClick={toggleStreamTags}
          disabled={!has}
          title="Show stream conditions on the canvas"
          aria-pressed={showTags}
        >
          <Tags size={16} />
        </button>
      </div>
      <div className="ml-auto flex items-center gap-3">
        {flowsheet && (
          <span className="doc-title" title={flowsheet.path ?? undefined}>
            {flowsheet.name}
          </span>
        )}
        <span className={`engine-status engine-${engineStatus}`} title="Engine">
          {engineStatus === "ready"
            ? "Engine ready"
            : engineStatus === "starting"
              ? "Starting engine…"
              : "Engine stopped"}
        </span>
        <button
          className="btn btn-icon"
          onClick={() => openPalette(true)}
          title={`Command palette (${keyLabel("Mod+K")})`}
          aria-label="Command palette"
        >
          <Command size={16} />
        </button>
        <button
          className="btn btn-icon"
          onClick={onToggleTheme}
          title="Toggle theme"
          aria-label="Toggle theme"
        >
          {theme === "dark" ? <Sun size={16} /> : <Moon size={16} />}
        </button>
      </div>
    </header>
  );
}
