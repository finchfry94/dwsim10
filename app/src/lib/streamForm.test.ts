import { describe, expect, it } from "vitest";
import type { StreamDetails, UnitSystem } from "../engine/types";
import {
  formFromStream,
  normalized,
  toSpecification,
  validate,
  withCompositionBasis,
  type StreamForm,
} from "./streamForm";

const units: UnitSystem = {
  name: "SI (Engineering)",
  temperature: "C",
  pressure: "bar",
  massflow: "kg/h",
  molarflow: "kmol/h",
  volumetricFlow: "m3/h",
  enthalpy: "kJ/kg",
  heatflow: "kW",
  density: "kg/m3",
};

const stream: Extract<StreamDetails, { kind: "material" }> = {
  id: "s",
  tag: "feed",
  kind: "material",
  calculated: true,
  spec: "Temperature_and_Pressure",
  temperature: 298.15,
  pressure: 101325,
  massFlow: 1,
  molarFlow: 50,
  vaporFraction: 0,
  phases: [
    {
      index: 0,
      name: "Mixture",
      moleFraction: 1,
      massFraction: 1,
      massFlow: 1,
      molarFlow: 50,
      volumetricFlow: null,
      enthalpy: null,
      entropy: null,
      density: null,
      molecularWeight: null,
      viscosity: null,
      heatCapacityCp: null,
      compressibilityFactor: null,
      compounds: [
        { name: "Water", moleFraction: 0.6, massFraction: 0.5, molarFlow: 30, massFlow: 0.5 },
        { name: "Ethanol", moleFraction: 0.4, massFraction: 0.5, molarFlow: 20, massFlow: 0.5 },
      ],
    },
  ],
};

describe("formFromStream", () => {
  it("shows the stream in the flowsheet's units", () => {
    const f = formFromStream(stream, units);
    expect(f.mode).toBe("TP");
    expect(f.temperature).toEqual({ value: "25", unit: "C" });
    expect(f.pressure).toEqual({ value: "1.01325", unit: "bar" });
    expect(f.flow).toEqual({ value: "3600", unit: "kg/h" });
    expect(f.composition).toEqual({ Water: "0.6", Ethanol: "0.4" });
  });

  it("falls back to SI for a unit it cannot convert", () => {
    const f = formFromStream(stream, { ...units, pressure: "mmHg" });
    expect(f.pressure.unit).toBe("Pa");
  });
});

describe("toSpecification", () => {
  it("converts to SI and sends fractions with the total flow", () => {
    const spec = toSpecification(formFromStream(stream, units));
    expect(spec.temperature).toBeCloseTo(298.15, 10);
    expect(spec.pressure).toBeCloseTo(101325, 6);
    expect(spec.massFlow).toBeCloseTo(1, 12);
    expect(spec.moleFractions).toEqual({ Water: 0.6, Ethanol: 0.4 });
    expect(spec.vaporFraction).toBeUndefined();
  });

  it("sends only what the spec mode uses", () => {
    const f: StreamForm = { ...formFromStream(stream, units), mode: "PVF", vaporFraction: "0.25" };
    const spec = toSpecification(f);
    expect(spec.temperature).toBeUndefined();
    expect(spec.pressure).toBeCloseTo(101325, 6);
    expect(spec.vaporFraction).toBe(0.25);
  });

  it("sends compound flows, converted, with no total", () => {
    const f = withCompositionBasis(formFromStream(stream, units), "molarFlow", stream, units);
    expect(f.compositionUnit).toBe("kmol/h");
    expect(f.composition.Water).toBe("108");
    const spec = toSpecification(f);
    expect(spec.compoundMolarFlows?.Water).toBeCloseTo(30, 9);
    expect(spec.massFlow).toBeUndefined();
    expect(spec.molarFlow).toBeUndefined();
  });
});

describe("validate", () => {
  it("accepts a complete form", () => {
    expect(validate(formFromStream(stream, units))).toEqual({});
  });

  it("names the fields that are wrong", () => {
    const f: StreamForm = {
      ...formFromStream(stream, units),
      temperature: { value: "-300", unit: "C" },
      composition: { Water: "0", Ethanol: "0" },
    };
    expect(Object.keys(validate(f)).sort()).toEqual(["composition", "temperature"]);
  });

  it("checks the vapor fraction only when the mode uses it", () => {
    const f: StreamForm = { ...formFromStream(stream, units), vaporFraction: "2" };
    expect(validate(f)).toEqual({});
    expect(validate({ ...f, mode: "PVF" })).toHaveProperty("vaporFraction");
  });
});

describe("normalized", () => {
  it("scales fractions to one", () => {
    const f: StreamForm = { ...formFromStream(stream, units), composition: { Water: "3", Ethanol: "1" } };
    expect(normalized(f).composition).toEqual({ Water: "0.75", Ethanol: "0.25" });
  });
});
