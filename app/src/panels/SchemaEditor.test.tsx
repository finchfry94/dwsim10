import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EditorSchema } from "../engine/types";

const heater = (mode: "HeatAdded" | "OutletTemperature"): EditorSchema => ({
  id: "HT-1",
  tag: "HT-1",
  type: "Heater",
  calculated: false,
  error: null,
  mode: {
    property: "CalcMode",
    value: mode,
    options: [
      { value: "HeatAdded", label: "Heat Added" },
      { value: "OutletTemperature", label: "Outlet Temperature" },
    ],
  },
  dof: {
    supported: true,
    remaining: mode === "HeatAdded" ? 1 : 0,
    note: null,
    slots: [{ name: "Heat added", required: mode === "HeatAdded", set: false, note: null }],
  },
  sections: [
    {
      id: "spec",
      title: "Specification",
      fields: [
        {
          id: "PROP_HT_3",
          label: "Heat Added",
          kind: "number",
          value: null,
          unit: "kW",
          units: ["kW", "W"],
          editable: mode === "HeatAdded",
          missing: mode === "HeatAdded",
        },
        {
          id: "PROP_HT_2",
          label: "Outlet Temperature",
          kind: "number",
          value: 298.15,
          unit: "K",
          units: ["K", "C"],
          editable: mode === "OutletTemperature",
        },
      ],
    },
    { id: "results", title: "Results", fields: [] },
  ],
  connections: [
    {
      port: "in-0",
      label: "Inlet",
      direction: "in",
      energy: false,
      stream: { id: "S1", tag: "feed" },
      candidates: [],
    },
    {
      port: "out-0",
      label: "Outlet",
      direction: "out",
      energy: false,
      stream: null,
      candidates: [{ id: "S2", tag: "product" }],
    },
  ],
});

const engine = vi.hoisted(() => ({
  schema: vi.fn(),
  setMode: vi.fn(),
  setField: vi.fn(),
  convertUnits: vi.fn(),
  connect: vi.fn(),
  disconnect: vi.fn(),
  variables: vi.fn(),
}));
vi.mock("../engine/api", () => ({ engine }));

const { SchemaEditor } = await import("./SchemaEditor");
const { useApp } = await import("../state/store");

beforeEach(() => {
  engine.schema.mockResolvedValue(heater("HeatAdded"));
  engine.setMode.mockResolvedValue(heater("OutletTemperature"));
  engine.setField.mockImplementation(async () => heater("OutletTemperature"));
  engine.convertUnits.mockImplementation(async (v: number, from: string, to: string) =>
    from === "K" && to === "C" ? v - 273.15 : v,
  );
  engine.connect.mockResolvedValue({ nodes: [], edges: [] });
  useApp.setState({ afterEdit: vi.fn(async () => {}) });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("SchemaEditor", () => {
  it("shows the mode, what is missing and what is calculated", async () => {
    render(<SchemaEditor flowsheetId="F" id="HT-1" />);
    expect(await screen.findByLabelText("Calculation mode")).toHaveProperty("value", "HeatAdded");
    expect(screen.getByTestId("dof").textContent).toContain("Missing: Heat added");
    expect(screen.getByText("required")).toBeTruthy();
    // editable in this mode: an input; calculated: plain text
    expect(screen.getByLabelText("Heat Added").tagName).toBe("INPUT");
    expect(screen.queryByLabelText("Outlet Temperature")).toBeNull();
    expect(screen.getByText("feed")).toBeTruthy();
  });

  it("switches the mode through the engine and re-renders from its answer", async () => {
    render(<SchemaEditor flowsheetId="F" id="HT-1" />);
    fireEvent.change(await screen.findByLabelText("Calculation mode"), {
      target: { value: "OutletTemperature" },
    });
    await waitFor(() => expect(engine.setMode).toHaveBeenCalledWith("F", "HT-1", "OutletTemperature"));
    expect(await screen.findByLabelText("Outlet Temperature")).toBeTruthy();
    expect(screen.getByTestId("dof").textContent).toContain("Fully specified");
    expect(useApp.getState().afterEdit).toHaveBeenCalled();
  });

  it("converts the typed value when the unit changes and sends value with unit", async () => {
    engine.schema.mockResolvedValue(heater("OutletTemperature"));
    render(<SchemaEditor flowsheetId="F" id="HT-1" />);
    const input = (await screen.findByLabelText("Outlet Temperature")) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Outlet Temperature unit"), { target: { value: "C" } });
    });
    await waitFor(() => expect(input.value).toBe("25"));
    fireEvent.change(input, { target: { value: "80" } });
    fireEvent.blur(input);
    await waitFor(() => expect(engine.setField).toHaveBeenCalledWith("F", "HT-1", "PROP_HT_2", 80, "C"));
  });

  it("connects a free port to a stream picked from the list", async () => {
    render(<SchemaEditor flowsheetId="F" id="HT-1" />);
    fireEvent.change(await screen.findByLabelText("Connect Outlet"), { target: { value: "S2" } });
    await waitFor(() => expect(engine.connect).toHaveBeenCalledWith("F", "HT-1", "out-0", "S2", "in-0"));
  });
});

