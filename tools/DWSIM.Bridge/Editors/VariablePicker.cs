using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.SharedClasses.SystemsOfUnits;
using DWSIM.UnitOperations.SpecialOps;
using DWSIM.UnitOperations.SpecialOps.Helpers;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Editors
{
    /// <summary>
    /// The variables an Adjust and a Spec block point at (an object and one of its properties),
    /// with the set point and expression that go with them. Linking follows the Avalonia
    /// LogicalBlockEditors and the Fluent API builders: the block's info record and typed link,
    /// the marker on the object, and the drawing's dashed line; the object it pointed at before
    /// loses its marker.
    /// </summary>
    /// <remarks>
    /// A variable field is <c>{ kind: "variable", value: { object, property }, objects, properties,
    /// display }</c>; it is set with <c>{ object, property }</c>. <c>object.variables</c> lists the
    /// properties of another object for the picker.
    /// </remarks>
    internal static class VariablePicker
    {
        public const string Prefix = "variable:";

        private enum Role { Manipulated, Controlled, Reference, Source, Target }

        private const string DynamicsOnlySuffix = " (dynamics only)";

        // ------------------------------------------------------------------ reading

        public static IEnumerable<SpecialFields.Section> For(IFlowsheet fs, ISimulationObject block)
        {
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            if (block is Adjust adjust)
            {
                Ensure(adjust);
                var section = new SpecialFields.Section { Id = "variables", Title = "Variables" };
                section.Fields.Add(Field(fs, adjust, Role.Manipulated, "Manipulated variable", (SpecialOpObjectInfo)adjust.ManipulatedObjectData));
                section.Fields.Add(Field(fs, adjust, Role.Controlled, "Controlled variable", (SpecialOpObjectInfo)adjust.ControlledObjectData));
                section.Fields.Add(Bool("adjust:referenced", "Offset from a reference variable", adjust.Referenced));
                if (adjust.Referenced)
                    section.Fields.Add(Field(fs, adjust, Role.Reference, "Reference variable", (SpecialOpObjectInfo)adjust.ReferencedObjectData));

                var unit = SetPointUnit(fs, adjust);
                var setPoint = new JObject
                {
                    ["id"] = "adjust:setPoint",
                    ["label"] = adjust.Referenced ? "Offset from the reference" : "Set point",
                    ["kind"] = "number",
                    ["value"] = Finite(string.IsNullOrEmpty(unit) ? adjust.AdjustValue : Converter.ConvertFromSI(OffsetUnit(su, adjust, unit), adjust.AdjustValue)),
                    ["unit"] = string.IsNullOrEmpty(unit) ? null : unit,
                    ["editable"] = true,
                    ["hint"] = string.IsNullOrEmpty(unit) ? "In the unit of the controlled variable; pick it first." : null
                };
                // An offset is a difference, whose units the converter spells apart, so it keeps its one unit.
                var units = string.IsNullOrEmpty(unit) || adjust.Referenced ? null : UnitsOf(su, unit);
                if (units != null && units.Count > 1) setPoint["units"] = new JArray(units);
                section.Fields.Add(setPoint);
                var simultaneous = Bool("adjust:simultaneous", "Solve with the flowsheet solver", adjust.SimultaneousAdjust);
                if (!adjust.SimultaneousAdjust) simultaneous["hint"] = "Off, the adjust does nothing when the flowsheet solves.";
                section.Fields.Add(simultaneous);
                yield return section;
            }
            else if (block is Spec spec)
            {
                Ensure(spec);
                var section = new SpecialFields.Section { Id = "variables", Title = "Variables" };
                section.Fields.Add(Field(fs, spec, Role.Source, "Source variable", (SpecialOpObjectInfo)spec.SourceObjectData));
                section.Fields.Add(Field(fs, spec, Role.Target, "Target variable", (SpecialOpObjectInfo)spec.TargetObjectData));
                var expression = new JObject
                {
                    ["id"] = "spec:expression",
                    ["label"] = "Expression",
                    ["kind"] = "text",
                    ["value"] = string.IsNullOrEmpty(spec.Expression) ? null : spec.Expression,
                    ["editable"] = true,
                    ["hint"] = "Y = target variable, X = source variable. " + Evaluate(fs, spec)
                };
                section.Fields.Add(expression);
                if (spec.SpecCalculationMode == SpecCalcMode2.AfterObject || spec.SpecCalculationMode == SpecCalcMode2.BeforeObject)
                {
                    section.Fields.Add(new JObject
                    {
                        ["id"] = "spec:reference",
                        ["label"] = "Reference object",
                        ["kind"] = "choice",
                        ["value"] = string.IsNullOrEmpty(spec.ReferenceObjectID) ? null : spec.ReferenceObjectID,
                        ["options"] = new JArray(Others(fs, spec).Select(o => new JObject { ["value"] = o.Name, ["label"] = o.GraphicObject.Tag })),
                        ["editable"] = true
                    });
                }
                yield return section;
            }
        }

        /// <summary>The properties a block may point at on <paramref name="obj"/>, as picker options.</summary>
        public static JArray Properties(IFlowsheet fs, ISimulationObject obj, bool writable)
        {
            string[] ids;
            try { ids = obj.GetProperties(writable ? PropertyType.WR : PropertyType.ALL) ?? Array.Empty<string>(); }
            catch { ids = Array.Empty<string>(); }
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            bool Dynamic(string p) { try { return obj.IsDynamicProperty(p); } catch { return false; } }
            return new JArray(ids.Distinct().OrderBy(Dynamic).Select(p =>
            {
                var caption = Caption(fs, obj, p);
                string unit = null;
                try { unit = obj.GetPropertyUnit(p, su); } catch { }
                return new JObject { ["value"] = p, ["label"] = caption, ["unit"] = string.IsNullOrEmpty(unit) ? null : unit };
            }));
        }

        /// <summary>Whether the field is one a block writes, so only writable properties qualify.</summary>
        public static bool Writable(string field) => field == Prefix + "manipulated" || field == Prefix + "target";

        private static JObject Field(IFlowsheet fs, ISimulationObject block, Role role, string label, SpecialOpObjectInfo info)
        {
            var writable = role == Role.Manipulated || role == Role.Target;
            var linked = !string.IsNullOrEmpty(info.ID) && fs.SimulationObjects.ContainsKey(info.ID) ? fs.SimulationObjects[info.ID] : null;
            return new JObject
            {
                ["id"] = Prefix + role.ToString().ToLowerInvariant(),
                ["label"] = label,
                ["kind"] = "variable",
                ["writable"] = writable,
                ["value"] = new JObject
                {
                    ["object"] = linked?.Name,
                    ["property"] = linked == null || string.IsNullOrEmpty(info.PropertyName) ? null : info.PropertyName
                },
                ["objects"] = new JArray(Others(fs, block).Select(o => new JObject { ["value"] = o.Name, ["label"] = o.GraphicObject.Tag })),
                ["properties"] = linked == null ? new JArray() : Properties(fs, linked, writable),
                ["display"] = linked == null ? null : Display(fs, linked, info.PropertyName),
                ["editable"] = true
            };
        }

        private static IEnumerable<ISimulationObject> Others(IFlowsheet fs, ISimulationObject block)
            => fs.SimulationObjects.Values.Where(o => o.GraphicObject != null && o.Name != block.Name).OrderBy(o => o.GraphicObject.Tag);

        private static string Caption(IFlowsheet fs, ISimulationObject obj, string id)
        {
            string caption;
            try { caption = fs.GetTranslatedString(id); } catch { caption = null; }
            if (string.IsNullOrEmpty(caption)) caption = id;
            // Adjust and Spec solve in steady state, so dynamics-only properties are marked.
            try { if (obj.IsDynamicProperty(id)) caption += DynamicsOnlySuffix; } catch { }
            return caption;
        }

        private static string Display(IFlowsheet fs, ISimulationObject obj, string property)
        {
            if (string.IsNullOrEmpty(property)) return null;
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            try
            {
                var value = Convert.ToDouble(obj.GetPropertyValue(property, su), CultureInfo.InvariantCulture);
                if (double.IsNaN(value) || double.IsInfinity(value)) return null;
                var unit = obj.GetPropertyUnit(property, su);
                return value.ToString("G6", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
            }
            catch
            {
                return null;
            }
        }

        private static string Evaluate(IFlowsheet fs, Spec spec)
        {
            if (string.IsNullOrEmpty(spec.Expression) || string.IsNullOrEmpty(spec.TargetObjectData?.ID)
                || !fs.SimulationObjects.ContainsKey(spec.TargetObjectData.ID) || string.IsNullOrEmpty(spec.SourceObjectData?.ID))
                return "";
            try
            {
                var unit = fs.SimulationObjects[spec.TargetObjectData.ID].GetPropertyUnit(spec.TargetObjectData.PropertyName, fs.FlowsheetOptions.SelectedUnitSystem);
                return "Now Y = " + spec.ParseExpression().ToString("G6", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(unit) ? "" : " " + unit) + ".";
            }
            catch (Exception ex)
            {
                return "The expression does not evaluate: " + ex.Message;
            }
        }

        // ------------------------------------------------------------------ writing

        public static bool TrySet(IFlowsheet fs, ISimulationObject block, string field, JToken value, string unit)
        {
            if (block is Adjust adjust)
            {
                Ensure(adjust);
                switch (field)
                {
                    case Prefix + "manipulated": Link(fs, adjust, Role.Manipulated, (SpecialOpObjectInfo)adjust.ManipulatedObjectData, value); return true;
                    case Prefix + "controlled": Link(fs, adjust, Role.Controlled, (SpecialOpObjectInfo)adjust.ControlledObjectData, value); return true;
                    case Prefix + "reference": Link(fs, adjust, Role.Reference, (SpecialOpObjectInfo)adjust.ReferencedObjectData, value); return true;
                    case "adjust:referenced": adjust.Referenced = value.Value<bool>(); return true;
                    case "adjust:simultaneous": adjust.SimultaneousAdjust = value.Value<bool>(); return true;
                    case "adjust:setPoint":
                        var v = Number(value);
                        var own = SetPointUnit(fs, adjust);
                        var su = fs.FlowsheetOptions.SelectedUnitSystem;
                        adjust.AdjustValue = string.IsNullOrEmpty(own) ? v : Converter.ConvertToSI(OffsetUnit(su, adjust, string.IsNullOrEmpty(unit) ? own : unit), v);
                        return true;
                }
            }
            else if (block is Spec spec)
            {
                Ensure(spec);
                switch (field)
                {
                    case Prefix + "source": Link(fs, spec, Role.Source, (SpecialOpObjectInfo)spec.SourceObjectData, value); return true;
                    case Prefix + "target": Link(fs, spec, Role.Target, (SpecialOpObjectInfo)spec.TargetObjectData, value); return true;
                    case "spec:expression": spec.Expression = value.ToString(); return true;
                    case "spec:reference":
                        var id = value.ToString();
                        if (!fs.SimulationObjects.ContainsKey(id)) throw new RpcException(RpcErrorCodes.InvalidParams, "No object with id '" + id + "'.");
                        spec.ReferenceObjectID = id;
                        return true;
                }
            }
            return false;
        }

        private static void Link(IFlowsheet fs, ISimulationObject block, Role role, SpecialOpObjectInfo info, JToken value)
        {
            if (!(value is JObject pick)) throw new RpcException(RpcErrorCodes.InvalidParams, "A variable is { object, property }.");
            var objectId = pick.Value<string>("object");
            if (string.IsNullOrEmpty(objectId) || !fs.SimulationObjects.TryGetValue(objectId, out var target) || target.Name == block.Name)
                throw new RpcException(RpcErrorCodes.InvalidParams, "Pick another object on the flowsheet.");

            var writable = role == Role.Manipulated || role == Role.Target;
            var allowed = Properties(fs, target, writable).Select(p => p.Value<string>("value")).ToList();
            var property = pick.Value<string>("property");
            if (string.IsNullOrEmpty(property)) property = info.ID == target.Name && allowed.Contains(info.PropertyName) ? info.PropertyName : allowed.FirstOrDefault();
            if (property == null || !allowed.Contains(property))
                throw new RpcException(RpcErrorCodes.InvalidParams, (property ?? "That property") + " is not a property " + (writable ? "the block can write on " : "of ") + target.GraphicObject?.Tag + ".");

            if (info.ID != target.Name) Detach(fs, info, role);
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            info.ID = target.Name;
            info.Name = target.GraphicObject?.Tag;
            info.ObjectType = target.GraphicObject?.ObjectType.ToString();
            info.PropertyName = property;
            try
            {
                info.Units = target.GetPropertyUnit(property, su);
                info.UnitsType = su.GetUnitType(info.Units);
            }
            catch
            {
                info.Units = "";
            }
            Attach(block, target, role);
        }

        /// <summary>Marks the object as linked to the block and points the block and its drawing at it.</summary>
        private static void Attach(ISimulationObject block, ISimulationObject target, Role role)
        {
            var shape = target.GraphicObject as DWSIM.Drawing.SkiaSharp.GraphicObjects.GraphicObject;
            var linked = target as DWSIM.SharedClasses.UnitOperations.BaseClass;
            if (block is Adjust adjust)
            {
                target.IsAdjustAttached = true;
                target.AttachedAdjustId = block.Name;
                target.AdjustVarType = role == Role.Manipulated ? AdjustVarType.Manipulated
                    : role == Role.Controlled ? AdjustVarType.Controlled : AdjustVarType.Reference;
                var g = block.GraphicObject as DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.AdjustGraphic;
                switch (role)
                {
                    case Role.Manipulated:
                        adjust.ManipulatedObject = linked;
                        if (g != null) g.ConnectedToMv = shape;
                        break;
                    case Role.Controlled:
                        adjust.ControlledObject = linked;
                        if (g != null) g.ConnectedToCv = shape;
                        break;
                    default:
                        adjust.ReferenceObject = linked;
                        if (g != null) g.ConnectedToRv = shape;
                        break;
                }
            }
            else if (block is Spec spec)
            {
                target.IsSpecAttached = true;
                target.AttachedSpecId = block.Name;
                target.SpecVarType = role == Role.Source ? SpecVarType.Source : SpecVarType.Target;
                var g = block.GraphicObject as DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.SpecGraphic;
                if (role == Role.Source)
                {
                    spec.SourceObject = linked;
                    if (g != null) g.ConnectedToSv = shape;
                }
                else
                {
                    spec.TargetObject = linked;
                    if (g != null) g.ConnectedToTv = shape;
                }
            }
        }

        /// <summary>Clears the marker the block left on the object it pointed at before.</summary>
        private static void Detach(IFlowsheet fs, SpecialOpObjectInfo info, Role role)
        {
            if (string.IsNullOrEmpty(info.ID) || !fs.SimulationObjects.TryGetValue(info.ID, out var previous)) return;
            if (role == Role.Source || role == Role.Target)
            {
                previous.IsSpecAttached = false;
                previous.AttachedSpecId = "";
                previous.SpecVarType = SpecVarType.None;
            }
            else
            {
                previous.IsAdjustAttached = false;
                previous.AttachedAdjustId = "";
                previous.AdjustVarType = AdjustVarType.None;
            }
        }

        // ------------------------------------------------------------------ degrees of freedom

        /// <summary>
        /// The engine's analysis looks for the variables under property ids the blocks do not
        /// expose, so it always reports them unset; this reads the blocks' own records instead.
        /// </summary>
        public static DWSIM.Automation.FluentAPI.Diagnostics.ObjectDegreesOfFreedom Adjust(
            IFlowsheet fs, DWSIM.Automation.FluentAPI.Diagnostics.ObjectDegreesOfFreedom dof, ISimulationObject block)
        {
            var linked = new Dictionary<string, SpecialOpObjectInfo>();
            if (block is Adjust adjust)
            {
                linked["ManipulatedObjectData"] = (SpecialOpObjectInfo)adjust.ManipulatedObjectData;
                linked["ControlledObjectData"] = (SpecialOpObjectInfo)adjust.ControlledObjectData;
            }
            else if (block is Spec spec)
            {
                linked["SourceObjectData"] = (SpecialOpObjectInfo)spec.SourceObjectData;
                linked["TargetObjectData"] = (SpecialOpObjectInfo)spec.TargetObjectData;
            }
            else return dof;

            bool IsLinked(SpecialOpObjectInfo i) => i != null && !string.IsNullOrEmpty(i.ID) && fs.SimulationObjects.ContainsKey(i.ID) && !string.IsNullOrEmpty(i.PropertyName);
            var slots = dof.Slots.Select(s => linked.TryGetValue(s.Property, out var info)
                ? new DWSIM.Automation.FluentAPI.Diagnostics.SpecificationSlot(s.Name, s.Property, s.Value, s.Units, s.Required, IsLinked(info),
                    IsLinked(info) ? (info.Name ?? info.ID) + ", " + Caption(fs, fs.SimulationObjects[info.ID], info.PropertyName) : s.Note)
                : s).ToList();
            return new DWSIM.Automation.FluentAPI.Diagnostics.ObjectDegreesOfFreedom(dof.ObjectTag, dof.ObjectType, dof.Mode, dof.Supported, slots, dof.Note);
        }

        // ------------------------------------------------------------------ helpers

        private static void Ensure(Adjust adjust)
        {
            if (adjust.ManipulatedObjectData == null) adjust.ManipulatedObjectData = new SpecialOpObjectInfo();
            if (adjust.ControlledObjectData == null) adjust.ControlledObjectData = new SpecialOpObjectInfo();
            if (adjust.ReferencedObjectData == null) adjust.ReferencedObjectData = new SpecialOpObjectInfo();
        }

        private static void Ensure(Spec spec)
        {
            if (spec.SourceObjectData == null) spec.SourceObjectData = new SpecialOpObjectInfo();
            if (spec.TargetObjectData == null) spec.TargetObjectData = new SpecialOpObjectInfo();
        }

        /// <summary>The set point is in the unit of the controlled property, or the reference's when offset.</summary>
        private static string SetPointUnit(IFlowsheet fs, Adjust adjust)
        {
            var info = adjust.Referenced ? adjust.ReferencedObjectData : adjust.ControlledObjectData;
            if (info == null || string.IsNullOrEmpty(info.ID) || !fs.SimulationObjects.ContainsKey(info.ID) || string.IsNullOrEmpty(info.PropertyName)) return "";
            try { return fs.SimulationObjects[info.ID].GetPropertyUnit(info.PropertyName, fs.FlowsheetOptions.SelectedUnitSystem) ?? ""; }
            catch { return ""; }
        }

        /// <summary>An offset is a difference; the converter spells a temperature difference with a trailing dot.</summary>
        private static string OffsetUnit(IUnitsOfMeasure su, Adjust adjust, string unit)
            => adjust.Referenced && su.GetUnitType(unit) == UnitOfMeasure.temperature ? unit + "." : unit;

        private static List<string> UnitsOf(IUnitsOfMeasure su, string unit)
        {
            try
            {
                var type = su.GetUnitType(unit);
                if (type == UnitOfMeasure.none) return null;
                var list = su.GetUnitSet(type)?.Distinct().ToList() ?? new List<string>();
                if (!list.Contains(unit)) list.Insert(0, unit);
                return list;
            }
            catch
            {
                return null;
            }
        }

        private static double Number(JToken value) => value.Type == JTokenType.Integer || value.Type == JTokenType.Float
            ? value.Value<double>()
            : double.Parse(value.ToString(), CultureInfo.InvariantCulture);

        private static JToken Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? JValue.CreateNull() : new JValue(v);

        private static JObject Bool(string id, string label, bool value) => new JObject
        {
            ["id"] = id,
            ["label"] = label,
            ["kind"] = "bool",
            ["value"] = value,
            ["editable"] = true
        };
    }
}
