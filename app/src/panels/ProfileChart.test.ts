import { describe, expect, it } from "vitest";
import { niceTicks } from "./ProfileChart";

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
