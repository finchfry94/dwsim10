using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.Thermodynamics.PropertyPackages;
using DWSIM.Thermodynamics.Streams;
using DWSIM.Thermodynamics.Utilities.PetroleumCharacterization;
using DWSIM.UI.Desktop.Editors;
using Newtonsoft.Json.Linq;
using Converter = DWSIM.SharedClasses.SystemsOfUnits.Converter;
using LightEnds = DWSIM.SharedClasses.Utilities.PetroleumCharacterization.Assay.LightEnds;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// Petroleum fluids as pseudocompounds, as the C7+ characterization and distillation curve
    /// windows build them: from bulk properties, or from a distillation curve, with the light
    /// ends the analysis reports compound by compound. The pseudocompounds join the flowsheet and
    /// a new stream holds the characterized fluid.
    /// </summary>
    public sealed class PetroleumMethods
    {
        private readonly SessionStore _sessions;

        public PetroleumMethods(SessionStore sessions) { _sessions = sessions; }

        private static readonly string[] MwMethods = { "Winn (1956)", "Riazi (1986)", "Lee-Kesler (1974)" };
        private static readonly string[] TcMethods = { "Riazi-Daubert (1985)", "Riazi (2005)", "Lee-Kesler (1976)", "Farah (2006)" };
        private static readonly string[] PcMethods = { "Riazi-Daubert (1985)", "Lee-Kesler (1976)", "Farah (2006)" };
        private static readonly string[] AfMethods = { "Lee-Kesler (1976)", "Korsten (2000)" };

        /// <summary>The correlations each characterization can use, and the light-end candidates.</summary>
        [RpcMethod("petroleum.options")]
        public JObject Options(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
                return new JObject
                {
                    ["mw"] = new JArray(MwMethods),
                    ["tc"] = new JArray(TcMethods),
                    ["pc"] = new JArray(PcMethods),
                    ["omega"] = new JArray(AfMethods),
                    ["lightEnds"] = new JArray(LightEnds.Candidates(session.Inner.AvailableCompounds.Values)),
                    ["bases"] = new JArray(LightEnds.MoleBasis, LightEnds.MassBasis, LightEnds.VolumeBasis)
                };
        }

        private static string Pick(JObject o, string key, string[] allowed)
        {
            var v = o.Value<string>(key);
            if (string.IsNullOrEmpty(v)) return allowed[0];
            return allowed.Contains(v) ? v : throw new RpcException(RpcErrorCodes.InvalidParams, "'" + v + "' is not one of " + string.Join(", ", allowed) + ".");
        }

        private static string Name(JObject o, IFlowsheet fs)
        {
            var name = (o.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) name = "OIL";
            if (fs.SimulationObjects.Values.Any(x => x.GraphicObject?.Tag == name))
                throw new RpcException(RpcErrorCodes.InvalidParams, "There is already an object called '" + name + "'; name the assay differently.");
            if (fs.AvailableCompounds.Keys.Any(k => k.StartsWith(name + "_", StringComparison.Ordinal)))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Pseudocompounds named after '" + name + "' already exist; name the assay differently.");
            return name;
        }

        /// <summary>Light ends as {compound, percent} rows, checked against the database.</summary>
        private static (List<string> names, List<double> fractions, string basis) LightEndRows(IFlowsheet fs, JObject o)
        {
            var names = new List<string>();
            var fractions = new List<double>();
            foreach (var row in o["lightEnds"] as JArray ?? new JArray())
            {
                var compound = row.Value<string>("compound");
                if (!fs.AvailableCompounds.ContainsKey(compound ?? ""))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "No compound '" + compound + "' in the database.");
                names.Add(compound);
                fractions.Add((row.Value<double?>("percent") ?? 0) / 100.0);
            }
            var basis = o.Value<string>("lightEndsBasis") ?? LightEnds.MoleBasis;
            if (!LightEnds.Bases.Contains(basis)) throw new RpcException(RpcErrorCodes.InvalidParams, "The light ends' basis is Mole, Mass or Volume.");
            return (names, fractions, basis);
        }

        /// <summary>A light end's specific gravity at 60 F, which only the volume basis needs.</summary>
        private static double SpecificGravity(IFlowsheet fs, ICompoundConstantProperties cp, string basis)
        {
            if (basis != LightEnds.VolumeBasis) return 0.0;
            var pp = fs.PropertyPackages.Count > 0 ? (PropertyPackage)fs.PropertyPackages.Values.First() : new PengRobinsonPropertyPackage();
            var density = pp.AUX_LIQDENSi(cp, 288.706);
            if (!(density > 0) || double.IsInfinity(density))
                throw new RpcException(RpcErrorCodes.InvalidParams, "The liquid density of '" + cp.Name + "' at 15.6 °C cannot be computed; give the light ends on the mole or mass basis.");
            return density / 999.0;
        }

        /// <summary>
        /// A C7+ (or any plus) fraction from its bulk molar weight, specific gravity and mean
        /// boiling point (any of them), split into pseudocompounds by GenerateCompounds.
        /// </summary>
        [RpcMethod("petroleum.bulk")]
        public JObject Bulk(string flowsheetId, JObject assay)
        {
            if (assay == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the assay.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var su = fs.FlowsheetOptions.SelectedUnitSystem;
                var name = Name(assay, fs);
                double? Positive(string key) => assay.Value<double?>(key) is double v && v > 0 ? v : (double?)null;
                double? Temperature(string key) => Positive(key) is double v ? Converter.ConvertToSI(assay.Value<string>("temperatureUnit") ?? su.temperature, v) : (double?)null;
                var mw = Positive("molarWeight");
                var sg = Positive("specificGravity");
                var nbp = Temperature("boilingPoint");
                if (mw == null && sg == null && nbp == null)
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Give at least one of the molar weight, the specific gravity and the mean boiling point.");
                var count = assay.Value<int?>("count") ?? 10;
                if (count < 1 || count > 100) throw new RpcException(RpcErrorCodes.InvalidParams, "Between 1 and 100 pseudocompounds.");
                var mw0 = Positive("lightestMolarWeight") ?? 80.0;
                var sg0 = Positive("lightestSpecificGravity") ?? 0.70;
                var nbp0 = Temperature("lightestBoilingPoint") ?? 333.0;
                var (leNames, leFractions, leBasis) = LightEndRows(fs, assay);
                if (leNames.Count > 0)
                {
                    var props = leNames.Select(n => fs.AvailableCompounds[n]).ToList();
                    var issues = LightEnds.ValidateAgainstPlusFraction(leNames, props.Select(c => c.Molar_Weight).ToList(), props.Select(c => c.IsPF == 1).ToList(), mw0);
                    var blocking = LightEnds.BlockingMessage(issues);
                    if (!string.IsNullOrEmpty(blocking)) throw new RpcException(RpcErrorCodes.InvalidParams, blocking);
                }

                Dictionary<string, ICompound> comps;
                try
                {
                    comps = new GenerateCompounds().GenerateCompounds(name, count,
                        Pick(assay, "tcMethod", TcMethods), Pick(assay, "pcMethod", PcMethods), Pick(assay, "omegaMethod", AfMethods), Pick(assay, "mwMethod", MwMethods),
                        assay.Value<bool?>("adjustOmega") ?? true, assay.Value<bool?>("adjustRackett") ?? true,
                        mw, sg, nbp, null, null, 38 + 273.15, 98.9 + 273.15, mw0, sg0, nbp0);
                }
                catch (Exception ex) { throw new RpcException(RpcErrorCodes.EngineError, "The characterization failed: " + (ex.InnerException ?? ex).Message); }

                var light = new Dictionary<string, double>();
                if (leNames.Count > 0)
                {
                    var props = leNames.Select(n => fs.AvailableCompounds[n]).ToList();
                    var pseudos = comps.Values.ToList();
                    double[] definedx = null, pseudox = null;
                    LightEnds.Combine(leFractions.ToArray(), props.Select(c => c.Molar_Weight).ToArray(),
                        props.Select(c => SpecificGravity(fs, c, leBasis)).ToArray(), leBasis,
                        pseudos.Select(c => c.MoleFraction.GetValueOrDefault()).ToArray(),
                        pseudos.Select(c => c.ConstantProperties.Molar_Weight).ToArray(),
                        pseudos.Select(c => c.ConstantProperties.PF_SG.GetValueOrDefault()).ToArray(),
                        ref definedx, ref pseudox);
                    for (int p = 0; p < pseudos.Count; p++) pseudos[p].MoleFraction = pseudox[p];
                    for (int i = 0; i < leNames.Count; i++) light[leNames[i]] = (light.TryGetValue(leNames[i], out var x) ? x : 0) + definedx[i];
                }
                session.Record("Characterize " + name);
                return Add(session, name, comps.Values.ToList(), light);
            }
        }

        /// <summary>
        /// A crude or cut from its distillation curve (TBP, ASTM D86, D1160 or D2887; percent
        /// distilled against temperature, with optional molar weight and specific gravity per
        /// point), through DistCurveCharacterizer.
        /// </summary>
        [RpcMethod("petroleum.curve")]
        public JObject Curve(string flowsheetId, JObject assay)
        {
            if (assay == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the assay.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var su = fs.FlowsheetOptions.SelectedUnitSystem;
                var name = Name(assay, fs);
                var tunit = assay.Value<string>("temperatureUnit") ?? su.temperature;
                var points = (assay["points"] as JArray ?? new JArray())
                    .Select(p => (pct: p.Value<double?>("percent"), t: p.Value<double?>("temperature"), mw: p.Value<double?>("molarWeight"), sg: p.Value<double?>("specificGravity")))
                    .Where(p => p.pct != null && p.t != null).OrderBy(p => p.pct).ToList();
                if (points.Count < 2) throw new RpcException(RpcErrorCodes.InvalidParams, "Give at least two curve points.");
                if (points.Any(p => p.pct < 0 || p.pct > 100)) throw new RpcException(RpcErrorCodes.InvalidParams, "The percent distilled runs from 0 to 100.");
                for (int i = 1; i < points.Count; i++)
                    if (Converter.ConvertToSI(tunit, points[i].t.Value) <= Converter.ConvertToSI(tunit, points[i - 1].t.Value))
                        throw new RpcException(RpcErrorCodes.InvalidParams, "The temperature has to rise along the curve.");
                var hasMw = points.All(p => p.mw is double v && v > 0);
                var hasSg = points.All(p => p.sg is double v && v > 0);

                var c = new DistCurveCharacterizer
                {
                    Flowsheet = fs,
                    assayname = name,
                    tbpcurvetype = Math.Max(0, Array.IndexOf(new[] { "TBP", "D86", "D1160", "D2887" }, assay.Value<string>("curveType") ?? "TBP")),
                    curvebasis = Math.Max(0, Array.IndexOf(new[] { "LiquidVolume", "Mole", "Mass" }, assay.Value<string>("curveBasis") ?? "LiquidVolume")),
                    pseudomode = 0,
                    pseudocuts = assay.Value<int?>("count") ?? 10,
                    hasmwc = hasMw,
                    hassgc = hasSg,
                    mwb = assay.Value<double?>("molarWeight") ?? 0,
                    sgb = assay.Value<double?>("specificGravity") ?? 0,
                    Tccorr = Pick(assay, "tcMethod", TcMethods),
                    Pccorr = Pick(assay, "pcMethod", PcMethods),
                    AFcorr = Pick(assay, "omegaMethod", AfMethods),
                    MWcorr = Pick(assay, "mwMethod", MwMethods),
                };
                if (c.pseudocuts < 2 || c.pseudocuts > 100) throw new RpcException(RpcErrorCodes.InvalidParams, "Between 2 and 100 pseudocompounds.");
                foreach (var p in points)
                {
                    c.cb.Add(p.pct.Value / 100.0);
                    c.tbp.Add(Converter.ConvertToSI(tunit, p.t.Value));
                    if (hasMw) c.mwc.Add(p.mw.Value);
                    if (hasSg) c.sgc.Add(p.sg.Value);
                }
                var (leNames, leFractions, leBasis) = LightEndRows(fs, assay);
                c.lightEndsCompounds = leNames;
                c.lightEndsFractions = leFractions;
                c.lightEndsBasis = leBasis;
                c.lightEndsIncludedInCurve = assay.Value<bool?>("lightEndsInCurve") ?? false;
                var blocking = LightEnds.BlockingMessage(c.ValidateLightEnds());
                if (!string.IsNullOrEmpty(blocking)) throw new RpcException(RpcErrorCodes.InvalidParams, blocking);
                var errors = new List<string>();
                c.OnError = m => errors.Add(m);

                Dictionary<string, DWSIM.Thermodynamics.BaseClasses.Compound> comps;
                try { comps = c.GenerateCompounds(su); }
                catch (Exception ex) { throw new RpcException(RpcErrorCodes.EngineError, "The characterization failed: " + (ex.InnerException ?? ex).Message); }
                if (comps == null || comps.Count == 0) throw new RpcException(RpcErrorCodes.EngineError, "The characterization produced no compounds" + (errors.Count > 0 ? ": " + errors[0] : "."));
                session.Record("Characterize " + name);
                // The assay itself is kept with the simulation, as the curve window keeps it.
                try
                {
                    var options = (DWSIM.SharedClasses.DWSIM.Flowsheet.FlowsheetVariables)fs.FlowsheetOptions;
                    options.PetroleumAssays ??= new Dictionary<string, DWSIM.SharedClasses.Utilities.PetroleumCharacterization.Assay.Assay>();
                    var stored = c.BuildAssay();
                    stored.Name = name;
                    options.PetroleumAssays[name] = stored;
                }
                catch { /* the compounds and the stream are what matters */ }
                var result = Add(session, name, comps.Values.Cast<ICompound>().ToList(), c.LightEndsMoleFractions);
                result["warnings"] = new JArray(errors.Distinct().Take(5));
                return result;
            }
        }

        /// <summary>
        /// Adds the pseudocompounds (and selects the light ends) and a stream holding the fluid at
        /// the fractions the characterization gave; returns them with their properties.
        /// </summary>
        private static JObject Add(Session session, string name, List<ICompound> pseudos, IDictionary<string, double> light)
        {
            var fs = session.Inner;
            foreach (var comp in pseudos) CompoundMethods.Add(fs, comp.ConstantProperties);
            foreach (var le in light.Keys) CompoundMethods.Add(fs, fs.AvailableCompounds[le]);
            var ms = (MaterialStream)fs.AddObject(ObjectType.MaterialStream, 100, 100, name);
            var total = pseudos.Sum(c => c.MoleFraction.GetValueOrDefault()) + light.Values.Sum();
            if (!(total > 0)) total = 1;
            foreach (var c in ms.Phases[0].Compounds.Values) { c.MoleFraction = 0.0; c.MassFraction = 0.0; }
            foreach (var c in pseudos) ms.Phases[0].Compounds[c.Name].MoleFraction = c.MoleFraction.GetValueOrDefault() / total;
            foreach (var le in light) ms.Phases[0].Compounds[le.Key].MoleFraction = le.Value / total;
            var mix = ms.Phases[0].Compounds.Values.Sum(c => c.MoleFraction.GetValueOrDefault() * c.ConstantProperties.Molar_Weight);
            foreach (var c in ms.Phases[0].Compounds.Values)
                c.MassFraction = mix > 0 ? c.MoleFraction.GetValueOrDefault() * c.ConstantProperties.Molar_Weight / mix : 0.0;
            JToken Num(double? v) => v is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? new JValue(d) : JValue.CreateNull();
            return new JObject
            {
                ["stream"] = ms.Name,
                ["tag"] = ms.GraphicObject.Tag,
                ["compounds"] = new JArray(pseudos.Select(c => new JObject
                {
                    ["name"] = c.Name,
                    ["moleFraction"] = Num(c.MoleFraction.GetValueOrDefault() / total),
                    ["molarWeight"] = Num(c.ConstantProperties.Molar_Weight),
                    ["specificGravity"] = Num(c.ConstantProperties.PF_SG),
                    ["boilingPoint"] = Num(c.ConstantProperties.NBP),
                    ["criticalTemperature"] = Num(c.ConstantProperties.Critical_Temperature),
                    ["criticalPressure"] = Num(c.ConstantProperties.Critical_Pressure),
                    ["acentricFactor"] = Num(c.ConstantProperties.Acentric_Factor)
                })),
                ["lightEnds"] = new JObject(light.Select(l => new JProperty(l.Key, l.Value / total))),
                ["summary"] = FlowsheetMethods.Summary(session)
            };
        }
    }
}
