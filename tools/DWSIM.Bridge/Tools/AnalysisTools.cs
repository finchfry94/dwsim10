using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DWSIM.Automation.DynamicRunner.Depressurization;
using DWSIM.Automation.DynamicRunner.McCabeThiele;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.SharedClasses.SystemsOfUnits;
using DWSIM.Thermodynamics.Streams;
using DWSIM.Thermodynamics.Utilities.PetroleumProperties;
using Newtonsoft.Json.Linq;
using sc = DWSIM.Thermodynamics.ShortcutUtilities;

namespace DWSIM.Bridge.Tools
{
    /// <summary>TBP, ASTM D86/D1160/D2887 and EFV curves and petroleum properties, as StreamDistillationCurvesWindow runs them.</summary>
    internal sealed class DistillationCurvesTool : ITool
    {
        public string Id => "distillationCurves";
        public string Title => "Distillation curves";
        public string Group => "Petroleum";
        public string Description => "A stream's TBP, ASTM D86, D1160, D2887 and EFV curves, with its petroleum product properties (gravity, flash point, vapor pressures, cetane index…).";

        private static readonly string[] Names = { "TBP", "ASTM D86", "ASTM D1160", "ASTM D2887", "EFV" };

        public IEnumerable<JObject> Inputs(IFlowsheet fs)
        {
            var d = new DistillationCurveSettings();
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            yield return Field.Objects("stream", "Material stream", fs, ObjectType.MaterialStream);
            yield return Field.Bool("liquidOnly", "Use the liquid phase only", d.LiquidPhaseOnly);
            yield return Field.Choice("basis", "TBP basis", "0", new[] { ("0", "Liquid volume"), ("1", "Mass"), ("2", "Mole") });
            yield return new JObject
            {
                ["id"] = "percents", ["label"] = "Percents distilled", ["kind"] = "text",
                ["value"] = string.Join(", ", d.Percents.Select(p => p.ToString(CultureInfo.InvariantCulture))), ["editable"] = true
            };
            yield return Field.Number("d1160Pressure", "D1160 pressure", d.D1160Pressure, su.pressure, su);
            yield return Field.Choice("efvMethod", "EFV method", "0", new[] { ("0", "Flash with the property package"), ("1", "Riazi-Daubert (from D86)") });
            yield return Field.Number("efvPressure", "EFV pressure", d.EFVPressure, su.pressure, su);
        }

        public void Run(IFlowsheet fs, ToolInputs inputs, ToolResult r, CancellationToken cancellation)
        {
            var ms = inputs.Object<MaterialStream>("stream", "a material stream");
            var su = r.Units;
            double[] percents;
            try
            {
                percents = (inputs.Text("percents") ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            }
            catch { throw new RpcException(RpcErrorCodes.InvalidParams, "Write the percents as numbers separated by commas, e.g. 0, 10, 50, 90, 100."); }
            if (percents.Length < 2 || percents.Any(p => p < 0 || p > 100))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Give at least two percents between 0 and 100.");
            var settings = new DistillationCurveSettings
            {
                LiquidPhaseOnly = inputs.Bool("liquidOnly"),
                TBPBasis = (DistillationCurveBasis)int.Parse(inputs.Text("basis", "0")),
                Percents = percents.OrderBy(p => p).ToArray(),
                D1160Pressure = inputs.Number("d1160Pressure", 1333.224, su.pressure),
                EFVMethod = (EFVCalculationMethod)int.Parse(inputs.Text("efvMethod", "0")),
                EFVPressure = inputs.Number("efvPressure", 101325, su.pressure)
            };
            var res = DistillationCurves.Calculate(ms, settings);
            var types = new[] { DistillationCurveType.TBP, DistillationCurveType.ASTM_D86, DistillationCurveType.ASTM_D1160, DistillationCurveType.ASTM_D2887, DistillationCurveType.EFV };
            string Name(DistillationCurveType t, DistillationCurve c) =>
                Names[(int)t] + (t == DistillationCurveType.ASTM_D1160 || t == DistillationCurveType.EFV
                    ? $" @ {Converter.ConvertFromSI(su.pressure, c.Pressure):G4} {su.pressure}" : "");

            var chart = r.Chart(ms.GraphicObject.Tag + " distillation curves", ("Distilled", "%"), ("Temperature", su.temperature));
            foreach (var t in types)
                if (res.Curves.TryGetValue(t, out var c))
                    chart.Line(Name(t, c), c.Percents, c.Temperatures.Select(v => Converter.ConvertFromSI(su.temperature, v)));

            var tbp = res.Curves[DistillationCurveType.TBP];
            r.Table($"Curves ({su.temperature})", new[] { "% distilled" }.Concat(types.Where(res.Curves.ContainsKey).Select(t => Name(t, res.Curves[t]))),
                tbp.Percents.Select((p, i) => new object[] { p }.Concat(types.Where(res.Curves.ContainsKey)
                    .Select(t => (object)Converter.ConvertFromSI(su.temperature, res.Curves[t].Temperatures[i])))));

            var pr = res.Properties;
            r.Section("Product properties")
             .Value("Molecular weight", pr.MolecularWeight, su.molecularWeight)
             .Shown("Specific gravity 60F/60F", pr.SpecificGravity, null)
             .Shown("API gravity", pr.API, null)
             .Shown("Watson K", pr.WatsonK, null)
             .Value("Mean average boiling point", pr.MeanAverageBoilingPoint, su.temperature)
             .Value("Density @ 15 °C", pr.Density15C, su.density)
             .Value("Kinematic viscosity @ 40 °C", pr.KinematicViscosity40C, su.cinematic_viscosity)
             .Value("Kinematic viscosity @ 100 °C", pr.KinematicViscosity100C, su.cinematic_viscosity)
             .Value("Flash point, closed cup (D93)", pr.FlashPointClosedCup, su.temperature)
             .Value("Pour point", pr.PourPoint, su.temperature)
             .Value("Cloud point", pr.CloudPoint, su.temperature)
             .Value("Freezing point", pr.FreezingPoint, su.temperature)
             .Shown("Cetane index (D4737)", pr.CetaneIndexD4737, null)
             .Value("True vapor pressure @ 100 °F", pr.TrueVaporPressure, su.pressure)
             .Value("Reid vapor pressure", pr.ReidVaporPressure, su.pressure);
            r.Warnings.AddRange(res.Messages);
            r.Summary = $"TBP from {Converter.ConvertFromSI(su.temperature, tbp.Temperatures.First()):G4} to {Converter.ConvertFromSI(su.temperature, tbp.Temperatures.Last()):G4} {su.temperature}.";
        }
    }

