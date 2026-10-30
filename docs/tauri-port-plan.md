# DWSIM → Rust + Tauri + React: port plan

## Context
DWSIM's current desktop UI (`ui/`, Avalonia on .NET 10) has dated controls that are hard to use: editors are built row by row in code, the canvas is a Skia bitmap, and charts are custom. The goal is a modern web-based UI in Tauri with a Rust backend. Over time the computational engine should move to Rust too.

The engine is about 590k lines of VB/C#: 161k in thermo, 107k in unit ops, 153k in numerics. A rewrite up front would leave nothing usable for a long time, so the approach is **phased**:
1. Ship a new Tauri + React UI first, with the existing .NET engine running as a sidecar process.
2. Then port engine modules to Rust one at a time, checking each against the .NET results.

The existing code and the validation suite serve as the specification for every ported module.

## Status

Phase 0 is done, and a first slice of phases 1 and 2. The engine bridge, the Rust client, the
Tauri shell and the React app open, edit, solve and save flowsheets, and set a new simulation up
from scratch (milestone M1 below), and every unit operation has an editor generated from the
engine (M2), rigorous columns and the Adjust and Spec pickers included (M2b), and editing is
comfortable (M3: undo, copy/paste, multi-select and alignment, command palette, cancel solve,
diagnostics), and results can be read and analysed (M4a: stream table, phase and binary envelopes,
report; M4b: engineering tools, pipe profiles, Excel export). See `app/README.md`.

Front-end milestones still to do: M3 leftovers (annotations, per-object solve progress), M5 advanced tools (reactions,
sensitivity, optimizer, dynamics, scripting, spreadsheet, compound tools), M6 polish and shipping.
Simulation settings (solver mode, tolerances) from M1 are also still to do.

## Target architecture
```
app/
  src/                     React + TS + Vite
  src-tauri/               Rust: Tauri commands, sidecar manager
  crates/dwsim-bridge      JSON-RPC client to the .NET sidecar; Engine::call is the routing seam
  crates/dwsim-devserver   the engine over HTTP for browser development and e2e tests
  crates/dwsim-units …     (phase 3+) ported engine crates
tools/DWSIM.Bridge/        UI-oriented JSON-RPC sidecar (.NET)
```
- **The frontend only talks to Rust**, through Tauri `invoke` and events. Rust either forwards a call to the sidecar or answers it natively. Over time, more methods move to the native side behind the same `Engine::call`.
- **The sidecar** is a self-contained .NET publish shipped as a bundle resource (`engine/`), speaking JSON-RPC over stdio. It is a folder of a few hundred files, which suits a resource better than an `externalBin`.

## Phase 0: Foundations (scaffolding)
- Scaffold Tauri 2 + React/TS/Vite in `app/`. Use pnpm, ESLint, Prettier and Vitest; for Rust, cargo clippy/fmt.
- Create `tools/DWSIM.Bridge`. It reuses `tools/DWSIM.MCPServer/Rpc/JsonRpcDispatcher.cs`, `Transport/StdioTransport.cs`, `Sessions/SessionManager.cs` and the `McpTool` registry pattern (`Tools/ToolRegistry.cs`) by referencing them or moving them into a shared lib. The MCP tools are written for LLMs, so the bridge adds UI-oriented methods instead of changing them.
- The Rust `dwsim-bridge` crate spawns the sidecar, correlates requests and responses, forwards notifications (solver progress, log messages) as Tauri events, and restarts the sidecar if it crashes.
- Shared types: for now the TS types in `app/src/engine/types.ts` mirror the bridge by hand. Once methods are answered natively in Rust, generate them with `ts-rs` or `specta`.
- CI: a GitHub workflow that builds the sidecar (`dotnet publish -r <rid> --self-contained`), the Tauri bundles (win/linux/mac) and runs the tests. The existing `packaging/README.md` documents the RIDs.

