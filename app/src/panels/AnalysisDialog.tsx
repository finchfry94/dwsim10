import { Download, Play, Square, X } from "lucide-react";
import { useEffect, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type { ChartData, EnvelopeDiagram } from "../engine/types";
import { downloadText, fileName, toCsv } from "../lib/csv";
import { formatNumber, unitLabel } from "../lib/units";
import { useEscape } from "../lib/useEscape";
import { useApp } from "../state/store";
import { chartRows, XYChart } from "./XYChart";

const DIAGRAMS: { value: EnvelopeDiagram; label: string }[] = [
  { value: "PT", label: "Pressure / temperature" },
  { value: "PH", label: "Pressure / enthalpy" },
  { value: "PS", label: "Pressure / entropy" },
  { value: "TH", label: "Temperature / enthalpy" },
  { value: "TS", label: "Temperature / entropy" },
  { value: "VT", label: "Volume / temperature" },
  { value: "VP", label: "Volume / pressure" },
];

const closeAnalysis = () => useApp.getState().openAnalysis(null);

/** Phase and binary envelopes, computed by the engine and charted here. */
export function AnalysisDialog() {
  const analysis = useApp((s) => s.analysis);
  const flowsheet = useApp((s) => s.flowsheet);
  useEscape(analysis !== null, closeAnalysis);
  if (!analysis || !flowsheet) return null;
  const close = closeAnalysis;
  const setTab = (tab: "phase" | "binary") => useApp.getState().openAnalysis({ ...analysis, tab });
  return (
    <div className="dialog-backdrop" onMouseDown={close}>
      <div
        className="dialog analysis-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Analysis"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="panel-header">
          <div className="tabs" role="tablist">
            <button
              role="tab"
              className="tab"
              aria-selected={analysis.tab === "phase"}
              onClick={() => setTab("phase")}
            >
              Phase envelope
            </button>
            <button
              role="tab"
              className="tab"
              aria-selected={analysis.tab === "binary"}
              onClick={() => setTab("binary")}
            >
              Binary envelope
            </button>
          </div>
          <button className="btn btn-icon btn-ghost" onClick={close} aria-label="Close">
            <X size={15} />
          </button>
        </div>
        {analysis.tab === "phase" ? (
          <PhaseEnvelope flowsheetId={flowsheet.id} initialStream={analysis.streamId} />
        ) : (
          <BinaryEnvelope flowsheetId={flowsheet.id} />
        )}
      </div>
    </div>
  );
}

/** Runs one calculation at a time, cancellable, keeping the last chart or error. */
function useCalculation() {
  const [chart, setChart] = useState<ChartData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [request, setRequest] = useState<string | null>(null);
  const run = async (calc: (requestId: string) => Promise<ChartData>) => {
    const id = `analysis-${Date.now()}`;
    setRequest(id);
    setError(null);
    try {
      setChart(await calc(id));
    } catch (e) {
      const err = EngineError.from(e);
      setError(err.code === -32800 ? "Cancelled." : err.message);
    } finally {
      setRequest(null);
    }
  };
  const cancel = () => request && void engine.cancel(request);
  return { chart, error, busy: request !== null, run, cancel };
}

function PhaseEnvelope({ flowsheetId, initialStream }: { flowsheetId: string; initialStream?: string }) {
  const nodes = useApp((s) => s.graph?.nodes ?? []);
  const streams = nodes.filter((n) => n.type === "MaterialStream");
  const [stream, setStream] = useState(initialStream ?? streams[0]?.id ?? "");
  const [diagram, setDiagram] = useState<EnvelopeDiagram>("PT");
  const [quality, setQuality] = useState<number | null>(null);
  const { chart, error, busy, run, cancel } = useCalculation();

  const calculate = () =>
    run((id) =>
      engine.phaseEnvelope(
        flowsheetId,
        stream,
        diagram,
        quality === null ? undefined : { QualityLine: true, QualityValue: quality },
        id,
      ),
    );

  // Opened from a stream: show its PT envelope straight away.
  useEffect(() => {
    if (initialStream) void calculate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="analysis-body">
      <div className="analysis-controls">
        <label className="field">
          <span className="field-label">Stream</span>
          <select
            className="select"
            value={stream}
            onChange={(e) => setStream(e.target.value)}
            aria-label="Stream"
          >
            {streams.map((s) => (
              <option key={s.id} value={s.id}>
                {s.tag}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">Diagram</span>
          <select
            className="select"
            value={diagram}
            onChange={(e) => setDiagram(e.target.value as EnvelopeDiagram)}
            aria-label="Diagram"
          >
            {DIAGRAMS.map((d) => (
              <option key={d.value} value={d.value}>
                {d.label}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">Quality line (vapor fraction)</span>
          <input
            className="input"
            inputMode="decimal"
            placeholder="none"
            value={quality ?? ""}
            onChange={(e) => {
              const v = e.target.value.trim();
              setQuality(
                v === "" || !Number.isFinite(Number(v)) ? null : Math.min(1, Math.max(0, Number(v))),
              );
            }}
            aria-label="Quality line"
            disabled={diagram !== "PT"}
          />
        </label>
        <RunButton busy={busy} disabled={!stream} onRun={calculate} onCancel={cancel} />
      </div>
      <Result chart={chart} error={error} busy={busy} />
    </div>
  );
}

function BinaryEnvelope({ flowsheetId }: { flowsheetId: string }) {
  const flowsheet = useApp((s) => s.flowsheet);
  const compounds = flowsheet?.compounds ?? [];
  const packages = flowsheet?.propertyPackages ?? [];
  const units = flowsheet?.unitSystem;
  const [c1, setC1] = useState(compounds[0] ?? "");
  const [c2, setC2] = useState(compounds[1] ?? "");
  const [pkg, setPkg] = useState(packages[0]?.id ?? "");
  const [diagram, setDiagram] = useState<"Txy" | "Pxy">("Txy");
  const [condition, setCondition] = useState("");
  const [lle, setLle] = useState(false);
  const { chart, error, busy, run, cancel } = useCalculation();

  if (compounds.length < 2)
    return <p className="muted analysis-body">A binary envelope needs two compounds in the flowsheet.</p>;

  const unit = diagram === "Txy" ? units?.pressure : units?.temperature;
  const value = condition.trim() === "" ? undefined : Number(condition);
  const calculate = () =>
    run((id) =>
      engine.binaryEnvelope(
        flowsheetId,
        {
          compound1: c1,
          compound2: c2,
          diagram,
          packageId: pkg || undefined,
          ...(diagram === "Txy" ? { pressure: value } : { temperature: value }),
          lle,
        },
        id,
      ),
    );

  const compoundSelect = (label: string, v: string, set: (s: string) => void) => (
    <label className="field">
      <span className="field-label">{label}</span>
      <select className="select" value={v} onChange={(e) => set(e.target.value)} aria-label={label}>
        {compounds.map((c) => (
          <option key={c} value={c}>
            {c}
          </option>
        ))}
      </select>
    </label>
  );

  return (
    <div className="analysis-body">
      <div className="analysis-controls">
        {compoundSelect("First compound (x axis)", c1, setC1)}
        {compoundSelect("Second compound", c2, setC2)}
        <label className="field">
          <span className="field-label">Diagram</span>
          <select
            className="select"
            value={diagram}
            onChange={(e) => {
              setDiagram(e.target.value as "Txy" | "Pxy");
              setCondition("");
            }}
            aria-label="Binary diagram"
          >
            <option value="Txy">Txy (at a pressure)</option>
            <option value="Pxy">Pxy (at a temperature)</option>
          </select>
        </label>
        <label className="field">
          <span className="field-label">
            {diagram === "Txy" ? "Pressure" : "Temperature"} {unit ? `(${unitLabel(unit)})` : ""}
          </span>
          <input
            className="input"
            inputMode="decimal"
            placeholder={diagram === "Txy" ? "1 atm" : "25 °C"}
            value={condition}
            onChange={(e) => setCondition(e.target.value)}
            aria-label={diagram === "Txy" ? "Pressure" : "Temperature"}
          />
        </label>
        {packages.length > 1 && (
          <label className="field">
            <span className="field-label">Property package</span>
            <select
              className="select"
              value={pkg}
              onChange={(e) => setPkg(e.target.value)}
              aria-label="Property package"
            >
              {packages.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </label>
        )}
        <label className="toggle">
          <input type="checkbox" checked={lle} onChange={(e) => setLle(e.target.checked)} /> Liquid-liquid
          split
        </label>
        <RunButton
          busy={busy}
          disabled={c1 === c2 || (value !== undefined && !(value > 0))}
          onRun={calculate}
          onCancel={cancel}
        />
      </div>
      {c1 === c2 && <p className="callout callout-error">Pick two different compounds.</p>}
      <Result chart={chart} error={error} busy={busy} />
    </div>
  );
}

function RunButton({
  busy,
  disabled,
  onRun,
  onCancel,
}: {
  busy: boolean;
  disabled: boolean;
  onRun: () => void;
  onCancel: () => void;
}) {
  return busy ? (
    <button className="btn btn-danger" onClick={onCancel}>
      <Square size={13} /> Cancel
    </button>
  ) : (
    <button className="btn btn-primary" onClick={onRun} disabled={disabled} data-testid="calculate">
      <Play size={14} /> Calculate
    </button>
  );
}

/** The chart, its critical point, and the data as a table and a CSV file. */
function Result({ chart, error, busy }: { chart: ChartData | null; error: string | null; busy: boolean }) {
  const [table, setTable] = useState(false);
  if (error) return <p className="callout callout-error">{error}</p>;
  if (!chart) return <p className="muted">{busy ? "Calculating…" : "Choose the settings and calculate."}</p>;
  const rows = chartRows(chart);
  const cp = chart.criticalPoint;
  return (
    <div className={busy ? "is-stale" : ""}>
      <XYChart chart={chart} />
      <div className="flex items-center gap-2 mt-2">
        {cp && (
          <span className="muted text-[12px]" data-testid="critical-point">
            Critical point: {formatNumber(cp.temperature, 5)} {unitLabel(cp.units.temperature)},{" "}
            {formatNumber(cp.pressure, 5)} {unitLabel(cp.units.pressure)}
          </span>
        )}
        <span className="ml-auto flex gap-1">
          <button className="btn btn-ghost" onClick={() => setTable(!table)} aria-pressed={table}>
            {table ? "Hide data" : "Show data"}
          </button>
          <button
            className="btn btn-ghost"
            onClick={() => downloadText(fileName(chart.title, "csv"), toCsv(rows))}
          >
            <Download size={13} /> CSV
          </button>
        </span>
      </div>
      {table && (
        <div className="table-scroll table-scroll-tall">
          <table className="grid-table">
            <thead>
              <tr>
                {rows[0]!.map((h, i) => (
                  <th key={i}>{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.slice(1).map((r, i) => (
                <tr key={i}>
                  {r.map((v, j) => (
                    <td key={j} className="num">
                      {typeof v === "number" ? formatNumber(v, 6) : ""}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
