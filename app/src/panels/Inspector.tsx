import { AlertTriangle, Search } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { typeLabel } from "../canvas/catalog";
import { ProcessSymbol } from "../canvas/symbols";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type { GraphNode, ObjectProperties, PropertyEntry, StreamDetails, UnitSystem } from "../engine/types";
import { formatNumber, formatQuantity } from "../lib/units";
import { useApp } from "../state/store";

/** Reads engine data for the selection, again whenever the store's revision moves on. */
function useEngineData<T>(
  load: (() => Promise<T>) | null,
  deps: unknown[],
): { data: T | null; error: string | null } {
  const [state, setState] = useState<{ data: T | null; error: string | null }>({ data: null, error: null });
  useEffect(() => {
    if (!load) {
      setState({ data: null, error: null });
      return;
    }
    let live = true;
    load()
      .then((data) => live && setState({ data, error: null }))
      .catch((e) => live && setState({ data: null, error: EngineError.from(e).message }));
    return () => {
      live = false;
    };
  }, deps);
  return state;
}

export function Inspector() {
  const flowsheet = useApp((s) => s.flowsheet);
  const graph = useApp((s) => s.graph);
  const selection = useApp((s) => s.selection);
  const node = graph?.nodes.find((n) => n.id === selection) ?? null;

  if (!flowsheet) return <aside className="panel inspector" aria-label="Inspector" />;
  return (
    <aside className="panel inspector" aria-label="Inspector" data-testid="inspector">
      {node ? (
        <ObjectInspector flowsheetId={flowsheet.id} node={node} units={flowsheet.unitSystem} />
      ) : (
        <FlowsheetInfo />
      )}
    </aside>
  );
}

function FlowsheetInfo() {
  const fs = useApp((s) => s.flowsheet)!;
  const graph = useApp((s) => s.graph);
  const streams = graph?.nodes.filter((n) => n.type === "MaterialStream").length ?? 0;
  const units =
    (graph?.nodes.length ?? 0) -
    streams -
    (graph?.nodes.filter((n) => n.type === "EnergyStream").length ?? 0);
  return (
    <div className="inspector-body">
      <h2 className="inspector-title">{fs.name}</h2>
      <p className="muted break-all">{fs.path ?? "Not saved yet"}</p>
      <dl className="facts">
        <dt>Material streams</dt>
        <dd>{streams}</dd>
        <dt>Unit operations</dt>
        <dd>{units}</dd>
        <dt>Unit system</dt>
        <dd>{fs.unitSystem?.name ?? "—"}</dd>
        <dt>Property packages</dt>
        <dd>{fs.propertyPackages.map((p) => p.model).join(", ") || "None"}</dd>
      </dl>
      <h3 className="section-title">Compounds ({fs.compounds.length})</h3>
      <div className="chips">
        {fs.compounds.map((c) => (
          <span key={c} className="chip">
            {c}
          </span>
        ))}
      </div>
      <p className="muted mt-4">Select an object on the flowsheet to see and edit it.</p>
    </div>
  );
}