    /// <summary>The McCabe-Thiele construction of a binary distillation, as McCabeThieleWindow draws it.</summary>
    internal sealed class McCabeThieleTool : ITool
    {
        public string Id => "mccabeThiele";
        public string Title => "McCabe–Thiele diagram";
        public string Group => "Columns";
        public string Description => "The equilibrium curve, operating lines, q-line and stages of a binary distillation, with the minimum reflux and stages. A shortcut column can fill in the keys, pressure, reflux and purities.";

        public IEnumerable<JObject> Inputs(IFlowsheet fs)
        {
            var d = new McCabeThieleInput();
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            var compounds = fs.SelectedCompounds.Keys.Select(c => (c, c)).ToList();
            var columns = Field.Objects("column", "Fill from a shortcut column", fs, ObjectType.ShortcutColumn);
            ((JArray)columns["options"]).Insert(0, new JObject { ["value"] = "", ["label"] = "None: use the values below" });
            columns["value"] = "";
            yield return columns;
            yield return Field.Choice("light", "Light key", compounds.FirstOrDefault().Item1, compounds);
            yield return Field.Choice("heavy", "Heavy key", compounds.Skip(1).FirstOrDefault().Item1, compounds);
            yield return Field.Number("pressure", "Column pressure", d.Pressure, su.pressure, su);
            yield return Field.Number("xf", "Feed composition xF (light key)", d.FeedComposition, null);
            yield return Field.Number("q", "Feed quality q", d.FeedQuality, null, hint: "1 saturated liquid, 0 saturated vapor.");
            yield return Field.Number("xd", "Distillate xD", d.DistillateComposition, null);
            yield return Field.Number("xb", "Bottoms xB", d.BottomsComposition, null);
            yield return Field.Number("reflux", "Reflux ratio R", d.RefluxRatio, null);
            yield return Field.Bool("refluxMultiple", "Reflux as a multiple of the minimum", d.RefluxAsMultipleOfMinimum);
            yield return Field.Number("multiplier", "R / Rmin", d.RefluxMultiplier, null);
            yield return Field.Number("murphree", "Murphree efficiency", d.MurphreeEfficiency, null);
        }

