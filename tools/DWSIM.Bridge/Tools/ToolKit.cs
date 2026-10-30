using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.SharedClasses.SystemsOfUnits;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Tools
{
    /// <summary>
    /// An engineering tool: a calculation on part of the flowsheet that changes nothing in it.
    /// It describes its inputs as schema fields, so the frontend renders any tool the same way,
    /// and answers with read-only sections, charts and tables.
    /// </summary>
    internal interface ITool
    {
        string Id { get; }
        string Title { get; }
        /// <summary>"Sizing", "Thermodynamics", "Columns", "Safety": how the tools list groups it.</summary>
        string Group { get; }
        string Description { get; }

        /// <summary>The input fields, with their defaults, for this flowsheet.</summary>
        IEnumerable<JObject> Inputs(IFlowsheet fs);

        void Run(IFlowsheet fs, ToolInputs inputs, ToolResult result, CancellationToken cancellation);
    }

    /// <summary>The values a tool was run with, read by field id; numbers come back in SI.</summary>
    internal sealed class ToolInputs
    {
        private readonly IFlowsheet _fs;
        private readonly JObject _values;
        private readonly JObject _units;

        public ToolInputs(IFlowsheet fs, JObject values, JObject units)
        {
            _fs = fs;
            _values = values ?? new JObject();
            _units = units ?? new JObject();
        }

        private JToken Raw(string id) => _values[id] is JToken t && t.Type != JTokenType.Null ? t : null;

        /// <summary>
        /// A number in SI: converted from the unit the frontend sent with it, else from
        /// <paramref name="displayUnit"/> (the field's own unit); <paramref name="fallback"/> is SI.
        /// </summary>
        public double Number(string id, double fallback, string displayUnit = null)
        {
            var raw = Raw(id);
            if (raw == null) return fallback;
            double v;
            try { v = raw.Type == JTokenType.String ? double.Parse(raw.ToString(), CultureInfo.InvariantCulture) : raw.Value<double>(); }
            catch { throw new RpcException(RpcErrorCodes.InvalidParams, $"'{id}' is not a number."); }
            var unit = _units.Value<string>(id) ?? displayUnit;
            return string.IsNullOrEmpty(unit) ? v : Converter.ConvertToSI(unit, v);
        }

        public string Text(string id, string fallback = null) => Raw(id)?.ToString() ?? fallback;

        public bool Bool(string id, bool fallback = false) => Raw(id)?.Type == JTokenType.Boolean ? Raw(id).Value<bool>() : fallback;

        /// <summary>The object picked in a choice field, which must be of type <typeparamref name="T"/>.</summary>
        public T Object<T>(string id, string what) where T : class
        {
            var key = Text(id);
            if (string.IsNullOrEmpty(key) || !_fs.SimulationObjects.TryGetValue(key, out var obj))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Pick " + what + ".");
            return obj as T ?? throw new RpcException(RpcErrorCodes.InvalidParams, obj.GraphicObject?.Tag + " is not " + what + ".");
        }
    }

    /// <summary>What a tool answers: titled sections of read-only fields, charts, tables and notes.</summary>
    internal sealed class ToolResult
    {
        private readonly IUnitsOfMeasure _su;
        private JObject _section;
        public readonly JArray Sections = new JArray();
        public readonly JArray Charts = new JArray();
        public readonly JArray Tables = new JArray();
        public readonly List<string> Warnings = new List<string>();
        public string Summary;

        public ToolResult(IFlowsheet fs) { _su = fs.FlowsheetOptions.SelectedUnitSystem; }

        public IUnitsOfMeasure Units => _su;

        public ToolResult Section(string title)
        {
            _section = new JObject { ["id"] = "s" + Sections.Count, ["title"] = title, ["fields"] = new JArray() };
            Sections.Add(_section);
            return this;
        }

        private JArray Fields => (JArray)(_section ?? Section("Results")._section)["fields"];

        /// <summary>A result in SI, shown in <paramref name="unit"/> (a flowsheet unit, or any the converter knows).</summary>
        public ToolResult Value(string label, double si, string unit = null, string hint = null)
        {
            var shown = string.IsNullOrEmpty(unit) ? si : Converter.ConvertFromSI(unit, si);
            Fields.Add(new JObject
            {
                ["id"] = "r" + Fields.Count,
                ["label"] = label,
                ["kind"] = "number",
                ["value"] = double.IsNaN(shown) || double.IsInfinity(shown) ? JValue.CreateNull() : (JToken)shown,
                ["unit"] = string.IsNullOrEmpty(unit) ? null : unit,
                ["editable"] = false,
                ["hint"] = hint
            });
            return this;
        }

        /// <summary>A result already in its display unit (in², mm, %).</summary>
        public ToolResult Shown(string label, double value, string unit, string hint = null)
        {
            Fields.Add(new JObject
            {
                ["id"] = "r" + Fields.Count,
                ["label"] = label,
                ["kind"] = "number",
                ["value"] = double.IsNaN(value) || double.IsInfinity(value) ? JValue.CreateNull() : (JToken)value,
                ["unit"] = unit,
                ["editable"] = false,
                ["hint"] = hint
            });
            return this;
        }

        public ToolResult Text(string label, string value, string hint = null)
        {
            Fields.Add(new JObject
            {
                ["id"] = "r" + Fields.Count,
                ["label"] = label,
                ["kind"] = "text",
                ["value"] = value,
                ["editable"] = false,
                ["hint"] = hint
            });
            return this;
        }

        /// <summary>Adds a chart in the shape the frontend's XY chart draws; values already in display units.</summary>
        public ChartBuilder Chart(string title, (string label, string unit) x, (string label, string unit) y)
        {
            var chart = new ChartBuilder(title, x, y);
            Charts.Add(chart.Json);
            return chart;
        }

        /// <summary>A table of rows; numbers stay numbers so the frontend formats and exports them.</summary>
        public void Table(string title, IEnumerable<string> columns, IEnumerable<IEnumerable<object>> rows)
            => Tables.Add(new JObject
            {
                ["title"] = title,
                ["columns"] = new JArray(columns),
                ["rows"] = new JArray(rows.Select(r => new JArray(r.Select(v => v is double d && (double.IsNaN(d) || double.IsInfinity(d)) ? null : v))))
            });

        public JObject ToJson() => new JObject
        {
            ["summary"] = Summary,
            ["sections"] = Sections,
            ["charts"] = Charts,
            ["tables"] = Tables,
            ["warnings"] = new JArray(Warnings)
        };
    }

    internal sealed class ChartBuilder
    {
        public readonly JObject Json;
        private readonly JArray _series = new JArray();

        public ChartBuilder(string title, (string label, string unit) x, (string label, string unit) y)
        {
            Json = new JObject
            {
                ["title"] = title,
                ["x"] = new JObject { ["label"] = x.label, ["unit"] = string.IsNullOrEmpty(x.unit) ? null : x.unit },
                ["y"] = new JObject { ["label"] = y.label, ["unit"] = string.IsNullOrEmpty(y.unit) ? null : y.unit },
                ["series"] = _series
            };
        }

        public ChartBuilder Line(string name, IEnumerable<double> x, IEnumerable<double> y) => Add(name, "line", x, y);

        public ChartBuilder Points(string name, IEnumerable<double> x, IEnumerable<double> y) => Add(name, "point", x, y);

        private ChartBuilder Add(string name, string kind, IEnumerable<double> x, IEnumerable<double> y)
        {
            var xs = new JArray();
            var ys = new JArray();
            foreach (var (a, b) in x.Zip(y, (a, b) => (a, b)))
            {
                if (double.IsNaN(a) || double.IsInfinity(a) || double.IsNaN(b) || double.IsInfinity(b)) continue;
                xs.Add(a);
                ys.Add(b);
            }
            if (xs.Count > 0) _series.Add(new JObject { ["name"] = name, ["kind"] = kind, ["x"] = xs, ["y"] = ys });
            return this;
        }
    }

    /// <summary>Input field builders, in the schema shape the frontend renders.</summary>
    internal static class Field
    {
        public static JObject Number(string id, string label, double si, string unit, IUnitsOfMeasure su = null, string hint = null)
        {
            var shown = string.IsNullOrEmpty(unit) ? si : Converter.ConvertFromSI(unit, si);
            var field = new JObject
            {
                ["id"] = id,
                ["label"] = label,
                ["kind"] = "number",
                ["value"] = shown,
                ["unit"] = string.IsNullOrEmpty(unit) ? null : unit,
                ["editable"] = true,
                ["hint"] = hint
            };
            if (su != null && !string.IsNullOrEmpty(unit))
            {
                try
                {
                    var set = su.GetUnitSet(su.GetUnitType(unit))?.Distinct().ToList();
                    if (set != null && set.Count > 1)
                    {
                        if (!set.Contains(unit)) set.Insert(0, unit);
                        field["units"] = new JArray(set);
                    }
                }
                catch { /* a unit outside the unit system's sets */ }
            }
            return field;
        }

        public static JObject Choice(string id, string label, string value, IEnumerable<(string value, string label)> options, string hint = null)
        {
            var list = options.ToList();
            return new JObject
            {
                ["id"] = id,
                ["label"] = label,
                ["kind"] = "choice",
                ["value"] = value ?? list.FirstOrDefault().value,
                ["options"] = new JArray(list.Select(o => new JObject { ["value"] = o.value, ["label"] = o.label })),
                ["editable"] = true,
                ["hint"] = hint
            };
        }

        public static JObject Bool(string id, string label, bool value, string hint = null) => new JObject
        {
            ["id"] = id,
            ["label"] = label,
            ["kind"] = "bool",
            ["value"] = value,
            ["editable"] = true,
            ["hint"] = hint
        };

        /// <summary>A choice of the flowsheet's objects of the given types, by tag.</summary>
        public static JObject Objects(string id, string label, IFlowsheet fs, params ObjectType[] types)
        {
            var objects = fs.SimulationObjects.Values
                .Where(o => o.GraphicObject != null && types.Contains(o.GraphicObject.ObjectType))
                .OrderBy(o => o.GraphicObject.Tag, StringComparer.OrdinalIgnoreCase)
                .Select(o => (o.Name, o.GraphicObject.Tag));
            return Choice(id, label, null, objects);
        }
    }
}
