import { CheckCircle2, FlaskConical, Play, Plus, Square, Trash2, X } from "lucide-react";
import { useEffect, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type {
  CompoundEstimate,
  LightEnd,
  PetroleumOptions,
  PetroleumResult,
  RegressionModel,
  RegressionPoint,
  RegressionResult,
} from "../engine/types";
import { downloadText, fileName, toCsv } from "../lib/csv";
import { formatNumber, unitLabel } from "../lib/units";
import { useEscape } from "../lib/useEscape";
import { useApp } from "../state/store";
import { XYChart } from "./XYChart";

type Tab = "create" | "petroleum" | "regression";

const TABS: { id: Tab; label: string }[] = [
  { id: "create", label: "Create a compound" },
  { id: "petroleum", label: "Petroleum fluid" },
  { id: "regression", label: "Data regression" },
];

const closeTools = () => useApp.getState().openCompoundTools(null);

/** Compounds the databases do not have: from a structure, from petroleum data, and fitted parameters. */
export function CompoundToolsDialog() {
  const tab = useApp((s) => s.compoundTools);
  const flowsheet = useApp((s) => s.flowsheet);
  useEscape(tab !== null, closeTools);
  if (!tab || !flowsheet) return null;
  return (
    <div className="dialog-backdrop" onMouseDown={closeTools}>
      <div
        className="dialog studies-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Compound tools"
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
                onClick={() => useApp.getState().openCompoundTools(t.id)}
              >
                {t.label}
              </button>
            ))}
          </div>
          <button className="btn btn-icon btn-ghost" onClick={closeTools} aria-label="Close">
            <X size={15} />
          </button>
        </div>
        <div className="studies-body">
          {tab === "create" ? (
            <CreateCompound flowsheetId={flowsheet.id} />
          ) : tab === "petroleum" ? (
            <Petroleum flowsheetId={flowsheet.id} />
          ) : (
            <Regression flowsheetId={flowsheet.id} />
          )}
        </div>
      </div>
    </div>
  );
}

/** A number typed freely; empty means "not given". */
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

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="schema-row">
      <span className="schema-label">{label}</span>
      <span className="schema-value">{children}</span>
    </label>
  );
}

const useError = () => {
  const [error, setError] = useState<string | null>(null);
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
  return { error, attempt };
};

// ------------------------------------------------------------------ create