const adjust = (): EditorSchema => ({
  id: "ADJ-1",
  tag: "ADJ-1",
  type: "OT_Adjust",
  calculated: false,
  error: null,
  mode: null,
  dof: null,
  connections: [],
  sections: [
    {
      id: "variables",
      title: "Variables",
      fields: [
        {
          id: "variable:manipulated",
          label: "Manipulated variable",
          kind: "variable",
          editable: true,
          missing: true,
          writable: true,
          value: { object: null, property: null },
          objects: [
            { value: "HT-1", label: "Heater" },
            { value: "S2", label: "product" },
          ],
          properties: [],
          display: null,
        },
        { id: "column:readEstimates", label: "Read estimates", kind: "action", editable: true },
      ],
    },
    {
      id: "profiles",
      title: "Profiles",
      fields: [
        {
          id: "column:profile",
          label: "Stage profile",
          kind: "table",
          editable: false,
          readOnly: true,
          chart: { x: "stage", series: ["temperature", "liquid", "vapor"] },
          columns: [
            { key: "stage", label: "Stage", kind: "text", editable: false },
            { key: "temperature", label: "T", kind: "number", unit: "K", editable: false },
            { key: "liquid", label: "L", kind: "number", unit: "mol/s", editable: false },
            { key: "vapor", label: "V", kind: "number", unit: "mol/s", editable: false },
          ],
          rows: [
            { stage: "Stage1 (Condenser)", temperature: 351.5, liquid: 50, vapor: 0 },
            { stage: "Stage2", temperature: 352.1, liquid: 49, vapor: 75 },
            { stage: "Stage3 (Reboiler)", temperature: 353.7, liquid: 75, vapor: 74 },
          ],
        },
      ],
    },
  ],
});

describe("SchemaEditor – logical blocks and results", () => {
  it("loads the picked object's properties, then sends object and property together", async () => {
    engine.schema.mockResolvedValue(adjust());
    engine.variables.mockResolvedValue([{ value: "PROP_HT_3", label: "Heat Added", unit: "kW" }]);
    engine.setField.mockResolvedValue(adjust());
    render(<SchemaEditor flowsheetId="F" id="ADJ-1" />);
    const property = (await screen.findByLabelText("Manipulated variable property")) as HTMLSelectElement;
    expect(property.disabled).toBe(true);
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Manipulated variable object"), { target: { value: "HT-1" } });
    });
    expect(engine.variables).toHaveBeenCalledWith("F", "HT-1", true);
    await waitFor(() => expect(property.disabled).toBe(false));
    fireEvent.change(property, { target: { value: "PROP_HT_3" } });
    await waitFor(() =>
      expect(engine.setField).toHaveBeenCalledWith(
        "F",
        "ADJ-1",
        "variable:manipulated",
        { object: "HT-1", property: "PROP_HT_3" },
        undefined,
      ),
    );
  });

  it("runs an action and shows a results table with its chart and no Apply", async () => {
    engine.schema.mockResolvedValue(adjust());
    engine.setField.mockResolvedValue(adjust());
    render(<SchemaEditor flowsheetId="F" id="ADJ-1" />);
    fireEvent.click(await screen.findByTestId("action-column:readEstimates"));
    await waitFor(() =>
      expect(engine.setField).toHaveBeenCalledWith("F", "ADJ-1", "column:readEstimates", true, undefined),
    );
    expect(screen.queryByTestId("apply-column:profile")).toBeNull();
    // one panel per unit: temperature alone, liquid and vapor flows together
    expect(screen.getByTestId("chart-column:profile").querySelectorAll("figure")).toHaveLength(2);
    expect(screen.getByText("351.5")).toBeTruthy();
  });
});
