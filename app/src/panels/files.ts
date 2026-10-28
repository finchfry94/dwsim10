import { isTauri } from "../engine/transport";

const FILTERS = [{ name: "DWSIM flowsheet", extensions: ["dwxmz", "dwxml"] }];

/** Asks for a flowsheet to open with the native dialog; null in a browser or when cancelled. */
export async function pickFileToOpen(): Promise<string | null> {
  if (!isTauri()) return null;
  const { open } = await import("@tauri-apps/plugin-dialog");
  const path = await open({ multiple: false, directory: false, filters: FILTERS });
  return typeof path === "string" ? path : null;
}

export async function pickFileToSave(current?: string | null): Promise<string | null> {
  if (!isTauri()) return null;
  const { save } = await import("@tauri-apps/plugin-dialog");
  return save({ filters: FILTERS, defaultPath: current ?? undefined });
}
