import { useEffect, useRef } from "react";
import { useApp } from "../state/store";

export function LogPanel() {
  const logs = useApp((s) => s.logs);
  const clear = useApp((s) => s.clearLog);
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => {
    end.current?.scrollIntoView({ block: "end" });
  }, [logs.length]);
  return (
    <section className="panel log" aria-label="Log">
      <div className="panel-header">
        <h2 className="section-title">Log</h2>
        <button className="btn btn-ghost" onClick={clear} disabled={logs.length === 0}>
          Clear
        </button>
      </div>
      <div className="log-scroll" data-testid="log">
        {logs.map((l) => (
          <div key={l.id} className={`log-line log-${l.level}`}>
            <time>{l.time.toLocaleTimeString()}</time>
            <span>{l.text}</span>
          </div>
        ))}
        <div ref={end} />
      </div>
    </section>
  );
}
