import { Download, Printer, RefreshCw, X } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import { downloadText, fileName } from "../lib/csv";
import { useEscape } from "../lib/useEscape";
import { useApp } from "../state/store";

const SECTIONS: { key: string; label: string }[] = [
  { key: "conditions", label: "Conditions" },
  { key: "compositions", label: "Compositions" },
  { key: "mixture", label: "Mixture" },
  { key: "vapor", label: "Vapor" },
  { key: "liquid", label: "Liquid" },
  { key: "liquid1", label: "Liquid 1" },
  { key: "liquid2", label: "Liquid 2" },
  { key: "aqueous", label: "Aqueous" },
  { key: "solid", label: "Solid" },
];

/**
 * The simulation report, built by the engine as a page of its own: previewed here, printed
 * (to paper or PDF) or saved as HTML. With objects selected it covers only those.
 */
const closeReport = () => useApp.getState().openReport(false);

export function ReportDialog() {
  const open = useApp((s) => s.report);
  const flowsheet = useApp((s) => s.flowsheet);
  if (!open || !flowsheet) return null;
  return <Report flowsheetId={flowsheet.id} name={flowsheet.name} />;
}

function Report({ flowsheetId, name }: { flowsheetId: string; name: string }) {
  const selected = useApp((s) => s.selected);
  const [onlySelected, setOnlySelected] = useState(selected.length > 0);
  const [sections, setSections] = useState<Record<string, boolean>>(
    Object.fromEntries(SECTIONS.map((s) => [s.key, true])),
  );
  const [html, setHtml] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const frame = useRef<HTMLIFrameElement>(null);
  const close = closeReport;
  useEscape(true, close);

  const build = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await engine.report(flowsheetId, onlySelected ? selected : undefined, sections);
      setHtml(r.html);
    } catch (e) {
      setError(EngineError.from(e).message);
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    void build();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [onlySelected, sections]);

  return (
    <div className="dialog-backdrop" onMouseDown={close}>
      <div
        className="dialog report-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Report"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="panel-header">
          <h2 className="dialog-title">Report</h2>
          <span className="flex gap-1">
            <button
              className="btn btn-ghost"
              onClick={() => void build()}
              disabled={busy}
              title="Build again"
            >
              <RefreshCw size={13} />
            </button>
            <button
              className="btn"
              onClick={() => frame.current?.contentWindow?.print()}
              disabled={!html || busy}
              title="Print, or save as PDF from the print dialog"
            >
              <Printer size={14} /> Print / PDF
            </button>
            <button
              className="btn"
              onClick={() =>
                html && downloadText(fileName(`${name} report`, "html"), html, "text/html;charset=utf-8")
              }
              disabled={!html}
            >
              <Download size={14} /> HTML
            </button>
            <button className="btn btn-icon btn-ghost" onClick={close} aria-label="Close">
              <X size={15} />
            </button>
          </span>
        </div>
        <div className="report-options">
          {selected.length > 0 && (
            <label className="toggle">
              <input
                type="checkbox"
                checked={onlySelected}
                onChange={(e) => setOnlySelected(e.target.checked)}
              />
              Only the {selected.length} selected object{selected.length === 1 ? "" : "s"}
            </label>
          )}
          <span className="muted text-[11px]">Stream sections:</span>
          {SECTIONS.map((s) => (
            <label key={s.key} className="toggle">
              <input
                type="checkbox"
                checked={sections[s.key]}
                onChange={(e) => setSections({ ...sections, [s.key]: e.target.checked })}
              />
              {s.label}
            </label>
          ))}
        </div>
        {error && <p className="callout callout-error">{error}</p>}
        {html ? (
          // allow-modals lets its print dialog open; no scripts run inside.
          <iframe
            ref={frame}
            className={`report-frame ${busy ? "is-stale" : ""}`}
            title="Report preview"
            sandbox="allow-same-origin allow-modals"
            srcDoc={html}
            data-testid="report-frame"
          />
        ) : (
          !error && <p className="muted">Building the report…</p>
        )}
      </div>
    </div>
  );
}
