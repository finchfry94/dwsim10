import { Download, FileSpreadsheet, RefreshCw } from "lucide-react";
import { useEffect, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type { StreamTable } from "../engine/types";
import { downloadBase64, downloadText, fileName, toCsv } from "../lib/csv";
import { formatNumber, unitLabel } from "../lib/units";
import { useApp } from "../state/store";

/** Every material stream side by side, in the flowsheet's units; re-read after each solve. */
export function StreamTablePanel() {
  const flowsheet = useApp((s) => s.flowsheet);
  const revision = useApp((s) => s.revision);
  const [basis, setBasis] = useState<"mole" | "mass">("mole");
  const [table, setTable] = useState<StreamTable | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);

  useEffect(() => {
    if (!flowsheet) return;
    let live = true;
    engine
      .streamTable(flowsheet.id, basis)
      .then((t) => {
        if (live) {
          setTable(t);
          setError(null);
        }
      })
      .catch((e) => live && setError(EngineError.from(e).message));
    return () => {
      live = false;
    };
  }, [flowsheet, revision, basis]);

  if (!flowsheet) return <p className="log-scroll muted">Open a flowsheet to see its streams.</p>;
  if (error) return <p className="log-scroll callout callout-error">{error}</p>;
  if (!table) return <p className="log-scroll muted">Loading…</p>;
  if (table.streams.length === 0)
    return <p className="log-scroll muted">The flowsheet has no material streams yet.</p>;

  const label = (r: StreamTable["rows"][number]) =>
    r.group === "conditions" ? r.label : `${r.label} (${basis === "mass" ? "mass" : "mole"} fraction)`;

  const exportCsv = () => {
    const rows: (string | number | null)[][] = [
      ["Property", "Unit", ...table.streams.map((s) => s.tag)],
      ...table.rows.map((r) => [label(r), r.unit ?? "", ...table.streams.map((s) => r.values[s.id] ?? null)]),
    ];
    downloadText(fileName(`${flowsheet.name} streams`, "csv"), toCsv(rows));
  };

  const exportExcel = async () => {
    setExporting(true);
    setExportError(null);
    try {
      const file = await engine.exportExcel(flowsheet.id);
      downloadBase64(
        file.name,
        file.base64,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      );
    } catch (e) {
      setExportError(EngineError.from(e).message);
    } finally {
      setExporting(false);
    }
  };

  let lastGroup = "";
  return (
    <div className="stream-table" data-testid="stream-table">
      <div className="stream-table-bar">
        <label className="toggle">
          Composition as
          <select
            className="select select-sm"
            value={basis}
            onChange={(e) => setBasis(e.target.value as "mole" | "mass")}
            aria-label="Composition basis"
          >
            <option value="mole">mole fractions</option>
            <option value="mass">mass fractions</option>
          </select>
        </label>
        <span className="muted text-[11px]">{table.streams.length} streams</span>
        <span className="ml-auto flex gap-1">
          <button className="btn btn-ghost" onClick={() => useApp.getState().refresh()} title="Read again">
            <RefreshCw size={13} />
          </button>
          <button className="btn btn-ghost" onClick={exportCsv} data-testid="export-streams">
            <Download size={13} /> CSV
          </button>
          <button
            className="btn btn-ghost"
            onClick={() => void exportExcel()}
            disabled={exporting}
            title="The stream table and every object's results, as an Excel workbook"
            data-testid="export-excel"
          >
            <FileSpreadsheet size={13} /> {exporting ? "Exporting…" : "Excel"}
          </button>
        </span>
      </div>
      {exportError && <p className="callout callout-error">{exportError}</p>}
      <div className="table-scroll stream-table-scroll">
        <table className="grid-table">
          <thead>
            <tr>
              <th className="sticky-col">Property</th>
              {table.streams.map((s) => (
                <th key={s.id} className="num">
                  <button
                    className={`link ${s.calculated ? "" : "is-pending"}`}
                    onClick={() => useApp.getState().focusObject(s.id)}
                    title={s.calculated ? `Show ${s.tag} on the flowsheet` : `${s.tag} is not calculated`}
                  >
                    {s.tag}
                  </button>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {table.rows.map((r) => {
              const heading = r.group !== lastGroup && r.group !== "conditions";
              lastGroup = r.group;
              return [
                heading && (
                  <tr key={`h-${r.group}`} className="group-row">
                    <th className="sticky-col" colSpan={table.streams.length + 1}>
                      Composition ({basis === "mass" ? "mass" : "mole"} fractions)
                    </th>
                  </tr>
                ),
                <tr key={r.key}>
                  <th className="sticky-col">
                    {r.label}
                    {r.unit && <span className="unit"> ({unitLabel(r.unit)})</span>}
                  </th>
                  {table.streams.map((s) => (
                    <td key={s.id} className="num">
                      {formatNumber(r.values[s.id], 5)}
                    </td>
                  ))}
                </tr>,
              ];
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
