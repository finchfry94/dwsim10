import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { FlowsheetSummary, SchemaField, ToolInfo, ToolRun } from "../engine/types";

const hydrates: ToolInfo = {
  id: "hydrates",
  title: "Hydrate formation",
  group: "Thermodynamics",
  description: "Whether hydrates form.",
  inputs: [
    {
      id: "stream",
      label: "Stream",
      kind: "choice",
      value: null,
      editable: true,
      options: [
        { value: "S1", label: "gas" },
        { value: "S2", label: "wet gas" },
      ],
    },
    { id: "vaporOnly", label: "Vapor only", kind: "bool", value: false, editable: true },
  ],
};

const separator: ToolInfo = {
  id: "separator",
  title: "Separator sizing",
  group: "Sizing",
  description: "Sizes a vessel.",
  inputs: [
    {
      id: "vessel",
      label: "Vessel",
      kind: "choice",
      value: "V1",
      editable: true,
      options: [{ value: "V1", label: "V-1" }],
    },
    {
      id: "residence",
      label: "Residence time",
      kind: "number",
      value: 5,
      unit: "min",
      units: ["min", "s"],
      editable: true,
    },
  ],
};

const run: ToolRun = {
  tool: "hydrates",
  title: "Hydrate formation",
  summary: "Hydrates (sI) form at the stream conditions.",
  sections: [
    {
      id: "s0",
      title: "At the stream temperature",
      fields: [
        { id: "r0", label: "Formation pressure", kind: "number", value: 4.09e6, unit: "Pa", editable: false },
      ],
    },
  ],
  charts: [],
  tables: [
    { title: "Hydrate composition", columns: ["Compound", "Mole fraction"], rows: [["Methane", 0.14]] },
  ],
  warnings: ["Check the water content."],
};

const engine = vi.hoisted(() => ({
  tools: vi.fn(),
  runTool: vi.fn(),
  cancel: vi.fn(),
  convertUnits: vi.fn(),
}));
vi.mock("../engine/api", () => ({ engine }));

const { ToolsDialog, toolArguments, toolFor, withObject } = await import("./ToolsDialog");
const { useApp } = await import("../state/store");

beforeEach(() => {
  engine.tools.mockResolvedValue([separator, hydrates]);
  engine.runTool.mockResolvedValue(run);
  useApp.setState({ flowsheet: { id: "F", name: "test" } as FlowsheetSummary, tools: null });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("tool helpers", () => {
  it("finds the tool that takes an object and picks the object in it", () => {
    expect(toolFor([separator, hydrates], "S2")?.id).toBe("hydrates");
    expect(toolFor([separator, hydrates], "nothing")).toBeUndefined();
    const inputs = withObject(hydrates.inputs, "S2");
    expect(inputs[0]!.kind === "choice" && inputs[0]!.value).toBe("S2");
    expect(withObject(hydrates.inputs, undefined)).toBe(hydrates.inputs);
  });

  it("sends a number's unit only when it differs from the field's own", () => {
    const changed = separator.inputs.map((f) =>
      f.kind === "number" ? ({ ...f, value: 300, unit: "s" } as SchemaField) : f,
    );
    expect(toolArguments(separator.inputs, separator.inputs)).toEqual({
      values: { vessel: "V1", residence: 5 },
      units: {},
    });
    expect(toolArguments(changed, separator.inputs)).toEqual({
      values: { vessel: "V1", residence: 300 },
      units: { residence: "s" },
    });
  });
});

describe("ToolsDialog", () => {
  it("opens on the tool for the selected stream, runs it and shows what comes back", async () => {
    render(<ToolsDialog />);
    act(() => useApp.getState().openTools({ objectId: "S2" }));
    expect(await screen.findByText("Whether hydrates form.")).toBeTruthy();
    expect(screen.getByTestId("tool-hydrates").getAttribute("aria-current")).toBe("true");
    expect(screen.getByLabelText("Stream")).toHaveProperty("value", "S2");
    // grouped as the bridge groups them
    expect(screen.getByText("Sizing")).toBeTruthy();

    fireEvent.click(screen.getByLabelText("Vapor only"));
    fireEvent.click(screen.getByTestId("run-tool"));
    await waitFor(() => expect(engine.runTool).toHaveBeenCalled());
    expect(engine.runTool.mock.calls[0]!.slice(0, 4)).toEqual([
      "F",
      "hydrates",
      { stream: "S2", vaporOnly: true },
      {},
    ]);
    const result = await screen.findByTestId("tool-result");
    expect(result.textContent).toContain("Hydrates (sI) form");
    expect(result.textContent).toContain("Formation pressure");
    expect(result.textContent).toContain("Check the water content.");
    expect(screen.getByTestId("tool-table").textContent).toContain("Methane");
  });

  it("switching tools resets the inputs and the result, and errors show in place", async () => {
    engine.runTool.mockRejectedValueOnce(new Error("Pick a vessel."));
    render(<ToolsDialog />);
    act(() => useApp.getState().openTools({ toolId: "separator" }));
    expect(await screen.findByLabelText("Residence time")).toHaveProperty("value", "5");
    fireEvent.click(screen.getByTestId("run-tool"));
    expect(await screen.findByText("Pick a vessel.")).toBeTruthy();
    fireEvent.click(screen.getByTestId("tool-hydrates"));
    expect(screen.queryByText("Pick a vessel.")).toBeNull();
    expect(screen.getByLabelText("Stream")).toHaveProperty("value", "");
  });

  it("keeps the object it was opened for when switching tools", async () => {
    render(<ToolsDialog />);
    act(() => useApp.getState().openTools({ toolId: "separator", objectId: "S1" }));
    expect(await screen.findByLabelText("Residence time")).toBeTruthy();
    fireEvent.click(screen.getByTestId("tool-hydrates"));
    expect(screen.getByLabelText("Stream")).toHaveProperty("value", "S1");
  });
});
