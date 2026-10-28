// The objects the palette offers, by DWSIM ObjectType name, grouped as the old object palette
// grouped them. Labels are what the user reads; types are what the engine is asked to add.

export interface CatalogEntry {
  type: string;
  label: string;
}

export interface CatalogGroup {
  name: string;
  items: CatalogEntry[];
}

export const CATALOG: CatalogGroup[] = [
  {
    name: "Streams",
    items: [
      { type: "MaterialStream", label: "Material stream" },
      { type: "EnergyStream", label: "Energy stream" },
    ],
  },
  {
    name: "Mixing & splitting",
    items: [
      { type: "Mixer", label: "Mixer" },
      { type: "Splitter", label: "Splitter" },
    ],
  },
  {
    name: "Heat transfer",
    items: [
      { type: "Heater", label: "Heater" },
      { type: "Cooler", label: "Cooler" },
      { type: "HeatExchanger", label: "Heat exchanger" },
      { type: "AirCooler2", label: "Air cooler" },
    ],
  },
  {
    name: "Pressure change",
    items: [
      { type: "Pump", label: "Pump" },
      { type: "Compressor", label: "Compressor" },
      { type: "Expander", label: "Expander" },
      { type: "Valve", label: "Valve" },
      { type: "Pipe", label: "Pipe segment" },
      { type: "OrificePlate", label: "Orifice plate" },
    ],
  },
  {
    name: "Separation",
    items: [
      { type: "Vessel", label: "Gas–liquid separator" },
      { type: "Tank", label: "Tank" },
      { type: "ComponentSeparator", label: "Component separator" },
      { type: "SolidSeparator", label: "Solids separator" },
      { type: "Filter", label: "Filter" },
    ],
  },
  {
    name: "Columns",
    items: [
      { type: "ShortcutColumn", label: "Shortcut column" },
      { type: "DistillationColumn", label: "Distillation column" },
      { type: "AbsorptionColumn", label: "Absorption column" },
      { type: "RefluxedAbsorber", label: "Refluxed absorber" },
      { type: "ReboiledAbsorber", label: "Reboiled absorber" },
    ],
  },
  {
    name: "Reactors",
    items: [
      { type: "RCT_Conversion", label: "Conversion reactor" },
      { type: "RCT_Equilibrium", label: "Equilibrium reactor" },
      { type: "RCT_Gibbs", label: "Gibbs reactor" },
      { type: "RCT_CSTR", label: "CSTR" },
      { type: "RCT_PFR", label: "PFR" },
    ],
  },
  {
    name: "Logical",
    items: [
      { type: "OT_Recycle", label: "Recycle" },
      { type: "OT_EnergyRecycle", label: "Energy recycle" },
      { type: "OT_Adjust", label: "Adjust" },
      { type: "OT_Spec", label: "Specification" },
    ],
  },
];

const LABELS = new Map(CATALOG.flatMap((g) => g.items.map((i) => [i.type, i.label] as const)));

export function typeLabel(type: string): string {
  return LABELS.get(type) ?? type.replace(/^(RCT_|OT_|GO_)/, "").replace(/([a-z])([A-Z])/g, "$1 $2");
}

export const isStreamType = (type: string) => type === "MaterialStream" || type === "EnergyStream";

/** The drag-and-drop payload type for an object dragged from the palette. */
export const DRAG_TYPE = "application/x-dwsim-object";
