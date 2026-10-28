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
