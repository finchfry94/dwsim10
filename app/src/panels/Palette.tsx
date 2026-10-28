import { useState } from "react";
import { CATALOG, DRAG_TYPE } from "../canvas/catalog";
import { ProcessSymbol } from "../canvas/symbols";

/** Objects to drag onto the canvas, filterable by name. */
export function Palette() {
  const [filter, setFilter] = useState("");
  const f = filter.trim().toLowerCase();
  return (
    <aside className="panel palette" aria-label="Object palette">
      <input
        className="input"
        placeholder="Find an object…"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
      />
      <div className="palette-scroll">
        {CATALOG.map((group) => {
          const items = group.items.filter((i) => !f || i.label.toLowerCase().includes(f));
          if (items.length === 0) return null;
          return (
            <section key={group.name}>
              <h3 className="section-title">{group.name}</h3>
              <div className="palette-grid">
                {items.map((item) => (
                  <div
                    key={item.type}
                    className="palette-item"
                    draggable
                    onDragStart={(e) => {
                      e.dataTransfer.setData(DRAG_TYPE, item.type);
                      e.dataTransfer.effectAllowed = "copy";
                    }}
                    title={`Drag onto the flowsheet to add a ${item.label.toLowerCase()}`}
                    data-testid={`palette-${item.type}`}
                  >
                    <ProcessSymbol type={item.type} className="palette-symbol" />
                    <span>{item.label}</span>
                  </div>
                ))}
              </div>
            </section>
          );
        })}
      </div>
    </aside>
  );
}
