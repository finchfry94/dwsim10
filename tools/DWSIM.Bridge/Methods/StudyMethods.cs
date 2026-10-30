using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Editors;
using DWSIM.Bridge.Rpc;
using DWSIM.GlobalSettings;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.SharedClasses.Flowsheet.Optimization;
using Newtonsoft.Json.Linq;
using Converter = DWSIM.SharedClasses.SystemsOfUnits.Converter;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// Sensitivity analysis and optimization, run on the flowsheet as the Avalonia windows run
    /// them. Their cases live in the flowsheet (and its file); a case is addressed by its
    /// position in the list.
    /// </summary>
    public sealed class StudyMethods
    {
        private readonly SessionStore _sessions;
        private readonly INotifier _notifier;

        public StudyMethods(SessionStore sessions, INotifier notifier)
        {
            _sessions = sessions;
            _notifier = notifier;
        }

        private static FlowsheetBase.FlowsheetBase Base(Session session)
            => session.Inner as FlowsheetBase.FlowsheetBase
               ?? throw new RpcException(RpcErrorCodes.EngineError, "This flowsheet keeps no studies.");

        // ------------------------------------------------------------------ variables

        /// <summary>A variable as the frontend shows it: object, property, unit and a readable label.</summary>
        private static JObject VariableJson(IFlowsheet fs, string objectId, string propId, string unit)
        {
            fs.SimulationObjects.TryGetValue(objectId ?? "", out var obj);
            var caption = obj == null || string.IsNullOrEmpty(propId) ? propId : VariablePicker.Caption(fs, obj, propId);
            return new JObject
            {
                ["object"] = string.IsNullOrEmpty(objectId) ? null : objectId,
                ["property"] = string.IsNullOrEmpty(propId) ? null : propId,
                ["unit"] = string.IsNullOrEmpty(unit) ? null : unit,
                ["label"] = obj == null ? null : obj.GraphicObject?.Tag + " · " + caption
            };
        }

        /// <summary>Checks an object and property exist and returns the object; the unit defaults to the flowsheet's.</summary>
        private static ISimulationObject Resolve(IFlowsheet fs, JToken v, string what, out string property, out string unit)
        {
            var id = v?.Value<string>("object");
            property = v?.Value<string>("property");
            if (string.IsNullOrEmpty(id) || !fs.SimulationObjects.TryGetValue(id, out var obj))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Pick the object of " + what + ".");
            if (string.IsNullOrEmpty(property) || !(obj.GetProperties(PropertyType.ALL) ?? new string[0]).Contains(property))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Pick a property of " + obj.GraphicObject.Tag + " for " + what + ".");
            unit = v.Value<string>("unit");
            if (string.IsNullOrEmpty(unit))
            {
                try { unit = obj.GetPropertyUnit(property, fs.FlowsheetOptions.SelectedUnitSystem); } catch { unit = ""; }
            }
            return obj;
        }

        /// <summary>The unit given, else the flowsheet's unit for the property.</summary>
        private static string UnitOr(IFlowsheet fs, string objectId, string property, string unit)
        {
            if (!string.IsNullOrEmpty(unit) || !fs.SimulationObjects.TryGetValue(objectId ?? "", out var obj) || string.IsNullOrEmpty(property)) return unit ?? "";
            try { return obj.GetPropertyUnit(property, fs.FlowsheetOptions.SelectedUnitSystem) ?? ""; } catch { return ""; }
        }

        private static double Read(IFlowsheet fs, string objectId, string property, string unit)
        {
            if (!fs.SimulationObjects.TryGetValue(objectId, out var obj)) return double.NaN;
            try
            {
                var si = Convert.ToDouble(obj.GetPropertyValue(property), CultureInfo.InvariantCulture);
                return string.IsNullOrEmpty(unit) ? si : Converter.ConvertFromSI(unit, si);
            }
            catch { return double.NaN; }
        }

        private static void Write(IFlowsheet fs, string objectId, string property, string unit, double value)
            => fs.SimulationObjects[objectId].SetPropertyValue(property, string.IsNullOrEmpty(unit) ? value : Converter.ConvertToSI(unit, value));

        private static JToken Num(double v) => double.IsNaN(v) || double.IsInfinity(v) ? JValue.CreateNull() : new JValue(v);

        private static int Index<T>(List<T> list, int index)
            => index >= 0 && index < list.Count ? index : throw new RpcException(RpcErrorCodes.InvalidParams, "No case " + (index + 1) + ".");

        private void Progress(string flowsheetId, string study, int done, int total, string text)
            => _notifier.Notify("study/progress", new JObject
            {
                ["flowsheetId"] = flowsheetId,
                ["study"] = study,
                ["done"] = done,
                ["total"] = total,
                ["text"] = text
            });

        // ------------------------------------------------------------------ sensitivity

        private static JObject SensitivityJson(IFlowsheet fs, SensitivityAnalysisCase c, int index)
        {
            var iv = VariableJson(fs, c.iv1.objectID, c.iv1.propID, c.iv1.unit);
            iv["from"] = Num(c.iv1.lowerlimit ?? double.NaN);
            iv["to"] = Num(c.iv1.upperlimit ?? double.NaN);
            iv["points"] = c.iv1.points > 1 ? c.iv1.points : 10;
            return new JObject
            {
                ["index"] = index,
                ["name"] = c.name ?? "",
                ["description"] = c.description ?? "",
                ["independent"] = iv,
                ["dependents"] = new JArray(c.depvariables.Values.Select(d => VariableJson(fs, d.objectID, d.propID, d.unit)))
            };
        }

        [RpcMethod("sensitivity.list")]
        public JArray SensitivityList(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var cases = Base(session).SensAnalysisCollection;
                return new JArray(cases.Select((c, i) => SensitivityJson(session.Inner, c, i)));
            }
        }

        /// <summary>Creates (no index) or replaces a case. Incomplete variables are kept, so a case can be built up in steps.</summary>
        [RpcMethod("sensitivity.save")]
        public JObject SensitivitySave(string flowsheetId, JObject study)
        {
            if (study == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the case.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var cases = Base(session).SensAnalysisCollection;
                var index = study.Value<int?>("index");
                var c = index == null ? new SensitivityAnalysisCase() : cases[Index(cases, index.Value)];
                var name = (study.Value<string>("name") ?? "").Trim();
                session.Record((index == null ? "Add sensitivity case " : "Edit sensitivity case ") + name);
                c.name = name.Length > 0 ? name : "Case " + (cases.Count + (index == null ? 1 : 0));
                c.description = study.Value<string>("description") ?? "";
                c.numvar = 1;
                var iv = study["independent"] as JObject ?? new JObject();
                c.iv1.objectID = iv.Value<string>("object") ?? "";
                c.iv1.objectTAG = fs.SimulationObjects.TryGetValue(c.iv1.objectID, out var o) ? o.GraphicObject.Tag : "";
                c.iv1.propID = iv.Value<string>("property") ?? "";
                c.iv1.unit = UnitOr(fs, c.iv1.objectID, c.iv1.propID, iv.Value<string>("unit"));
                c.iv1.lowerlimit = iv.Value<double?>("from");
                c.iv1.upperlimit = iv.Value<double?>("to");
                c.iv1.points = Math.Max(2, iv.Value<int?>("points") ?? 10);
                c.depvariables.Clear();
                foreach (var d in study["dependents"] as JArray ?? new JArray())
                {
                    var v = new SAVariable
                    {
                        id = Guid.NewGuid().ToString(),
                        objectID = d.Value<string>("object") ?? "",
                        propID = d.Value<string>("property") ?? "",
                        unit = d.Value<string>("unit") ?? ""
                    };
                    v.unit = UnitOr(fs, v.objectID, v.propID, v.unit);
                    v.objectTAG = fs.SimulationObjects.TryGetValue(v.objectID, out var dob) ? dob.GraphicObject.Tag : "";
                    c.depvariables.Add(v.id, v);
                }
                if (index == null) cases.Add(c);
                return SensitivityJson(fs, c, cases.IndexOf(c));
            }
        }

        [RpcMethod("sensitivity.delete")]
        public JArray SensitivityDelete(string flowsheetId, int index)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var cases = Base(session).SensAnalysisCollection;
                var c = cases[Index(cases, index)];
                session.Record("Delete sensitivity case " + c.name);
                cases.Remove(c);
            }
            return SensitivityList(flowsheetId);
        }

        /// <summary>
        /// Steps the independent variable over its range, solving at each point and reading every
        /// dependent one; then puts the variable back and solves again. Points the solver fails
        /// at have no values. Cancelling keeps the points done so far.
        /// </summary>
        [RpcMethod("sensitivity.run")]
        public JObject SensitivityRun(string flowsheetId, int index, CancellationToken cancellation = default)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var cases = Base(session).SensAnalysisCollection;
                var c = cases[Index(cases, index)];
                var ivJson = JObject.FromObject(new { @object = c.iv1.objectID, property = c.iv1.propID, unit = c.iv1.unit });
                var indep = Resolve(fs, ivJson, "the independent variable", out var ip, out var iu);
                if (c.iv1.lowerlimit == null || c.iv1.upperlimit == null)
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Give the independent variable's range.");
                var deps = c.depvariables.Values.ToList();
                if (deps.Count == 0) throw new RpcException(RpcErrorCodes.InvalidParams, "Add a dependent variable.");
                var depUnits = deps.Select(d =>
                {
                    Resolve(fs, JObject.FromObject(new { @object = d.objectID, property = d.propID, unit = d.unit }), "a dependent variable", out _, out var u);
                    return u;
                }).ToList();

                int points = Math.Max(2, c.iv1.points);
                double from = c.iv1.lowerlimit.Value, to = c.iv1.upperlimit.Value;
                var original = Convert.ToDouble(indep.GetPropertyValue(ip), CultureInfo.InvariantCulture);
                var xs = new List<double>();
                var rows = new List<double[]>();
                var failures = new List<string>();
                bool cancelled = false;
                using (cancellation.Register(() => Settings.TaskCancellationTokenSource?.Cancel()))
                {
                    try
                    {
                        for (int i = 0; i < points; i++)
                        {
                            if (cancellation.IsCancellationRequested) { cancelled = true; break; }
                            var x = from + i * (to - from) / (points - 1);
                            Write(fs, indep.Name, ip, iu, x);
                            var errors = session.Flowsheet.TrySolve();
                            if (cancellation.IsCancellationRequested) { cancelled = true; break; }
                            xs.Add(x);
                            if (errors.Count > 0)
                            {
                                failures.Add($"{x.ToString("G6", CultureInfo.InvariantCulture)}: {(errors[0].InnerException ?? errors[0]).Message}");
                                rows.Add(deps.Select(_ => double.NaN).ToArray());
                            }
                            else rows.Add(deps.Select((d, k) => Read(fs, d.objectID, d.propID, depUnits[k])).ToArray());
                            Progress(flowsheetId, "sensitivity", i + 1, points, $"Point {i + 1} of {points}");
                        }
                    }
                    finally
                    {
                        // Back to where it started, whatever happened.
                        indep.SetPropertyValue(ip, original);
                        try { Settings.TaskCancellationTokenSource = new CancellationTokenSource(); } catch { }
                        session.Flowsheet.TrySolve();
                    }
                }

                c.results = new System.Collections.ArrayList(rows.Cast<object>().ToList());
                var xLabel = VariableJson(fs, indep.Name, ip, iu).Value<string>("label");
                var depLabels = deps.Select((d, k) => VariableJson(fs, d.objectID, d.propID, depUnits[k]).Value<string>("label")).ToList();

                // One chart per unit, so two scales never share an axis.
                var charts = new JArray();
                foreach (var group in deps.Select((d, k) => k).GroupBy(k => depUnits[k] ?? ""))
                {
                    var keys = group.ToList();
                    charts.Add(new JObject
                    {
                        ["title"] = string.Join(", ", keys.Select(k => depLabels[k])),
                        ["x"] = new JObject { ["label"] = Property(xLabel), ["unit"] = string.IsNullOrEmpty(iu) ? null : iu },
                        ["y"] = new JObject { ["label"] = keys.Count == 1 ? Property(depLabels[keys[0]]) : "Value", ["unit"] = group.Key.Length == 0 ? null : group.Key },
                        ["series"] = new JArray(keys.Select(k =>
                        {
                            var pairs = xs.Select((x, i) => (x, y: rows[i][k])).Where(p => !double.IsNaN(p.y) && !double.IsInfinity(p.y)).ToList();
                            return new JObject { ["name"] = depLabels[k], ["kind"] = "line", ["x"] = new JArray(pairs.Select(p => p.x)), ["y"] = new JArray(pairs.Select(p => p.y)) };
                        }))
                    });
                }
                return new JObject
                {
                    ["cancelled"] = cancelled,
                    ["charts"] = charts,
                    ["table"] = new JObject
                    {
                        ["columns"] = new JArray(new[] { Header(xLabel, iu) }.Concat(depLabels.Select((l, k) => Header(l, depUnits[k])))),
                        ["rows"] = new JArray(xs.Select((x, i) => new JArray(new[] { Num(x) }.Concat(rows[i].Select(Num)))))
                    },
                    ["warnings"] = new JArray(failures.Take(5).Select(f => "The flowsheet did not solve at " + f))
                };
            }
        }

        /// <summary>The property part of "tag · property": axis titles are short, the chart title names the object.</summary>
        private static string Property(string label) => label?.Split(new[] { " · " }, 2, StringSplitOptions.None).Last() ?? "";

        private static string Header(string label, string unit) => string.IsNullOrEmpty(unit) ? label : label + " (" + unit + ")";

        // ------------------------------------------------------------------ optimizer

        private static readonly OptimizationCase.SolvingMethod[] Methods =
        {
            OptimizationCase.SolvingMethod.DN_NELDERMEAD_SIMPLEX,
            OptimizationCase.SolvingMethod.DN_LBFGS,
            OptimizationCase.SolvingMethod.DN_TRUNCATED_NEWTON,
            OptimizationCase.SolvingMethod.DN_NELDERMEAD_SIMPLEX_B,
            OptimizationCase.SolvingMethod.DN_LBFGS_B,
            OptimizationCase.SolvingMethod.DN_TRUNCATED_NEWTON_B
        };

        private static readonly string[] MethodLabels =
            { "Simplex", "L-BFGS", "Truncated Newton", "Simplex (bounded)", "L-BFGS (bounded)", "Truncated Newton (bounded)" };

        private static JObject OptimizerJson(IFlowsheet fs, OptimizationCase c, int index) => new JObject
        {
            ["index"] = index,
            ["name"] = c.name ?? "",
            ["description"] = c.description ?? "",
            ["type"] = c.type.ToString(),
            ["objective"] = c.objfunctype.ToString(),
            ["expression"] = c.expression ?? "",
            ["method"] = (Array.IndexOf(Methods, c.solvm) is var m && m >= 0 ? Methods[m] : Methods[0]).ToString(),
            ["maxIterations"] = c.maxits,
            ["tolerance"] = c.tolerance,
            ["epsilon"] = c.epsilon,
            ["barrier"] = c.barriermultiplier,
            ["variables"] = new JArray(c.variables.Values.Select(v =>
            {
                var j = VariableJson(fs, v.objectID, v.propID, v.unit);
                j["id"] = v.id;
                j["name"] = v.name ?? "";
                j["kind"] = v.type.ToString();
                j["initial"] = Num(v.initialvalue);
                j["lower"] = Num(v.lowerlimit ?? double.NaN);
                j["upper"] = Num(v.upperlimit ?? double.NaN);
                j["current"] = Num(v.currentvalue);
                return j;
            }))
        };

        /// <summary>The optimizer's methods, by engine name, with labels.</summary>
        [RpcMethod("optimizer.methods")]
        public JArray OptimizerMethods()
            => new JArray(Methods.Select((m, i) => new JObject { ["value"] = m.ToString(), ["label"] = MethodLabels[i] }));

        [RpcMethod("optimizer.list")]
        public JArray OptimizerList(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var cases = Base(session).OptimizationCollection;
                return new JArray(cases.Select((c, i) => OptimizerJson(session.Inner, c, i)));
            }
        }

        [RpcMethod("optimizer.save")]
        public JObject OptimizerSave(string flowsheetId, JObject study)
        {
            if (study == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the case.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var cases = Base(session).OptimizationCollection;
                var index = study.Value<int?>("index");
                var c = index == null ? new OptimizationCase() : cases[Index(cases, index.Value)];
                var name = (study.Value<string>("name") ?? "").Trim();
                session.Record((index == null ? "Add optimization case " : "Edit optimization case ") + name);
                c.name = name.Length > 0 ? name : "Case " + (cases.Count + (index == null ? 1 : 0));
                c.description = study.Value<string>("description") ?? "";
                c.type = Enum.TryParse<OPTType>(study.Value<string>("type") ?? "", out var t) ? t : OPTType.Minimization;
                c.objfunctype = Enum.TryParse<OPTObjectiveFunctionType>(study.Value<string>("objective") ?? "", out var f) ? f : OPTObjectiveFunctionType.Variable;
                c.expression = study.Value<string>("expression") ?? "";
                c.solvm = Enum.TryParse<OptimizationCase.SolvingMethod>(study.Value<string>("method") ?? "", out var m) && Methods.Contains(m) ? m : Methods[0];
                c.maxits = Math.Max(1, study.Value<int?>("maxIterations") ?? 100);
                c.tolerance = study.Value<double?>("tolerance") ?? 1e-6;
                c.epsilon = study.Value<double?>("epsilon") ?? 1e-3;
                c.barriermultiplier = study.Value<double?>("barrier") ?? 1e-4;
                c.variables.Clear();
                foreach (var v in study["variables"] as JArray ?? new JArray())
                {
                    var ov = new OPTVariable
                    {
                        id = string.IsNullOrEmpty(v.Value<string>("id")) ? Guid.NewGuid().ToString() : v.Value<string>("id"),
                        name = v.Value<string>("name") ?? "",
                        objectID = v.Value<string>("object") ?? "",
                        propID = v.Value<string>("property") ?? "",
                        unit = v.Value<string>("unit") ?? "",
                        type = Enum.TryParse<OPTVariableType>(v.Value<string>("kind") ?? "", out var k) ? k : OPTVariableType.Independent,
                        initialvalue = v.Value<double?>("initial") ?? 0,
                        lowerlimit = v.Value<double?>("lower"),
                        upperlimit = v.Value<double?>("upper")
                    };
                    ov.unit = UnitOr(fs, ov.objectID, ov.propID, ov.unit);
                    ov.objectTAG = fs.SimulationObjects.TryGetValue(ov.objectID, out var o) ? o.GraphicObject.Tag : "";
                    c.variables[ov.id] = ov;
                }
                if (index == null) cases.Add(c);
                return OptimizerJson(fs, c, cases.IndexOf(c));
            }
        }

        [RpcMethod("optimizer.delete")]
        public JArray OptimizerDelete(string flowsheetId, int index)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var cases = Base(session).OptimizationCollection;
                var c = cases[Index(cases, index)];
                session.Record("Delete optimization case " + c.name);
                cases.Remove(c);
            }
            return OptimizerList(flowsheetId);
        }

        /// <summary>
        /// Minimises (or maximises) the objective by moving the independent variables, solving the
        /// flowsheet at each evaluation, as OptimizerWindow does: a dependent variable or an
        /// expression of the named variables, plus a barrier penalty for constraints. The optimum
        /// is left in the flowsheet (one undo step puts it back); a cancelled run restores the
        /// initial values.
        /// </summary>
        [RpcMethod("optimizer.run")]
        public JObject OptimizerRun(string flowsheetId, int index, CancellationToken cancellation = default)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var cases = Base(session).OptimizationCollection;
                var c = cases[Index(cases, index)];
                var all = c.variables.Values.ToList();
                foreach (var v in all)
                {
                    Resolve(fs, JObject.FromObject(new { @object = v.objectID, property = v.propID, unit = v.unit }), (v.name is { Length: > 0 } ? v.name : v.type.ToString().ToLowerInvariant() + " variable"), out _, out var u);
                    v.unit = u;
                }
                var ind = all.Where(v => v.type == OPTVariableType.Independent).ToList();
                var dep = all.Where(v => v.type == OPTVariableType.Dependent).ToList();
                var con = all.Where(v => v.type == OPTVariableType.Constraint).ToList();
                if (ind.Count == 0) throw new RpcException(RpcErrorCodes.InvalidParams, "Add an independent variable to move.");
                if (c.objfunctype == OPTObjectiveFunctionType.Variable && dep.Count == 0)
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Add a dependent variable as the objective.");
                if (c.objfunctype == OPTObjectiveFunctionType.Expression && string.IsNullOrWhiteSpace(c.expression))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Write the objective expression.");
                var bounded = c.solvm.ToString().EndsWith("_B", StringComparison.Ordinal);
                if (bounded && ind.Any(v => v.lowerlimit == null || v.upperlimit == null))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "A bounded method needs both limits on every independent variable.");
                DWSIM.SharedClasses.CompiledExpression compiled = null;
                if (c.objfunctype == OPTObjectiveFunctionType.Expression)
                {
                    try { compiled = DWSIM.SharedClasses.ExpressionEvaluator.Compile(c.expression); }
                    catch (Exception ex) { throw new RpcException(RpcErrorCodes.InvalidParams, "The objective expression does not compile: " + ex.Message); }
                }

                session.Record("Optimize " + c.name);
                var history = new List<double>();
                var best = (value: double.PositiveInfinity, x: (double[])null);
                var gradient = false;
                double Penalty()
                {
                    double pen = 0;
                    foreach (var v in con)
                    {
                        double d1 = v.currentvalue - (v.lowerlimit ?? double.NegativeInfinity), d2 = v.currentvalue - (v.upperlimit ?? double.PositiveInfinity);
                        if (d1 < 0) pen += -d1 * 1e6;
                        else if (d2 > 0) pen += d2 * 1e6;
                        else pen += 1 / d1 - 1 / d2;
                    }
                    pen *= c.barriermultiplier;
                    return double.IsNaN(pen) ? 0 : pen;
                }
                double Objective(double[] x)
                {
                    if (cancellation.IsCancellationRequested) throw new OperationCanceledException();
                    for (int i = 0; i < ind.Count; i++) Write(fs, ind[i].objectID, ind[i].propID, ind[i].unit, x[i]);
                    var errors = session.Flowsheet.TrySolve();
                    if (cancellation.IsCancellationRequested) throw new OperationCanceledException();
                    foreach (var v in all) v.currentvalue = Read(fs, v.objectID, v.propID, v.unit);
                    double value;
                    if (errors.Count > 0) value = 1e30;
                    else if (compiled != null)
                    {
                        var table = new DWSIM.SharedClasses.ExpressionEvaluator.VariableTable();
                        foreach (var v in all.Where(v => !string.IsNullOrEmpty(v.name))) table.SetValue(v.name, v.currentvalue);
                        value = compiled.Evaluate(table) + Penalty();
                    }
                    else value = dep[0].currentvalue + Penalty();
                    if (double.IsNaN(value)) value = 1e30;
                    if (!gradient)
                    {
                        history.Add(value);
                        if (best.x == null || (c.type == OPTType.Maximization ? value > best.value : value < best.value))
                            best = (value, (double[])x.Clone());
                        Progress(flowsheetId, "optimizer", history.Count, c.maxits, $"Evaluation {history.Count}: {value.ToString("G8", CultureInfo.InvariantCulture)}");
                    }
                    return c.type == OPTType.Maximization ? -value : value;
                }
                double[] Gradient(double[] x)
                {
                    gradient = true;
                    try
                    {
                        var g = new double[x.Length];
                        var f1 = Objective(x);
                        for (int i = 0; i < x.Length; i++)
                        {
                            var x2 = (double[])x.Clone();
                            x2[i] = Math.Abs(x[i]) < double.Epsilon ? c.epsilon : x[i] * (1 + c.epsilon);
                            g[i] = (Objective(x2) - f1) / (x2[i] - x[i]);
                        }
                        return g;
                    }
                    finally { gradient = false; }
                }

                var unbound = ind.Select(v => new DotNumerics.Optimization.OptVariable(v.initialvalue)).ToArray();
                var bound = ind.Select(v => new DotNumerics.Optimization.OptBoundVariable(v.initialvalue, v.lowerlimit ?? double.MinValue, v.upperlimit ?? double.MaxValue)).ToArray();
                bool cancelled = false;
                string failure = null;
                double[] solution = null;
                using (cancellation.Register(() => Settings.TaskCancellationTokenSource?.Cancel()))
                {
                    try
                    {
                        switch (c.solvm)
                        {
                            case OptimizationCase.SolvingMethod.DN_LBFGS:
                                solution = new DotNumerics.Optimization.L_BFGS_B { Tolerance = c.tolerance, MaxFunEvaluations = c.maxits }.ComputeMin(Objective, Gradient, unbound);
                                break;
                            case OptimizationCase.SolvingMethod.DN_TRUNCATED_NEWTON:
                                solution = new DotNumerics.Optimization.TruncatedNewton { Tolerance = c.tolerance, MaxFunEvaluations = c.maxits }.ComputeMin(Objective, Gradient, unbound);
                                break;
                            case OptimizationCase.SolvingMethod.DN_NELDERMEAD_SIMPLEX_B:
                                solution = new DotNumerics.Optimization.Simplex { Tolerance = c.tolerance, MaxFunEvaluations = c.maxits }.ComputeMin(Objective, bound);
                                break;
                            case OptimizationCase.SolvingMethod.DN_LBFGS_B:
                                solution = new DotNumerics.Optimization.L_BFGS_B { Tolerance = c.tolerance, MaxFunEvaluations = c.maxits }.ComputeMin(Objective, Gradient, bound);
                                break;
                            case OptimizationCase.SolvingMethod.DN_TRUNCATED_NEWTON_B:
                                solution = new DotNumerics.Optimization.TruncatedNewton { Tolerance = c.tolerance, MaxFunEvaluations = c.maxits }.ComputeMin(Objective, Gradient, bound);
                                break;
                            default:
                                solution = new DotNumerics.Optimization.Simplex { Tolerance = c.tolerance, MaxFunEvaluations = c.maxits }.ComputeMin(Objective, unbound);
                                break;
                        }
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    catch (Exception ex) { failure = (ex.InnerException ?? ex).Message; }
                }
                try { Settings.TaskCancellationTokenSource = new CancellationTokenSource(); } catch { }

                // Leave the flowsheet at the best point found (the initial point if cancelled).
                var final = cancelled ? ind.Select(v => v.initialvalue).ToArray() : best.x ?? solution;
                if (final != null)
                {
                    for (int i = 0; i < ind.Count; i++) Write(fs, ind[i].objectID, ind[i].propID, ind[i].unit, final[i]);
                    session.Flowsheet.TrySolve();
                    foreach (var v in all) v.currentvalue = Read(fs, v.objectID, v.propID, v.unit);
                }

                var result = OptimizerJson(fs, c, cases.IndexOf(c));
                result["cancelled"] = cancelled;
                result["error"] = failure;
                result["evaluations"] = history.Count;
                result["best"] = cancelled ? JValue.CreateNull() : Num(best.value);
                result["chart"] = new JObject
                {
                    ["title"] = "Objective per evaluation",
                    ["x"] = new JObject { ["label"] = "Evaluation", ["unit"] = null },
                    ["y"] = new JObject { ["label"] = "Objective", ["unit"] = null },
                    ["series"] = new JArray(new JObject
                    {
                        ["name"] = "Objective",
                        ["kind"] = "line",
                        ["x"] = new JArray(history.Select((_, i) => (double)(i + 1)).Where((_, i) => history[i] < 1e29)),
                        ["y"] = new JArray(history.Where(h => h < 1e29))
                    })
                };
                return result;
            }
        }
    }
}
