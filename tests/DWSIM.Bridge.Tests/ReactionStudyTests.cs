using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>Reactions and reaction sets, sensitivity analysis and the optimizer.</summary>
    [NonParallelizable]
    public class ReactionStudyTests
    {
        private sealed class Silent : INotifier
        {
            public void Notify(string method, JToken parameters) { }
        }

        private RpcRegistry _registry;

        [OneTimeSetUp]
        public void StartEngine()
        {
            _registry = new RpcRegistry();
            BridgeHost.Register(_registry, new SessionStore(), new Silent());
            BridgeHost.WarmUp();
        }

        private JToken Call(string method, object args)
            => _registry.Invoke(method, JObject.FromObject(args), CancellationToken.None);

        private string NewFlowsheet(string package, params string[] compounds)
        {
            var fs = Call("flowsheet.new", new { name = "m5a" }).Value<string>("id");
            Call("flowsheet.addCompounds", new { flowsheetId = fs, names = compounds });
            Call("flowsheet.addPropertyPackage", new { flowsheetId = fs, name = package });
            Call("flowsheet.setUnitSystem", new { flowsheetId = fs, name = "SI" });
            return fs;
        }

        private string Add(string fs, string type, string tag, double x)
            => Call("graph.add", new { flowsheetId = fs, type, x, y = 0, tag }).Value<string>("id");

        private void Connect(string fs, string source, string sourcePort, string target, string targetPort)
            => Call("graph.connect", new { flowsheetId = fs, sourceId = source, sourcePort, targetId = target, targetPort });

        private static object Combustion(string expression) => new
        {
            type = "Conversion",
            name = "Combustion",
            phase = "Vapor",
            expression,
            components = new object[]
            {
                new { compound = "Methane", coefficient = -1.0, @base = true },
                new { compound = "Oxygen", coefficient = -2.0 },
                new { compound = "Carbon dioxide", coefficient = 1.0 },
                new { compound = "Water", coefficient = 2.0 }
            }
        };

        [Test]
        public void AConversionReactionInTheDefaultSetConvertsItsStatedFraction()
        {
            var fs = NewFlowsheet("Peng-Robinson (PR)", "Methane", "Oxygen", "Carbon dioxide", "Water", "Nitrogen");
            var r = Call("reactions.save", new { flowsheetId = fs, reaction = JObject.FromObject(Combustion("60")) });
            Assert.That(r.Value<string>("equation"), Does.Contain("CH4 + 2O2 --> CO2"));
            Assert.That(r.Value<double>("heat"), Is.EqualTo(-802.6e3).Within(2e3), "kJ/kmol of methane, lower heating value");
            var list = Call("reactions.list", new { flowsheetId = fs });
            var set = list["sets"].Single(s => s.Value<bool>("isDefault"));
            Assert.That(set["reactions"].Select(m => m.Value<string>("id")), Is.EqualTo(new[] { r.Value<string>("id") }));

            var feed = Add(fs, "MaterialStream", "feed", 0);
            var reactor = Add(fs, "RCT_Conversion", "R-1", 100);
            var vapor = Add(fs, "MaterialStream", "vapor", 200);
            var liquid = Add(fs, "MaterialStream", "liquid", 200);
            Call("stream.specify", new { flowsheetId = fs, id = feed, temperature = 600.0, pressure = 1e5, molarFlow = 10.0, moleFractions = new { Methane = 0.1, Oxygen = 0.25, Nitrogen = 0.65 } });
            Connect(fs, feed, "out-0", reactor, "in-0");
            Connect(fs, reactor, "out-0", vapor, "in-0");
            Connect(fs, reactor, "out-1", liquid, "in-0");
            Call("object.setField", new { flowsheetId = fs, id = reactor, field = "reactionSet", value = "DefaultSet" });
            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);
            double Methane(string id) => Call("stream.get", new { flowsheetId = fs, id })["phases"][0]["compounds"]
                .Where(c => c.Value<string>("name") == "Methane").Sum(c => c.Value<double>("molarFlow"));
            Assert.That(Methane(vapor) + Methane(liquid), Is.EqualTo(0.4).Within(1e-6), "60 % of 1 mol/s converted");

            // Edited to 90 %, the reactor follows; deleted, it leaves every set.
            var edited = JObject.FromObject(Combustion("90"));
            edited["id"] = r.Value<string>("id");
            Call("reactions.save", new { flowsheetId = fs, reaction = edited });
            Call("solve.run", new { flowsheetId = fs });
            Assert.That(Methane(vapor) + Methane(liquid), Is.EqualTo(0.1).Within(1e-6));
            Call("reactions.delete", new { flowsheetId = fs, id = r.Value<string>("id") });
            Assert.That(Call("reactions.list", new { flowsheetId = fs })["sets"].SelectMany(s => s["reactions"]), Is.Empty);
            Call("edit.undo", new { flowsheetId = fs });
            Assert.That(Call("reactions.list", new { flowsheetId = fs })["reactions"].Count(), Is.EqualTo(1), "undo brings it back");
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "m5a-" + System.Guid.NewGuid().ToString("N") + ".dwxml");
            Call("flowsheet.save", new { flowsheetId = fs, path });
            var reopened = Call("flowsheet.open", new { path }).Value<string>("id");
            Assert.That(Call("reactions.list", new { flowsheetId = reopened })["sets"][0]["reactions"].Count(), Is.EqualTo(1), "saved with the file");
            Call("flowsheet.close", new { flowsheetId = reopened });
            System.IO.File.Delete(path);
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void UnbalancedOrBaselessReactionsAreRefused()
        {
            var fs = NewFlowsheet("Peng-Robinson (PR)", "Methane", "Oxygen", "Carbon dioxide", "Water");
            var bad = JObject.FromObject(Combustion("50"));
            bad["components"][1]["coefficient"] = -1.0;
            var ex = Assert.Throws<RpcException>(() => Call("reactions.save", new { flowsheetId = fs, reaction = bad }));
            Assert.That(ex.Message, Does.Contain("not mass-balanced"));
            var baseless = JObject.FromObject(Combustion("50"));
            baseless["components"][0]["base"] = false;
            ex = Assert.Throws<RpcException>(() => Call("reactions.save", new { flowsheetId = fs, reaction = baseless }));
            Assert.That(ex.Message, Does.Contain("base component"));
            Assert.That(Call("reactions.list", new { flowsheetId = fs })["reactions"], Is.Empty);
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void EquilibriumReactionsAndSetsRoundTrip()
        {
            var fs = NewFlowsheet("Peng-Robinson (PR)", "Carbon monoxide", "Water", "Carbon dioxide", "Hydrogen");
            var shift = Call("reactions.save", new
            {
                flowsheetId = fs,
                reaction = JObject.FromObject(new
                {
                    type = "Equilibrium",
                    name = "Water-gas shift",
                    phase = "Vapor",
                    basis = "Fugacity",
                    keq = "Constant",
                    constantKeq = 12.5,
                    tmin = 400.0,
                    tmax = 1200.0,
                    approach = 0.0,
                    components = new object[]
                    {
                        new { compound = "Carbon monoxide", coefficient = -1.0, @base = true },
                        new { compound = "Water", coefficient = -1.0 },
                        new { compound = "Carbon dioxide", coefficient = 1.0 },
                        new { compound = "Hydrogen", coefficient = 1.0 }
                    }
                })
            });
            var id = shift.Value<string>("id");
            var back = Call("reactions.get", new { flowsheetId = fs, id });
            Assert.That(back.Value<string>("type"), Is.EqualTo("Equilibrium"));
            Assert.That(back.Value<string>("keq"), Is.EqualTo("Constant"));
            Assert.That(back.Value<double>("constantKeq"), Is.EqualTo(12.5));
            Assert.That(back.Value<double>("tmax"), Is.EqualTo(1200.0));
            Assert.That(back.Value<string>("equation"), Does.Contain("<-->"));

            var copy = Call("reactions.duplicate", new { flowsheetId = fs, id });
            Assert.That(copy.Value<string>("name"), Is.EqualTo("Water-gas shift 2"));
            var set = Call("reactionSets.save", new
            {
                flowsheetId = fs,
                set = JObject.FromObject(new { name = "Shift only", reactions = new[] { new { id, active = true, rank = 1 } } })
            });
            Assert.That(set["reactions"].Single().Value<int>("rank"), Is.EqualTo(1));
            var sets = Call("reactions.list", new { flowsheetId = fs })["sets"];
            Assert.That(sets.Select(s => s.Value<string>("name")), Is.EquivalentTo(new[] { "Default Set", "Shift only" }));
            var ex = Assert.Throws<RpcException>(() => Call("reactionSets.delete", new { flowsheetId = fs, id = "DefaultSet" }));
            Assert.That(ex.Message, Does.Contain("default"));
            Call("reactionSets.delete", new { flowsheetId = fs, id = set.Value<string>("id") });
            Assert.That(Call("reactions.list", new { flowsheetId = fs })["sets"].Count(), Is.EqualTo(1));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        /// <summary>Water heated by a duty: feed 300 K, 1 bar, 1 kg/s.</summary>
        private (string fs, string heater, string product) Heater()
        {
            var fs = NewFlowsheet("Steam Tables (IAPWS-IF97)", "Water");
            var feed = Add(fs, "MaterialStream", "feed", 0);
            var heater = Add(fs, "Heater", "H-1", 100);
            var product = Add(fs, "MaterialStream", "product", 200);
            var duty = Add(fs, "EnergyStream", "Q", 100);
            Call("stream.specify", new { flowsheetId = fs, id = feed, temperature = 300.0, pressure = 1e5, massFlow = 1.0, moleFractions = new { Water = 1.0 } });
            Connect(fs, feed, "out-0", heater, "in-0");
            Connect(fs, heater, "out-0", product, "in-0");
            Connect(fs, duty, "out-0", heater, "energy");
            Call("object.setField", new { flowsheetId = fs, id = heater, field = "PROP_HT_3", value = 50.0, unit = "kW" });
            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);
            return (fs, heater, product);
        }

        [Test]
        public void ASensitivityRunIsLinearInTheDutyAndPutsTheDutyBack()
        {
            var (fs, heater, product) = Heater();
            var before = Call("stream.get", new { flowsheetId = fs, id = product }).Value<double>("temperature");
            var c = Call("sensitivity.save", new
            {
                flowsheetId = fs,
                study = JObject.FromObject(new
                {
                    name = "Duty",
                    independent = new { @object = heater, property = "PROP_HT_3", unit = "kW", from = 0.0, to = 100.0, points = 5 },
                    dependents = new[] { new { @object = product, property = "PROP_MS_0" } }
                })
            });
            Assert.That(c["dependents"][0].Value<string>("unit"), Is.EqualTo("K"), "the flowsheet's unit when none is given");
            var r = Call("sensitivity.run", new { flowsheetId = fs, index = 0 });
            var rows = r["table"]["rows"].Select(row => row.Values<double>().ToArray()).ToList();
            Assert.That(rows.Select(x => x[0]), Is.EqualTo(new[] { 0.0, 25, 50, 75, 100 }));
            Assert.That(rows[0][1], Is.EqualTo(300.0).Within(1e-3));
            var step = rows[1][1] - rows[0][1];
            Assert.That(step, Is.EqualTo(25.0 / 4.18).Within(0.2), "25 kW on 1 kg/s of water");
            for (int i = 2; i < rows.Count; i++) Assert.That(rows[i][1] - rows[i - 1][1], Is.EqualTo(step).Within(0.02));
            Assert.That(r["charts"].Single()["series"].Single()["x"].Count(), Is.EqualTo(5));
            Assert.That(Call("stream.get", new { flowsheetId = fs, id = product }).Value<double>("temperature"), Is.EqualTo(before).Within(1e-9), "the duty is put back");

            Assert.That(Call("sensitivity.list", new { flowsheetId = fs }).Count(), Is.EqualTo(1));
            // The case is part of the file.
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "m5a-" + System.Guid.NewGuid().ToString("N") + ".dwxml");
            Call("flowsheet.save", new { flowsheetId = fs, path });
            var reopened = Call("flowsheet.open", new { path }).Value<string>("id");
            var saved = Call("sensitivity.list", new { flowsheetId = reopened }).Single();
            Assert.That(saved["independent"].Value<double>("to"), Is.EqualTo(100.0));
            Assert.That(saved["dependents"].Single().Value<string>("label"), Is.EqualTo("product · Temperature"));
            Call("flowsheet.close", new { flowsheetId = reopened });
            System.IO.File.Delete(path);
            Call("sensitivity.delete", new { flowsheetId = fs, index = 0 });
            Assert.That(Call("sensitivity.list", new { flowsheetId = fs }), Is.Empty);
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [TestCase("DN_NELDERMEAD_SIMPLEX")]
        [TestCase("DN_LBFGS_B")]
        public void TheOptimizerFindsTheDutyForATargetTemperature(string method)
        {
            var (fs, heater, product) = Heater();
            Call("optimizer.save", new
            {
                flowsheetId = fs,
                study = JObject.FromObject(new
                {
                    name = "Hit 330 K",
                    type = "Minimization",
                    objective = "Expression",
                    expression = "(T - 330)^2",
                    method,
                    maxIterations = 200,
                    tolerance = 1e-8,
                    variables = new object[]
                    {
                        new { kind = "Independent", name = "Q", @object = heater, property = "PROP_HT_3", unit = "kW", initial = 50.0, lower = 0.0, upper = 300.0 },
                        new { kind = "Dependent", name = "T", @object = product, property = "PROP_MS_0" }
                    }
                })
            });
            var r = Call("optimizer.run", new { flowsheetId = fs, index = 0 });
            Assert.That(r.Value<string>("error"), Is.Null);
            var q = r["variables"].Single(v => v.Value<string>("name") == "Q").Value<double>("current");
            Assert.That(q, Is.EqualTo(125.4).Within(0.5), "kW to lift 1 kg/s of water by 30 K");
            Assert.That(Call("stream.get", new { flowsheetId = fs, id = product }).Value<double>("temperature"), Is.EqualTo(330.0).Within(0.05), "the optimum stays applied");
            Assert.That(r["chart"]["series"][0]["y"].Count(), Is.GreaterThan(3));
            Assert.That(Call("graph.get", new { flowsheetId = fs })["history"].Value<string>("undo"), Is.EqualTo("Optimize Hit 330 K"));
            Call("edit.undo", new { flowsheetId = fs });
            Call("solve.run", new { flowsheetId = fs });
            Assert.That(Call("stream.get", new { flowsheetId = fs, id = product }).Value<double>("temperature"), Is.EqualTo(311.96).Within(0.05), "undo returns to 50 kW");
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void TheOptimizerExplainsWhatIsMissing()
        {
            var (fs, heater, _) = Heater();
            Call("optimizer.save", new
            {
                flowsheetId = fs,
                study = JObject.FromObject(new
                {
                    name = "Incomplete",
                    variables = new object[] { new { kind = "Independent", @object = heater, property = "PROP_HT_3", initial = 50.0 } }
                })
            });
            var ex = Assert.Throws<RpcException>(() => Call("optimizer.run", new { flowsheetId = fs, index = 0 }));
            Assert.That(ex.Message, Does.Contain("dependent variable"));
            Assert.That(Call("optimizer.methods", new { }).Count(), Is.EqualTo(6));
            Call("flowsheet.close", new { flowsheetId = fs });
        }
    }
}
