import { useMemo, useState } from "react";
import type { ChartData } from "../engine/types";
import { formatNumber, unitLabel } from "../lib/units";
import { niceTicks } from "./ProfileChart";

/** Line colours in fixed order (validated for colour-vision deficiency on both themes). */
const LINE = [
  "var(--chart-1)",
  "var(--chart-2)",
  "var(--chart-3)",
  "var(--chart-4)",
  "var(--chart-5)",
  "var(--chart-6)",
];
/** Points are drawn in ink and told apart by their marker. */
const MARKERS = ["circle", "diamond", "square", "triangle"] as const;

const W = 640;
const H = 380;
const PAD = { left: 64, right: 24, top: 12, bottom: 44 };

export interface Scale {
  lo: number;
  hi: number;
  ticks: number[];
}

/** An axis covering every series' values, padded to round ticks. */
export function scaleFor(values: number[]): Scale | null {
  const finite = values.filter(Number.isFinite);
  if (finite.length === 0) return null;
  const ticks = niceTicks(Math.min(...finite), Math.max(...finite), 5);
  return { lo: ticks[0] ?? Math.min(...finite), hi: ticks[ticks.length - 1] ?? Math.max(...finite), ticks };
}

/** Index of the point nearest (px, py) in plot coordinates, within `radius`. */
export function nearest(
  points: { x: number; y: number }[],
  px: number,
  py: number,
  radius: number,
): number | null {
  let best: number | null = null;
  let bestD = radius * radius;
  points.forEach((p, i) => {
    const d = (p.x - px) ** 2 + (p.y - py) ** 2;
    if (d <= bestD) {
      bestD = d;
      best = i;
    }
  });
  return best;
}

const axisTitle = (a: { label: string; unit: string | null }) =>
  a.unit ? `${a.label} (${unitLabel(a.unit)})` : a.label;

const SUPERSCRIPT: Record<string, string> = {
  "-": "⁻",
  "0": "⁰",
  "1": "¹",
  "2": "²",
  "3": "³",
  "4": "⁴",
  "5": "⁵",
  "6": "⁶",
  "7": "⁷",
  "8": "⁸",
  "9": "⁹",
};

/**
 * A power of ten to take out of an axis whose ticks are very large or very small, so they read
 * "2 4 6 8" with "×10⁶" in the title instead of "2.000e+6". Zero when the ticks read fine.
 */
export function axisExponent(ticks: number[]): number {
  const largest = Math.max(...ticks.map(Math.abs));
  if (!Number.isFinite(largest) || largest === 0) return 0;
  // Engineering steps (10³, 10⁶, …) keep the ticks between 1 and 1000.
  const e = Math.floor(Math.log10(largest) / 3) * 3;
  return Math.log10(largest) >= 5 || Math.log10(largest) < -2 ? e : 0;
}

const times10 = (e: number) => (e ? ` ×10${String(e).replace(/./g, (c) => SUPERSCRIPT[c] ?? c)}` : "");

/** End labels only when no two would overlap; otherwise the legend names the lines. */
export function labelsFit(ends: { x: number; y: number }[]): boolean {
  return ends.every((a, i) =>
    ends.every((b, j) => i === j || Math.abs(a.y - b.y) >= 14 || Math.abs(a.x - b.x) >= 110),
  );
}

/**
 * An XY chart of a calculation's series on one pair of axes: lines in the series colours,
 * points as ink markers, a legend, end labels on up to four lines and a hover readout.
 */
