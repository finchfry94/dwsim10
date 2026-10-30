using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.SharedClasses.SystemsOfUnits;
using DWSIM.Thermodynamics.Streams;
using DWSIM.Thermodynamics.Utilities.Hydrates;
using DWSIM.Thermodynamics.Utilities.Sizing;
using Newtonsoft.Json.Linq;
using PSV = DWSIM.Thermodynamics.Utilities.PSV;

namespace DWSIM.Bridge.Tools
{
    /// <summary>Hydrate formation for a stream at its T and P, as the Avalonia HydratesWindow runs it.</summary>
    internal sealed class HydrateTool : ITool
    {
        public string Id => "hydrates";
        public string Title => "Natural gas hydrates";
        public string Group => "Safety";
        public string Description => "Whether hydrates form at a stream's temperature and pressure, and where they start to. The stream must contain water.";

        public IEnumerable<JObject> Inputs(IFlowsheet fs)
        {
            yield return Field.Objects("stream", "Material stream", fs, ObjectType.MaterialStream);
            yield return Field.Choice("model", "Model", "0", new[]
            {
                ("0", "van der Waals - Platteeuw"), ("1", "Klauda - Sandler"), ("2", "Chen - Guo"), ("3", "Klauda - Sandler (modified)")
            });
            yield return Field.Bool("vaporOnly", "Vapor-hydrate equilibrium only (no free water)", false);
        }

        public void Run(IFlowsheet fs, ToolInputs inputs, ToolResult r, CancellationToken cancellation)
        {
            var ms = inputs.Object<MaterialStream>("stream", "a material stream");
            if (!ms.Phases[0].Compounds.Any(c => c.Key.Equals("Water", StringComparison.OrdinalIgnoreCase) || c.Key.Equals("Agua", StringComparison.OrdinalIgnoreCase)))
                throw new RpcException(RpcErrorCodes.InvalidParams, ms.GraphicObject.Tag + " has no water; hydrates need it.");
            var model = (HydrateModel)int.Parse(inputs.Text("model", "0"));
            var vaporOnly = inputs.Bool("vaporOnly");
            var res = HydrateCalculator.Calculate(ms, model, vaporOnly);
            var su = r.Units;

            r.Summary = res.FormsHydrate ? $"Hydrates ({res.FormedStructure}) form at the stream conditions." : "No hydrates form at the stream conditions.";
            r.Section("Stream conditions").Value("Temperature", res.StreamTemperature, su.temperature).Value("Pressure", res.StreamPressure, su.pressure);
            r.Section("At the stream temperature");
            if (res.FormationPressure > 600 * 101325) r.Text("Formation pressure", "not found below 600 atm");
            else r.Value("Formation pressure", res.FormationPressure, su.pressure);
            r.Text("Structure", res.StructureAtStreamTemperature)
             .Text("Phases", Phases(vaporOnly, res.StructureAtStreamTemperature, res.StreamTemperature, res.DetailsAtStreamTemperature));
            r.Section("At the stream pressure");
            if (res.FormationTemperature < 0) r.Text("Formation temperature", "not found");
            else r.Value("Formation temperature", res.FormationTemperature, su.temperature);
            r.Text("Structure", res.StructureAtStreamPressure)
             .Text("Phases", Phases(vaporOnly, res.StructureAtStreamPressure, res.FormationTemperature, res.DetailsAtStreamPressure));
            r.Section("Verdict").Text("Hydrates form", res.FormsHydrate ? "Yes" : "No").Text("Structure", res.FormsHydrate ? res.FormedStructure : "—");

            // The hydrate phase composition at the stream temperature, as the window lists it.
            var details = res.DetailsAtStreamTemperature;
            if (details != null && details.Length >= 7 && details[6] is Array vh)
            {
                var names = fs.SelectedCompounds.Keys.ToList();
                var rows = new JArray();
                for (int i = 0; i < Math.Min(names.Count, vh.Length); i++)
                {
                    var x = Convert.ToDouble(vh.GetValue(i));
                    if (x > 0) rows.Add(new JArray(names[i], x));
                }
                if (rows.Count > 0)
                    r.Tables.Add(new JObject
                    {
                        ["title"] = "Hydrate phase composition (at the stream temperature)",
                        ["columns"] = new JArray("Compound", "Mole fraction"),
                        ["rows"] = rows
                    });
            }
        }

