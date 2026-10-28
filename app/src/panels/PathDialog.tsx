import { useEffect, useState } from "react";
import { transport } from "../engine/transport";
import type { SampleFile } from "../engine/types";

/**
 * The browser's stand-in for the native file dialogs: a path field, and for opening, the
 * reference flowsheets the dev server can see. The Tauri app uses the system dialogs instead.
 */
export function PathDialog({
  mode,
  initial,
  onSubmit,
  onClose,
}: {
  mode: "open" | "save";
  initial?: string;
  onSubmit: (path: string) => void;
  onClose: () => void;
}) {
  const [path, setPath] = useState(initial ?? "");
  const [samples, setSamples] = useState<SampleFile[]>([]);
  useEffect(() => {
    if (mode === "open")
      void transport
        .samples()
        .then(setSamples)
        .catch(() => setSamples([]));
  }, [mode]);

  return (
    <div className="dialog-backdrop" onClick={onClose}>
      <div
        className="dialog"
        role="dialog"
        aria-label={mode === "open" ? "Open flowsheet" : "Save flowsheet"}
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="dialog-title">{mode === "open" ? "Open flowsheet" : "Save flowsheet as"}</h2>
        <form
          className="flex gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (path.trim()) onSubmit(path.trim());
          }}
        >
          <input
            autoFocus
            className="input flex-1"
            placeholder="/path/to/flowsheet.dwxmz"
            value={path}
            onChange={(e) => setPath(e.target.value)}
            aria-label="File path"
          />
          <button className="btn btn-primary" type="submit" disabled={!path.trim()}>
            {mode === "open" ? "Open" : "Save"}
          </button>
        </form>
        {samples.length > 0 && (
          <>
            <h3 className="section-title mt-4">Reference flowsheets</h3>
            <ul className="sample-list">
              {samples.map((s) => (
                <li key={s.path}>
                  <button className="sample" onClick={() => onSubmit(s.path)}>
                    {s.name}
                  </button>
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
    </div>
  );
}
