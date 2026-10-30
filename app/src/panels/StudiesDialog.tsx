import { Download, Play, Plus, Save, Square, Trash2, X } from "lucide-react";
import { useEffect, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type {
  ScenarioComparison,
  ScenarioSlot,
  ChoiceOption,
  OptimizerCase,
  OptimizerResult,
  OptimizerVariable,
  SensitivityCase,
  SensitivityResult,
  StudyVariable,
  VariableOption,
} from "../engine/types";
import { downloadText, fileName, toCsv } from "../lib/csv";
import { formatNumber, unitLabel } from "../lib/units";
import { useEscape } from "../lib/useEscape";
import { useApp } from "../state/store";
import { XYChart } from "./XYChart";

const closeStudies = () => useApp.getState().openStudies(null);

/** Sensitivity analysis and the optimizer: cases kept in the flowsheet, run by the engine. */
export function StudiesDialog() {
  const tab = useApp((s) => s.studies);
  const flowsheet = useApp((s) => s.flowsheet);
  useEscape(tab !== null, closeStudies);
  if (!tab || !flowsheet) return null;
  return (
    <div className="dialog-backdrop" onMouseDown={closeStudies}>
      <div
        className="dialog studies-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Studies"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="panel-header">
          <div className="tabs" role="tablist">
            <button
              role="tab"
              className="tab"
              aria-selected={tab === "sensitivity"}
              onClick={() => useApp.getState().openStudies("sensitivity")}
            >
              Sensitivity analysis
            </button>
            <button
              role="tab"
              className="tab"
              aria-selected={tab === "optimizer"}
              onClick={() => useApp.getState().openStudies("optimizer")}
            >
              Optimizer
            </button>
            <button
              role="tab"
              className="tab"
              aria-selected={tab === "scenarios"}
              onClick={() => useApp.getState().openStudies("scenarios")}
            >
              Scenarios
            </button>
          </div>
          <button className="btn btn-icon btn-ghost" onClick={closeStudies} aria-label="Close">
            <X size={15} />
          </button>
        </div>
        <div className="studies-body">
          {tab === "sensitivity" ? (
            <Sensitivity flowsheetId={flowsheet.id} />
          ) : tab === "optimizer" ? (
            <Optimizer flowsheetId={flowsheet.id} />
          ) : (
            <Scenarios flowsheetId={flowsheet.id} />
          )}
        </div>
      </div>
    </div>
  );
}

/** Picks an object on the flowsheet, then one of its properties; the unit is the property's. */
export function VariablePicker({
  flowsheetId,
  value,
  onChange,
  writable,
  label,
}: {
  flowsheetId: string;
  value: StudyVariable;
  onChange: (v: StudyVariable) => void;
  writable: boolean;
  label: string;
}) {
  const nodes = useApp((s) => s.graph?.nodes ?? []);
  const [properties, setProperties] = useState<VariableOption[]>([]);
  useEffect(() => {
    if (!value.object) return setProperties([]);
    let live = true;
    engine
      .variables(flowsheetId, value.object, writable)
      .then((p) => live && setProperties(p))
      .catch(() => live && setProperties([]));
    return () => {
      live = false;
    };
  }, [flowsheetId, value.object, writable]);
  const objects = [...nodes].sort((a, b) => a.tag.localeCompare(b.tag, undefined, { numeric: true }));
  return (
    <span className="variable-picker">
      <select
        className="select"
        value={value.object ?? ""}
        onChange={(e) => onChange({ object: e.target.value || null, property: null, unit: null })}
        aria-label={`${label} object`}
      >
        <option value="">Object…</option>
        {objects.map((n) => (
          <option key={n.id} value={n.id}>
            {n.tag}
          </option>
        ))}
      </select>
      <select
        className="select"
        value={value.property ?? ""}
        disabled={!value.object}
        onChange={(e) => {
          const p = properties.find((x) => x.value === e.target.value);
          onChange({ ...value, property: p?.value ?? null, unit: p?.unit ?? null });
        }}
        aria-label={`${label} property`}
      >
        <option value="">Property…</option>
        {value.property && !properties.some((p) => p.value === value.property) && (
          <option value={value.property}>{value.label?.split(" · ")[1] ?? value.property}</option>
        )}
        {properties.map((p) => (
          <option key={p.value} value={p.value}>
            {p.label}
            {p.unit ? ` (${unitLabel(p.unit)})` : ""}
          </option>
        ))}
      </select>
    </span>
  );
}

/** Loads a study's cases, keeps one selected, and runs it with cancel and progress. */
function useStudy<Case extends { index?: number; name: string }, Result>(
  list: () => Promise<Case[]>,
  blank: (n: number) => Case,
) {
  const [cases, setCases] = useState<Case[] | null>(null);
  const [current, setCurrent] = useState<Case | null>(null);
  const [result, setResult] = useState<Result | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [request, setRequest] = useState<string | null>(null);
  const load = async (select?: number) => {
    try {
      const all = await list();
      setCases(all);
      setCurrent(all.length ? structuredClone(all[Math.min(select ?? 0, all.length - 1)]!) : blank(1));
    } catch (e) {
      setError(EngineError.from(e).message);
    }
  };
  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const attempt = async <T,>(action: () => Promise<T>): Promise<T | null> => {
    setError(null);
    try {
      return await action();
    } catch (e) {
      const err = EngineError.from(e);
      setError(err.code === -32800 ? "Cancelled." : err.message);
      return null;
    }
  };
  /** Runs case `index`; then re-reads the cases (an optimizer run updates its variables) and the flowsheet. */
  const run = async (index: number, action: (requestId: string) => Promise<Result>) => {
    const id = `study-${Date.now()}`;
    setRequest(id);
    useApp.setState({ studyProgress: null });
    const r = await attempt(() => action(id));
    if (r) setResult(r);
    setRequest(null);
    await load(index);
    await useApp.getState().refresh();
  };
  return {
    cases,
    current,
    setCurrent,
    result,
    setResult,
    error,
    setError,
    busy: request !== null,
    cancel: () => request && void engine.cancel(request),
    load,
    attempt,
    run,
  };
}

function CaseBar<Case extends { index?: number; name: string }>({
  cases,
  current,
  busy,
  onPick,
  onNew,
  onSave,
  onDelete,
}: {
  cases: Case[];
  current: Case;
  busy: boolean;
  onPick: (i: number) => void;
  onNew: () => void;
  onSave: () => void;
  onDelete: () => void;
}) {
  return (
    <div className="analysis-controls">
      <label className="field">
        <span className="field-label">Case</span>
        <select
          className="select"
          value={current.index ?? "new"}
          onChange={(e) => onPick(Number(e.target.value))}
          aria-label="Case"
          disabled={busy}
        >
          {cases.map((c, i) => (
            <option key={i} value={i}>
              {c.name || `Case ${i + 1}`}
            </option>
          ))}
          {current.index === undefined && <option value="new">{current.name} (unsaved)</option>}
        </select>
      </label>
      <button className="btn btn-ghost" onClick={onNew} disabled={busy}>
        <Plus size={13} /> New
      </button>
      <button className="btn btn-ghost" onClick={onSave} disabled={busy}>
        <Save size={13} /> Save
      </button>
      {current.index !== undefined && (
        <button className="btn btn-ghost" onClick={onDelete} disabled={busy}>
          <Trash2 size={13} /> Delete
        </button>
      )}
    </div>
  );
}

function RunBar({
  busy,
  onRun,
  onCancel,
  label,
}: {
  busy: boolean;
  onRun: () => void;
  onCancel: () => void;
  label: string;
}) {
  const progress = useApp((s) => s.studyProgress);
  return (
    <div className="flex items-center gap-2 mt-2">
      {busy ? (
        <button className="btn btn-danger" onClick={onCancel}>
          <Square size={13} /> Cancel
        </button>
      ) : (
        <button className="btn btn-primary" onClick={onRun} data-testid="run-study">
          <Play size={14} /> {label}
        </button>
      )}
      {busy && <span className="muted text-[12px]">{progress?.text ?? "Starting…"}</span>}
    </div>
  );
}

function Num({
  value,
  onChange,
  label,
  placeholder,
}: {
  value: number | null;
  onChange: (v: number | null) => void;
  label: string;
  placeholder?: string;
}) {
  const [draft, setDraft] = useState(value === null ? "" : String(value));
  useEffect(
    () => setDraft((d) => (d.trim() !== "" && Number(d) === value ? d : value === null ? "" : String(value))),
    [value],
  );
  return (
    <input
      className="input input-sm num"
      inputMode="decimal"
      value={draft}
      placeholder={placeholder}
      aria-label={label}
      onChange={(e) => {
        setDraft(e.target.value);
        const t = e.target.value.trim();
        if (t === "") onChange(null);
        else if (Number.isFinite(Number(t))) onChange(Number(t));
      }}
    />
  );
}

// ------------------------------------------------------------------ sensitivity

const blankSensitivity = (n: number): SensitivityCase => ({
  name: `Case ${n}`,
  description: "",
  independent: { object: null, property: null, unit: null, from: null, to: null, points: 10 },
  dependents: [{ object: null, property: null, unit: null }],
});

function Sensitivity({ flowsheetId }: { flowsheetId: string }) {
  const s = useStudy<SensitivityCase, SensitivityResult>(
    () => engine.sensitivityCases(flowsheetId),
    blankSensitivity,
  );
  const c = s.current;
  if (!s.cases || !c) return <p className="muted">{s.error ?? "Loading…"}</p>;
  const set = (change: Partial<SensitivityCase>) => s.setCurrent({ ...c, ...change });
  const iv = c.independent;

  const save = async () => {
    const saved = await s.attempt(() => engine.saveSensitivity(flowsheetId, c));
    if (saved) await s.load(saved.index);
    return saved;
  };
  const run = async () => {
    const saved = await save();
    if (saved?.index !== undefined)
      await s.run(saved.index, (id) => engine.runSensitivity(flowsheetId, saved.index!, id));
  };

  return (
    <div className="analysis-body" data-testid="sensitivity">
      <CaseBar
        cases={s.cases}
        current={c}
        busy={s.busy}
        onPick={(i) => {
          s.setCurrent(structuredClone(s.cases![i]!));
          s.setResult(null);
        }}
        onNew={() => {
          s.setCurrent(blankSensitivity(s.cases!.length + 1));
          s.setResult(null);
        }}
        onSave={() => void save()}
        onDelete={async () => {
          if (await s.attempt(() => engine.deleteSensitivity(flowsheetId, c.index!))) await s.load();
          s.setResult(null);
        }}
      />
      <div className="schema-fields studies-form">
        <label className="schema-row">
          <span className="schema-label">Name</span>
          <span className="schema-value">
            <input
              className="input w-full"
              value={c.name}
              onChange={(e) => set({ name: e.target.value })}
              aria-label="Case name"
            />
          </span>
        </label>
        <div className="schema-row">
          <span className="schema-label">Vary</span>
          <span className="schema-value">
            <VariablePicker
              flowsheetId={flowsheetId}
              value={iv}
              writable
              label="Independent variable"
              onChange={(v) => set({ independent: { ...iv, ...v } })}
            />
          </span>
        </div>
        <div className="schema-row">
          <span className="schema-label">From, to{iv.unit ? ` (${unitLabel(iv.unit)})` : ""}, points</span>
          <span className="schema-value studies-range">
            <Num value={iv.from} onChange={(v) => set({ independent: { ...iv, from: v } })} label="From" />
            <Num value={iv.to} onChange={(v) => set({ independent: { ...iv, to: v } })} label="To" />
            <Num
              value={iv.points}
              onChange={(v) => set({ independent: { ...iv, points: Math.max(2, Math.round(v ?? 2)) } })}
              label="Points"
            />
          </span>
        </div>
        {c.dependents.map((d, i) => (
          <div key={i} className="schema-row">
            <span className="schema-label">{i === 0 ? "Observe" : ""}</span>
            <span className="schema-value flex gap-1">
              <VariablePicker
                flowsheetId={flowsheetId}
                value={d}
                writable={false}
                label={`Dependent variable ${i + 1}`}
                onChange={(v) => set({ dependents: c.dependents.map((x, j) => (j === i ? v : x)) })}
              />
              <button
                className="btn btn-icon btn-ghost"
                onClick={() => set({ dependents: c.dependents.filter((_, j) => j !== i) })}
                aria-label={`Remove dependent variable ${i + 1}`}
              >
                <Trash2 size={13} />
              </button>
            </span>
          </div>
        ))}
        <div>
          <button
            className="btn btn-ghost"
            onClick={() =>
              set({ dependents: [...c.dependents, { object: null, property: null, unit: null }] })
            }
          >
            <Plus size={13} /> Observe another variable
          </button>
        </div>
      </div>
      <RunBar busy={s.busy} onRun={() => void run()} onCancel={s.cancel} label="Run" />
      {s.error && <p className="callout callout-error">{s.error}</p>}
      {s.result && <SensitivityView result={s.result} name={c.name} stale={s.busy} />}
    </div>
  );
}

function SensitivityView({
  result,
  name,
  stale,
}: {
  result: SensitivityResult;
  name: string;
  stale: boolean;
}) {
  return (
    <div className={stale ? "is-stale" : ""} data-testid="sensitivity-result">
      {result.cancelled && (
        <p className="callout callout-warning">Cancelled; the points done so far are shown.</p>
      )}
      {result.warnings.map((w, i) => (
        <p key={i} className="callout callout-warning">
          {w}
        </p>
      ))}
      {result.charts.map((c, i) => (
        <XYChart key={i} chart={c} />
      ))}
      <div className="flex items-center mt-2">
        <span className="muted text-[12px]">{result.table.rows.length} points</span>
        <button
          className="btn btn-ghost ml-auto"
          onClick={() =>
            downloadText(
              fileName(`${name} sensitivity`, "csv"),
              toCsv([result.table.columns, ...result.table.rows]),
            )
          }
        >
          <Download size={13} /> CSV
        </button>
      </div>
      <div className="table-scroll table-scroll-tall">
        <table className="grid-table">
          <thead>
            <tr>
              {result.table.columns.map((h) => (
                <th key={h}>{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {result.table.rows.map((r, i) => (
              <tr key={i}>
                {r.map((v, j) => (
                  <td key={j} className="num">
                    {v === null ? "—" : formatNumber(v, 6)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ optimizer

const blankOptimizer = (n: number): OptimizerCase => ({
  name: `Case ${n}`,
  description: "",
  type: "Minimization",
  objective: "Variable",
  expression: "",
  method: "DN_NELDERMEAD_SIMPLEX",
  maxIterations: 100,
  tolerance: 1e-6,
  epsilon: 1e-3,
  barrier: 1e-4,
  variables: [
    {
      kind: "Independent",
      name: "x1",
      object: null,
      property: null,
      unit: null,
      initial: null,
      lower: null,
      upper: null,
    },
    {
      kind: "Dependent",
      name: "y1",
      object: null,
      property: null,
      unit: null,
      initial: null,
      lower: null,
      upper: null,
    },
  ],
});

const KINDS: { value: OptimizerVariable["kind"]; label: string }[] = [
  { value: "Independent", label: "Varied" },
  { value: "Dependent", label: "Objective / result" },
  { value: "Auxiliary", label: "Auxiliary" },
  { value: "Constraint", label: "Constraint" },
];

function Optimizer({ flowsheetId }: { flowsheetId: string }) {
  const s = useStudy<OptimizerCase, OptimizerResult>(
    () => engine.optimizerCases(flowsheetId),
    blankOptimizer,
  );
  const [methods, setMethods] = useState<ChoiceOption[]>([]);
  useEffect(() => {
    engine.optimizerMethods().then(setMethods, () => setMethods([]));
  }, []);
  const c = s.current;
  if (!s.cases || !c) return <p className="muted">{s.error ?? "Loading…"}</p>;
  const set = (change: Partial<OptimizerCase>) => s.setCurrent({ ...c, ...change });
  const setVar = (i: number, change: Partial<OptimizerVariable>) =>
    set({ variables: c.variables.map((v, j) => (j === i ? { ...v, ...change } : v)) });

  const save = async () => {
    const saved = await s.attempt(() => engine.saveOptimizer(flowsheetId, c));
    if (saved) await s.load(saved.index);
    return saved;
  };
  const run = async () => {
    const saved = await save();
    if (saved?.index !== undefined)
      await s.run(saved.index, (id) => engine.runOptimizer(flowsheetId, saved.index!, id));
  };

  return (
    <div className="analysis-body" data-testid="optimizer">
      <CaseBar
        cases={s.cases}
        current={c}
        busy={s.busy}
        onPick={(i) => {
          s.setCurrent(structuredClone(s.cases![i]!));
          s.setResult(null);
        }}
        onNew={() => {
          s.setCurrent(blankOptimizer(s.cases!.length + 1));
          s.setResult(null);
        }}
        onSave={() => void save()}
        onDelete={async () => {
          if (await s.attempt(() => engine.deleteOptimizer(flowsheetId, c.index!))) await s.load();
          s.setResult(null);
        }}
      />
      <div className="analysis-controls">
        <label className="field">
          <span className="field-label">Name</span>
          <input
            className="input"
            value={c.name}
            onChange={(e) => set({ name: e.target.value })}
            aria-label="Case name"
          />
        </label>
        <label className="field">
          <span className="field-label">Goal</span>
          <select
            className="select"
            value={c.type}
            onChange={(e) => set({ type: e.target.value as OptimizerCase["type"] })}
            aria-label="Goal"
          >
            <option value="Minimization">Minimise</option>
            <option value="Maximization">Maximise</option>
          </select>
        </label>
        <label className="field">
          <span className="field-label">Objective</span>
          <select
            className="select"
            value={c.objective}
            onChange={(e) => set({ objective: e.target.value as OptimizerCase["objective"] })}
            aria-label="Objective"
          >
            <option value="Variable">The first objective variable</option>
            <option value="Expression">An expression of the variables' names</option>
          </select>
        </label>
        {c.objective === "Expression" && (
          <label className="field">
            <span className="field-label">Expression</span>
            <input
              className="input"
              value={c.expression}
              onChange={(e) => set({ expression: e.target.value })}
              placeholder="(T - 330)^2"
              aria-label="Expression"
            />
          </label>
        )}
        <label className="field">
          <span className="field-label">Method</span>
          <select
            className="select"
            value={c.method}
            onChange={(e) => set({ method: e.target.value })}
            aria-label="Method"
          >
            {methods.map((m) => (
              <option key={m.value} value={m.value}>
                {m.label}
              </option>
            ))}
          </select>
        </label>
        <label className="field studies-small">
          <span className="field-label">Max evaluations</span>
          <Num
            value={c.maxIterations}
            onChange={(v) => set({ maxIterations: Math.max(1, Math.round(v ?? 1)) })}
            label="Max evaluations"
          />
        </label>
        <label className="field studies-small">
          <span className="field-label">Tolerance</span>
          <Num value={c.tolerance} onChange={(v) => set({ tolerance: v ?? 1e-6 })} label="Tolerance" />
        </label>
      </div>
      <div className="table-scroll">
        <table className="grid-table optimizer-variables">
          <thead>
            <tr>
              <th>Role</th>
              <th>Name</th>
              <th>Variable</th>
              <th>Initial</th>
              <th>Lower</th>
              <th>Upper</th>
              <th>Now</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {c.variables.map((v, i) => (
              <tr key={v.id ?? i}>
                <td>
                  <select
                    className="select select-sm"
                    value={v.kind}
                    onChange={(e) => setVar(i, { kind: e.target.value as OptimizerVariable["kind"] })}
                    aria-label={`Role of variable ${i + 1}`}
                  >
                    {KINDS.map((k) => (
                      <option key={k.value} value={k.value}>
                        {k.label}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <input
                    className="input input-sm"
                    value={v.name}
                    onChange={(e) => setVar(i, { name: e.target.value })}
                    aria-label={`Name of variable ${i + 1}`}
                  />
                </td>
                <td>
                  <VariablePicker
                    flowsheetId={flowsheetId}
                    value={v}
                    writable={v.kind === "Independent"}
                    label={`Variable ${i + 1}`}
                    onChange={(x) => setVar(i, x)}
                  />
                </td>
                <td>
                  {v.kind === "Independent" && (
                    <Num
                      value={v.initial}
                      onChange={(x) => setVar(i, { initial: x })}
                      label={`Initial value of variable ${i + 1}`}
                    />
                  )}
                </td>
                <td>
                  {(v.kind === "Independent" || v.kind === "Constraint") && (
                    <Num
                      value={v.lower}
                      onChange={(x) => setVar(i, { lower: x })}
                      label={`Lower limit of variable ${i + 1}`}
                      placeholder="—"
                    />
                  )}
                </td>
                <td>
                  {(v.kind === "Independent" || v.kind === "Constraint") && (
                    <Num
                      value={v.upper}
                      onChange={(x) => setVar(i, { upper: x })}
                      label={`Upper limit of variable ${i + 1}`}
                      placeholder="—"
                    />
                  )}
                </td>
                <td className="num">{v.current == null ? "" : formatNumber(v.current, 6)}</td>
                <td>
                  <button
                    className="btn btn-icon btn-ghost"
                    onClick={() => set({ variables: c.variables.filter((_, j) => j !== i) })}
                    aria-label={`Remove variable ${i + 1}`}
                  >
                    <Trash2 size={13} />
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div>
        <button
          className="btn btn-ghost"
          onClick={() =>
            set({
              variables: [
                ...c.variables,
                {
                  kind: "Independent",
                  name: `v${c.variables.length + 1}`,
                  object: null,
                  property: null,
                  unit: null,
                  initial: null,
                  lower: null,
                  upper: null,
                },
              ],
            })
          }
        >
          <Plus size={13} /> Add a variable
        </button>
      </div>
      <RunBar busy={s.busy} onRun={() => void run()} onCancel={s.cancel} label="Optimise" />
      {s.error && <p className="callout callout-error">{s.error}</p>}
      {s.result && (
        <div className={s.busy ? "is-stale" : ""} data-testid="optimizer-result">
          {s.result.error && <p className="callout callout-error">The method stopped: {s.result.error}</p>}
          <p className="tools-summary">
            {s.result.cancelled
              ? "Cancelled; the initial values are back in place."
              : `Best objective ${formatNumber(s.result.best, 6)} after ${s.result.evaluations} evaluations. The flowsheet now holds the optimum (undo puts it back).`}
          </p>
          <div className="schema-fields">
            {s.result.variables.map((v) => (
              <div key={v.id} className="schema-row">
                <span className="schema-label">
                  {v.name || v.label} <span className="muted text-[11px]">{v.label}</span>
                </span>
                <span className="schema-value readonly">
                  {formatNumber(v.current, 6)} <span className="unit">{v.unit ? unitLabel(v.unit) : ""}</span>
                </span>
              </div>
            ))}
          </div>
          <XYChart chart={s.result.chart} />
        </div>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ scenarios

/**
 * Two snapshots of the solved flowsheet side by side: capture A, change something and solve,
 * capture B, and see what moved, the changed specifications first.
 */
function Scenarios({ flowsheetId }: { flowsheetId: string }) {
  const [slots, setSlots] = useState<{ A: ScenarioSlot | null; B: ScenarioSlot | null }>({
    A: null,
    B: null,
  });
  const [labels, setLabels] = useState({ A: "A", B: "B" });
  const [result, setResult] = useState<ScenarioComparison | null>(null);
  const [onlyChanged, setOnlyChanged] = useState(true);
  const [filter, setFilter] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    engine.scenarios(flowsheetId).then(setSlots, () => undefined);
  }, [flowsheetId]);

  const capture = async (slot: "A" | "B") => {
    setError(null);
    try {
      const s = await engine.captureScenario(flowsheetId, slot, labels[slot]);
      setSlots(s);
      setResult(s.A && s.B ? await engine.compareScenarios(flowsheetId) : null);
    } catch (e) {
      setError(EngineError.from(e).message);
    }
  };

  const q = filter.trim().toLowerCase();
  const rows = (result?.rows ?? []).filter(
    (r) => (!onlyChanged || r.changed) && (!q || `${r.object} ${r.property}`.toLowerCase().includes(q)),
  );
  const fmt = (v: number | string | null) => (typeof v === "number" ? formatNumber(v, 6) : (v ?? ""));

  return (
    <div className="analysis-body" data-testid="scenarios">
      <p className="muted">
        Solve, capture A; change something and solve again, capture B. The comparison lists the changed
        specifications first, then the results by how much they moved. Snapshots last until the app closes.
      </p>
      <div className="analysis-controls">
        {(["A", "B"] as const).map((slot) => (
          <div key={slot} className="scenario-slot">
            <label className="field">
              <span className="field-label">Scenario {slot}</span>
              <input
                className="input"
                value={labels[slot]}
                onChange={(e) => setLabels({ ...labels, [slot]: e.target.value })}
                aria-label={`Scenario ${slot} label`}
              />
            </label>
            <button className="btn" onClick={() => void capture(slot)} data-testid={`capture-${slot}`}>
              Capture {slot}
            </button>
            <span className="muted text-[11px]">
              {slots[slot]
                ? `${slots[slot]!.label}, ${new Date(slots[slot]!.taken).toLocaleTimeString()}`
                : "not captured"}
            </span>
          </div>
        ))}
      </div>
      {error && <p className="callout callout-error">{error}</p>}
      {result && (
        <div data-testid="scenario-result">
          {result.summary.map((line, i) => (
            <p key={i} className={i === 0 ? "tools-summary" : "muted text-[12px]"}>
              {line}
            </p>
          ))}
          <div className="flex items-center gap-2 mt-2">
            <label className="toggle">
              <input
                type="checkbox"
                checked={onlyChanged}
                onChange={(e) => setOnlyChanged(e.target.checked)}
              />{" "}
              Only what changed
            </label>
            <input
              className="input input-sm"
              placeholder="filter by object or property"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              aria-label="Filter"
            />
            <span className="muted text-[12px]">{rows.length} rows</span>
            <button
              className="btn btn-ghost ml-auto"
              onClick={() =>
                downloadText(
                  fileName(`${result.labelA} vs ${result.labelB}`, "csv"),
                  toCsv([
                    ["Object", "Property", "Kind", result.labelA, result.labelB, "Change", "%", "Unit"],
                    ...rows.map((r) => [
                      r.object,
                      r.property,
                      r.input ? "specification" : "result",
                      r.a,
                      r.b,
                      r.delta,
                      r.percent,
                      r.unit,
                    ]),
                  ]),
                )
              }
            >
              <Download size={13} /> CSV
            </button>
          </div>
          <div className="table-scroll table-scroll-tall">
            <table className="grid-table">
              <thead>
                <tr>
                  <th>Object</th>
                  <th>Property</th>
                  <th>{result.labelA}</th>
                  <th>{result.labelB}</th>
                  <th>Change</th>
                  <th>%</th>
                  <th>Unit</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={i} className={r.input ? "is-spec" : ""}>
                    <td>{r.object}</td>
                    <td>
                      {r.property}
                      {r.input && <span className="badge"> specification</span>}
                    </td>
                    <td className="num">{fmt(r.a)}</td>
                    <td className="num">{fmt(r.b)}</td>
                    <td className="num">{r.delta === null ? "" : formatNumber(r.delta, 4)}</td>
                    <td className="num">
                      {r.percent === null ? "" : `${r.percent > 0 ? "+" : ""}${r.percent.toFixed(1)}`}
                    </td>
                    <td>{r.unit ? unitLabel(r.unit) : ""}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}