        private static string Phases(bool vaporOnly, string structure, double t, object[] details)
        {
            if (vaporOnly) return $"vapor and hydrate ({structure})";
            if (details == null || details.Length == 0) return $"hydrate ({structure})";
            var t0 = Convert.ToDouble(details[0]);
            if (Math.Abs(t - t0) < 0.1) return $"ice, liquid water, gas and hydrate ({structure})";
            return t < t0 ? $"ice, gas and hydrate ({structure})" : $"liquid water, gas and hydrate ({structure})";
        }
    }

    /// <summary>Relief valve orifice sizing by API RP 520, as the Avalonia PsvSizingWindow does it.</summary>
    internal sealed class PsvSizingTool : ITool
    {
        public string Id => "psv";
        public string Title => "Relief valve sizing (API 520)";
        public string Group => "Safety";
        public string Description => "The orifice a pressure safety valve needs. The valve's inlet stream sets the relieving conditions and the set pressure; its outlet stream sets the back pressure. Solve the flowsheet first.";

        public IEnumerable<JObject> Inputs(IFlowsheet fs)
        {
            yield return Field.Objects("valve", "Valve", fs, ObjectType.Valve);
            yield return Field.Choice("fluid", "Relieving fluid", "1", new[] { ("0", "Liquid"), ("1", "Vapor"), ("2", "Two-phase (gas-liquid)") });
            yield return Field.Number("kd", "Discharge coefficient Kd", 0.85, null);
            yield return Field.Number("kb", "Back pressure correction Kb (Kw for liquid)", 1.0, null);
            yield return Field.Number("kc", "Rupture disk combination Kc", 1.0, null);
            yield return Field.Number("overpressure", "Overpressure (% of gauge set pressure)", 10.0, null,
                hint: "Relieving pressure P1 = set pressure (gauge) × (1 + overpressure) + 1 atm, absolute.");
        }

