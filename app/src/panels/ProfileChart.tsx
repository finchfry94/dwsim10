import { useState } from "react";
import type { TableField } from "../engine/types";
import { formatNumber, unitLabel } from "../lib/units";

/** Series colours in fixed order; a panel with one series draws it in ink and needs no legend. */
const SERIES = ["var(--chart-1)", "var(--chart-2)", "var(--chart-3)"];

const W = 320;
const H = 132;
const PAD = { left: 44, right: 30, top: 8, bottom: 22 };

/** Round tick values covering [lo, hi]. */
export function niceTicks(lo: number, hi: number, count = 4): number[] {
  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return [];
  if (lo === hi) {
    const pad = Math.abs(lo) > 0 ? Math.abs(lo) * 0.05 : 1;
    lo -= pad;
    hi += pad;
  }
  const raw = (hi - lo) / count;
  const mag = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 5, 10].map((m) => m * mag).find((s) => raw <= s) ?? 10 * mag;
  const ticks: number[] = [];
  for (let t = Math.floor(lo / step) * step; t <= hi + step * 1e-9; t += step)
    ticks.push(Number(t.toPrecision(12)));
  const top = ticks[ticks.length - 1] ?? lo;
  if (top < hi) ticks.push(Number((top + step).toPrecision(12)));
  return ticks;
}

/**
 * Line charts of a results table's chart columns against its x column (the stage), one
 * panel per unit so two scales never share an axis. Hovering shows every value at a stage.
 */
export function ProfileChart({ field }: { field: TableField }) {
  if (!field.chart || field.rows.length < 2) return null;
  const { x, series } = field.chart;
  const panels = new Map<string, string[]>();
  for (const key of series) {
    const column = field.columns.find((c) => c.key === key);
    if (!column) continue;
    const unit = column.unit ?? "";
    panels.set(unit, [...(panels.get(unit) ?? []), key]);
  }
  return (
    <div className="profile-charts" data-testid={`chart-${field.id}`}>
      {[...panels.entries()].map(([unit, keys]) => (
        <Panel key={unit} field={field} x={x} keys={keys} unit={unit} />
      ))}
    </div>
  );
}

function Panel({ field, x, keys, unit }: { field: TableField; x: string; keys: string[]; unit: string }) {
  const [hover, setHover] = useState<number | null>(null);
  const n = field.rows.length;
  const label = (key: string) => field.columns.find((c) => c.key === key)?.label ?? key;
  const value = (i: number, key: string) => {
    const v = field.rows[i]?.[key];
    return typeof v === "number" && Number.isFinite(v) ? v : null;
  };
  const values = keys
    .flatMap((k) => field.rows.map((_, i) => value(i, k)))
    .filter((v): v is number => v !== null);
  if (values.length === 0) return null;
  const ticks = niceTicks(Math.min(...values), Math.max(...values));
  const yLo = ticks[0] ?? Math.min(...values);
  const yHi = ticks[ticks.length - 1] ?? Math.max(...values);
  const px = (i: number) => PAD.left + (i / (n - 1)) * (W - PAD.left - PAD.right);
  const py = (v: number) => PAD.top + (1 - (v - yLo) / (yHi - yLo || 1)) * (H - PAD.top - PAD.bottom);
  const color = (j: number) => (keys.length === 1 ? "var(--text)" : SERIES[j % SERIES.length]);
  const path = (key: string) => {
    let d = "";
    let pen = false;
    field.rows.forEach((_, i) => {
      const v = value(i, key);
      if (v === null) {
        pen = false;
        return;
      }
      d += `${pen ? "L" : "M"}${px(i).toFixed(1)},${py(v).toFixed(1)}`;
      pen = true;
    });
    return d;
  };
  const xTicks = [...new Set([0, Math.round((n - 1) / 2), n - 1])];
  const title = keys.map(label).join(", ") + (unit ? ` (${unitLabel(unit)})` : "");

  const onMove = (e: React.MouseEvent<SVGSVGElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    const sx = ((e.clientX - box.left) / box.width) * W;
    const i = Math.round(((sx - PAD.left) / (W - PAD.left - PAD.right)) * (n - 1));
    setHover(Math.max(0, Math.min(n - 1, i)));
  };

  return (
    <figure className="profile-chart">
      <figcaption className="profile-chart-title">
        <span>{title}</span>
        {keys.length > 1 && (
          <span className="profile-legend">
            {keys.map((k, j) => (
              <span key={k} className="profile-legend-item">
                <span className="profile-swatch" style={{ background: color(j) }} />
                {label(k)}
              </span>
            ))}
          </span>
        )}
      </figcaption>
      <div className="profile-plot">
        <svg
          viewBox={`0 0 ${W} ${H}`}
          role="img"
          aria-label={`${title} against stage`}
          onMouseMove={onMove}
          onMouseLeave={() => setHover(null)}
        >
          {ticks.map((t) => (
            <g key={t}>
              <line x1={PAD.left} x2={W - PAD.right} y1={py(t)} y2={py(t)} className="chart-grid" />
              <text
                x={PAD.left - 6}
                y={py(t)}
                className="chart-tick"
                textAnchor="end"
                dominantBaseline="middle"
              >
                {formatNumber(t, 4)}
              </text>
            </g>
          ))}
          {xTicks.map((i) => (
            <text key={i} x={px(i)} y={H - 6} className="chart-tick" textAnchor="middle">
              {i + 1}
            </text>
          ))}
          {hover !== null && (
            <line
              x1={px(hover)}
              x2={px(hover)}
              y1={PAD.top}
              y2={H - PAD.bottom}
              className="chart-crosshair"
            />
          )}
          {keys.map((k, j) => (
            <path key={k} d={path(k)} fill="none" stroke={color(j)} strokeWidth={2} strokeLinejoin="round" />
          ))}
          {keys.length > 1 &&
            keys.map((k) => {
              const last = [...field.rows.keys()].reverse().find((i) => value(i, k) !== null);
              return last === undefined ? null : (
                <text
                  key={k}
                  x={px(last) + 4}
                  y={py(value(last, k)!)}
                  className="chart-label"
                  dominantBaseline="middle"
                >
                  {label(k)}
                </text>
              );
            })}
          {hover !== null &&
            keys.map((k, j) => {
              const v = value(hover, k);
              return v === null ? null : (
                <circle
                  key={k}
                  cx={px(hover)}
                  cy={py(v)}
                  r={4}
                  fill={color(j)}
                  stroke="var(--surface)"
                  strokeWidth={2}
                />
              );
            })}
        </svg>
        {hover !== null && (
          <div
            className="chart-tooltip"
            role="status"
            style={{
              left: `${(px(hover) / W) * 100}%`,
              transform: hover > n / 2 ? "translateX(-105%)" : "translateX(5%)",
            }}
          >
            <div className="chart-tooltip-title">
              {String(field.rows[hover]?.[x] ?? `Stage ${hover + 1}`)}
            </div>
            {keys.map((k, j) => (
              <div key={k} className="chart-tooltip-row">
                <span className="profile-swatch" style={{ background: color(j) }} />
                <span>{label(k)}</span>
                <span className="num">
                  {value(hover, k) === null ? "—" : formatNumber(value(hover, k)!, 5)}{" "}
                  {unit ? unitLabel(unit) : ""}
                </span>
              </div>
            ))}
          </div>
        )}
      </div>
      <div className="chart-axis-title">Stage</div>
    </figure>
  );
}