export function XYChart({ chart }: { chart: ChartData }) {
  const [hover, setHover] = useState<{ series: number; index: number } | null>(null);
  const lines = chart.series.filter((s) => s.kind === "line");
  const points = chart.series.filter((s) => s.kind === "point");
  const color = (name: string) => LINE[lines.findIndex((l) => l.name === name) % LINE.length] ?? LINE[0]!;
  const marker = (name: string) => MARKERS[points.findIndex((p) => p.name === name) % MARKERS.length]!;

  const sx = useMemo(() => scaleFor(chart.series.flatMap((s) => s.x)), [chart]);
  const sy = useMemo(() => scaleFor(chart.series.flatMap((s) => s.y)), [chart]);
  if (!sx || !sy) return <p className="muted">The calculation returned no curves.</p>;

  const ex = axisExponent(sx.ticks);
  const ey = axisExponent(sy.ticks);
  const px = (v: number) => PAD.left + ((v - sx.lo) / (sx.hi - sx.lo || 1)) * (W - PAD.left - PAD.right);
  const py = (v: number) => PAD.top + (1 - (v - sy.lo) / (sy.hi - sy.lo || 1)) * (H - PAD.top - PAD.bottom);

  // Every plotted point, in plot coordinates, for the hover search.
  const all = chart.series.flatMap((s, si) =>
    s.x.map((x, i) => ({ x: px(x), y: py(s.y[i]!), series: si, index: i })),
  );

  const onMove = (e: React.MouseEvent<SVGSVGElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    const x = ((e.clientX - box.left) / box.width) * W;
    const y = ((e.clientY - box.top) / box.height) * H;
    const i = nearest(all, x, y, 24);
    setHover(i === null ? null : { series: all[i]!.series, index: all[i]!.index });
  };

  const hovered = hover && chart.series[hover.series];
  const hx = hovered ? hovered.x[hover.index]! : 0;
  const hy = hovered ? hovered.y[hover.index]! : 0;

  return (
    <figure className="xy-chart" data-testid="xy-chart">
      <figcaption className="xy-head">
        <div>
          <div className="xy-title">{chart.title}</div>
          {chart.subtitle && <div className="muted text-[11px]">{chart.subtitle}</div>}
        </div>
        <ul className="xy-legend" aria-label="Legend">
          {lines.map((s) => (
            <li key={s.name}>
              <span className="profile-swatch" style={{ background: color(s.name) }} />
              {s.name}
            </li>
          ))}
          {points.map((s) => (
            <li key={s.name}>
              <svg width="10" height="10" viewBox="-5 -5 10 10" aria-hidden>
                <Marker shape={marker(s.name)} size={4} />
              </svg>
              {s.name}
            </li>
          ))}
        </ul>
      </figcaption>
      <div className="profile-plot">
        <svg
          viewBox={`0 0 ${W} ${H}`}
          role="img"
          aria-label={`${chart.title}: ${axisTitle(chart.y)} against ${axisTitle(chart.x)}`}
          onMouseMove={onMove}
          onMouseLeave={() => setHover(null)}
        >
          {sy.ticks.map((t) => (
            <g key={`y${t}`}>
              <line x1={PAD.left} x2={W - PAD.right} y1={py(t)} y2={py(t)} className="chart-grid" />
              <text
                x={PAD.left - 6}
                y={py(t)}
                className="chart-tick"
                textAnchor="end"
                dominantBaseline="middle"
              >
                {formatNumber(t / 10 ** ey, 4)}
              </text>
            </g>
          ))}
          {sx.ticks.map((t) => (
            <text key={`x${t}`} x={px(t)} y={H - PAD.bottom + 14} className="chart-tick" textAnchor="middle">
              {formatNumber(t / 10 ** ex, 4)}
            </text>
          ))}
          <text x={(PAD.left + W - PAD.right) / 2} y={H - 8} className="chart-axis" textAnchor="middle">
            {axisTitle(chart.x) + times10(ex)}
          </text>
          <text
            x={14}
            y={(PAD.top + H - PAD.bottom) / 2}
            className="chart-axis"
            textAnchor="middle"
            transform={`rotate(-90 14 ${(PAD.top + H - PAD.bottom) / 2})`}
          >
            {axisTitle(chart.y) + times10(ey)}
          </text>
          {hovered && (
            <g className="chart-crosshair">
              <line x1={px(hx)} x2={px(hx)} y1={PAD.top} y2={H - PAD.bottom} />
              <line x1={PAD.left} x2={W - PAD.right} y1={py(hy)} y2={py(hy)} />
            </g>
          )}
          {lines.map((s) => (
            <path
              key={s.name}
              d={s.x.map((x, i) => `${i ? "L" : "M"}${px(x).toFixed(1)},${py(s.y[i]!).toFixed(1)}`).join("")}
              fill="none"
              stroke={color(s.name)}
              strokeWidth={2}
              strokeLinejoin="round"
            />
          ))}
          {lines.length >= 2 &&
            lines.length <= 4 &&
            labelsFit(lines.map((s) => ({ x: px(s.x[s.x.length - 1]!), y: py(s.y[s.y.length - 1]!) }))) &&
            lines.map((s) => {
              const last = s.x.length - 1;
              return (
                <text
                  key={`l${s.name}`}
                  x={Math.min(px(s.x[last]!) + 4, W - PAD.right - 2)}
                  y={py(s.y[last]!)}
                  className="chart-label"
                  textAnchor={px(s.x[last]!) + 60 > W - PAD.right ? "end" : "start"}
                  dominantBaseline="middle"
                >
                  {s.name}
                </text>
              );
            })}
          {points.map((s) =>
            s.x.map((x, i) => (
              <g key={`${s.name}${i}`} transform={`translate(${px(x)},${py(s.y[i]!)})`}>
                <Marker shape={marker(s.name)} size={5} />
              </g>
            )),
          )}
          {hovered && (
            <circle
              cx={px(hx)}
              cy={py(hy)}
              r={5}
              fill={hovered.kind === "line" ? color(hovered.name) : "var(--text)"}
              stroke="var(--surface)"
              strokeWidth={2}
            />
          )}
        </svg>
        {hovered && (
          <div
            className="chart-tooltip"
            role="status"
            style={{
              left: `${(px(hx) / W) * 100}%`,
              top: `${(py(hy) / H) * 100}%`,
              transform: px(hx) > W / 2 ? "translate(-105%, -110%)" : "translate(5%, -110%)",
            }}
          >
            <div className="chart-tooltip-title">{hovered.name}</div>
            <div className="chart-tooltip-row">
              <span />
              <span>{chart.x.label}</span>
              <span className="num">
                {formatNumber(hx, 5)} {chart.x.unit ? unitLabel(chart.x.unit) : ""}
              </span>
            </div>
            <div className="chart-tooltip-row">
              <span />
              <span>{chart.y.label}</span>
              <span className="num">
                {formatNumber(hy, 5)} {chart.y.unit ? unitLabel(chart.y.unit) : ""}
              </span>
            </div>
          </div>
        )}
      </div>
    </figure>
  );
}

