import { Play, Plus, Save, Trash2, X } from "lucide-react";
import { useEffect, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type { Script, ScriptRun } from "../engine/types";
import { useEscape } from "../lib/useEscape";
import { useApp } from "../state/store";

const closeScripts = () => useApp.getState().openScripts(false);

const blankScript = (n: number): Script => ({
  title: `Script ${n}`,
  text: "# Flowsheet, Solver and every object by its tag (dashes as underscores) are in scope.\n",
  interpreter: "IronPython",
  linked: false,
  linkedTo: "Simulation",
  linkedObject: null,
  event: "SimulationOpened",
});

const SOURCES: { value: Script["linkedTo"]; label: string }[] = [
  { value: "Simulation", label: "The simulation" },
  { value: "Solver", label: "The solver" },
  { value: "Integrator", label: "The dynamic integrator" },
  { value: "FlowsheetObject", label: "An object" },
];

/** Splits "ObjectCalculationFinished" into "Object calculation finished". */
export const eventLabel = (name: string) =>
  name
    .replace(/([a-z])([A-Z0-9])/g, "$1 $2")
    .replace(/^./, (c) => c.toUpperCase())
    .replace(/ (\w)/g, (m) => m.toLowerCase());

/** Inserts four spaces at the caret, or indents/outdents the selected lines. */
export function indent(
  text: string,
  start: number,
  end: number,
  outdent: boolean,
): { text: string; start: number; end: number } {
  if (start === end && !outdent)
    return { text: text.slice(0, start) + "    " + text.slice(end), start: start + 4, end: start + 4 };
  const lineStart = text.lastIndexOf("\n", start - 1) + 1;
  const block = text.slice(lineStart, end);
  const lines = block.split("\n");
  const changed = lines.map((l) => (outdent ? l.replace(/^ {1,4}/, "") : "    " + l));
  const delta = changed.join("\n").length - block.length;
  const firstDelta = changed[0]!.length - lines[0]!.length;
  return {
    text: text.slice(0, lineStart) + changed.join("\n") + text.slice(end),
    start: Math.max(lineStart, start + firstDelta),
    end: end + delta,
  };
}

/** The flowsheet's Python scripts: edit, link to an event, run now and see the output. */
export function ScriptsDialog() {
  const open = useApp((s) => s.scripts);
  const flowsheet = useApp((s) => s.flowsheet);
  const nodes = useApp((s) => s.graph?.nodes ?? []);
  useEscape(open, closeScripts);
  const [scripts, setScripts] = useState<Script[] | null>(null);
  const [events, setEvents] = useState<Record<string, string[]>>({});
  const [current, setCurrent] = useState<Script | null>(null);
  const [run, setRun] = useState<ScriptRun | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async (select?: string) => {
    if (!flowsheet) return;
    try {
      const r = await engine.scripts(flowsheet.id);
      setScripts(r.scripts);
      setEvents(r.events);
      setCurrent((c) => r.scripts.find((s) => s.id === (select ?? c?.id)) ?? r.scripts[0] ?? blankScript(1));
    } catch (e) {
      setError(EngineError.from(e).message);
    }
  };

  useEffect(() => {
    if (!open) return;
    setRun(null);
    setError(null);
    setCurrent(null);
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, flowsheet]);

  if (!open || !flowsheet) return null;
  const id = flowsheet.id;

  const attempt = async <T,>(action: () => Promise<T>): Promise<T | null> => {
    setError(null);
    try {
      return await action();
    } catch (e) {
      setError(EngineError.from(e).message);
      return null;
    }
  };
  const save = async () => {
    if (!current) return null;
    const saved = await attempt(() => engine.saveScript(id, current));
    if (saved) await load(saved.id);
    return saved;
  };
  const runNow = async () => {
    setBusy(true);
    const saved = await save();
    if (saved?.id) {
      const r = await attempt(() => engine.runScript(id, saved.id!));
      if (r) setRun(r);
      await useApp.getState().refresh();
    }
    setBusy(false);
  };

  const set = (change: Partial<Script>) => current && setCurrent({ ...current, ...change });
  const sourceEvents = current ? (events[current.linkedTo] ?? []) : [];

  return (
    <div className="dialog-backdrop" onMouseDown={closeScripts}>
      <div
        className="dialog scripts-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Scripts"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="panel-header">
          <h2 className="dialog-title">Scripts</h2>
          <button className="btn btn-icon btn-ghost" onClick={closeScripts} aria-label="Close">
            <X size={15} />
          </button>
        </div>
        <div className="tools-layout">
          <nav className="tools-list" aria-label="Scripts">
            {(scripts ?? []).map((s) => (
              <button
                key={s.id}
                className="tools-item"
                aria-current={current?.id === s.id}
                onClick={() => {
                  setCurrent(s);
                  setRun(null);
                }}
              >
                {s.title}
                {s.linked && (
                  <span className="muted text-[11px] block">on {eventLabel(s.event).toLowerCase()}</span>
                )}
              </button>
            ))}
            <button
              className="btn btn-ghost mt-1"
              onClick={() => {
                setCurrent(blankScript((scripts?.length ?? 0) + 1));
                setRun(null);
              }}
            >
              <Plus size={13} /> New script
            </button>
          </nav>
          <div className="tools-main scripts-main">
            {current && (
              <>
                <div className="analysis-controls">
                  <label className="field">
                    <span className="field-label">Title</span>
                    <input
                      className="input"
                      value={current.title}
                      onChange={(e) => set({ title: e.target.value })}
                      aria-label="Title"
                    />
                  </label>
                  <label className="field">
                    <span className="field-label">Interpreter</span>
                    <select
                      className="select"
                      value={current.interpreter}
                      onChange={(e) => set({ interpreter: e.target.value as Script["interpreter"] })}
                      aria-label="Interpreter"
                    >
                      <option value="IronPython">IronPython (built in)</option>
                      <option value="Python_NET">Python.NET (a configured Python)</option>
                    </select>
                  </label>
                  <label className="toggle">
                    <input
                      type="checkbox"
                      checked={current.linked}
                      onChange={(e) => set({ linked: e.target.checked })}
                    />{" "}
                    Run on an event
                  </label>
                  {current.linked && (
                    <>
                      <label className="field">
                        <span className="field-label">Raised by</span>
                        <select
                          className="select"
                          value={current.linkedTo}
                          onChange={(e) => {
                            const linkedTo = e.target.value as Script["linkedTo"];
                            set({ linkedTo, event: events[linkedTo]?.[0] ?? current.event });
                          }}
                          aria-label="Raised by"
                        >
                          {SOURCES.map((s) => (
                            <option key={s.value} value={s.value}>
                              {s.label}
                            </option>
                          ))}
                        </select>
                      </label>
                      {current.linkedTo === "FlowsheetObject" && (
                        <label className="field">
                          <span className="field-label">Object</span>
                          <select
                            className="select"
                            value={current.linkedObject ?? ""}
                            onChange={(e) => set({ linkedObject: e.target.value || null })}
                            aria-label="Object"
                          >
                            <option value="">Choose…</option>
                            {nodes.map((n) => (
                              <option key={n.id} value={n.id}>
                                {n.tag}
                              </option>
                            ))}
                          </select>
                        </label>
                      )}
                      <label className="field">
                        <span className="field-label">Event</span>
                        <select
                          className="select"
                          value={current.event}
                          onChange={(e) => set({ event: e.target.value })}
                          aria-label="Event"
                        >
                          {sourceEvents.map((ev) => (
                            <option key={ev} value={ev}>
                              {eventLabel(ev)}
                            </option>
                          ))}
                        </select>
                      </label>
                    </>
                  )}
                </div>
                <textarea
                  className="code-editor"
                  spellCheck={false}
                  value={current.text}
                  onChange={(e) => set({ text: e.target.value })}
                  onKeyDown={(e) => {
                    if (e.key === "Tab") {
                      e.preventDefault();
                      const t = e.currentTarget;
                      const r = indent(t.value, t.selectionStart, t.selectionEnd, e.shiftKey);
                      set({ text: r.text });
                      requestAnimationFrame(() => t.setSelectionRange(r.start, r.end));
                    } else if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
                      e.preventDefault();
                      void runNow();
                    }
                  }}
                  aria-label="Script"
                  data-testid="script-text"
                />
                <div className="flex items-center gap-2 mt-2">
                  <button
                    className="btn btn-primary"
                    onClick={() => void runNow()}
                    disabled={busy}
                    data-testid="run-script"
                  >
                    <Play size={14} /> {busy ? "Running…" : "Run"}
                  </button>
                  <button className="btn btn-ghost" onClick={() => void save()} disabled={busy}>
                    <Save size={13} /> Save
                  </button>
                  {current.id && (
                    <button
                      className="btn btn-ghost"
                      onClick={async () => {
                        if (await attempt(() => engine.deleteScript(id, current.id!))) {
                          setCurrent(null);
                          await load();
                        }
                      }}
                      disabled={busy}
                    >
                      <Trash2 size={13} /> Delete
                    </button>
                  )}
                  <span className="muted text-[12px] ml-auto">
                    Ctrl+Enter runs · Tab indents · saved with the flowsheet
                  </span>
                </div>
              </>
            )}
            {error && <p className="callout callout-error">{error}</p>}
            {run && (
              <div className="script-output" data-testid="script-output">
                <div className="field-label">
                  {run.ok ? `Output (${(run.durationMs / 1000).toFixed(2)} s)` : "The script failed"}
                </div>
                <pre className={run.ok ? "" : "is-error"}>
                  {run.output.length ? run.output.join("\n") : "(no output)"}
                </pre>
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
