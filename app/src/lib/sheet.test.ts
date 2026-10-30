import { describe, expect, it } from "vitest";
import { address, columnName, gridSize, parseAddress, shown } from "./sheet";

describe("spreadsheet addressing", () => {
  it("names columns as spreadsheets do", () => {
    expect([0, 25, 26, 27, 51, 52, 701, 702].map(columnName)).toEqual([
      "A",
      "Z",
      "AA",
      "AB",
      "AZ",
      "BA",
      "ZZ",
      "AAA",
    ]);
    expect(address(2, 1)).toBe("B3");
  });

  it("reads addresses back", () => {
    expect(parseAddress("B3")).toEqual({ row: 2, col: 1 });
    expect(parseAddress(" aa10 ")).toEqual({ row: 9, col: 26 });
    for (const bad of ["", "3B", "A0", "ZZZZ1", "A1:B2"]) expect(parseAddress(bad)).toBeNull();
    for (let c = 0; c < 800; c += 37) expect(parseAddress(address(5, c))).toEqual({ row: 5, col: c });
  });

  it("leaves room around the used range", () => {
    expect(gridSize(0, 0)).toEqual({ rows: 30, cols: 10 });
    expect(gridSize(50, 12)).toEqual({ rows: 60, cols: 15 });
  });

  it("shows numbers compactly and text as given", () => {
    expect(shown(38.81314582348904, "38.81314582348904")).toBe("38.8131");
    expect(shown(1.5e-7, "")).toBe("1.5000e-7");
    expect(shown("EXPORT OK", "EXPORT OK")).toBe("EXPORT OK");
    expect(shown(null, "")).toBe("");
  });
});
