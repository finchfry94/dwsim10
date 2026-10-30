import { Box, Command as CommandIcon, Search } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { fuzzyScore, keyLabel, type Command } from "../lib/commands";
import { selectNodes, useApp } from "../state/store";

interface Item {
  key: string;
  label: string;
  hint?: string;
  kind: "command" | "object";
  run: () => void;
}

/**
 * Ctrl+K: type to find a command or an object on the flowsheet by its tag. Enter runs the
 * highlighted item; Escape closes.
 */
export function CommandPalette({ commands }: { commands: Command[] }) {
  const open = useApp((s) => s.palette);
  if (!open) return null;
  return <Palette commands={commands} />;
}

function Palette({ commands }: { commands: Command[] }) {
  const nodes = useApp(selectNodes);
  const close = () => useApp.getState().openPalette(false);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const list = useRef<HTMLUListElement>(null);

  const items = useMemo(() => {
    const all: Item[] = [
      ...commands
        .filter((c) => c.enabled?.() ?? true)
        .map<Item>((c) => ({
          key: c.id,
          label: c.label,
          hint: c.keys?.[0] ? keyLabel(c.keys[0]) : c.group,
          kind: "command",
          run: () => void c.run(),
        })),
      ...nodes.map<Item>((n) => ({
        key: `object:${n.id}`,
        label: n.tag,
        hint: n.type.replace(/([a-z])([A-Z])/g, "$1 $2"),
        kind: "object",
        run: () => useApp.getState().focusObject(n.id),
      })),
    ];
    if (!query.trim()) return all.filter((i) => i.kind === "command").slice(0, 50);
    return all
      .map((i) => ({ i, score: fuzzyScore(i.label, query) }))
      .filter((x): x is { i: Item; score: number } => x.score !== null)
      .sort((a, b) => b.score - a.score)
      .slice(0, 50)
      .map((x) => x.i);
  }, [commands, nodes, query]);

  useEffect(() => setActive(0), [query]);
  useEffect(() => {
    list.current?.querySelector(`[data-index="${active}"]`)?.scrollIntoView({ block: "nearest" });
  }, [active]);

  const choose = (item: Item | undefined) => {
    if (!item) return;
    close();
    item.run();
  };

  return (
    <div className="dialog-backdrop" onMouseDown={close}>
      <div
        className="cmdk"
        role="dialog"
        aria-modal="true"
        aria-label="Command palette"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="cmdk-input">
          <Search size={16} className="muted" />
          <input
            autoFocus
            value={query}
            placeholder="Type a command or an object's name…"
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "ArrowDown") {
                e.preventDefault();
                setActive((a) => Math.min(a + 1, items.length - 1));
              } else if (e.key === "ArrowUp") {
                e.preventDefault();
                setActive((a) => Math.max(a - 1, 0));
              } else if (e.key === "Enter") {
                e.preventDefault();
                choose(items[active]);
              } else if (e.key === "Escape") {
                e.preventDefault();
                e.stopPropagation();
                close();
              }
            }}
            role="combobox"
            aria-expanded="true"
            aria-controls="cmdk-list"
            aria-activedescendant={items[active] ? `palette-${active}` : undefined}
            aria-label="Search commands and objects"
          />
        </div>
        <ul className="cmdk-list" id="cmdk-list" role="listbox" ref={list}>
          {items.length === 0 && <li className="cmdk-empty muted">Nothing matches.</li>}
          {items.map((item, i) => (
            <li
              key={item.key}
              id={`palette-${i}`}
              data-index={i}
              role="option"
              aria-selected={i === active}
              className={`cmdk-item ${i === active ? "is-active" : ""}`}
              onMouseEnter={() => setActive(i)}
              onClick={() => choose(item)}
            >
              {item.kind === "object" ? (
                <Box size={14} className="muted" />
              ) : (
                <CommandIcon size={14} className="muted" />
              )}
              <span className="cmdk-label">{item.label}</span>
              {item.hint && <kbd className="cmdk-hint">{item.hint}</kbd>}
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

/** "?": every command with a shortcut, by group. */
export function ShortcutSheet({ commands }: { commands: Command[] }) {
  const open = useApp((s) => s.shortcuts);
  if (!open) return null;
  const close = () => useApp.getState().openShortcuts(false);
  const groups = new Map<string, Command[]>();
  for (const c of commands.filter((c) => c.keys?.length))
    groups.set(c.group, [...(groups.get(c.group) ?? []), c]);
  return (
    <div className="dialog-backdrop" onMouseDown={close}>
      <div
        className="dialog shortcut-sheet"
        role="dialog"
        aria-modal="true"
        aria-label="Keyboard shortcuts"
        onMouseDown={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.key === "Escape" && close()}
      >
        <div className="panel-header">
          <h2 className="section-title">Keyboard shortcuts</h2>
          <button className="btn btn-ghost" onClick={close} autoFocus>
            Close
          </button>
        </div>
        <div className="shortcut-groups">
          {[...groups.entries()].map(([group, list]) => (
            <section key={group}>
              <h3 className="field-label">{group}</h3>
              <dl className="shortcut-list">
                {list.map((c) => (
                  <div key={c.id} className="shortcut-row">
                    <dt>{c.label}</dt>
                    <dd>
                      {c.keys!.map((k) => (
                        <kbd key={k}>{keyLabel(k)}</kbd>
                      ))}
                    </dd>
                  </div>
                ))}
              </dl>
            </section>
          ))}
        </div>
        <p className="muted text-[11px]">
          Shift-click adds an object to the selection; Shift-drag on the canvas selects a box. Connecting two
          unit operations puts a stream between them.
        </p>
      </div>
    </div>
  );
}
