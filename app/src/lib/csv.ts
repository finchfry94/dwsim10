/** One CSV field: quoted when it holds a comma, quote or line break. */
function field(value: string | number | null | undefined): string {
  if (value === null || value === undefined) return "";
  const text = typeof value === "number" ? (Number.isFinite(value) ? String(value) : "") : value;
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

/** Rows as CSV text, numbers in full precision. */
export function toCsv(rows: (string | number | null | undefined)[][]): string {
  return rows.map((r) => r.map(field).join(",")).join("\r\n") + "\r\n";
}

/** Offers text as a file download. */
export function downloadText(name: string, text: string, type = "text/csv;charset=utf-8"): void {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** A file name from a title: "Pressure/temperature diagram" -> "pressure-temperature-diagram". */
export function fileName(title: string, ext: string): string {
  const base = title
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return `${base || "export"}.${ext}`;
}
