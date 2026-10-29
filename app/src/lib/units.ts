// Display conversion from SI for the handful of quantities the canvas and the stream summary
// show. Unit strings are DWSIM's own (SystemsOfUnits.vb). Anything not listed stays in SI; the
// property list needs none of this, the engine converts those values itself.

export type Quantity = "temperature" | "pressure" | "massFlow" | "molarFlow" | "power";

interface Unit {
  /** value_in_unit = si * factor + offset */
  factor: number;
  offset?: number;
}

const UNITS: Record<Quantity, Record<string, Unit>> = {
  temperature: {
    K: { factor: 1 },
    C: { factor: 1, offset: -273.15 },
    F: { factor: 1.8, offset: -459.67 },
    R: { factor: 1.8 },
  },
  pressure: {
    Pa: { factor: 1 },
    kPa: { factor: 1e-3 },
    MPa: { factor: 1e-6 },
    bar: { factor: 1e-5 },
    atm: { factor: 1 / 101325 },
    psi: { factor: 1.450377e-4 },
    "kgf/cm2": { factor: 1 / 98066.5 },
    "lbf/ft2": { factor: 0.0208854 },
  },
  massFlow: {
    "kg/s": { factor: 1 },
    "kg/h": { factor: 3600 },
    "kg/min": { factor: 60 },
    "kg/d": { factor: 86400 },
    "g/s": { factor: 1000 },
    "t/h": { factor: 3.6 },
    "lbm/h": { factor: 7936.64 },
    "lb/h": { factor: 7936.64 },
  },
  molarFlow: {
    "mol/s": { factor: 1 },
    "mol/h": { factor: 3600 },
    "kmol/h": { factor: 3.6 },
    "lbmol/h": { factor: 7.93664 },
  },
  power: {
    kW: { factor: 1 },
    W: { factor: 1000 },
    MW: { factor: 1e-3 },
    "kJ/h": { factor: 3600 },
    "BTU/h": { factor: 3412.14 },
  },
};

export const SI: Record<Quantity, string> = {
  temperature: "K",
  pressure: "Pa",
  massFlow: "kg/s",
  molarFlow: "mol/s",
  power: "kW",
};

/** The units the editors offer for a quantity, SI first. */
export function unitsOf(quantity: Quantity): string[] {
  return Object.keys(UNITS[quantity]);
}

/** Whether a unit string is one this module converts (DWSIM's own spelling). */
export const knownUnit = (quantity: Quantity, unit: string | undefined): unit is string =>
  unit !== undefined && unit in UNITS[quantity];

/** Converts a value in `unit` to SI; the inverse of fromSI. An unknown unit is taken as SI. */
export function toSI(quantity: Quantity, value: number, unit: string | undefined): number {
  const u = unit ? UNITS[quantity][unit] : undefined;
  if (!u) return value;
  return (value - (u.offset ?? 0)) / u.factor;
}

/** Shown name of a unit: DWSIM writes Celsius and Fahrenheit as C and F. */
/** A unit as shown; the engine spells a temperature difference with a trailing dot ("C."). */
export const unitLabel = (unit: string): string => {
  if (/^[KCFR]\.$/.test(unit)) return `Δ${unitLabel(unit.slice(0, -1))}`;
  return unit === "C" || unit === "F" ? `°${unit}` : unit;
};

/** Converts an SI value; falls back to SI when the unit is unknown. */
export function fromSI(
  quantity: Quantity,
  value: number,
  unit: string | undefined,
): { value: number; unit: string } {
  const u = unit ? UNITS[quantity][unit] : undefined;
  if (!u || !unit) return { value, unit: SI[quantity] };
  return {
    value: value * u.factor + (u.offset ?? 0),
    unit: unitLabel(unit),
  };
}

/** Rounds for display: four significant figures, no exponent for ordinary magnitudes. */
export function formatNumber(value: number | null | undefined, digits = 4): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return "—";
  if (value === 0) return "0";
  const abs = Math.abs(value);
  if (abs >= 1e6 || abs < 1e-3) return value.toExponential(digits - 1);
  return Number(value.toPrecision(digits)).toLocaleString("en-US", { maximumFractionDigits: 6 });
}

export function formatQuantity(
  quantity: Quantity,
  si: number | null | undefined,
  unit: string | undefined,
): string {
  if (si === null || si === undefined || !Number.isFinite(si)) return "—";
  const c = fromSI(quantity, si, unit);
  return `${formatNumber(c.value)} ${c.unit}`;
}
