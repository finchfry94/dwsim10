using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.SharedClasses.SystemsOfUnits;
using DWSIM.Thermodynamics.PropertyPackages;
using DWSIM.Thermodynamics.Streams;
using Newtonsoft.Json.Linq;
using sc = DWSIM.Thermodynamics.ShortcutUtilities;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// Results and analysis: the stream table, the simulation report, and phase and binary
    /// envelopes. Envelopes go through the engine's ShortcutUtilities.Calculation, as the
    /// Avalonia windows do, and come back as plain series for the frontend to chart.
    /// </summary>
    public sealed class AnalysisMethods
    {
        private readonly SessionStore _sessions;

        public AnalysisMethods(SessionStore sessions) { _sessions = sessions; }

        // ------------------------------------------------------------------ stream table

        /// <summary>
        /// Every material stream's state, in the flowsheet's units: one row per property, with the
        /// value of each stream. <paramref name="basis"/> picks mole or mass fractions for the
        /// composition rows.
        /// </summary>
        [RpcMethod("streams.table")]
        public JObject StreamTable(string flowsheetId, string basis = "mole")
        {
            var mass = string.Equals(basis, "mass", StringComparison.OrdinalIgnoreCase);
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var su = fs.FlowsheetOptions.SelectedUnitSystem;
                var streams = fs.SimulationObjects.Values.OfType<MaterialStream>()
                    .Where(s => s.GraphicObject != null)
                    .OrderBy(s => s.GraphicObject.Tag, NaturalOrder.Instance)
                    .ToList();

                var rows = new JArray();
                void Row(string key, string label, string unit, Func<MaterialStream, double?> si, string group = "conditions")
                {
                    var values = new JObject();
                    foreach (var s in streams)
                    {
                        double? v;
                        try { v = si(s); } catch { v = null; }
                        values[s.Name] = v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value)
                            ? (JToken)(string.IsNullOrEmpty(unit) ? v.Value : Converter.ConvertFromSI(unit, v.Value))
                            : JValue.CreateNull();
                    }
                    rows.Add(new JObject
                    {
                        ["key"] = key,
                        ["label"] = label,
                        ["unit"] = string.IsNullOrEmpty(unit) ? null : unit,
                        ["group"] = group,
                        ["values"] = values
                    });
                }
                PhaseProps Mix(MaterialStream s) => new PhaseProps(s.Phases[0].Properties);

                Row("temperature", "Temperature", su.temperature, s => Mix(s).P.temperature);
                Row("pressure", "Pressure", su.pressure, s => Mix(s).P.pressure);
                Row("massFlow", "Mass flow", su.massflow, s => Mix(s).P.massflow);
                Row("molarFlow", "Molar flow", su.molarflow, s => Mix(s).P.molarflow);
                Row("volumetricFlow", "Volumetric flow", su.volumetricFlow, s => Mix(s).P.volumetric_flow);
                Row("vaporFraction", "Vapor fraction", null, s => GraphMethods.VaporFraction(s));
                Row("enthalpy", "Specific enthalpy", su.enthalpy, s => Mix(s).P.enthalpy);
                Row("entropy", "Specific entropy", su.entropy, s => Mix(s).P.entropy);
                Row("density", "Density", su.density, s => Mix(s).P.density);
                Row("molecularWeight", "Molecular weight", su.molecularWeight, s => Mix(s).P.molecularWeight);
                foreach (var compound in fs.SelectedCompounds.Keys)
                    Row("x:" + compound, compound, null, s =>
                    {
                        if (!s.Phases[0].Compounds.TryGetValue(compound, out var c)) return null;
                        return mass ? c.MassFraction : c.MoleFraction;
                    }, mass ? "massFractions" : "moleFractions");

                return new JObject
                {
                    ["streams"] = new JArray(streams.Select(s => new JObject
                    {
                        ["id"] = s.Name,
                        ["tag"] = s.GraphicObject.Tag,
                        ["calculated"] = s.Calculated
                    })),
                    ["rows"] = rows,
                    ["basis"] = mass ? "mass" : "mole"
                };
            }
        }

        private readonly struct PhaseProps
        {
            public readonly IPhaseProperties P;
            public PhaseProps(IPhaseProperties p) { P = p; }
        }

        /// <summary>Stream tags sorted as people read them: "2" before "10".</summary>
        private sealed class NaturalOrder : IComparer<string>
        {
            public static readonly NaturalOrder Instance = new NaturalOrder();

            public int Compare(string a, string b)
            {
                if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    return x.CompareTo(y);
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ------------------------------------------------------------------ report

        /// <summary>
        /// The simulation report as a standalone HTML page, from the engine's report builder (the
        /// one the Avalonia report window uses). <paramref name="objectIds"/> limits it to those
        /// objects; <paramref name="sections"/> switches stream sections off by name.
        /// </summary>
        [RpcMethod("report.html")]
        public JObject Report(string flowsheetId, string[] objectIds = null, JObject sections = null)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var options = new DWSIM.SharedClasses.Reports.ReportOptions
                {
                    ObjectNames = objectIds?.Length > 0 ? objectIds.Select(id => session.Object(id).Name).ToList() : null,
                    ProductVersion = "DWSIM (desktop)"
                };
                bool On(string name) => sections?[name]?.Type != JTokenType.Boolean || sections.Value<bool>(name);
                options.IncludeConditions = On("conditions");
                options.IncludeCompositions = On("compositions");
                options.IncludeMixtureProps = On("mixture");
                options.IncludeVaporProps = On("vapor");
                options.IncludeLiquidMixtureProps = On("liquid");
                options.IncludeLiquid1Props = On("liquid1");
                options.IncludeLiquid2Props = On("liquid2");
                options.IncludeAqueousProps = On("aqueous");
                options.IncludeSolidProps = On("solid");
                try
                {
                    var html = new DWSIM.SharedClasses.Reports.SimulationReportBuilder(session.Inner).GenerateHTML(options);
                    return new JObject { ["html"] = html };
                }
                catch (Exception ex)
                {
                    throw new RpcException(RpcErrorCodes.EngineError, "The report could not be built: " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------ envelopes

        private static readonly Dictionary<string, sc.CalculationType> Diagrams = new Dictionary<string, sc.CalculationType>(StringComparer.OrdinalIgnoreCase)
        {
            ["PT"] = sc.CalculationType.PhaseEnvelopePT,
            ["PH"] = sc.CalculationType.PhaseEnvelopePH,
            ["PS"] = sc.CalculationType.PhaseEnvelopePS,
            ["TH"] = sc.CalculationType.PhaseEnvelopeTH,
            ["TS"] = sc.CalculationType.PhaseEnvelopeTS,
            ["VT"] = sc.CalculationType.PhaseEnvelopeVT,
            ["VP"] = sc.CalculationType.PhaseEnvelopeVP
        };

        /// <summary>
        /// A stream's phase envelope. <paramref name="diagram"/> is PT, PH, PS, TH, TS, VT or VP;
        /// <paramref name="options"/> sets PhaseEnvelopeOptions by name (QualityLine,
        /// QualityValue, StabilityCurve, PhaseIdentificationCurve, ImmiscibleWater, OperatingPoint).
        /// The series pair as the Avalonia phase envelope window plots them.
        /// </summary>
        [RpcMethod("analysis.phaseEnvelope")]
        public JObject PhaseEnvelope(string flowsheetId, string streamId, string diagram = "PT", JObject options = null, CancellationToken cancellation = default)
        {
            if (!Diagrams.TryGetValue(diagram ?? "", out var type))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Pick a diagram: PT, PH, PS, TH, TS, VT or VP.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                if (!(session.Object(streamId) is MaterialStream ms))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "A phase envelope needs a material stream.");
                if (ms.Phases[0].Compounds.Values.All(c => c.MoleFraction.GetValueOrDefault() <= 0))
                    throw new RpcException(RpcErrorCodes.EngineError, ms.GraphicObject.Tag + " has no composition yet.");
                var peo = new PhaseEnvelopeOptions();
                foreach (var p in options?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    var prop = typeof(PhaseEnvelopeOptions).GetProperty(p.Name);
                    if (prop == null || !prop.CanWrite) throw new RpcException(RpcErrorCodes.InvalidParams, "Unknown envelope option: " + p.Name);
                    prop.SetValue(peo, p.Value.ToObject(prop.PropertyType));
                }
                var results = Run(new sc.Calculation(ms) { CalcType = type, PhaseEnvelopeOptions = peo }, cancellation);
                var su = session.Inner.FlowsheetOptions.SelectedUnitSystem;
                var mix = ms.Phases[0].Properties;
                var op = (T: Converter.ConvertFromSI(su.temperature, mix.temperature.GetValueOrDefault()),
                          P: Converter.ConvertFromSI(su.pressure, mix.pressure.GetValueOrDefault()),
                          H: Converter.ConvertFromSI(su.enthalpy, mix.enthalpy.GetValueOrDefault()),
                          S: Converter.ConvertFromSI(su.entropy, mix.entropy.GetValueOrDefault()));
                var chart = new Chart(results);
                var temperature = ("Temperature", su.temperature);
                var pressure = ("Pressure", su.pressure);
                var enthalpy = ("Enthalpy", su.enthalpy);
                var entropy = ("Entropy", su.entropy);
                var volume = ("Molar volume", su.molar_volume);
                var cp = chart.Curve("CP");
                switch (type)
                {
                    case sc.CalculationType.PhaseEnvelopePT:
                        chart.Axes("Pressure/temperature diagram", temperature, pressure);
                        chart.Line("Bubble points", "TB", "PB");
                        chart.Line("Dew points", "TD", "PD");
                        if (cp?.Count >= 2) chart.Point("Critical point", cp[0], cp[1]);
                        if (peo.QualityLine) chart.Line("Vapor fraction " + peo.QualityValue.ToString(CultureInfo.InvariantCulture), "TQ", "PQ");
                        if (peo.StabilityCurve) chart.Line("Stability curve", "TE", "PE");
                        if (peo.PhaseIdentificationCurve) chart.Line("Phase identification", "TI", "PI");
                        chart.Line("SLE liquidus", "TSLE1", "PSLE1");
                        chart.Line("SLE solidus", "TSLE2", "PSLE2");
                        if (peo.OperatingPoint) chart.Point("Operating point", op.T, op.P);
                        break;
                    case sc.CalculationType.PhaseEnvelopePH:
                        chart.Axes("Pressure/enthalpy diagram", enthalpy, pressure);
                        chart.Line("Bubble points", "HB", "PB");
                        chart.Line("Dew points", "HD", "PD");
                        if (peo.OperatingPoint) chart.Point("Operating point", op.H, op.P);
                        break;
                    case sc.CalculationType.PhaseEnvelopePS:
                        chart.Axes("Pressure/entropy diagram", entropy, pressure);
                        chart.Line("Bubble points", "SB", "PB");
                        chart.Line("Dew points", "SD", "PD");
                        if (peo.OperatingPoint) chart.Point("Operating point", op.S, op.P);
                        break;
                    case sc.CalculationType.PhaseEnvelopeTH:
                        chart.Axes("Temperature/enthalpy diagram", enthalpy, temperature);
                        chart.Line("Bubble points", "HB", "TB");
                        chart.Line("Dew points", "HD", "TD");
                        if (peo.OperatingPoint) chart.Point("Operating point", op.H, op.T);
                        break;
                    case sc.CalculationType.PhaseEnvelopeTS:
                        chart.Axes("Temperature/entropy diagram", entropy, temperature);
                        chart.Line("Bubble points", "SB", "TB");
                        chart.Line("Dew points", "SD", "TD");
                        if (peo.OperatingPoint) chart.Point("Operating point", op.S, op.T);
                        break;
                    case sc.CalculationType.PhaseEnvelopeVT:
                        chart.Axes("Volume/temperature diagram", temperature, volume);
                        chart.Line("Bubble points", "TB", "VB");
                        chart.Line("Dew points", "TD", "VD");
                        if (cp?.Count >= 3) chart.Point("Critical point", cp[0], cp[2]);
                        break;
                    default:
                        chart.Axes("Volume/pressure diagram", pressure, volume);
                        chart.Line("Bubble points", "PB", "VB");
                        chart.Line("Dew points", "PD", "VD");
                        if (cp?.Count >= 3) chart.Point("Critical point", cp[1], cp[2]);
                        break;
                }
                var json = chart.ToJson();
                json["subtitle"] = $"{ms.GraphicObject.Tag} · {ms.PropertyPackage?.ComponentName}";
                if (cp?.Count >= 2) json["criticalPoint"] = new JObject
                {
                    ["temperature"] = cp[0], ["pressure"] = cp[1], ["volume"] = cp.Count >= 3 ? cp[2] : (double?)null,
                    ["units"] = new JObject { ["temperature"] = su.temperature, ["pressure"] = su.pressure, ["volume"] = su.molar_volume }
                };
                return json;
            }
        }

        /// <summary>
        /// A binary envelope of two of the flowsheet's compounds: Txy at <paramref name="pressure"/>
        /// or Pxy at <paramref name="temperature"/> (in the flowsheet's units). VLE always; LLE,
        /// SLE and the critical line on request.
        /// </summary>
        [RpcMethod("analysis.binaryEnvelope")]
        public JObject BinaryEnvelope(string flowsheetId, string compound1, string compound2, string diagram = "Txy",
            string packageId = null, double? temperature = null, double? pressure = null,
            bool lle = false, bool sle = false, bool critical = false, CancellationToken cancellation = default)
        {
            var txy = !string.Equals(diagram, "Pxy", StringComparison.OrdinalIgnoreCase);
            if (!txy && !string.Equals(diagram, "Pxy", StringComparison.OrdinalIgnoreCase) && !string.Equals(diagram, "Txy", StringComparison.OrdinalIgnoreCase))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Pick a diagram: Txy or Pxy.");
            if (string.Equals(compound1, compound2, StringComparison.Ordinal))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Pick two different compounds.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var su = fs.FlowsheetOptions.SelectedUnitSystem;
                if (!fs.SelectedCompounds.TryGetValue(compound1 ?? "", out var c1) || !fs.SelectedCompounds.TryGetValue(compound2 ?? "", out var c2))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Pick two of the flowsheet's compounds.");
                var pp = (string.IsNullOrEmpty(packageId) ? fs.PropertyPackages.Values.FirstOrDefault()
                    : fs.PropertyPackages.TryGetValue(packageId, out var chosen) ? chosen : null) as PropertyPackage
                    ?? throw new RpcException(RpcErrorCodes.InvalidParams, "No such property package.");

                // A scratch stream with only the two compounds, never added to the flowsheet,
                // as the Avalonia binary envelope window builds it.
                var ms = new MaterialStream("", "");
                ms.SetFlowsheet(fs);
                ms.PropertyPackage = pp;
                foreach (var phase in ms.Phases.Values)
                    foreach (var c in new[] { c1, c2 })
                    {
                        phase.Compounds.Add(c.Name, new DWSIM.Thermodynamics.BaseClasses.Compound(c.Name, ""));
                        phase.Compounds[c.Name].ConstantProperties = c;
                    }
                var t = temperature ?? Converter.ConvertFromSI(su.temperature, 298.15);
                var p = pressure ?? Converter.ConvertFromSI(su.pressure, 101325.0);
                ms.Phases[0].Properties.temperature = Converter.ConvertToSI(su.temperature, t);
                ms.Phases[0].Properties.pressure = Converter.ConvertToSI(su.pressure, p);
                if (ms.Phases[0].Properties.pressure <= 0 || ms.Phases[0].Properties.temperature <= 0)
                    throw new RpcException(RpcErrorCodes.InvalidParams, "The temperature and pressure must be above zero.");

                var results = Run(new sc.Calculation(ms)
                {
                    CalcType = txy ? sc.CalculationType.BinaryEnvelopeTxy : sc.CalculationType.BinaryEnvelopePxy,
                    BinaryEnvelopeOptions = new object[] { "", 0, 0, true, lle, sle, critical, false }
                }, cancellation);

                var chart = new Chart(results);
                chart.Axes(txy ? $"Txy at {Fmt(p)} {su.pressure}" : $"Pxy at {Fmt(t)} {su.temperature}",
                    ("Mole fraction " + c1.Name, ""),
                    txy ? ("Temperature", su.temperature) : ("Pressure", su.pressure));
                chart.Line("Bubble points", "px", "py1");
                chart.Line("Dew points", "px", "py2");
                chart.Line("Liquid-liquid (1)", "px1l1", "py3");
                chart.Line("Liquid-liquid (2)", "px1l2", "py3");
                chart.Line("Solid-liquid (1)", "pxs1", "pys1");
                chart.Line("Solid-liquid (2)", "pxs2", "pys2");
                chart.Line("Critical line", "pxc", "pyc");
                var json = chart.ToJson();
                json["subtitle"] = $"{c1.Name} / {c2.Name} · {pp.ComponentName}";
                return json;
            }
        }

        private static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

        /// <summary>Runs a calculation, turning its failure into an engine error; a cancel stops the wait.</summary>
        private static sc.CalculationResults Run(sc.Calculation calculation, CancellationToken cancellation)
        {
            using (cancellation.Register(() => DWSIM.GlobalSettings.Settings.TaskCancellationTokenSource?.Cancel()))
            {
                sc.CalculationResults results;
                try { results = calculation.Calculate(); }
                catch (Exception ex) { throw new RpcException(RpcErrorCodes.EngineError, "The calculation failed: " + ex.Message); }
                if (cancellation.IsCancellationRequested) throw new RpcException(RpcErrorCodes.Cancelled, "Cancelled.");
                if (results.ExceptionResult != null)
                    throw new RpcException(RpcErrorCodes.EngineError, "The calculation failed: " + (results.ExceptionResult.InnerException ?? results.ExceptionResult).Message);
                return results;
            }
        }

        /// <summary>Series from a calculation's named curves, skipping the ones it did not produce.</summary>
        private sealed class Chart
        {
            private readonly sc.CalculationResults _results;
            private readonly JArray _series = new JArray();
            private string _title;
            private (string label, string unit) _x, _y;

            public Chart(sc.CalculationResults results) { _results = results; }

            public List<double> Curve(string key)
                => _results.Data != null && _results.Data.TryGetValue(key, out var list) && list != null && list.Count > 0 ? list : null;

            public void Axes(string title, (string, string) x, (string, string) y)
            {
                _title = title;
                _x = x;
                _y = y;
            }

            public void Line(string name, string xKey, string yKey)
            {
                var x = Curve(xKey);
                var y = Curve(yKey);
                if (x == null || y == null) return;
                var n = Math.Min(x.Count, y.Count);
                var xs = new JArray();
                var ys = new JArray();
                for (int i = 0; i < n; i++)
                {
                    if (!Finite(x[i]) || !Finite(y[i])) continue;
                    xs.Add(x[i]);
                    ys.Add(y[i]);
                }
                if (xs.Count == 0) return;
                _series.Add(new JObject { ["name"] = name, ["kind"] = "line", ["x"] = xs, ["y"] = ys });
            }

            public void Point(string name, double x, double y)
            {
                if (!Finite(x) || !Finite(y)) return;
                _series.Add(new JObject { ["name"] = name, ["kind"] = "point", ["x"] = new JArray(x), ["y"] = new JArray(y) });
            }

            private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

            public JObject ToJson() => new JObject
            {
                ["title"] = _title,
                ["x"] = new JObject { ["label"] = _x.label, ["unit"] = string.IsNullOrEmpty(_x.unit) ? null : _x.unit },
                ["y"] = new JObject { ["label"] = _y.label, ["unit"] = string.IsNullOrEmpty(_y.unit) ? null : _y.unit },
                ["series"] = _series,
                ["text"] = _results.TextOutput
            };
        }
    }
}
