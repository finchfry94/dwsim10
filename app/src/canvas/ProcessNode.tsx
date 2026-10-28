import { Handle, Position, useStore, type Node, type NodeProps } from "@xyflow/react";
import { memo } from "react";
import type { GraphNode, Port } from "../engine/types";
import { formatQuantity } from "../lib/units";
import { ProcessSymbol } from "./symbols";

export interface ProcessNodeData extends Record<string, unknown> {
  object: GraphNode;
  /** Scale colour for a stream, from the colour mode; undefined uses the status colour. */
  color?: string;
  showTag: boolean;
  units?: { temperature?: string; pressure?: string; massflow?: string };
}

export type ProcessFlowNode = Node<ProcessNodeData, "process">;

/** Which side of the box a port sits on, from its relative position. */
function side(p: Port): Position {
  const d = [
    [p.x, Position.Left],
    [1 - p.x, Position.Right],
    [p.y, Position.Top],
    [1 - p.y, Position.Bottom],
  ] as const;
  return d.reduce((a, b) => (b[0] < a[0] ? b : a))[1];
}

function handleStyle(p: Port, s: Position) {
  const along =
    s === Position.Left || s === Position.Right ? { top: `${p.y * 100}%` } : { left: `${p.x * 100}%` };
  return along;
}

const statusClass: Record<string, string> = {
  Calculated: "status-ok",
  Calculating: "status-busy",
  ErrorCalculating: "status-error",
  Inactive: "status-inactive",
};

const zoomSelector = (s: { transform: [number, number, number] }) => s.transform[2];

function ProcessNodeView({ data, selected }: NodeProps<ProcessFlowNode>) {
  const { object: o, color, showTag, units } = data;
  const zoom = useStore(zoomSelector);
  const isStream = o.type === "MaterialStream" || o.type === "EnergyStream";
  const status = o.error ? "status-error" : (statusClass[o.status] ?? "status-pending");
  const transform = [
    o.flippedH ? "scaleX(-1)" : "",
    o.flippedV ? "scaleY(-1)" : "",
    o.rotation ? `rotate(${o.rotation}deg)` : "",
  ]
    .filter(Boolean)
    .join(" ");

  // Every stream's tag when the toolbar asks for them and the view is close enough to read them;
  // otherwise only the hovered or selected stream's (the rest are hidden by CSS until hovered).
  const tag =
    o.type === "MaterialStream" && o.calculated && o.stream ? (
      <div
        className={`stream-tag ${selected || (showTag && zoom > 0.9) ? "" : "on-hover"}`}
        data-testid="stream-tag"
      >
        <span>{formatQuantity("temperature", o.stream.temperature, units?.temperature)}</span>
        <span>{formatQuantity("pressure", o.stream.pressure, units?.pressure)}</span>
        <span>{formatQuantity("massFlow", o.stream.massFlow, units?.massflow)}</span>
      </div>
    ) : null;

  return (
    <div
      className={`process-node ${isStream ? "is-stream" : "is-unit"} ${o.type === "EnergyStream" ? "is-energy" : ""} ${status} ${selected ? "is-selected" : ""} ${o.active ? "" : "is-inactive"}`}
      style={{ width: o.width, height: o.height, color: color }}
      title={o.error ?? undefined}
      data-testid={`node-${o.tag}`}
    >
      <div className="symbol-wrap" style={transform ? { transform } : undefined}>
        <ProcessSymbol type={o.type} className="symbol" />
      </div>
      {!isStream && <span className="status-dot" aria-label={o.status} />}
      {o.ports.map((p) => {
        const s = side(p);
        const isTarget = p.kind === "in" || p.kind === "energy-in";
        return (
          <Handle
            key={p.id}
            id={p.id}
            type={isTarget ? "target" : "source"}
            position={s}
            style={handleStyle(p, s)}
            className={`port port-${p.kind} ${p.attached ? "is-attached" : ""}`}
            title={p.name ?? p.id}
          />
        );
      })}
      <div className="node-label">{o.tag}</div>
      {tag}
    </div>
  );
}

export const ProcessNode = memo(ProcessNodeView);
