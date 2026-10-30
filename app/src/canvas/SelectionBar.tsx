import {
  AlignCenterHorizontal,
  AlignCenterVertical,
  AlignEndHorizontal,
  AlignEndVertical,
  AlignHorizontalSpaceAround,
  AlignStartHorizontal,
  AlignStartVertical,
  AlignVerticalSpaceAround,
  Copy,
  CopyPlus,
  FlipHorizontal2,
  FlipVertical2,
  RotateCw,
  Trash2,
} from "lucide-react";
import type { ReactNode } from "react";
import { useApp, type Alignment } from "../state/store";

/** Actions on the selected objects, floating over the canvas while something is selected. */
export function SelectionBar() {
  const count = useApp((s) => s.selected.length);
  const busy = useApp((s) => s.busy);
  if (count === 0) return null;
  const app = useApp.getState();
  const align = (how: Alignment) => () => void app.align(how);
  return (
    <div className="selection-bar" role="toolbar" aria-label="Selection" data-testid="selection-bar">
      <span className="selection-count">{count === 1 ? "1 object" : `${count} objects`}</span>
      <Tool label="Rotate 90° (R)" onClick={() => void app.transform({ rotate: 90 })} disabled={busy}>
        <RotateCw size={15} />
      </Tool>
      <Tool label="Flip horizontally (H)" onClick={() => void app.transform({ flipH: true })} disabled={busy}>
        <FlipHorizontal2 size={15} />
      </Tool>
      <Tool label="Flip vertically (V)" onClick={() => void app.transform({ flipV: true })} disabled={busy}>
        <FlipVertical2 size={15} />
      </Tool>
      {count >= 2 && (
        <>
          <span className="toolbar-sep" />
          <Tool label="Align left edges" onClick={align("left")} disabled={busy}>
            <AlignStartVertical size={15} />
          </Tool>
          <Tool label="Align centres horizontally" onClick={align("center")} disabled={busy}>
            <AlignCenterVertical size={15} />
          </Tool>
          <Tool label="Align right edges" onClick={align("right")} disabled={busy}>
            <AlignEndVertical size={15} />
          </Tool>
          <Tool label="Align top edges" onClick={align("top")} disabled={busy}>
            <AlignStartHorizontal size={15} />
          </Tool>
          <Tool label="Align centres vertically" onClick={align("middle")} disabled={busy}>
            <AlignCenterHorizontal size={15} />
          </Tool>
          <Tool label="Align bottom edges" onClick={align("bottom")} disabled={busy}>
            <AlignEndHorizontal size={15} />
          </Tool>
        </>
      )}
      {count >= 3 && (
        <>
          <Tool label="Spread evenly across" onClick={align("spreadX")} disabled={busy}>
            <AlignHorizontalSpaceAround size={15} />
          </Tool>
          <Tool label="Spread evenly down" onClick={align("spreadY")} disabled={busy}>
            <AlignVerticalSpaceAround size={15} />
          </Tool>
        </>
      )}
      <span className="toolbar-sep" />
      <Tool label="Copy (Ctrl+C)" onClick={() => void app.copy()} disabled={busy}>
        <Copy size={15} />
      </Tool>
      <Tool label="Duplicate (Ctrl+D)" onClick={() => void app.duplicate()} disabled={busy}>
        <CopyPlus size={15} />
      </Tool>
      <Tool label="Delete (Del)" onClick={() => void app.deleteSelected()} disabled={busy}>
        <Trash2 size={15} />
      </Tool>
    </div>
  );
}

function Tool({
  label,
  onClick,
  disabled,
  children,
}: {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  children: ReactNode;
}) {
  return (
    <button
      className="btn btn-icon btn-ghost"
      onClick={onClick}
      disabled={disabled}
      title={label}
      aria-label={label}
    >
      {children}
    </button>
  );
}
