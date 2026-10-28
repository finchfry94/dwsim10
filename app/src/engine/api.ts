import { transport } from "./transport";
import type {
  EngineInfo,
  Finding,
  FlowsheetSummary,
  Graph,
  GraphNode,
  ObjectProperties,
  PropertyEntry,
  SolveResult,
  StreamDetails,
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

  solve: (flowsheetId: string) => call<SolveResult>("solve.run", { flowsheetId }),
  check: (flowsheetId: string) => call<Finding[]>("solve.check", { flowsheetId }),
};