        public void Run(IFlowsheet fs, ToolInputs inputs, ToolResult r, CancellationToken cancellation)
        {
            var su = r.Units;
            var input = new McCabeThieleInput
            {
                LightKey = inputs.Text("light"),
                HeavyKey = inputs.Text("heavy"),
                Pressure = inputs.Number("pressure", 101325, su.pressure),
                FeedComposition = inputs.Number("xf", 0.5),
                FeedQuality = inputs.Number("q", 1.0),
                DistillateComposition = inputs.Number("xd", 0.95),
                BottomsComposition = inputs.Number("xb", 0.05),
                RefluxRatio = inputs.Number("reflux", 2.0),
                RefluxAsMultipleOfMinimum = inputs.Bool("refluxMultiple"),
                RefluxMultiplier = inputs.Number("multiplier", 1.5),
                MurphreeEfficiency = inputs.Number("murphree", 1.0)
            };
            var column = inputs.Text("column");
            if (!string.IsNullOrEmpty(column))
            {
                if (!fs.SimulationObjects.TryGetValue(column, out var c)) throw new RpcException(RpcErrorCodes.InvalidParams, "No such column.");
                McCabeThieleStudy.FillFromShortcutColumn(fs, c, input);
            }
            if (string.IsNullOrEmpty(input.LightKey) || input.LightKey == input.HeavyKey)
                throw new RpcException(RpcErrorCodes.InvalidParams, "Pick two different key compounds.");
            var res = McCabeThieleStudy.Run(fs, input, null, cancellation);

            var chart = r.Chart($"McCabe–Thiele: {input.LightKey} / {input.HeavyKey}", ("x, liquid " + input.LightKey, ""), ("y, vapor " + input.LightKey, ""));
            chart.Line("Equilibrium", res.EquilibriumX, res.EquilibriumY)
                 .Line("Stages", res.StairX, res.StairY)
                 .Line("Rectifying line", res.RectifyingX, res.RectifyingY)
                 .Line("Stripping line", res.StrippingX, res.StrippingY)
                 .Line("q-line", res.QLineX, res.QLineY)
                 .Line("y = x", new[] { 0.0, 1.0 }, new[] { 0.0, 1.0 })
                 .Points("Pinch", new[] { res.PinchX }, new[] { res.PinchY });
            r.Chart("T-x-y at " + Converter.ConvertFromSI(su.pressure, input.Pressure).ToString("G5", CultureInfo.InvariantCulture) + " " + su.pressure,
                    ("x, y " + input.LightKey, ""), ("Temperature", su.temperature))
             .Line("Bubble point", res.EquilibriumX, res.EquilibriumT.Select(t => Converter.ConvertFromSI(su.temperature, t)))
             .Line("Dew point", res.EquilibriumX, res.DewT.Select(t => Converter.ConvertFromSI(su.temperature, t)))
             .Points("Stages", res.Stages.Select(s => s.X), res.Stages.Select(s => Converter.ConvertFromSI(su.temperature, s.Temperature)));

            r.Section("Design")
             .Shown("Minimum reflux Rmin", res.MinimumReflux, null)
             .Shown("Reflux ratio R", res.RefluxRatio, null, res.MinimumReflux > 0 ? $"R/Rmin = {res.RefluxRatio / res.MinimumReflux:F2}" : null)
             .Shown("Minimum stages (total reflux)", res.MinimumStages, null, "including the reboiler")
             .Shown("Theoretical stages", res.TheoreticalStages, null, "including the reboiler")
             .Shown("Feed stage (from the top)", res.FeedStage, null);
            if (!double.IsNaN(res.Azeotrope)) r.Shown("Azeotrope at x", res.Azeotrope, null);
            r.Table("Stages", new[] { "Stage", "Section", "x", "y", "T (" + su.temperature + ")" },
                res.Stages.Select(s => new object[] { s.Number, s.Section, s.X, s.Y, Converter.ConvertFromSI(su.temperature, s.Temperature) }));
            r.Warnings.AddRange(res.Warnings);
            r.Summary = res.Feasible
                ? $"{res.TheoreticalStages} theoretical stages, feed on stage {res.FeedStage}; Rmin = {res.MinimumReflux:F3}."
                : "The construction could not be completed; see the warnings.";
        }
    }

    /// <summary>A stream's true critical point(s), from the Peng-Robinson package, as TrueCriticalPointWindow finds them.</summary>
    internal sealed class CriticalPointTool : ITool
    {
        public string Id => "criticalPoint";
        public string Title => "True critical point";
        public string Group => "Thermodynamics";
        public string Description => "The true critical point(s) of a stream's mixture. Needs the Peng-Robinson property package.";

        public IEnumerable<JObject> Inputs(IFlowsheet fs)
        {
            yield return Field.Objects("stream", "Material stream", fs, ObjectType.MaterialStream);
        }

