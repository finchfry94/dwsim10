import { useEffect, useMemo, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type { StreamDetails, UnitSystem } from "../engine/types";
import {
  COMPOSITION_BASES,
  SPEC_MODES,
  compositionQuantity,
  compositionSum,
  formFromStream,
  isFractionBasis,
  normalized,
  toSpecification,
  usesPressure,
  usesTemperature,
  usesVaporFraction,
  validate,
  withCompositionBasis,
  withFlowBasis,
  type CompositionBasis,
  type FlowBasis,
  type Measured,
  type SpecMode,
  type StreamForm,
} from "../lib/streamForm";
import { formatNumber, toSI, unitLabel, unitsOf, fromSI, type Quantity } from "../lib/units";
import { useApp } from "../state/store";

type Material = Extract<StreamDetails, { kind: "material" }>;

/**
 * Specifies a feed stream: how it is flashed, its conditions, its flow and its composition.
 * Nothing reaches the engine until Apply; the stream then needs solving to show new results.
 */
export function StreamEditor({
  flowsheetId,
  stream,
  units,
}: {
  flowsheetId: string;
  stream: Material;
  units: UnitSystem | null;
}) {
  const [form, setForm] = useState<StreamForm>(() => formFromStream(stream, units));
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const log = useApp((s) => s.log);

  // Follow the engine when the stream changes underneath and the user has nothing pending.
  useEffect(() => {
    if (!dirty) setForm(formFromStream(stream, units));
  }, [stream, units, dirty]);

  const errors = useMemo(() => validate(form), [form]);
  const valid = Object.keys(errors).length === 0;
  const edit = (next: StreamForm) => {
    setForm(next);
    setDirty(true);
  };

  const apply = async () => {
    if (!valid) return;
    setSaving(true);
    try {
      await engine.specifyStream(flowsheetId, stream.id, toSpecification(form));
      setDirty(false);
      log("info", `${stream.tag}: specification applied. Solve to update the results.`);
      await useApp.getState().refresh();
    } catch (e) {
      log("error", `${stream.tag}: ${EngineError.from(e).message}`);
    } finally {
      setSaving(false);
    }
  };

  const fractions = isFractionBasis(form.compositionBasis);
  const sum = compositionSum(form);

  return (
    <section className="stream-editor" data-testid="stream-editor" aria-label="Feed specification">
      <div className="panel-header">
        <h3 className="section-title">Specification</h3>
        {dirty && <span className="pill">Unsaved</span>}
      </div>

      <label className="field">
        <span className="field-label">Flash spec</span>
        <select
          className="select"
          value={form.mode}
          onChange={(e) => edit({ ...form, mode: e.target.value as SpecMode })}
          aria-label="Flash specification"
        >
          {SPEC_MODES.map((m) => (
            <option key={m.mode} value={m.mode}>
              {m.label}
            </option>
          ))}
        </select>
      </label>

      {usesTemperature(form.mode) && (
        <MeasuredField
          label="Temperature"
          quantity="temperature"
          value={form.temperature}
          error={errors.temperature}
          onChange={(temperature) => edit({ ...form, temperature })}
        />
      )}
      {usesPressure(form.mode) && (
        <MeasuredField
          label="Pressure"
          quantity="pressure"
          value={form.pressure}
          error={errors.pressure}
          onChange={(pressure) => edit({ ...form, pressure })}
        />
      )}
      {usesVaporFraction(form.mode) && (
        <label className="field">
          <span className="field-label">Vapor fraction</span>
          <input
            className={`input ${errors.vaporFraction ? "is-invalid" : ""}`}
            inputMode="decimal"
            value={form.vaporFraction}
            onChange={(e) => edit({ ...form, vaporFraction: e.target.value })}
            aria-label="Vapor fraction"
            aria-invalid={!!errors.vaporFraction}
          />
          {errors.vaporFraction && <span className="field-error">{errors.vaporFraction}</span>}
        </label>
      )}

      <div className="field">
        <span className="field-label">Total flow</span>
        <div className="flex gap-1">
          <select
            className="select"
            value={form.flowBasis}
            disabled={!fractions}
            onChange={(e) => edit(withFlowBasis(form, e.target.value as FlowBasis, stream, units))}
            aria-label="Flow basis"
          >
            <option value="mass">Mass</option>
            <option value="molar">Molar</option>
          </select>
          {fractions ? (
            <UnitInput
              quantity={form.flowBasis === "mass" ? "massFlow" : "molarFlow"}
              value={form.flow}
              invalid={!!errors.flow}
              label="Total flow"
              onChange={(flow) => edit({ ...form, flow })}
            />
          ) : (
            <span className="muted self-center">the sum of the compound flows</span>
          )}
        </div>
        {errors.flow && <span className="field-error">{errors.flow}</span>}
      </div>

      <div className="panel-header">
        <h3 className="section-title">Composition</h3>
        <select
          className="select select-sm"
          value={form.compositionBasis}
          onChange={(e) =>
            edit(withCompositionBasis(form, e.target.value as CompositionBasis, stream, units))
          }
          aria-label="Composition basis"
        >
          {COMPOSITION_BASES.map((b) => (
            <option key={b.basis} value={b.basis}>
              {b.label}
            </option>
          ))}
        </select>
      </div>
      <table className="grid-table" data-testid="composition-editor">
        <tbody>
          {Object.entries(form.composition).map(([name, value]) => (
            <tr key={name}>
              <th scope="row">{name}</th>
              <td className="num">
                <input
                  className="cell-input"
                  inputMode="decimal"
                  value={value}
                  onChange={(e) =>
                    edit({ ...form, composition: { ...form.composition, [name]: e.target.value } })
                  }
                  aria-label={`${name} amount`}
                />
              </td>
              <td className="unit">{fractions ? "" : unitLabel(form.compositionUnit)}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <th scope="row">Sum</th>
            <td className={`num ${fractions && Math.abs(sum - 1) > 1e-6 ? "text-[var(--warning)]" : ""}`}>
              {formatNumber(sum, 6)}
            </td>
            <td className="unit">
              {!fractions && (
                <select
                  className="select select-sm"
                  value={form.compositionUnit}
                  onChange={(e) => edit(convertCompositionUnit(form, e.target.value))}
                  aria-label="Compound flow unit"
                >
                  {unitsOf(compositionQuantity(form.compositionBasis)).map((u) => (
                    <option key={u} value={u}>
                      {unitLabel(u)}
                    </option>
                  ))}
                </select>
              )}
            </td>
          </tr>
        </tfoot>
      </table>
      {errors.composition && <span className="field-error">{errors.composition}</span>}
      {fractions && Math.abs(sum - 1) > 1e-6 && sum > 0 && (
        <p className="muted">
          Fractions are normalized when applied.{" "}
          <button className="link" onClick={() => edit(normalized(form))}>
            Normalize now
          </button>
        </p>
      )}

      <div className="flex justify-end gap-2 mt-3">
        <button
          className="btn"
          disabled={!dirty || saving}
          onClick={() => {
            setForm(formFromStream(stream, units));
            setDirty(false);
          }}
        >
          Revert
        </button>
        <button
          className="btn btn-primary"
          disabled={!dirty || !valid || saving}
          onClick={() => void apply()}
          data-testid="apply-spec"
        >
          {saving ? "Applying…" : "Apply"}
        </button>
      </div>
    </section>
  );
}

/** Re-expresses the compound flows in another unit, so the amounts stay the same. */
function convertCompositionUnit(form: StreamForm, unit: string): StreamForm {
  const q = compositionQuantity(form.compositionBasis);
  const composition: Record<string, string> = {};
  for (const [n, v] of Object.entries(form.composition)) {
    const x = Number(v);
    composition[n] =
      v.trim() === "" || !Number.isFinite(x)
        ? v
        : String(Number(fromSI(q, toSI(q, x, form.compositionUnit), unit).value.toPrecision(8)));
  }
  return { ...form, compositionUnit: unit, composition };
}

function MeasuredField({
  label,
  quantity,
  value,
  error,
  onChange,
}: {
  label: string;
  quantity: Quantity;
  value: Measured;
  error?: string;
  onChange: (v: Measured) => void;
}) {
  return (
    <div className="field">
      <span className="field-label">{label}</span>
      <UnitInput quantity={quantity} value={value} invalid={!!error} label={label} onChange={onChange} />
      {error && <span className="field-error">{error}</span>}
    </div>
  );
}

/** A number with its unit; changing the unit converts the number, so the quantity is unchanged. */
function UnitInput({
  quantity,
  value,
  invalid,
  label,
  onChange,
}: {
  quantity: Quantity;
  value: Measured;
  invalid: boolean;
  label: string;
  onChange: (v: Measured) => void;
}) {
  const changeUnit = (unit: string) => {
    const x = Number(value.value);
    if (value.value.trim() === "" || !Number.isFinite(x)) return onChange({ ...value, unit });
    const converted = fromSI(quantity, toSI(quantity, x, value.unit), unit).value;
    onChange({ unit, value: String(Number(converted.toPrecision(8))) });
  };
  return (
    <div className="unit-input">
      <input
        className={`input ${invalid ? "is-invalid" : ""}`}
        inputMode="decimal"
        value={value.value}
        onChange={(e) => onChange({ ...value, value: e.target.value })}
        aria-label={label}
        aria-invalid={invalid}
      />
      <select
        className="select"
        value={value.unit}
        onChange={(e) => changeUnit(e.target.value)}
        aria-label={`${label} unit`}
      >
        {unitsOf(quantity).map((u) => (
          <option key={u} value={u}>
            {unitLabel(u)}
          </option>
        ))}
      </select>
    </div>
  );
}
