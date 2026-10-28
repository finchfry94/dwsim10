import { AlertCircle, CheckCircle2, ChevronRight, Link2, Plus, Trash2, Unlink } from "lucide-react";
import { useEffect, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type {
  ChoiceField,
  EditorSchema,
  NumberField,
  SchemaConnection,
  SchemaField,
  SchemaSection,
  TableField,
} from "../engine/types";
import { formatNumber, unitLabel } from "../lib/units";
import { useApp } from "../state/store";

/**
 * A unit operation's editor, rendered from the schema the engine bridge generates for it:
 * the calculation mode, what is still missing, the specification, any tables, connections
 * and results. Every change goes to the engine at once and the editor shows what comes back.
 */
export function SchemaEditor({ flowsheetId, id }: { flowsheetId: string; id: string }) {
  const revision = useApp((s) => s.revision);
  const log = useApp((s) => s.log);
  const [schema, setSchema] = useState<EditorSchema | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let live = true;
    engine
      .schema(flowsheetId, id)
      .then((s) => {
        if (live) {
          setSchema(s);
          setError(null);
        }
      })
      .catch((e) => live && setError(EngineError.from(e).message));
    return () => {
      live = false;
    };
  }, [flowsheetId, id, revision]);

  /** Runs an edit; the engine answers with the new schema. */
  const edit = async (what: string, action: () => Promise<EditorSchema>): Promise<boolean> => {
    setBusy(true);
    try {
      setSchema(await action());
      void useApp.getState().afterEdit();
      return true;
    } catch (e) {
      log("error", `${schema?.tag ?? "Edit"} – ${what}: ${EngineError.from(e).message}`);
      return false;
    } finally {
      setBusy(false);
    }
  };

  if (error) return <div className="callout callout-error">{error}</div>;
  if (!schema) return <p className="muted">Loading…</p>;

  const setField = (field: SchemaField, value: unknown, unit?: string | null) =>
    edit(field.label, () => engine.setField(flowsheetId, id, field.id, value, unit));

  return (
    <div className="schema-editor" data-testid="schema-editor" aria-busy={busy}>
      {schema.mode && (
        <label className="field">
          <span className="field-label">Calculation mode</span>
          <select
            className="select"
            value={schema.mode.value ?? ""}
            disabled={busy}
            onChange={(e) => void edit("mode", () => engine.setMode(flowsheetId, id, e.target.value))}
            aria-label="Calculation mode"
          >
            {schema.mode.options.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </select>
        </label>
      )}
      <DofBanner schema={schema} />
      {schema.sections
        .filter((s) => s.id !== "results")
        .map((section) => (
          <Section key={section.id} section={section} disabled={busy} onSet={setField} />
        ))}
      <Connections flowsheetId={flowsheetId} schema={schema} disabled={busy} />
      {schema.sections
        .filter((s) => s.id === "results" && s.fields.length > 0)
        .map((section) => (
          <Section key={section.id} section={section} disabled onSet={setField} />
        ))}
    </div>
  );
}

function DofBanner({ schema }: { schema: EditorSchema }) {
  const dof = schema.dof;
  if (!dof || !dof.supported) return null;
  const missing = dof.slots.filter((s) => s.required && !s.set);
  if (missing.length === 0)
    return (
      <div className="dof dof-ok" data-testid="dof">
        <CheckCircle2 size={14} /> Fully specified
      </div>
    );
  return (
    <div className="dof dof-missing" data-testid="dof">
      <AlertCircle size={14} />
      <span>
        {missing.length === 1 ? "Missing: " : `${missing.length} specifications missing: `}
        {missing.map((m) => m.name).join(", ")}
      </span>
    </div>
  );
}