## Phase 1: Bridge API (.NET sidecar, reusing the engine as-is)
These methods build on `engine/DWSIM.Automation/Automation.cs` (`Automation3`, `Flowsheet2`), `engine/DWSIM.FlowsheetBase/FlowsheetBase.vb` and `engine/DWSIM.FlowsheetSolver/FlowsheetSolver2.vb`:
- **Session:** `flowsheet.new/open/save/close`, for `.dwxml` and `.dwxmz` through the existing `LoadFromXML`/`SaveToXML`/`LoadZippedXML`.
- **Graph:** `graph.get` returns nodes (id, type, x, y, w, h, rotation, flip, port definitions) and edges (from/to object and port index), taken from `IGraphicObject`/connectors in `engine/DWSIM.Drawing.SkiaSharp`. Also `graph.add`, `graph.move`, `graph.connect`/`disconnect` (wrapping `DesignSurface.ConnectObject`), `graph.delete` and `graph.autolayout` (`OrthogonalFlowsheetLayout.vb`).
- **Icons:** `icons.get(type)` returns PNG bytes that already exist in Drawing.SkiaSharp. Hand-drawn SVGs can replace them later.
- **Editor schema (the key change):** `object.schema(id)` returns a declarative form (sections → fields of kind number+unit / enum / bool / text / compound-table, plus read-only results). It is produced by walking each editor's existing `CreateAndAdd*Row` calls in `ui/DWSIM.UI.Desktop.Editors.Avalonia/UnitOperations/*.cs`. The path: add an `IEditorSink` abstraction so the same row-building code can emit JSON instead of Avalonia controls, starting from `ui/DWSIM.UI.Shared.Avalonia/AvaloniaEditorExtensions.cs` and `AvaloniaTabBuilders.cs`. This keeps the editor knowledge of all 34 files and avoids rewriting it. `object.set(id, path, value, unit)` handles edits.
- **Streams:** stream compositions and phase properties (port the logic of `MaterialStream/CompoundGrid` and `PhasePropertyGrid`).
- **Setup data:** compounds (`compounds.search`, wrapping `CompoundSearch.cs` logic and `Databases.vb`), property packages (list/add/configure, `PropertyPackageFactory.vb`), reactions, and unit systems (`SystemsOfUnits.vb`).
- **Solving:** `solve.run` with progress notifications and cancellation, `solve.diagnostics` (reusing the MCP `SolveTools` logic), and the degrees-of-freedom check.
- **Analysis:** `analysis.phaseEnvelope`, `binaryEnvelope` and `propertyChart` return raw series, not images.
- **Reports:** `report.generate` (`SimulationReportBuilder.vb`) returns markdown/HTML.
- **Undo/redo:** a server-side command stack keyed by session. Edits go through it, and XML snapshots via `CloneXML` serve as the fallback.

## Phase 2: React frontend
Library choices: **React Flow** for the canvas, **shadcn/ui + Radix + Tailwind** for controls, **TanStack Table** or AG Grid for compound and result grids, **Plotly/ECharts** for charts, **Zustand** for state and **TanStack Query** for engine calls, **cmdk** for the command palette, and **dockview** for docking panels.
- **Shell:** docked layout (flowsheet in the center, object palette, properties inspector, log/solver console, results), a command palette (Ctrl+K), keyboard shortcuts and light/dark themes.
- **Flowsheet canvas:** custom React Flow node per object type with typed ports, orthogonal edges, drag from the palette, snapping, multi-select, alignment and minimap. Stream color comes from solved state, following the `StreamColoring.vb` rules.
- **Canvas styling** (React Flow custom nodes and edges, all colors from CSS tokens so light/dark mode comes for free):
  - *Symbols:* redraw the ~40 shapes in `engine/DWSIM.Drawing.SkiaSharp/GraphicObjects/Shapes/*.vb` as clean, line-art PFD symbols (ISO 10628-style) in inline SVG React components with `stroke="currentColor"` and a 1.5px stroke. Each symbol has the same port positions as its Skia original. No PNG bitmaps.
  - *Streams:* each kind has its own look. Material streams are solid 2px with arrowheads, energy streams dashed amber, signal/controller lines thin dotted grey. Edges are routed orthogonally with rounded corners. Auto-layout and routing use elkjs (layered, orthogonal) in place of `OrthogonalFlowsheetLayout.vb` on the frontend side.
  - *State:* a small status dot on each node (solved, not solved, error, calculating as a pulse) and a toolbar toggle that mirrors `StreamColorMode` (Status/T/P/VF/Phase/MassFlow). Those modes use perceptual scales from d3-scale-chromatic (sequential for T/P/flow, categorical for phase) with a legend overlay.
  - *Ports and interaction:* ports are hidden until hover or a connection drag. While dragging, compatible ports highlight and incompatible ones dim. Also snap-to-grid (dot grid background), alignment guides, a selection accent outline, and a minimap.
  - *Labels and level of detail:* the name sits under each node. Optional stream data tags (T/P/F with units) appear as floating chips and hide when zoomed out.
  - *Export:* SVG/PNG of the canvas for reports.
- **Schema-driven inspector:** one generic form renderer for `object.schema`. Every numeric field gets a unit dropdown with live conversion, inline validation and a "specified vs. calculated" indicator. Material streams get a dedicated composition grid with basis switching (mole/mass, fraction/flow).
- **Setup wizard and managers:** a multi-step new-simulation wizard (compounds → property package → units), a compound browser with search and filters, a property-package configurator with an editable BIP table, and a reaction manager.
- **Results:** sortable/exportable stream tables, a unit-op results panel, interactive charts (phase envelopes, column profiles, McCabe–Thiele) and a report viewer rendering the markdown.
- **Help:** bundle the MkDocs site from `helpsystem/` and open it in a panel.
- **Delivery order:** open and view an existing file (using `tests/flowsheets/*.dwxmz`) → edit streams and simple unit ops → build a flowsheet from scratch → solve → columns/reactors/analysis tools → dynamics, scripting, spreadsheet.

