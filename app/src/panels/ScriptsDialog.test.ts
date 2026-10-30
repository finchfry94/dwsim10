import { describe, expect, it } from "vitest";
import { eventLabel, indent } from "./ScriptsDialog";

describe("script editor helpers", () => {
  it("names events in plain words", () => {
    expect(eventLabel("ObjectCalculationFinished")).toBe("Object calculation finished");
    expect(eventLabel("SimulationTimer15")).toBe("Simulation timer 15");
    expect(eventLabel("SolverStarted")).toBe("Solver started");
  });

  it("inserts four spaces at the caret", () => {
    expect(indent("ab", 1, 1, false)).toEqual({ text: "a    b", start: 5, end: 5 });
  });

  it("indents and outdents every selected line", () => {
    const text = "x = 1\ny = 2\nz = 3";
    const r = indent(text, 2, 9, false);
    expect(r.text).toBe("    x = 1\n    y = 2\nz = 3");
    expect(r.start).toBe(6);
    const back = indent(r.text, r.start, r.end, true);
    expect(back.text).toBe(text);
  });
});
