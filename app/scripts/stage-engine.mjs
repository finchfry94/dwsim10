// Publishes the engine sidecar (tools/DWSIM.Bridge) self-contained for this machine into
// src-tauri/engine/, where tauri.bundle.conf.json picks it up as a bundle resource.
//
//   node scripts/stage-engine.mjs            # this machine's runtime id
//   node scripts/stage-engine.mjs linux-arm64

import { execFileSync } from "node:child_process";
import { rmSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const app = join(here, "..");
const project = join(app, "..", "tools", "DWSIM.Bridge", "DWSIM.Bridge.csproj");
const out = join(app, "src-tauri", "engine");

const os = { win32: "win", darwin: "osx", linux: "linux" }[process.platform];
const arch = { x64: "x64", arm64: "arm64" }[process.arch];
const rid = process.argv[2] ?? `${os}-${arch}`;
if (!rid || rid.includes("undefined")) {
  console.error(`No .NET runtime id for ${process.platform}/${process.arch}; pass one, e.g. linux-x64.`);
  process.exit(1);
}

rmSync(out, { recursive: true, force: true });
console.log(`Publishing the engine for ${rid} to ${out}`);
execFileSync("dotnet", ["publish", project, "-c", "Release", "-r", rid, "--self-contained", "true", "-o", out], {
  stdio: "inherit",
});