        public void Run(IFlowsheet fs, ToolInputs inputs, ToolResult r, CancellationToken cancellation)
        {
            var ms = inputs.Object<MaterialStream>("stream", "a material stream");
            if (ms.PropertyPackage?.ComponentName != "Peng-Robinson (PR)")
                throw new RpcException(RpcErrorCodes.InvalidParams, $"{ms.GraphicObject.Tag} uses {ms.PropertyPackage?.ComponentName}; the true critical point needs Peng-Robinson (PR).");
            var res = new sc.Calculation(ms) { CalcType = sc.CalculationType.CriticalPoint }.Calculate();
            if (res.ExceptionResult != null) throw new RpcException(RpcErrorCodes.EngineError, (res.ExceptionResult.InnerException ?? res.ExceptionResult).Message);
            var su = r.Units;
            // The calculation reports in display units already, one entry per critical point found.
            if (!res.Data.TryGetValue("CriticalPoint", out var cp) || cp.Count < 3)
            {
                r.Summary = "No critical point was found for this mixture.";
                return;
            }
            r.Section("Critical point")
             .Shown("Temperature", cp[0], su.temperature)
             .Shown("Pressure", cp[1], su.pressure)
             .Shown("Molar volume", cp[2], su.molar_volume);
            r.Summary = $"Critical point at {cp[0]:G5} {su.temperature}, {cp[1]:G5} {su.pressure}.";
        }
    }

    /// <summary>A vessel blowdown (adiabatic, fire or isothermal), as DepressurizationWindow runs it.</summary>
    internal sealed class DepressurizationTool : ITool
    {
        public string Id => "depressurization";
        public string Title => "Vessel depressurization";
        public string Group => "Safety";
        public string Description => "Blows a vessel filled with a stream's mixture down through an orifice: pressure, temperatures and the release over time, with the minimum wall temperatures (adiabatic) or the API 521 fire case.";

        private static readonly string[] Heads = { "Ellipsoidal (2:1)", "Hemispherical", "Torispherical (ASME F&D)", "Torispherical (Standard F&D)", "Torispherical (80:10 F&D)", "Flat" };
        private static readonly string[] Materials = { "Carbon Steel", "Stainless Steel", "Steel", "Cast Iron", "Commercial Copper" };

        public IEnumerable<JObject> Inputs(IFlowsheet fs)
        {
            var d = new DepressurizationInput();
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            yield return Field.Objects("stream", "Content (composition of)", fs, ObjectType.MaterialStream);
            yield return Field.Choice("mode", "Case", "0", new[] { ("0", "Adiabatic (no fire)"), ("1", "API 521 pool fire"), ("2", "Isothermal") });
            yield return Field.Number("pressure", "Initial pressure", d.InitialPressure, su.pressure, su);
            yield return Field.Number("temperature", "Initial temperature", d.InitialTemperature, su.temperature, su);
            yield return Field.Number("liquidFraction", "Initial liquid volume fraction", d.InitialLiquidVolumeFraction, null);
            yield return Field.Choice("orientation", "Orientation", "vertical", new[] { ("vertical", "Vertical"), ("horizontal", "Horizontal") });
            yield return Field.Number("diameter", "Diameter", d.Diameter, su.diameter, su);
            yield return Field.Number("length", "Length or height (tangent to tangent)", d.Length, su.distance, su);
            yield return Field.Choice("head", "Head type", d.HeadType, Heads.Select(h => (h, h)));
            yield return Field.Number("wall", "Wall thickness", d.WallThickness, su.thickness, su);
            yield return Field.Choice("material", "Wall material", d.WallMaterial, Materials.Select(m => (m, m)));
            yield return Field.Number("orifice", "Orifice diameter", d.OrificeDiameter, su.diameter, su);
            yield return Field.Number("cd", "Discharge coefficient", d.DischargeCoefficient, null);
            yield return Field.Number("backPressure", "Back pressure", d.BackPressure, su.pressure, su);
            yield return Field.Number("duration", "Duration", d.Duration, su.time, su);
            yield return Field.Number("step", "Time step", d.TimeStep, su.time, su);
        }

