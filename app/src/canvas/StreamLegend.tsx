import type { UnitSystem } from "../engine/types";
import { formatQuantity, formatNumber } from "../lib/units";
import { COLOR_MODES, type Legend } from "./coloring";

export function StreamLegend({ legend, units }: { legend: Legend | null; units?: UnitSystem }) {
  if (!legend) return null;
  const label = COLOR_MODES.find((m) => m.mode === legend.mode)?.label;
  const fmt = (v: number | undefined) => {
    if (v === undefined) return "";
    switch (legend.mode) {
      case "temperature":
        return formatQuantity("temperature", v, units?.temperature);
      case "pressure":
        return formatQuantity("pressure", v, units?.pressure);
      case "massFlow":
        return formatQuantity("massFlow", v, units?.massflow);
      default:
        return formatNumber(v, 3);
    }
  };
  return (
    <div className="legend" data-testid="legend">
      <div className="legend-title">{label}</div>
      {legend.gradient && (
        <>
          <div className="legend-bar" style={{ background: legend.gradient }} />
          <div className="legend-range">
            <span>{fmt(legend.min)}</span>
            <span>{fmt(legend.max)}</span>
          </div>
        </>
      )}
      {legend.swatches?.map((s) => (
        <div key={s.label} className="legend-swatch">
          <span style={{ background: s.color }} />
          {s.label}
        </div>
      ))}
    </div>
  );
}
