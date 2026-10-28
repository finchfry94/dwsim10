using System;
using System.Linq;
using DWSIM.Automation.FluentAPI;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.Thermodynamics.Streams;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// Reading and writing an object's properties. Until editors are described as schemas, the
    /// inspector shows the engine's own property list: every property id, its label, its value in
    /// the flowsheet's unit system and whether it can be written.
    /// </summary>
    public sealed class ObjectMethods
    {
        private readonly SessionStore _sessions;

        public ObjectMethods(SessionStore sessions) { _sessions = sessions; }

        [RpcMethod("object.properties")]
        public JObject Properties(string flowsheetId, string id)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var obj = session.Object(id);
                var su = session.Inner.FlowsheetOptions.SelectedUnitSystem;
                var list = new JArray();
                foreach (var entry in PropertyCatalog.For(obj, su))
                    list.Add(Entry(entry.Id, entry.Description, entry.Units, entry.Value, entry.Writable));
                return new JObject
                {
                    ["id"] = obj.Name,
                    ["tag"] = obj.GraphicObject?.Tag,
                    ["type"] = obj.GraphicObject?.ObjectType.ToString(),
                    ["properties"] = list
                };
            }
        }

        /// <summary>Sets one property, in the flowsheet's unit system, and returns its new state.</summary>
        [RpcMethod("object.setProperty")]
        public JObject SetProperty(string flowsheetId, string id, string property, JToken value)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var obj = session.Object(id);
                var su = session.Inner.FlowsheetOptions.SelectedUnitSystem;
                object raw;
                switch (value?.Type)
                {
                    case JTokenType.Integer:
                    case JTokenType.Float: raw = value.Value<double>(); break;
                    case JTokenType.Boolean: raw = value.Value<bool>(); break;
                    case null:
                    case JTokenType.Null: throw new RpcException(RpcErrorCodes.InvalidParams, "A value is required.");
                    default: raw = value.ToString(); break;
                }
                bool ok;
                try
                {
                    ok = obj.SetPropertyValue(property, raw, su);
                }
                catch (Exception ex)
                {
                    throw new RpcException(RpcErrorCodes.EngineError, ex.Message);
                }
                if (!ok) throw new RpcException(RpcErrorCodes.EngineError, $"'{property}' cannot be set on {obj.GraphicObject?.Tag}.");
                return Entry(property, Describe(session.Inner, property), SafeUnit(obj, property, su),
                    SafeValue(obj, property, su), true);
            }
        }

        [RpcMethod("object.rename")]
        public JObject Rename(string flowsheetId, string id, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) throw new RpcException(RpcErrorCodes.InvalidParams, "The tag cannot be empty.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var obj = session.Object(id);
                obj.GraphicObject.Tag = tag.Trim();
                return GraphMethods.Node(obj);
            }
        }

        /// <summary>
        /// A material stream's state and composition by phase, in SI: K, Pa, kg/s, mol/s, kJ/kg,
        /// kg/m3. An energy stream returns its power in kW.
        /// </summary>
        [RpcMethod("stream.get")]
        public JObject Stream(string flowsheetId, string id)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var obj = session.Object(id);
                if (obj is IEnergyStream es)
                    return new JObject { ["id"] = obj.Name, ["tag"] = obj.GraphicObject?.Tag, ["kind"] = "energy", ["power_kW"] = (obj as DWSIM.SharedClasses.UnitOperations.BaseClass)?.EnergyFlow };
                if (!(obj is MaterialStream ms))
                    throw new RpcException(RpcErrorCodes.EngineError, obj.GraphicObject?.Tag + " is not a stream.");

                var phases = new JArray();
                foreach (var kv in ms.Phases.OrderBy(p => p.Key))
                {
                    var phase = kv.Value;
                    var p = phase.Properties;
                    // Absent phases still sit in the dictionary; skip all but the mixture when empty.
                    if (kv.Key != 0 && p.molarfraction.GetValueOrDefault() <= 0) continue;
                    phases.Add(new JObject
                    {
                        ["index"] = kv.Key,
                        ["name"] = phase.Name,
                        ["moleFraction"] = p.molarfraction,
                        ["massFraction"] = p.massfraction,
                        ["massFlow"] = p.massflow,
                        ["molarFlow"] = p.molarflow,
                        ["volumetricFlow"] = p.volumetric_flow,
                        ["enthalpy"] = p.enthalpy,
                        ["entropy"] = p.entropy,
                        ["density"] = p.density,
                        ["molecularWeight"] = p.molecularWeight,
                        ["viscosity"] = p.viscosity,
                        ["heatCapacityCp"] = p.heatCapacityCp,
                        ["compressibilityFactor"] = p.compressibilityFactor,
                        ["compounds"] = new JArray(phase.Compounds.Values.Select(c => new JObject
                        {
                            ["name"] = c.Name,
                            ["moleFraction"] = c.MoleFraction,
                            ["massFraction"] = c.MassFraction,
                            ["molarFlow"] = c.MolarFlow,
                            ["massFlow"] = c.MassFlow
                        }))
                    });
                }

                var mix = ms.Phases[0].Properties;
                return new JObject
                {
                    ["id"] = obj.Name,
                    ["tag"] = obj.GraphicObject?.Tag,
                    ["kind"] = "material",
                    ["calculated"] = obj.Calculated,
                    ["spec"] = ms.SpecType.ToString(),
                    ["temperature"] = mix.temperature,
                    ["pressure"] = mix.pressure,
                    ["massFlow"] = mix.massflow,
                    ["molarFlow"] = mix.molarflow,
                    ["vaporFraction"] = GraphMethods.VaporFraction(ms),
                    ["phases"] = phases
                };
            }
        }

        private static JObject Entry(string id, string label, string units, object value, bool writable)
        {
            JToken v;
            switch (value)
            {
                case null: v = JValue.CreateNull(); break;
                case double d when double.IsNaN(d) || double.IsInfinity(d): v = JValue.CreateNull(); break;
                case double _:
                case float _:
                case int _:
                case long _:
                case bool _:
                case string _: v = new JValue(value); break;
                default: v = new JValue(value.ToString()); break;
            }
            return new JObject
            {
                ["id"] = id,
                ["label"] = string.IsNullOrEmpty(label) ? id : label,
                ["units"] = string.IsNullOrEmpty(units) ? null : units,
                ["value"] = v,
                ["writable"] = writable
            };
        }

        private static string Describe(IFlowsheet fs, string id)
        {
            try { return fs.GetTranslatedString(id); } catch { return id; }
        }

        private static string SafeUnit(ISimulationObject obj, string id, IUnitsOfMeasure su)
        {
            try { return obj.GetPropertyUnit(id, su); } catch { return null; }
        }

        private static object SafeValue(ISimulationObject obj, string id, IUnitsOfMeasure su)
        {
            try { return obj.GetPropertyValue(id, su); } catch { return null; }
        }
    }
}
