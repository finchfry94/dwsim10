using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DWSIM.Automation.FluentAPI.Diagnostics;
using DWSIM.Bridge.Editors;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.SharedClasses.SystemsOfUnits;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// Unit operation editors, described as data for the frontend to render.
    /// </summary>
    /// <remarks>
    /// A schema is generated from the engine, not written per type:
    /// <list type="bullet">
    /// <item>the fields are the object's property ids (<c>GetProperties</c>), labelled, valued and
    /// united by the engine in the flowsheet's unit system;</item>
    /// <item>a property is an input when the engine lists it as writable (<c>PropertyType.WR</c>)
    /// in some calculation mode, and editable when it does in the current one. The writable set of
    /// every mode is read from a copy of the object, so the object itself is never touched;</item>
    /// <item>other settings are the object's enum-typed properties, found by reflection;</item>
    /// <item>the degrees-of-freedom analysis says which specifications are still missing.</item>
    /// </list>
    /// <see cref="EditorCatalog"/> adds the labels and order the engine does not carry.
    /// </remarks>
    public sealed class EditorMethods
    {
        private readonly SessionStore _sessions;

        /// <summary>Mode property names tried when the catalog names none, in order.</summary>
        private static readonly string[] ModeCandidates = { "CalcMode", "CalculationMode", "ReactorOperationMode", "OperationMode" };

        /// <summary>Property labels no editor shows: the mode has its own control.</summary>
        private static readonly HashSet<string> HiddenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Calculation Mode" };

        /// <summary>Enum settings no editor shows: the object's class is a drawing matter.</summary>
        private static readonly HashSet<string> HiddenChoices = new HashSet<string> { "ObjectClass" };

        private const string ChoicePrefix = "choice:";
        private const string DynamicPrefix = "dynamic:";

        public EditorMethods(SessionStore sessions) { _sessions = sessions; }

        [RpcMethod("object.schema")]
        public JObject Schema(string flowsheetId, string id)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate) return Build(session, session.Object(id));
        }

        /// <summary>
        /// Sets one field of a schema. <paramref name="field"/> is a field id from object.schema:
        /// a property id (value in <paramref name="unit"/>, or in the flowsheet's units when none
        /// is given), <c>choice:Name</c> (an enum value name) or <c>dynamic:Name</c> (SI).
        /// Returns the schema as it is after the change.
        /// </summary>
        [RpcMethod("object.setField")]
        public JObject SetField(string flowsheetId, string id, string field, JToken value, string unit = null)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var obj = session.Object(id);
                if (value == null || value.Type == JTokenType.Null)
                    throw new RpcException(RpcErrorCodes.InvalidParams, "A value is required.");
                try
                {
                    if (SpecialFields.TrySet(session.Inner, obj, field, value, unit)) { }
                    else if (field.StartsWith(ChoicePrefix, StringComparison.Ordinal))
                        SetEnum(obj, field.Substring(ChoicePrefix.Length), value.ToString());
                    else if (field.StartsWith(DynamicPrefix, StringComparison.Ordinal))
                        SetDynamic(obj, field.Substring(DynamicPrefix.Length), value);
                    else
                        SetProperty(session, obj, field, value, unit);
                }
                catch (RpcException) { throw; }
                catch (Exception ex)
                {
                    throw new RpcException(RpcErrorCodes.EngineError, ex.Message);
                }
                MarkChanged(obj);
                return Build(session, obj);
            }
        }

        /// <summary>Sets the calculation mode, by enum value name, and returns the new schema.</summary>
        [RpcMethod("object.setMode")]
        public JObject SetMode(string flowsheetId, string id, string value)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var obj = session.Object(id);
                var mode = ModeProperty(obj) ?? throw new RpcException(RpcErrorCodes.EngineError, obj.GraphicObject?.Tag + " has no calculation mode.");
                SetEnum(obj, mode.Name, value);
                MarkChanged(obj);
                return Build(session, obj);
            }
        }

        /// <summary>Converts a value between two of the engine's units of the same kind.</summary>
        [RpcMethod("units.convert")]
        public double Convert(double value, string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return value;
            try
            {
                return Converter.ConvertFromSI(to, Converter.ConvertToSI(from, value));
            }
            catch (Exception ex)
            {
                throw new RpcException(RpcErrorCodes.InvalidParams, $"Cannot convert {from} to {to}: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ schema

        private static JObject Build(Session session, ISimulationObject obj)
        {
            var fs = session.Inner;
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            var type = ObjectTypes.Name(obj);
            var entry = EditorCatalog.For(type);

            var dynamicIds = DynamicIds(obj);
            var all = SafeProperties(obj, PropertyType.ALL).Where(p => !dynamicIds.Contains(p)).Distinct().ToList();
            var writableNow = new HashSet<string>(SafeProperties(obj, PropertyType.WR));

            var mode = ModeProperty(obj, entry);
            var modeOptions = mode == null ? null : ModeOptions(mode, entry);
            var inputs = new HashSet<string>(writableNow);
            if (mode != null)
                foreach (var option in modeOptions)
                    inputs.UnionWith(WritableIn(obj, mode, option.value));

            var dof = Dof(fs, obj);
            var missing = MissingFields(dof, entry, all, fs);

            // A numeric copy of the calculation mode and other internals the editor shows another way.
            all = all.Where(p => !HiddenLabels.Contains(Label(fs, p)) && !entry.HiddenFields.Contains(Label(fs, p))).ToList();

            var spec = new JArray();
            foreach (var p in all.Where(inputs.Contains))
                spec.Add(PropertyField(fs, su, obj, p, writableNow.Contains(p), missing.Contains(p)));
            foreach (var choice in Choices(obj, entry, mode))
                spec.Add(choice);

            var results = new JArray();
            foreach (var p in all.Where(p => !inputs.Contains(p)))
                results.Add(PropertyField(fs, su, obj, p, false, false));

            var dynamics = new JArray();
            foreach (var key in dynamicIds) dynamics.Add(DynamicField(obj, key));

            var sections = new JArray { new JObject { ["id"] = "spec", ["title"] = "Specification", ["fields"] = spec } };
            foreach (var special in SpecialFields.For(fs, obj))
            {
                foreach (var f in special.Fields)
                    if (dof != null && dof.Missing.Any(m => string.Equals(m.Name, f.Value<string>("label"), StringComparison.OrdinalIgnoreCase)))
                        f["missing"] = true;
                sections.Add(new JObject { ["id"] = special.Id, ["title"] = special.Title, ["fields"] = new JArray(special.Fields) });
            }
            sections.Add(new JObject { ["id"] = "results", ["title"] = "Results", ["fields"] = results });
            if (dynamics.Count > 0)
                sections.Add(new JObject { ["id"] = "dynamics", ["title"] = "Dynamic mode", ["collapsed"] = true, ["fields"] = dynamics });

            return new JObject
            {
                ["id"] = obj.Name,
                ["tag"] = obj.GraphicObject?.Tag,
                ["type"] = type,
                ["calculated"] = obj.Calculated,
                ["error"] = string.IsNullOrEmpty(obj.ErrorMessage) ? null : obj.ErrorMessage,
                ["mode"] = mode == null ? null : new JObject
                {
                    ["property"] = mode.Name,
                    ["value"] = mode.GetValue(obj)?.ToString(),
                    ["options"] = new JArray(modeOptions.Select(o => new JObject { ["value"] = o.value, ["label"] = o.label }))
                },
                ["dof"] = dof == null ? null : new JObject
                {
                    ["supported"] = dof.Supported,
                    ["remaining"] = dof.Remaining,
                    ["note"] = string.IsNullOrEmpty(dof.Note) ? null : dof.Note,
                    ["slots"] = new JArray(dof.Slots.Select(s => new JObject
                    {
                        ["name"] = s.Name,
                        ["required"] = s.Required,
                        ["set"] = s.IsSet,
                        ["note"] = string.IsNullOrEmpty(s.Note) ? null : s.Note
                    }))
                },
                ["sections"] = sections,
                ["connections"] = Connections(fs, obj)
            };
        }

        private static JObject PropertyField(IFlowsheet fs, IUnitsOfMeasure su, ISimulationObject obj, string id, bool editable, bool missing)
        {
            object raw = null;
            string unit = null;
            try { raw = obj.GetPropertyValue(id, su); } catch { /* some properties need a solved object */ }
            try { unit = obj.GetPropertyUnit(id, su); } catch { }
            if (string.IsNullOrWhiteSpace(unit) || unit == "-") unit = null;

            string kind;
            JToken value;
            switch (raw)
            {
                case bool b: kind = "bool"; value = b; break;
                case double d: kind = "number"; value = double.IsNaN(d) || double.IsInfinity(d) ? null : (JToken)d; break;
                case float f: kind = "number"; value = (double)f; break;
                case int i: kind = "number"; value = i; break;
                case null: kind = unit != null ? "number" : "text"; value = null; break;
                default:
                    var s = raw.ToString();
                    if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        kind = "number"; value = parsed;
                    }
                    else
                    {
                        kind = "text"; value = s;
                    }
                    break;
            }

            var field = new JObject
            {
                ["id"] = id,
                ["label"] = Label(fs, id),
                ["kind"] = kind,
                ["value"] = value,
                ["unit"] = unit,
                ["editable"] = editable
            };
            if (unit != null)
            {
                var units = UnitChoices(su, unit);
                if (units.Count > 1) field["units"] = new JArray(units);
            }
            if (missing) field["missing"] = true;
            return field;
        }

        private static JObject DynamicField(ISimulationObject obj, string key)
        {
            var descriptions = (IDictionary<string, object>)obj.ExtraPropertiesDescriptions;
            var values = (IDictionary<string, object>)obj.ExtraProperties;
            values.TryGetValue(key, out var raw);
            string unit = null;
            try
            {
                var type = obj.GetDynamicPropertyUnitType(key);
                if (type != UnitOfMeasure.none) unit = new SI().GetCurrentUnits(type);
            }
            catch { }
            var isBool = raw is bool;
            return new JObject
            {
                ["id"] = DynamicPrefix + key,
                ["label"] = key,
                ["kind"] = isBool ? "bool" : raw is string ? "text" : "number",
                ["value"] = raw == null ? null : JToken.FromObject(raw),
                ["unit"] = string.IsNullOrEmpty(unit) ? null : unit,
                ["editable"] = true,
                ["hint"] = descriptions.TryGetValue(key, out var d) ? d?.ToString() : null
            };
        }

        private static IEnumerable<JObject> Choices(ISimulationObject obj, EditorCatalog.TypeEntry entry, PropertyInfo mode)
        {
            var engineAssemblies = new[] { typeof(DWSIM.UnitOperations.UnitOperations.Heater).Assembly, typeof(DWSIM.Thermodynamics.Streams.MaterialStream).Assembly };
            foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.PropertyType.IsEnum || !p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                if (mode != null && p.Name == mode.Name) continue;
                if (entry.HiddenChoices.Contains(p.Name) || HiddenChoices.Contains(p.Name)) continue;
                var declaredHere = engineAssemblies.Contains(p.DeclaringType.Assembly);
                var catalogued = entry.Choices.TryGetValue(p.Name, out var labels);
                // Settings declared by shared base classes (object class, dynamics specs) belong elsewhere.
                if (!declaredHere && !catalogued) continue;

                var options = catalogued && labels.options != null
                    ? labels.options.Where(o => Enum.IsDefined(p.PropertyType, o.value)).ToArray()
                    : Enum.GetNames(p.PropertyType).Select(n => (n, EditorCatalog.Humanize(n))).ToArray();
                yield return new JObject
                {
                    ["id"] = ChoicePrefix + p.Name,
                    ["label"] = catalogued ? labels.label : EditorCatalog.Humanize(p.Name),
                    ["kind"] = "choice",
                    ["value"] = p.GetValue(obj)?.ToString(),
                    ["options"] = new JArray(options.Select(o => new JObject { ["value"] = o.Item1, ["label"] = o.Item2 })),
                    ["editable"] = true
                };
            }
        }

        private static JArray Connections(IFlowsheet fs, ISimulationObject obj)
        {
            var go = obj.GraphicObject;
            var result = new JArray();
            if (go == null) return result;

            JArray Candidates(bool energy, bool asInlet) => new JArray(fs.SimulationObjects.Values
                .Where(o => o.GraphicObject != null && o.GraphicObject.ObjectType == (energy ? ObjectType.EnergyStream : ObjectType.MaterialStream))
                // an inlet takes a stream that flows nowhere yet; an outlet one that comes from nowhere
                .Where(o => asInlet ? !o.GraphicObject.OutputConnectors[0].IsAttached : !o.GraphicObject.InputConnectors[0].IsAttached)
                .OrderBy(o => o.GraphicObject.Tag, StringComparer.OrdinalIgnoreCase)
                .Select(o => new JObject { ["id"] = o.Name, ["tag"] = o.GraphicObject.Tag }));

            void Add(IConnectionPoint cp, string port, bool inlet, bool energy)
            {
                if (cp == null || !cp.Active && port == "energy") return;
                var other = cp.IsAttached && cp.AttachedConnector != null
                    ? (inlet ? cp.AttachedConnector.AttachedFrom : cp.AttachedConnector.AttachedTo)?.Owner
                    : null;
                result.Add(new JObject
                {
                    ["port"] = port,
                    ["label"] = string.IsNullOrEmpty(cp.ConnectorName) ? (inlet ? "Inlet" : "Outlet") + (energy ? " (energy)" : "") : cp.ConnectorName,
                    ["direction"] = inlet ? "in" : "out",
                    ["energy"] = energy,
                    ["stream"] = other == null ? null : new JObject { ["id"] = other.Name, ["tag"] = other.GraphicObject?.Tag },
                    ["candidates"] = other == null ? Candidates(energy, inlet) : new JArray()
                });
            }

            for (int i = 0; i < go.InputConnectors.Count; i++)
                Add(go.InputConnectors[i], "in-" + i, true, go.InputConnectors[i].Type == ConType.ConEn);
            for (int i = 0; i < go.OutputConnectors.Count; i++)
                Add(go.OutputConnectors[i], "out-" + i, false, go.OutputConnectors[i].Type == ConType.ConEn);
            if (go.EnergyConnector != null && go.EnergyConnector.Active)
                Add(go.EnergyConnector, "energy", false, true);
            return result;
        }

        // ------------------------------------------------------------------ modes

        private static PropertyInfo ModeProperty(ISimulationObject obj, EditorCatalog.TypeEntry entry = null)
        {
            entry ??= EditorCatalog.For(ObjectTypes.Name(obj));
            var names = entry.ModeProperty != null ? new[] { entry.ModeProperty }.Concat(ModeCandidates) : ModeCandidates;
            foreach (var name in names)
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.PropertyType.IsEnum && p.CanRead && p.CanWrite) return p;
            }
            return null;
        }

        private static (string value, string label)[] ModeOptions(PropertyInfo mode, EditorCatalog.TypeEntry entry)
        {
            if (entry.Modes != null && entry.ModeProperty == mode.Name)
                return entry.Modes.Where(m => Enum.IsDefined(mode.PropertyType, m.value)).ToArray();
            return Enum.GetNames(mode.PropertyType).Select(n => (n, EditorCatalog.Humanize(n))).ToArray();
        }

        /// <summary>The property ids the engine lets the user write in one mode, read on a copy.</summary>
        private static IEnumerable<string> WritableIn(ISimulationObject obj, PropertyInfo mode, string value)
        {
            try
            {
                if (!(obj.CloneXML() is ISimulationObject copy)) return Enumerable.Empty<string>();
                mode.SetValue(copy, Enum.Parse(mode.PropertyType, value));
                return SafeProperties(copy, PropertyType.WR).Where(p => p.StartsWith("PROP_", StringComparison.Ordinal)).ToList();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        // ------------------------------------------------------------------ writing

        private static void SetEnum(ISimulationObject obj, string property, string value)
        {
            var p = obj.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.PropertyType.IsEnum || !p.CanWrite)
                throw new RpcException(RpcErrorCodes.EngineError, "No setting named '" + property + "'.");
            if (!Enum.IsDefined(p.PropertyType, value))
                throw new RpcException(RpcErrorCodes.InvalidParams, $"'{value}' is not one of {string.Join(", ", Enum.GetNames(p.PropertyType))}.");
            p.SetValue(obj, Enum.Parse(p.PropertyType, value));
        }

        private static void SetDynamic(ISimulationObject obj, string key, JToken value)
        {
            var values = (IDictionary<string, object>)obj.ExtraProperties;
            if (!values.ContainsKey(key)) throw new RpcException(RpcErrorCodes.EngineError, "No dynamic property named '" + key + "'.");
            var current = values[key];
            values[key] = current switch
            {
                bool _ => value.Value<bool>(),
                int _ => (object)value.Value<int>(),
                string _ => value.ToString(),
                _ => value.Value<double>()
            };
        }

        private static void SetProperty(Session session, ISimulationObject obj, string id, JToken value, string unit)
        {
            if (!SafeProperties(obj, PropertyType.ALL).Contains(id))
                throw new RpcException(RpcErrorCodes.EngineError, $"{obj.GraphicObject?.Tag} has no property '{id}'.");
            if (!SafeProperties(obj, PropertyType.WR).Contains(id))
                throw new RpcException(RpcErrorCodes.EngineError, $"'{Label(session.Inner, id)}' is calculated in the current mode, not specified.");

            object raw;
            IUnitsOfMeasure su;
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                var number = value.Value<double>();
                if (!string.IsNullOrEmpty(unit))
                {
                    // The value comes in its own unit; hand the engine SI with the SI system.
                    raw = Converter.ConvertToSI(unit, number);
                    su = new SI();
                }
                else
                {
                    raw = number;
                    su = session.Inner.FlowsheetOptions.SelectedUnitSystem;
                }
            }
            else
            {
                raw = value.Type == JTokenType.Boolean ? (object)value.Value<bool>() : value.ToString();
                su = session.Inner.FlowsheetOptions.SelectedUnitSystem;
            }

            if (!obj.SetPropertyValue(id, raw, su))
                throw new RpcException(RpcErrorCodes.EngineError, $"'{Label(session.Inner, id)}' could not be set.");
        }

        private static void MarkChanged(ISimulationObject obj)
        {
            obj.Calculated = false;
            if (obj.GraphicObject != null) obj.GraphicObject.Calculated = false;
        }

        // ------------------------------------------------------------------ helpers

        private static ObjectDegreesOfFreedom Dof(IFlowsheet fs, ISimulationObject obj)
        {
            try { return DegreesOfFreedomAnalysis.Analyze(fs, obj); }
            catch { return null; }
        }

        /// <summary>The property ids of the specifications the analysis reports missing.</summary>
        private static HashSet<string> MissingFields(ObjectDegreesOfFreedom dof, EditorCatalog.TypeEntry entry, List<string> ids, IFlowsheet fs)
        {
            var result = new HashSet<string>();
            if (dof == null) return result;
            foreach (var slot in dof.Missing)
            {
                if (entry.SlotFields.TryGetValue(slot.Property, out var id)) result.Add(id);
                else
                {
                    // Without a mapping, a field whose label is the slot's name is the one.
                    var byLabel = ids.FirstOrDefault(p => string.Equals(Label(fs, p), slot.Name, StringComparison.OrdinalIgnoreCase));
                    if (byLabel != null) result.Add(byLabel);
                }
            }
            return result;
        }

        private static HashSet<string> DynamicIds(ISimulationObject obj)
        {
            try
            {
                var descriptions = (IDictionary<string, object>)obj.ExtraPropertiesDescriptions;
                return new HashSet<string>(descriptions.Keys);
            }
            catch
            {
                return new HashSet<string>();
            }
        }

        private static string[] SafeProperties(ISimulationObject obj, PropertyType type)
        {
            try { return obj.GetProperties(type) ?? Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        private static string Label(IFlowsheet fs, string id)
        {
            try
            {
                var s = fs.GetTranslatedString(id);
                if (string.IsNullOrEmpty(s)) s = id;
                // Some properties are labelled with their code name ("MinVal"); make those readable.
                return s.IndexOf(' ') < 0 && !s.StartsWith("PROP_", StringComparison.Ordinal) ? EditorCatalog.Humanize(s) : s;
            }
            catch
            {
                return id;
            }
        }

        private static List<string> UnitChoices(IUnitsOfMeasure su, string unit)
        {
            try
            {
                var measure = su.GetUnitType(unit);
                if (measure == UnitOfMeasure.none) return new List<string> { unit };
                var set = su.GetUnitSet(measure);
                if (set == null || set.Count == 0) return new List<string> { unit };
                // The engine's sets repeat some units (kcal/h for heat flow).
                var units = set.Distinct(StringComparer.Ordinal).ToList();
                if (!units.Contains(unit)) units.Insert(0, unit);
                return units;
            }
            catch
            {
                return new List<string> { unit };
            }
        }
    }
}
