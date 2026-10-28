import { Check, FlaskConical, Plus, Ruler, Search, Thermometer, X } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { engine } from "../engine/api";
import { EngineError } from "../engine/transport";
import type { CompoundInfo, FlowsheetSummary, UnitSystem } from "../engine/types";
import { unitLabel } from "../lib/units";
import { useApp } from "../state/store";

type Step = "compounds" | "package" | "units";

const STEPS: { step: Step; label: string; icon: typeof FlaskConical }[] = [
  { step: "compounds", label: "Compounds", icon: FlaskConical },
  { step: "package", label: "Property package", icon: Thermometer },
  { step: "units", label: "Units", icon: Ruler },
];

/**
 * When each common property package fits, in a line: the hint the old wizard left to the help
 * pages. Packages not listed are still offered, without a hint.
 */
const PACKAGE_HINTS: Record<string, string> = {
  "Peng-Robinson (PR)": "Hydrocarbons, gases and light non-polar mixtures; the usual first choice.",
  "Soave-Redlich-Kwong (SRK)": "Hydrocarbons and gases; close to Peng-Robinson.",
  "Peng-Robinson 1978 (PR78)": "Peng-Robinson with the 1978 correction for heavier hydrocarbons.",
  "Lee-Kesler-Plöcker": "Hydrocarbon mixtures, good enthalpies.",
  NRTL: "Polar and non-ideal liquids, azeotropes and liquid-liquid splits.",
  UNIQUAC: "Polar and non-ideal liquids, liquid-liquid splits.",
  UNIFAC: "Predicts activity coefficients when no binary data exist.",
  "Modified UNIFAC (Dortmund)": "Predictive, better than UNIFAC for enthalpies and dilute solutions.",
  Wilson: "Miscible polar mixtures; cannot predict two liquids.",
  "Raoult's Law": "Ideal mixtures at low pressure, for quick estimates.",
  "Steam Tables (IAPWS-IF97)": "Pure water and steam.",
  "Seawater IAPWS-08": "Seawater and brines.",
  "Grayson-Streed": "Hydrogen-rich hydrocarbon systems, refinery heavy ends.",
  "Chao-Seader": "Light hydrocarbons at moderate pressure.",
  CoolProp: "High-accuracy pure fluids and refrigerants.",
};

/**
 * Sets a simulation up: compounds, property package, units. As a wizard (`mode="new"`) it walks
 * the steps and holds Finish until the flowsheet can be solved; as an editor it opens on any step.
 * Every change goes to the engine at once, so closing never loses work.
 */
