import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import type { ChartData } from "../engine/types";
import { axisExponent, chartRows, labelsFit, nearest, scaleFor, XYChart } from "./XYChart";

const chart: ChartData = {
  title: "Pressure/temperature diagram",
  x: { label: "Temperature", unit: "K" },
  y: { label: "Pressure", unit: "Pa" },
  series: [
    { name: "Bubble points", kind: "line", x: [200, 250, 300], y: [1e6, 3e6, 6e6] },
    { name: "Dew points", kind: "line", x: [220, 280], y: [1e6, 5e6] },
    { name: "Critical point", kind: "point", x: [305], y: [6.2e6] },
  ],
};

afterEach(cleanup);

describe("XYChart", () => {
  it("draws a line per line series, a marker per point and a legend entry for each", () => {
    const { container } = render(<XYChart chart={chart} />);
    expect(container.querySelectorAll("svg[role=img] path[fill=none]")).toHaveLength(2);
    expect(screen.getByLabelText("Legend").textContent).toContain("Critical point");
    expect(screen.getByRole("img").getAttribute("aria-label")).toBe(
      "Pressure/temperature diagram: Pressure (Pa) against Temperature (K)",
    );
  });

  it("says so when there is nothing to draw", () => {
    render(<XYChart chart={{ ...chart, series: [] }} />);
    expect(screen.getByText("The calculation returned no curves.")).toBeTruthy();
  });
});

describe("chart helpers", () => {
  it("scales an axis over every value with round ends", () => {
    const s = scaleFor([200, 305, 250])!;
    expect(s.lo).toBeLessThanOrEqual(200);
    expect(s.hi).toBeGreaterThanOrEqual(305);
    expect(scaleFor([])).toBeNull();
  });

  it("finds the nearest point within the radius", () => {
    const points = [
      { x: 0, y: 0 },
      { x: 10, y: 0 },
    ];
    expect(nearest(points, 8, 1, 5)).toBe(1);
    expect(nearest(points, 50, 50, 5)).toBeNull();
  });

  it("writes one pair of columns per series, padding the shorter ones", () => {
    const rows = chartRows(chart);
    expect(rows[0]).toEqual([
      "Bubble points: Temperature (K)",
      "Bubble points: Pressure (Pa)",
      "Dew points: Temperature (K)",
      "Dew points: Pressure (Pa)",
      "Critical point: Temperature (K)",
      "Critical point: Pressure (Pa)",
    ]);
    expect(rows).toHaveLength(4);
    expect(rows[3]).toEqual([300, 6e6, null, null, null, null]);
  });
});

describe("axis readability", () => {
  it("takes a power of ten out of large and small ticks only", () => {
    expect(axisExponent([0, 2e6, 4e6, 1e7])).toBe(6);
    expect(axisExponent([0, 2e5, 4e5])).toBe(3);
    expect(axisExponent([0, 100, 200, 400])).toBe(0);
    expect(axisExponent([0, 0.0005, 0.001])).toBe(-3);
    expect(axisExponent([0])).toBe(0);
  });

  it("drops end labels that would overlap", () => {
    expect(
      labelsFit([
        { x: 600, y: 100 },
        { x: 600, y: 105 },
      ]),
    ).toBe(false);
    expect(
      labelsFit([
        { x: 600, y: 100 },
        { x: 600, y: 140 },
      ]),
    ).toBe(true);
    expect(
      labelsFit([
        { x: 100, y: 100 },
        { x: 400, y: 100 },
      ]),
    ).toBe(true);
  });
});
