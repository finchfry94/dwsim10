// The feed stream editor's form, and its translation to the engine's stream.specify call.
// Values are kept as the user typed them, in the units they chose; conversion to SI happens
// only when the specification is built, so switching a unit never rounds a value away.

import type { StreamDetails, StreamSpecification, StreamSpecType, UnitSystem } from "../engine/types";
import { fromSI, knownUnit, toSI, type Quantity } from "./units";

export type SpecMode = "TP" | "PVF" | "TVF";
export type FlowBasis = "mass" | "molar";
export type CompositionBasis = "moleFraction" | "massFraction" | "molarFlow" | "massFlow";

export interface Measured {
  value: string;
  unit: string;
}

export interface StreamForm {
  mode: SpecMode;
  temperature: Measured;
  pressure: Measured;
  vaporFraction: string;
  flowBasis: FlowBasis;
  flow: Measured;
  compositionBasis: CompositionBasis;
  /** Unit of the compound flows when the composition is given as flows. */
  compositionUnit: string;
  composition: Record<string, string>;
}

export const SPEC_MODES: { mode: SpecMode; label: string }[] = [
  { mode: "TP", label: "Temperature and pressure" },
  { mode: "PVF", label: "Pressure and vapor fraction" },
  { mode: "TVF", label: "Temperature and vapor fraction" },
];

export const COMPOSITION_BASES: { basis: CompositionBasis; label: string }[] = [
  { basis: "moleFraction", label: "Mole fractions" },
  { basis: "massFraction", label: "Mass fractions" },
  { basis: "molarFlow", label: "Molar flows" },
  { basis: "massFlow", label: "Mass flows" },
];

const MODE_OF: Partial<Record<StreamSpecType, SpecMode>> = {
  Temperature_and_Pressure: "TP",
  Pressure_and_VaporFraction: "PVF",
  Temperature_and_VaporFraction: "TVF",
};

export const usesTemperature = (m: SpecMode) => m === "TP" || m === "TVF";
export const usesPressure = (m: SpecMode) => m === "TP" || m === "PVF";
export const usesVaporFraction = (m: SpecMode) => m === "PVF" || m === "TVF";
export const isFractionBasis = (b: CompositionBasis) => b === "moleFraction" || b === "massFraction";
export const compositionQuantity = (b: CompositionBasis): Quantity =>
  b === "massFlow" ? "massFlow" : "molarFlow";

/** The unit to show a quantity in: the flowsheet's, when this module can convert it. */
export function displayUnit(quantity: Quantity, system: UnitSystem | null | undefined): string {
  const preferred = {
    temperature: system?.temperature,
    pressure: system?.pressure,
    massFlow: system?.massflow,
    molarFlow: system?.molarflow,
    power: system?.heatflow,
  }[quantity];
  return knownUnit(quantity, preferred) ? preferred : fromSI(quantity, 0, undefined).unit;
}

const text = (v: number | null | undefined, digits = 8) =>
  v === null || v === undefined || !Number.isFinite(v) ? "" : String(Number(v.toPrecision(digits)));

function measured(
  quantity: Quantity,
  si: number | null | undefined,
  system: UnitSystem | null | undefined,
): Measured {
  const unit = displayUnit(quantity, system);
  return { unit, value: si === null || si === undefined ? "" : text(fromSI(quantity, si, unit).value) };
}

/** The form for a stream as the engine has it now. */
export function formFromStream(
  s: Extract<StreamDetails, { kind: "material" }>,
  system: UnitSystem | null | undefined,
): StreamForm {
  const mixture = s.phases.find((p) => p.index === 0);
  const composition: Record<string, string> = {};
  for (const c of mixture?.compounds ?? []) composition[c.name] = text(c.moleFraction, 6);
  return {
    mode: MODE_OF[s.spec] ?? "TP",
    temperature: measured("temperature", s.temperature, system),
    pressure: measured("pressure", s.pressure, system),
    vaporFraction: text(s.vaporFraction, 6) || "0",
    flowBasis: "mass",
    flow: measured("massFlow", s.massFlow, system),
    compositionBasis: "moleFraction",
    compositionUnit: displayUnit("molarFlow", system),
    composition,
  };
}

/** Switches the total flow between mass and molar, carrying the value over if both are known. */
export function withFlowBasis(
  form: StreamForm,
  basis: FlowBasis,
  s: Extract<StreamDetails, { kind: "material" }>,
  system: UnitSystem | null | undefined,
): StreamForm {
  if (basis === form.flowBasis) return form;
  const q: Quantity = basis === "mass" ? "massFlow" : "molarFlow";
  return {
    ...form,
    flowBasis: basis,
    flow: measured(q, basis === "mass" ? s.massFlow : s.molarFlow, system),
  };
}

