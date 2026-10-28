// Stream colouring, after the engine's StreamColoring.vb: by solve status (the default), or on a
// colour scale of one property spread over the range among the flowsheet's solved streams.

import {
  interpolateCividis,
  interpolateRdYlBu,
  interpolateViridis,
  interpolateYlGnBu,
} from "d3-scale-chromatic";
import type { GraphNode, StreamState } from "../engine/types";

export type ColorMode = "status" | "temperature" | "pressure" | "vaporFraction" | "phase" | "massFlow";

export const COLOR_MODES: { mode: ColorMode; label: string }[] = [
  { mode: "status", label: "Status" },
  { mode: "temperature", label: "Temperature" },
  { mode: "pressure", label: "Pressure" },
  { mode: "vaporFraction", label: "Vapor fraction" },
  { mode: "phase", label: "Phase" },
  { mode: "massFlow", label: "Mass flow" },
];

type Scaled = Exclude<ColorMode, "status" | "phase">;

const INTERPOLATORS: Record<Scaled, (t: number) => string> = {
  // Blue for cold, red for hot.
  temperature: (t) => interpolateRdYlBu(1 - t),
  pressure: (t) => interpolateViridis(0.15 + 0.8 * t),
  vaporFraction: (t) => interpolateYlGnBu(0.25 + 0.7 * t),
  massFlow: (t) => interpolateCividis(0.1 + 0.85 * t),
};

export const PHASE_COLORS = {
  liquid: "var(--phase-liquid)",
  mixed: "var(--phase-mixed)",
  vapor: "var(--phase-vapor)",
} as const;

export interface Legend {
  mode: ColorMode;
  min?: number;
  max?: number;
  /** CSS gradient for a scale, or swatches for categories. */
  gradient?: string;
  swatches?: { label: string; color: string }[];
}

export interface StreamColors {
  /** Colour per node id, for material streams only; absent means "use the status colour". */
  byNode: Map<string, string>;
  legend: Legend | null;
}

function value(s: StreamState, mode: Scaled): number | null {
  return s[mode];
}

const phaseOf = (vf: number | null) =>
  vf === null ? null : vf <= 1e-9 ? "liquid" : vf >= 1 - 1e-9 ? "vapor" : ("mixed" as const);

export function streamColors(nodes: GraphNode[], mode: ColorMode): StreamColors {
  const byNode = new Map<string, string>();
  const solved = nodes.filter((n) => n.type === "MaterialStream" && n.calculated && n.stream);
  if (mode === "status") return { byNode, legend: null };

  if (mode === "phase") {
    for (const n of solved) {
      const phase = phaseOf(n.stream!.vaporFraction);
      if (phase) byNode.set(n.id, PHASE_COLORS[phase]);
    }
    return {
      byNode,
      legend: {
        mode,
        swatches: [
          { label: "Liquid", color: PHASE_COLORS.liquid },
          { label: "Two-phase", color: PHASE_COLORS.mixed },
          { label: "Vapor", color: PHASE_COLORS.vapor },
        ],
      },
    };
  }

  const values = solved
    .map((n) => value(n.stream!, mode))
    .filter((v): v is number => v !== null && Number.isFinite(v));
  if (values.length === 0) return { byNode, legend: null };
  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min;
  const color = INTERPOLATORS[mode];
  for (const n of solved) {
    const v = value(n.stream!, mode);
    if (v === null || !Number.isFinite(v)) continue;
    byNode.set(n.id, color(span > 0 ? (v - min) / span : 0.5));
  }
  const stops = [0, 0.25, 0.5, 0.75, 1].map((t) => `${color(t)} ${t * 100}%`).join(", ");
  return { byNode, legend: { mode, min, max, gradient: `linear-gradient(to right, ${stops})` } };
}
