import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { FlowsheetSummary, Graph, SensitivityCase, SensitivityResult } from "../engine/types";

const study: SensitivityCase = {
  index: 0,
  name: "Duty",
  description: "",
  independent: {
    object: "H1",
    property: "PROP_HT_3",
    unit: "kW",
    label: "H-1 · Heat Added",
    from: 0,
    to: 100,
    points: 3,
  },
  dependents: [{ object: "S2", property: "PROP_MS_0", unit: "K", label: "product · Temperature" }],
};

const result: SensitivityResult = {
  cancelled: false,
  charts: [
    {
      title: "product · Temperature",
      x: { label: "Heat Added", unit: "kW" },
      y: { label: "Temperature", unit: "K" },
      series: [{ name: "product · Temperature", kind: "line", x: [0, 50, 100], y: [300, 312, 324] }],
    },
  ],
  table: {
    columns: ["H-1 · Heat Added (kW)", "product · Temperature (K)"],
    rows: [
      [0, 300],
      [50, 312],
      [100, 324],
    ],
  },
  warnings: [],
};

const engine = vi.hoisted(() => ({
  sensitivityCases: vi.fn(),
  saveSensitivity: vi.fn(),
  runSensitivity: vi.fn(),
  variables: vi.fn(),
  cancel: vi.fn(),
  optimizerMethods: vi.fn(),
  optimizerCases: vi.fn(),
}));
vi.mock("../engine/api", () => ({ engine }));

const { StudiesDialog } = await import("./StudiesDialog");
const { useApp } = await import("../state/store");

beforeEach(() => {
  engine.sensitivityCases.mockResolvedValue([study]);
  engine.saveSensitivity.mockImplementation(async (_: string, c: SensitivityCase) => ({ ...c, index: 0 }));
  engine.runSensitivity.mockResolvedValue(result);
  engine.variables.mockResolvedValue([
    { value: "PROP_HT_3", label: "Heat Added", unit: "kW" },
    { value: "PROP_MS_0", label: "Temperature", unit: "K" },
  ]);
  const graph = {
    nodes: [
      { id: "H1", tag: "H-1", type: "Heater" },
      { id: "S2", tag: "product", type: "MaterialStream" },
    ],
    edges: [],
  } as unknown as Graph;
  useApp.setState({
    flowsheet: { id: "F", name: "test" } as FlowsheetSummary,
    graph,
    studies: null,
    refresh: vi.fn(async () => {}),
  });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("StudiesDialog", () => {
  it("shows the saved case, saves it before running, and shows the points", async () => {
    render(<StudiesDialog />);
    act(() => useApp.getState().openStudies("sensitivity"));
    expect(await screen.findByLabelText("Case name")).toHaveProperty("value", "Duty");
    expect(screen.getByLabelText("Independent variable object")).toHaveProperty("value", "H1");
    await waitFor(() =>
      expect(screen.getByLabelText("Independent variable property")).toHaveProperty("value", "PROP_HT_3"),
    );
    fireEvent.change(screen.getByLabelText("Points"), { target: { value: "5" } });
    fireEvent.click(screen.getByTestId("run-study"));
    await waitFor(() => expect(engine.runSensitivity).toHaveBeenCalled());
    expect(engine.saveSensitivity.mock.calls[0]![1].independent.points).toBe(5);
    const view = await screen.findByTestId("sensitivity-result");
    expect(view.querySelectorAll("tbody tr")).toHaveLength(3);
    expect(view.textContent).toContain("324");
  });

  it("starts a blank case when the flowsheet has none, and shows engine refusals", async () => {
    engine.sensitivityCases.mockResolvedValue([]);
    engine.saveSensitivity.mockRejectedValue(new Error("Pick the object of the independent variable."));
    render(<StudiesDialog />);
    act(() => useApp.getState().openStudies("sensitivity"));
    expect(await screen.findByLabelText("Case name")).toHaveProperty("value", "Case 1");
    fireEvent.click(screen.getByTestId("run-study"));
    expect(await screen.findByText("Pick the object of the independent variable.")).toBeTruthy();
    expect(engine.runSensitivity).not.toHaveBeenCalled();
  });
});