function Section({
  section,
  disabled,
  onSet,
}: {
  section: SchemaSection;
  disabled: boolean;
  onSet: (field: SchemaField, value: unknown, unit?: string | null) => Promise<boolean>;
}) {
  const [open, setOpen] = useState(!section.collapsed);
  if (section.fields.length === 0) return null;
  return (
    <section className="schema-section" data-testid={`section-${section.id}`}>
      <button className="section-toggle" onClick={() => setOpen(!open)} aria-expanded={open}>
        <ChevronRight size={14} className={open ? "rotate-90" : ""} />
        <h3 className="section-title">{section.title}</h3>
        <span className="muted text-[11px]">{section.fields.length}</span>
      </button>
      {open && (
        <div className="schema-fields">
          {section.fields.map((f) => (
            <FieldRow key={f.id} field={f} disabled={disabled} onSet={onSet} />
          ))}
        </div>
      )}
    </section>
  );
}

function FieldRow({
  field,
  disabled,
  onSet,
}: {
  field: SchemaField;
  disabled: boolean;
  onSet: (field: SchemaField, value: unknown, unit?: string | null) => Promise<boolean>;
}) {
  if (field.kind === "table") return <TableEditor field={field} disabled={disabled} onSet={onSet} />;
  return (
    <div
      className={`schema-row ${field.missing ? "is-missing" : ""} ${field.editable ? "" : "is-calculated"}`}
    >
      <span className="schema-label" title={field.hint ?? undefined}>
        {field.label}
        {field.missing && <span className="badge badge-missing">required</span>}
      </span>
      <span className="schema-value">
        {field.kind === "number" && <NumberInput field={field} disabled={disabled} onSet={onSet} />}
        {field.kind === "choice" && <ChoiceInput field={field} disabled={disabled} onSet={onSet} />}
        {field.kind === "bool" && (
          <input
            type="checkbox"
            checked={!!field.value}
            disabled={disabled || !field.editable}
            onChange={(e) => void onSet(field, e.target.checked)}
            aria-label={field.label}
          />
        )}
        {field.kind === "text" &&
          (field.editable ? (
            <TextInput
              value={field.value ?? ""}
              label={field.label}
              disabled={disabled}
              onCommit={(v) => onSet(field, v)}
            />
          ) : (
            <span className="readonly">{field.value || "—"}</span>
          ))}
      </span>
    </div>
  );
}

/** A number with its unit. Typing in another unit is allowed; the engine converts on commit. */
function NumberInput({
  field,
  disabled,
  onSet,
}: {
  field: NumberField;
  disabled: boolean;
  onSet: (field: SchemaField, value: unknown, unit?: string | null) => Promise<boolean>;
}) {
  const shown = field.value === null ? "" : formatNumber(field.value, 6).replace(/,/g, "");
  const [draft, setDraft] = useState(shown);
  const [unit, setUnit] = useState(field.unit);
  useEffect(() => {
    setDraft(shown);
    setUnit(field.unit);
  }, [shown, field.unit]);

  if (!field.editable)
    return (
      <span className="readonly" title={field.missing ? undefined : "Calculated in the current mode"}>
        {field.value === null ? "—" : formatNumber(field.value, 5)}{" "}
        <span className="unit">{field.unit ? unitLabel(field.unit) : ""}</span>
      </span>
    );

  const commit = async () => {
    if (draft === shown && unit === field.unit) return;
    const v = Number(draft);
    if (draft.trim() === "" || !Number.isFinite(v)) {
      setDraft(shown);
      setUnit(field.unit);
      return;
    }
    if (!(await onSet(field, v, unit))) {
      setDraft(shown);
      setUnit(field.unit);
    }
  };

  const changeUnit = async (next: string) => {
    const v = Number(draft);
    if (unit && draft.trim() !== "" && Number.isFinite(v)) {
      try {
        const converted = await engine.convertUnits(v, unit, next);
        setDraft(String(Number(converted.toPrecision(8))));
      } catch {
        /* keep the number; the unit says what it means */
      }
    }
    setUnit(next);
  };

  return (
    <span className="unit-input">
      <input
        className="input"
        inputMode="decimal"
        value={draft}
        disabled={disabled}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={() => void commit()}
        onKeyDown={(e) => {
          if (e.key === "Enter") (e.target as HTMLInputElement).blur();
          if (e.key === "Escape") {
            setDraft(shown);
            setUnit(field.unit);
          }
        }}
        aria-label={field.label}
        aria-invalid={field.missing}
      />
      {field.units && field.units.length > 1 ? (
        <select
          className="select"
          value={unit ?? ""}
          disabled={disabled}
          onChange={(e) => void changeUnit(e.target.value)}
          aria-label={`${field.label} unit`}
        >
          {field.units.map((u) => (
            <option key={u} value={u}>
              {unitLabel(u)}
            </option>
          ))}
        </select>
      ) : (
        field.unit && <span className="unit unit-fixed">{unitLabel(field.unit)}</span>
      )}
    </span>
  );
}

