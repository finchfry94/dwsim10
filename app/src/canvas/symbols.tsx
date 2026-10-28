// Process symbols as line art, after ISO 10628: thin strokes in the current colour, no fill but
// the node surface, so one set serves light and dark themes. Each symbol is drawn in a 100×100
// box that the node stretches to the engine's width and height, the same box the engine's ports
// are placed on; strokes do not stretch with it.

import type { JSX } from "react";

const S = { vectorEffect: "non-scaling-stroke" } as const;

const column = (
  <>
    <rect x="30" y="2" width="40" height="96" rx="20" {...S} />
    {[22, 34, 46, 58, 70, 82].map((y) => (
      <line key={y} x1="30" x2="70" y1={y} y2={y} strokeDasharray="4 3" {...S} />
    ))}
  </>
);

const vessel = (extra?: JSX.Element) => (
  <>
    <rect x="22" y="2" width="56" height="96" rx="26" {...S} />
    {extra}
  </>
);

const SYMBOLS: Record<string, JSX.Element> = {
  MaterialStream: (
    <>
      <line x1="2" y1="50" x2="78" y2="50" strokeWidth={2} {...S} />
      <path d="M70 32 L98 50 L70 68 Z" className="fill-current" {...S} />
    </>
  ),
  EnergyStream: (
    <>
      <path d="M2 50 L30 50 L42 30 L58 70 L70 50 L78 50" {...S} />
      <path d="M70 34 L98 50 L70 66 Z" className="fill-current" {...S} />
    </>
  ),
  Mixer: <path d="M4 4 L96 50 L4 96 Z" {...S} />,
  Splitter: <path d="M96 4 L4 50 L96 96 Z" {...S} />,
  Heater: (
    <>
      <circle cx="50" cy="50" r="46" {...S} />
      <path d="M14 70 L32 30 L50 70 L68 30 L86 70" {...S} />
    </>
  ),
  Cooler: (
    <>
      <circle cx="50" cy="50" r="46" {...S} />
      <path d="M14 30 L32 70 L50 30 L68 70 L86 30" {...S} />
    </>
  ),
  HeatExchanger: (
    <>
      <circle cx="50" cy="50" r="46" {...S} />
      <path d="M4 50 L30 50 L42 26 L58 74 L70 50 L96 50" {...S} />
    </>
  ),
  AirCooler2: (
    <>
      <rect x="4" y="30" width="92" height="40" {...S} />
      <path d="M30 30 L40 6 L60 6 L70 30 M50 6 L50 30" {...S} />
    </>
  ),
  Pump: (
    <>
      <circle cx="46" cy="54" r="38" {...S} />
      <path d="M46 16 L96 16 L96 34 L80 34" {...S} />
      <path d="M22 84 L12 98 L80 98 L70 84" {...S} />
    </>
  ),
  Compressor: <path d="M6 6 L94 30 L94 70 L6 94 Z" {...S} />,
  Expander: <path d="M6 30 L94 6 L94 94 L6 70 Z" {...S} />,
  Valve: <path d="M4 20 L96 80 L96 20 L4 80 Z" {...S} />,
  OrificePlate: (
    <>
      <line x1="4" y1="50" x2="96" y2="50" {...S} />
      <line x1="50" y1="6" x2="50" y2="40" strokeWidth={3} {...S} />
      <line x1="50" y1="60" x2="50" y2="94" strokeWidth={3} {...S} />
    </>
  ),
  Pipe: (
    <>
      <rect x="2" y="34" width="96" height="32" rx="4" {...S} />
      <line x1="20" y1="34" x2="20" y2="66" {...S} />
      <line x1="80" y1="34" x2="80" y2="66" {...S} />
    </>
  ),
  Vessel: vessel(<line x1="22" y1="60" x2="78" y2="60" strokeDasharray="5 4" {...S} />),
  TPVessel: vessel(<line x1="22" y1="60" x2="78" y2="60" strokeDasharray="5 4" {...S} />),
  Tank: (
    <>
      <path d="M8 24 Q50 0 92 24 L92 98 L8 98 Z" {...S} />
      <line x1="8" y1="56" x2="92" y2="56" strokeDasharray="5 4" {...S} />
    </>
  ),
  ComponentSeparator: (
    <>
      <rect x="10" y="4" width="80" height="92" rx="6" {...S} />
      <line x1="10" y1="96" x2="90" y2="4" {...S} />
    </>
  ),
  SolidSeparator: (
    <>
      <path d="M10 4 L90 4 L90 60 L50 96 L10 60 Z" {...S} />
      <line x1="10" y1="40" x2="90" y2="40" strokeDasharray="5 4" {...S} />
    </>
  ),
  Filter: (
    <>
      <rect x="6" y="20" width="88" height="60" rx="4" {...S} />
      <line x1="50" y1="20" x2="50" y2="80" strokeDasharray="3 3" {...S} />
    </>
  ),
  ShortcutColumn: column,
  DistillationColumn: column,
  AbsorptionColumn: column,
  RefluxedAbsorber: column,
  ReboiledAbsorber: column,
  RCT_Conversion: vessel(
    <text x="50" y="60" textAnchor="middle" className="symbol-text">
      C
    </text>,
  ),
  RCT_Equilibrium: vessel(
    <text x="50" y="60" textAnchor="middle" className="symbol-text">
      E
    </text>,
  ),
  RCT_Gibbs: vessel(
    <text x="50" y="60" textAnchor="middle" className="symbol-text">
      G
    </text>,
  ),
  RCT_GibbsReaktoro: vessel(
    <text x="50" y="60" textAnchor="middle" className="symbol-text">
      G
    </text>,
  ),
  RCT_CSTR: (
    <>
      <path d="M10 14 L10 80 Q50 104 90 80 L90 14 Z" {...S} />
      <path d="M50 2 L50 70 M34 64 L66 76 M34 76 L66 64" {...S} />
    </>
  ),
  RCT_PFR: (
    <>
      <rect x="2" y="30" width="96" height="40" rx="20" {...S} />
      <path d="M20 30 L36 70 M36 30 L52 70 M52 30 L68 70 M68 30 L84 70" strokeDasharray="3 3" {...S} />
    </>
  ),
  OT_Recycle: (
    <>
      <path d="M80 38 A32 32 0 1 0 82 62" {...S} />
      <path d="M70 28 L84 40 L94 22" {...S} />
    </>
  ),
  OT_EnergyRecycle: (
    <>
      <path d="M80 38 A32 32 0 1 0 82 62" strokeDasharray="6 4" {...S} />
      <path d="M70 28 L84 40 L94 22" {...S} />
    </>
  ),
  OT_Adjust: (
    <>
      <path d="M50 4 L96 50 L50 96 L4 50 Z" {...S} />
      <text x="50" y="62" textAnchor="middle" className="symbol-text">
        A
      </text>
    </>
  ),
  OT_Spec: (
    <>
      <path d="M50 4 L96 50 L50 96 L4 50 Z" {...S} />
      <text x="50" y="62" textAnchor="middle" className="symbol-text">
        S
      </text>
    </>
  ),
};

export const hasSymbol = (type: string) => type in SYMBOLS;

function abbreviation(type: string): string {
  const words = type.replace(/^(RCT_|OT_|GO_)/, "").match(/[A-Z][a-z]*|\d+/g) ?? [type];
  return words
    .slice(0, 3)
    .map((w) => w[0])
    .join("");
}

export function ProcessSymbol({ type, className }: { type: string; className?: string }) {
  const body = SYMBOLS[type] ?? (
    <>
      <rect x="4" y="4" width="92" height="92" rx="10" {...S} />
      <text x="50" y="60" textAnchor="middle" className="symbol-text">
        {abbreviation(type)}
      </text>
    </>
  );
  return (
    <svg
      viewBox="0 0 100 100"
      preserveAspectRatio="none"
      className={className}
      fill="none"
      stroke="currentColor"
      strokeWidth={1.5}
      strokeLinejoin="round"
      aria-hidden
    >
      {body}
    </svg>
  );
}
