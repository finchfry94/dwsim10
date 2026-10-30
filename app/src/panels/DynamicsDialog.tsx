import { CheckCircle2, Download, Play, Plus, Save, Square, Trash2, Wand2, X } from "lucide-react";
import { useEffect, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type {
  DynamicsEvent,
  DynamicsEventSet,
  DynamicsIntegrator,
  DynamicsIssue,
  DynamicsRun,
  DynamicsSchedule,
  DynamicsState,
} from "../engine/types";
import { downloadText, fileName, toCsv } from "../lib/csv";
import { formatNumber, unitLabel } from "../lib/units";
import { useEscape } from "../lib/useEscape";
import { useApp } from "../state/store";
import { VariablePicker } from "./StudiesDialog";
import { XYChart } from "./XYChart";

type Tab = "setup" | "integrators" | "schedules" | "events" | "run";

const TABS: { id: Tab; label: string }[] = [
  { id: "setup", label: "Setup" },
  { id: "integrators", label: "Integrators" },
  { id: "schedules", label: "Schedules" },
  { id: "events", label: "Events" },
  { id: "run", label: "Run" },
];

const closeDynamics = () => useApp.getState().openDynamics(false);

/** Group order and labels of the readiness check, as the dynamics wizard pages it. */
const CATEGORIES: { id: string; label: string }[] = [
  { id: "Overview", label: "Overview" },
  { id: "Holdup", label: "Holdup" },
  { id: "Hydraulics", label: "Hydraulics" },
  { id: "BoundarySpecs", label: "Boundary specifications" },
  { id: "Control", label: "Control" },
  { id: "Integrator", label: "Integrator and schedule" },
];

/** Dynamic simulation: getting the flowsheet ready, setting up what to run, and running it. */
export function DynamicsDialog() {
  const open = useApp((s) => s.dynamics);
  const flowsheet = useApp((s) => s.flowsheet);
  useEscape(open, closeDynamics);
  const [tab, setTab] = useState<Tab>("setup");
  const [state, setState] = useState<DynamicsState | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open || !flowsheet) return;
    setError(null);
    engine.dynamics(flowsheet.id).then(setState, (e) => setError(EngineError.from(e).message));
  }, [open, flowsheet]);

  if (!open || !flowsheet) return null;
  const id = flowsheet.id;

  /** Runs a change that answers with the new state; the flowsheet is re-read after it. */
  const change = async (action: () => Promise<DynamicsState>) => {
    setError(null);
    try {
      setState(await action());
      await useApp.getState().refresh();
      return true;
    } catch (e) {
      setError(EngineError.from(e).message);
      return false;
    }
  };

  return (
    <div className="dialog-backdrop" onMouseDown={closeDynamics}>
      <div
        className="dialog dynamics-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Dynamics"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="panel-header">
          <div className="tabs" role="tablist">
            {TABS.map((t) => (
              <button
                key={t.id}
                role="tab"
                className="tab"
                aria-selected={tab === t.id}
                onClick={() => setTab(t.id)}
              >
                {t.label}
              </button>
            ))}
          </div>
          {state && (
            <span className={`badge ${state.enabled ? "badge-ok" : ""}`} data-testid="dynamic-mode-badge">
              {state.enabled ? "Dynamic mode" : "Steady state"}
            </span>
          )}
          <button className="btn btn-icon btn-ghost" onClick={closeDynamics} aria-label="Close">
            <X size={15} />
          </button>
        </div>
        {error && <p className="callout callout-error">{error}</p>}
        <div className="studies-body">
          {!state ? (
            <p className="muted">Loading…</p>
          ) : tab === "setup" ? (
            <Setup flowsheetId={id} state={state} change={change} onState={setState} />
          ) : tab === "integrators" ? (
            <Integrators flowsheetId={id} state={state} change={change} />
          ) : tab === "schedules" ? (
            <Schedules flowsheetId={id} state={state} change={change} />
          ) : tab === "events" ? (
            <Events flowsheetId={id} state={state} change={change} />
          ) : (
            <Run flowsheetId={id} state={state} />
          )}
        </div>
      </div>
    </div>
  );
}

type Change = (action: () => Promise<DynamicsState>) => Promise<boolean>;