## Phase 3+: Incremental Rust engine port
Order chosen by fewest dependencies and easiest validation. Each module is a cargo crate behind an `EngineApi` trait, with a feature flag or per-call routing so either the .NET or Rust implementation can serve it:
1. `dwsim-units`: units of measure (`SystemsOfUnits.vb`). It moves conversion into the frontend's path with no IPC.
2. `dwsim-compounds`: load `chemsep*.xml`, `dwsim.xml`, `chedl_thermo.json` and `content/addcomps/*.json`, plus the BIP `.dat`/`.ipd` files, with serde.
3. `dwsim-thermo`: pure-component correlations, then PR/SRK (+ `CubicEOSDerivatives.vb`), steam tables IF97, NRTL/UNIQUAC/UNIFAC. Use `nalgebra`/`faer`; the `rust-coolprop` FFI works through the existing flat C API.
4. `dwsim-flash`: nested loops PT/PH/PS, then inside-out, then 3-phase/Gibbs (IPOPT: either the `ipopt` crate or port the managed `DWSIM.Numerics.Ipopt`).
5. `dwsim-unitops`: simple ops (mixer, splitter, heater/cooler, valve, pump, compressor, separators), then HX and pipe, then reactors and columns.
6. `dwsim-solver`: sequential-modular (port `FlowsheetSolver2.vb`), then write the EO driver from `helpsystem/docs/29-simultaneous-flowsheet-solver.md`. That driver is **missing from this repo**: only the `EOResiduals`/`EOJacobian` equations exist.
7. `dwsim-io`: native `.dwxml`/`.dwxmz` reading and writing (`quick-xml` + `zip`), compatible with existing files.

Once a solver-complete set of Rust modules covers a flowsheet type, that type can run with no sidecar. CAPE-OPEN (COM, Windows only), IronPython scripting and pythonnet ops stay on the sidecar until last, or for good.

## Optional later phase: 1D transient flow (new capability, Rust-native)
DWSIM has no true 1D CFD. The pipe segment (`UnitOperations/Pipe.vb`) marches in steady state through increments, using the Beggs-Brill, Lockhart-Martinelli or Petalas-Aziz correlations (`FluidFlowCorrelations/`). Its dynamic mode, like the PFR's (`Reactors/PFR.vb`), is a series of holdup cells with no momentum equation. A new `dwsim-flow1d` crate could add:
- Finite-volume mass, momentum and energy equations on a staggered grid, with implicit time stepping.
- Water hammer and surge handling.
- A drift-flux two-phase model later.

It would reuse the thermo crates for properties and plug in as a new unit op, with an axial-profile/time plot in the UI. Validation would use analytical water-hammer (Joukowsky) cases and steady-state agreement with the existing Pipe.

## Verification
- **Golden/differential tests:** a harness that runs the same input through the .NET sidecar and the Rust crate and compares within a tolerance. Sources:
  - `tests/DWSIM.Validation.Tests` cases T01–T19, unit ops and F01–F21 (export the expected values to JSON once).
  - `tests/DWSIM.Thermodynamics.Derivatives.Tests` for EOS and activity derivatives.
  - `tests/flowsheets/*.dwxml(z)` round-trip and solve results.
- **Bridge:** xUnit tests in `tests/` for every RPC method, including load → graph.get → solve → results on all 22 reference flowsheets.
- **Rust:** `cargo test` plus `proptest` for unit conversion round-trips; an integration test that spawns the sidecar.
- **Frontend:** Vitest component tests for the schema renderer; Playwright end-to-end against the Vite dev build with a mocked engine, plus smoke tests of the packaged Tauri app via `tauri-driver`.
- **Manual check per milestone:** run `pnpm tauri dev`, open `tests/flowsheets/` Cavett, edit a feed, solve, and compare the stream table with the Avalonia app.

## Risks and notes
- Sidecar size: a self-contained .NET publish is about 100+ MB. Accepted for phases 1–2, and it shrinks as modules move to Rust.
- The editor-schema extraction is the biggest leverage point and the biggest risk. Prototype it on `SimpleUnitOpEditors` before committing to it everywhere.
- Paid-edition features (refining, electrolytes, LCA) aren't in this fork and are out of scope.
- The existing Avalonia app stays buildable throughout as the reference implementation.
