using System;
using System.Collections.Generic;
using System.Linq;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.PureCompoundData.Builder;
using DWSIM.PureCompoundData.Core;
using ConstantProperties = DWSIM.Thermodynamics.BaseClasses.ConstantProperties;
using PcdCompound = DWSIM.PureCompoundData.Core.Compound;
using DWSIM.Thermodynamics.Streams;
using DWSIM.UI.Desktop.Editors;
using Newtonsoft.Json.Linq;
using Converter = DWSIM.SharedClasses.SystemsOfUnits.Converter;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// New compounds from what is known about them: a structure (SMILES) for the group
    /// contribution estimators (Joback, then Lee-Kesler and Rackett, as the compound importer
    /// chains them) and any constants the user has, which take precedence over estimates.
    /// </summary>
    public sealed class CompoundMethods
    {
        private readonly SessionStore _sessions;

        public CompoundMethods(SessionStore sessions) { _sessions = sessions; }

        /// <summary>The constants a user can give, with the property the builder files them under (SI).</summary>
        private static readonly (string key, PropertyCategory category, string property, string siUnit)[] Known =
        {
            ("tc", PropertyCategory.Critical, "Tc", "K"),
            ("pc", PropertyCategory.Critical, "Pc", "Pa"),
            ("vc", PropertyCategory.Critical, "Vc", "m3/mol"),
            ("omega", PropertyCategory.Acentric, "omega", ""),
            ("tb", PropertyCategory.NormalBoilingPoint, "Tb", "K")
        };

        private static BuiltConstantProperties Build(JObject compound, JObject units)
        {
            var name = (compound.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the compound a name.");
            var smiles = (compound.Value<string>("smiles") ?? "").Trim();
            var formula = (compound.Value<string>("formula") ?? "").Trim();
            var mw = compound.Value<double?>("molarWeight");
            if (smiles.Length == 0 && formula.Length == 0 && mw == null)
                throw new RpcException(RpcErrorCodes.InvalidParams, "Give a structure (SMILES), a formula or a molar weight.");
            if (smiles.Length > 0)
            {
                var (f, m) = FromSmiles(smiles);
                if (formula.Length == 0) formula = f;
                mw ??= m;
            }
            var identity = new PcdCompound(compound.Value<string>("cas") ?? "", name, null, smiles.Length == 0 ? null : smiles, null,
                formula.Length == 0 ? null : formula, mw);
            var citation = new Citation(null, "Entered by the user", Array.Empty<string>(), null, null, null, null);
            Func<PropertyCategory, string, double?, string, PureCompoundRecord> Record = (cat, prop, value, unit) =>
                new PureCompoundRecord(Guid.NewGuid().ToString("N"), identity, cat, prop, value, null, unit, null, null, null,
                    MeasurementMethod.Unknown, citation, "User");
            var records = new List<PureCompoundRecord> { Record(PropertyCategory.Identification, "Name", null, "") };
            foreach (var (key, category, property, siUnit) in Known)
            {
                var value = compound.Value<double?>(key);
                if (value == null) continue;
                var unit = units?.Value<string>(key);
                var si = string.IsNullOrEmpty(unit) || unit == siUnit ? value.Value : Converter.ConvertToSI(unit, value.Value);
                // The converter's molar volume is per kmol; the builder's per mol.
                if (key == "vc" && !string.IsNullOrEmpty(unit) && unit != siUnit) si /= 1000.0;
                records.Add(Record(category, property, si, siUnit));
            }
            BuiltConstantProperties built;
            try { built = new ConstantPropertiesBuilder().Build(records, null, smiles.Length == 0 ? null : smiles); }
            catch (Exception ex) { throw new RpcException(RpcErrorCodes.InvalidParams, "The structure could not be read: " + (ex.InnerException ?? ex).Message); }
            built.Name = name;
            return built;
        }

        /// <summary>The molecular formula and molar weight of a structure (NCDK, as the fragmenter reads it).</summary>
        internal static (string formula, double mw) FromSmiles(string smiles)
        {
            NCDK.IAtomContainer mol;
            try { mol = new NCDK.Smiles.SmilesParser(NCDK.Silent.ChemObjectBuilder.Instance).ParseSmiles(smiles); }
            catch (Exception ex) { throw new RpcException(RpcErrorCodes.InvalidParams, "'" + smiles + "' is not a structure the SMILES reader understands: " + ex.Message); }
            NCDK.Tools.Manipulator.AtomContainerManipulator.PercieveAtomTypesAndConfigureAtoms(mol);
            NCDK.CDK.HydrogenAdder.AddImplicitHydrogens(mol);
            var mf = NCDK.Tools.Manipulator.MolecularFormulaManipulator.GetMolecularFormula(mol);
            return (NCDK.Tools.Manipulator.MolecularFormulaManipulator.GetString(mf),
                NCDK.Tools.Manipulator.MolecularFormulaManipulator.GetMass(mf, NCDK.Tools.Manipulator.MolecularWeightTypes.MolWeight));
        }

        private static JObject Describe(BuiltConstantProperties built, ConstantProperties cp)
        {
            var checklist = CompoundDataChecklist.For(cp);
            return new JObject
            {
                ["name"] = built.Name,
                ["formula"] = built.Formula,
                ["smiles"] = built.Smiles,
                ["properties"] = new JArray(PureCompoundImporter.Describe(built).Select(r => new JObject
                {
                    ["property"] = r.Property,
                    ["value"] = r.Value,
                    ["unit"] = r.Units,
                    ["origin"] = r.Origin
                })),
                ["groups"] = new JObject(built.JobackGroups.OrderBy(g => g.Key).Select(g => new JProperty(g.Key, g.Value))),
                // Groups Joback has no contribution for: then it gives no estimate, and the
                // constants have to be entered.
                ["unsupportedGroups"] = new JArray(DWSIM.PureCompoundData.Estimation.Joback.JobackEstimator.Unsupported(built.JobackGroups.Keys)),
                ["checklist"] = new JArray(checklist.Select(c => new JObject { ["property"] = c.Property, ["available"] = c.Available })),
                // An equation of state needs these; the activity models need more, but start from them.
                ["usable"] = cp.Molar_Weight > 0 && cp.Critical_Temperature > 0 && cp.Critical_Pressure > 0
            };
        }

        /// <summary>What the estimators make of a compound, without adding it.</summary>
        [RpcMethod("compounds.estimate")]
        public JObject Estimate(string flowsheetId, JObject compound, JObject units = null)
        {
            _sessions.Get(flowsheetId);
            if (compound == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Describe the compound.");
            var built = Build(compound, units);
            return Describe(built, PureCompoundImporter.ToConstantProperties(built));
        }

        /// <summary>
        /// Estimates the compound and adds it to the flowsheet, and to every stream at zero flow.
        /// Refused when the name is taken or the compound lacks what an equation of state needs.
        /// </summary>
        [RpcMethod("compounds.create")]
        public JObject Create(string flowsheetId, JObject compound, JObject units = null)
        {
            if (compound == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Describe the compound.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var built = Build(compound, units);
                if (fs.AvailableCompounds.ContainsKey(built.Name) || fs.SelectedCompounds.ContainsKey(built.Name))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "There is already a compound called '" + built.Name + "'.");
                var cp = PureCompoundImporter.ToConstantProperties(built);
                cp.Name = built.Name;
                if (!(cp.Molar_Weight > 0 && cp.Critical_Temperature > 0 && cp.Critical_Pressure > 0))
                    throw new RpcException(RpcErrorCodes.InvalidParams,
                        "Not enough is known to use the compound: give its structure (SMILES), or its molar weight, critical temperature and pressure.");
                session.Record("Create compound " + cp.Name);
                Add(fs, cp);
                var described = Describe(built, cp);
                described["summary"] = FlowsheetMethods.Summary(session);
                return described;
            }
        }

        /// <summary>Makes a compound available and selected, and gives every stream a zero amount of it.</summary>
        internal static void Add(IFlowsheet fs, ICompoundConstantProperties cp)
        {
            if (!fs.AvailableCompounds.ContainsKey(cp.Name)) fs.AvailableCompounds.Add(cp.Name, cp);
            if (!fs.SelectedCompounds.ContainsKey(cp.Name)) fs.SelectedCompounds.Add(cp.Name, fs.AvailableCompounds[cp.Name]);
            foreach (var ms in fs.SimulationObjects.Values.OfType<MaterialStream>())
                foreach (var phase in ms.Phases.Values)
                    if (!phase.Compounds.ContainsKey(cp.Name))
                        phase.Compounds.Add(cp.Name, new DWSIM.Thermodynamics.BaseClasses.Compound(cp.Name, "") { ConstantProperties = fs.SelectedCompounds[cp.Name] });
        }
    }
}
