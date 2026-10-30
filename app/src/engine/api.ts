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
  check: (flowsheetId: string) => call<Finding[]>("solve.check", { flowsheetId }),
};