        public void Run(IFlowsheet fs, ToolInputs inputs, ToolResult r, CancellationToken cancellation)
        {
            var ms = inputs.Object<MaterialStream>("stream", "a material stream");
            var su = r.Units;
            var input = new DepressurizationInput
            {
                SourceStreamName = ms.Name,
                Mode = (DepressurizationMode)int.Parse(inputs.Text("mode", "0")),
                InitialPressure = inputs.Number("pressure", 50e5, su.pressure),
                InitialTemperature = inputs.Number("temperature", 298.15, su.temperature),
                InitialLiquidVolumeFraction = inputs.Number("liquidFraction", 0.0),
                Horizontal = inputs.Text("orientation") == "horizontal",
                Diameter = inputs.Number("diameter", 1.5, su.diameter),
                Length = inputs.Number("length", 6.0, su.distance),
                HeadType = inputs.Text("head", "Ellipsoidal (2:1)"),
                WallThickness = inputs.Number("wall", 0.01, su.thickness),
                WallMaterial = inputs.Text("material", "Carbon Steel"),
                OrificeDiameter = inputs.Number("orifice", 0.02, su.diameter),
                DischargeCoefficient = inputs.Number("cd", 0.62),
                BackPressure = inputs.Number("backPressure", 101325, su.pressure),
                Duration = inputs.Number("duration", 900, su.time),
                TimeStep = inputs.Number("step", 1, su.time)
            };
            if (input.Duration / Math.Max(input.TimeStep, 1e-9) > 20000)
                throw new RpcException(RpcErrorCodes.InvalidParams, "That is more than 20,000 steps; lengthen the time step or shorten the duration.");
            var res = DepressurizationStudy.Run(fs, input, null, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var pts = res.Points;
            if (pts.Count == 0) throw new RpcException(RpcErrorCodes.EngineError, res.Warnings.FirstOrDefault() ?? "The blowdown produced no points.");

            double T(double k) => Converter.ConvertFromSI(su.temperature, k);
            var time = pts.Select(p => Converter.ConvertFromSI(su.time, p.Time)).ToList();
            r.Chart("Pressure", ("Time", su.time), ("Pressure", su.pressure))
             .Line("Vessel pressure", time, pts.Select(p => Converter.ConvertFromSI(su.pressure, p.Pressure)));
            var temps = r.Chart("Temperatures", ("Time", su.time), ("Temperature", su.temperature))
             .Line("Content", time, pts.Select(p => T(p.Temperature)));
            if (input.Mode != DepressurizationMode.Isothermal)
                temps.Line("Wetted wall", time, pts.Select(p => T(p.WettedWallTemperature)))
                     .Line("Dry wall", time, pts.Select(p => T(p.DryWallTemperature)));
            r.Chart("Release", ("Time", su.time), ("Mass flow", su.massflow))
             .Line("Mass flow through the orifice", time, pts.Select(p => Converter.ConvertFromSI(su.massflow, p.MassFlow)));

            r.Section("Vessel").Value("Volume", res.VesselVolume, su.volume).Value("Initial content", res.InitialMass, su.mass);
            r.Section("Blowdown")
             .Value("Final pressure", res.FinalPressure, su.pressure)
             .Value("Final temperature", res.FinalTemperature, su.temperature)
             .Value("Peak mass flow", res.PeakMassFlow, su.massflow)
             .Value("Mass released", res.TotalMassReleased, su.mass);
            if (res.TimeToHalfPressure.HasValue) r.Value("Time to half the gauge pressure", res.TimeToHalfPressure.Value, su.time);
            r.Section("Extremes")
             .Value("Minimum fluid temperature", res.MinimumFluidTemperature, su.temperature)
             .Value("Minimum wetted wall temperature", res.MinimumWettedWallTemperature, su.temperature)
             .Value("Minimum dry wall temperature", res.MinimumDryWallTemperature, su.temperature)
             .Value("Maximum dry wall temperature", res.MaximumDryWallTemperature, su.temperature);
            var every = Math.Max(1, pts.Count / 200);
            r.Table("Time series", new[] { $"Time ({su.time})", $"P ({su.pressure})", $"T ({su.temperature})", $"Wetted wall ({su.temperature})", $"Dry wall ({su.temperature})", $"Mass flow ({su.massflow})", $"Released ({su.mass})" },
                pts.Where((_, i) => i % every == 0 || i == pts.Count - 1).Select(p => new object[]
                {
                    Converter.ConvertFromSI(su.time, p.Time), Converter.ConvertFromSI(su.pressure, p.Pressure), T(p.Temperature),
                    T(p.WettedWallTemperature), T(p.DryWallTemperature), Converter.ConvertFromSI(su.massflow, p.MassFlow), Converter.ConvertFromSI(su.mass, p.CumulativeMass)
                }));
            r.Warnings.AddRange(res.Warnings);
            if (res.Aborted) r.Warnings.Add("The run stopped early.");
            r.Summary = $"From {Converter.ConvertFromSI(su.pressure, input.InitialPressure):G4} to {Converter.ConvertFromSI(su.pressure, res.FinalPressure):G4} {su.pressure} in {time.Last():G4} {su.time}; coldest content {T(res.MinimumFluidTemperature):G4} {su.temperature}.";
        }
    }
}
