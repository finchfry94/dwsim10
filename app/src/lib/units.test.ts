import { describe, expect, it } from "vitest";
import { formatNumber, formatQuantity, fromSI, toSI, unitsOf } from "./units";

describe("fromSI", () => {
  it("converts temperatures with an offset", () => {
    expect(fromSI("temperature", 373.15, "C").value).toBeCloseTo(100, 10);
    expect(fromSI("temperature", 373.15, "F").value).toBeCloseTo(212, 6);
    expect(fromSI("temperature", 373.15, "C").unit).toBe("°C");
  });

  it("scales pressures and flows", () => {
    expect(fromSI("pressure", 101325, "atm").value).toBeCloseTo(1, 10);
    expect(fromSI("pressure", 2e5, "bar").value).toBeCloseTo(2, 10);
    expect(fromSI("massFlow", 1, "kg/h").value).toBe(3600);
    expect(fromSI("molarFlow", 1, "kmol/h").value).toBeCloseTo(3.6, 10);
  });

  it("keeps SI for a unit it does not know", () => {
    expect(fromSI("pressure", 5, "furlongs")).toEqual({ value: 5, unit: "Pa" });
  });
});

describe("toSI", () => {
  it("inverts fromSI for every unit", () => {
    for (const [q, si] of [
      ["temperature", 350.0],
      ["pressure", 2.5e5],
      ["massFlow", 3.2],
      ["molarFlow", 11.0],
      ["power", 42.0],
    ] as const) {
      for (const u of unitsOf(q)) {
        expect(toSI(q, fromSI(q, si, u).value, u)).toBeCloseTo(si, 9);
      }
    }
  });

  it("reads °C and bar", () => {
    expect(toSI("temperature", 25, "C")).toBeCloseTo(298.15, 12);
    expect(toSI("pressure", 1.01325, "bar")).toBeCloseTo(101325, 9);
  });
});

describe("formatting", () => {
  it("rounds to significant figures", () => {
    expect(formatNumber(331.19017)).toBe("331.2");
    expect(formatNumber(0.000012)).toBe("1.200e-5");
    expect(formatNumber(null)).toBe("—");
  });

  it("formats a quantity with its unit", () => {
    expect(formatQuantity("temperature", 300, "C")).toBe("26.85 °C");
  });
});
