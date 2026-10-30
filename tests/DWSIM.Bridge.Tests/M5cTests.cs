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
    }
}
