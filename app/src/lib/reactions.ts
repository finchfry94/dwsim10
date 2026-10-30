import type { Reaction, ReactionCompound, ReactionType, StoichiometryRow } from "../engine/types";

/** What the editor shows as the stoichiometry changes: the engine recomputes the same on save. */
export interface StoichiometryPreview {
  equation: string;
  /** kJ/kmol of the base reactant, at 25 °C. */
  heat: number;
  /** Products' minus reactants' molar mass, kg/kmol; zero when balanced. */
  imbalance: number;
  balanced: boolean;
  /** Why the reaction cannot be saved yet, if it cannot. */
  problem: string | null;
}

const term = (coefficient: number, formula: string) => {
  const a = Math.abs(coefficient);
  return (Math.abs(a - 1) < 1e-10 ? "" : Number(a.toPrecision(4)).toString()) + formula;
};

export function preview(
  type: ReactionType,
  rows: StoichiometryRow[],
  compounds: ReactionCompound[],
): StoichiometryPreview {
  const data = new Map(compounds.map((c) => [c.name, c]));
  const reactants: string[] = [];
  const products: string[] = [];
  const inerts: string[] = [];
  let hr = 0;
  let hp = 0;
  let mr = 0;
  let mp = 0;
  let baseCoefficient = 1;
  for (const r of rows) {
    const c = data.get(r.compound);
    if (!c) continue;
    const a = Math.abs(r.coefficient);
    if (r.coefficient < 0) {
      reactants.push(term(r.coefficient, c.formula));
      hr += a * c.enthalpyOfFormation;
      mr += a * c.molarWeight;
    } else if (r.coefficient > 0) {
      products.push(term(r.coefficient, c.formula));
      hp += a * c.enthalpyOfFormation;
      mp += a * c.molarWeight;
    } else inerts.push(c.formula);
    if (r.base && a > 0) baseCoefficient = a;
  }
  let equation = reactants.join(" + ") + (type === "Conversion" ? " --> " : " <--> ") + products.join(" + ");
  if (type === "Heterogeneous_Catalytic" && inerts.length > 0) equation += `  (N: ${inerts.join(", ")})`;
  const imbalance = mp - mr;
  const balanced = Math.abs(imbalance) < 1e-3;
  const base = rows.filter((r) => r.base);
  const problem =
    base.length !== 1
      ? "Mark one reactant as the base component."
      : base[0]!.coefficient >= 0
        ? "The base component must be a reactant (a negative coefficient)."
        : !rows.some((r) => r.coefficient > 0)
          ? "Add at least one product (a positive coefficient)."
          : !balanced
            ? "The stoichiometry is not mass-balanced."
            : null;
  return { equation, heat: (hp - hr) / baseCoefficient, imbalance, balanced, problem };
}

/** The base reactant's coefficient that balances the masses, as the classic Balance button sets it. */
export function balanceBase(rows: StoichiometryRow[], compounds: ReactionCompound[]): StoichiometryRow[] {
  const data = new Map(compounds.map((c) => [c.name, c]));
  const base = rows.find((r) => r.base);
  const mw = base && data.get(base.compound)?.molarWeight;
  if (!base || !mw) return rows;
  const others = rows
    .filter((r) => r !== base)
    .reduce((sum, r) => sum + r.coefficient * (data.get(r.compound)?.molarWeight ?? 0), 0);
  const coefficient = Number((-others / mw).toPrecision(6));
  return rows.map((r) => (r === base ? { ...r, coefficient } : r));
}

/** A new reaction of a type with the editors' defaults. */
export function blankReaction(type: ReactionType): Reaction {
  const kinetic = type === "Kinetic" || type === "Heterogeneous_Catalytic";
  const term = { type: "Arrhenius" as const, a: 0, e: 0, eUnit: "J/mol", expression: "" };
  return {
    type,
    name: "",
    description: "",
    components: [],
    phase: type === "Equilibrium" ? "Vapor" : "Mixture",
    basis: type === "Equilibrium" ? "Fugacity" : "MolarConc",
    basisUnit: kinetic ? "kmol/m3" : "",
    rateUnit: type === "Heterogeneous_Catalytic" ? "kmol/[kg.s]" : kinetic ? "kmol/[m3.s]" : "",
    tmin: type === "Equilibrium" ? 298.15 : 0,
    tmax: 2000,
    approach: 0,
    keq: "Gibbs",
    expression: type === "Conversion" ? "50" : "",
    constantKeq: 1,
    kinetics: "Expression",
    script: "",
    forward: { ...term },
    reverse: { ...term },
    numerator: "",
    denominator: "1",
  };
}

export const REACTION_TYPES: { type: ReactionType; label: string }[] = [
  { type: "Conversion", label: "Conversion" },
  { type: "Equilibrium", label: "Equilibrium" },
  { type: "Kinetic", label: "Kinetic" },
  { type: "Heterogeneous_Catalytic", label: "Heterogeneous catalytic" },
];

export const typeLabel = (type: string) => REACTION_TYPES.find((t) => t.type === type)?.label ?? type;