/** Switches the composition basis, re-reading the values from the stream in the new basis. */
export function withCompositionBasis(
  form: StreamForm,
  basis: CompositionBasis,
  s: Extract<StreamDetails, { kind: "material" }>,
  system: UnitSystem | null | undefined,
): StreamForm {
  const mixture = s.phases.find((p) => p.index === 0);
  const unit = displayUnit(compositionQuantity(basis), system);
  const composition: Record<string, string> = {};
  for (const c of mixture?.compounds ?? []) {
    const v =
      basis === "moleFraction"
        ? c.moleFraction
        : basis === "massFraction"
          ? c.massFraction
          : basis === "molarFlow"
            ? c.molarFlow
            : c.massFlow;
    composition[c.name] = isFractionBasis(basis)
      ? text(v, 6)
      : v === null
        ? ""
        : text(fromSI(compositionQuantity(basis), v, unit).value, 6);
  }
  return { ...form, compositionBasis: basis, compositionUnit: unit, composition };
}

const num = (s: string) => (s.trim() === "" ? NaN : Number(s));

/** Problems that stop the form being applied, by field. Empty when it can be applied. */
export function validate(form: StreamForm): Record<string, string> {
  const errors: Record<string, string> = {};
  if (usesTemperature(form.mode)) {
    const t = num(form.temperature.value);
    if (!Number.isFinite(t)) errors.temperature = "Enter a temperature.";
    else if (toSI("temperature", t, form.temperature.unit) <= 0)
      errors.temperature = "Must be above absolute zero.";
  }
  if (usesPressure(form.mode)) {
    const p = num(form.pressure.value);
    if (!Number.isFinite(p)) errors.pressure = "Enter a pressure.";
    else if (toSI("pressure", p, form.pressure.unit) <= 0) errors.pressure = "Must be above zero.";
  }
  if (usesVaporFraction(form.mode)) {
    const vf = num(form.vaporFraction);
    if (!Number.isFinite(vf) || vf < 0 || vf > 1) errors.vaporFraction = "Between 0 and 1.";
  }
  if (isFractionBasis(form.compositionBasis)) {
    const f = num(form.flow.value);
    if (!Number.isFinite(f) || f < 0) errors.flow = "Enter a flow of zero or more.";
  }
  const values = Object.values(form.composition).map(num);
  if (values.some((v) => !Number.isFinite(v) || v < 0))
    errors.composition = "Every amount must be a number of zero or more.";
  else if (values.reduce((a, b) => a + b, 0) <= 0)
    errors.composition = "At least one compound must be present.";
  return errors;
}

/** The SI specification for stream.specify. Call only when validate() is empty. */
export function toSpecification(form: StreamForm): StreamSpecification {
  const spec: StreamSpecification = {};
  if (usesTemperature(form.mode))
    spec.temperature = toSI("temperature", num(form.temperature.value), form.temperature.unit);
  if (usesPressure(form.mode)) spec.pressure = toSI("pressure", num(form.pressure.value), form.pressure.unit);
  if (usesVaporFraction(form.mode)) spec.vaporFraction = num(form.vaporFraction);

  const amounts: Record<string, number> = {};
  for (const [name, v] of Object.entries(form.composition)) amounts[name] = num(v);

  switch (form.compositionBasis) {
    case "moleFraction":
    case "massFraction": {
      if (form.compositionBasis === "moleFraction") spec.moleFractions = amounts;
      else spec.massFractions = amounts;
      // Fractions say nothing about how much; the total flow does.
      const q: Quantity = form.flowBasis === "mass" ? "massFlow" : "molarFlow";
      const total = toSI(q, num(form.flow.value), form.flow.unit);
      if (form.flowBasis === "mass") spec.massFlow = total;
      else spec.molarFlow = total;
      break;
    }
    case "molarFlow":
      // Compound flows fix the total themselves.
      spec.compoundMolarFlows = Object.fromEntries(
        Object.entries(amounts).map(([n, v]) => [n, toSI("molarFlow", v, form.compositionUnit)]),
      );
      break;
    case "massFlow":
      spec.compoundMassFlows = Object.fromEntries(
        Object.entries(amounts).map(([n, v]) => [n, toSI("massFlow", v, form.compositionUnit)]),
      );
      break;
  }
  return spec;
}

/** The sum of the amounts typed, for the composition grid's footer. */
export function compositionSum(form: StreamForm): number {
  return Object.values(form.composition)
    .map(num)
    .filter(Number.isFinite)
    .reduce((a, b) => a + b, 0);
}

/** Scales fractions to add up to one. Flows are left as they are. */
export function normalized(form: StreamForm): StreamForm {
  if (!isFractionBasis(form.compositionBasis)) return form;
  const sum = compositionSum(form);
  if (sum <= 0) return form;
  const composition: Record<string, string> = {};
  for (const [n, v] of Object.entries(form.composition)) composition[n] = text(num(v) / sum, 6) || "0";
  return { ...form, composition };
}
