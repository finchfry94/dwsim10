using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.SharedClasses.SystemsOfUnits;
using DWSIM.UnitOperations.UnitOperations;
using DWSIM.UnitOperations.UnitOperations.Auxiliary.Pipe;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Editors
{
    /// <summary>
    /// The settings the engine's property list does not carry: a splitter's ratios, a component
    /// separator's per-compound splits, a pipe's hydraulic profile, a shortcut column's key
    /// compounds and a reactor's reaction set. Each is a field with an id of its own, read and
    /// written here, so the generated schema stays the single shape the frontend renders.
    /// </summary>
    internal static class SpecialFields
    {
        public sealed class Section
        {
            public string Id;
            public string Title;
            public bool Collapsed;
            public List<JObject> Fields = new List<JObject>();
        }

        public static IEnumerable<Section> For(IFlowsheet fs, ISimulationObject obj)
        {
            switch (obj)
            {
                case DWSIM.UnitOperations.SpecialOps.Adjust _:
                case DWSIM.UnitOperations.SpecialOps.Spec _:
                    foreach (var section in VariablePicker.For(fs, obj)) yield return section;
                    break;
                case Column column:
                    foreach (var section in ColumnFields.For(fs, column)) yield return section;
                    break;
                case Splitter splitter:
                    yield return SplitterRatios(fs, splitter);
                    break;
                case ComponentSeparator separator:
                    yield return SeparatorSplits(fs, separator);
                    break;
                case Pipe pipe:
                    yield return PipeProfile(fs, pipe);
                    var results = PipeResultsProfile(fs, pipe);
                    if (results != null) yield return results;
                    break;
                case ShortcutColumn column:
                    yield return new Section
                    {
                        Id = "keys",
                        Title = "Key compounds",
                        Fields =
                        {
                            CompoundChoice(fs, "compound:m_lightkey", "Light key compound", column.m_lightkey),
                            CompoundChoice(fs, "compound:m_heavykey", "Heavy key compound", column.m_heavykey)
                        }
                    };
                    break;
                case DWSIM.UnitOperations.Reactors.Reactor reactor:
                    yield return new Section
                    {
                        Id = "reactions",
                        Title = "Reactions",
                        Fields =
                        {
                            new JObject
                            {
                                ["id"] = "reactionSet",
                                ["label"] = "Reaction set",
                                ["kind"] = "choice",
                                ["value"] = reactor.ReactionSetID,
                                ["options"] = new JArray(fs.ReactionSets.Values.Select(r => new JObject { ["value"] = r.ID, ["label"] = r.Name })),
                                ["editable"] = true
                            }
                        }
                    };
                    break;
            }
        }

        /// <summary>Sets a special field; false when the id is not one of these.</summary>
        public static bool TrySet(IFlowsheet fs, ISimulationObject obj, string field, JToken value, string unit)
        {
            if (obj is Column rigorous && ColumnFields.TrySet(fs, rigorous, field, value, unit)) return true;
            if (VariablePicker.TrySet(fs, obj, field, value, unit)) return true;
            if (field.StartsWith("split:", StringComparison.Ordinal) && obj is Splitter splitter)
            {
                var index = int.Parse(field.Substring(6), CultureInfo.InvariantCulture);
                if (index < 0 || index >= splitter.Ratios.Count) throw new RpcException(RpcErrorCodes.InvalidParams, "No outlet " + (index + 1) + ".");
                var ratio = value.Value<double>();
                if (ratio < 0 || ratio > 1) throw new RpcException(RpcErrorCodes.InvalidParams, "A split ratio is between 0 and 1.");
                splitter.Ratios[index] = ratio;
                return true;
            }
            if (field == "table:separatorSplits" && obj is ComponentSeparator separator)
            {
                SetSeparatorSplits(fs, separator, value);
                return true;
            }
            if (field == "choice:separatorStream" && obj is ComponentSeparator sep2)
            {
                sep2.SpecifiedStreamIndex = (byte)(value.ToString() == "1" ? 1 : 0);
                return true;
            }
            if (field == "table:pipeProfile" && obj is Pipe pipe)
            {
                SetPipeProfile(fs, pipe, value);
                return true;
            }
            if (field.StartsWith("compound:", StringComparison.Ordinal) && obj is ShortcutColumn column)
            {
                var name = value.ToString();
                if (!fs.SelectedCompounds.ContainsKey(name)) throw new RpcException(RpcErrorCodes.InvalidParams, name + " is not one of the flowsheet's compounds.");
                if (field == "compound:m_lightkey") column.m_lightkey = name;
                else if (field == "compound:m_heavykey") column.m_heavykey = name;
                else return false;
                return true;
            }
            if (field == "reactionSet" && obj is DWSIM.UnitOperations.Reactors.Reactor reactor)
            {
                var id = value.ToString();
                if (!fs.ReactionSets.ContainsKey(id)) throw new RpcException(RpcErrorCodes.InvalidParams, "No reaction set with id '" + id + "'.");
                reactor.ReactionSetID = id;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ splitter

        private static Section SplitterRatios(IFlowsheet fs, Splitter splitter)
        {
            var section = new Section { Id = "ratios", Title = "Split ratios" };
            var outlets = splitter.GraphicObject.OutputConnectors;
            var editable = splitter.OperationMode == Splitter.OpMode.SplitRatios;
            for (int i = 0; i < splitter.Ratios.Count; i++)
            {
                var stream = i < outlets.Count && outlets[i].IsAttached ? outlets[i].AttachedConnector?.AttachedTo?.Tag : null;
                section.Fields.Add(new JObject
                {
                    ["id"] = "split:" + i,
                    ["label"] = $"Outlet {i + 1}" + (stream != null ? $" ({stream})" : ""),
                    ["kind"] = "number",
                    ["value"] = Convert.ToDouble(splitter.Ratios[i], CultureInfo.InvariantCulture),
                    ["editable"] = editable,
                    ["hint"] = "Fraction of the inlet that leaves by this outlet; the ratios add up to 1."
                });
            }
            return section;
        }

        // ------------------------------------------------------------------ component separator

        private static Section SeparatorSplits(IFlowsheet fs, ComponentSeparator separator)
        {
            EnsureSeparatorSpecs(fs, separator);
            var specOptions = new JArray(
                new JObject { ["value"] = "PercentInletMassFlow", ["label"] = "% of inlet mass flow" },
                new JObject { ["value"] = "PercentInletMolarFlow", ["label"] = "% of inlet molar flow" },
                new JObject { ["value"] = "MassFlow", ["label"] = "Mass flow" },
                new JObject { ["value"] = "MolarFlow", ["label"] = "Molar flow" });
            var rows = new JArray(fs.SelectedCompounds.Keys.Select(c =>
            {
                var s = separator.ComponentSepSpecs[c];
                return new JObject
                {
                    ["compound"] = c,
                    ["spec"] = s.SepSpec.ToString(),
                    ["value"] = s.SpecValue,
                    ["unit"] = UnitOf(s)
                };
            }));
            return new Section
            {
                Id = "splits",
                Title = "Compound splits",
                Fields =
                {
                    new JObject
                    {
                        ["id"] = "choice:separatorStream",
                        ["label"] = "Specified outlet",
                        ["kind"] = "choice",
                        ["value"] = separator.SpecifiedStreamIndex.ToString(CultureInfo.InvariantCulture),
                        ["options"] = new JArray(new JObject { ["value"] = "0", ["label"] = "Outlet 1" }, new JObject { ["value"] = "1", ["label"] = "Outlet 2" }),
                        ["editable"] = true
                    },
                    new JObject
                    {
                        ["id"] = "table:separatorSplits",
                        ["label"] = "What the specified outlet takes of each compound",
                        ["kind"] = "table",
                        ["columns"] = new JArray(
                            new JObject { ["key"] = "compound", ["label"] = "Compound", ["kind"] = "text", ["editable"] = false },
                            new JObject { ["key"] = "spec", ["label"] = "Specification", ["kind"] = "choice", ["options"] = specOptions, ["editable"] = true },
                            new JObject { ["key"] = "value", ["label"] = "Value", ["kind"] = "number", ["editable"] = true, ["unitKey"] = "unit" }),
                        ["rows"] = rows,
                        ["editable"] = true
                    }
                }
            };
        }

        private static string UnitOf(DWSIM.UnitOperations.UnitOperations.Auxiliary.ComponentSeparationSpec s)
        {
            switch (s.SepSpec)
            {
                case DWSIM.UnitOperations.UnitOperations.Auxiliary.SeparationSpec.PercentInletMassFlow:
                case DWSIM.UnitOperations.UnitOperations.Auxiliary.SeparationSpec.PercentInletMolarFlow:
                    return "%";
                case DWSIM.UnitOperations.UnitOperations.Auxiliary.SeparationSpec.MassFlow:
                    return string.IsNullOrEmpty(s.SpecUnit) ? "kg/s" : s.SpecUnit;
                default:
                    return string.IsNullOrEmpty(s.SpecUnit) ? "mol/s" : s.SpecUnit;
            }
        }

        /// <summary>Every compound gets a spec, as the Avalonia editor creates them: 0 % to the specified outlet.</summary>
        private static void EnsureSeparatorSpecs(IFlowsheet fs, ComponentSeparator separator)
        {
            foreach (var c in fs.SelectedCompounds.Keys)
                if (!separator.ComponentSepSpecs.ContainsKey(c))
                    separator.ComponentSepSpecs[c] = new DWSIM.UnitOperations.UnitOperations.Auxiliary.ComponentSeparationSpec(
                        c, DWSIM.UnitOperations.UnitOperations.Auxiliary.SeparationSpec.PercentInletMassFlow, 0.0, "%");
        }

        private static void SetSeparatorSplits(IFlowsheet fs, ComponentSeparator separator, JToken value)
        {
            if (!(value is JArray rows)) throw new RpcException(RpcErrorCodes.InvalidParams, "The splits are a list of rows.");
            EnsureSeparatorSpecs(fs, separator);
            foreach (var row in rows)
            {
                var compound = row.Value<string>("compound");
                if (compound == null || !separator.ComponentSepSpecs.TryGetValue(compound, out var s))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Unknown compound in the splits: " + compound);
                if (!Enum.TryParse<DWSIM.UnitOperations.UnitOperations.Auxiliary.SeparationSpec>(row.Value<string>("spec"), out var spec))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Unknown specification: " + row.Value<string>("spec"));
                var v = row.Value<double?>("value") ?? 0.0;
                var isPercent = spec == DWSIM.UnitOperations.UnitOperations.Auxiliary.SeparationSpec.PercentInletMassFlow
                                || spec == DWSIM.UnitOperations.UnitOperations.Auxiliary.SeparationSpec.PercentInletMolarFlow;
                if (v < 0 || (isPercent && v > 100))
                    throw new RpcException(RpcErrorCodes.InvalidParams, $"{compound}: {v} is out of range{(isPercent ? " (0 to 100 %)" : "")}.");
                s.SepSpec = spec;
                s.SpecValue = v;
                s.SpecUnit = isPercent ? "%" : row.Value<string>("unit") ?? (spec == DWSIM.UnitOperations.UnitOperations.Auxiliary.SeparationSpec.MassFlow ? "kg/s" : "mol/s");
            }
        }

        // ------------------------------------------------------------------ pipe

        /// <summary>The value DWSIM stores for a straight tube, whatever the language.</summary>
        private const string StraightTube = "Tubulaosimples";

        private static readonly Lazy<string[]> Fittings = new Lazy<string[]>(() =>
        {
            try
            {
                var asm = typeof(PipeSection).Assembly;
                using (var stream = asm.GetManifestResourceStream("DWSIM.UnitOperations.fittings.dat"))
                {
                    if (stream == null) return Array.Empty<string>();
                    // ANSI, as the Windows editor reads it.
                    using (var reader = new StreamReader(stream, Encoding.Latin1))
                    {
                        var list = new List<string>();
                        string line;
                        while ((line = reader.ReadLine()) != null)
                            if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Split(';')[0]);
                        return list.ToArray();
                    }
                }
            }
            catch
            {
                return Array.Empty<string>();
            }
        });

        private static (string stored, string label)[] Materials(IFlowsheet fs)
        {
            string Raw(string key)
            {
                try { return fs.GetTranslatedString(key) ?? key; } catch { return key; }
            }
            string Shown(string key, string fallback)
            {
                var t = Raw(key);
                return string.IsNullOrEmpty(t) || t == key ? fallback : t;
            }
            // The same list, stored values and labels as the Avalonia pipe editor's.
            return new[]
            {
                (Raw("AoComum"), Shown("AoComum", "Raw Steel")),
                (Raw("AoCarbono"), Shown("AoCarbono", "Carbon Steel")),
                (Raw("FerroBottomido"), Shown("FerroBottomido", "Cast Iron")),
                (Raw("AoInoxidvel"), Shown("AoInoxidvel", "Stainless Steel")),
                ("PVC", "PVC"),
                ("PVC+PFRV", "PVC+PFRV"),
                (Raw("CommercialCopper"), Shown("CommercialCopper", "Commercial Copper")),
                ("UserDefined", Shown("UserDefined", "User Defined"))
            };
        }

        /// <summary>
        /// The solved pipe along its length (pressure, temperature, velocities, holdup), point by
        /// point as the Avalonia PipeProfilesViewer collects them, with the outlet as the last point.
        /// Null before the pipe is solved.
        /// </summary>
        private static Section PipeResultsProfile(IFlowsheet fs, Pipe pipe)
        {
            if (pipe.Profile == null || !pipe.Calculated) return null;
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            var rows = new JArray();
            double length = 0.0;
            JObject last = null;
            JToken V(string unit, double? si) => si.HasValue && !double.IsNaN(si.Value) && !double.IsInfinity(si.Value)
                ? (JToken)(string.IsNullOrEmpty(unit) ? si.Value : Converter.ConvertFromSI(unit, si.Value)) : JValue.CreateNull();
            foreach (var ps in pipe.Profile.Sections.Values)
            {
                if (ps.Results == null) continue;
                var straight = ps.TipoSegmento == "Tubulaosimples" || ps.TipoSegmento == "Straight Tube Section";
                var step = straight && ps.Incrementos > 0 ? ps.Comprimento / ps.Incrementos : 0.0;
                var start = length;
                int k = 0;
                foreach (PipeResults res in ps.Results)
                {
                    last = new JObject
                    {
                        ["length"] = V(su.distance, start + Math.Min(k, Math.Max(ps.Incrementos, 0)) * step),
                        ["pressure"] = V(su.pressure, res.Pressure_Initial),
                        ["temperature"] = V(su.temperature, res.Temperature_Initial),
                        ["liquidVelocity"] = V(su.velocity, res.LiqVel),
                        ["vaporVelocity"] = V(su.velocity, res.VapVel),
                        ["holdup"] = V(null, res.LiquidHoldup),
                        ["regime"] = res.FlowRegime ?? ""
                    };
                    rows.Add(last);
                    k++;
                }
                length = start + (straight ? ps.Comprimento : 0.0);
            }
            if (rows.Count == 0) return null;
            // The increments carry their inlet state; the outlet stream closes the profile.
            var cp = pipe.GraphicObject?.OutputConnectors.FirstOrDefault();
            if (cp != null && cp.IsAttached && fs.SimulationObjects.TryGetValue(cp.AttachedConnector.AttachedTo.Name, out var o)
                && o is IMaterialStream outlet && last.Value<double?>("length") < Converter.ConvertFromSI(su.distance, length) - 1e-9)
                rows.Add(new JObject
                {
                    ["length"] = V(su.distance, length),
                    ["pressure"] = V(su.pressure, outlet.GetPressure()),
                    ["temperature"] = V(su.temperature, outlet.GetTemperature()),
                    ["liquidVelocity"] = last["liquidVelocity"],
                    ["vaporVelocity"] = last["vaporVelocity"],
                    ["holdup"] = last["holdup"],
                    ["regime"] = "outlet"
                });
            JObject Col(string key, string label, string unit, string kind = "number") => new JObject
            {
                ["key"] = key, ["label"] = label, ["kind"] = kind, ["editable"] = false, ["unit"] = string.IsNullOrEmpty(unit) ? null : unit
            };
            return new Section
            {
                Id = "pipeResults",
                Title = "Profiles",
                Fields =
                {
                    new JObject
                    {
                        ["id"] = "table:pipeResults",
                        ["label"] = "Along the pipe",
                        ["kind"] = "table",
                        ["readOnly"] = true,
                        ["editable"] = false,
                        ["chart"] = new JObject { ["x"] = "length", ["series"] = new JArray("pressure", "temperature", "liquidVelocity", "vaporVelocity", "holdup") },
                        ["columns"] = new JArray(
                            Col("length", "Length", su.distance), Col("pressure", "P", su.pressure), Col("temperature", "T", su.temperature),
                            Col("liquidVelocity", "Liquid velocity", su.velocity), Col("vaporVelocity", "Vapor velocity", su.velocity),
                            Col("holdup", "Liquid holdup", null), Col("regime", "Flow regime", null, "text")),
                        ["rows"] = rows
                    }
                }
            };
        }

        private static Section PipeProfile(IFlowsheet fs, Pipe pipe)
        {
            var su = fs.FlowsheetOptions.SelectedUnitSystem;
            var lengthUnit = su.distance;
            var typeOptions = new JArray(new JObject { ["value"] = StraightTube, ["label"] = "Straight tube" });
            foreach (var f in Fittings.Value) typeOptions.Add(new JObject { ["value"] = f, ["label"] = f });
            var materials = new JArray(Materials(fs).Select(m => new JObject { ["value"] = m.stored, ["label"] = m.label }));

            var rows = new JArray(pipe.Profile.Sections.Values.OrderBy(s => s.Indice).Select(s => new JObject
            {
                ["type"] = IsStraight(s) ? StraightTube : s.TipoSegmento,
                ["quantity"] = s.Quantidade,
                ["increments"] = s.Incrementos,
                ["material"] = s.Material,
                ["length"] = Converter.ConvertFromSI(lengthUnit, s.Comprimento),
                ["elevation"] = Converter.ConvertFromSI(lengthUnit, s.Elevacao),
                ["outerDiameter"] = s.DE,
                ["innerDiameter"] = s.DI
            }));

            return new Section
            {
                Id = "profile",
                Title = "Hydraulic profile",
                Fields =
                {
                    new JObject
                    {
                        ["id"] = "table:pipeProfile",
                        ["label"] = "Sections, inlet to outlet",
                        ["kind"] = "table",
                        ["columns"] = new JArray(
                            new JObject { ["key"] = "type", ["label"] = "Type", ["kind"] = "choice", ["options"] = typeOptions, ["editable"] = true },
                            new JObject { ["key"] = "quantity", ["label"] = "Qty", ["kind"] = "number", ["editable"] = true },
                            new JObject { ["key"] = "increments", ["label"] = "Increments", ["kind"] = "number", ["editable"] = true },
                            new JObject { ["key"] = "material", ["label"] = "Material", ["kind"] = "choice", ["options"] = materials, ["editable"] = true },
                            new JObject { ["key"] = "length", ["label"] = "Length", ["kind"] = "number", ["unit"] = lengthUnit, ["editable"] = true },
                            new JObject { ["key"] = "elevation", ["label"] = "Elevation", ["kind"] = "number", ["unit"] = lengthUnit, ["editable"] = true },
                            new JObject { ["key"] = "outerDiameter", ["label"] = "OD", ["kind"] = "number", ["unit"] = "in", ["editable"] = true },
                            new JObject { ["key"] = "innerDiameter", ["label"] = "ID", ["kind"] = "number", ["unit"] = "in", ["editable"] = true }),
                        ["rows"] = rows,
                        ["editable"] = true,
                        ["canAddRows"] = true,
                        ["newRow"] = new JObject
                        {
                            ["type"] = StraightTube, ["quantity"] = 1, ["increments"] = 5,
                            ["material"] = Materials(fs)[1].stored,
                            ["length"] = Converter.ConvertFromSI(lengthUnit, 1.0), ["elevation"] = 0.0,
                            ["outerDiameter"] = 2.375, ["innerDiameter"] = 1.939
                        }
                    }
                }
            };
        }

        private static bool IsStraight(PipeSection s)
        {
            var t = s.TipoSegmento ?? "";
            return t == StraightTube || t == "Straight Tube" || t == "Tubulação Simples";
        }

        private static void SetPipeProfile(IFlowsheet fs, Pipe pipe, JToken value)
        {
            if (!(value is JArray rows)) throw new RpcException(RpcErrorCodes.InvalidParams, "The profile is a list of sections.");
            var lengthUnit = fs.FlowsheetOptions.SelectedUnitSystem.distance;
            var sections = new SortedDictionary<int, PipeSection>();
            var i = 1;
            foreach (var row in rows)
            {
                double Num(string key) => row.Value<double?>(key) ?? throw new RpcException(RpcErrorCodes.InvalidParams, $"Section {i}: {key} is missing.");
                var section = new PipeSection
                {
                    Indice = i,
                    TipoSegmento = row.Value<string>("type") ?? StraightTube,
                    Quantidade = Math.Max(1, (int)Num("quantity")),
                    Incrementos = Math.Max(1, (int)Num("increments")),
                    Material = row.Value<string>("material") ?? Materials(fs)[1].stored,
                    Comprimento = Converter.ConvertToSI(lengthUnit, Num("length")),
                    Elevacao = Converter.ConvertToSI(lengthUnit, Num("elevation")),
                    DE = Num("outerDiameter"),
                    DI = Num("innerDiameter")
                };
                if (section.Comprimento <= 0) throw new RpcException(RpcErrorCodes.InvalidParams, $"Section {i}: the length must be positive.");
                if (section.DI <= 0 || section.DE < section.DI) throw new RpcException(RpcErrorCodes.InvalidParams, $"Section {i}: the diameters must be positive, the outer at least the inner.");
                sections[i] = section;
                i++;
            }
            pipe.Profile.Sections.Clear();
            foreach (var kv in sections) pipe.Profile.Sections.Add(kv.Key, kv.Value);
            pipe.Profile.Status = PipeEditorStatus.OK;
        }

        // ------------------------------------------------------------------ helpers

        private static JObject CompoundChoice(IFlowsheet fs, string id, string label, string value) => new JObject
        {
            ["id"] = id,
            ["label"] = label,
            ["kind"] = "choice",
            ["value"] = string.IsNullOrEmpty(value) ? null : value,
            ["options"] = new JArray(fs.SelectedCompounds.Keys.Select(c => new JObject { ["value"] = c, ["label"] = c })),
            ["editable"] = true
        };
    }
}