function ObjectInspector({
  flowsheetId,
  node,
  units,
}: {
  flowsheetId: string;
  node: GraphNode;
  units: UnitSystem | null;
}) {
  const revision = useApp((s) => s.revision);
  const rename = useApp((s) => s.rename);
  const [tag, setTag] = useState(node.tag);
  useEffect(() => {
    setTag(node.tag);
  }, [node.tag]);

  const isStream = node.type === "MaterialStream" || node.type === "EnergyStream";
  const stream = useEngineData<StreamDetails>(isStream ? () => engine.stream(flowsheetId, node.id) : null, [
    flowsheetId,
    node.id,
    revision,
  ]);

  return (
    <div className="inspector-body">
      <div className="inspector-head">
        <ProcessSymbol type={node.type} className="inspector-symbol" />
        <div className="min-w-0 flex-1">
          <input
            className="tag-input"
            value={tag}
            aria-label="Name"
            onChange={(e) => setTag(e.target.value)}
            onBlur={() => tag.trim() && tag !== node.tag && void rename(node.id, tag.trim())}
            onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()}
          />
          <div className="muted">
            {typeLabel(node.type)} ·{" "}
            <span className={`status-text status-${node.status}`}>{statusText(node)}</span>
          </div>
        </div>
      </div>
      {node.error && (
        <div className="callout callout-error">
          <AlertTriangle size={16} /> <span>{node.error}</span>
        </div>
      )}
      {stream.data?.kind === "material" && <StreamSummary s={stream.data} units={units} />}
      {stream.data?.kind === "energy" && (
        <div className="metrics">
          <Metric label="Power" value={formatQuantity("power", stream.data.power_kW, units?.heatflow)} />
        </div>
      )}
      <PropertyList flowsheetId={flowsheetId} id={node.id} revision={revision} />
    </div>
  );
}

function statusText(n: GraphNode) {
  if (n.error) return "Error";
  if (!n.active) return "Inactive";
  return n.calculated ? "Solved" : "Not solved";
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div className="metric">
      <div className="metric-label">{label}</div>
      <div className="metric-value">{value}</div>
    </div>
  );
}

