import {
  Background,
  BackgroundVariant,
  Controls,
  MarkerType,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  useReactFlow,
  type Connection,
  type Edge,
  type IsValidConnection,
  type OnNodesChange,
  applyNodeChanges,
} from "@xyflow/react";
import { useCallback, useEffect, useMemo, useState, type DragEvent } from "react";
import type { GraphNode, PortKind } from "../engine/types";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import { useApp } from "../state/store";
import { streamColors } from "./coloring";
import { ProcessNode, type ProcessFlowNode } from "./ProcessNode";
import { StreamLegend } from "./StreamLegend";
import { DRAG_TYPE } from "./catalog";

const nodeTypes = { process: ProcessNode };

/** Material goes to material ports, energy to energy ports, outputs to inputs. */
const compatible = (from: PortKind | undefined, to: PortKind | undefined) =>
  (from === "out" && to === "in") || (from === "energy-out" && to === "energy-in");

function Canvas() {
  const graph = useApp((s) => s.graph);
  const flowsheet = useApp((s) => s.flowsheet);
  const colorMode = useApp((s) => s.colorMode);
  const showStreamTags = useApp((s) => s.showStreamTags);
  const selected = useApp((s) => s.selected);
  const { select, setSelected, moveMany, connect, deleteSelected, addObject, log } = useApp.getState();
  const flow = useReactFlow();

  const colors = useMemo(() => streamColors(graph?.nodes ?? [], colorMode), [graph, colorMode]);
  const byId = useMemo(() => new Map((graph?.nodes ?? []).map((n) => [n.id, n])), [graph]);
  const selectedSet = useMemo(() => new Set(selected), [selected]);

  const toFlowNode = useCallback(
    (o: GraphNode): ProcessFlowNode => ({
      id: o.id,
      type: "process",
      position: { x: o.x, y: o.y },
      data: {
        object: o,
        color: colors.byNode.get(o.id),
        showTag: showStreamTags,
        units: flowsheet?.unitSystem ?? undefined,
      },
      selected: selectedSet.has(o.id),
      width: o.width,
      height: o.height,
    }),
    [colors, showStreamTags, flowsheet, selectedSet],
  );

  const [nodes, setNodes] = useState<ProcessFlowNode[]>([]);
  useEffect(() => {
    setNodes((graph?.nodes ?? []).map(toFlowNode));
  }, [graph, toFlowNode]);

  const edges = useMemo<Edge[]>(
    () =>
      (graph?.edges ?? []).map((e) => {
        const target = byId.get(e.target);
        const intoStream = target?.type === "MaterialStream" || target?.type === "EnergyStream";
        const stream = [byId.get(e.source), target].find((n) => n?.type === "MaterialStream");
        const color =
          e.kind === "energy"
            ? "var(--stream-energy)"
            : stream
              ? (colors.byNode.get(stream.id) ??
                (stream.calculated ? "var(--stream-material)" : "var(--stream-pending)"))
              : "var(--stream-material)";
        return {
          id: e.id,
          source: e.source,
          sourceHandle: e.sourcePort,
          target: e.target,
          targetHandle: e.targetPort,
          type: "smoothstep",
          pathOptions: { borderRadius: 8, offset: 16 },
          className: `edge-${e.kind}`,
          style: { stroke: color },
          // A stream glyph is itself an arrow; only the end that enters a unit needs a head.
          markerEnd: intoStream ? undefined : { type: MarkerType.ArrowClosed, color, width: 14, height: 14 },
        } as Edge;
      }),
    [graph, byId, colors],
  );

  // Bring an object into view when asked (the command palette, a diagnostics finding).
  const focus = useApp((s) => s.focus);
  useEffect(() => {
    if (focus) void flow.fitView({ nodes: [{ id: focus.id }], padding: 1.2, duration: 250, maxZoom: 1.5 });
  }, [focus, flow]);

  // Fit the view when another flowsheet is opened, not on every refresh.
  useEffect(() => {
    // An empty flowsheet has nothing to fit, and React Flow would hold the request until the
    // first object is dropped, then move the view under the user's next drop.
    if (flowsheet && (useApp.getState().graph?.nodes.length ?? 0) > 0)
      requestAnimationFrame(() => void flow.fitView({ padding: 0.12, duration: 200, maxZoom: 1.25 }));
  }, [flowsheet?.id, flow]);

  const onNodesChange: OnNodesChange<ProcessFlowNode> = useCallback(
    (changes) => setNodes((current) => applyNodeChanges(changes, current)),
    [],
  );

  const portKind = (nodeId: string | null, portId: string | null | undefined) =>
    byId.get(nodeId ?? "")?.ports.find((p) => p.id === portId)?.kind;

  const isValidConnection: IsValidConnection = (c) =>
    c.source !== c.target &&
    compatible(portKind(c.source, c.sourceHandle), portKind(c.target, c.targetHandle));

  const onConnect = (c: Connection) => {
    if (c.sourceHandle && c.targetHandle) void connect(c.source, c.sourceHandle, c.target, c.targetHandle);
  };

  /** Deleting objects removes their connections too; deleting a connection alone disconnects it. */
  const onDelete = ({ nodes: gone, edges: cut }: { nodes: ProcessFlowNode[]; edges: Edge[] }) => {
    if (gone.length > 0) {
      setSelected(gone.map((n) => n.id));
      void deleteSelected();
      return;
    }
    const id = flowsheet?.id;
    if (!id) return;
    void (async () => {
      for (const e of cut) {
        try {
          await engine.disconnect(id, e.source, e.target);
        } catch (err) {
          log("error", `Disconnect: ${EngineError.from(err).message}`);
        }
      }
      await useApp.getState().afterEdit();
    })();
  };

  // Stable handlers: React Flow re-syncs a changed handler into its store and fires the selection
  // listener again, so an inline one loops.
  const onSelectionChange = useCallback(
    ({ nodes: chosen }: { nodes: ProcessFlowNode[] }) => setSelected(chosen.map((n) => n.id)),
    [setSelected],
  );
  const onNodeDragStop = useCallback(
    (_: unknown, __: unknown, dragged: ProcessFlowNode[]) =>
      void moveMany(
        dragged.map((n) => ({ id: n.id, x: Math.round(n.position.x), y: Math.round(n.position.y) })),
      ),
    [moveMany],
  );
  const onPaneClick = useCallback(() => select(null), [select]);

  const onDrop = (event: DragEvent) => {
    event.preventDefault();
    const type = event.dataTransfer.getData(DRAG_TYPE);
    if (!type) return;
    const p = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
    // The engine places an object by its top-left corner; most are about 40 units across.
    void addObject(type, Math.round(p.x - 20), Math.round(p.y - 20));
  };

  if (!flowsheet) return null;

  return (
    <div
      className="h-full w-full"
      onDragOver={(e) => e.preventDefault()}
      onDrop={onDrop}
      data-testid="canvas"
    >
      <ReactFlow<ProcessFlowNode, Edge>
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        onNodesChange={onNodesChange}
        onSelectionChange={onSelectionChange}
        onPaneClick={onPaneClick}
        onNodeDragStop={onNodeDragStop}
        onConnect={onConnect}
        isValidConnection={isValidConnection}
        onDelete={onDelete}
        deleteKeyCode={["Delete", "Backspace"]}
        multiSelectionKeyCode={["Shift", "Meta", "Control"]}
        selectionKeyCode="Shift"
        selectionOnDrag={false}
        snapToGrid
        snapGrid={[5, 5]}
        minZoom={0.05}
        maxZoom={4}
        fitViewOptions={{ maxZoom: 1.25 }}
        proOptions={{ hideAttribution: true }}
        connectionRadius={24}
      >
        <Background variant={BackgroundVariant.Dots} gap={20} size={1} color="var(--grid-dot)" />
        <MiniMap
          pannable
          zoomable
          style={{ width: 150, height: 100 }}
          nodeColor={(n) => (n.data as { color?: string }).color ?? "var(--minimap-node)"}
        />
        <Controls showInteractive={false} />
      </ReactFlow>
      <StreamLegend legend={colors.legend} units={flowsheet.unitSystem ?? undefined} />
    </div>
  );
}

export function FlowsheetCanvas() {
  return (
    <ReactFlowProvider>
      <Canvas />
    </ReactFlowProvider>
  );
}
