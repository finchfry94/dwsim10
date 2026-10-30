import { AlertTriangle, Download, Play, Square, X } from "lucide-react";
import { useEffect, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type { SchemaField, ToolInfo, ToolRun, ToolTable } from "../engine/types";
import { downloadText, fileName, toCsv } from "../lib/csv";
import { formatNumber } from "../lib/units";
import { useEscape } from "../lib/useEscape";
import { useApp } from "../state/store";
import { FieldRow } from "./SchemaEditor";
import { chartRows, XYChart } from "./XYChart";

const closeTools = () => useApp.getState().openTools(null);

/** The tool, among `tools`, that applies to an object: the first with a choice offering it. */
export function toolFor(tools: ToolInfo[], objectId: string): ToolInfo | undefined {
  return tools.find((t) =>
    t.inputs.some((f) => f.kind === "choice" && f.options.some((o) => o.value === objectId)),
  );
}

/** A tool's inputs with an object picked in the first choice that offers it. */
export function withObject(inputs: SchemaField[], objectId: string | undefined): SchemaField[] {
  if (!objectId) return inputs;
  let done = false;
  return inputs.map((f) => {
    if (done || f.kind !== "choice" || !f.options.some((o) => o.value === objectId)) return f;
    done = true;
    return { ...f, value: objectId };
  });
}

/** What tools.run takes: every input's value, and the unit of each number typed in another one. */
export function toolArguments(
  inputs: SchemaField[],
  defaults: SchemaField[],
): { values: Record<string, unknown>; units: Record<string, string> } {
  const values: Record<string, unknown> = {};
  const units: Record<string, string> = {};
  for (const f of inputs) {
    if (f.kind === "number" || f.kind === "text" || f.kind === "bool" || f.kind === "choice")
      values[f.id] = f.value;
    if (f.kind === "number" && f.unit) {
      const original = defaults.find((d) => d.id === f.id);
      // The field's own unit needs no mention; the bridge converts from it by default.
      if (!original || original.kind !== "number" || original.unit !== f.unit) units[f.id] = f.unit;
    }
  }
  return { values, units };
}

/**
 * The engineering tools (sizing, hydrates, critical point, blowdown, distillation curves,
 * McCabe–Thiele). Each describes its inputs as schema fields, so one dialog runs them all.
 */
export function ToolsDialog() {
  const open = useApp((s) => s.tools);
  const flowsheet = useApp((s) => s.flowsheet);
  useEscape(open !== null, closeTools);
  const [tools, setTools] = useState<ToolInfo[] | null>(null);
  const [toolId, setToolId] = useState<string | null>(null);
  const [inputs, setInputs] = useState<SchemaField[]>([]);
  const [result, setResult] = useState<ToolRun | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [request, setRequest] = useState<string | null>(null);

  const select = (list: ToolInfo[], id: string, objectId?: string) => {
    const tool = list.find((t) => t.id === id);
    setToolId(id);
    setInputs(withObject(tool?.inputs ?? [], objectId));
    setResult(null);
    setError(null);
  };

  // The defaults depend on the flowsheet (its streams, vessels, compounds), so read them on each open.
  useEffect(() => {
    if (!open || !flowsheet) return;
    let live = true;
    setTools(null);
    engine
      .tools(flowsheet.id)
      .then((list) => {
        if (!live) return;
        setTools(list);
        const first =
          (open.toolId && list.find((t) => t.id === open.toolId)) ||
          (open.objectId && toolFor(list, open.objectId)) ||
          list[0];
        if (first) select(list, first.id, open.objectId);
      })
      .catch((e) => live && setError(EngineError.from(e).message));
    return () => {
      live = false;
    };
  }, [open, flowsheet]);

  if (!open || !flowsheet) return null;
  const tool = tools?.find((t) => t.id === toolId);
  const busy = request !== null;

  const run = async () => {
    if (!tool) return;
    const id = `tool-${Date.now()}`;
    setRequest(id);
    setError(null);
    try {
      const { values, units } = toolArguments(inputs, tool.inputs);
      setResult(await engine.runTool(flowsheet.id, tool.id, values, units, id));
    } catch (e) {
      const err = EngineError.from(e);
      setError(err.code === -32800 ? "Cancelled." : err.message);
    } finally {
      setRequest(null);
    }
  };

  const setInput = (field: SchemaField, value: unknown, unit?: string | null) => {
    setInputs((all) =>
      all.map((f) =>
        f.id !== field.id
          ? f
          : ({ ...f, value, ...(f.kind === "number" && unit !== undefined ? { unit } : {}) } as SchemaField),
      ),
    );
    return Promise.resolve(true);
  };

  const groups = [...new Set((tools ?? []).map((t) => t.group))];

  return (
    <div className="dialog-backdrop" onMouseDown={closeTools}>
      <div
        className="dialog tools-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Tools"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="panel-header">
          <h2 className="dialog-title">Tools</h2>
          <button className="btn btn-icon btn-ghost" onClick={closeTools} aria-label="Close">
            <X size={15} />
          </button>
        </div>
        <div className="tools-layout">
          <nav className="tools-list" aria-label="Tools">
            {!tools && !error && <p className="muted">Loading…</p>}
            {groups.map((g) => (
              <div key={g}>
                <div className="tools-group">{g}</div>
                {tools!
                  .filter((t) => t.group === g)
                  .map((t) => (
                    <button
                      key={t.id}
                      className="tools-item"
                      aria-current={t.id === toolId}
                      disabled={busy}
                      onClick={() => select(tools!, t.id, open.objectId)}
                      data-testid={`tool-${t.id}`}
                    >
                      {t.title}
                    </button>
                  ))}
              </div>
            ))}
          </nav>
          <div className="tools-main">
            {tool && (
              <>
                <p className="muted tools-description">{tool.description}</p>
                <div className="schema-fields tools-inputs" data-testid="tool-inputs">
                  {inputs.map((f) => (
                    <FieldRow
                      key={f.id}
                      flowsheetId={flowsheet.id}
                      field={f}
                      disabled={busy}
                      onSet={setInput}
                    />
                  ))}
                </div>
                <div className="flex gap-2 mt-2">
                  {busy ? (
                    <button className="btn btn-danger" onClick={() => void engine.cancel(request!)}>
                      <Square size={13} /> Cancel
                    </button>
                  ) : (
                    <button className="btn btn-primary" onClick={() => void run()} data-testid="run-tool">
                      <Play size={14} /> Calculate
                    </button>
                  )}
                </div>
              </>
            )}
            {error && <p className="callout callout-error">{error}</p>}
            {busy && !result && <p className="muted">Calculating…</p>}
            {result && result.tool === toolId && (
              <ToolResult result={result} flowsheetId={flowsheet.id} stale={busy} />
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

function ToolResult({
  result,
  flowsheetId,
  stale,
}: {
  result: ToolRun;
  flowsheetId: string;
  stale: boolean;
}) {
  const ignore = () => Promise.resolve(false);
  return (
    <div className={`tools-result ${stale ? "is-stale" : ""}`} data-testid="tool-result">
      {result.summary && <p className="tools-summary">{result.summary}</p>}
      {result.warnings.map((w, i) => (
        <p key={i} className="callout callout-warning">
          <AlertTriangle size={13} /> {w}
        </p>
      ))}
      {result.sections.map((s) => (
        <section key={s.id} className="schema-section">
          <h3 className="section-title">{s.title}</h3>
          <div className="schema-fields">
            {s.fields.map((f) => (
              <FieldRow key={f.id} flowsheetId={flowsheetId} field={f} disabled onSet={ignore} />
            ))}
          </div>
        </section>
      ))}
      {result.charts.map((c, i) => (
        <div key={i} className="tools-chart">
          <XYChart chart={c} />
          <div className="flex justify-end">
            <button
              className="btn btn-ghost"
              onClick={() => downloadText(fileName(c.title, "csv"), toCsv(chartRows(c)))}
            >
              <Download size={13} /> CSV
            </button>
          </div>
        </div>
      ))}
      {result.tables.map((t, i) => (
        <ResultTable key={i} table={t} />
      ))}
    </div>
  );
}

function ResultTable({ table }: { table: ToolTable }) {
  return (
    <section className="schema-section" data-testid="tool-table">
      <div className="flex items-center">
        <h3 className="section-title">{table.title}</h3>
        <button
          className="btn btn-ghost ml-auto"
          onClick={() => downloadText(fileName(table.title, "csv"), toCsv([table.columns, ...table.rows]))}
        >
          <Download size={13} /> CSV
        </button>
      </div>
      <div className="table-scroll table-scroll-tall">
        <table className="grid-table">
          <thead>
            <tr>
              {table.columns.map((c, i) => (
                <th key={i}>{c}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {table.rows.map((r, i) => (
              <tr key={i}>
                {r.map((v, j) => (
                  <td key={j} className={typeof v === "number" ? "num" : ""}>
                    {v === null ? "—" : typeof v === "number" ? formatNumber(v, 6) : v}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