function StreamSummary({
  s,
  units,
}: {
  s: Extract<StreamDetails, { kind: "material" }>;
  units: UnitSystem | null;
}) {
  const [basis, setBasis] = useState<"mole" | "mass">("mole");
  const mixture = s.phases.find((p) => p.index === 0);
  const others = s.phases.filter((p) => p.index !== 0 && p.index !== 1);
  return (
    <>
      <div className="metrics">
        <Metric
          label="Temperature"
          value={formatQuantity("temperature", s.temperature, units?.temperature)}
        />
        <Metric label="Pressure" value={formatQuantity("pressure", s.pressure, units?.pressure)} />
        <Metric label="Mass flow" value={formatQuantity("massFlow", s.massFlow, units?.massflow)} />
        <Metric label="Molar flow" value={formatQuantity("molarFlow", s.molarFlow, units?.molarflow)} />
        <Metric label="Vapor fraction" value={formatNumber(s.vaporFraction)} />
      </div>
      {mixture && (
        <section>
          <div className="panel-header">
            <h3 className="section-title">Composition</h3>
            <div className="segmented" role="radiogroup" aria-label="Basis">
              {(["mole", "mass"] as const).map((b) => (
                <button
                  key={b}
                  role="radio"
                  aria-checked={basis === b}
                  className={basis === b ? "is-on" : ""}
                  onClick={() => setBasis(b)}
                >
                  {b === "mole" ? "Mole" : "Mass"}
                </button>
              ))}
            </div>
          </div>
          <div className="table-scroll">
            <table className="grid-table" data-testid="composition">
              <thead>
                <tr>
                  <th>Compound</th>
                  <th>Overall</th>
                  {others.map((p) => (
                    <th key={p.index}>{p.name}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {mixture.compounds.map((c, i) => (
                  <tr key={c.name}>
                    <td>{c.name}</td>
                    <td className="num">
                      {formatNumber(basis === "mole" ? c.moleFraction : c.massFraction)}
                    </td>
                    {others.map((p) => {
                      const pc = p.compounds[i];
                      return (
                        <td key={p.index} className="num">
                          {formatNumber(basis === "mole" ? pc?.moleFraction : pc?.massFraction)}
                        </td>
                      );
                    })}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </>
  );
}

const MAX_ROWS = 250;

function PropertyList({ flowsheetId, id, revision }: { flowsheetId: string; id: string; revision: number }) {
  const [filter, setFilter] = useState("");
  const [editableOnly, setEditableOnly] = useState(true);
  const [version, setVersion] = useState(0);
  const { data, error } = useEngineData<ObjectProperties>(
    () => engine.properties(flowsheetId, id),
    [flowsheetId, id, revision, version],
  );

  const rows = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return (data?.properties ?? []).filter(
      (p) =>
        (!editableOnly || p.writable) &&
        (!f || p.label.toLowerCase().includes(f) || p.id.toLowerCase().includes(f)),
    );
  }, [data, filter, editableOnly]);

  return (
    <section>
      <div className="panel-header">
        <h3 className="section-title">Properties</h3>
        <label className="toggle">
          <input type="checkbox" checked={editableOnly} onChange={(e) => setEditableOnly(e.target.checked)} />
          Editable only
        </label>
      </div>
      <div className="search">
        <Search size={14} />
        <input
          placeholder="Filter properties…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          aria-label="Filter properties"
        />
      </div>
      {error && <div className="callout callout-error">{error}</div>}
      <table className="grid-table props" data-testid="properties">
        <tbody>
          {rows.slice(0, MAX_ROWS).map((p) => (
            <PropertyRow
              key={p.id}
              p={p}
              flowsheetId={flowsheetId}
              objectId={id}
              onChanged={() => setVersion((v) => v + 1)}
            />
          ))}
        </tbody>
      </table>
      {rows.length > MAX_ROWS && (
        <p className="muted">
          Showing {MAX_ROWS} of {rows.length}; filter to narrow the list.
        </p>
      )}
      {data && rows.length === 0 && <p className="muted">No properties match.</p>}
    </section>
  );
}

function PropertyRow({
  p,
  flowsheetId,
  objectId,
  onChanged,
}: {
  p: PropertyEntry;
  flowsheetId: string;
  objectId: string;
  onChanged: () => void;
}) {
  const log = useApp((s) => s.log);
  const shown =
    typeof p.value === "number" ? formatNumber(p.value, 6) : p.value === null ? "" : String(p.value);
  const [draft, setDraft] = useState(shown);
  const [saving, setSaving] = useState(false);
  useEffect(() => {
    setDraft(shown);
  }, [shown]);

  const commit = async () => {
    if (draft === shown) return;
    const value =
      typeof p.value === "number" || p.value === null
        ? Number(draft)
        : typeof p.value === "boolean"
          ? draft === "true"
          : draft;
    if (typeof value === "number" && !Number.isFinite(value)) {
      log("warning", `${p.label}: "${draft}" is not a number.`);
      setDraft(shown);
      return;
    }
    setSaving(true);
    try {
      await engine.setProperty(flowsheetId, objectId, p.id, value);
      onChanged();
      // Other objects' state may depend on this one; the canvas re-reads statuses.
      void useApp.getState().refresh();
    } catch (e) {
      log("error", `${p.label}: ${EngineError.from(e).message}`);
      setDraft(shown);
    } finally {
      setSaving(false);
    }
  };

  return (
    <tr>
      <th scope="row" title={p.id}>
        {p.label}
      </th>
      <td className="num">
        {p.writable && typeof p.value !== "boolean" ? (
          <input
            className={`cell-input ${saving ? "is-saving" : ""}`}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onBlur={() => void commit()}
            onKeyDown={(e) => {
              if (e.key === "Enter") (e.target as HTMLInputElement).blur();
              if (e.key === "Escape") setDraft(shown);
            }}
            aria-label={p.label}
          />
        ) : p.writable && typeof p.value === "boolean" ? (
          <input
            type="checkbox"
            checked={p.value}
            onChange={(e) => {
              setDraft(String(e.target.checked));
              void engine
                .setProperty(flowsheetId, objectId, p.id, e.target.checked)
                .then(onChanged)
                .catch((err) => log("error", `${p.label}: ${EngineError.from(err).message}`));
            }}
            aria-label={p.label}
          />
        ) : (
          <span>{shown || "—"}</span>
        )}
      </td>
      <td className="unit">{p.units ?? ""}</td>
    </tr>
  );
}
