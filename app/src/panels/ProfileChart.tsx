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

/** Significant digits that tell neighbouring ticks apart (299.998 and 300 need six, not four). */
export function tickDigits(ticks: number[]): number {
  if (ticks.length < 2) return 4;
  const step = Math.abs(ticks[1]! - ticks[0]!);
  const big = Math.max(...ticks.map(Math.abs));
  if (!(step > 0) || !(big > 0)) return 4;
  return Math.min(10, Math.max(4, Math.floor(Math.log10(big)) - Math.floor(Math.log10(step) + 1e-9) + 1));
}

/**
 * The y range a panel spans. A series flat to within a few parts per million (a pipe's
 * temperature to the third decimal) is shown flat, not stretched until its noise fills the plot.
 */
export function yRange(values: number[]): [number, number] {
  const lo = Math.min(...values);
  const hi = Math.max(...values);
  const mid = (Math.abs(lo) + Math.abs(hi)) / 2;
  return hi - lo <= mid * 1e-5 ? [(lo + hi) / 2, (lo + hi) / 2] : [lo, hi];
}

/**
 * Line charts of a results table's chart columns against its x column, one panel per unit so
 * two scales never share an axis. A text x (stage names) spaces the rows evenly; a numeric x
 * (a pipe's length) places them by value. Hovering shows every value at a row.
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
  const ticks = niceTicks(...yRange(values));
  const digits = tickDigits(ticks);
  const yLo = ticks[0] ?? Math.min(...values);
  const yHi = ticks[ticks.length - 1] ?? Math.max(...values);

  // A numeric x places each row at its value; otherwise rows are evenly spaced stages.
  const xColumn = field.columns.find((c) => c.key === x);
  const xs = field.rows.map((r) => r[x]);
  const numeric = xColumn?.kind === "number" && xs.every((v) => typeof v === "number" && Number.isFinite(v));
  const xAt = (i: number) => (numeric ? (xs[i] as number) : i);
  const xLo = numeric ? Math.min(...(xs as number[])) : 0;
  const xHi = numeric ? Math.max(...(xs as number[])) : n - 1;
  const px = (i: number) => PAD.left + ((xAt(i) - xLo) / (xHi - xLo || 1)) * (W - PAD.left - PAD.right);
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
  const xTicks: { at: number; label: string }[] = numeric
    ? niceTicks(xLo, xHi, 4)
        .filter((t) => t >= xLo - 1e-9 && t <= xHi + 1e-9)
        .map((t) => ({
          at: PAD.left + ((t - xLo) / (xHi - xLo || 1)) * (W - PAD.left - PAD.right),
          label: formatNumber(t, 4),
        }))
    : [...new Set([0, Math.round((n - 1) / 2), n - 1])].map((i) => ({ at: px(i), label: String(i + 1) }));
  const xTitle = numeric ? xColumn!.label + (xColumn!.unit ? ` (${unitLabel(xColumn!.unit)})` : "") : "Stage";
  const title = keys.map(label).join(", ") + (unit ? ` (${unitLabel(unit)})` : "");

  const onMove = (e: React.MouseEvent<SVGSVGElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    const sx = ((e.clientX - box.left) / box.width) * W;
    let best = 0;
    for (let i = 1; i < n; i++) if (Math.abs(px(i) - sx) < Math.abs(px(best) - sx)) best = i;
    setHover(best);
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
          aria-label={`${title} against ${xTitle.toLowerCase()}`}
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
                {formatNumber(t, digits)}
              </text>
            </g>
          ))}
          {xTicks.map((t) => (
            <text key={t.at} x={t.at} y={H - 6} className="chart-tick" textAnchor="middle">
              {t.label}
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
              transform: px(hover) > W / 2 ? "translateX(-105%)" : "translateX(5%)",
            }}
          >
            <div className="chart-tooltip-title">
              {numeric
                ? `${xColumn!.label} ${formatNumber(xAt(hover), 5)} ${xColumn!.unit ? unitLabel(xColumn!.unit) : ""}`
                : String(field.rows[hover]?.[x] ?? `Stage ${hover + 1}`)}
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
      <div className="chart-axis-title">{xTitle}</div>
    </figure>
  );
}
