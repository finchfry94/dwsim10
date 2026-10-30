import { describe, expect, it } from "vitest";
import { parsePoints } from "./CompoundToolsDialog";

describe("regression data", () => {
  it("reads T P x1 y1 rows separated by spaces, tabs, commas or semicolons", () => {
    expect(parsePoints("-3.15 8.74 0.1 0.5\n-3.15\t14.61\t0.25\n78.3;1.013;0.9;0.88")).toEqual([
      { t: -3.15, p: 8.74, x1: 0.1, y1: 0.5 },
      { t: -3.15, p: 14.61, x1: 0.25, y1: null },
      { t: 78.3, p: 1.013, x1: 0.9, y1: 0.88 },
    ]);
  });

  it("skips headers, comments, blanks and short rows", () => {
    expect(parsePoints("T P x1 y1\n# from the paper\n\n1 2\n50, 1.0, 0.3, 0.6")).toEqual([
      { t: 50, p: 1, x1: 0.3, y1: 0.6 },
    ]);
  });
});
