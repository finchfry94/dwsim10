using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DWSIM.Bridge.Editors
{
    /// <summary>
    /// What the engine cannot say about a unit operation's editor: which setting is its
    /// calculation mode, the names and order the user sees, and which settings to leave out.
    /// Labels and orders are those of the Avalonia editors in
    /// ui/DWSIM.UI.Desktop.Editors.Avalonia/UnitOperations, so the two apps read the same.
    /// Everything else in an editor is generated from the engine.
    /// </summary>
    public static class EditorCatalog
    {
        public sealed class TypeEntry
        {
            /// <summary>The enum property that picks the calculation mode, if the type has one.</summary>
            public string ModeProperty;

            /// <summary>Mode values in display order, with their labels; values left out are hidden.</summary>
            public (string value, string label)[] Modes;

            /// <summary>Labels for other enum settings, by property name; values left out keep a generated label.</summary>
            public Dictionary<string, (string label, (string value, string label)[] options)> Choices =
                new Dictionary<string, (string, (string, string)[])>();

            /// <summary>Property fields not to offer, by label (shown another way, or internal).</summary>
            public HashSet<string> HiddenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Enum properties not to offer (internal state, or edited elsewhere).</summary>
            public HashSet<string> HiddenChoices = new HashSet<string>();

            /// <summary>Degrees-of-freedom slot property name to the PROP_ id of the field it fills.</summary>
            public Dictionary<string, string> SlotFields = new Dictionary<string, string>();
        }

        private static readonly Dictionary<string, TypeEntry> Entries = new Dictionary<string, TypeEntry>(StringComparer.Ordinal)
        {
            ["Heater"] = new TypeEntry
            {
                ModeProperty = "CalcMode",
                Modes = new[]
                {
                    ("HeatAdded", "Heat Added"), ("TemperatureChange", "Temperature Change"),
                    ("OutletTemperature", "Outlet Temperature"), ("OutletVaporFraction", "Outlet Vapor Mole Fraction"),
                    ("EnergyStream", "Energy Stream")
                },
                SlotFields = { ["DeltaQ"] = "PROP_HT_3", ["OutletTemperature"] = "PROP_HT_2", ["OutletVaporFraction"] = "PROP_HT_4", ["DeltaT"] = "PROP_HT_5", ["DeltaP"] = "PROP_HT_0", ["Eficiencia"] = "PROP_HT_1" }
            },
            ["Cooler"] = new TypeEntry
            {
                ModeProperty = "CalcMode",
                Modes = new[]
                {
                    ("HeatRemoved", "Heat Removed"), ("TemperatureChange", "Temperature Change"),
                    ("OutletTemperature", "Outlet Temperature"), ("OutletVaporFraction", "Outlet Vapor Mole Fraction"),
                    ("EnergyStream", "Energy Stream")
                },
                SlotFields = { ["DeltaQ"] = "PROP_CO_3", ["OutletTemperature"] = "PROP_CO_2", ["OutletVaporFraction"] = "PROP_CO_4", ["DeltaT"] = "PROP_CO_5", ["DeltaP"] = "PROP_CO_0", ["Eficiencia"] = "PROP_CO_1" }
            },
            ["Pump"] = new TypeEntry
            {
                ModeProperty = "CalcMode",
                Modes = new[]
                {
                    ("Delta_P", "Pressure Increase"), ("OutletPressure", "Outlet Pressure"), ("Power", "Power Required"),
                    ("EnergyStream", "Energy Stream"), ("Curves", "Performance Curves"), ("PositiveDisplacement", "Positive Displacement")
                }
            },
            ["Compressor"] = new TypeEntry
            {
                ModeProperty = "CalcMode",
                Modes = new[]
                {
                    ("OutletPressure", "Outlet Pressure"), ("Delta_P", "Pressure Increase"), ("PowerRequired", "Power Required"),
                    ("EnergyStream", "Energy Stream"), ("Head", "Known Head"), ("Curves", "Performance Curves"), ("PressureRatio", "Pressure Ratio")
                },
                Choices = { ["ProcessPath"] = ("Process path", new[] { ("Adiabatic", "Adiabatic"), ("Polytropic", "Polytropic") }) }
            },
            ["Expander"] = new TypeEntry
            {
                ModeProperty = "CalcMode",
                Modes = new[]
                {
                    ("OutletPressure", "Outlet Pressure"), ("Delta_P", "Pressure Decrease"), ("PowerGenerated", "Power Generated"),
                    ("Head", "Known Head"), ("Curves", "Performance Curves"), ("PressureRatio", "Pressure Ratio")
                },
                Choices = { ["ProcessPath"] = ("Process path", new[] { ("Adiabatic", "Adiabatic"), ("Polytropic", "Polytropic") }) }
            },
            ["Valve"] = new TypeEntry
            {
                ModeProperty = "CalcMode",
                Modes = new[]
                {
                    ("OutletPressure", "Outlet Pressure"), ("DeltaP", "Pressure Drop"), ("Kv_General", "General Service Kv/Cv (IEC 60534)"),
                    ("Kv_Steam", "Steam Service Kv/Cv"), ("Kv_Liquid", "Liquid Service Kv/Cv (Deprecated)"), ("Kv_Gas", "Gas Service Kv/Cv (Deprecated)")
                },
                Choices =
                {
                    ["FlowCoefficient"] = ("Flow coefficient", new[] { ("Kv", "Kv"), ("Cv", "Cv") }),
                    ["DefinedOpeningKvRelationShipType"] = ("Opening characteristic", new[]
                    {
                        ("Linear", "Linear"), ("EqualPercentage", "Equal Percentage"), ("QuickOpening", "Quick Opening"),
                        ("UserDefined", "User-Defined Expression"), ("DataTable", "Data Table")
                    })
                }
            },
            ["Mixer"] = new TypeEntry
            {
                Choices = { ["PressureCalculation"] = ("Pressure calculation", new[] { ("Minimum", "Inlet Minimum"), ("Average", "Inlet Average"), ("Maximum", "Inlet Maximum") }) }
            },
            ["Splitter"] = new TypeEntry
            {
                ModeProperty = "OperationMode",
                Modes = new[]
                {
                    ("SplitRatios", "Split Ratios"), ("StreamMassFlowSpec", "Stream Mass Flow Spec"),
                    ("StreamMoleFlowSpec", "Stream Molar Flow Spec"), ("StreamVolumetricFlowSpec", "Stream Volumetric Flow Spec")
                }
            },
            ["Vessel"] = new TypeEntry
            {
                ModeProperty = "CalculationMode",
                Modes = new[]
                {
                    ("Adiabatic", "Adiabatic"), ("Legacy", "Legacy"),
                    ("HeatingCoolingIsothermic", "Heating/Cooling Isothermic"), ("HeatingCoolingIsobaric", "Heating/Cooling Isobaric")
                },
                Choices = { ["PressureCalculation"] = ("Pressure calculation", new[] { ("Minimum", "Inlet Minimum"), ("Average", "Inlet Average"), ("Maximum", "Inlet Maximum") }) }
            },
            ["HeatExchanger"] = new TypeEntry
            {
                ModeProperty = "CalculationMode",
                Modes = new[]
                {
                    ("CalcTempHotOut", "Calculate Hot Fluid Outlet Temperature"), ("CalcTempColdOut", "Calculate Cold Fluid Outlet Temperature"),
                    ("CalcBothTemp", "Calculate Both Outlet Temperatures"), ("CalcBothTemp_UA", "Calculate Both Temperatures (UA)"),
                    ("CalcArea", "Calculate Area"), ("ShellandTube_Rating", "Shell and Tube (Rating)"),
                    ("ShellandTube_CalcFoulingFactor", "Shell and Tube (Fouling Factor)"), ("PinchPoint", "Pinch Point"),
                    ("ThermalEfficiency", "Thermal Efficiency"), ("OutletVaporFraction1", "Hot Fluid Outlet Vapor Fraction"),
                    ("OutletVaporFraction2", "Cold Fluid Outlet Vapor Fraction")
                },
                Choices = { ["FlowDir"] = ("Flow direction", new[] { ("CounterCurrent", "Counter Current"), ("CoCurrent", "Co Current") }) }
            },
            ["ComponentSeparator"] = new TypeEntry
            {
                // The specified outlet is a choice in the compound splits section.
                HiddenFields = { "Specified stream index" }
            },
            ["ShortcutColumn"] = new TypeEntry
            {
                Choices = { ["condtype"] = ("Condenser", new[] { ("TotalCond", "Total"), ("PartialCond", "Partial") }) }
            },
            ["RCT_Conversion"] = Reactor(),
            ["RCT_Equilibrium"] = Reactor(),
            ["RCT_Gibbs"] = Reactor(),
            ["RCT_CSTR"] = Reactor(),
            ["RCT_PFR"] = Reactor(),
            // The variables, set point and expression are in the Variables section (VariablePicker).
            ["OT_Adjust"] = new TypeEntry { HiddenFields = { "Set-point" } },
            ["OT_Spec"] = new TypeEntry
            {
                HiddenFields = { "Expression" },
                Choices =
                {
                    ["SpecCalculationMode"] = ("Calculation mode", new[]
                    {
                        ("GlobalSetting", "Global Setting"), ("AfterSourceObject", "After Source Object"),
                        ("BeforeTargetObject", "Before Target Object"), ("BeforeFlowsheet", "Before Flowsheet"),
                        ("AfterFlowsheet", "After Flowsheet"), ("AfterObject", "After Object"), ("BeforeObject", "Before Object")
                    })
                }
            },
        };

        private static TypeEntry Reactor() => new TypeEntry
        {
            ModeProperty = "ReactorOperationMode",
            Modes = new[] { ("Isothermic", "Isothermic"), ("Adiabatic", "Adiabatic"), ("OutletTemperature", "Define Outlet Temperature") }
        };

        private static readonly TypeEntry Empty = new TypeEntry();

        public static TypeEntry For(string objectType) => Entries.TryGetValue(objectType, out var e) ? e : Empty;

        public static IEnumerable<string> CataloguedTypes => Entries.Keys;

        /// <summary>A label for an identifier the catalog does not name: "CalcTempHotOut" → "Calc temp hot out".</summary>
        public static string Humanize(string name)
        {
            var spaced = Regex.Replace(name.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Trim();
            if (spaced.Length == 0) return name;
            return char.ToUpperInvariant(spaced[0]) + spaced.Substring(1).ToLowerInvariant();
        }
    }
}