export function SetupDialog({ mode, onClose }: { mode: "new" | "edit"; onClose: () => void }) {
  const flowsheet = useApp((s) => s.flowsheet)!;
  const [step, setStep] = useState<Step>("compounds");
  const ready = flowsheet.compounds.length > 0 && flowsheet.propertyPackages.length > 0;
  const index = STEPS.findIndex((s) => s.step === step);

  return (
    <div className="dialog-backdrop">
      <div className="dialog setup-dialog" role="dialog" aria-label="Simulation setup">
        <div className="setup-head">
          <h2 className="dialog-title">{mode === "new" ? "New simulation" : "Simulation setup"}</h2>
          {mode === "edit" && (
            <button className="btn btn-icon btn-ghost" onClick={onClose} aria-label="Close">
              <X size={16} />
            </button>
          )}
        </div>
        <div className="setup-body">
          <nav className="setup-steps" aria-label="Steps">
            {STEPS.map(({ step: s, label, icon: Icon }, i) => {
              const done =
                s === "compounds"
                  ? flowsheet.compounds.length > 0
                  : s === "package"
                    ? flowsheet.propertyPackages.length > 0
                    : true;
              return (
                <button
                  key={s}
                  className={`setup-step ${s === step ? "is-on" : ""}`}
                  onClick={() => setStep(s)}
                  aria-current={s === step}
                >
                  <span className={`setup-step-n ${done && i < 2 ? "is-done" : ""}`}>
                    {done && i < 2 ? <Check size={12} /> : i + 1}
                  </span>
                  <Icon size={14} />
                  {label}
                </button>
              );
            })}
          </nav>
          <div className="setup-content">
            {step === "compounds" && <CompoundsStep fs={flowsheet} />}
            {step === "package" && <PackageStep fs={flowsheet} />}
            {step === "units" && <UnitsStep fs={flowsheet} />}
          </div>
        </div>
        <div className="setup-foot">
          <span className="muted">
            {flowsheet.compounds.length} compound{flowsheet.compounds.length === 1 ? "" : "s"} ·{" "}
            {flowsheet.propertyPackages.map((p) => p.model).join(", ") || "no property package"} ·{" "}
            {flowsheet.unitSystem?.name ?? "SI"}
          </span>
          <div className="flex gap-2">
            {mode === "new" && index > 0 && (
              <button className="btn" onClick={() => setStep(STEPS[index - 1]!.step)}>
                Back
              </button>
            )}
            {mode === "new" && index < STEPS.length - 1 ? (
              <button
                className="btn btn-primary"
                onClick={() => setStep(STEPS[index + 1]!.step)}
                disabled={step === "compounds" && flowsheet.compounds.length === 0}
              >
                Next
              </button>
            ) : (
              <button
                className="btn btn-primary"
                onClick={onClose}
                disabled={mode === "new" && !ready}
                title={ready ? undefined : "Choose at least one compound and a property package"}
              >
                {mode === "new" ? "Finish" : "Done"}
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

/** Applies a summary-returning engine change to the store, logging a refusal. */
function useApply() {
  const log = useApp((s) => s.log);
  const [busy, setBusy] = useState(false);
  const apply = async (what: string, action: () => Promise<FlowsheetSummary>) => {
    setBusy(true);
    try {
      const summary = await action();
      useApp.setState({ flowsheet: summary });
      await useApp.getState().refresh();
      return true;
    } catch (e) {
      log("error", `${what}: ${EngineError.from(e).message}`);
      return false;
    } finally {
      setBusy(false);
    }
  };
  return { apply, busy };
}

function CompoundsStep({ fs }: { fs: FlowsheetSummary }) {
  const [query, setQuery] = useState("");
  const [hits, setHits] = useState<CompoundInfo[]>([]);
  const { apply, busy } = useApply();
  const selected = useMemo(() => new Set(fs.compounds), [fs.compounds]);

  useEffect(() => {
    let live = true;
    const t = setTimeout(() => {
      engine
        .searchCompounds(fs.id, query, 60)
        .then((r) => live && setHits(r))
        .catch(() => live && setHits([]));
    }, 150);
    return () => {
      live = false;
      clearTimeout(t);
    };
  }, [fs.id, query]);

  return (
    <div className="setup-split">
      <section className="setup-pane">
        <div className="search">
          <Search size={14} />
          <input
            autoFocus
            placeholder="Search by name, formula or CAS number…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            aria-label="Search compounds"
          />
        </div>
        <div className="setup-list" data-testid="compound-results">
          {hits.map((c) => {
            const added = selected.has(c.name);
            return (
              <div key={c.name} className="setup-row">
                <div className="min-w-0 flex-1">
                  <div className="truncate font-medium">{c.name}</div>
                  <div className="muted truncate text-[11px]">
                    {[
                      c.formula,
                      c.cas,
                      c.molecularWeight ? `${c.molecularWeight.toFixed(2)} g/mol` : null,
                      c.database,
                    ]
                      .filter(Boolean)
                      .join(" · ")}
                  </div>
                </div>
                <button
                  className={`btn ${added ? "is-on" : ""}`}
                  disabled={added || busy}
                  onClick={() => void apply("Add compound", () => engine.addCompounds(fs.id, [c.name]))}
                  aria-label={`Add ${c.name}`}
                >
                  {added ? <Check size={14} /> : <Plus size={14} />}
                  {added ? "Added" : "Add"}
                </button>
              </div>
            );
          })}
          {hits.length === 0 && <p className="muted p-3">No compound matches “{query}”.</p>}
        </div>
      </section>
      <section className="setup-pane setup-pane-side">
        <h3 className="section-title">In the simulation ({fs.compounds.length})</h3>
        <div className="setup-list" data-testid="compound-selection">
          {fs.compounds.map((name) => (
            <div key={name} className="setup-row">
              <span className="flex-1 truncate">{name}</span>
              <button
                className="btn btn-icon btn-ghost"
                disabled={busy}
                onClick={() => void apply("Remove compound", () => engine.removeCompound(fs.id, name))}
                aria-label={`Remove ${name}`}
                title="Remove from the simulation"
              >
                <X size={14} />
              </button>
            </div>
          ))}
          {fs.compounds.length === 0 && <p className="muted p-3">Add the compounds your process handles.</p>}
        </div>
      </section>
    </div>
  );
}

function PackageStep({ fs }: { fs: FlowsheetSummary }) {
  const [available, setAvailable] = useState<string[]>([]);
  const [filter, setFilter] = useState("");
  const { apply, busy } = useApply();
  useEffect(() => {
    engine
      .propertyPackages(fs.id)
      .then(setAvailable)
      .catch(() => setAvailable([]));
  }, [fs.id]);

  const f = filter.trim().toLowerCase();
  const shown = available.filter(
    (n) => !f || n.toLowerCase().includes(f) || (PACKAGE_HINTS[n] ?? "").toLowerCase().includes(f),
  );
  // The ones with a hint first: they are the common choices.
  shown.sort((a, b) => Number(!(a in PACKAGE_HINTS)) - Number(!(b in PACKAGE_HINTS)) || a.localeCompare(b));

  return (
    <div className="setup-split">
      <section className="setup-pane">
        <div className="search">
          <Search size={14} />
          <input
            placeholder="Filter property packages…"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            aria-label="Filter property packages"
          />
        </div>
        <div className="setup-list" data-testid="package-list">
          {shown.map((name) => (
            <div key={name} className="setup-row">
              <div className="min-w-0 flex-1">
                <div className="font-medium">{name}</div>
                {PACKAGE_HINTS[name] && <div className="muted text-[11px]">{PACKAGE_HINTS[name]}</div>}
              </div>
              <button
                className="btn"
                disabled={busy}
                onClick={() =>
                  void apply("Add property package", () => engine.addPropertyPackage(fs.id, name))
                }
                aria-label={`Add ${name}`}
              >
                <Plus size={14} /> Add
              </button>
            </div>
          ))}
        </div>
      </section>
      <section className="setup-pane setup-pane-side">
        <h3 className="section-title">In the simulation</h3>
        <div className="setup-list" data-testid="package-selection">
          {fs.propertyPackages.map((p, i) => (
            <div key={p.id} className="setup-row">
              <div className="min-w-0 flex-1">
                <div className="truncate">{p.model}</div>
                <div className="muted text-[11px]">{i === 0 ? "Default for new objects" : p.name}</div>
              </div>
              <button
                className="btn btn-icon btn-ghost"
                disabled={busy}
                onClick={() =>
                  void apply("Remove property package", () => engine.removePropertyPackage(fs.id, p.id))
                }
                aria-label={`Remove ${p.model}`}
              >
                <X size={14} />
              </button>
            </div>
          ))}
          {fs.propertyPackages.length === 0 && (
            <p className="muted p-3">Pick the thermodynamic model that suits your compounds.</p>
          )}
        </div>
      </section>
    </div>
  );
}

function UnitsStep({ fs }: { fs: FlowsheetSummary }) {
  const [systems, setSystems] = useState<UnitSystem[]>([]);
  const { apply, busy } = useApply();
  useEffect(() => {
    engine
      .unitSystems(fs.id)
      .then(setSystems)
      .catch(() => setSystems([]));
  }, [fs.id]);
  const current = fs.unitSystem?.name;
  return (
    <div className="setup-list setup-units" role="radiogroup" aria-label="System of units">
      {systems.map((u) => (
        <label key={u.name} className={`setup-row unit-option ${u.name === current ? "is-on" : ""}`}>
          <input
            type="radio"
            name="units"
            checked={u.name === current}
            disabled={busy}
            onChange={() => void apply("Units", () => engine.setUnitSystem(fs.id, u.name))}
          />
          <span className="w-40 font-medium">{u.name}</span>
          <span className="muted">
            {[u.temperature, u.pressure, u.massflow, u.molarflow, u.heatflow].map(unitLabel).join(" · ")}
          </span>
        </label>
      ))}
    </div>
  );
}
