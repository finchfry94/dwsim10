import { describe, expect, it } from "vitest";
import type { ReactionCompound, StoichiometryRow } from "../engine/types";
import { balanceBase, blankReaction, preview } from "./reactions";

const compounds: ReactionCompound[] = [
  {
    name: "Methane",
    formula: "CH4",
    molarWeight: 16.0425,
    enthalpyOfFormation: -74_520,
    gibbsOfFormation: -50_490,
  },
  { name: "Oxygen", formula: "O2", molarWeight: 31.9988, enthalpyOfFormation: 0, gibbsOfFormation: 0 },
  {
    name: "Carbon dioxide",
    formula: "CO2",
    molarWeight: 44.0095,
    enthalpyOfFormation: -393_510,
    gibbsOfFormation: -394_360,
  },
  {
    name: "Water",
    formula: "H2O",
    molarWeight: 18.0153,
    enthalpyOfFormation: -241_814,
    gibbsOfFormation: -228_590,
  },
  { name: "Nitrogen", formula: "N2", molarWeight: 28.014, enthalpyOfFormation: 0, gibbsOfFormation: 0 },
];

const row = (compound: string, coefficient: number, base = false): StoichiometryRow => ({
  compound,
  coefficient,
  base,
  directOrder: 0,
  reverseOrder: 0,
});

const combustion = [row("Methane", -1, true), row("Oxygen", -2), row("Carbon dioxide", 1), row("Water", 2)];

describe("stoichiometry preview", () => {
  it("writes the equation, the heat per base reactant and the balance", () => {
    const p = preview("Conversion", combustion, compounds);
    expect(p.equation).toBe("CH4 + 2O2 --> CO2 + 2H2O");
    expect(p.heat).toBeCloseTo(-393_510 - 2 * 241_814 + 74_520, 0);
    expect(p.balanced).toBe(true);
    expect(p.problem).toBeNull();
    expect(preview("Equilibrium", combustion, compounds).equation).toContain("<-->");
  });

  it("says what stops a save", () => {
    expect(
      preview(
        "Conversion",
        combustion.map((r) => ({ ...r, base: false })),
        compounds,
      ).problem,
    ).toMatch(/base component/);
    const unbalanced = [
      row("Methane", -1, true),
      row("Oxygen", -1),
      row("Carbon dioxide", 1),
      row("Water", 2),
    ];
    const p = preview("Conversion", unbalanced, compounds);
    expect(p.balanced).toBe(false);
    expect(p.imbalance).toBeCloseTo(31.9988, 3);
    expect(p.problem).toMatch(/mass-balanced/);
    expect(preview("Conversion", [row("Methane", -1, true)], compounds).problem).toMatch(/product/);
  });

  it("lists inerts of a catalytic reaction", () => {
    expect(
      preview("Heterogeneous_Catalytic", [...combustion, row("Nitrogen", 0)], compounds).equation,
    ).toContain("(N: N2)");
  });

  it("balances by the base component's coefficient", () => {
    const rows = [row("Methane", -3, true), row("Oxygen", -2), row("Carbon dioxide", 1), row("Water", 2)];
    const balanced = balanceBase(rows, compounds);
    expect(balanced[0]!.coefficient).toBeCloseTo(-1, 3);
    expect(preview("Conversion", balanced, compounds).balanced).toBe(true);
    expect(balanceBase([row("Methane", -1)], compounds)).toEqual([row("Methane", -1)]);
  });

  it("starts each type with the editors' defaults", () => {
    expect(blankReaction("Equilibrium")).toMatchObject({ phase: "Vapor", basis: "Fugacity", keq: "Gibbs" });
    expect(blankReaction("Heterogeneous_Catalytic").rateUnit).toBe("kmol/[kg.s]");
    expect(blankReaction("Conversion").expression).toBe("50");
  });
});
