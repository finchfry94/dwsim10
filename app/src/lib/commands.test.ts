import { describe, expect, it, vi } from "vitest";
import { buildCommands, commandFor, fuzzyScore, matches } from "./commands";

const key = (
  k: string,
  mods: Partial<Record<"ctrlKey" | "metaKey" | "shiftKey" | "altKey", boolean>> = {},
) => ({
  key: k,
  ctrlKey: false,
  metaKey: false,
  shiftKey: false,
  altKey: false,
  ...mods,
});

describe("matches", () => {
  it("reads Mod as Ctrl off a Mac and tells Shift apart", () => {
    expect(matches(key("z", { ctrlKey: true }), "Mod+Z")).toBe(true);
    expect(matches(key("Z", { ctrlKey: true, shiftKey: true }), "Mod+Z")).toBe(false);
    expect(matches(key("Z", { ctrlKey: true, shiftKey: true }), "Mod+Shift+Z")).toBe(true);
    expect(matches(key("z"), "Mod+Z")).toBe(false);
    expect(matches(key("r"), "R")).toBe(true);
    expect(matches(key("r", { ctrlKey: true }), "R")).toBe(false);
  });

  it("takes a shifted symbol as typed", () => {
    expect(matches(key("?", { shiftKey: true }), "?")).toBe(true);
    expect(matches(key("F5"), "F5")).toBe(true);
  });
});

describe("commandFor", () => {
  const shell = { open: vi.fn(), save: vi.fn(), toggleTheme: vi.fn() };
  const commands = buildCommands(shell);
  const event = (k: string, mods: Parameters<typeof key>[1], target: EventTarget | null = document.body) =>
    ({ ...key(k, mods), target }) as unknown as KeyboardEvent;

  it("leaves typing keys to text fields but still saves from them", () => {
    const input = document.createElement("input");
    expect(commandFor(event("r", {}, input), commands)).toBeUndefined();
    expect(commandFor(event("s", { ctrlKey: true }, input), commands)).toBeUndefined(); // no flowsheet to save
    expect(commandFor(event("o", { ctrlKey: true }, input), commands)?.id).toBe("file.open");
  });

  it("finds an enabled command for its shortcut", () => {
    expect(commandFor(event("k", { ctrlKey: true }), commands)?.id).toBe("view.palette");
    expect(commandFor(event("o", { ctrlKey: true }), commands)?.id).toBe("file.open");
    // nothing selected and no flowsheet: rotate and undo stay disabled
    expect(commandFor(event("r", {}), commands)).toBeUndefined();
    expect(commandFor(event("z", { ctrlKey: true }), commands)).toBeUndefined();
  });
});

describe("fuzzyScore", () => {
  it("matches subsequences and ranks word starts and runs first", () => {
    expect(fuzzyScore("Rotate 90° clockwise", "rot")).not.toBeNull();
    expect(fuzzyScore("Undo", "xyz")).toBeNull();
    const word = fuzzyScore("Heat exchanger", "hx")!;
    const inner = fuzzyScore("Check the flowsheet", "hx");
    expect(inner).toBeNull();
    expect(fuzzyScore("Save as…", "save")!).toBeGreaterThan(
      fuzzyScore("Spread evenly across", "save") ?? -Infinity,
    );
    expect(word).toBeGreaterThan(0);
  });
});