        public void Run(IFlowsheet fs, ToolInputs inputs, ToolResult r, CancellationToken cancellation)
        {
            var valve = inputs.Object<ISimulationObject>("valve", "a valve");
            var go = valve.GraphicObject;
            if (!(go.InputConnectors.FirstOrDefault()?.IsAttached ?? false) || !(go.OutputConnectors.FirstOrDefault()?.IsAttached ?? false))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Connect the valve's inlet and outlet before sizing it.");
            var inlet = fs.SimulationObjects[go.InputConnectors[0].AttachedConnector.AttachedFrom.Name] as MaterialStream;
            var outlet = fs.SimulationObjects[go.OutputConnectors[0].AttachedConnector.AttachedTo.Name] as MaterialStream;
            if (inlet == null || outlet == null) throw new RpcException(RpcErrorCodes.InvalidParams, "The valve needs material streams at both ends.");
            if (!inlet.Calculated)
                throw new RpcException(RpcErrorCodes.InvalidParams, inlet.GraphicObject.Tag + " is not calculated; solve the flowsheet to size the valve on its conditions.");
            if (!outlet.Calculated)
                r.Warnings.Add("The outlet stream is not calculated, so its pressure may not be the back pressure; solve the flowsheet first.");

            double kd = inputs.Number("kd", 0.85), kb = inputs.Number("kb", 1.0), kc = inputs.Number("kc", 1.0), over = inputs.Number("overpressure", 10.0);
            double T = inlet.Phases[0].Properties.temperature.GetValueOrDefault();
            double P = inlet.Phases[0].Properties.pressure.GetValueOrDefault();
            double BP = outlet.Phases[0].Properties.pressure.GetValueOrDefault();
            double WT = inlet.Phases[0].Properties.massflow.GetValueOrDefault();
            double viscL = inlet.Phases[3].Properties.viscosity.GetValueOrDefault();
            double rhoL = inlet.Phases[3].Properties.density.GetValueOrDefault();
            double QL = inlet.Phases[3].Properties.volumetric_flow.GetValueOrDefault();
            double WV = inlet.Phases[2].Properties.massflow.GetValueOrDefault();
            double zg = inlet.Phases[2].Properties.compressibilityFactor.GetValueOrDefault();
            double cp = inlet.Phases[2].Properties.heatCapacityCp.GetValueOrDefault();
            double cv = inlet.Phases[2].Properties.heatCapacityCv.GetValueOrDefault();
            double k = cv == 0.0 ? 1.0 : cp / cv;
            double mw = inlet.Phases[2].Properties.molecularWeight.GetValueOrDefault();
            double P1 = PSV.Sizing.RelievingPressure(P, over);

            var su = r.Units;
            r.Section("Relieving conditions")
             .Value("Temperature", T, su.temperature)
             .Value("Set pressure (inlet, absolute)", P, su.pressure)
             .Value("Relieving pressure P1 (absolute)", P1, su.pressure)
             .Value("Back pressure (outlet, absolute)", BP, su.pressure);

            double area;
            switch (inputs.Text("fluid", "1"))
            {
                case "0":
                    area = PSV.Sizing.LiquidArea(QL, P1, BP, rhoL, viscL, kd, kb, kc);
                    break;
                case "2":
                    var v = PSV.Sizing.OmegaSpecificVolumes(inlet);
                    var tp = PSV.Sizing.TwoPhaseArea(v[0], v[1], P1, BP, WT, kd, kb, kc);
                    area = tp[0];
                    r.Shown("Omega parameter", tp[1], null).Text("Flow regime", tp[4] > 0.5 ? "Critical" : "Subcritical");
                    if (!(tp[1] > 0.0))
                        r.Warnings.Add("The mixture does not expand on the isentropic flash to 90 % of the inlet pressure, so the omega method does not apply. Size it as a liquid.");
                    break;
                default:
                    area = PSV.Sizing.GasArea(P1, BP, T, WV, zg, mw, k, kd, kb, kc);
                    r.Shown("Compressibility factor Z", zg, null).Shown("Cp/Cv", k, null)
                     .Text("Flow regime", BP <= PSV.Sizing.CriticalFlowPressure(P1, k) ? "Critical" : "Subcritical");
                    var warning = PSV.Sizing.IdealGasWarning(zg, k);
                    if (!string.IsNullOrEmpty(warning)) r.Warnings.Add(warning);
                    break;
            }
            if (double.IsNaN(area) || double.IsInfinity(area) || area <= 0)
                throw new RpcException(RpcErrorCodes.EngineError, r.Warnings.LastOrDefault() ?? "The orifice area could not be calculated; check the relieving conditions and the stream phases.");

            var orifice = PSV.Sizing.StandardOrifice(area);
            r.Section("Orifice")
             .Shown("Required area", area, "in2", $"{area * 645.16:N0} mm²")
             .Text("API designation", orifice.Item2 > 0 ? orifice.Item1 : "larger than T: use several valves");
            if (orifice.Item2 > 0) r.Shown("API orifice area", orifice.Item2, "in2", $"{orifice.Item2 * 645.16:N0} mm²");
            r.Summary = orifice.Item2 > 0 ? $"Orifice {orifice.Item1}: {area:N3} in² required." : $"{area:N3} in² required, more than a T orifice.";
        }
    }

    /// <summary>Gas-liquid separator sizing (Souders-Brown), as the Avalonia SeparatorSizingWindow does it.</summary>
    internal sealed class SeparatorSizingTool : ITool
    {
        public string Id => "separator";
        public string Title => "Gas-liquid separator sizing";
        public string Group => "Sizing";
        public string Description => "The smallest vertical or horizontal vessel for a separator's gas and liquid, and its nozzles. The separator must be connected and solved.";

