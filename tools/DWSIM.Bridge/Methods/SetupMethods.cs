using System;
using System.Collections.Generic;
using System.Linq;
using DWSIM.Automation.FluentAPI;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces.Enums;
using DWSIM.Thermodynamics.Streams;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// What a simulation is set up with before anything is drawn: its compounds, its property
    /// package and the conditions of its feeds. The new-simulation wizard is built on these.
    /// </summary>
    public sealed class SetupMethods
    {
        private readonly SessionStore _sessions;

        public SetupMethods(SessionStore sessions) { _sessions = sessions; }

        /// <summary>Compounds in the databases whose name, formula or CAS number contains <paramref name="query"/>.</summary>
        [RpcMethod("compounds.search")]
        public JArray SearchCompounds(string flowsheetId, string query = "", int limit = 50)
        {
            var session = _sessions.Get(flowsheetId);
            var q = (query ?? "").Trim();
            lock (session.Gate)
            {
                var all = session.Inner.AvailableCompounds.Values;
                var hits = string.IsNullOrEmpty(q)
                    ? all
                    : all.Where(c => Contains(c.Name, q) || Contains(c.Formula, q) || Contains(c.CAS_Number, q));
                return new JArray(hits
                    .OrderBy(c => string.Equals(c.Name, q, StringComparison.OrdinalIgnoreCase) ? 0 : c.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(1, limit))
                    .Select(c => new JObject
                    {
                        ["name"] = c.Name,
                        ["formula"] = c.Formula,
                        ["cas"] = c.CAS_Number,
                        ["molecularWeight"] = c.Molar_Weight,
                        ["database"] = c.OriginalDB
                    }));
            }
        }

        /// <summary>The property packages a flowsheet can be given, by name.</summary>
        [RpcMethod("propertyPackages.list")]
        public JArray ListPropertyPackages(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
                return new JArray(session.Flowsheet.AvailablePropertyPackages.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>Adds compounds by database name; ones already in the flowsheet are left alone.</summary>
        [RpcMethod("flowsheet.addCompounds")]
        public JObject AddCompounds(string flowsheetId, string[] names)
        {
            if (names == null || names.Length == 0) throw new RpcException(RpcErrorCodes.InvalidParams, "Name at least one compound.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var missing = names.Where(n => !session.Inner.AvailableCompounds.ContainsKey(n)).ToList();
                if (missing.Count > 0)
                    throw new RpcException(RpcErrorCodes.EngineError, "Not in the compound databases: " + string.Join(", ", missing));
                foreach (var name in names.Where(n => !session.Inner.SelectedCompounds.ContainsKey(n)))
                    session.Flowsheet.WithCompound(name);
                return FlowsheetMethods.Summary(session);
            }
        }

        /// <summary>
        /// Adds a property package by name. <paramref name="flashSettings"/> maps FlashSetting names
        /// (e.g. <c>HandleSolidsInDefaultEqCalcMode</c>) to values, as the package's flash settings
        /// dialog would set them.
        /// </summary>
        [RpcMethod("flowsheet.addPropertyPackage")]
        public JObject AddPropertyPackage(string flowsheetId, string name, JObject flashSettings = null)
        {
            var session = _sessions.Get(flowsheetId);
            var settings = new List<(FlashSetting key, JToken value)>();
            foreach (var p in flashSettings?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                if (!Enum.TryParse<FlashSetting>(p.Name, true, out var key))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Unknown flash setting: " + p.Name);
                settings.Add((key, p.Value));
            }
            lock (session.Gate)
            {
                if (!session.Flowsheet.AvailablePropertyPackages.Contains(name))
                    throw new RpcException(RpcErrorCodes.EngineError, "No property package named '" + name + "'.");
                try
                {
                    session.Flowsheet.WithPropertyPackage(name, pp =>
                    {
                        foreach (var (key, value) in settings)
                        {
                            switch (value.Type)
                            {
                                case JTokenType.Boolean: pp.WithFlashSetting(key, value.Value<bool>()); break;
                                case JTokenType.Integer:
                                case JTokenType.Float: pp.WithFlashSetting(key, value.Value<double>()); break;
                                default: pp.WithFlashSetting(key, value.ToString()); break;
                            }
                        }
                    });
                }
                catch (Exception ex) when (!(ex is RpcException))
                {
                    throw new RpcException(RpcErrorCodes.EngineError, ex.Message);
                }
                return FlowsheetMethods.Summary(session);
            }
        }

        /// <summary>
        /// Sets a material stream's conditions, in SI: temperature (K), pressure (Pa), vapor
        /// fraction, total molar (mol/s) or mass (kg/s) flow, and the composition as compound
        /// molar or mass flows. A composition given here is the whole composition: compounds not
        /// named are set to zero. A vapor fraction makes the stream specified by pressure and vapor
        /// fraction, or by temperature and vapor fraction when no pressure is given; otherwise a
        /// temperature and a pressure specify it.
        /// </summary>
        [RpcMethod("stream.specify")]
        public JObject Specify(string flowsheetId, string id,
            double? temperature = null, double? pressure = null, double? vaporFraction = null,
            double? molarFlow = null, double? massFlow = null,
            Dictionary<string, double> compoundMolarFlows = null, Dictionary<string, double> compoundMassFlows = null)
        {
            if (compoundMolarFlows != null && compoundMassFlows != null)
                throw new RpcException(RpcErrorCodes.InvalidParams, "Give the composition as molar flows or as mass flows, not both.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var obj = session.Object(id);
                if (!(obj is MaterialStream ms))
                    throw new RpcException(RpcErrorCodes.EngineError, id + " is not a material stream.");
                foreach (var c in (compoundMolarFlows ?? compoundMassFlows ?? new Dictionary<string, double>()).Keys)
                    if (!session.Inner.SelectedCompounds.ContainsKey(c))
                        throw new RpcException(RpcErrorCodes.EngineError, c + " is not one of the flowsheet's compounds.");

                // The same order the validation cases and the fluent API use: state, total flow,
                // compound flows (which fix the total when given), then the vapor fraction spec.
                var b = session.Flowsheet.MaterialStream(ms.GraphicObject.Tag);
                if (temperature.HasValue && pressure.HasValue) b.At(temperature.Value.Kelvin(), pressure.Value.Pascal());
                else if (temperature.HasValue) b.WithTemperature(temperature.Value.Kelvin());
                else if (pressure.HasValue) b.WithPressure(pressure.Value.Pascal());
                if (molarFlow.HasValue) b.WithMolarFlow(molarFlow.Value.MolPerSecond());
                if (massFlow.HasValue) b.WithMassFlow(massFlow.Value.KgPerSecond());

                if (compoundMolarFlows != null || compoundMassFlows != null)
                {
                    var molar = compoundMolarFlows != null;
                    var flows = compoundMolarFlows ?? compoundMassFlows;
                    foreach (var c in session.Inner.SelectedCompounds.Keys)
                    {
                        flows.TryGetValue(c, out var f);
                        if (molar) ms.SetOverallCompoundMolarFlow(c, f);
                        else ms.SetOverallCompoundMassFlow(c, f);
                    }
                }

                if (vaporFraction.HasValue)
                {
                    if (vaporFraction < 0 || vaporFraction > 1)
                        throw new RpcException(RpcErrorCodes.InvalidParams, "The vapor fraction must be between 0 and 1.");
                    ms.Phases[2].Properties.molarfraction = vaporFraction.Value;
                    ms.SpecType = pressure.HasValue || !temperature.HasValue
                        ? StreamSpec.Pressure_and_VaporFraction
                        : StreamSpec.Temperature_and_VaporFraction;
                }
                else if (temperature.HasValue || pressure.HasValue)
                {
                    ms.SpecType = StreamSpec.Temperature_and_Pressure;
                }

                ms.Calculated = false;
                return GraphMethods.Node(obj);
            }
        }

        private static bool Contains(string text, string q)
            => text != null && text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
