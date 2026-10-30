import { describe, expect, it } from "vitest";
import type { GraphNode } from "../engine/types";
import { aligned } from "./store";

const node = (id: string, x: number, y: number, width = 40, height = 40) =>
  ({ id, x, y, width, height }) as GraphNode;

describe("aligned", () => {
  const nodes = [node("a", 0, 0), node("b", 100, 50, 20, 20), node("c", 300, 10)];

  it("lines edges and centres up", () => {
    expect(aligned(nodes, "left").map((m) => m.x)).toEqual([0, 0, 0]);
    expect(aligned(nodes, "right").map((m) => m.x)).toEqual([300, 320, 300]);
    expect(aligned(nodes, "top").map((m) => m.y)).toEqual([0, 0, 0]);
    expect(aligned(nodes, "bottom").map((m) => m.y)).toEqual([30, 50, 30]);
    // centres meet halfway between the outer edges (0 and 340)
    expect(aligned(nodes, "center").map((m) => m.x + (m.id === "b" ? 10 : 20))).toEqual([170, 170, 170]);
  });

  it("spreads centres evenly between the outer two", () => {
    const spread = aligned(nodes, "spreadX");
    const centres = spread.map((m) => m.x + (m.id === "b" ? 10 : 20));
    expect(centres).toEqual([20, 170, 320]);
    expect(spread.map((m) => m.y)).toEqual([0, 50, 10]);
  });

  it("needs two objects to align and three to spread", () => {
    expect(aligned([node("a", 0, 0)], "left")).toEqual([]);
    expect(aligned(nodes.slice(0, 2), "spreadX").map((m) => m.x)).toEqual([0, 100]);
  });
});
