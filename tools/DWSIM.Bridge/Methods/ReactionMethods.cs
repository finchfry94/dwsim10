using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using Newtonsoft.Json.Linq;
using Rxn = DWSIM.Thermodynamics.BaseClasses;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// The flowsheet's reactions and reaction sets, as the Avalonia reaction manager and its four
    /// editors handle them. A reaction travels as one JSON shape whatever its type; the fields
    /// a type does not use are ignored.
    /// </summary>
    public sealed class ReactionMethods
    {
        private const string DefaultSet = "DefaultSet";
        private readonly SessionStore _sessions;

        public ReactionMethods(SessionStore sessions) { _sessions = sessions; }

        private static readonly string[] ConcMolar = { "kmol/m3", "mol/m3", "mol/L", "mol/cm3", "mol/mL", "lbmol/ft3" };
        private static readonly string[] ConcMass = { "kg/m3", "g/L", "g/cm3", "g/mL", "lbm/ft3" };
        private static readonly string[] Pressures = { "Pa", "atm", "kgf/cm2", "kPa", "bar", "MPa", "psi", "mmHg" };
        private static readonly string[] RateHomogeneous = { "kmol/[m3.s]", "kmol/[m3.min.]", "kmol/[m3.h]", "mol/[m3.s]", "mol/[m3.min.]", "mol/[m3.h]", "mol/[L.s]", "mol/[L.min.]", "mol/[L.h]", "mol/[cm3.s]", "mol/[cm3.min.]", "mol/[cm3.h]", "lbmol/[ft3.h]" };
        private static readonly string[] RateCatalytic = { "kmol/[kg.s]", "kmol/[kg.min.]", "kmol/[kg.h]", "mol/[kg.s]", "mol/[kg.min.]", "mol/[kg.h]", "lbmol/[lbm.h]" };

        /// <summary>Reactions, sets, the compound data a stoichiometry preview needs, and the option lists.</summary>
        [RpcMethod("reactions.list")]
        public JObject List(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var su = fs.FlowsheetOptions.SelectedUnitSystem;
                string[] Set(UnitOfMeasure u) { try { return su.GetUnitSet(u).Distinct().ToArray(); } catch { return new string[0]; } }
                return new JObject
                {
                    ["reactions"] = new JArray(fs.Reactions.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => new JObject
                    {
                        ["id"] = r.ID,
                        ["name"] = r.Name,
                        ["type"] = r.ReactionType.ToString(),
                        ["equation"] = r.Equation ?? "",
                        ["description"] = r.Description ?? ""
                    })),
                    ["sets"] = new JArray(fs.ReactionSets.Values.Select(SetJson)),
                    ["compounds"] = new JArray(fs.SelectedCompounds.Values.Select(c => new JObject
                    {
                        ["name"] = c.Name,
                        ["formula"] = c.Formula,
                        ["molarWeight"] = c.Molar_Weight,
                        // kJ/kg in the database; per kmol here, as the reaction heat is.
                        ["enthalpyOfFormation"] = c.IG_Enthalpy_of_Formation_25C * c.Molar_Weight,
                        ["gibbsOfFormation"] = c.IG_Gibbs_Energy_of_Formation_25C * c.Molar_Weight
                    })),
                    ["options"] = new JObject
                    {
                        ["phases"] = Names<ReactionPhase>(),
                        ["bases"] = Names<ReactionBasis>(),
                        ["energyUnits"] = new JArray(Set(UnitOfMeasure.molar_enthalpy).DefaultIfEmpty("J/mol")),
                        ["basisUnits"] = new JObject
                        {
                            ["MolarConc"] = new JArray(ConcMolar),
                            ["MassConc"] = new JArray(ConcMass),
                            ["PartialPress"] = new JArray(Pressures)
                        },
                        ["rateUnits"] = new JObject { ["Kinetic"] = new JArray(RateHomogeneous), ["Heterogeneous_Catalytic"] = new JArray(RateCatalytic) },
                        ["scripts"] = new JArray(fs.Scripts.Values.Select(s => s.Title))
                    }
                };
            }
        }

        private static JArray Names<T>() where T : struct, Enum => new JArray(Enum.GetNames(typeof(T)));

        private static JObject SetJson(IReactionSet s) => new JObject
        {
            ["id"] = s.ID,
            ["name"] = s.Name,
            ["description"] = s.Description ?? "",
            ["isDefault"] = s.ID == DefaultSet,
            ["reactions"] = new JArray(s.Reactions.Values.OrderBy(r => r.Rank).Select(r => new JObject
            {
                ["id"] = r.ReactionID,
                ["active"] = r.IsActive,
                ["rank"] = r.Rank
            }))
        };

        [RpcMethod("reactions.get")]
        public JObject Get(string flowsheetId, string id)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate) return ToJson(Find(session.Inner, id));
        }

        private static IReaction Find(IFlowsheet fs, string id)
            => fs.Reactions.TryGetValue(id ?? "", out var r) ? r : throw new RpcException(RpcErrorCodes.InvalidParams, "No reaction with id '" + id + "'.");

        internal static JObject ToJson(IReaction r) => new JObject
        {
            ["id"] = r.ID,
            ["type"] = r.ReactionType.ToString(),
            ["name"] = r.Name,
            ["description"] = r.Description ?? "",
            ["equation"] = r.Equation ?? "",
            ["heat"] = r.ReactionHeat,
            ["gibbs"] = r.ReactionGibbsEnergy,
            ["components"] = new JArray(r.Components.Values.Select(c => new JObject
            {
                ["compound"] = c.CompName,
                ["coefficient"] = c.StoichCoeff,
                ["base"] = c.IsBaseReactant,
                ["directOrder"] = c.DirectOrder,
                ["reverseOrder"] = c.ReverseOrder
            })),
            ["phase"] = r.ReactionPhase.ToString(),
            ["basis"] = r.ReactionBasis.ToString(),
            ["basisUnit"] = r.ReactionType == ReactionType.Equilibrium ? r.EquilibriumReactionBasisUnits ?? "" : r.ConcUnit ?? "",
            ["rateUnit"] = r.VelUnit ?? "",
            ["tmin"] = r.Tmin,
            ["tmax"] = r.Tmax,
            ["approach"] = r.Approach,
            ["keq"] = r.KExprType.ToString(),
            ["expression"] = r.Expression ?? "",
            ["constantKeq"] = r.ConstantKeqValue,
            ["kinetics"] = r.ReactionKinetics.ToString(),
            ["script"] = r.ScriptTitle ?? "",
            ["forward"] = new JObject
            {
                ["type"] = r.ReactionKinFwdType.ToString(),
                ["a"] = r.A_Forward,
                ["e"] = r.E_Forward,
                ["eUnit"] = r.E_Forward_Unit ?? "J/mol",
                ["expression"] = r.ReactionKinFwdExpression ?? ""
            },
            ["reverse"] = new JObject
            {
                ["type"] = r.ReactionKinRevType.ToString(),
                ["a"] = r.A_Reverse,
                ["e"] = r.E_Reverse,
                ["eUnit"] = r.E_Reverse_Unit ?? "J/mol",
                ["expression"] = r.ReactionKinRevExpression ?? ""
            },
            ["numerator"] = r.RateEquationNumerator ?? "",
            ["denominator"] = r.RateEquationDenominator ?? "1"
        };

        /// <summary>
        /// Creates (no id) or replaces a reaction. Refused unless it is balanced, has a base
        /// reactant and a product, and a name, as the Avalonia editors refuse. A new reaction
        /// joins the default set.
        /// </summary>
        [RpcMethod("reactions.save")]
        public JObject Save(string flowsheetId, JObject reaction)
        {
            if (reaction == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the reaction.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var id = reaction.Value<string>("id");
                IReaction existing = string.IsNullOrEmpty(id) ? null : Find(fs, id);
                var type = existing?.ReactionType ?? Parse<ReactionType>(reaction, "type", ReactionType.Conversion);

                var rows = (reaction["components"] as JArray ?? new JArray())
                    .Select(c => new
                    {
                        compound = c.Value<string>("compound"),
                        coefficient = c.Value<double?>("coefficient") ?? 0,
                        isBase = c.Value<bool?>("base") ?? false,
                        direct = c.Value<double?>("directOrder") ?? 0,
                        reverse = c.Value<double?>("reverseOrder") ?? 0
                    })
                    .ToList();
                var unknown = rows.Where(r => !fs.SelectedCompounds.ContainsKey(r.compound ?? "")).Select(r => r.compound).ToList();
                if (unknown.Count > 0) throw new RpcException(RpcErrorCodes.InvalidParams, "Not in the flowsheet: " + string.Join(", ", unknown) + ".");
                var name = (reaction.Value<string>("name") ?? "").Trim();
                if (name.Length == 0) throw Refuse("Enter a name for the reaction.");
                var bases = rows.Where(r => r.isBase).ToList();
                if (bases.Count != 1) throw Refuse("Mark one reactant as the base component.");
                if (bases[0].coefficient >= 0) throw Refuse("The base component must be a reactant (a negative stoichiometric coefficient).");
                if (!rows.Any(r => r.coefficient > 0)) throw Refuse("Add at least one product (a positive stoichiometric coefficient).");
                var balance = rows.Sum(r => r.coefficient * fs.SelectedCompounds[r.compound].Molar_Weight);
                if (Math.Abs(balance) >= 1e-3)
                    throw Refuse($"The stoichiometry is not mass-balanced ({balance.ToString("G6", CultureInfo.InvariantCulture)} kg/kmol). Balance it, or adjust the coefficients.");

                session.Record((existing == null ? "Add reaction " : "Edit reaction ") + name);
                var r = existing ?? new Rxn.Reaction { ID = Guid.NewGuid().ToString(), ReactionType = type };
                r.Name = name.Replace(":", "_");
                r.Description = reaction.Value<string>("description") ?? "";
                r.BaseReactant = bases[0].compound;
                r.Components.Clear();
                foreach (var row in rows)
                    r.Components[row.compound] = new Rxn.ReactionStoichBase(row.compound, row.coefficient, row.isBase, row.direct, row.reverse);
                ApplyParameters(r, type, reaction);
                WriteDerived(fs, r, type);
                if (existing == null)
                {
                    fs.AddReaction(r);
                    if (fs.ReactionSets.ContainsKey(DefaultSet)) fs.AddReactionToSet(r.ID, DefaultSet, true, 0);
                }
                return ToJson(r);
            }
        }

        private static RpcException Refuse(string message) => new RpcException(RpcErrorCodes.InvalidParams, message);

        private static T Parse<T>(JObject o, string key, T fallback) where T : struct, Enum
            => Enum.TryParse<T>(o.Value<string>(key) ?? "", out var v) ? v : fallback;

        private static void ApplyParameters(IReaction r, ReactionType type, JObject o)
        {
            double Num(string key, double fallback) => o.Value<double?>(key) ?? fallback;
            switch (type)
            {
                case ReactionType.Conversion:
                    r.ReactionPhase = Parse(o, "phase", ReactionPhase.Mixture);
                    r.Expression = o.Value<string>("expression") ?? "";
                    break;
                case ReactionType.Equilibrium:
                    // An equilibrium reaction is in the vapor or the liquid, as its editor offers.
                    r.ReactionPhase = Parse(o, "phase", ReactionPhase.Vapor) == ReactionPhase.Liquid ? ReactionPhase.Liquid : ReactionPhase.Vapor;
                    r.ReactionBasis = Parse(o, "basis", ReactionBasis.Fugacity);
                    r.EquilibriumReactionBasisUnits = o.Value<string>("basisUnit") ?? "";
                    r.Tmin = Num("tmin", 298.15);
                    r.Tmax = Num("tmax", 2000);
                    r.Approach = Num("approach", 1);
                    r.KExprType = Parse(o, "keq", KOpt.Gibbs);
                    if (r.KExprType == KOpt.Expression) r.Expression = o.Value<string>("expression") ?? "";
                    if (r.KExprType == KOpt.Constant) r.ConstantKeqValue = Num("constantKeq", 1);
                    break;
                default:
                    r.ReactionKinetics = Parse(o, "kinetics", ReactionKinetics.Expression);
                    r.ScriptTitle = o.Value<string>("script") ?? "";
                    r.ReactionPhase = Parse(o, "phase", ReactionPhase.Mixture);
                    r.ReactionBasis = Parse(o, "basis", ReactionBasis.MolarConc);
                    r.ConcUnit = o.Value<string>("basisUnit") ?? "";
                    r.VelUnit = o.Value<string>("rateUnit") ?? "";
                    r.Tmin = Num("tmin", 0);
                    r.Tmax = Num("tmax", 2000);
                    if (type == ReactionType.Kinetic)
                    {
                        var f = o["forward"] as JObject ?? new JObject();
                        var b = o["reverse"] as JObject ?? new JObject();
                        r.ReactionKinFwdType = Parse(f, "type", ReactionKineticType.Arrhenius);
                        r.A_Forward = f.Value<double?>("a") ?? 0;
                        r.E_Forward = f.Value<double?>("e") ?? 0;
                        r.E_Forward_Unit = f.Value<string>("eUnit") ?? "J/mol";
                        r.ReactionKinFwdExpression = f.Value<string>("expression") ?? "";
                        r.ReactionKinRevType = Parse(b, "type", ReactionKineticType.Arrhenius);
                        r.A_Reverse = b.Value<double?>("a") ?? 0;
                        r.E_Reverse = b.Value<double?>("e") ?? 0;
                        r.E_Reverse_Unit = b.Value<string>("eUnit") ?? "J/mol";
                        r.ReactionKinRevExpression = b.Value<string>("expression") ?? "";
                    }
                    else
                    {
                        r.RateEquationNumerator = o.Value<string>("numerator") ?? "";
                        r.RateEquationDenominator = string.IsNullOrWhiteSpace(o.Value<string>("denominator")) ? "1" : o.Value<string>("denominator");
                    }
                    break;
            }
        }

        /// <summary>The equation, reaction heat and Gibbs energy (per kmol of base reactant) and balance, as the editors write them.</summary>
        internal static void WriteDerived(IFlowsheet fs, IReaction r, ReactionType type)
        {
            double hr = 0, hp = 0, gr = 0, gp = 0, br = 0, bp = 0, brsc = 1;
            var reactants = new List<string>();
            var products = new List<string>();
            var inerts = new List<string>();
            foreach (var c in r.Components.Values)
            {
                var cp = fs.SelectedCompounds[c.CompName];
                var a = Math.Abs(c.StoichCoeff);
                var term = (Math.Abs(a - 1) < 1e-10 ? "" : a.ToString("G4", CultureInfo.InvariantCulture)) + cp.Formula;
                if (c.StoichCoeff < 0)
                {
                    reactants.Add(term);
                    hr += a * cp.IG_Enthalpy_of_Formation_25C * cp.Molar_Weight;
                    gr += a * cp.IG_Gibbs_Energy_of_Formation_25C * cp.Molar_Weight;
                    br += a * cp.Molar_Weight;
                }
                else if (c.StoichCoeff > 0)
                {
                    products.Add(term);
                    hp += a * cp.IG_Enthalpy_of_Formation_25C * cp.Molar_Weight;
                    gp += a * cp.IG_Gibbs_Energy_of_Formation_25C * cp.Molar_Weight;
                    bp += a * cp.Molar_Weight;
                }
                else inerts.Add(cp.Formula);
                if (c.IsBaseReactant && a > 0) brsc = a;
            }
            var arrow = type == ReactionType.Conversion ? " --> " : " <--> ";
            var eq = string.Join(" + ", reactants) + arrow + string.Join(" + ", products);
            if (type == ReactionType.Heterogeneous_Catalytic && inerts.Count > 0) eq += "  (N: " + string.Join(", ", inerts) + ")";
            r.ReactionType = type;
            r.Equation = eq;
            r.ReactionHeat = (hp - hr) / brsc;
            r.ReactionGibbsEnergy = (gp - gr) / brsc;
            r.StoichBalance = bp - br;
        }

        /// <summary>Removes a reaction from the flowsheet and from every set.</summary>
        [RpcMethod("reactions.delete")]
        public JObject Delete(string flowsheetId, string id)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var r = Find(session.Inner, id);
                session.Record("Delete reaction " + r.Name);
                session.Inner.Reactions.Remove(r.ID);
                foreach (var s in session.Inner.ReactionSets.Values) s.Reactions.Remove(r.ID);
                return List(flowsheetId);
            }
        }

        /// <summary>A copy of a reaction under a new name, in the default set.</summary>
        [RpcMethod("reactions.duplicate")]
        public JObject Duplicate(string flowsheetId, string id)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var r = Find(fs, id);
                session.Record("Duplicate reaction " + r.Name);
                var clone = new Rxn.Reaction();
                ((ICustomXMLSerialization)clone).LoadData(((ICustomXMLSerialization)r).SaveData());
                clone.ID = Guid.NewGuid().ToString();
                var names = new HashSet<string>(fs.Reactions.Values.Select(x => x.Name));
                var n = 2;
                while (names.Contains(r.Name + " " + n)) n++;
                clone.Name = r.Name + " " + n;
                fs.AddReaction(clone);
                if (fs.ReactionSets.ContainsKey(DefaultSet)) fs.AddReactionToSet(clone.ID, DefaultSet, true, 0);
                return ToJson(clone);
            }
        }

        /// <summary>Creates (no id) or replaces a set: its name, description and which reactions it runs, at what rank.</summary>
        [RpcMethod("reactionSets.save")]
        public JObject SaveSet(string flowsheetId, JObject set)
        {
            if (set == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the reaction set.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var id = set.Value<string>("id");
                IReactionSet existing = null;
                if (!string.IsNullOrEmpty(id) && !fs.ReactionSets.TryGetValue(id, out existing))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "No reaction set with id '" + id + "'.");
                var name = (set.Value<string>("name") ?? "").Trim();
                if (name.Length == 0) throw Refuse("Enter a name for the reaction set.");
                var members = (set["reactions"] as JArray ?? new JArray()).Select(m => new
                {
                    id = m.Value<string>("id"),
                    active = m.Value<bool?>("active") ?? true,
                    rank = m.Value<int?>("rank") ?? 0
                }).ToList();
                var unknown = members.Where(m => !fs.Reactions.ContainsKey(m.id ?? "")).Select(m => m.id).ToList();
                if (unknown.Count > 0) throw new RpcException(RpcErrorCodes.InvalidParams, "No reaction with id " + string.Join(", ", unknown) + ".");

                session.Record((existing == null ? "Add reaction set " : "Edit reaction set ") + name);
                var s = existing ?? new Rxn.ReactionSet(Guid.NewGuid().ToString(), name, "");
                s.Name = name;
                s.Description = set.Value<string>("description") ?? "";
                s.Reactions.Clear();
                foreach (var m in members) s.Reactions[m.id] = new Rxn.ReactionSetBase(m.id, m.rank, m.active);
                if (existing == null) fs.AddReactionSet(s);
                return SetJson(s);
            }
        }

        /// <summary>Removes a set; the default set stays. Reactors that used it are left without one.</summary>
        [RpcMethod("reactionSets.delete")]
        public JObject DeleteSet(string flowsheetId, string id)
        {
            if (id == DefaultSet) throw Refuse("The default reaction set cannot be removed.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                if (!session.Inner.ReactionSets.TryGetValue(id ?? "", out var s))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "No reaction set with id '" + id + "'.");
                session.Record("Delete reaction set " + s.Name);
                session.Inner.ReactionSets.Remove(s.ID);
                return List(flowsheetId);
            }
        }
    }
}
