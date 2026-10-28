import { describe, expect, it } from "vitest";
import type { GraphNode } from "../engine/types";
import { streamColors } from "./coloring";

const stream = (id: string, temperature: number, vaporFraction: number): GraphNode => ({
  id,
  tag: id,
  type: "MaterialStream",
  x: 0,
  y: 0,
  width: 20,
  height: 20,
  rotation: 0,
  flippedH: false,
  flippedV: false,
  active: true,
  status: "Calculated",
  calculated: true,
  error: null,
  ports: [],
  stream: { temperature, pressure: 1e5, massFlow: 1, molarFlow: 1, vaporFraction },
});

describe("streamColors", () => {
  const nodes = [stream("cold", 280, 0), stream("hot", 400, 1), stream("mid", 340, 0.5)];

  it("leaves status mode to the theme", () => {
    const c = streamColors(nodes, "status");
    expect(c.byNode.size).toBe(0);
    expect(c.legend).toBeNull();
  });

  it("spreads a property over the solved range", () => {
    const c = streamColors(nodes, "temperature");
    expect(c.legend?.min).toBe(280);
    expect(c.legend?.max).toBe(400);
    expect(c.byNode.get("cold")).not.toBe(c.byNode.get("hot"));
  });

  it("classifies phases by vapor fraction", () => {
    const c = streamColors(nodes, "phase");
    expect(c.byNode.get("cold")).toBe("var(--phase-liquid)");
    expect(c.byNode.get("hot")).toBe("var(--phase-vapor)");
    expect(c.byNode.get("mid")).toBe("var(--phase-mixed)");
  });

  it("skips streams that are not solved", () => {
    const c = streamColors([{ ...stream("x", 300, 0), calculated: false }], "temperature");
    expect(c.byNode.size).toBe(0);
  });
});
