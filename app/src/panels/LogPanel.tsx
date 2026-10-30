import { AlertCircle, AlertTriangle, Info, RefreshCw } from "lucide-react";
import { useEffect, useRef } from "react";
import type { Finding } from "../engine/types";
import { useApp } from "../state/store";
import { StreamTablePanel } from "./StreamTablePanel";

/** The bottom panel: the engine's log, and the findings of the last flowsheet check. */
export function LogPanel() {
  const tab = useApp((s) => s.bottomTab);
  const findings = useApp((s) => s.findings);
  const setTab = useApp((s) => s.setBottomTab);
  const problems = findings?.filter((f) => f.severity !== "info").length ?? 0;
  return (
    <section className="panel log" aria-label="Log, diagnostics and streams">
      <div className="panel-header">
        <div className="tabs" role="tablist">
          <button
            role="tab"
            aria-selected={tab === "log"}
            className="tab"
            onClick={() => setTab("log")}
            data-testid="tab-log"
          >
            Log
          </button>
          <button
            role="tab"
            aria-selected={tab === "diagnostics"}
            className="tab"
            onClick={() => setTab("diagnostics")}
            data-testid="tab-diagnostics"
          >
            Diagnostics
            {findings && <span className={`count ${problems ? "count-warn" : ""}`}>{findings.length}</span>}
          </button>
          <button
            role="tab"
            aria-selected={tab === "streams"}
            className="tab"
            onClick={() => setTab("streams")}
            data-testid="tab-streams"
          >
            Streams
          </button>
        </div>
        {tab === "log" ? <LogActions /> : tab === "diagnostics" ? <CheckAction /> : null}
      </div>
      {tab === "log" ? (
        <LogLines />
      ) : tab === "diagnostics" ? (
        <Findings findings={findings} />
      ) : (
        <StreamTablePanel />
      )}
    </section>
  );
}

function LogActions() {
  const logs = useApp((s) => s.logs);
  const clear = useApp((s) => s.clearLog);
  return (
    <button className="btn btn-ghost" onClick={clear} disabled={logs.length === 0}>
      Clear
    </button>
  );
}

function CheckAction() {
  const has = useApp((s) => s.flowsheet !== null);
  const busy = useApp((s) => s.busy);
  return (
    <button className="btn btn-ghost" onClick={() => void useApp.getState().check()} disabled={!has || busy}>
      <RefreshCw size={13} /> Check again
    </button>
  );
}

function LogLines() {
  const logs = useApp((s) => s.logs);
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => {
    end.current?.scrollIntoView({ block: "end" });
  }, [logs.length]);
  return (
    <div className="log-scroll" data-testid="log">
      {logs.map((l) => (
        <div key={l.id} className={`log-line log-${l.level}`}>
          <time>{l.time.toLocaleTimeString()}</time>
          <span>{l.text}</span>
        </div>
      ))}
      <div ref={end} />
    </div>
  );
}

const ICONS = { error: AlertCircle, warning: AlertTriangle, info: Info };
const ORDER = { error: 0, warning: 1, info: 2 };

function Findings({ findings }: { findings: Finding[] | null }) {
  const nodes = useApp((s) => s.graph?.nodes);
  if (findings === null)
    return (
      <p className="log-scroll muted" data-testid="findings">
        Check the flowsheet (F7) to list what would stop it solving: missing specifications, loose
        connections, unsolvable loops.
      </p>
    );
  if (findings.length === 0)
    return (
      <p className="log-scroll muted" data-testid="findings">
        No problems found.
      </p>
    );
  const byTag = new Map((nodes ?? []).map((n) => [n.tag, n.id]));
  const sorted = [...findings].sort((a, b) => ORDER[a.severity] - ORDER[b.severity]);
  return (
    <ul className="log-scroll findings" data-testid="findings">
      {sorted.map((f, i) => {
        const Icon = ICONS[f.severity];
        const id = f.object ? byTag.get(f.object) : undefined;
        return (
          <li key={i} className={`finding finding-${f.severity}`}>
            <Icon size={14} aria-label={f.severity} />
            <div>
              <div>
                {f.object &&
                  (id ? (
                    <button className="link" onClick={() => useApp.getState().focusObject(id)}>
                      {f.object}
                    </button>
                  ) : (
                    <strong>{f.object}</strong>
                  ))}
                {f.object && ": "}
                {f.message}
              </div>
              {f.fix && <div className="muted">{f.fix}</div>}
            </div>
          </li>
        );
      })}
    </ul>
  );
}