        public IEnumerable<JObject> Inputs(IFlowsheet fs)
        {
            var d = new SeparatorSizingInput();
            yield return Field.Objects("vessel", "Separator", fs, ObjectType.Vessel);
            yield return Field.Choice("orientation", "Orientation", "vertical", new[] { ("vertical", "Vertical"), ("horizontal", "Horizontal") });
            yield return Field.Number("ld", "Length / diameter ratio", d.LengthToDiameter, null);
            yield return Field.Number("k", "Souders-Brown K (m/s)", d.KFactor, null);
            yield return Field.Number("gasVelocity", "Gas velocity (% of terminal)", d.GasVelocityPercent, null,
                hint: "Terminal velocity = K × √((ρL − ρG)/ρG); a lower percentage gives a wider vessel.");
            yield return Field.Number("nozzle", "Nozzle constant", d.NozzleConstant, null, hint: "Sizes the inlet and gas nozzles: v max = constant / √ρ.");
            yield return Field.Number("liquidVelocity", "Max. liquid nozzle velocity (m/s)", d.MaxLiquidVelocity, null);
            yield return Field.Number("residence", "Liquid residence time (min)", d.ResidenceTime, null);
            yield return Field.Number("surge", "Surge factor", d.SurgeFactor, null, hint: "Multiplies the gas and liquid flows before sizing.");
        }

        public void Run(IFlowsheet fs, ToolInputs inputs, ToolResult r, CancellationToken cancellation)
        {
            var vessel = inputs.Object<ISimulationObject>("vessel", "a separator");
            var d = new SeparatorSizingInput
            {
                LengthToDiameter = inputs.Number("ld", 3.0),
                KFactor = inputs.Number("k", 0.1),
                GasVelocityPercent = inputs.Number("gasVelocity", 75.0),
                NozzleConstant = inputs.Number("nozzle", 100.0),
                MaxLiquidVelocity = inputs.Number("liquidVelocity", 1.0),
                ResidenceTime = inputs.Number("residence", 5.0),
                SurgeFactor = inputs.Number("surge", 1.0)
            };
            if (!SeparatorSizing.ReadStreams(fs, vessel, d))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Connect the separator's inlet, gas outlet and liquid outlet before sizing it.");
            if (d.VaporDensity <= 0 || d.LiquidDensity <= 0)
                throw new RpcException(RpcErrorCodes.EngineError, "Solve the flowsheet first: the outlet stream densities are zero.");
            var horizontal = inputs.Text("orientation") == "horizontal";
            var res = horizontal ? SeparatorSizing.SizeHorizontal(d) : SeparatorSizing.SizeVertical(d);
            var su = r.Units;
            r.Section("Flows")
             .Value("Liquid volumetric flow", d.LiquidVolumetricFlow, su.volumetricFlow).Value("Liquid density", d.LiquidDensity, su.density)
             .Value("Gas volumetric flow", d.VaporVolumetricFlow, su.volumetricFlow).Value("Gas density", d.VaporDensity, su.density);
            r.Section("Vessel")
             .Shown("Minimum diameter", res.Diameter, "mm")
             .Shown(horizontal ? "Minimum length" : "Minimum height", res.Length, "mm");
            if (!horizontal)
                r.Text("Diameter set by", res.DiameterSetByLiquid ? "the liquid holdup" : "the gas velocity",
                    res.DiameterSetByLiquid ? "A shorter residence time or a higher L/D gives a narrower vessel." : null);
            r.Section("Nozzles").Shown("Inlet", res.InletNozzle, "in").Shown("Gas outlet", res.GasNozzle, "in").Shown("Liquid outlet", res.LiquidNozzle, "in");
            r.Summary = $"{(horizontal ? "Horizontal" : "Vertical")} vessel {res.Diameter:N0} mm × {res.Length:N0} mm.";
        }
    }
}
