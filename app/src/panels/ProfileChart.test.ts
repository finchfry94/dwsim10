import { describe, expect, it } from "vitest";
import { niceTicks, tickDigits, yRange } from "./ProfileChart";

describe("niceTicks", () => {
  it("covers the range with round steps", () => {
    const t = niceTicks(328.55, 337.9);
    expect(t[0]).toBeLessThanOrEqual(328.55);
    expect(t[t.length - 1]).toBeGreaterThanOrEqual(337.9);
    expect(t).toEqual([325, 330, 335, 340]);
  });

  it("widens a flat series so it still has an axis", () => {
    const t = niceTicks(101325, 101325);
    expect(t.length).toBeGreaterThan(1);
    expect(t[0]).toBeLessThan(101325);
  });
});

describe("tick labels", () => {
  it("carries enough digits to tell neighbouring ticks apart", () => {
    expect(tickDigits([325, 330, 335, 340])).toBe(4);
    expect(tickDigits([1.997e6, 1.9975e6, 1.998e6])).toBe(5);
    expect(tickDigits([299.998, 299.999, 300])).toBe(6);
  });

  it("shows a series flat to a few ppm as flat", () => {
    expect(yRange([300, 299.9982])).toEqual([299.9991, 299.9991]);
    expect(yRange([2e6, 1.9972e6])).toEqual([1.9972e6, 2e6]);
  });
});