function CreateCompound({ flowsheetId }: { flowsheetId: string }) {
  const units = useApp((s) => s.flowsheet?.unitSystem);
  const tUnit = units?.temperature ?? "K";
  const pUnit = units?.pressure ?? "Pa";
  const [name, setName] = useState("");
  const [smiles, setSmiles] = useState("");
  const [known, setKnown] = useState<Record<"molarWeight" | "tc" | "pc" | "omega" | "tb", number | null>>({
    molarWeight: null,
    tc: null,
    pc: null,
    omega: null,
    tb: null,
  });
  const [estimate, setEstimate] = useState<CompoundEstimate | null>(null);
  const [added, setAdded] = useState<string | null>(null);
  const { error, attempt } = useError();

  const input = () => ({ name, smiles, ...known });
  const unitMap = { tc: tUnit, pc: pUnit, tb: tUnit };

  return (
    <div className="analysis-body" data-testid="compound-create">
      <p className="muted">
        A compound from its structure: Joback's group contributions estimate its constants (then Lee-Kesler
        and Rackett), and any value you know takes their place. It is saved with the flowsheet.
      </p>
      <div className="schema-fields studies-form">
        <Row label="Name">
          <input
            className="input w-full"
            value={name}
            onChange={(e) => setName(e.target.value)}
            aria-label="Compound name"
          />
        </Row>
        <Row label="Structure (SMILES)">
          <input
            className="input w-full code-input"
            value={smiles}
            onChange={(e) => setSmiles(e.target.value)}
            placeholder="CCO for ethanol"
            aria-label="SMILES"
          />
        </Row>
        <Row label="Molar weight (g/mol)">
          <Num
            value={known.molarWeight}
            onChange={(v) => setKnown({ ...known, molarWeight: v })}
            label="Molar weight"
            placeholder="from the structure"
          />
        </Row>
        <Row label={`Critical temperature (${unitLabel(tUnit)})`}>
          <Num
            value={known.tc}
            onChange={(v) => setKnown({ ...known, tc: v })}
            label="Critical temperature"
            placeholder="estimate"
          />
        </Row>
        <Row label={`Critical pressure (${unitLabel(pUnit)})`}>
          <Num
            value={known.pc}
            onChange={(v) => setKnown({ ...known, pc: v })}
            label="Critical pressure"
            placeholder="estimate"
          />
        </Row>
        <Row label="Acentric factor">
          <Num
            value={known.omega}
            onChange={(v) => setKnown({ ...known, omega: v })}
            label="Acentric factor"
            placeholder="estimate"
          />
        </Row>
        <Row label={`Normal boiling point (${unitLabel(tUnit)})`}>
          <Num
            value={known.tb}
            onChange={(v) => setKnown({ ...known, tb: v })}
            label="Normal boiling point"
            placeholder="estimate"
          />
        </Row>
      </div>
      <div className="flex gap-2">
        <button
          className="btn"
          disabled={!name.trim()}
          onClick={async () => {
            setAdded(null);
            const e = await attempt(() => engine.estimateCompound(flowsheetId, input(), unitMap));
            if (e) setEstimate(e);
          }}
          data-testid="estimate-compound"
        >
          <FlaskConical size={14} /> Estimate
        </button>
        <button
          className="btn btn-primary"
          disabled={!name.trim()}
          onClick={async () => {
            const e = await attempt(() => engine.createCompound(flowsheetId, input(), unitMap));
            if (e) {
              setEstimate(e);
              setAdded(e.name);
              await useApp.getState().refresh();
            }
          }}
          data-testid="create-compound"
        >
          <Plus size={14} /> Add to the simulation
        </button>
      </div>
      {error && <p className="callout callout-error">{error}</p>}
      {added && (
        <p className="ok-text" data-testid="compound-added">
          <CheckCircle2 size={14} /> {added} is in the simulation, at zero flow in every stream.
        </p>
      )}
      {estimate && (
        <div data-testid="compound-estimate">
          {estimate.unsupportedGroups.length > 0 && (
            <p className="callout callout-warning">
              Joback has no contribution for {estimate.unsupportedGroups.join(", ")}, so it gives no estimate:
              enter the critical constants and the acentric factor.
            </p>
          )}
          {!estimate.usable && estimate.unsupportedGroups.length === 0 && (
            <p className="callout callout-warning">
              Not enough is known yet: a structure, or the molar weight, critical temperature and pressure.
            </p>
          )}
          {Object.keys(estimate.groups).length > 0 && (
            <p className="muted text-[12px]">
              Groups:{" "}
              {Object.entries(estimate.groups)
                .map(([g, n]) => `${n} × ${g}`)
                .join(", ")}
            </p>
          )}
          <div className="table-scroll table-scroll-tall">
            <table className="grid-table">
              <thead>
                <tr>
                  <th>Property</th>
                  <th>Value</th>
                  <th>Unit</th>
                  <th>From</th>
                </tr>
              </thead>
              <tbody>
                {estimate.properties
                  .filter((p) => p.value !== "")
                  .map((p) => (
                    <tr key={p.property}>
                      <td>{p.property}</td>
                      <td className="num">{p.value}</td>
                      <td>{p.unit}</td>
                      <td className="muted">{p.origin}</td>
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

// ------------------------------------------------------------------ petroleum

function LightEnds({
  rows,
  onChange,
  candidates,
}: {
  rows: LightEnd[];
  onChange: (rows: LightEnd[]) => void;
  candidates: string[];
}) {
  return (
    <div>
      {rows.map((r, i) => (
        <div key={i} className="flex items-center gap-1 mb-1">
          <select
            className="select select-sm"
            value={r.compound}
            onChange={(e) => onChange(rows.map((x, j) => (j === i ? { ...x, compound: e.target.value } : x)))}
            aria-label={`Light end ${i + 1}`}
          >
            {candidates.map((c) => (
              <option key={c} value={c}>
                {c}
              </option>
            ))}
          </select>
          <Num
            value={r.percent}
            onChange={(v) => onChange(rows.map((x, j) => (j === i ? { ...x, percent: v ?? 0 } : x)))}
            label={`Light end ${i + 1} percent`}
          />
          <span className="muted">%</span>
          <button
            className="btn btn-icon btn-ghost"
            onClick={() => onChange(rows.filter((_, j) => j !== i))}
            aria-label={`Remove light end ${i + 1}`}
          >
            <Trash2 size={13} />
          </button>
        </div>
      ))}
      <button
        className="btn btn-ghost"
        onClick={() =>
          onChange([
            ...rows,
            { compound: candidates.find((c) => c === "Methane") ?? candidates[0] ?? "", percent: 0 },
          ])
        }
        disabled={candidates.length === 0}
      >
        <Plus size={13} /> Add a light end
      </button>
    </div>
  );
}

const CURVE_TYPES = [
  { value: "TBP", label: "True boiling point (TBP)" },
  { value: "D86", label: "ASTM D86" },
  { value: "D1160", label: "ASTM D1160 (vacuum)" },
  { value: "D2887", label: "ASTM D2887 (simulated)" },
];

function Petroleum({ flowsheetId }: { flowsheetId: string }) {
  const tUnit = useApp((s) => s.flowsheet?.unitSystem?.temperature ?? "K");
  const [options, setOptions] = useState<PetroleumOptions | null>(null);
  const [mode, setMode] = useState<"bulk" | "curve">("bulk");
  const [name, setName] = useState("OIL");
  const [count, setCount] = useState<number | null>(10);
  const [mw, setMw] = useState<number | null>(null);
  const [sg, setSg] = useState<number | null>(null);
  const [nbp, setNbp] = useState<number | null>(null);
  const [curveType, setCurveType] = useState("TBP");
  const [curveBasis, setCurveBasis] = useState("LiquidVolume");
  const [points, setPoints] = useState<{ percent: number | null; temperature: number | null }[]>([
    { percent: 0, temperature: null },
    { percent: 50, temperature: null },
    { percent: 100, temperature: null },
  ]);
  const [methods, setMethods] = useState({ mwMethod: "", tcMethod: "", pcMethod: "", omegaMethod: "" });
  const [lightEnds, setLightEnds] = useState<LightEnd[]>([]);
  const [basis, setBasis] = useState("Mole");
  const [result, setResult] = useState<PetroleumResult | null>(null);
  const [busy, setBusy] = useState(false);
  const { error, attempt } = useError();

  useEffect(() => {
    engine.petroleumOptions(flowsheetId).then(
      (o) => {
        setOptions(o);
        setMethods({ mwMethod: o.mw[0]!, tcMethod: o.tc[0]!, pcMethod: o.pc[0]!, omegaMethod: o.omega[0]! });
      },
      () => undefined,
    );
  }, [flowsheetId]);

  if (!options) return <p className="muted">Loading…</p>;

  const run = async () => {
    setBusy(true);
    const common = { name, count, temperatureUnit: tUnit, lightEnds, lightEndsBasis: basis, ...methods };
    const r = await attempt(() =>
      mode === "bulk"
        ? engine.characterizeBulk(flowsheetId, {
            ...common,
            molarWeight: mw,
            specificGravity: sg,
            boilingPoint: nbp,
          })
        : engine.characterizeCurve(flowsheetId, {
            ...common,
            curveType,
            curveBasis,
            molarWeight: mw,
            specificGravity: sg,
            points: points.filter((p) => p.percent !== null && p.temperature !== null),
          }),
    );
    if (r) {
      setResult(r);
      await useApp.getState().refresh();
    }
    setBusy(false);
  };

  const methodSelect = (key: keyof typeof methods, label: string, list: string[]) => (
    <label className="field">
      <span className="field-label">{label}</span>
      <select
        className="select"
        value={methods[key]}
        onChange={(e) => setMethods({ ...methods, [key]: e.target.value })}
        aria-label={label}
      >
        {list.map((m) => (
          <option key={m} value={m}>
            {m}
          </option>
        ))}
      </select>
    </label>
  );

  return (
    <div className="analysis-body" data-testid="petroleum">
      <div className="segmented" role="radiogroup" aria-label="Data">
        <button aria-pressed={mode === "bulk"} onClick={() => setMode("bulk")}>
          Bulk properties (C7+)
        </button>
        <button aria-pressed={mode === "curve"} onClick={() => setMode("curve")}>
          Distillation curve
        </button>
      </div>
      <div className="analysis-controls">
        <label className="field">
          <span className="field-label">Assay name</span>
          <input
            className="input"
            value={name}
            onChange={(e) => setName(e.target.value)}
            aria-label="Assay name"
          />
        </label>
        <label className="field studies-small">
          <span className="field-label">Pseudocompounds</span>
          <Num value={count} onChange={setCount} label="Pseudocompounds" />
        </label>
        <label className="field studies-small">
          <span className="field-label">Molar weight</span>
          <Num
            value={mw}
            onChange={setMw}
            label="Bulk molar weight"
            placeholder={mode === "curve" ? "optional" : ""}
          />
        </label>
        <label className="field studies-small">
          <span className="field-label">Specific gravity</span>
          <Num
            value={sg}
            onChange={setSg}
            label="Bulk specific gravity"
            placeholder={mode === "curve" ? "optional" : ""}
          />
        </label>
        {mode === "bulk" && (
          <label className="field studies-small">
            <span className="field-label">Mean boiling point ({unitLabel(tUnit)})</span>
            <Num value={nbp} onChange={setNbp} label="Mean boiling point" />
          </label>
        )}
      </div>
      {mode === "bulk" ? (
        <p className="muted text-[12px]">Give at least one of the three; the others are estimated from it.</p>
      ) : (
        <>
          <div className="analysis-controls">
            <label className="field">
              <span className="field-label">Curve</span>
              <select
                className="select"
                value={curveType}
                onChange={(e) => setCurveType(e.target.value)}
                aria-label="Curve type"
              >
                {CURVE_TYPES.map((c) => (
                  <option key={c.value} value={c.value}>
                    {c.label}
                  </option>
                ))}
              </select>
            </label>
            <label className="field">
              <span className="field-label">Distilled as</span>
              <select
                className="select"
                value={curveBasis}
                onChange={(e) => setCurveBasis(e.target.value)}
                aria-label="Curve basis"
              >
                <option value="LiquidVolume">liquid volume %</option>
                <option value="Mole">mole %</option>
                <option value="Mass">mass %</option>
              </select>
            </label>
          </div>
          <table className="grid-table optimizer-variables" data-testid="curve-points">
            <thead>
              <tr>
                <th>% distilled</th>
                <th>Temperature ({unitLabel(tUnit)})</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {points.map((p, i) => (
                <tr key={i}>
                  <td>
                    <Num
                      value={p.percent}
                      onChange={(v) => setPoints(points.map((x, j) => (j === i ? { ...x, percent: v } : x)))}
                      label={`Point ${i + 1} percent`}
                    />
                  </td>
                  <td>
                    <Num
                      value={p.temperature}
                      onChange={(v) =>
                        setPoints(points.map((x, j) => (j === i ? { ...x, temperature: v } : x)))
                      }
                      label={`Point ${i + 1} temperature`}
                    />
                  </td>
                  <td>
                    <button
                      className="btn btn-icon btn-ghost"
                      onClick={() => setPoints(points.filter((_, j) => j !== i))}
                      aria-label={`Remove point ${i + 1}`}
                    >
                      <Trash2 size={13} />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div>
            <button
              className="btn btn-ghost"
              onClick={() => setPoints([...points, { percent: null, temperature: null }])}
            >
              <Plus size={13} /> Add a point
            </button>
          </div>
        </>
      )}
      <h3 className="section-title">Light ends (of the whole fluid)</h3>
      <div className="flex gap-2 items-center">
        <LightEnds rows={lightEnds} onChange={setLightEnds} candidates={options.lightEnds} />
        {lightEnds.length > 0 && (
          <label className="field">
            <span className="field-label">Basis</span>
            <select
              className="select select-sm"
              value={basis}
              onChange={(e) => setBasis(e.target.value)}
              aria-label="Light ends basis"
            >
              {options.bases.map((b) => (
                <option key={b} value={b}>
                  {b.toLowerCase()} %
                </option>
              ))}
            </select>
          </label>
        )}
      </div>
      <h3 className="section-title">Correlations</h3>
      <div className="analysis-controls">
        {methodSelect("mwMethod", "Molar weight", options.mw)}
        {methodSelect("tcMethod", "Critical temperature", options.tc)}
        {methodSelect("pcMethod", "Critical pressure", options.pc)}
        {methodSelect("omegaMethod", "Acentric factor", options.omega)}
      </div>
      <div>
        <button
          className="btn btn-primary"
          onClick={() => void run()}
          disabled={busy}
          data-testid="characterize"
        >
          <Play size={14} /> {busy ? "Characterizing…" : "Characterize and add"}
        </button>
      </div>
      {error && <p className="callout callout-error">{error}</p>}
      {result && (
        <div data-testid="petroleum-result">
          <p className="tools-summary">
            Stream {result.tag} holds the fluid: {result.compounds.length} pseudocompounds
            {Object.keys(result.lightEnds).length > 0
              ? ` and ${Object.keys(result.lightEnds).length} light ends`
              : ""}
            . Specify its conditions and flow to use it.
          </p>
          {(result.warnings ?? []).map((w, i) => (
            <p key={i} className="callout callout-warning">
              {w}
            </p>
          ))}
          <div className="table-scroll table-scroll-tall">
            <table className="grid-table">
              <thead>
                <tr>
                  <th>Compound</th>
                  <th>Mole fraction</th>
                  <th>MW</th>
                  <th>SG</th>
                  <th>NBP (K)</th>
                  <th>Tc (K)</th>
                  <th>Pc (bar)</th>
                  <th>ω</th>
                </tr>
              </thead>
              <tbody>
                {result.compounds.map((c) => (
                  <tr key={c.name}>
                    <td>{c.name}</td>
                    <td className="num">{formatNumber(c.moleFraction, 4)}</td>
                    <td className="num">{formatNumber(c.molarWeight, 4)}</td>
                    <td className="num">{formatNumber(c.specificGravity, 4)}</td>
                    <td className="num">{formatNumber(c.boilingPoint, 4)}</td>
                    <td className="num">{formatNumber(c.criticalTemperature, 4)}</td>
                    <td className="num">
                      {c.criticalPressure === null ? "" : formatNumber(c.criticalPressure / 1e5, 4)}
                    </td>
                    <td className="num">{formatNumber(c.acentricFactor, 3)}</td>
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

// ------------------------------------------------------------------ regression

/** Rows of "T P x1 y1" (spaces, tabs, commas or semicolons) pasted from a spreadsheet or a paper. */
export function parsePoints(text: string): RegressionPoint[] {
  return text
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l && !/^[#a-z]/i.test(l))
    .map((l) => l.split(/[\s,;]+/).map(Number))
    .filter((v) => v.length >= 3 && v.slice(0, 3).every(Number.isFinite))
    .map((v) => ({ t: v[0]!, p: v[1]!, x1: v[2]!, y1: Number.isFinite(v[3]) ? v[3]! : null }));
}

function Regression({ flowsheetId }: { flowsheetId: string }) {
  const flowsheet = useApp((s) => s.flowsheet);
  const compounds = flowsheet?.compounds ?? [];
  const packages = flowsheet?.propertyPackages ?? [];
  const [catalog, setCatalog] = useState<{
    models: RegressionModel[];
    methods: string[];
    objectives: string[];
    dataTypes: string[];
  } | null>(null);
  const [comp1, setComp1] = useState(compounds[0] ?? "");
  const [comp2, setComp2] = useState(compounds[1] ?? "");
  const [modelName, setModelName] = useState("Peng-Robinson");
  const [dataType, setDataType] = useState("Pxy");
  const [tUnit, setTUnit] = useState("C");
  const [pUnit, setPUnit] = useState("bar");
  const [method, setMethod] = useState("IPOPT");
  const [objective, setObjective] = useState("Least Squares (min T/P)");
  const [params, setParams] = useState<RegressionModel["parameters"]>([]);
  const [text, setText] = useState("");
  const [result, setResult] = useState<RegressionResult | null>(null);
  const [request, setRequest] = useState<string | null>(null);
  const [applyTo, setApplyTo] = useState(packages[0]?.id ?? "");
  const [applied, setApplied] = useState<string | null>(null);
  const progress = useApp((s) => s.studyProgress);
  const { error, attempt } = useError();

  useEffect(() => {
    engine.regressionModels().then(setCatalog, () => undefined);
  }, []);
  useEffect(() => {
    const m = catalog?.models.find((x) => x.name === modelName);
    if (m) setParams(structuredClone(m.parameters));
  }, [catalog, modelName]);

  if (!catalog) return <p className="muted">Loading…</p>;
  if (compounds.length < 2)
    return <p className="muted">A regression needs two compounds in the flowsheet.</p>;

  const points = parsePoints(text);
  const model = catalog.models.find((m) => m.name === modelName);
  const run = async () => {
    const id = `regression-${Date.now()}`;
    setRequest(id);
    setApplied(null);
    useApp.setState({ studyProgress: null });
    const r = await attempt(() =>
      engine.runRegression(
        flowsheetId,
        {
          comp1,
          comp2,
          model: modelName,
          dataType,
          temperatureUnit: tUnit,
          pressureUnit: pUnit,
          method,
          objective,
          parameters: params,
          points,
        },
        id,
      ),
    );
    if (r) setResult(r);
    setRequest(null);
  };
  const matching = packages.filter((p) => p.model === model?.package);

  return (
    <div className="analysis-body" data-testid="regression">
      <p className="muted">
        Fit a model's binary interaction parameters to phase-equilibrium data, then write them into a property
        package of the flowsheet.
      </p>
      <div className="analysis-controls">
        {(
          [
            ["First compound", comp1, setComp1],
            ["Second compound", comp2, setComp2],
          ] as const
        ).map(([label, value, set]) => (
          <label key={label} className="field">
            <span className="field-label">{label}</span>
            <select className="select" value={value} onChange={(e) => set(e.target.value)} aria-label={label}>
              {compounds.map((c) => (
                <option key={c} value={c}>
                  {c}
                </option>
              ))}
            </select>
          </label>
        ))}
        <label className="field">
          <span className="field-label">Model</span>
          <select
            className="select"
            value={modelName}
            onChange={(e) => setModelName(e.target.value)}
            aria-label="Model"
          >
            {catalog.models.map((m) => (
              <option key={m.name} value={m.name}>
                {m.name}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">Data</span>
          <select
            className="select"
            value={dataType}
            onChange={(e) => setDataType(e.target.value)}
            aria-label="Data type"
          >
            {catalog.dataTypes.map((d) => (
              <option key={d} value={d}>
                {d}
              </option>
            ))}
          </select>
        </label>
        <label className="field studies-small">
          <span className="field-label">T unit</span>
          <select
            className="select"
            value={tUnit}
            onChange={(e) => setTUnit(e.target.value)}
            aria-label="Temperature unit"
          >
            {["C", "K", "F"].map((u) => (
              <option key={u} value={u}>
                {unitLabel(u)}
              </option>
            ))}
          </select>
        </label>
        <label className="field studies-small">
          <span className="field-label">P unit</span>
          <select
            className="select"
            value={pUnit}
            onChange={(e) => setPUnit(e.target.value)}
            aria-label="Pressure unit"
          >
            {["bar", "kPa", "Pa", "atm", "mmHg", "psi"].map((u) => (
              <option key={u} value={u}>
                {u}
              </option>
            ))}
          </select>
        </label>
      </div>
      <label className="field">
        <span className="field-label">Data points: one per line, T P x1 y1 (y1 may be left out)</span>
        <textarea
          className="code-editor regression-data"
          spellCheck={false}
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder={"-3.15  8.74  0.10\n-3.15  14.61  0.25"}
          aria-label="Data points"
        />
        <span className="muted text-[12px]">{points.length} points read</span>
      </label>
      <div className="analysis-controls">
        <label className="field">
          <span className="field-label">Method</span>
          <select
            className="select"
            value={method}
            onChange={(e) => setMethod(e.target.value)}
            aria-label="Method"
          >
            {catalog.methods.map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">Objective</span>
          <select
            className="select"
            value={objective}
            onChange={(e) => setObjective(e.target.value)}
            aria-label="Objective"
          >
            {catalog.objectives.map((o) => (
              <option key={o} value={o}>
                {o}
              </option>
            ))}
          </select>
        </label>
      </div>
      <table className="grid-table optimizer-variables">
        <thead>
          <tr>
            <th>Parameter</th>
            <th>Initial</th>
            <th>Lower</th>
            <th>Upper</th>
            <th>Fixed</th>
          </tr>
        </thead>
        <tbody>
          {params.map((p, i) => {
            const set = (c: Partial<typeof p>) =>
              setParams(params.map((x, j) => (j === i ? { ...x, ...c } : x)));
            return (
              <tr key={p.label}>
                <td>{p.label}</td>
                <td>
                  <Num
                    value={p.initial}
                    onChange={(v) => set({ initial: v ?? 0 })}
                    label={`${p.label} initial`}
                  />
                </td>
                <td>
                  <Num value={p.lower} onChange={(v) => set({ lower: v ?? 0 })} label={`${p.label} lower`} />
                </td>
                <td>
                  <Num value={p.upper} onChange={(v) => set({ upper: v ?? 0 })} label={`${p.label} upper`} />
                </td>
                <td>
                  <input
                    type="checkbox"
                    checked={p.fixed}
                    onChange={(e) => set({ fixed: e.target.checked })}
                    aria-label={`${p.label} fixed`}
                  />
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <div className="flex items-center gap-2">
        {request ? (
          <button className="btn btn-danger" onClick={() => void engine.cancel(request)}>
            <Square size={13} /> Cancel
          </button>
        ) : (
          <button
            className="btn btn-primary"
            onClick={() => void run()}
            disabled={comp1 === comp2 || points.length < 2}
            data-testid="run-regression"
          >
            <Play size={14} /> Regress
          </button>
        )}
        {request && <span className="muted text-[12px]">{progress?.text ?? "Starting…"}</span>}
      </div>
      {error && <p className="callout callout-error">{error}</p>}
      {result && (
        <div className={request ? "is-stale" : ""} data-testid="regression-result">
          {result.error && <p className="callout callout-error">The method stopped: {result.error}</p>}
          <div className="schema-fields studies-form">
            {Object.entries(result.parameters).map(([k, v]) => (
              <div key={k} className="schema-row">
                <span className="schema-label">{k}</span>
                <span className="schema-value readonly" data-testid={`parameter-${k}`}>
                  {formatNumber(v, 6)}
                </span>
              </div>
            ))}
            <div className="schema-row">
              <span className="schema-label">Objective</span>
              <span className="schema-value readonly">{formatNumber(result.objective, 5)}</span>
            </div>
          </div>
          {result.chart && <XYChart chart={result.chart} />}
          <div className="flex items-center gap-2 mt-2">
            {matching.length > 0 ? (
              <>
                <select
                  className="select select-sm"
                  value={applyTo}
                  onChange={(e) => setApplyTo(e.target.value)}
                  aria-label="Apply to"
                >
                  {matching.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </select>
                <button
                  className="btn"
                  onClick={async () => {
                    const r = await attempt(() =>
                      engine.applyRegression(
                        flowsheetId,
                        applyTo || matching[0]!.id,
                        result.comp1,
                        result.comp2,
                        result.parameters,
                      ),
                    );
                    if (r) {
                      setApplied(r.package);
                      await useApp.getState().afterEdit();
                    }
                  }}
                  data-testid="apply-regression"
                >
                  Apply to the property package
                </button>
              </>
            ) : (
              <span className="muted text-[12px]">
                The flowsheet has no {model?.package} package to apply them to.
              </span>
            )}
            {applied && <span className="ok-text">Applied to {applied}.</span>}
            <button
              className="btn btn-ghost ml-auto"
              onClick={() =>
                downloadText(
                  fileName(`${result.comp1} ${result.comp2} ${result.model}`, "csv"),
                  toCsv([result.table.columns, ...result.table.rows]),
                )
              }
            >
              CSV
            </button>
          </div>
          <div className="table-scroll table-scroll-tall">
            <table className="grid-table">
              <thead>
                <tr>
                  {result.table.columns.map((c) => (
                    <th key={c}>{c}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {result.table.rows.map((r, i) => (
                  <tr key={i}>
                    {r.map((v, j) => (
                      <td key={j} className="num">
                        {v === null ? "—" : formatNumber(v, 5)}
                      </td>
                    ))}
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