function Marker({ shape, size }: { shape: (typeof MARKERS)[number]; size: number }) {
  const ink = { fill: "var(--text)", stroke: "var(--surface)", strokeWidth: 1.5 };
  switch (shape) {
    case "diamond":
      return <path d={`M0,${-size - 1}L${size + 1},0L0,${size + 1}L${-size - 1},0Z`} {...ink} />;
    case "square":
      return <rect x={-size} y={-size} width={size * 2} height={size * 2} {...ink} />;
    case "triangle":
      return <path d={`M0,${-size - 1}L${size + 1},${size}L${-size - 1},${size}Z`} {...ink} />;
    default:
      return <circle r={size} {...ink} />;
  }
}

/** The chart's series as CSV rows: one block of (x, y) columns per series. */
export function chartRows(chart: ChartData): (string | number | null)[][] {
  const head: string[] = [];
  for (const s of chart.series)
    head.push(`${s.name}: ${axisTitle(chart.x)}`, `${s.name}: ${axisTitle(chart.y)}`);
  const n = Math.max(0, ...chart.series.map((s) => s.x.length));
  const rows: (string | number | null)[][] = [head];
  for (let i = 0; i < n; i++) rows.push(chart.series.flatMap((s) => [s.x[i] ?? null, s.y[i] ?? null]));
  return rows;
}