function ChoiceInput({
  field,
  disabled,
  onSet,
}: {
  field: ChoiceField;
  disabled: boolean;
  onSet: (field: SchemaField, value: unknown) => Promise<boolean>;
}) {
  return (
    <select
      className="select w-full"
      value={field.value ?? ""}
      disabled={disabled || !field.editable}
      onChange={(e) => void onSet(field, e.target.value)}
      aria-label={field.label}
    >
      {field.value === null && <option value="">Choose…</option>}
      {field.options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}

function TextInput({
  value,
  label,
  disabled,
  onCommit,
}: {
  value: string;
  label: string;
  disabled: boolean;
  onCommit: (v: string) => Promise<boolean>;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  return (
    <input
      className="input w-full"
      value={draft}
      disabled={disabled}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => draft !== value && void onCommit(draft).then((ok) => ok || setDraft(value))}
      onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()}
      aria-label={label}
    />
  );
}

/**
 * A table edited as a whole: changes stay local until Apply sends every row, so a half-typed
 * row never reaches the engine.
 */
function TableEditor({
  field,
  disabled,
  onSet,
}: {
  field: TableField;
  disabled: boolean;
  onSet: (field: SchemaField, value: unknown) => Promise<boolean>;
}) {
  const [rows, setRows] = useState(field.rows);
  const [dirty, setDirty] = useState(false);
  useEffect(() => {
    if (!dirty) setRows(field.rows);
  }, [field.rows, dirty]);

  const update = (i: number, key: string, value: string | number | null) => {
    setRows(rows.map((r, j) => (j === i ? { ...r, [key]: value } : r)));
    setDirty(true);
  };

  const apply = async () => {
    const parsed = rows.map((r) => {
      const out: Record<string, unknown> = { ...r };
      for (const c of field.columns)
        if (c.kind === "number" && out[c.key] !== null && out[c.key] !== "") out[c.key] = Number(out[c.key]);
      return out;
    });
    if (await onSet(field, parsed)) setDirty(false);
  };

  return (
    <div className="schema-table" data-testid={`table-${field.id}`}>
      <div className="field-label">{field.label}</div>
      <div className="table-scroll">
        <table className="grid-table">
          <thead>
            <tr>
              {field.columns.map((c) => (
                <th key={c.key}>
                  {c.label}
                  {c.unit && <span className="unit"> ({unitLabel(c.unit)})</span>}
                </th>
              ))}
              {field.canAddRows && <th aria-label="Remove" />}
            </tr>
          </thead>
          <tbody>
            {rows.map((r, i) => (
              <tr key={i}>
                {field.columns.map((c) => (
                  <td key={c.key} className={c.kind === "number" ? "num" : ""}>
                    {!c.editable || !field.editable ? (
                      <span>{String(r[c.key] ?? "")}</span>
                    ) : c.kind === "choice" ? (
                      <select
                        className="select select-sm"
                        value={String(r[c.key] ?? "")}
                        disabled={disabled}
                        onChange={(e) => update(i, c.key, e.target.value)}
                        aria-label={`${c.label} ${i + 1}`}
                      >
                        {c.options?.map((o) => (
                          <option key={o.value} value={o.value}>
                            {o.label}
                          </option>
                        ))}
                      </select>
                    ) : (
                      <span className="inline-flex items-center gap-1">
                        <input
                          className="cell-input"
                          inputMode={c.kind === "number" ? "decimal" : undefined}
                          value={r[c.key] === null || r[c.key] === undefined ? "" : String(r[c.key])}
                          disabled={disabled}
                          onChange={(e) => update(i, c.key, e.target.value)}
                          aria-label={`${c.label} ${i + 1}`}
                        />
                        {c.unitKey && r[c.unitKey] ? (
                          <span className="unit">{unitLabel(String(r[c.unitKey]))}</span>
                        ) : null}
                      </span>
                    )}
                  </td>
                ))}
                {field.canAddRows && (
                  <td>
                    <button
                      className="btn btn-icon btn-ghost"
                      onClick={() => {
                        setRows(rows.filter((_, j) => j !== i));
                        setDirty(true);
                      }}
                      aria-label={`Remove row ${i + 1}`}
                    >
                      <Trash2 size={13} />
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="flex justify-between gap-2 mt-2">
        {field.canAddRows && field.newRow ? (
          <button
            className="btn"
            onClick={() => {
              setRows([...rows, { ...field.newRow! }]);
              setDirty(true);
            }}
          >
            <Plus size={14} /> Add row
          </button>
        ) : (
          <span />
        )}
        <span className="flex gap-2">
          <button
            className="btn"
            disabled={!dirty || disabled}
            onClick={() => {
              setRows(field.rows);
              setDirty(false);
            }}
          >
            Revert
          </button>
          <button
            className="btn btn-primary"
            disabled={!dirty || disabled}
            onClick={() => void apply()}
            data-testid={`apply-${field.id}`}
          >
            Apply
          </button>
        </span>
      </div>
    </div>
  );
}

/** The object's ports and what is attached to them; a free port can take an unattached stream. */
function Connections({
  flowsheetId,
  schema,
  disabled,
}: {
  flowsheetId: string;
  schema: EditorSchema;
  disabled: boolean;
}) {
  const log = useApp((s) => s.log);
  const [open, setOpen] = useState(true);
  if (schema.connections.length === 0) return null;

  const run = async (what: string, action: () => Promise<unknown>) => {
    try {
      await action();
      await useApp.getState().afterEdit();
    } catch (e) {
      log("error", `${schema.tag} – ${what}: ${EngineError.from(e).message}`);
    }
  };

  const attach = (c: SchemaConnection, streamId: string) =>
    run("connect", () =>
      c.direction === "in"
        ? engine.connect(flowsheetId, streamId, "out-0", schema.id, c.port)
        : engine.connect(flowsheetId, schema.id, c.port, streamId, "in-0"),
    );

  const detach = (c: SchemaConnection) =>
    run("disconnect", () =>
      c.direction === "in"
        ? engine.disconnect(flowsheetId, c.stream!.id, schema.id)
        : engine.disconnect(flowsheetId, schema.id, c.stream!.id),
    );

  return (
    <section className="schema-section" data-testid="section-connections">
      <button className="section-toggle" onClick={() => setOpen(!open)} aria-expanded={open}>
        <ChevronRight size={14} className={open ? "rotate-90" : ""} />
        <h3 className="section-title">Connections</h3>
      </button>
      {open && (
        <div className="schema-fields">
          {schema.connections.map((c) => (
            <div key={c.port} className="schema-row">
              <span className="schema-label">
                {c.label}
                {c.energy && <span className="badge badge-energy">energy</span>}
              </span>
              <span className="schema-value">
                {c.stream ? (
                  <span className="flex items-center justify-between gap-2">
                    <span className="flex items-center gap-1">
                      <Link2 size={13} className="muted" /> {c.stream.tag}
                    </span>
                    <button
                      className="btn btn-icon btn-ghost"
                      disabled={disabled}
                      onClick={() => void detach(c)}
                      aria-label={`Disconnect ${c.stream.tag}`}
                      title="Disconnect"
                    >
                      <Unlink size={13} />
                    </button>
                  </span>
                ) : c.candidates.length > 0 ? (
                  <select
                    className="select w-full"
                    value=""
                    disabled={disabled}
                    onChange={(e) => void attach(c, e.target.value)}
                    aria-label={`Connect ${c.label}`}
                  >
                    <option value="">Connect a stream…</option>
                    {c.candidates.map((s) => (
                      <option key={s.id} value={s.id}>
                        {s.tag}
                      </option>
                    ))}
                  </select>
                ) : (
                  <span className="muted">Not connected</span>
                )}
              </span>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
