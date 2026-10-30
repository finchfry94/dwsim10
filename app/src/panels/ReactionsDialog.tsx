import { Copy, Plus, Scale, Trash2, X } from "lucide-react";
import { useEffect, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type {
  Reaction,
  ReactionSet,
  ReactionsCatalog,
  ReactionType,
  StoichiometryRow,
} from "../engine/types";
import { balanceBase, blankReaction, preview, REACTION_TYPES, typeLabel } from "../lib/reactions";
import { formatNumber } from "../lib/units";
import { useEscape } from "../lib/useEscape";
import { useApp } from "../state/store";

const closeReactions = () => useApp.getState().openReactions(false);

type Editing = { kind: "reaction"; reaction: Reaction } | { kind: "set"; set: ReactionSet } | null;

/** The flowsheet's reactions and reaction sets, as the classic reaction manager lists them. */
export function ReactionsDialog() {
  const open = useApp((s) => s.reactions);
  const flowsheet = useApp((s) => s.flowsheet);
  useEscape(open, closeReactions);
  const [catalog, setCatalog] = useState<ReactionsCatalog | null>(null);
  const [editing, setEditing] = useState<Editing>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = async () => {
    if (!flowsheet) return;
    try {
      setCatalog(await engine.reactions(flowsheet.id));
    } catch (e) {
      setError(EngineError.from(e).message);
    }
  };

  useEffect(() => {
    if (!open) return;
    setEditing(null);
    setError(null);
    void reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, flowsheet]);

  if (!open || !flowsheet) return null;
  const id = flowsheet.id;

  /** Runs a change, then re-reads the catalog and the flowsheet (a reactor may re-solve). */
  const change = async (action: () => Promise<unknown>) => {
    setError(null);
    try {
      await action();
      await reload();
      await useApp.getState().afterEdit();
      return true;
    } catch (e) {
      setError(EngineError.from(e).message);
      return false;
    }
  };

  const editReaction = async (rid: string) => {
    try {
      setEditing({ kind: "reaction", reaction: await engine.reaction(id, rid) });
      setError(null);
    } catch (e) {
      setError(EngineError.from(e).message);
    }
  };

  return (
    <div className="dialog-backdrop" onMouseDown={closeReactions}>
      <div
        className="dialog reactions-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Reactions"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="panel-header">
          <h2 className="dialog-title">Reactions</h2>
          <button className="btn btn-icon btn-ghost" onClick={closeReactions} aria-label="Close">
            <X size={15} />
          </button>
        </div>
        {error && <p className="callout callout-error">{error}</p>}
        {!catalog ? (
          <p className="muted">Loading…</p>
        ) : (
          <div className="tools-layout">
            <nav className="tools-list reactions-list" aria-label="Reactions and sets">
              <div className="tools-group">Reactions</div>
              {catalog.reactions.length === 0 && <p className="muted text-[12px] px-2">None yet.</p>}
              {catalog.reactions.map((r) => (
                <button
                  key={r.id}
                  className="tools-item"
                  aria-current={editing?.kind === "reaction" && editing.reaction.id === r.id}
                  onClick={() => void editReaction(r.id)}
                  title={r.equation}
                >
                  {r.name}
                  <span className="muted text-[11px] block">{typeLabel(r.type)}</span>
                </button>
              ))}
              <label className="field mt-2 px-1">
                <span className="field-label">Add a reaction</span>
                <select
                  className="select"
                  value=""
                  onChange={(e) =>
                    e.target.value &&
                    setEditing({ kind: "reaction", reaction: blankReaction(e.target.value as ReactionType) })
                  }
                  aria-label="Add a reaction"
                  disabled={catalog.compounds.length < 2}
                >
                  <option value="">Type…</option>
                  {REACTION_TYPES.map((t) => (
                    <option key={t.type} value={t.type}>
                      {t.label}
                    </option>
                  ))}
                </select>
              </label>
              <div className="tools-group mt-3">Reaction sets</div>
              {catalog.sets.map((s) => (
                <button
                  key={s.id}
                  className="tools-item"
                  aria-current={editing?.kind === "set" && editing.set.id === s.id}
                  onClick={() => setEditing({ kind: "set", set: structuredClone(s) })}
                >
                  {s.name}
                  <span className="muted text-[11px] block">
                    {s.reactions.length} reaction{s.reactions.length === 1 ? "" : "s"}
                  </span>
                </button>
              ))}
              <button
                className="btn btn-ghost mt-1"
                onClick={() => setEditing({ kind: "set", set: { name: "", description: "", reactions: [] } })}
              >
                <Plus size={13} /> New set
              </button>
            </nav>
            <div className="tools-main">
              {editing?.kind === "reaction" && (
                <ReactionEditor
                  key={editing.reaction.id ?? `new-${editing.reaction.type}`}
                  catalog={catalog}
                  initial={editing.reaction}
                  onSave={async (r) => {
                    let saved: Reaction | null = null;
                    const ok = await change(async () => (saved = await engine.saveReaction(id, r)));
                    if (ok && saved) setEditing({ kind: "reaction", reaction: saved });
                  }}
                  onDuplicate={async (rid) => {
                    let copy: Reaction | null = null;
                    if ((await change(async () => (copy = await engine.duplicateReaction(id, rid)))) && copy)
                      setEditing({ kind: "reaction", reaction: copy });
                  }}
                  onDelete={async (rid) => {
                    if (await change(() => engine.deleteReaction(id, rid))) setEditing(null);
                  }}
                />
              )}
              {editing?.kind === "set" && (
                <SetEditor
                  key={editing.set.id ?? "new-set"}
                  catalog={catalog}
                  initial={editing.set}
                  onSave={async (s) => {
                    let saved: ReactionSet | null = null;
                    if ((await change(async () => (saved = await engine.saveReactionSet(id, s)))) && saved)
                      setEditing({ kind: "set", set: saved });
                  }}
                  onDelete={async (sid) => {
                    if (await change(() => engine.deleteReactionSet(id, sid))) setEditing(null);
                  }}
                />
              )}
              {!editing && (
                <p className="muted">
                  {catalog.compounds.length < 2
                    ? "A reaction needs at least two compounds in the flowsheet."
                    : "Pick a reaction or a set, or add one. New reactions join the default set; a reactor runs the set chosen in its editor."}
                </p>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

/** A number typed freely and read when it parses. */
function NumberBox({
  value,
  onChange,
  label,
  className = "input",
}: {
  value: number;
  onChange: (v: number) => void;
  label: string;
  className?: string;
}) {
  const [draft, setDraft] = useState(String(value));
  useEffect(() => setDraft((d) => (Number(d) === value ? d : String(value))), [value]);
  return (
    <input
      className={className}
      inputMode="decimal"
      value={draft}
      aria-label={label}
      onChange={(e) => {
        setDraft(e.target.value);
        const v = Number(e.target.value);
        if (e.target.value.trim() !== "" && Number.isFinite(v)) onChange(v);
      }}
      onBlur={() => setDraft(String(value))}
    />
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="schema-row">
      <span className="schema-label">{label}</span>
      <span className="schema-value">{children}</span>
    </label>
  );
}

function Select({
  value,
  options,
  onChange,
  label,
}: {
  value: string;
  options: (string | { value: string; label: string })[];
  onChange: (v: string) => void;
  label: string;
}) {
  return (
    <select
      className="select w-full"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label={label}
    >
      {options.map((o) => {
        const v = typeof o === "string" ? o : o.value;
        return (
          <option key={v} value={v}>
            {typeof o === "string" ? o.replace(/_/g, " + ") : o.label}
          </option>
        );
      })}
    </select>
  );
}

const BASIS_LABELS: Record<string, string> = {
  Activity: "Activity",
  Fugacity: "Fugacity",
  MolarConc: "Molar concentration",
  MassConc: "Mass concentration",
  MolarFrac: "Mole fraction",
  MassFrac: "Mass fraction",
  PartialPress: "Partial pressure",
};

function ReactionEditor({
  catalog,
  initial,
  onSave,
  onDuplicate,
  onDelete,
}: {
  catalog: ReactionsCatalog;
  initial: Reaction;
  onSave: (r: Reaction) => Promise<void>;
  onDuplicate: (id: string) => Promise<void>;
  onDelete: (id: string) => Promise<void>;
}) {
  const [r, setR] = useState<Reaction>(initial);
  const [busy, setBusy] = useState(false);
  const set = <K extends keyof Reaction>(key: K, value: Reaction[K]) => setR((x) => ({ ...x, [key]: value }));
  const kinetic = r.type === "Kinetic" || r.type === "Heterogeneous_Catalytic";
  const { options } = catalog;

  // Every compound gets a row; those in the reaction are ticked.
  const rowFor = (name: string): StoichiometryRow | undefined =>
    r.components.find((c) => c.compound === name);
  const setRows = (rows: StoichiometryRow[]) => set("components", rows);
  const toggle = (name: string, on: boolean) =>
    setRows(
      on
        ? [...r.components, { compound: name, coefficient: 0, base: false, directOrder: 0, reverseOrder: 0 }]
        : r.components.filter((c) => c.compound !== name),
    );
  const edit = (name: string, change: Partial<StoichiometryRow>) =>
    setRows(
      r.components.map((c) =>
        c.compound === name ? { ...c, ...change } : change.base ? { ...c, base: false } : c,
      ),
    );

  const p = preview(r.type, r.components, catalog.compounds);
  const problem = !r.name.trim() ? "Enter a name." : p.problem;
  const basisUnits = options.basisUnits[r.basis];

  const save = async () => {
    setBusy(true);
    await onSave(r);
    setBusy(false);
  };

  return (
    <div className="reaction-editor" data-testid="reaction-editor">
      <h3 className="section-title">
        {r.id ? "Edit" : "New"} {typeLabel(r.type).toLowerCase()} reaction
      </h3>
      <div className="schema-fields">
        <Row label="Name">
          <input
            className="input w-full"
            value={r.name}
            onChange={(e) => set("name", e.target.value)}
            aria-label="Name"
          />
        </Row>
        <Row label="Description">
          <input
            className="input w-full"
            value={r.description}
            onChange={(e) => set("description", e.target.value)}
            aria-label="Description"
          />
        </Row>
      </div>

      <h3 className="section-title mt-3">Stoichiometry</h3>
      <div className="table-scroll">
        <table className="grid-table stoich-table">
          <thead>
            <tr>
              <th>In</th>
              <th>Compound</th>
              <th>Coefficient</th>
              <th>Base</th>
              {kinetic && <th>Direct order</th>}
              {kinetic && <th>Reverse order</th>}
            </tr>
          </thead>
          <tbody>
            {catalog.compounds.map((c) => {
              const row = rowFor(c.name);
              return (
                <tr key={c.name} className={row ? "" : "muted"}>
                  <td>
                    <input
                      type="checkbox"
                      checked={!!row}
                      onChange={(e) => toggle(c.name, e.target.checked)}
                      aria-label={`Include ${c.name}`}
                    />
                  </td>
                  <td title={c.formula}>{c.name}</td>
                  <td>
                    {row && (
                      <NumberBox
                        className="input input-sm num"
                        value={row.coefficient}
                        onChange={(v) => edit(c.name, { coefficient: v })}
                        label={`Coefficient of ${c.name}`}
                      />
                    )}
                  </td>
                  <td>
                    {row && (
                      <input
                        type="radio"
                        name="base"
                        checked={row.base}
                        onChange={() => edit(c.name, { base: true })}
                        aria-label={`${c.name} is the base component`}
                      />
                    )}
                  </td>
                  {kinetic && (
                    <td>
                      {row && (
                        <NumberBox
                          className="input input-sm num"
                          value={row.directOrder}
                          onChange={(v) => edit(c.name, { directOrder: v })}
                          label={`Direct order of ${c.name}`}
                        />
                      )}
                    </td>
                  )}
                  {kinetic && (
                    <td>
                      {row && (
                        <NumberBox
                          className="input input-sm num"
                          value={row.reverseOrder}
                          onChange={(v) => edit(c.name, { reverseOrder: v })}
                          label={`Reverse order of ${c.name}`}
                        />
                      )}
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className="stoich-summary" data-testid="stoichiometry">
        <div className="equation">
          {r.components.length ? p.equation : "Tick the compounds that take part."}
        </div>
        <div className="muted text-[12px]">
          Reaction heat {formatNumber(p.heat, 6)} kJ/kmol of base reactant at 25 °C · Mass balance{" "}
          {p.balanced ? (
            <span className="ok-text">OK</span>
          ) : (
            <span className="warn-text">{formatNumber(p.imbalance, 5)} kg/kmol</span>
          )}
        </div>
        <button
          className="btn btn-ghost"
          onClick={() => setRows(balanceBase(r.components, catalog.compounds))}
          disabled={!r.components.some((c) => c.base)}
          title="Set the base component's coefficient so the masses balance"
        >
          <Scale size={13} /> Balance
        </button>
      </div>

      <h3 className="section-title mt-3">Parameters</h3>
      <div className="schema-fields">
        {r.type === "Conversion" && (
          <>
            <Row label="Phase">
              <Select
                value={r.phase}
                options={options.phases.slice(0, 3)}
                onChange={(v) => set("phase", v)}
                label="Phase"
              />
            </Row>
            <Row label="Conversion of the base component (%, may use T in K)">
              <input
                className="input w-full"
                value={r.expression}
                onChange={(e) => set("expression", e.target.value)}
                aria-label="Conversion"
              />
            </Row>
          </>
        )}
        {r.type === "Equilibrium" && (
          <>
            <Row label="Phase">
              <Select
                value={r.phase}
                options={["Vapor", "Liquid"]}
                onChange={(v) => set("phase", v)}
                label="Phase"
              />
            </Row>
            <Row label="Basis">
              <Select
                value={r.basis}
                options={options.bases.map((b) => ({ value: b, label: BASIS_LABELS[b] ?? b }))}
                onChange={(v) =>
                  setR((x) => ({ ...x, basis: v, basisUnit: options.basisUnits[v]?.[0] ?? "" }))
                }
                label="Basis"
              />
            </Row>
            {basisUnits && (
              <Row label="Basis unit">
                <Select
                  value={r.basisUnit}
                  options={basisUnits}
                  onChange={(v) => set("basisUnit", v)}
                  label="Basis unit"
                />
              </Row>
            )}
            <Row label="Tmin (K)">
              <NumberBox value={r.tmin} onChange={(v) => set("tmin", v)} label="Tmin" />
            </Row>
            <Row label="Tmax (K)">
              <NumberBox value={r.tmax} onChange={(v) => set("tmax", v)} label="Tmax" />
            </Row>
            <Row label="Temperature approach (K)">
              <NumberBox
                value={r.approach}
                onChange={(v) => set("approach", v)}
                label="Temperature approach"
              />
            </Row>
            <Row label="Equilibrium constant">
              <Select
                value={r.keq}
                options={[
                  { value: "Gibbs", label: "From the Gibbs energy of reaction" },
                  { value: "Expression", label: "ln Keq = f(T)" },
                  { value: "Constant", label: "Constant" },
                ]}
                onChange={(v) => set("keq", v as Reaction["keq"])}
                label="Equilibrium constant"
              />
            </Row>
            {r.keq === "Expression" && (
              <Row label="ln Keq = (T in K)">
                <input
                  className="input w-full"
                  value={r.expression}
                  onChange={(e) => set("expression", e.target.value)}
                  aria-label="ln Keq expression"
                />
              </Row>
            )}
            {r.keq === "Constant" && (
              <Row label="Keq">
                <NumberBox value={r.constantKeq} onChange={(v) => set("constantKeq", v)} label="Keq" />
              </Row>
            )}
          </>
        )}
        {kinetic && (
          <>
            <Row label="Rate from">
              <Select
                value={r.kinetics}
                options={[
                  { value: "Expression", label: r.type === "Kinetic" ? "Rate constants" : "Rate expression" },
                  { value: "PythonScript", label: "Python script" },
                ]}
                onChange={(v) => set("kinetics", v as Reaction["kinetics"])}
                label="Rate from"
              />
            </Row>
            {r.kinetics === "PythonScript" && (
              <Row label="Script">
                <Select
                  value={r.script}
                  options={["", ...options.scripts]}
                  onChange={(v) => set("script", v)}
                  label="Script"
                />
              </Row>
            )}
            <Row label="Phase">
              <Select
                value={r.phase}
                options={options.phases}
                onChange={(v) => set("phase", v)}
                label="Phase"
              />
            </Row>
            <Row label="Basis">
              <Select
                value={r.basis}
                options={options.bases.map((b) => ({ value: b, label: BASIS_LABELS[b] ?? b }))}
                onChange={(v) =>
                  setR((x) => ({ ...x, basis: v, basisUnit: options.basisUnits[v]?.[0] ?? "" }))
                }
                label="Basis"
              />
            </Row>
            {basisUnits && (
              <Row label="Amount unit">
                <Select
                  value={r.basisUnit}
                  options={basisUnits}
                  onChange={(v) => set("basisUnit", v)}
                  label="Amount unit"
                />
              </Row>
            )}
            <Row label="Rate unit">
              <Select
                value={r.rateUnit}
                options={options.rateUnits[r.type] ?? []}
                onChange={(v) => set("rateUnit", v)}
                label="Rate unit"
              />
            </Row>
            <Row label="Tmin (K)">
              <NumberBox value={r.tmin} onChange={(v) => set("tmin", v)} label="Tmin" />
            </Row>
            <Row label="Tmax (K)">
              <NumberBox value={r.tmax} onChange={(v) => set("tmax", v)} label="Tmax" />
            </Row>
            {r.type === "Kinetic" &&
              r.kinetics === "Expression" &&
              (["forward", "reverse"] as const).map((dir) => {
                const k = r[dir];
                const setK = (change: Partial<typeof k>) => set(dir, { ...k, ...change });
                const name = dir === "forward" ? "Forward" : "Reverse";
                return (
                  <fieldset key={dir} className="kinetic-term">
                    <legend>{name} rate constant</legend>
                    <Row label="Form">
                      <Select
                        value={k.type}
                        options={[
                          { value: "Arrhenius", label: "Arrhenius: k = A exp(−E/RT)" },
                          { value: "UserDefined", label: "User-defined: k = f(T)" },
                        ]}
                        onChange={(v) => setK({ type: v as typeof k.type })}
                        label={`${name} form`}
                      />
                    </Row>
                    {k.type === "Arrhenius" ? (
                      <>
                        <Row label="A">
                          <NumberBox value={k.a} onChange={(v) => setK({ a: v })} label={`${name} A`} />
                        </Row>
                        <Row label="E">
                          <span className="unit-input">
                            <NumberBox value={k.e} onChange={(v) => setK({ e: v })} label={`${name} E`} />
                            <select
                              className="select"
                              value={k.eUnit}
                              onChange={(e) => setK({ eUnit: e.target.value })}
                              aria-label={`${name} E unit`}
                            >
                              {[...new Set([k.eUnit, ...options.energyUnits])].map((u) => (
                                <option key={u} value={u}>
                                  {u}
                                </option>
                              ))}
                            </select>
                          </span>
                        </Row>
                      </>
                    ) : (
                      <Row label="k = f(T), T in K">
                        <input
                          className="input w-full"
                          value={k.expression}
                          onChange={(e) => setK({ expression: e.target.value })}
                          aria-label={`${name} expression`}
                        />
                      </Row>
                    )}
                  </fieldset>
                );
              })}
            {r.type === "Heterogeneous_Catalytic" && r.kinetics === "Expression" && (
              <>
                <Row label="Rate numerator">
                  <input
                    className="input w-full"
                    value={r.numerator}
                    onChange={(e) => set("numerator", e.target.value)}
                    aria-label="Rate numerator"
                  />
                </Row>
                <Row label="Rate denominator">
                  <input
                    className="input w-full"
                    value={r.denominator}
                    onChange={(e) => set("denominator", e.target.value)}
                    aria-label="Rate denominator"
                  />
                </Row>
              </>
            )}
          </>
        )}
      </div>

      <div className="flex items-center gap-2 mt-3">
        {problem && <span className="warn-text text-[12px]">{problem}</span>}
        <span className="ml-auto flex gap-1">
          {r.id && (
            <>
              <button className="btn btn-ghost" onClick={() => void onDuplicate(r.id!)}>
                <Copy size={13} /> Duplicate
              </button>
              <button className="btn btn-ghost" onClick={() => void onDelete(r.id!)}>
                <Trash2 size={13} /> Delete
              </button>
            </>
          )}
          <button
            className="btn btn-primary"
            onClick={() => void save()}
            disabled={busy || problem !== null}
            data-testid="save-reaction"
          >
            Save
          </button>
        </span>
      </div>
    </div>
  );
}

function SetEditor({
  catalog,
  initial,
  onSave,
  onDelete,
}: {
  catalog: ReactionsCatalog;
  initial: ReactionSet;
  onSave: (s: ReactionSet) => Promise<void>;
  onDelete: (id: string) => Promise<void>;
}) {
  const [s, setS] = useState<ReactionSet>(initial);
  const member = (id: string) => s.reactions.find((m) => m.id === id);
  const setMember = (id: string, change: Partial<ReactionSet["reactions"][number]> | null) =>
    setS((x) => ({
      ...x,
      reactions:
        change === null
          ? x.reactions.filter((m) => m.id !== id)
          : member(id)
            ? x.reactions.map((m) => (m.id === id ? { ...m, ...change } : m))
            : [...x.reactions, { id, active: true, rank: 0, ...change }],
    }));
  return (
    <div data-testid="set-editor">
      <h3 className="section-title">{s.id ? "Edit" : "New"} reaction set</h3>
      <div className="schema-fields">
        <Row label="Name">
          <input
            className="input w-full"
            value={s.name}
            onChange={(e) => setS({ ...s, name: e.target.value })}
            aria-label="Set name"
          />
        </Row>
        <Row label="Description">
          <input
            className="input w-full"
            value={s.description}
            onChange={(e) => setS({ ...s, description: e.target.value })}
            aria-label="Set description"
          />
        </Row>
      </div>
      <h3 className="section-title mt-3">Reactions in the set</h3>
      {catalog.reactions.length === 0 ? (
        <p className="muted">The flowsheet has no reactions yet.</p>
      ) : (
        <table className="grid-table">
          <thead>
            <tr>
              <th>In set</th>
              <th>Reaction</th>
              <th>Active</th>
              <th title="Reactions of lower rank run first in sequential reactors">Rank</th>
            </tr>
          </thead>
          <tbody>
            {catalog.reactions.map((r) => {
              const m = member(r.id);
              return (
                <tr key={r.id}>
                  <td>
                    <input
                      type="checkbox"
                      checked={!!m}
                      onChange={(e) => setMember(r.id, e.target.checked ? {} : null)}
                      aria-label={`${r.name} in the set`}
                    />
                  </td>
                  <td>
                    {r.name} <span className="muted text-[11px]">{r.equation}</span>
                  </td>
                  <td>
                    {m && (
                      <input
                        type="checkbox"
                        checked={m.active}
                        onChange={(e) => setMember(r.id, { active: e.target.checked })}
                        aria-label={`${r.name} active`}
                      />
                    )}
                  </td>
                  <td>
                    {m && (
                      <NumberBox
                        className="input input-sm num"
                        value={m.rank}
                        onChange={(v) => setMember(r.id, { rank: Math.round(v) })}
                        label={`${r.name} rank`}
                      />
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      <div className="flex items-center gap-2 mt-3">
        <span className="ml-auto flex gap-1">
          {s.id && !s.isDefault && (
            <button className="btn btn-ghost" onClick={() => void onDelete(s.id!)}>
              <Trash2 size={13} /> Delete
            </button>
          )}
          <button
            className="btn btn-primary"
            onClick={() => void onSave(s)}
            disabled={!s.name.trim()}
            data-testid="save-set"
          >
            Save
          </button>
        </span>
      </div>
    </div>
  );
}
