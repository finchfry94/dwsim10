/** "A" for 0, "Z" for 25, "AA" for 26: spreadsheet column letters. */
export function columnName(index: number): string {
  let name = "";
  for (let n = index + 1; n > 0; n = Math.floor((n - 1) / 26))
    name = String.fromCharCode(65 + ((n - 1) % 26)) + name;
  return name;
}

/** "B3" for row 2, column 1 (both from zero). */
export const address = (row: number, col: number) => `${columnName(col)}${row + 1}`;

/** Row and column (from zero) of an address such as "AB12", or null when it is not one. */
export function parseAddress(text: string): { row: number; col: number } | null {
  const m = /^\s*([A-Za-z]{1,3})(\d{1,7})\s*$/.exec(text);
  if (!m) return null;
  const col =
    m[1]!
      .toUpperCase()
      .split("")
      .reduce((n, c) => n * 26 + (c.charCodeAt(0) - 64), 0) - 1;
  const row = Number(m[2]) - 1;
  return row >= 0 ? { row, col } : null;
}

/** How many rows and columns the grid shows: the used range plus room to grow. */
export function gridSize(rows: number, cols: number): { rows: number; cols: number } {
  return { rows: Math.max(30, rows + 10), cols: Math.max(10, cols + 3) };
}

/** A cell's shown text: numbers at up to 6 significant figures, everything else as the engine shows it. */
export function shown(value: number | string | null, display: string): string {
  if (typeof value === "number" && Number.isFinite(value)) {
    const abs = Math.abs(value);
    return abs !== 0 && (abs < 1e-4 || abs >= 1e9)
      ? value.toExponential(4)
      : String(Number(value.toPrecision(6)));
  }
  return display;
}
