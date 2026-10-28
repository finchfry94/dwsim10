import { describe, expect, it } from "vitest";
import { formatNumber, formatQuantity, fromSI } from "./units";

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
