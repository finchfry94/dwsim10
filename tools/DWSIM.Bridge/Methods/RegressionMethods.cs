using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.SharedClasses.DataRegression.Engine;
using DWSIM.SharedClasses.DataRegression.Models;
using DWSIM.SharedClasses.DataRegression.Reporting;
using DWSIM.Thermodynamics.PropertyPackages;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// Binary interaction parameters regressed from phase-equilibrium data, as DataRegressionWindow
    /// does it with the engine's RegressionEngine; the result can be written into a property
    /// package of the flowsheet, as the package editor's Transfer does.
    /// </summary>
    public sealed class RegressionMethods
    {
        private readonly SessionStore _sessions;
        private readonly INotifier _notifier;

        public RegressionMethods(SessionStore sessions, INotifier notifier)
        {
            _sessions = sessions;
            _notifier = notifier;
        }

        private static readonly string[] Methods =
        {
            "IPOPT", "Limited Memory BFGS", "Truncated Newton", "Nelder-Mead Simplex Downhill",
            "Particle Swarm", "Particle Swarm Optimization", "Differential Evolution",
            "Local Unimodal Sampling", "Gradient Descent", "Many Optimizing Liaisons", "Mesh"
        };

        private static readonly string[] Objectives =
        {
            "Least Squares (min T/P)", "Least Squares (min y/x)", "Least Squares (min T/P+y/x)",
            "Weighted Least Squares (min T/P)", "Weighted Least Squares (min y/x)",
            "Weighted Least Squares (min T/P+y/x)", "Chi Square"
        };

        /// <summary>The data a regression can fit, as the window offers them (vapor-liquid only here).</summary>
        private static readonly DataType[] Types = { DataType.Txy, DataType.Pxy, DataType.TPxy };

        /// <summary>The models with their parameters and default bounds, the methods and the objectives.</summary>
        [RpcMethod("regression.models")]
        public JObject Models()
            => new JObject
            {
                ["models"] = new JArray(ModelRegistry.AllModels().Select(m => new JObject
                {
                    ["name"] = m.Name,
                    ["package"] = m.PropertyPackageName,
                    ["idealVaporOption"] = m.AllowIdealVaporOption,
                    ["parameters"] = new JArray(m.DefaultRows.Select(r => new JObject
                    {
                        ["label"] = r.Label,
                        ["lower"] = r.LowerBound,
                        ["initial"] = r.InitialValue,
                        ["upper"] = r.UpperBound,
                        ["fixed"] = r.Fixed
                    }))
                })),
                ["methods"] = new JArray(Methods),
                ["objectives"] = new JArray(Objectives),
                ["dataTypes"] = new JArray(Types.Select(t => t.ToString()))
            };

        /// <summary>
        /// Fits the model's parameters to the data. Points carry T and P in the units given (by
        /// default °C and bar, as the engine's case stores them), x1 and y1 as mole fractions of
        /// the first compound. Cancellable; returns the parameters, the per-point comparison and
        /// a chart of the data against the model.
        /// </summary>
        [RpcMethod("regression.run")]
        public JObject Run(string flowsheetId, JObject study, CancellationToken cancellation = default)
        {
            if (study == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Describe the regression.");
            var session = _sessions.Get(flowsheetId);
            IDictionary<string, ICompoundConstantProperties> compounds;
            lock (session.Gate) compounds = session.Inner.AvailableCompounds;

            var c = new RegressionCase
            {
                comp1 = study.Value<string>("comp1"),
                comp2 = study.Value<string>("comp2"),
                model = study.Value<string>("model") ?? "Peng-Robinson",
                method = study.Value<string>("method") ?? "IPOPT",
                objfunction = study.Value<string>("objective") ?? Objectives[0],
                tunit = study.Value<string>("temperatureUnit") ?? "C",
                punit = study.Value<string>("pressureUnit") ?? "bar",
                tolerance = study.Value<double?>("tolerance") ?? 1e-5,
                maxits = study.Value<double?>("maxIterations") ?? 250,
                idealvapormodel = study.Value<bool?>("idealVapor") ?? true,
                title = study.Value<string>("title") ?? ""
            };
            if (string.IsNullOrEmpty(c.comp1) || string.IsNullOrEmpty(c.comp2) || c.comp1 == c.comp2)
                throw new RpcException(RpcErrorCodes.InvalidParams, "Pick two different compounds.");
            foreach (var name in new[] { c.comp1, c.comp2 })
                if (!compounds.ContainsKey(name)) throw new RpcException(RpcErrorCodes.InvalidParams, "No compound '" + name + "' in the database.");
            var model = ModelRegistry.GetDefinition(c.model) ?? throw new RpcException(RpcErrorCodes.InvalidParams, "No model '" + c.model + "'.");
            if (!Methods.Contains(c.method)) throw new RpcException(RpcErrorCodes.InvalidParams, "No method '" + c.method + "'.");
            if (!Objectives.Contains(c.objfunction)) throw new RpcException(RpcErrorCodes.InvalidParams, "No objective '" + c.objfunction + "'.");
            c.datatype = Enum.TryParse<DataType>(study.Value<string>("dataType") ?? "", out var dt) && Types.Contains(dt) ? dt : DataType.Pxy;

            var points = (study["points"] as JArray ?? new JArray()).ToList();
            if (points.Count < 2) throw new RpcException(RpcErrorCodes.InvalidParams, "Give at least two data points.");
            foreach (var p in points)
            {
                double Field(string key) => p.Value<double?>(key) ?? 0.0;
                var x1 = Field("x1");
                var y1 = Field("y1");
                if (x1 < 0 || x1 > 1 || y1 < 0 || y1 > 1) throw new RpcException(RpcErrorCodes.InvalidParams, "Mole fractions lie between 0 and 1.");
                c.checkp.Add(p.Value<bool?>("use") ?? true);
                c.tp.Add(Field("t"));
                c.pp.Add(Field("p"));
                c.x1p.Add(x1);
                c.x2p.Add(0.0);
                c.yp.Add(y1);
                c.tl.Add(0.0);
                c.ts.Add(0.0);
            }

            var parameters = (study["parameters"] as JArray)?.ToList();
            for (int i = 0; i < model.ParameterCount && i < 3; i++)
            {
                var row = model.DefaultRows[i];
                var given = parameters != null && i < parameters.Count ? parameters[i] : null;
                CaseAccessor.SetLowerBound(c, i, given?.Value<double?>("lower") ?? row.LowerBound);
                CaseAccessor.SetInitial(c, i, given?.Value<double?>("initial") ?? row.InitialValue);
                CaseAccessor.SetUpperBound(c, i, given?.Value<double?>("upper") ?? row.UpperBound);
                CaseAccessor.SetFixed(c, i, given?.Value<bool?>("fixed") ?? row.Fixed);
            }

            var engine = new RegressionEngine { Output = false, EnableParallel = false };
            engine.TDep.Enabled = false;
            var history = new List<double>();
            engine.IterationCompleted += (_, e) =>
            {
                history.Add(e.FunctionValue);
                _notifier.Notify("study/progress", new JObject
                {
                    ["flowsheetId"] = flowsheetId,
                    ["study"] = "regression",
                    ["done"] = e.Iteration,
                    ["total"] = (int)c.maxits,
                    ["text"] = $"Iteration {e.Iteration}: {e.FunctionValue.ToString("G6", CultureInfo.InvariantCulture)}"
                });
            };
            string error = null;
            using (cancellation.Register(engine.Cancel))
            {
                try { engine.Run(c, CaseAccessor.GetInitialVector(c, model.ParameterCount), compounds); }
                catch (Exception ex)
                {
                    while (ex.InnerException != null) ex = ex.InnerException;
                    error = ex.Message;
                }
            }
            var objective = double.NaN;
            if (engine.RegressedParameters.Count > 0)
            {
                // The objective, the comparison and the chart at the fitted parameters.
                var best = model.DefaultRows.Select(r => engine.RegressedParameters.TryGetValue(r.Label, out var v) ? v : r.InitialValue).ToArray();
                try
                {
                    objective = engine.FunctionValue(best);
                    engine.EvaluateOnce(c, best);
                }
                catch { /* the fit stands; only the comparison is missing */ }
            }

            JToken Num(double v) => double.IsNaN(v) || double.IsInfinity(v) ? JValue.CreateNull() : new JValue(v);
            var columns = StatsColumns.VisibleFor(c.datatype);
            var stats = RegressionStatsBuilder.Build(c) ?? new List<RegressionStatsRow>();
            var chartData = ChartDataBuilder.Build(c, c.title);
            return new JObject
            {
                ["cancelled"] = cancellation.IsCancellationRequested || engine.IsCancelled,
                ["error"] = error,
                ["model"] = c.model,
                ["package"] = model.PropertyPackageName,
                ["comp1"] = c.comp1,
                ["comp2"] = c.comp2,
                ["parameters"] = new JObject(engine.RegressedParameters.Select(p => new JProperty(p.Key, Num(p.Value)))),
                ["iterations"] = engine.IterationCount,
                // The engine reports a failed evaluation as double.MaxValue.
                ["objective"] = objective < 1e300 ? Num(objective) : JValue.CreateNull(),
                ["table"] = new JObject
                {
                    ["columns"] = new JArray(columns.Select(StatsColumns.HeaderFor)),
                    ["rows"] = new JArray(stats.Select(r => new JArray(columns.Select(col => Num(StatsColumns.ValueOf(r, col))))))
                },
                ["chart"] = chartData == null ? JValue.CreateNull() : Chart(chartData)
            };
        }

        /// <summary>The engine's chart payload in the shape the frontend draws.</summary>
        private static JObject Chart(RegressionChartData d)
        {
            var xs = new[] { d.Px, d.Px, d.Px2, d.Px3, d.Px4 };
            var ys = new[] { d.Py1, d.Py2, d.Py3, d.Py4, d.Py5 };
            var titles = new[] { d.Y1Title, d.Y2Title, d.Y3Title, d.Y4Title, d.Y5Title };
            var series = new JArray();
            for (int i = 0; i < ys.Length; i++)
            {
                var y = Doubles(ys[i]);
                var x = Doubles(xs[i]);
                if (x.Count == 0) x = Doubles(d.Px);
                var n = Math.Min(x.Count, y.Count);
                var pairs = Enumerable.Range(0, n).Where(k => !double.IsNaN(x[k]) && !double.IsNaN(y[k]) && !double.IsInfinity(y[k])).ToList();
                if (pairs.Count == 0) continue;
                var style = i < d.CurveStyles.Count ? d.CurveStyles[i] : CurveStyle.PointsAndLine;
                series.Add(new JObject
                {
                    ["name"] = string.IsNullOrEmpty(titles[i]) ? "Series " + (i + 1) : titles[i],
                    ["kind"] = style == CurveStyle.PointsOnly ? "point" : "line",
                    ["x"] = new JArray(pairs.Select(k => x[k])),
                    ["y"] = new JArray(pairs.Select(k => y[k]))
                });
            }
            return new JObject
            {
                ["title"] = string.IsNullOrWhiteSpace(d.Title) ? "Data and model" : d.Title,
                ["x"] = new JObject { ["label"] = d.XAxisTitle ?? "x", ["unit"] = null },
                ["y"] = new JObject { ["label"] = d.YAxisTitle ?? "y", ["unit"] = null },
                ["series"] = series
            };
        }

        private static List<double> Doubles(System.Collections.ArrayList list)
        {
            var result = new List<double>();
            if (list == null) return result;
            foreach (var item in list)
            {
                try { result.Add(Convert.ToDouble(item, CultureInfo.InvariantCulture)); }
                catch { result.Add(double.NaN); }
            }
            return result;
        }

        /// <summary>
        /// Writes regressed parameters into a property package of the flowsheet: kij for the cubic
        /// equations of state, A/B/C (and alpha) for NRTL and UNIQUAC, both ways round as their
        /// editor keeps them. One undo step.
        /// </summary>
        [RpcMethod("regression.apply")]
        public JObject Apply(string flowsheetId, string packageId, string comp1, string comp2, JObject parameters)
        {
            if (parameters == null || !parameters.HasValues) throw new RpcException(RpcErrorCodes.InvalidParams, "There are no parameters to apply.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                if (!fs.PropertyPackages.TryGetValue(packageId ?? "", out var ipp)) throw new RpcException(RpcErrorCodes.InvalidParams, "No property package '" + packageId + "'.");
                foreach (var name in new[] { comp1, comp2 })
                    if (!fs.SelectedCompounds.ContainsKey(name ?? "")) throw new RpcException(RpcErrorCodes.InvalidParams, "'" + name + "' is not in the simulation.");
                double? P(string key) => parameters.Value<double?>(key);
                session.Record("Apply regressed parameters to " + ipp.Tag);
                switch (ipp)
                {
                    case PengRobinsonPropertyPackage pr:
                        SetKij(pr.m_pr.InteractionParameters, comp1, comp2, P("kij") ?? throw Missing("kij"));
                        break;
                    case SRKPropertyPackage srk:
                        SetKij(srk.m_pr.InteractionParameters, comp1, comp2, P("kij") ?? throw Missing("kij"));
                        break;
                    case NRTLPropertyPackage nrtl:
                    {
                        var ip = nrtl.m_uni.InteractionParameters;
                        if (!ip.ContainsKey(comp1)) ip[comp1] = new Dictionary<string, DWSIM.Thermodynamics.PropertyPackages.Auxiliary.NRTL_IPData>();
                        if (!ip[comp1].ContainsKey(comp2)) ip[comp1][comp2] = new DWSIM.Thermodynamics.PropertyPackages.Auxiliary.NRTL_IPData();
                        var d = ip[comp1][comp2];
                        d.A12 = P("A12") ?? d.A12; d.A21 = P("A21") ?? d.A21;
                        d.B12 = P("B12") ?? d.B12; d.B21 = P("B21") ?? d.B21;
                        d.C12 = P("C12") ?? d.C12; d.C21 = P("C21") ?? d.C21;
                        d.alpha12 = P("alpha12") ?? d.alpha12;
                        break;
                    }
                    case UNIQUACPropertyPackage uniquac:
                    {
                        var ip = uniquac.m_uni.InteractionParameters;
                        if (!ip.ContainsKey(comp1)) ip[comp1] = new Dictionary<string, DWSIM.Thermodynamics.PropertyPackages.Auxiliary.UNIQUAC_IPData>();
                        if (!ip[comp1].ContainsKey(comp2)) ip[comp1][comp2] = new DWSIM.Thermodynamics.PropertyPackages.Auxiliary.UNIQUAC_IPData();
                        var d = ip[comp1][comp2];
                        d.A12 = P("A12") ?? d.A12; d.A21 = P("A21") ?? d.A21;
                        d.B12 = P("B12") ?? d.B12; d.B21 = P("B21") ?? d.B21;
                        d.C12 = P("C12") ?? d.C12; d.C21 = P("C21") ?? d.C21;
                        break;
                    }
                    default:
                        throw new RpcException(RpcErrorCodes.InvalidParams, ipp.Tag + " parameters cannot be written here yet; Peng-Robinson, SRK, NRTL and UNIQUAC can.");
                }
                foreach (var obj in fs.SimulationObjects.Values) obj.Calculated = false;
                return new JObject { ["applied"] = true, ["package"] = ipp.Tag };
            }
        }

        private static RpcException Missing(string key) => new RpcException(RpcErrorCodes.InvalidParams, "The parameters have no '" + key + "' for this package.");

        private static void SetKij(Dictionary<string, Dictionary<string, DWSIM.Thermodynamics.PropertyPackages.Auxiliary.PR_IPData>> ip, string c1, string c2, double kij)
        {
            foreach (var (a, b) in new[] { (c1, c2), (c2, c1) })
            {
                if (!ip.ContainsKey(a)) ip[a] = new Dictionary<string, DWSIM.Thermodynamics.PropertyPackages.Auxiliary.PR_IPData>();
                if (!ip[a].ContainsKey(b)) ip[a][b] = new DWSIM.Thermodynamics.PropertyPackages.Auxiliary.PR_IPData();
                ip[a][b].kij = kij;
            }
        }
    }
}