function Num({ value, onChange, label }: { value: number; onChange: (v: number) => void; label: string }) {
  const [draft, setDraft] = useState(String(value));
  useEffect(() => setDraft((d) => (Number(d) === value ? d : String(value))), [value]);
  return (
    <input
      className="input input-sm num"
      inputMode="decimal"
      value={draft}
      aria-label={label}
      onChange={(e) => {
        setDraft(e.target.value);
        const v = Number(e.target.value);
        if (e.target.value.trim() !== "" && Number.isFinite(v)) onChange(v);
      }}
      onBlur={() => setDraft(String(value))}
    />
  );
}

// ------------------------------------------------------------------ setup

function Setup({
  flowsheetId,
  state,
  change,
  onState,
}: {
  flowsheetId: string;
  state: DynamicsState;
  change: Change;
  onState: (s: DynamicsState) => void;
}) {
  const [issues, setIssues] = useState<DynamicsIssue[] | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const check = () => engine.dynamicsReadiness(flowsheetId).then(setIssues, () => setIssues([]));
  useEffect(() => {
    void check();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [flowsheetId, state.enabled]);

  const fix = async (keys?: string[]) => {
    try {
      const r = await engine.applyDynamicsFixes(flowsheetId, keys);
      setIssues(r.remaining);
      onState(r.state);
      setNote(
        `Applied ${r.applied.length} fix${r.applied.length === 1 ? "" : "es"}` +
          (r.failed.length ? `; ${r.failed.length} could not be applied: ${r.failed.join("; ")}` : "."),
      );
      await useApp.getState().refresh();
    } catch (e) {
      setNote(EngineError.from(e).message);
    }
  };

  const fixable = (issues ?? []).filter((i) => i.canFix);
  return (
    <div className="analysis-body" data-testid="dynamics-setup">
      <label className="toggle dynamics-mode">
        <input
          type="checkbox"
          checked={state.enabled}
          onChange={(e) => void change(() => engine.setDynamicMode(flowsheetId, e.target.checked))}
          aria-label="Dynamic mode"
        />
        Dynamic mode: pressures and holdups drive the flows, and the integrator steps the flowsheet through
        time.
      </label>
      <div className="flex items-center gap-2">
        <h3 className="section-title">What the flowsheet still needs</h3>
        {fixable.length > 0 && (
          <button
            className="btn btn-primary ml-auto"
            onClick={() => void fix()}
            data-testid="apply-all-fixes"
          >
            <Wand2 size={14} /> Apply all {fixable.length} suggestions
          </button>
        )}
      </div>
      {note && <p className="callout callout-warning">{note}</p>}
      {!issues ? (
        <p className="muted">Checking…</p>
      ) : issues.length === 0 ? (
        <p className="ok-text">
          <CheckCircle2 size={14} /> Nothing stands in the way of a dynamic run.
        </p>
      ) : (
        CATEGORIES.filter((c) => issues.some((i) => i.category === c.id)).map((c) => (
          <section key={c.id} className="schema-section">
            <h3 className="section-title">{c.label}</h3>
            {issues
              .filter((i) => i.category === c.id)
              .map((i) => (
                <div
                  key={i.key}
                  className={`issue issue-${i.severity.toLowerCase()}`}
                  data-testid="dynamics-issue"
                >
                  <span className="badge">{i.severity}</span>
                  <div className="issue-text">
                    <div>
                      {i.tag && <strong>{i.tag}: </strong>}
                      {i.message}
                    </div>
                    {i.fix && <div className="muted text-[12px]">{i.fix}</div>}
                  </div>
                  {i.canFix && (
                    <button
                      className="btn btn-ghost"
                      onClick={() => void fix([i.key])}
                      title={i.valueLabel ?? undefined}
                    >
                      Apply
                      {i.suggested !== null && i.suggested !== "" && (
                        <span className="muted">
                          {" "}
                          {typeof i.suggested === "number" ? formatNumber(i.suggested, 4) : i.suggested}
                          {i.unit ? ` ${unitLabel(i.unit)}` : ""}
                        </span>
                      )}
                    </button>
                  )}
                </div>
              ))}
          </section>
        ))
      )}
    </div>
  );
}

// ------------------------------------------------------------------ integrators

const blankIntegrator = (n: number): DynamicsIntegrator => ({
  name: `Integrator ${n}`,
  step: 5,
  duration: 3600,
  method: "ExplicitEuler",
  equilibriumEvery: 1,
  pressureFlowEvery: 1,
  controlEvery: 1,
  variables: [],
});

const METHODS = [
  { value: "ExplicitEuler", label: "Explicit Euler" },
  { value: "RungeKutta4", label: "Runge-Kutta 4" },
  { value: "ImplicitEuler", label: "Implicit Euler" },
  { value: "AdaptiveRK45", label: "Adaptive RK45" },
];

/** Picks one item of a list by id, or a new blank one, and keeps it in step with saves. */
function usePick<T extends { id?: string }>(items: T[], blank: (n: number) => T) {
  const [current, setCurrent] = useState<T>(() => structuredClone(items[0] ?? blank(1)));
  useEffect(() => {
    setCurrent((c) =>
      structuredClone(items.find((x) => x.id === c.id) ?? (c.id ? (items[0] ?? blank(1)) : c)),
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [items]);
  return [current, setCurrent] as const;
}

function ItemBar<T extends { id?: string; name: string }>({
  items,
  current,
  onPick,
  onNew,
  onSave,
  onDelete,
  label,
}: {
  items: T[];
  current: T;
  onPick: (t: T) => void;
  onNew: () => void;
  onSave: () => void;
  onDelete: () => void;
  label: string;
}) {
  return (
    <div className="analysis-controls">
      <label className="field">
        <span className="field-label">{label}</span>
        <select
          className="select"
          value={current.id ?? ""}
          onChange={(e) => {
            const t = items.find((x) => x.id === e.target.value);
            if (t) onPick(structuredClone(t));
          }}
          aria-label={label}
        >
          {items.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
          {!current.id && <option value="">{current.name} (unsaved)</option>}
        </select>
      </label>
      <button className="btn btn-ghost" onClick={onNew}>
        <Plus size={13} /> New
      </button>
      <button
        className="btn btn-ghost"
        onClick={onSave}
        data-testid={`save-${label.toLowerCase().replace(/ /g, "-")}`}
      >
        <Save size={13} /> Save
      </button>
      {current.id && (
        <button className="btn btn-ghost" onClick={onDelete}>
          <Trash2 size={13} /> Delete
        </button>
      )}
    </div>
  );
}

function Integrators({
  flowsheetId,
  state,
  change,
}: {
  flowsheetId: string;
  state: DynamicsState;
  change: Change;
}) {
  const [i, setI] = usePick(state.integrators, blankIntegrator);
  const set = (c: Partial<DynamicsIntegrator>) => setI({ ...i, ...c });
  return (
    <div className="analysis-body" data-testid="dynamics-integrators">
      <ItemBar
        label="Integrator"
        items={state.integrators}
        current={i}
        onPick={setI}
        onNew={() => setI(blankIntegrator(state.integrators.length + 1))}
        onSave={() => void change(() => engine.saveIntegrator(flowsheetId, i))}
        onDelete={() => void change(() => engine.deleteDynamics(flowsheetId, "integrator", i.id!))}
      />
      <div className="analysis-controls">
        <label className="field">
          <span className="field-label">Name</span>
          <input
            className="input"
            value={i.name}
            onChange={(e) => set({ name: e.target.value })}
            aria-label="Integrator name"
          />
        </label>
        <label className="field studies-small">
          <span className="field-label">Step (s)</span>
          <Num value={i.step} onChange={(v) => set({ step: v })} label="Step" />
        </label>
        <label className="field studies-small">
          <span className="field-label">Duration (s)</span>
          <Num value={i.duration} onChange={(v) => set({ duration: v })} label="Duration" />
        </label>
        <label className="field">
          <span className="field-label">Method</span>
          <select
            className="select"
            value={i.method}
            onChange={(e) => set({ method: e.target.value })}
            aria-label="Method"
          >
            {METHODS.map((m) => (
              <option key={m.value} value={m.value}>
                {m.label}
              </option>
            ))}
          </select>
        </label>
      </div>
      <h3 className="section-title">Recorded variables</h3>
      {i.variables.length === 0 && (
        <p className="muted">A run records only these; add at least one to see results.</p>
      )}
      {i.variables.map((v, k) => (
        <div key={v.id ?? k} className="flex items-center gap-1 mb-1">
          <VariablePicker
            flowsheetId={flowsheetId}
            value={v}
            writable={false}
            label={`Recorded variable ${k + 1}`}
            onChange={(x) =>
              set({ variables: i.variables.map((y, j) => (j === k ? { ...y, ...x, name: "" } : y)) })
            }
          />
          <button
            className="btn btn-icon btn-ghost"
            onClick={() => set({ variables: i.variables.filter((_, j) => j !== k) })}
            aria-label={`Remove recorded variable ${k + 1}`}
          >
            <Trash2 size={13} />
          </button>
        </div>
      ))}
      <div>
        <button
          className="btn btn-ghost"
          onClick={() =>
            set({ variables: [...i.variables, { name: "", object: null, property: null, unit: null }] })
          }
        >
          <Plus size={13} /> Record a variable
        </button>
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ schedules and states

const blankSchedule = (n: number): DynamicsSchedule => ({
  name: `Schedule ${n}`,
  integrator: null,
  useCurrentState: true,
  initialState: null,
  resetContents: false,
  eventSet: null,
});

function Schedules({
  flowsheetId,
  state,
  change,
}: {
  flowsheetId: string;
  state: DynamicsState;
  change: Change;
}) {
  const [s, setS] = usePick(state.schedules, blankSchedule);
  const [stateName, setStateName] = useState("");
  const set = (c: Partial<DynamicsSchedule>) => setS({ ...s, ...c });
  return (
    <div className="analysis-body" data-testid="dynamics-schedules">
      <ItemBar
        label="Schedule"
        items={state.schedules}
        current={s}
        onPick={setS}
        onNew={() =>
          setS({ ...blankSchedule(state.schedules.length + 1), integrator: state.integrators[0]?.id ?? null })
        }
        onSave={() => void change(() => engine.saveSchedule(flowsheetId, s))}
        onDelete={() => void change(() => engine.deleteDynamics(flowsheetId, "schedule", s.id!))}
      />
      <div className="schema-fields studies-form">
        <label className="schema-row">
          <span className="schema-label">Name</span>
          <span className="schema-value">
            <input
              className="input w-full"
              value={s.name}
              onChange={(e) => set({ name: e.target.value })}
              aria-label="Schedule name"
            />
          </span>
        </label>
        <label className="schema-row">
          <span className="schema-label">Integrator</span>
          <span className="schema-value">
            <select
              className="select w-full"
              value={s.integrator ?? ""}
              onChange={(e) => set({ integrator: e.target.value || null })}
              aria-label="Schedule integrator"
            >
              <option value="">Choose…</option>
              {state.integrators.map((x) => (
                <option key={x.id} value={x.id}>
                  {x.name}
                </option>
              ))}
            </select>
          </span>
        </label>
        <label className="schema-row">
          <span className="schema-label">Start from</span>
          <span className="schema-value">
            <select
              className="select w-full"
              value={s.useCurrentState ? "" : (s.initialState ?? "")}
              onChange={(e) =>
                set({ useCurrentState: e.target.value === "", initialState: e.target.value || null })
              }
              aria-label="Start from"
            >
              <option value="">The flowsheet as it is</option>
              {state.states.map((x) => (
                <option key={x} value={x}>
                  Stored state: {x}
                </option>
              ))}
            </select>
          </span>
        </label>
        <label className="schema-row">
          <span className="schema-label">Events</span>
          <span className="schema-value">
            <select
              className="select w-full"
              value={s.eventSet ?? ""}
              onChange={(e) => set({ eventSet: e.target.value || null })}
              aria-label="Schedule events"
            >
              <option value="">None</option>
              {state.eventSets.map((x) => (
                <option key={x.id} value={x.id}>
                  {x.name}
                </option>
              ))}
            </select>
          </span>
        </label>
        <label className="schema-row">
          <span className="schema-label">Empty every holdup first</span>
          <span className="schema-value">
            <input
              type="checkbox"
              checked={s.resetContents}
              onChange={(e) => set({ resetContents: e.target.checked })}
              aria-label="Reset contents"
            />
          </span>
        </label>
      </div>
      <h3 className="section-title mt-3">Stored states</h3>
      <p className="muted">
        A stored state is the flowsheet's process data at one moment; a schedule can start every run from it.
      </p>
      <div className="flex items-center gap-2">
        <input
          className="input"
          placeholder="name"
          value={stateName}
          onChange={(e) => setStateName(e.target.value)}
          aria-label="State name"
        />
        <button
          className="btn"
          onClick={async () => {
            if (await change(() => engine.storeState(flowsheetId, stateName))) setStateName("");
          }}
          data-testid="store-state"
        >
          Store the current state
        </button>
      </div>
      <ul className="state-list">
        {state.states.map((x) => (
          <li key={x}>
            {x}
            <button
              className="btn btn-icon btn-ghost"
              onClick={() => void change(() => engine.deleteState(flowsheetId, x))}
              aria-label={`Delete state ${x}`}
            >
              <Trash2 size={13} />
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

// ------------------------------------------------------------------ events

const blankEventSet = (n: number): DynamicsEventSet => ({ name: `Events ${n}`, events: [] });

const blankEvent = (): DynamicsEvent => ({
  name: "",
  enabled: true,
  time: 0,
  kind: "ChangeProperty",
  object: null,
  property: null,
  unit: null,
  value: "",
  script: null,
  transition: "StepChange",
});

function Events({
  flowsheetId,
  state,
  change,
}: {
  flowsheetId: string;
  state: DynamicsState;
  change: Change;
}) {
  const [set, setSet] = usePick(state.eventSets, blankEventSet);
  const edit = (k: number, c: Partial<DynamicsEvent>) =>
    setSet({ ...set, events: set.events.map((e, j) => (j === k ? { ...e, ...c } : e)) });
  return (
    <div className="analysis-body" data-testid="dynamics-events">
      <ItemBar
        label="Event set"
        items={state.eventSets}
        current={set}
        onPick={setSet}
        onNew={() => setSet(blankEventSet(state.eventSets.length + 1))}
        onSave={() => void change(() => engine.saveEventSet(flowsheetId, set))}
        onDelete={() => void change(() => engine.deleteDynamics(flowsheetId, "eventSet", set.id!))}
      />
      <label className="field">
        <span className="field-label">Name</span>
        <input
          className="input"
          value={set.name}
          onChange={(e) => setSet({ ...set, name: e.target.value })}
          aria-label="Event set name"
        />
      </label>
      <div className="table-scroll">
        <table className="grid-table optimizer-variables">
          <thead>
            <tr>
              <th>On</th>
              <th>At (s)</th>
              <th>Action</th>
              <th>What</th>
              <th>Value</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {set.events.map((e, k) => (
              <tr key={e.id ?? k}>
                <td>
                  <input
                    type="checkbox"
                    checked={e.enabled}
                    onChange={(x) => edit(k, { enabled: x.target.checked })}
                    aria-label={`Event ${k + 1} enabled`}
                  />
                </td>
                <td>
                  <Num value={e.time} onChange={(v) => edit(k, { time: v })} label={`Event ${k + 1} time`} />
                </td>
                <td>
                  <select
                    className="select select-sm"
                    value={e.kind}
                    onChange={(x) => edit(k, { kind: x.target.value as DynamicsEvent["kind"] })}
                    aria-label={`Event ${k + 1} action`}
                  >
                    <option value="ChangeProperty">Set a property</option>
                    <option value="RunScript">Run a script</option>
                  </select>
                </td>
                <td>
                  {e.kind === "ChangeProperty" ? (
                    <VariablePicker
                      flowsheetId={flowsheetId}
                      value={e}
                      writable
                      label={`Event ${k + 1}`}
                      onChange={(x) => edit(k, x)}
                    />
                  ) : (
                    <select
                      className="select select-sm"
                      value={e.script ?? ""}
                      onChange={(x) => edit(k, { script: x.target.value || null })}
                      aria-label={`Event ${k + 1} script`}
                    >
                      <option value="">Choose…</option>
                      {state.scripts.map((s) => (
                        <option key={s.value} value={s.value}>
                          {s.label}
                        </option>
                      ))}
                    </select>
                  )}
                </td>
                <td>
                  {e.kind === "ChangeProperty" && (
                    <span className="unit-input">
                      <input
                        className="input input-sm num"
                        value={e.value}
                        onChange={(x) => edit(k, { value: x.target.value })}
                        aria-label={`Event ${k + 1} value`}
                      />
                      <span className="unit">{e.unit ? unitLabel(e.unit) : ""}</span>
                    </span>
                  )}
                </td>
                <td>
                  <button
                    className="btn btn-icon btn-ghost"
                    onClick={() => setSet({ ...set, events: set.events.filter((_, j) => j !== k) })}
                    aria-label={`Remove event ${k + 1}`}
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
          onClick={() => setSet({ ...set, events: [...set.events, blankEvent()] })}
        >
          <Plus size={13} /> Add an event
        </button>
      </div>
      <p className="muted text-[12px]">An event set runs when a schedule names it (Schedules tab).</p>
    </div>
  );
}

// ------------------------------------------------------------------ run

function Run({ flowsheetId, state }: { flowsheetId: string; state: DynamicsState }) {
  const progress = useApp((s) => s.dynamicsProgress);
  const [schedule, setSchedule] = useState(state.currentSchedule ?? state.schedules[0]?.id ?? "");
  const [result, setResult] = useState<DynamicsRun | null>(null);
  const [request, setRequest] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const sch = state.schedules.find((s) => s.id === schedule);
  const integrator = state.integrators.find((i) => i.id === sch?.integrator);

  // Show the last run's results when the dialog opens.
  useEffect(() => {
    if (integrator?.id && (integrator.samples ?? 0) > 0)
      engine.dynamicsResults(flowsheetId, integrator.id).then(setResult, () => undefined);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [flowsheetId, integrator?.id]);

  const run = async () => {
    const id = `dynamics-${Date.now()}`;
    setRequest(id);
    setError(null);
    useApp.setState({ dynamicsProgress: null });
    try {
      setResult(await engine.runDynamics(flowsheetId, schedule || undefined, id));
    } catch (e) {
      setError(EngineError.from(e).message);
    } finally {
      setRequest(null);
      await useApp.getState().refresh();
    }
  };

  const busy = request !== null;
  const pct = progress?.total ? Math.min(100, (100 * progress.seconds) / progress.total) : null;
  return (
    <div className="analysis-body" data-testid="dynamics-run">
      {!state.enabled && (
        <p className="callout callout-warning">Dynamic mode is off; turn it on in Setup first.</p>
      )}
      <div className="analysis-controls">
        <label className="field">
          <span className="field-label">Schedule</span>
          <select
            className="select"
            value={schedule}
            onChange={(e) => setSchedule(e.target.value)}
            aria-label="Run schedule"
            disabled={busy}
          >
            {state.schedules.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
        </label>
        {integrator && (
          <span className="muted text-[12px]">
            {integrator.name}: {formatNumber(integrator.duration, 4)} s in steps of{" "}
            {formatNumber(integrator.step, 4)} s, recording {integrator.variables.length} variable
            {integrator.variables.length === 1 ? "" : "s"}
          </span>
        )}
        {busy ? (
          <button className="btn btn-danger" onClick={() => void engine.cancel(request!)}>
            <Square size={13} /> Stop
          </button>
        ) : (
          <button
            className="btn btn-primary"
            onClick={() => void run()}
            disabled={!schedule || !state.enabled}
            data-testid="run-dynamics"
          >
            <Play size={14} /> Run
          </button>
        )}
      </div>
      {busy && (
        <div
          className="progress"
          role="progressbar"
          aria-valuenow={pct ?? undefined}
          aria-valuemin={0}
          aria-valuemax={100}
        >
          <div className="progress-bar" style={{ width: `${pct ?? 0}%` }} />
          <span className="muted text-[12px]">
            {progress ? `${formatNumber(progress.seconds, 5)} s · ${progress.text}` : "Starting…"}
          </span>
        </div>
      )}
      {error && <p className="callout callout-error">{error}</p>}
      {result && (
        <div className={busy ? "is-stale" : ""} data-testid="dynamics-result">
          {result.cancelled && (
            <p className="callout callout-warning">Stopped; the samples taken so far are shown.</p>
          )}
          {(result.errors ?? []).map((e, k) => (
            <p key={k} className="callout callout-error">
              {e}
            </p>
          ))}
          {result.samples === 0 ? (
            <p className="muted">Nothing was recorded: add variables to the integrator.</p>
          ) : (
            <>
              <p className="muted text-[12px]">
                {result.samples} samples
                {result.durationMs !== undefined ? ` in ${(result.durationMs / 1000).toFixed(1)} s` : ""}
              </p>
              {result.charts.map((c, k) => (
                <XYChart key={k} chart={c} />
              ))}
              <button
                className="btn btn-ghost"
                onClick={() =>
                  downloadText(
                    fileName(`${sch?.name ?? "dynamics"} results`, "csv"),
                    toCsv([result.table.columns, ...result.table.rows]),
                  )
                }
              >
                <Download size={13} /> CSV
              </button>
            </>
          )}
        </div>
      )}
    </div>
  );
}
