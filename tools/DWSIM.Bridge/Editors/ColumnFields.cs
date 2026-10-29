using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DWSIM.Automation.FluentAPI.Diagnostics;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.SharedClasses.SystemsOfUnits;
using DWSIM.UnitOperations.UnitOperations;
using DWSIM.UnitOperations.UnitOperations.Auxiliary.SepOps;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Editors
{
    /// <summary>
    /// The editor sections of a rigorous column (distillation, absorption, refluxed and reboiled
    /// absorbers): the column itself, its condenser and reboiler, the stages, which stage each
    /// stream meets, the initial estimates and the solved profiles. They follow the Avalonia
    /// ColumnEditor and its stage, connection and estimate editors, writing through the column's
    /// own methods (SetNumberOfStages, SetTopStagePressure, spec objects) as those do.
    /// </summary>
    /// <remarks>Field ids start with <c>column:</c>; numbers carry their unit, written in SI.</remarks>
    internal static class ColumnFields
    {
        public const string Prefix = "column:";

        // ------------------------------------------------------------------ catalogues

        /// <summary>Spec types in the order and words of the Avalonia/Windows combos, indexed by SpecType.</summary>
        private static readonly string[] SpecLabels =
        {
            "Heat Load", "Product Molar Flow", "Compound Molar flow in Product Stream", "Product Mass Flow",
            "Compound Mass Flow in Product Stream", "Compound Fraction in Product Stream", "Compound Recovery",
            "Reflux Ratio", "Temperature", "Feed Recovery"
        };

        private static readonly string[] MolarFlowUnits =
        {
            "mol/s", "lbmol/h", "mol/h", "mol/d", "kmol/s", "kmol/h", "kmol/d",
            "m3/d @ BR", "m3/d @ NC", "m3/d @ CNTP", "m3/d @ SC", "m3/d @ 0 C, 1 atm",
            "m3/d @ 15.56 C, 1 atm", "m3/d @ 20 C, 1 atm", "ft3/d @ 60 F, 14.7 psia", "ft3/d @ 0 C, 1 atm"
        };

        private static readonly string[] MassFlowUnits = { "g/s", "lbm/h", "kg/s", "kg/h", "kg/d", "kg/min", "lb/min", "lb/s" };

        /// <summary>The units each spec type takes, as the Avalonia ColumnEditor.SpecUnits lists them.</summary>
        private static string[] SpecUnits(ColumnSpec.SpecType type)
        {
            switch (type)
            {
                case ColumnSpec.SpecType.Component_Fraction: return new[] { "Molar", "Mass" };
                case ColumnSpec.SpecType.Component_Mass_Flow_Rate:
                case ColumnSpec.SpecType.Product_Mass_Flow_Rate: return MassFlowUnits;
                case ColumnSpec.SpecType.Component_Molar_Flow_Rate:
                case ColumnSpec.SpecType.Product_Molar_Flow_Rate: return MolarFlowUnits;
                case ColumnSpec.SpecType.Component_Recovery: return new[] { "% M/M", "% W/W" };
                case ColumnSpec.SpecType.Heat_Duty: return new[] { "kW", "kcal/h", "BTU/h", "BTU/s", "cal/s", "HP", "kJ/h", "kJ/d", "MW", "W" };
                case ColumnSpec.SpecType.Temperature: return new[] { "K", "R", "C", "F" };
                case ColumnSpec.SpecType.Feed_Recovery: return new[] { "%" };
                default: return new[] { "" };
            }
        }

        private static bool NeedsCompound(ColumnSpec.SpecType t) =>
            t == ColumnSpec.SpecType.Component_Molar_Flow_Rate || t == ColumnSpec.SpecType.Component_Mass_Flow_Rate ||
            t == ColumnSpec.SpecType.Component_Fraction || t == ColumnSpec.SpecType.Component_Recovery;

        private static List<string> Solvers(Column column) => column is AbsorptionColumn
            ? new List<string> { "Burningham-Otto (Sum Rates)", "Naphtali-Sandholm (Simultaneous Correction)" }
            : new List<string> { "Wang-Henke (Bubble Point)", "Naphtali-Sandholm (Simultaneous Correction)", "Modified Wang-Henke (Bubble Point)" };

        private static bool HasCondenser(Column c) => c is DistillationColumn d && !d.ReboiledAbsorber;

        private static bool HasReboiler(Column c) => c is DistillationColumn d && !d.RefluxedAbsorber;

        private static readonly Dictionary<StreamInformation.Behavior, string> Roles = new Dictionary<StreamInformation.Behavior, string>
        {
            [StreamInformation.Behavior.Feed] = "Feed",
            [StreamInformation.Behavior.Distillate] = "Distillate",
            [StreamInformation.Behavior.OverheadVapor] = "Overhead vapor",
            [StreamInformation.Behavior.BottomsLiquid] = "Bottoms",
            [StreamInformation.Behavior.Sidedraw] = "Side draw",
            [StreamInformation.Behavior.FreeWater] = "Free water",
            [StreamInformation.Behavior.Steam] = "Steam"
        };

        // ------------------------------------------------------------------ reading

        public static IEnumerable<SpecialFields.Section> For(IFlowsheet fs, Column column)
        {
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            EnsureStreamStages(column);
            EnsureEstimates(fs, column);

            var general = new SpecialFields.Section { Id = "column", Title = "Column" };
            general.Fields.Add(Number("stages", "Number of stages", column.NumberOfStages, null, hint: "Including the condenser and the reboiler; at least 4."));
            general.Fields.Add(Number("topPressure", "Top stage pressure", column.Stages.Count > 0 ? column.Stages[0].P : (double?)null, su.pressure, units: UnitsOf(su, su.pressure)));
            general.Fields.Add(Number("pressureDrop", "Column pressure drop", double.IsNaN(column.ColumnPressureDrop) ? (double?)null : Converter.ConvertFromSI(su.deltaP, column.ColumnPressureDrop),
                su.deltaP, units: UnitsOf(su, su.deltaP), hint: double.IsNaN(column.ColumnPressureDrop) ? "Custom stage pressures (see Stages)." : null, siValue: false));
            if (column is AbsorptionColumn absorber)
                general.Fields.Add(Choice("absorberMode", "Operation mode", ((int)absorber.OperationMode).ToString(CultureInfo.InvariantCulture),
                    ("0", "Absorber"), ("1", "Liquid-Liquid Extractor")));
            var solvers = Solvers(column);
            if (!string.IsNullOrEmpty(column.SolvingMethodName) && !solvers.Contains(column.SolvingMethodName)) solvers.Add(column.SolvingMethodName);
            general.Fields.Add(Choice("solver", "Solver", column.SolvingMethodName, solvers.Select(s => (s, s)).ToArray()));
            general.Fields.Add(Number("maxIterations", "Maximum iterations", column.MaxIterations, null));
            general.Fields.Add(Number("tolerance", "Convergence tolerance", column.ExternalLoopTolerance, null));
            yield return general;

            if (column is DistillationColumn distillation)
            {
                var condenser = new SpecialFields.Section { Id = "condenser", Title = "Condenser" };
                condenser.Fields.Add(Bool("noCondenser", "No condenser (reboiled absorber)", distillation.ReboiledAbsorber));
                if (HasCondenser(column))
                {
                    condenser.Fields.Add(Choice("condenserType", "Condenser type", ((int)column.CondenserType).ToString(CultureInfo.InvariantCulture),
                        ("0", "Total"), ("1", "Partial"), ("2", "Full Reflux")));
                    condenser.Fields.Add(Number("condenserDeltaP", "Condenser pressure drop", Converter.ConvertFromSI(su.deltaP, column.CondenserDeltaP), su.deltaP, units: UnitsOf(su, su.deltaP), siValue: false));
                    condenser.Fields.AddRange(SpecFields(fs, column, "C", reboiler: false));
                    if (column.CondenserType == Column.condtype.Partial_Condenser)
                        condenser.Fields.Add(Number("vaporFlow", "Vapor product flow", column.VaporFlowRate, string.IsNullOrEmpty(column.VaporFlowRateUnit) ? "mol/s" : column.VaporFlowRateUnit, units: MolarFlowUnits, siValue: false));
                    if (column.CondenserType == Column.condtype.Total_Condenser)
                        condenser.Fields.Add(Number("subcooling", "Subcooling", Converter.ConvertFromSI(su.deltaT, distillation.TotalCondenserSubcoolingDeltaT), su.deltaT, units: UnitsOf(su, su.deltaT), siValue: false));
                }
                yield return condenser;

                var reboiler = new SpecialFields.Section { Id = "reboiler", Title = "Reboiler" };
                reboiler.Fields.Add(Bool("noReboiler", "No reboiler (refluxed absorber)", distillation.RefluxedAbsorber));
                if (HasReboiler(column)) reboiler.Fields.AddRange(SpecFields(fs, column, "R", reboiler: true));
                yield return reboiler;
            }

            yield return new SpecialFields.Section { Id = "stages", Title = "Stages", Fields = { StagesTable(su, column) } };
            yield return new SpecialFields.Section { Id = "streamStages", Title = "Stream stages", Fields = { StreamsTable(fs, column) } };

            var estimates = new SpecialFields.Section { Id = "estimates", Title = "Initial estimates", Collapsed = true };
            estimates.Fields.Add(Bool("useT", "Use temperature estimates", column.UseTemperatureEstimates));
            estimates.Fields.Add(Bool("useL", "Use liquid flow estimates", column.UseLiquidFlowEstimates));
            estimates.Fields.Add(Bool("useV", "Use vapor flow estimates", column.UseVaporFlowEstimates));
            estimates.Fields.Add(Bool("useX", "Use composition estimates", column.UseCompositionEstimates));
            estimates.Fields.Add(Bool("autoUpdate", "Update estimates after each solution", column.AutoUpdateInitialEstimates));
            estimates.Fields.Add(new JObject
            {
                ["id"] = Prefix + "readEstimates",
                ["label"] = "Read estimates from the last solution",
                ["kind"] = "action",
                ["editable"] = column.Tf != null && column.Tf.Length > 0,
                ["hint"] = column.Tf != null && column.Tf.Length > 0 ? null : "Solve the column first."
            });
            yield return estimates;

            if (column.Tf != null && column.Tf.Length > 0)
                yield return new SpecialFields.Section { Id = "profiles", Title = "Profiles", Fields = { ProfileTable(su, column), CompositionTable(fs, column, liquid: true), CompositionTable(fs, column, liquid: false) } };
        }

        private static IEnumerable<JObject> SpecFields(IFlowsheet fs, Column column, string key, bool reboiler)
        {
            if (!column.Specs.TryGetValue(key, out var spec)) yield break;
            var labels = (string[])SpecLabels.Clone();
            if (reboiler) labels[(int)ColumnSpec.SpecType.Stream_Ratio] = "Boil-Up Ratio";
            yield return Choice(key + ".type", "Specification", ((int)spec.SType).ToString(CultureInfo.InvariantCulture),
                labels.Select((l, i) => (i.ToString(CultureInfo.InvariantCulture), l)).ToArray());
            var units = SpecUnits(spec.SType);
            var unit = string.IsNullOrEmpty(spec.SpecUnit) || !units.Contains(spec.SpecUnit) ? units[0] : spec.SpecUnit;
            var value = Number(key + ".value", "Specification value", spec.SpecValue, unit == "" ? null : unit,
                units: units.Length > 1 && spec.SType != ColumnSpec.SpecType.Component_Fraction ? units : null, siValue: false);
            if (spec.SType == ColumnSpec.SpecType.Component_Fraction)
                value["hint"] = "On a " + (unit == "Mass" ? "mass" : "molar") + " basis.";
            if (spec.CalculatedValue != 0 && column.Calculated) value["calculated"] = spec.CalculatedValue;
            yield return value;
            if (spec.SType == ColumnSpec.SpecType.Component_Fraction)
                yield return Choice(key + ".basis", "Fraction basis", unit, ("Molar", "Molar"), ("Mass", "Mass"));
            if (NeedsCompound(spec.SType))
                yield return Choice(key + ".compound", "Specification compound", string.IsNullOrEmpty(spec.ComponentID) ? null : spec.ComponentID,
                    fs.SelectedCompounds.Keys.Select(c => (c, c)).ToArray());
        }

        private static JObject StagesTable(IUnitsOfMeasure su, Column column) => new JObject
        {
            ["id"] = Prefix + "stageTable",
            ["label"] = "Stages, top to bottom",
            ["kind"] = "table",
            ["columns"] = new JArray(
                Col("name", "Stage", "text", false),
                Col("pressure", "Pressure", "number", true, su.pressure),
                Col("efficiency", "Efficiency", "number", true)),
            ["rows"] = new JArray(column.Stages.Select(s => new JObject
            {
                ["name"] = s.Name,
                ["pressure"] = Round(Converter.ConvertFromSI(su.pressure, s.P)),
                ["efficiency"] = s.Efficiency
            })),
            ["editable"] = true
        };

        private static JObject StreamsTable(IFlowsheet fs, Column column)
        {
            var stageOptions = new JArray(column.Stages.Select(s => new JObject { ["value"] = s.ID, ["label"] = s.Name }));
            var rows = new JArray();
            foreach (var si in column.MaterialStreams.Values)
            {
                fs.SimulationObjects.TryGetValue(si.StreamID ?? "", out var stream);
                var row = new JObject
                {
                    ["key"] = si.ID,
                    ["stream"] = stream?.GraphicObject?.Tag ?? si.StreamID,
                    ["role"] = Roles.TryGetValue(si.StreamBehavior, out var r) ? r : si.StreamBehavior.ToString(),
                    ["stage"] = si.AssociatedStage
                };
                // Only a side draw has a phase and a flow; other rows leave the cells out.
                if (si.StreamBehavior == StreamInformation.Behavior.Sidedraw)
                {
                    row["phase"] = si.StreamPhase.ToString();
                    row["flow"] = si.FlowRate?.Value;
                }
                rows.Add(row);
            }
            return new JObject
            {
                ["id"] = Prefix + "streamTable",
                ["label"] = rows.Count == 0 ? "Connect feeds and products on the canvas or under Connections." : "Where each stream meets the column",
                ["kind"] = "table",
                ["columns"] = new JArray(
                    Col("stream", "Stream", "text", false),
                    Col("role", "Role", "text", false),
                    new JObject { ["key"] = "stage", ["label"] = "Stage", ["kind"] = "choice", ["options"] = stageOptions, ["editable"] = true },
                    new JObject { ["key"] = "phase", ["label"] = "Phase", ["kind"] = "choice", ["options"] = new JArray(Opt("L", "Liquid"), Opt("V", "Vapor")), ["editable"] = true },
                    Col("flow", "Draw flow", "number", true, "mol/s")),
                ["rows"] = rows,
                ["editable"] = rows.Count > 0
            };
        }

        private static JObject ProfileTable(IUnitsOfMeasure su, Column column)
        {
            double At(double[] a, int i) => a != null && i < a.Length ? a[i] : double.NaN;
            var rows = new JArray();
            for (int i = 0; i < column.Stages.Count; i++)
            {
                rows.Add(new JObject
                {
                    ["stage"] = column.Stages[i].Name,
                    ["temperature"] = Round(Converter.ConvertFromSI(su.temperature, At(column.Tf, i))),
                    ["pressure"] = Round(Converter.ConvertFromSI(su.pressure, At(column.P0, i))),
                    ["liquid"] = Round(Converter.ConvertFromSI(su.molarflow, At(column.Lf, i))),
                    ["vapor"] = Round(Converter.ConvertFromSI(su.molarflow, At(column.Vf, i)))
                });
            }
            return new JObject
            {
                ["id"] = Prefix + "profile",
                ["label"] = column.ic > 0 ? $"Stage profile, solved in {column.ic} iterations" : "Stage profile",
                ["kind"] = "table",
                ["readOnly"] = true,
                ["chart"] = new JObject { ["x"] = "stage", ["series"] = new JArray("temperature", "liquid", "vapor") },
                ["columns"] = new JArray(
                    Col("stage", "Stage", "text", false),
                    Col("temperature", "T", "number", false, su.temperature),
                    Col("pressure", "P", "number", false, su.pressure),
                    Col("liquid", "L", "number", false, su.molarflow),
                    Col("vapor", "V", "number", false, su.molarflow)),
                ["rows"] = rows,
                ["editable"] = false
            };
        }

        private static JObject CompositionTable(IFlowsheet fs, Column column, bool liquid)
        {
            var compounds = column.compids != null && column.compids.Count > 0
                ? column.compids.Cast<object>().Select(c => c.ToString()).ToList()
                : fs.SelectedCompounds.Keys.ToList();
            var source = liquid ? column.xf : column.yf;
            var columns = new JArray(Col("stage", "Stage", "text", false));
            foreach (var c in compounds) columns.Add(Col(c, c, "number", false));
            var rows = new JArray();
            for (int i = 0; i < column.Stages.Count; i++)
            {
                var row = new JObject { ["stage"] = column.Stages[i].Name };
                var x = source != null && i < source.Count ? source[i] as double[] : null;
                for (int j = 0; j < compounds.Count; j++) row[compounds[j]] = x != null && j < x.Length ? Round(x[j]) : null;
                rows.Add(row);
            }
            return new JObject
            {
                ["id"] = Prefix + (liquid ? "liquidComposition" : "vaporComposition"),
                ["label"] = liquid ? "Liquid mole fractions" : "Vapor mole fractions",
                ["kind"] = "table",
                ["readOnly"] = true,
                ["columns"] = columns,
                ["rows"] = rows,
                ["editable"] = false
            };
        }

        // ------------------------------------------------------------------ writing

        public static bool TrySet(IFlowsheet fs, Column column, string field, JToken value, string unit)
        {
            if (!field.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            var name = field.Substring(Prefix.Length);
            double Num() => value.Type == JTokenType.Integer || value.Type == JTokenType.Float
                ? value.Value<double>()
                : double.Parse(value.ToString(), CultureInfo.InvariantCulture);
            double Si(string fallbackUnit) => Converter.ConvertToSI(string.IsNullOrEmpty(unit) ? fallbackUnit : unit, Num());
            var su = fs.FlowsheetOptions.SelectedUnitSystem;

            switch (name)
            {
                case "stages":
                    var n = (int)Math.Round(Num());
                    if (n < 4) throw new RpcException(RpcErrorCodes.InvalidParams, "A column needs at least 4 stages, the condenser and reboiler included.");
                    column.SetNumberOfStages(n);
                    EnsureEstimates(fs, column, force: true);
                    return true;
                case "topPressure":
                    var p = Si(su.pressure);
                    if (p <= 0) throw new RpcException(RpcErrorCodes.InvalidParams, "The pressure must be above zero.");
                    column.SetTopStagePressure(p);
                    return true;
                case "pressureDrop":
                    column.ColumnPressureDrop = Si(su.deltaP);
                    column.SetTopStagePressure(column.Stages[0].P);
                    return true;
                case "absorberMode":
                    ((AbsorptionColumn)column).OperationMode = (AbsorptionColumn.OpMode)int.Parse(value.ToString(), CultureInfo.InvariantCulture);
                    return true;
                case "solver":
                    column.SolvingMethodName = value.ToString();
                    return true;
                case "maxIterations":
                    column.MaxIterations = Math.Max(1, (int)Math.Round(Num()));
                    return true;
                case "tolerance":
                    var tol = Num();
                    if (tol <= 0) throw new RpcException(RpcErrorCodes.InvalidParams, "The tolerance must be above zero.");
                    column.ExternalLoopTolerance = tol;
                    column.InternalLoopTolerance = tol;
                    return true;
                case "noCondenser":
                    ((DistillationColumn)column).ReboiledAbsorber = value.Value<bool>();
                    return true;
                case "noReboiler":
                    ((DistillationColumn)column).RefluxedAbsorber = value.Value<bool>();
                    return true;
                case "condenserType":
                    column.CondenserType = (Column.condtype)int.Parse(value.ToString(), CultureInfo.InvariantCulture);
                    return true;
                case "condenserDeltaP":
                    column.CondenserDeltaP = Si(su.deltaP);
                    return true;
                case "vaporFlow":
                    column.VaporFlowRate = Num();
                    if (!string.IsNullOrEmpty(unit)) column.VaporFlowRateUnit = unit;
                    return true;
                case "subcooling":
                    ((DistillationColumn)column).TotalCondenserSubcoolingDeltaT = Si(su.deltaT);
                    return true;
                case "useT": column.UseTemperatureEstimates = value.Value<bool>(); return true;
                case "useL": column.UseLiquidFlowEstimates = value.Value<bool>(); return true;
                case "useV": column.UseVaporFlowEstimates = value.Value<bool>(); return true;
                case "useX": column.UseCompositionEstimates = value.Value<bool>(); return true;
                case "autoUpdate": column.AutoUpdateInitialEstimates = value.Value<bool>(); return true;
                case "readEstimates":
                    ReadEstimates(fs, column);
                    return true;
                case "stageTable":
                    SetStages(su, column, value);
                    return true;
                case "streamTable":
                    SetStreams(column, value);
                    return true;
            }

            // condenser and reboiler specs: C.type, C.value, C.basis, C.compound (R.* alike)
            var dot = name.IndexOf('.');
            if (dot == 1 && (name[0] == 'C' || name[0] == 'R'))
            {
                var key = name.Substring(0, 1);
                if (!column.Specs.TryGetValue(key, out var spec)) throw new RpcException(RpcErrorCodes.EngineError, "The column has no " + (key == "C" ? "condenser" : "reboiler") + " specification.");
                switch (name.Substring(2))
                {
                    case "type":
                        var t = (ColumnSpec.SpecType)int.Parse(value.ToString(), CultureInfo.InvariantCulture);
                        if (!Enum.IsDefined(typeof(ColumnSpec.SpecType), t)) throw new RpcException(RpcErrorCodes.InvalidParams, "Unknown specification type.");
                        spec.SType = t;
                        // a new type takes its first unit, as the Avalonia editor does
                        spec.SpecUnit = SpecUnits(t)[0];
                        return true;
                    case "value":
                        var units = SpecUnits(spec.SType);
                        if (!string.IsNullOrEmpty(unit) && spec.SType != ColumnSpec.SpecType.Component_Fraction)
                        {
                            if (!units.Contains(unit)) throw new RpcException(RpcErrorCodes.InvalidParams, $"'{unit}' is not a unit of this specification.");
                            spec.SpecUnit = unit;
                        }
                        else if (string.IsNullOrEmpty(spec.SpecUnit) || !units.Contains(spec.SpecUnit))
                        {
                            spec.SpecUnit = units[0];
                        }
                        spec.SpecValue = Num();
                        return true;
                    case "basis":
                        spec.SpecUnit = value.ToString() == "Mass" ? "Mass" : "Molar";
                        return true;
                    case "compound":
                        var compound = value.ToString();
                        if (!fs.SelectedCompounds.ContainsKey(compound)) throw new RpcException(RpcErrorCodes.InvalidParams, compound + " is not one of the flowsheet's compounds.");
                        spec.ComponentID = compound;
                        return true;
                }
            }
            throw new RpcException(RpcErrorCodes.EngineError, "No column field '" + name + "'.");
        }

        private static void SetStages(IUnitsOfMeasure su, Column column, JToken value)
        {
            if (!(value is JArray rows) || rows.Count != column.Stages.Count)
                throw new RpcException(RpcErrorCodes.InvalidParams, "Send one row per stage; change the number of stages under Column.");
            var customPressure = false;
            for (int i = 0; i < rows.Count; i++)
            {
                var efficiency = rows[i].Value<double?>("efficiency");
                if (efficiency.HasValue)
                {
                    if (efficiency <= 0 || efficiency > 1) throw new RpcException(RpcErrorCodes.InvalidParams, $"{column.Stages[i].Name}: the efficiency is between 0 and 1.");
                    column.Stages[i].Efficiency = efficiency.Value;
                }
                var pressure = rows[i].Value<double?>("pressure");
                if (!pressure.HasValue) continue;
                var p = Converter.ConvertToSI(su.pressure, pressure.Value);
                if (p <= 0) throw new RpcException(RpcErrorCodes.InvalidParams, $"{column.Stages[i].Name}: the pressure must be above zero.");
                // Only a pressure that actually changed makes the profile custom.
                if (Math.Abs(p - column.Stages[i].P) > 1e-6 * Math.Max(1.0, Math.Abs(p)))
                {
                    if (i == 0 && !customPressure) column.SetTopStagePressure(p);
                    else
                    {
                        customPressure = true;
                        column.ColumnPressureDrop = double.NaN;
                        column.Stages[i].P = p;
                    }
                }
            }
        }

        private static void SetStreams(Column column, JToken value)
        {
            if (!(value is JArray rows)) throw new RpcException(RpcErrorCodes.InvalidParams, "The stream stages are a list of rows.");
            var stageIds = new HashSet<string>(column.Stages.Select(s => s.ID));
            foreach (var row in rows)
            {
                var key = row.Value<string>("key");
                if (key == null || !column.MaterialStreams.TryGetValue(key, out var si))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Unknown stream row: " + key);
                var stage = row.Value<string>("stage");
                if (!string.IsNullOrEmpty(stage))
                {
                    if (!stageIds.Contains(stage)) throw new RpcException(RpcErrorCodes.InvalidParams, "Unknown stage: " + stage);
                    si.AssociatedStage = stage;
                }
                if (si.StreamBehavior != StreamInformation.Behavior.Sidedraw) continue;
                var phase = row.Value<string>("phase");
                if (phase == "L" || phase == "V") si.StreamPhase = phase == "L" ? StreamInformation.Phase.L : StreamInformation.Phase.V;
                var flow = row.Value<double?>("flow");
                if (flow.HasValue)
                {
                    if (flow < 0) throw new RpcException(RpcErrorCodes.InvalidParams, "A side draw flow cannot be negative.");
                    if (si.FlowRate == null) si.FlowRate = new Parameter();
                    si.FlowRate.Value = flow.Value;
                }
            }
        }

        // ------------------------------------------------------------------ upkeep

        /// <summary>
        /// Registers streams connected on the canvas and gives any without a stage the stage the
        /// Avalonia editor would show first: feeds and side draws the middle, the distillate and
        /// overhead vapor the top, the bottoms the last. A column wired on the canvas then solves
        /// without a visit to its connections editor.
        /// </summary>
        public static void EnsureStreamStages(Column column)
        {
            try { column.SyncConnectedStreams(); } catch { /* a column with no flowsheet yet */ }
            if (column.Stages.Count == 0) return;
            var top = column.Stages[0].ID;
            var bottom = column.Stages[column.Stages.Count - 1].ID;
            var middle = column.Stages[column.Stages.Count / 2].ID;
            void Assign(IEnumerable<StreamInformation> streams)
            {
                foreach (var si in streams)
                {
                    if (!string.IsNullOrEmpty(si.AssociatedStage) && column.Stages.Any(s => s.ID == si.AssociatedStage)) continue;
                    switch (si.StreamBehavior)
                    {
                        case StreamInformation.Behavior.Distillate:
                        case StreamInformation.Behavior.OverheadVapor:
                        case StreamInformation.Behavior.FreeWater:
                            si.AssociatedStage = top; break;
                        case StreamInformation.Behavior.BottomsLiquid:
                            si.AssociatedStage = bottom; break;
                        default:
                            si.AssociatedStage = middle; break;
                    }
                }
            }
            Assign(column.MaterialStreams.Values);
            Assign(column.EnergyStreams.Values);
        }

        /// <summary>Rebuilds the estimates when they no longer match the stages or the compounds.</summary>
        private static void EnsureEstimates(IFlowsheet fs, Column column, bool force = false)
        {
            var ie = column.InitialEstimates;
            var stale = force || ie == null || ie.StageTemps.Count != column.Stages.Count
                        || ie.LiqCompositions.Count != column.Stages.Count
                        || (ie.LiqCompositions.Count > 0 && ie.LiqCompositions[0].Count != fs.SelectedCompounds.Count);
            if (!stale) return;
            try { column.InitialEstimates = column.RebuildEstimates(); } catch { /* not enough set up yet */ }
        }

        /// <summary>Copies the solved profiles into the estimates, as the Avalonia estimates editor does.</summary>
        private static void ReadEstimates(IFlowsheet fs, Column column)
        {
            if (column.Tf == null || column.Tf.Length == 0)
                throw new RpcException(RpcErrorCodes.EngineError, "There is no solution to read from. Solve the column first.");
            EnsureEstimates(fs, column);
            var ie = column.InitialEstimates;
            var compounds = fs.SelectedCompounds.Keys.ToList();
            for (int i = 0; i < column.Stages.Count; i++)
            {
                if (i < column.Tf.Length) ie.StageTemps[i].Value = column.Tf[i];
                if (i < column.Vf.Length) ie.VapMolarFlows[i].Value = column.Vf[i];
                if (i < column.Lf.Length) ie.LiqMolarFlows[i].Value = column.Lf[i];
                var x = i < column.xf.Count ? column.xf[i] as double[] : null;
                var y = i < column.yf.Count ? column.yf[i] as double[] : null;
                for (int j = 0; j < compounds.Count; j++)
                {
                    if (x != null && j < x.Length && ie.LiqCompositions[i].ContainsKey(compounds[j])) ie.LiqCompositions[i][compounds[j]].Value = x[j];
                    if (y != null && j < y.Length && ie.VapCompositions[i].ContainsKey(compounds[j])) ie.VapCompositions[i][compounds[j]].Value = y[j];
                }
            }
        }

        // ------------------------------------------------------------------ degrees of freedom

        /// <summary>
        /// The engine's analysis decides on the object type whether a column has a condenser and
        /// a reboiler; a distillation column flagged as an absorber lacks one, so its slot goes.
        /// </summary>
        public static ObjectDegreesOfFreedom Adjust(ObjectDegreesOfFreedom dof, Column column)
        {
            var drop = new HashSet<string>();
            if (!HasCondenser(column)) { drop.Add("Specs[C]"); drop.Add("CondenserType"); }
            if (column is DistillationColumn && !HasReboiler(column)) drop.Add("Specs[R]");
            if (drop.Count == 0) return dof;
            return new ObjectDegreesOfFreedom(dof.ObjectTag, dof.ObjectType, dof.Mode, dof.Supported,
                dof.Slots.Where(s => !drop.Contains(s.Property)).ToList(), dof.Note);
        }

        /// <summary>The field a degrees-of-freedom slot names, so it can be badged.</summary>
        public static string SlotField(string slotProperty)
        {
            switch (slotProperty)
            {
                case "NumberOfStages": return Prefix + "stages";
                case "Stages[0].P": return Prefix + "topPressure";
                case "Stages[N].P": return Prefix + "pressureDrop";
                case "Specs[C]": return Prefix + "C.value";
                case "Specs[R]": return Prefix + "R.value";
                default: return null;
            }
        }

        // ------------------------------------------------------------------ builders

        private static JObject Number(string id, string label, double? value, string unit, IEnumerable<string> units = null, string hint = null, bool siValue = true)
        {
            // Values are shown in their unit; siValue says the value given is SI and is converted here.
            var shown = value.HasValue && siValue && !string.IsNullOrEmpty(unit) ? Converter.ConvertFromSI(unit, value.Value) : value;
            var field = new JObject
            {
                ["id"] = Prefix + id,
                ["label"] = label,
                ["kind"] = "number",
                ["value"] = shown.HasValue && !double.IsNaN(shown.Value) && !double.IsInfinity(shown.Value) ? (JToken)Round(shown.Value) : null,
                ["unit"] = string.IsNullOrEmpty(unit) ? null : unit,
                ["editable"] = true
            };
            var list = units?.Distinct().ToList();
            if (list != null && list.Count > 1) field["units"] = new JArray(list);
            if (hint != null) field["hint"] = hint;
            return field;
        }

        private static JObject Choice(string id, string label, string value, params (string value, string label)[] options) => new JObject
        {
            ["id"] = Prefix + id,
            ["label"] = label,
            ["kind"] = "choice",
            ["value"] = value,
            ["options"] = new JArray(options.Select(o => Opt(o.value, o.label))),
            ["editable"] = true
        };

        private static JObject Bool(string id, string label, bool value) => new JObject
        {
            ["id"] = Prefix + id,
            ["label"] = label,
            ["kind"] = "bool",
            ["value"] = value,
            ["editable"] = true
        };

        private static JObject Col(string key, string label, string kind, bool editable, string unit = null)
        {
            var c = new JObject { ["key"] = key, ["label"] = label, ["kind"] = kind, ["editable"] = editable };
            if (!string.IsNullOrEmpty(unit)) c["unit"] = unit;
            return c;
        }

        private static JObject Opt(string value, string label) => new JObject { ["value"] = value, ["label"] = label };

        private static List<string> UnitsOf(IUnitsOfMeasure su, string unit)
        {
            try
            {
                var set = su.GetUnitSet(su.GetUnitType(unit));
                var list = set?.Distinct().ToList() ?? new List<string>();
                if (!list.Contains(unit)) list.Insert(0, unit);
                return list;
            }
            catch
            {
                return new List<string> { unit };
            }
        }

        private static JToken Round(double v) => double.IsNaN(v) || double.IsInfinity(v) ? JValue.CreateNull() : new JValue(Math.Round(v, 10));
    }
}
