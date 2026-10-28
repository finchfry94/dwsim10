// Shapes of the engine bridge's answers (tools/DWSIM.Bridge). Quantities are SI unless a field
// says otherwise: K, Pa, kg/s, mol/s, kJ/kg, kg/m3, kW.

export type PortKind = "in" | "out" | "energy-in" | "energy-out";

export interface Port {
  /** `in-N`, `out-N` or `energy`, as the engine's connector lists index them. */
  id: string;
  kind: PortKind;
  name: string | null;
  /** Position within the node box, 0..1 from the left and from the top. */
  x: number;
  y: number;
  attached: boolean;
}

export type ObjectStatus =
  "Calculated" | "Calculating" | "ErrorCalculating" | "Inactive" | "Idle" | "NotCalculated" | "Modified";

export interface StreamState {
  temperature: number | null;
  pressure: number | null;
  massFlow: number | null;
  molarFlow: number | null;
  vaporFraction: number | null;
}

export interface GraphNode {
  id: string;
  tag: string;
  type: string;
  x: number;
  y: number;
  width: number;
  height: number;
  rotation: number;
  flippedH: boolean;
  flippedV: boolean;
  active: boolean;
  status: ObjectStatus;
  calculated: boolean;
  error: string | null;
  ports: Port[];
  stream: StreamState | null;
}

export interface GraphEdge {
  id: string;
  source: string;
  sourcePort: string;
  target: string;
  targetPort: string;
  kind: "material" | "energy";
}

export interface Graph {
  nodes: GraphNode[];
  edges: GraphEdge[];
}

export interface UnitSystem {
  name: string;
  temperature: string;
  pressure: string;
  massflow: string;
  molarflow: string;
  volumetricFlow: string;
  enthalpy: string;
  heatflow: string;
  density: string;
}

export interface FlowsheetSummary {
  id: string;
  name: string;
  path: string | null;
  compounds: string[];
  propertyPackages: { id: string; name: string; model: string }[];
  unitSystem: UnitSystem | null;
  objectCount: number;
}

export interface PropertyEntry {
  id: string;
  label: string;
  /** In the flowsheet's unit system. */
  units: string | null;
  value: number | string | boolean | null;
  writable: boolean;
}

export interface ObjectProperties {
  id: string;
  tag: string;
  type: string;
  properties: PropertyEntry[];
}

export interface CompoundAmount {
  name: string;
  moleFraction: number | null;
  massFraction: number | null;
  molarFlow: number | null;
  massFlow: number | null;
}

export interface PhaseState {
  index: number;
  name: string;
  moleFraction: number | null;
  massFraction: number | null;
  massFlow: number | null;
  molarFlow: number | null;
  volumetricFlow: number | null;
  enthalpy: number | null;
  entropy: number | null;
  density: number | null;
  molecularWeight: number | null;
  viscosity: number | null;
  heatCapacityCp: number | null;
  compressibilityFactor: number | null;
  compounds: CompoundAmount[];
}

export type StreamDetails =
  | { id: string; tag: string; kind: "energy"; power_kW: number | null }
  | {
      id: string;
      tag: string;
      kind: "material";
      calculated: boolean;
      temperature: number | null;
      pressure: number | null;
      massFlow: number | null;
      molarFlow: number | null;
      vaporFraction: number | null;
      phases: PhaseState[];
    };

export interface SolveResult {
  flowsheetId: string;
  ok: boolean;
  cancelled: boolean;
  durationMs: number;
  errors: string[];
}

export interface Finding {
  code: string;
  severity: "error" | "warning" | "info";
  object: string | null;
  message: string;
  fix: string | null;
}

export interface EngineInfo {
  bridgeVersion: string;
  engineVersion: string;
  methods: string[];
  objectTypes: string[];
}

export interface Notification {
  method: string;
  params: Record<string, unknown>;
}

export interface SampleFile {
  name: string;
  path: string;
}
