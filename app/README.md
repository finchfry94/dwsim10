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
| `graph.get/add/move/connect/disconnect/delete/autoLayout` | the flowsheet as nodes, ports and edges; connecting two unit operations inserts a stream |
| `graph.moveMany/deleteMany/transform` | several objects at once; rotate and flip |
| `graph.copy/paste` | the engine's clipboard XML, pasteable into any open flowsheet |
| `edit.undo/redo` | step back and forth through edits; `graph.get` carries the history and the unsaved flag |
| `compounds.search`, `propertyPackages.list` | what a simulation can be set up with |
| `flowsheet.addCompounds/removeCompound` | the simulation's compounds (streams follow) |
| `flowsheet.addPropertyPackage/removePropertyPackage` | its property packages, with flash settings |
| `unitSystems.list`, `flowsheet.setUnitSystem` | the units it shows |
| `stream.specify` | a stream's T, P, vapor fraction, total flow and composition (compound flows or fractions), and its flash spec |
| `object.schema`, `object.setField/setMode` | a unit operation's editor, generated from the engine (mode, fields, units, missing specs, tables, connections, results) |
| `object.variables` | the properties a logical block (Adjust, Spec) can point at on another object |
| `units.convert` | value between two units of a kind |
| `object.properties/setProperty/rename` | an object's raw property list, in the flowsheet's units |
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
results. `BridgeValidationTests` runs the thermodynamics cases (T01–T34) and the unit-operation
cases (U01–U08, A04) and the distillation cases (A01, F11) of `tests/DWSIM.Validation.Tests` in a
separate `dwsim-bridge` process over stdio, setting unit operations up through their editors; the
column duties must equal the in-process run. `EditorSchemaTests` checks every catalogued editor,
the column editors on three reference columns, and the Adjust and Spec pickers. `EditingTests`
covers undo and redo, copy and paste, batch edits and the inserted stream.

With the dev servers running, `NODE_PATH=$(npm root -g) node e2e/smoke.cjs` drives the UI end
to end: the Cavett problem, a new heater flowsheet (with delete and undo, duplicate, a check from
the command palette), a column whose reflux ratio is changed in its editor, and a cancelled solve.

### Bundling

```bash
pnpm bundle
```

This publishes the engine self-contained into `src-tauri/engine/` (`pnpm stage:engine`), then
builds the installers with that folder as a bundle resource. The engine adds roughly 100 MB
compressed. The `desktop` workflow builds all three platforms on a tag or on demand.

## Roadmap

See [`docs/tauri-port-plan.md`](../docs/tauri-port-plan.md). A new simulation can be set up
(wizard: compounds, property package, units), built, specified and solved, and every unit
operation has an editor generated from the engine, including rigorous columns and the Adjust and
Spec variable pickers. Editing works as in a drawing tool: undo and redo, copy, paste and duplicate,
multi-select with alignment, rotation and flips, a command palette (Ctrl+K), a shortcut sheet (?),
cancel solve, a diagnostics tab with jump-to-object, Save As, recent files and an unsaved-changes
guard. Next: results and analysis (M4).

### How undo works

Every edit through the bridge records the flowsheet as it was (`Session.Begin/Record`): the XML a
save would write. Undo loads that XML the way a file is opened, into a fresh flowsheet that takes
the session's place; the objects keep their ids, which the XML carries. The engine's own
`RestoreSnapshot(All)` does not bring deleted objects back when headless, hence the reload. The
history keeps the last 30 steps; a snapshot costs about 0.2 s on a 40-object flowsheet.

### How unit-operation editors are made

There is no hand-written editor per type. `tools/DWSIM.Bridge/Methods/EditorMethods.cs` builds a
schema from what the engine knows: the property list and its units, which properties each
calculation mode lets the user write (read on a copy of the object, per mode), the enum settings
found by reflection, and the degrees-of-freedom analysis. `Editors/EditorCatalog.cs` adds only
labels, mode order and hidden values, taken from the Avalonia editors. `Editors/SpecialFields.cs`
covers what the property list lacks: splitter ratios, component separator splits, the pipe
profile, shortcut column keys and the reactor's reaction set. `Editors/ColumnFields.cs` is the
rigorous column's editor (column, condenser, reboiler, stages, stream stages, initial estimates,
profiles), following the Avalonia column editors; a stream wired to a column on the canvas gets a
default stage (feeds the middle, products their end). `Editors/VariablePicker.cs` links Adjust and
Spec blocks to an object and property. `app/src/panels/SchemaEditor.tsx` renders any schema;
`ProfileChart.tsx` draws a results table's chart, one panel per unit.
