# DWSIM desktop (Tauri + React)

A new desktop front end for DWSIM. A Rust [Tauri](https://tauri.app) shell hosts a React
frontend, and the existing .NET engine runs beside it as a sidecar process. The Avalonia app in
`ui/` remains the reference implementation while this one catches up.

```
app/
  src/                 React + TypeScript frontend (Vite, React Flow, Tailwind, Zustand)
  src-tauri/           the Tauri shell: owns the engine, relays calls and notifications
  crates/dwsim-bridge  Rust client for the engine sidecar (JSON-RPC over stdio)
  crates/dwsim-devserver  the same engine over HTTP, for browser development and e2e tests
  e2e/                 Playwright smoke test against the real engine
tools/DWSIM.Bridge/    the .NET engine sidecar (dwsim-bridge)
tests/DWSIM.Bridge.Tests/  its tests
```

## How it fits together

The frontend never reaches the engine directly. In the app it calls the Tauri command
`engine_call(method, params)` and listens to the `engine://notification` event. In a browser
the same calls go to `dwsim-devserver` (`POST /rpc`, `GET /events`). `src/engine/transport.ts`
picks the right one, and `src/engine/api.ts` types every method.

Both hosts use `dwsim_bridge::Engine`. It starts `dwsim-bridge`, matches responses to requests,
fans notifications out, and starts a new sidecar if the old one dies. As engine modules are
ported to Rust, `Engine::call` will answer their methods natively, so the frontend and the shell
don't change.

### Engine methods

| Method | Purpose |
|---|---|
| `engine.info`, `engine.ping` | version, method list, object types |
| `flowsheet.new/open/save/close/summary/list` | documents (`.dwxmz`, `.dwxml`) |
| `graph.get/add/move/connect/disconnect/delete/autoLayout` | the flowsheet as nodes, ports and edges |
| `compounds.search`, `propertyPackages.list` | what a simulation can be set up with |
| `flowsheet.addCompounds/addPropertyPackage` | set a simulation up (with flash settings) |
| `stream.specify` | a stream's T, P, vapor fraction, flows and composition, and its flash spec |
| `object.properties/setProperty/rename` | an object's property list, in the flowsheet's units |
| `stream.get` | a stream's state and composition by phase (SI) |
| `solve.run`, `solve.check` | solve (cancellable), and diagnose without solving |

Notifications: `engine/ready`, `engine/log`, `solve/started`, `solve/finished`. The Rust client
adds `engine/stderr` and `engine/exited`.

Port ids are `in-N` / `out-N` (the engine's connector indices) and `energy` (the energy
connector).

## Developing

Prerequisites: .NET 10 SDK, Rust (stable), Node 22 with pnpm, and on Linux the Tauri system
libraries (`libwebkit2gtk-4.1-dev libgtk-3-dev librsvg2-dev`). Check out the ReoGrid submodule
(`git submodule update --init`) before building the engine.

Build the engine first:

```bash
dotnet build tools/DWSIM.Bridge -c Release
```

Then, from `app/`, either run the desktop app:

```bash
pnpm install
pnpm tauri dev
```

or work in a browser, with the engine served over HTTP (two terminals):

```bash
pnpm dev:engine
```

```bash
pnpm dev
```

and open <http://localhost:1420>. The welcome page lists the reference flowsheets in
`tests/flowsheets`.

The sidecar is found through `DWSIM_BRIDGE` (path to the executable or the `.dll`), then next
to the app or in its bundled resources, then in a local `tools/DWSIM.Bridge/bin` build.

### Checks

```bash
pnpm typecheck && pnpm test && pnpm format:check
```

```bash
cargo fmt --all --check && cargo clippy --workspace --all-targets -- -D warnings && cargo test --workspace
```

```bash
dotnet test tests/DWSIM.Bridge.Tests -c Release
```

That suite also checks the bridge against the engine. `ReferenceFlowsheetTests` solves the 22
reference flowsheets through the bridge and through the app's own host, and requires the same
results. `BridgeValidationTests` runs the thermodynamics validation cases (T01–T34, with the
references of `tests/DWSIM.Validation.Tests`) in a separate `dwsim-bridge` process over stdio.

With the dev servers running, `NODE_PATH=$(npm root -g) node e2e/smoke.cjs` drives the UI end
to end.

### Bundling

```bash
pnpm bundle
```

This publishes the engine self-contained into `src-tauri/engine/` (`pnpm stage:engine`), then
builds the installers with that folder as a bundle resource. The engine adds roughly 100 MB
compressed. The `desktop` workflow builds all three platforms on a tag or on demand.

## Roadmap

See [`docs/tauri-port-plan.md`](../docs/tauri-port-plan.md). Next steps are an editor schema for
each unit operation (replacing the raw property list), the simulation setup wizard (compounds,
property package, units), and then porting engine modules to Rust behind `Engine::call`,
starting with units of measure.
