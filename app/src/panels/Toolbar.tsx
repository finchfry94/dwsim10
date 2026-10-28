import { FilePlus2, FolderOpen, LayoutGrid, Moon, Play, Save, Sun, Tags } from "lucide-react";
import { COLOR_MODES, type ColorMode } from "../canvas/coloring";
import { useApp } from "../state/store";

export function Toolbar({
  onOpen,
  onSave,
  theme,
  onToggleTheme,
}: {
  onOpen: () => void;
  onSave: () => void;
  theme: "light" | "dark";
  onToggleTheme: () => void;
}) {
  const flowsheet = useApp((s) => s.flowsheet);
  const solving = useApp((s) => s.solving);
  const colorMode = useApp((s) => s.colorMode);
  const showTags = useApp((s) => s.showStreamTags);
  const engineStatus = useApp((s) => s.engineStatus);
  const { newFlowsheet, solve, autoLayout, setColorMode, toggleStreamTags } = useApp.getState();
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
        <button className="btn" onClick={onSave} disabled={!has} title="Save (Ctrl+S)">
          <Save size={16} /> Save
        </button>
      </div>
      <div className="toolbar-group">
        <button
          className="btn btn-primary"
          onClick={() => void solve()}
          disabled={!has || solving}
          title="Solve (F5)"
          data-testid="solve"
        >
          <Play size={16} /> {solving ? "Solving…" : "Solve"}
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
