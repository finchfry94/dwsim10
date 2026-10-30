import { Plus, RefreshCw, Trash2 } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type { Sheet, SheetCell } from "../engine/types";
import { address, columnName, gridSize, parseAddress, shown } from "../lib/sheet";
import { useApp } from "../state/store";

/**
 * The flowsheet's spreadsheet, kept and calculated by the engine: values, formulas, and the
 * GETPROPVAL/SETPROPVAL functions that read and write the simulation. Pick a cell, type in
 * the formula bar (or start typing on the cell), Enter to write it.
 */
export function SpreadsheetPanel() {
  const flowsheet = useApp((s) => s.flowsheet);
  const revision = useApp((s) => s.revision);
  const [sheetName, setSheetName] = useState<string | undefined>(undefined);
  const [sheet, setSheet] = useState<Sheet | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState({ row: 0, col: 0 });
  const [draft, setDraft] = useState<string | null>(null);
  const bar = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!flowsheet) return;
    let live = true;
    engine
      .sheet(flowsheet.id, sheetName)
      .then((s) => {
        if (!live) return;
        setSheet(s);
        setError(null);
      })
      .catch((e) => {
        if (!live) return;
        // A sheet removed by undo: fall back to the first.
        if (sheetName) setSheetName(undefined);
        else setError(EngineError.from(e).message);
      });
    return () => {
      live = false;
    };
  }, [flowsheet, revision, sheetName]);

  const cells = useMemo(() => new Map((sheet?.cells ?? []).map((c) => [`${c.row},${c.col}`, c])), [sheet]);

  if (!flowsheet) return <p className="log-scroll muted">Open a flowsheet to use its spreadsheet.</p>;
  if (error) return <p className="log-scroll callout callout-error">{error}</p>;
  if (!sheet) return <p className="log-scroll muted">Loading…</p>;

  const id = flowsheet.id;
  const cellAt = (row: number, col: number): SheetCell | undefined => cells.get(`${row},${col}`);
  const current = cellAt(selected.row, selected.col);
  const input = draft ?? current?.input ?? "";
  const size = gridSize(sheet.rows, sheet.cols);

  const run = async (action: () => Promise<Sheet>, solve = false) => {
    try {
      const s = await action();
      setSheet(s);
      setSheetName(s.sheet);
      setError(null);
      if (solve) await useApp.getState().afterEdit();
      else await useApp.getState().refresh();
    } catch (e) {
      useApp.getState().log("error", EngineError.from(e).message);
    }
  };

  const commit = async (move: { row: number; col: number } = { row: 1, col: 0 }) => {
    if (draft !== null && draft !== (current?.input ?? "")) {
      const text = draft;
      setDraft(null);
      // A SETPROPVAL cell changes the simulation, so solve as after any edit.
      await run(
        () =>
          engine.setCells(id, [{ address: address(selected.row, selected.col), input: text }], sheet.sheet),
        true,
      );
    } else setDraft(null);
    setSelected((s) => ({ row: Math.max(0, s.row + move.row), col: Math.max(0, s.col + move.col) }));
  };

  const onGridKey = (e: React.KeyboardEvent) => {
    if (draft !== null) return;
    // The grid's keys are its own: not the canvas's Delete, nor a command shortcut.
    if (!e.ctrlKey && !e.metaKey) e.stopPropagation();
    const moves: Record<string, { row: number; col: number }> = {
      ArrowUp: { row: -1, col: 0 },
      ArrowDown: { row: 1, col: 0 },
      ArrowLeft: { row: 0, col: -1 },
      ArrowRight: { row: 0, col: 1 },
      Enter: { row: 1, col: 0 },
      Tab: { row: 0, col: 1 },
    };
    const m = moves[e.key];
    if (m) {
      e.preventDefault();
      setSelected((s) => ({ row: Math.max(0, s.row + m.row), col: Math.max(0, s.col + m.col) }));
    } else if (e.key === "Delete" || e.key === "Backspace") {
      e.preventDefault();
      if (current)
        void run(
          () =>
            engine.setCells(id, [{ address: address(selected.row, selected.col), input: "" }], sheet.sheet),
          true,
        );
    } else if (e.key === "F2") {
      e.preventDefault();
      setDraft(current?.input ?? "");
      bar.current?.focus();
    } else if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
      e.preventDefault();
      setDraft(e.key);
      bar.current?.focus();
    }
  };

  return (
    <div className="sheet-panel" data-testid="spreadsheet">
      <div className="sheet-bar">
        <select
          className="select select-sm"
          value={sheet.sheet}
          onChange={(e) => setSheetName(e.target.value)}
          aria-label="Sheet"
        >
          {sheet.sheets.map((n) => (
            <option key={n} value={n}>
              {n}
            </option>
          ))}
        </select>
        <button
          className="btn btn-icon btn-ghost"
          onClick={() => void run(() => engine.addSheet(id))}
          title="Add a sheet"
          aria-label="Add a sheet"
        >
          <Plus size={13} />
        </button>
        {sheet.sheets.length > 1 && (
          <button
            className="btn btn-icon btn-ghost"
            onClick={() => void run(() => engine.deleteSheet(id, sheet.sheet))}
            title={`Delete ${sheet.sheet}`}
            aria-label="Delete sheet"
          >
            <Trash2 size={13} />
          </button>
        )}
        <input
          className="input input-sm sheet-address"
          value={address(selected.row, selected.col)}
          onChange={(e) => {
            const p = parseAddress(e.target.value);
            if (p) setSelected(p);
          }}
          aria-label="Cell"
        />
        <input
          ref={bar}
          className="input input-sm sheet-formula"
          value={input}
          placeholder='a value, or a formula such as =GETPROPVAL("id","PROP_MS_0","C")'
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              void commit();
            } else if (e.key === "Tab") {
              e.preventDefault();
              void commit({ row: 0, col: 1 });
            } else if (e.key === "Escape") {
              e.stopPropagation();
              setDraft(null);
            }
          }}
          aria-label="Formula"
        />
        <button
          className="btn btn-icon btn-ghost"
          onClick={() => void run(() => engine.recalculateSheet(id, sheet.sheet), true)}
          title="Recalculate (SETPROPVAL cells write their properties)"
          aria-label="Recalculate"
        >
          <RefreshCw size={13} />
        </button>
      </div>
      <div className="sheet-scroll" tabIndex={0} onKeyDown={onGridKey} aria-label={`Sheet ${sheet.sheet}`}>
        <table className="sheet-grid">
          <thead>
            <tr>
              <th className="sheet-corner" />
              {Array.from({ length: size.cols }, (_, c) => (
                <th key={c} className={c === selected.col ? "is-current" : ""}>
                  {columnName(c)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {Array.from({ length: size.rows }, (_, r) => (
              <tr key={r}>
                <th className={r === selected.row ? "is-current" : ""}>{r + 1}</th>
                {Array.from({ length: size.cols }, (_, c) => {
                  const cell = cellAt(r, c);
                  const isSel = r === selected.row && c === selected.col;
                  return (
                    <td
                      key={c}
                      className={[
                        isSel ? "is-selected" : "",
                        cell?.error ? "is-error" : "",
                        typeof cell?.value === "number" ? "num" : "",
                        cell?.input.startsWith("=") ? "is-formula" : "",
                      ].join(" ")}
                      title={cell?.input}
                      onMouseDown={() => {
                        if (draft !== null) void commit({ row: 0, col: 0 });
                        setSelected({ row: r, col: c });
                      }}
                      onDoubleClick={() => {
                        setDraft(cell?.input ?? "");
                        bar.current?.focus();
                      }}
                      data-address={address(r, c)}
                    >
                      {cell ? shown(cell.value, cell.display) : ""}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
