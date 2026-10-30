import { describe, expect, it } from "vitest";
import { fileName, toCsv } from "./csv";

describe("toCsv", () => {
  it("quotes what needs quoting and leaves blanks for missing values", () => {
    expect(
      toCsv([
        ["Property", "a,b", 'say "hi"'],
        [1.5, null, Number.NaN],
      ]),
    ).toBe('Property,"a,b","say ""hi"""\r\n1.5,,\r\n');
  });
});

describe("fileName", () => {
  it("makes a safe name from a title", () => {
    expect(fileName("Pressure/temperature diagram", "csv")).toBe("pressure-temperature-diagram.csv");
    expect(fileName("///", "html")).toBe("export.html");
  });
});
