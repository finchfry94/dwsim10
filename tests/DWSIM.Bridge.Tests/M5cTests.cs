using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>The compound creator, petroleum characterization and data regression.</summary>
    [NonParallelizable]
    public class M5cTests
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
            var fs = Call("flowsheet.new", new { name = "m5c" }).Value<string>("id");
            Call("flowsheet.addCompounds", new { flowsheetId = fs, names = compounds });
            Call("flowsheet.addPropertyPackage", new { flowsheetId = fs, name = package });
            Call("flowsheet.setUnitSystem", new { flowsheetId = fs, name = "SI" });
            return fs;
        }

        private static double Row(JToken estimate, string property)
            => double.Parse(estimate["properties"].Single(r => r.Value<string>("property") == property).Value<string>("value"),
                System.Globalization.CultureInfo.InvariantCulture);

        [Test]
        public void AStructureIsEstimatedByJobackWithEveryGroupCounted()
        {
            var fs = NewFlowsheet("Peng-Robinson (PR)", "Water");
            var ethanol = Call("compounds.estimate", new { flowsheetId = fs, compound = JObject.FromObject(new { name = "My ethanol", smiles = "CCO" }) });
            Assert.That(ethanol.Value<string>("formula"), Is.EqualTo("C2H6O"));
            Assert.That(Row(ethanol, "Molecular Weight"), Is.EqualTo(46.07).Within(0.01));
            Assert.That(ethanol["groups"].ToObject<System.Collections.Generic.Dictionary<string, int>>(),
                Is.EquivalentTo(new System.Collections.Generic.Dictionary<string, int> { ["-CH3"] = 1, ["-CH2-"] = 1, ["-OH (alcohol)"] = 1 }));
            // Joback: Tb = 198.2 + 23.58 + 22.88 + 92.88. Before the fragmenter's "-CH2-" was mapped
            // to the table's ">CH2", the methylene was left out and Tb came out 23 K low.
            Assert.That(Row(ethanol, "Normal Boiling Point"), Is.EqualTo(337.54).Within(0.01));
            Assert.That(ethanol.Value<bool>("usable"), Is.True);

            var hexane = Call("compounds.estimate", new { flowsheetId = fs, compound = JObject.FromObject(new { name = "My hexane", smiles = "CCCCCC" }) });
            Assert.That(Row(hexane, "Critical Temperature"), Is.EqualTo(507.6).Within(507.6 * 0.02), "Joback's usual accuracy");
            Assert.That(Row(hexane, "Critical Pressure"), Is.EqualTo(3.025e6).Within(3.025e6 * 0.06));

            // A ring group the Joback table lacks: no partial estimate; the constants are needed.
            var cyclohexane = Call("compounds.estimate", new { flowsheetId = fs, compound = JObject.FromObject(new { name = "Ring", smiles = "C1CCCCC1" }) });
            Assert.That(cyclohexane["unsupportedGroups"].Values<string>(), Is.EqualTo(new[] { "ring-CH2-" }));
            Assert.That(cyclohexane.Value<bool>("usable"), Is.False);
            var ex = Assert.Throws<RpcException>(() => Call("compounds.create", new { flowsheetId = fs, compound = JObject.FromObject(new { name = "Ring", smiles = "C1CCCCC1" }) }));
            Assert.That(ex.Message, Does.Contain("Not enough is known"));
            Assert.Throws<RpcException>(() => Call("compounds.estimate", new { flowsheetId = fs, compound = JObject.FromObject(new { name = "Bad", smiles = "C((" }) }));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void ACreatedCompoundJoinsTheFlowsheetAndItsStreams()
        {
            var fs = NewFlowsheet("Peng-Robinson (PR)", "Water");
            var s = Call("graph.add", new { flowsheetId = fs, type = "MaterialStream", x = 0, y = 0, tag = "s" }).Value<string>("id");

            // Given constants win over estimates, in the units they are given in.
            var created = Call("compounds.create", new
            {
                flowsheetId = fs,
                compound = JObject.FromObject(new { name = "Cyclohexane (mine)", smiles = "C1CCCCC1", tc = 280.45, pc = 40.75, omega = 0.2096, tb = 80.7 }),
                units = JObject.FromObject(new { tc = "C", pc = "bar", tb = "C" })
            });
            Assert.That(created["summary"]["compounds"].Values<string>(), Contains.Item("Cyclohexane (mine)"));
            Assert.That(Row(created, "Critical Temperature"), Is.EqualTo(553.6).Within(0.01));
            Assert.That(Row(created, "Critical Pressure"), Is.EqualTo(4.075e6).Within(1));
            Assert.That(created["properties"].Single(r => r.Value<string>("property") == "Critical Temperature").Value<string>("origin"), Does.Contain("User"));

            Call("stream.specify", new { flowsheetId = fs, id = s, temperature = 300.0, pressure = 1e5, molarFlow = 1.0, moleFractions = JObject.FromObject(new System.Collections.Generic.Dictionary<string, double> { ["Water"] = 0.5, ["Cyclohexane (mine)"] = 0.5 }) });
            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);

            var dup = Assert.Throws<RpcException>(() => Call("compounds.create", new { flowsheetId = fs, compound = JObject.FromObject(new { name = "Cyclohexane (mine)", smiles = "CCO" }) }));
            Assert.That(dup.Message, Does.Contain("already"));
            Call("edit.undo", new { flowsheetId = fs }); // the stream's specification
            Call("edit.undo", new { flowsheetId = fs }); // the compound
            Assert.That(Call("flowsheet.summary", new { flowsheetId = fs })["compounds"].Values<string>(), Does.Not.Contain("Cyclohexane (mine)"));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void ABulkPlusFractionBecomesPseudocompoundsAndAStreamThatSolves()
        {
            var fs = NewFlowsheet("Peng-Robinson (PR)", "Methane");
            var r = Call("petroleum.bulk", new
            {
                flowsheetId = fs,
                assay = JObject.FromObject(new
                {
                    name = "C7P",
                    molarWeight = 200.0,
                    specificGravity = 0.83,
                    count = 6,
                    lightEnds = new[] { new { compound = "Methane", percent = 30.0 }, new { compound = "Propane", percent = 10.0 } },
                    lightEndsBasis = "Mole"
                })
            });
            var pseudos = r["compounds"].ToList();
            Assert.That(pseudos, Has.Count.EqualTo(6));
            var x = pseudos.Sum(c => c.Value<double>("moleFraction"));
            var mw = pseudos.Sum(c => c.Value<double>("moleFraction") * c.Value<double>("molarWeight")) / x;
            Assert.That(mw, Is.EqualTo(200.0).Within(2.0), "the cuts average the given molar weight");
            Assert.That(x, Is.EqualTo(0.6).Within(1e-6), "what the light ends leave");
            Assert.That(r["lightEnds"].Value<double>("Methane"), Is.EqualTo(0.3).Within(1e-9));
            var nbp = pseudos.Select(c => c.Value<double>("boilingPoint")).ToList();
            Assert.That(nbp, Is.Ordered.Ascending);
            Assert.That(r["summary"]["compounds"].Values<string>(), Contains.Item("Propane"), "a light end not yet in the flowsheet joins it");

            Call("stream.specify", new { flowsheetId = fs, id = r.Value<string>("stream"), temperature = 350.0, pressure = 2e5, massFlow = 1.0 });
            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);
            var vf = Call("stream.get", new { flowsheetId = fs, id = r.Value<string>("stream") }).Value<double>("vaporFraction");
            Assert.That(vf, Is.InRange(0.3, 0.5), "about the light ends' share flashes off at 350 K and 2 bar");

            Assert.Throws<RpcException>(() => Call("petroleum.bulk", new { flowsheetId = fs, assay = JObject.FromObject(new { name = "C7P", molarWeight = 150.0 }) }));
            Assert.Throws<RpcException>(() => Call("petroleum.bulk", new { flowsheetId = fs, assay = JObject.FromObject(new { name = "Y" }) }));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void ADistillationCurveBecomesCutsAcrossItsRange()
        {
            var fs = NewFlowsheet("Peng-Robinson (PR)", "Methane");
            var curve = new[] { (0, 30.0), (10, 120.0), (30, 220.0), (50, 300.0), (70, 390.0), (90, 520.0), (100, 600.0) };
            var r = Call("petroleum.curve", new
            {
                flowsheetId = fs,
                assay = JObject.FromObject(new
                {
                    name = "CRUDE",
                    curveType = "TBP",
                    curveBasis = "LiquidVolume",
                    count = 8,
                    temperatureUnit = "C",
                    specificGravity = 0.85,
                    points = curve.Select(p => new { percent = (double)p.Item1, temperature = p.Item2 })
                })
            });
            var cuts = r["compounds"].ToList();
            Assert.That(cuts, Has.Count.EqualTo(8));
            Assert.That(cuts.Sum(c => c.Value<double>("moleFraction")), Is.EqualTo(1.0).Within(1e-6));
            var nbp = cuts.Select(c => c.Value<double>("boilingPoint")).ToList();
            Assert.That(nbp, Is.Ordered.Ascending);
            Assert.That(nbp.First(), Is.InRange(303.15, 450.0), "the first cut sits low on the curve");
            Assert.That(nbp.Last(), Is.InRange(750.0, 873.15), "the last near its end");

            var ex = Assert.Throws<RpcException>(() => Call("petroleum.curve", new
            {
                flowsheetId = fs,
                assay = JObject.FromObject(new { name = "BAD", temperatureUnit = "C", points = new[] { new { percent = 0.0, temperature = 300.0 }, new { percent = 50.0, temperature = 200.0 } } })
            }));
            Assert.That(ex.Message, Does.Contain("rise"));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void ARegressionRecoversTheKijItsDataCameFromAndAppliesIt()
        {
            var fs = NewFlowsheet("Peng-Robinson (PR)", "Carbon dioxide", "Propane");
            var package = Call("flowsheet.summary", new { flowsheetId = fs })["propertyPackages"][0].Value<string>("id");
            var s = Call("graph.add", new { flowsheetId = fs, type = "MaterialStream", x = 0, y = 0, tag = "s" }).Value<string>("id");
            double BubblePressure(double x1)
            {
                Call("stream.specify", new
                {
                    flowsheetId = fs, id = s, temperature = 270.0, vaporFraction = 0.0, molarFlow = 1.0,
                    moleFractions = JObject.FromObject(new System.Collections.Generic.Dictionary<string, double> { ["Carbon dioxide"] = x1, ["Propane"] = 1 - x1 })
                });
                Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);
                return Call("stream.get", new { flowsheetId = fs, id = s }).Value<double>("pressure");
            }

            // Bubble pressures at 270 K made with kij = 0.12: the data the regression gets.
            Call("regression.apply", new { flowsheetId = fs, packageId = package, comp1 = "Carbon dioxide", comp2 = "Propane", parameters = JObject.FromObject(new { kij = 0.12 }) });
            var xs = new[] { 0.1, 0.25, 0.4, 0.55, 0.7, 0.85 };
            var points = xs.Select(x => new { t = 270.0 - 273.15, p = BubblePressure(x) / 1e5, x1 = x, y1 = 0.0 }).ToList();
            var withKij = points[2].p;
            Call("regression.apply", new { flowsheetId = fs, packageId = package, comp1 = "Carbon dioxide", comp2 = "Propane", parameters = JObject.FromObject(new { kij = 0.0 }) });
            Assert.That(BubblePressure(0.4) / 1e5, Is.Not.EqualTo(withKij).Within(0.1), "kij moves the bubble pressure");

            var r = Call("regression.run", new
            {
                flowsheetId = fs,
                study = JObject.FromObject(new { comp1 = "Carbon dioxide", comp2 = "Propane", model = "Peng-Robinson", dataType = "Pxy", method = "IPOPT", points })
            });
            Assert.That(r.Value<string>("error"), Is.Null);
            var kij = r["parameters"].Value<double>("kij");
            Assert.That(kij, Is.EqualTo(0.12).Within(1e-3));
            Assert.That(r["objective"].Type, Is.Not.EqualTo(JTokenType.Null));
            var table = r["table"];
            Assert.That(table["rows"].Count(), Is.EqualTo(6));
            var dp = table["columns"].Values<string>().ToList().IndexOf("ΔP %");
            Assert.That(dp, Is.GreaterThanOrEqualTo(0));
            Assert.That(table["rows"].Select(row => System.Math.Abs(row[dp].Value<double>())), Is.All.LessThan(0.01), "the model meets every bubble pressure");
            Assert.That(r["chart"]["series"].Any(x => x.Value<string>("kind") == "point"), Is.True, "the data shows as points");

            Call("regression.apply", new { flowsheetId = fs, packageId = package, comp1 = "Carbon dioxide", comp2 = "Propane", parameters = r["parameters"] });
            Assert.That(BubblePressure(0.4) / 1e5, Is.EqualTo(withKij).Within(0.01), "the fitted kij reproduces the data");

            var models = Call("regression.models", new { });
            Assert.That(models["models"].Select(m => m.Value<string>("name")), Is.SupersetOf(new[] { "Peng-Robinson", "NRTL", "UNIQUAC" }));
            Assert.Throws<RpcException>(() => Call("regression.run", new { flowsheetId = fs, study = JObject.FromObject(new { comp1 = "Propane", comp2 = "Propane", points }) }));
            Call("flowsheet.close", new { flowsheetId = fs });
        }
    }
}
