import { transport } from "./transport";
import type {
  CompoundInfo,
  EditorSchema,
  EngineInfo,
  Finding,
  FlowsheetSummary,
  Graph,
  GraphNode,
  ObjectProperties,
  PropertyEntry,
  SolveResult,
  StreamDetails,
  StreamSpecification,
  UnitSystem,
  VariableOption,
  ChartData,
  EnvelopeDiagram,
  StreamTable,
  ToolInfo,
  ToolRun,
  Reaction,
  ReactionSet,
  ReactionsCatalog,
  SensitivityCase,
  SensitivityResult,
  OptimizerCase,
  OptimizerResult,
  ChoiceOption,
  Script,
  ScriptRun,
  Sheet,
  ScenarioComparison,
  ScenarioSlot,
  DynamicsState,
  DynamicsIssue,
  DynamicsRun,
  DynamicsIntegrator,
  DynamicsSchedule,
  DynamicsEventSet,
  CompoundInput,
  CompoundEstimate,
  PetroleumOptions,
  PetroleumResult,
  RegressionModel,
  RegressionResult,
} from "./types";

const call = transport.call.bind(transport);

/** One function per bridge method, typed. Names follow the RPC names. */
export const engine = {
  info: () => call<EngineInfo>("engine.info"),

  newFlowsheet: (name?: string) => call<FlowsheetSummary>("flowsheet.new", { name }),
  openFlowsheet: (path: string) => call<FlowsheetSummary>("flowsheet.open", { path }),
  saveFlowsheet: (flowsheetId: string, path?: string) =>
    call<{ path: string }>("flowsheet.save", { flowsheetId, path }),
  closeFlowsheet: (flowsheetId: string) => call<{ closed: boolean }>("flowsheet.close", { flowsheetId }),
  summary: (flowsheetId: string) => call<FlowsheetSummary>("flowsheet.summary", { flowsheetId }),

  graph: (flowsheetId: string) => call<Graph>("graph.get", { flowsheetId }),
  undo: (flowsheetId: string) => call<Graph>("edit.undo", { flowsheetId }),
  redo: (flowsheetId: string) => call<Graph>("edit.redo", { flowsheetId }),
  moveMany: (flowsheetId: string, moves: { id: string; x: number; y: number }[]) =>
    call<Graph>("graph.moveMany", { flowsheetId, moves }),
  deleteMany: (flowsheetId: string, ids: string[]) => call<Graph>("graph.deleteMany", { flowsheetId, ids }),
  transform: (
    flowsheetId: string,
    ids: string[],
    change: { rotate?: number; flipH?: boolean; flipV?: boolean },
  ) => call<Graph>("graph.transform", { flowsheetId, ids, ...change }),
  copy: (flowsheetId: string, ids: string[]) =>
    call<{ text: string; count: number }>("graph.copy", { flowsheetId, ids }),
  paste: (flowsheetId: string, text: string) => call<Graph>("graph.paste", { flowsheetId, text }),
  addObject: (flowsheetId: string, type: string, x: number, y: number, tag?: string) =>
    call<GraphNode>("graph.add", { flowsheetId, type, x, y, tag }),
  moveObject: (flowsheetId: string, id: string, x: number, y: number) =>
    call<GraphNode>("graph.move", { flowsheetId, id, x, y }),
  connect: (
    flowsheetId: string,
    sourceId: string,
    sourcePort: string,
    targetId: string,
    targetPort: string,
  ) => call<Graph>("graph.connect", { flowsheetId, sourceId, sourcePort, targetId, targetPort }),
  disconnect: (flowsheetId: string, sourceId: string, targetId: string) =>
    call<Graph>("graph.disconnect", { flowsheetId, sourceId, targetId }),
  deleteObject: (flowsheetId: string, id: string) => call<Graph>("graph.delete", { flowsheetId, id }),
  autoLayout: (flowsheetId: string) => call<Graph>("graph.autoLayout", { flowsheetId }),

  properties: (flowsheetId: string, id: string) =>
    call<ObjectProperties>("object.properties", { flowsheetId, id }),
  setProperty: (flowsheetId: string, id: string, property: string, value: number | string | boolean) =>
    call<PropertyEntry>("object.setProperty", { flowsheetId, id, property, value }),
  rename: (flowsheetId: string, id: string, tag: string) =>
    call<GraphNode>("object.rename", { flowsheetId, id, tag }),
  stream: (flowsheetId: string, id: string) => call<StreamDetails>("stream.get", { flowsheetId, id }),

  searchCompounds: (flowsheetId: string, query: string, limit = 50) =>
    call<CompoundInfo[]>("compounds.search", { flowsheetId, query, limit }),
  propertyPackages: (flowsheetId: string) => call<string[]>("propertyPackages.list", { flowsheetId }),
  unitSystems: (flowsheetId: string) => call<UnitSystem[]>("unitSystems.list", { flowsheetId }),
  addCompounds: (flowsheetId: string, names: string[]) =>
    call<FlowsheetSummary>("flowsheet.addCompounds", { flowsheetId, names }),
  removeCompound: (flowsheetId: string, name: string) =>
    call<FlowsheetSummary>("flowsheet.removeCompound", { flowsheetId, name }),
  addPropertyPackage: (flowsheetId: string, name: string, flashSettings?: Record<string, unknown>) =>
    call<FlowsheetSummary>("flowsheet.addPropertyPackage", { flowsheetId, name, flashSettings }),
  removePropertyPackage: (flowsheetId: string, id: string) =>
    call<FlowsheetSummary>("flowsheet.removePropertyPackage", { flowsheetId, id }),
  setUnitSystem: (flowsheetId: string, name: string) =>
    call<FlowsheetSummary>("flowsheet.setUnitSystem", { flowsheetId, name }),
  specifyStream: (flowsheetId: string, id: string, spec: StreamSpecification) =>
    call<GraphNode>("stream.specify", { flowsheetId, id, ...spec }),

  schema: (flowsheetId: string, id: string) => call<EditorSchema>("object.schema", { flowsheetId, id }),
  setField: (flowsheetId: string, id: string, field: string, value: unknown, unit?: string | null) =>
    call<EditorSchema>("object.setField", { flowsheetId, id, field, value, unit: unit ?? undefined }),
  variables: (flowsheetId: string, id: string, writable: boolean) =>
    call<VariableOption[]>("object.variables", { flowsheetId, id, writable }),
  setMode: (flowsheetId: string, id: string, value: string) =>
    call<EditorSchema>("object.setMode", { flowsheetId, id, value }),
  convertUnits: (value: number, from: string, to: string) =>
    call<number>("units.convert", { value, from, to }),

  /** Solves; a `requestId` lets {@link engine.cancel} stop it. */
  solve: (flowsheetId: string, requestId?: string) =>
    call<SolveResult>("solve.run", { flowsheetId }, requestId),
  cancel: (requestId: string) => transport.cancel(requestId),
  streamTable: (flowsheetId: string, basis: "mole" | "mass" = "mole") =>
    call<StreamTable>("streams.table", { flowsheetId, basis }),
  report: (flowsheetId: string, objectIds?: string[], sections?: Record<string, boolean>) =>
    call<{ html: string }>("report.html", { flowsheetId, objectIds, sections }),
  phaseEnvelope: (
    flowsheetId: string,
    streamId: string,
    diagram: EnvelopeDiagram,
    options?: Record<string, unknown>,
    requestId?: string,
  ) => call<ChartData>("analysis.phaseEnvelope", { flowsheetId, streamId, diagram, options }, requestId),
  binaryEnvelope: (
    flowsheetId: string,
    params: {
      compound1: string;
      compound2: string;
      diagram: "Txy" | "Pxy";
      packageId?: string;
      temperature?: number;
      pressure?: number;
      lle?: boolean;
      sle?: boolean;
      critical?: boolean;
    },
    requestId?: string,
  ) => call<ChartData>("analysis.binaryEnvelope", { flowsheetId, ...params }, requestId),
  check: (flowsheetId: string) => call<Finding[]>("solve.check", { flowsheetId }),

  tools: (flowsheetId: string) => call<ToolInfo[]>("tools.list", { flowsheetId }),
  /** Numbers are in each field's unit, or in `units[id]` when the user picked another. */
  runTool: (
    flowsheetId: string,
    tool: string,
    inputs: Record<string, unknown>,
    units: Record<string, string>,
    requestId?: string,
  ) => call<ToolRun>("tools.run", { flowsheetId, tool, inputs, units }, requestId),
  /** The stream table and every object's results as an .xlsx workbook, base64. */
  exportExcel: (flowsheetId: string) =>
    call<{ name: string; base64: string }>("export.xlsx", { flowsheetId }),

  reactions: (flowsheetId: string) => call<ReactionsCatalog>("reactions.list", { flowsheetId }),
  reaction: (flowsheetId: string, id: string) => call<Reaction>("reactions.get", { flowsheetId, id }),
  saveReaction: (flowsheetId: string, reaction: Reaction) =>
    call<Reaction>("reactions.save", { flowsheetId, reaction }),
  deleteReaction: (flowsheetId: string, id: string) =>
    call<ReactionsCatalog>("reactions.delete", { flowsheetId, id }),
  duplicateReaction: (flowsheetId: string, id: string) =>
    call<Reaction>("reactions.duplicate", { flowsheetId, id }),
  saveReactionSet: (flowsheetId: string, set: ReactionSet) =>
    call<ReactionSet>("reactionSets.save", { flowsheetId, set }),
  deleteReactionSet: (flowsheetId: string, id: string) =>
    call<ReactionsCatalog>("reactionSets.delete", { flowsheetId, id }),

  sensitivityCases: (flowsheetId: string) => call<SensitivityCase[]>("sensitivity.list", { flowsheetId }),
  saveSensitivity: (flowsheetId: string, study: SensitivityCase) =>
    call<SensitivityCase>("sensitivity.save", { flowsheetId, study }),
  deleteSensitivity: (flowsheetId: string, index: number) =>
    call<SensitivityCase[]>("sensitivity.delete", { flowsheetId, index }),
  runSensitivity: (flowsheetId: string, index: number, requestId?: string) =>
    call<SensitivityResult>("sensitivity.run", { flowsheetId, index }, requestId),
  optimizerMethods: () => call<ChoiceOption[]>("optimizer.methods"),
  optimizerCases: (flowsheetId: string) => call<OptimizerCase[]>("optimizer.list", { flowsheetId }),
  saveOptimizer: (flowsheetId: string, study: OptimizerCase) =>
    call<OptimizerCase>("optimizer.save", { flowsheetId, study }),
  deleteOptimizer: (flowsheetId: string, index: number) =>
    call<OptimizerCase[]>("optimizer.delete", { flowsheetId, index }),
  runOptimizer: (flowsheetId: string, index: number, requestId?: string) =>
    call<OptimizerResult>("optimizer.run", { flowsheetId, index }, requestId),

  scripts: (flowsheetId: string) =>
    call<{ scripts: Script[]; events: Record<string, string[]> }>("scripts.list", { flowsheetId }),
  saveScript: (flowsheetId: string, script: Script) => call<Script>("scripts.save", { flowsheetId, script }),
  deleteScript: (flowsheetId: string, id: string) =>
    call<{ scripts: Script[]; events: Record<string, string[]> }>("scripts.delete", { flowsheetId, id }),
  runScript: (flowsheetId: string, id: string) => call<ScriptRun>("scripts.run", { flowsheetId, id }),

  sheet: (flowsheetId: string, sheet?: string) => call<Sheet>("spreadsheet.get", { flowsheetId, sheet }),
  setCells: (flowsheetId: string, cells: { address: string; input: string }[], sheet?: string) =>
    call<Sheet>("spreadsheet.set", { flowsheetId, cells, sheet }),
  recalculateSheet: (flowsheetId: string, sheet?: string) =>
    call<Sheet>("spreadsheet.recalculate", { flowsheetId, sheet }),
  addSheet: (flowsheetId: string, name?: string) =>
    call<Sheet>("spreadsheet.addSheet", { flowsheetId, name }),
  renameSheet: (flowsheetId: string, sheet: string, name: string) =>
    call<Sheet>("spreadsheet.renameSheet", { flowsheetId, sheet, name }),
  deleteSheet: (flowsheetId: string, sheet: string) =>
    call<Sheet>("spreadsheet.deleteSheet", { flowsheetId, sheet }),

  scenarios: (flowsheetId: string) =>
    call<{ A: ScenarioSlot | null; B: ScenarioSlot | null }>("scenarios.get", { flowsheetId }),
  captureScenario: (flowsheetId: string, slot: "A" | "B", label?: string) =>
    call<{ A: ScenarioSlot | null; B: ScenarioSlot | null }>("scenarios.capture", {
      flowsheetId,
      slot,
      label,
    }),
  compareScenarios: (flowsheetId: string) => call<ScenarioComparison>("scenarios.compare", { flowsheetId }),

  dynamics: (flowsheetId: string) => call<DynamicsState>("dynamics.get", { flowsheetId }),
  setDynamicMode: (flowsheetId: string, enabled: boolean) =>
    call<DynamicsState>("dynamics.setMode", { flowsheetId, enabled }),
  dynamicsReadiness: (flowsheetId: string) => call<DynamicsIssue[]>("dynamics.readiness", { flowsheetId }),
  applyDynamicsFixes: (flowsheetId: string, keys?: string[], values?: Record<string, number | string>) =>
    call<{ applied: string[]; failed: string[]; remaining: DynamicsIssue[]; state: DynamicsState }>(
      "dynamics.applyFixes",
      { flowsheetId, keys, values },
    ),
  saveIntegrator: (flowsheetId: string, integrator: DynamicsIntegrator) =>
    call<DynamicsState>("dynamics.saveIntegrator", { flowsheetId, integrator }),
  saveSchedule: (flowsheetId: string, schedule: DynamicsSchedule) =>
    call<DynamicsState>("dynamics.saveSchedule", { flowsheetId, schedule }),
  saveEventSet: (flowsheetId: string, eventSet: DynamicsEventSet) =>
    call<DynamicsState>("dynamics.saveEventSet", { flowsheetId, eventSet }),
  deleteDynamics: (flowsheetId: string, kind: "integrator" | "schedule" | "eventSet", id: string) =>
    call<DynamicsState>("dynamics.delete", { flowsheetId, kind, id }),
  storeState: (flowsheetId: string, name: string) =>
    call<DynamicsState>("dynamics.storeState", { flowsheetId, name }),
  deleteState: (flowsheetId: string, name: string) =>
    call<DynamicsState>("dynamics.deleteState", { flowsheetId, name }),
  runDynamics: (flowsheetId: string, schedule?: string, requestId?: string) =>
    call<DynamicsRun>("dynamics.run", { flowsheetId, schedule }, requestId),
  dynamicsResults: (flowsheetId: string, integrator: string) =>
    call<DynamicsRun>("dynamics.results", { flowsheetId, integrator }),

  estimateCompound: (flowsheetId: string, compound: CompoundInput, units?: Record<string, string>) =>
    call<CompoundEstimate>("compounds.estimate", { flowsheetId, compound, units }),
  createCompound: (flowsheetId: string, compound: CompoundInput, units?: Record<string, string>) =>
    call<CompoundEstimate>("compounds.create", { flowsheetId, compound, units }),
  petroleumOptions: (flowsheetId: string) => call<PetroleumOptions>("petroleum.options", { flowsheetId }),
  characterizeBulk: (flowsheetId: string, assay: Record<string, unknown>) =>
    call<PetroleumResult>("petroleum.bulk", { flowsheetId, assay }),
  characterizeCurve: (flowsheetId: string, assay: Record<string, unknown>) =>
    call<PetroleumResult>("petroleum.curve", { flowsheetId, assay }),
  regressionModels: () =>
    call<{ models: RegressionModel[]; methods: string[]; objectives: string[]; dataTypes: string[] }>(
      "regression.models",
    ),
  runRegression: (flowsheetId: string, study: Record<string, unknown>, requestId?: string) =>
    call<RegressionResult>("regression.run", { flowsheetId, study }, requestId),
  applyRegression: (
    flowsheetId: string,
    packageId: string,
    comp1: string,
    comp2: string,
    parameters: Record<string, number | null>,
  ) =>
    call<{ applied: boolean; package: string }>("regression.apply", {
      flowsheetId,
      packageId,
      comp1,
      comp2,
      parameters,
    }),
};
