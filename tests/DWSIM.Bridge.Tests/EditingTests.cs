using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>Editing comfort: undo and redo, copy and paste, batch moves and deletes, rotation.</summary>
    [NonParallelizable]
    public class EditingTests
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

        /// <summary>Water through a heater: in -> HT -> out, the feed at 25 °C, 1 kg/s.</summary>
        private (string fs, string feed, string heater, string product) HeaterFlowsheet()
        {
            var fs = Call("flowsheet.new", new { name = "editing" }).Value<string>("id");
            Call("flowsheet.addCompounds", new { flowsheetId = fs, names = new[] { "Water" } });
            Call("flowsheet.addPropertyPackage", new { flowsheetId = fs, name = "Steam Tables (IAPWS-IF97)" });
            Call("flowsheet.setUnitSystem", new { flowsheetId = fs, name = "SI" });
            string Add(string type, string tag, double x) => Call("graph.add", new { flowsheetId = fs, type, x, y = 100.0, tag }).Value<string>("id");
            var feed = Add("MaterialStream", "in", 0);
            var heater = Add("Heater", "HT", 100);
            var product = Add("MaterialStream", "out", 200);
            Call("stream.specify", new
            {
                flowsheetId = fs, id = feed, temperature = 298.15, pressure = 101325.0, massFlow = 1.0,
                moleFractions = new Dictionary<string, double> { ["Water"] = 1.0 }
            });
            Call("graph.connect", new { flowsheetId = fs, sourceId = feed, sourcePort = "out-0", targetId = heater, targetPort = "in-0" });
            Call("graph.connect", new { flowsheetId = fs, sourceId = heater, sourcePort = "out-0", targetId = product, targetPort = "in-0" });
            Call("object.setField", new { flowsheetId = fs, id = heater, field = "PROP_HT_3", value = 100.0, unit = "kW" });
            return (fs, feed, heater, product);
        }

        private static JToken History(JToken graph) => graph["history"];

        private double Duty(string fs, string heater)
            => Call("object.schema", new { flowsheetId = fs, id = heater })["sections"].SelectMany(s => s["fields"])
                .First(f => f.Value<string>("id") == "PROP_HT_3").Value<double>("value");

        [Test]
        public void UndoBringsADeletedObjectBackWithItsConnectionsAndRedoTakesItAway()
        {
            var (fs, _, heater, product) = HeaterFlowsheet();
            var before = Call("graph.get", new { flowsheetId = fs });
            Assert.That(History(before).Value<bool>("canUndo"), Is.True);
            Assert.That(History(before).Value<bool>("modified"), Is.True);

            var after = Call("graph.delete", new { flowsheetId = fs, id = heater });
            Assert.That(after["nodes"].Count(), Is.EqualTo(2));
            Assert.That(after["edges"], Is.Empty);
            Assert.That(History(after).Value<string>("undo"), Is.EqualTo("Delete HT"));

            var undone = Call("edit.undo", new { flowsheetId = fs });
            Assert.That(undone.Value<string>("done"), Is.EqualTo("Delete HT"));
            Assert.That(undone["nodes"].Select(n => n.Value<string>("id")), Does.Contain(heater), "the same id comes back");
            Assert.That(undone["edges"].Count(), Is.EqualTo(before["edges"].Count()));
            Assert.That(Duty(fs, heater), Is.EqualTo(100.0).Within(1e-9), "with its settings");
            Assert.That(History(undone).Value<string>("redo"), Is.EqualTo("Delete HT"));

            // The restored flowsheet solves like the original.
            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);
            var t = Call("stream.get", new { flowsheetId = fs, id = product }).Value<double>("temperature");
            Assert.That(t, Is.GreaterThan(298.15 + 20));

            var redone = Call("edit.redo", new { flowsheetId = fs });
            Assert.That(redone["nodes"].Select(n => n.Value<string>("id")), Does.Not.Contain(heater));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void UndoStepsBackThroughFieldEditsAndARefusedEditLeavesNoStep()
        {
            var (fs, _, heater, _) = HeaterFlowsheet();
            Call("object.setField", new { flowsheetId = fs, id = heater, field = "PROP_HT_3", value = 250.0, unit = "kW" });
            var steps = History(Call("graph.get", new { flowsheetId = fs })).Value<string>("undo");

            Assert.Throws<RpcException>(() => Call("object.setField", new { flowsheetId = fs, id = heater, field = "NoSuchField", value = 1.0 }));
            Assert.That(History(Call("graph.get", new { flowsheetId = fs })).Value<string>("undo"), Is.EqualTo(steps));

            Call("edit.undo", new { flowsheetId = fs });
            Assert.That(Duty(fs, heater), Is.EqualTo(100.0).Within(1e-9));
            Call("edit.redo", new { flowsheetId = fs });
            Assert.That(Duty(fs, heater), Is.EqualTo(250.0).Within(1e-9));
            Assert.Throws<RpcException>(() => Call("edit.redo", new { flowsheetId = fs }), "nothing left to redo");
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void SavingClearsTheUnsavedFlag()
        {
            var (fs, _, _, _) = HeaterFlowsheet();
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dwsim-editing-" + System.Guid.NewGuid().ToString("N") + ".dwxml");
            try
            {
                Call("flowsheet.save", new { flowsheetId = fs, path });
                Assert.That(History(Call("graph.get", new { flowsheetId = fs })).Value<bool>("modified"), Is.False);
                var heater = Call("graph.get", new { flowsheetId = fs })["nodes"].First(n => n.Value<string>("tag") == "HT").Value<string>("id");
                Call("graph.move", new { flowsheetId = fs, id = heater, x = 120.0, y = 100.0 });
                Assert.That(History(Call("graph.get", new { flowsheetId = fs })).Value<bool>("modified"), Is.True);
            }
            finally
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                Call("flowsheet.close", new { flowsheetId = fs });
            }
        }

        [Test]
        public void PastingCopiesKeepsTheirConnectionsAndTheFlowsheetsPropertyPackage()
        {
            var (fs, feed, heater, _) = HeaterFlowsheet();
            var packages = Call("flowsheet.summary", new { flowsheetId = fs })["propertyPackages"].Count();
            var copied = Call("graph.copy", new { flowsheetId = fs, ids = new[] { feed, heater } });
            Assert.That(copied.Value<int>("count"), Is.EqualTo(2));

            var graph = Call("graph.paste", new { flowsheetId = fs, text = copied.Value<string>("text") });
            var pasted = graph["pasted"].Select(p => p.Value<string>()).ToList();
            Assert.That(pasted, Has.Count.EqualTo(2));
            Assert.That(graph["nodes"].Count(), Is.EqualTo(5));
            // in -> HT came along; the copy of HT has no outlet, as only two objects were copied
            Assert.That(graph["edges"].Count(e => pasted.Contains(e.Value<string>("source")) && pasted.Contains(e.Value<string>("target"))), Is.EqualTo(1));
            Assert.That(Call("flowsheet.summary", new { flowsheetId = fs })["propertyPackages"].Count(), Is.EqualTo(packages), "no package copies");
            var copyOfHeater = pasted.First(id => graph["nodes"].First(n => n.Value<string>("id") == id).Value<string>("type") == "Heater");
            Assert.That(Duty(fs, copyOfHeater), Is.EqualTo(100.0).Within(1e-9), "with the original's settings");

            Call("edit.undo", new { flowsheetId = fs });
            Assert.That(Call("graph.get", new { flowsheetId = fs })["nodes"].Count(), Is.EqualTo(3));
            Assert.Throws<RpcException>(() => Call("graph.paste", new { flowsheetId = fs, text = "not a flowsheet" }));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void BatchMovesDeletesAndTurnsAreOneStepEach()
        {
            var (fs, feed, heater, product) = HeaterFlowsheet();
            var graph = Call("graph.moveMany", new
            {
                flowsheetId = fs,
                moves = new[] { new { id = feed, x = 10.0, y = 300.0 }, new { id = product, x = 210.0, y = 300.0 } }
            });
            Assert.That(graph["nodes"].First(n => n.Value<string>("id") == feed).Value<double>("y"), Is.EqualTo(300.0));
            Assert.That(History(graph).Value<string>("undo"), Is.EqualTo("Move 2 objects"));

            graph = Call("graph.transform", new { flowsheetId = fs, ids = new[] { heater }, rotate = 270, flipH = true });
            var turned = graph["nodes"].First(n => n.Value<string>("id") == heater);
            Assert.That(turned.Value<int>("rotation"), Is.EqualTo(270));
            Assert.That(turned.Value<bool>("flippedH"), Is.True);
            Assert.Throws<RpcException>(() => Call("graph.transform", new { flowsheetId = fs, ids = new[] { heater }, rotate = 45 }));

            graph = Call("graph.deleteMany", new { flowsheetId = fs, ids = new[] { feed, product } });
            Assert.That(graph["nodes"].Count(), Is.EqualTo(1));
            graph = Call("edit.undo", new { flowsheetId = fs });
            Assert.That(graph["nodes"].Count(), Is.EqualTo(3));
            Assert.That(graph["edges"].Count(), Is.EqualTo(2));
            graph = Call("edit.undo", new { flowsheetId = fs });
            Assert.That(graph["nodes"].First(n => n.Value<string>("id") == heater).Value<int>("rotation"), Is.EqualTo(0));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void ConnectingTwoUnitOperationsPutsAStreamBetweenThem()
        {
            var (fs, _, heater, product) = HeaterFlowsheet();
            Call("graph.delete", new { flowsheetId = fs, id = product });
            var pump = Call("graph.add", new { flowsheetId = fs, type = "Pump", x = 300.0, y = 100.0 }).Value<string>("id");
            var graph = Call("graph.connect", new { flowsheetId = fs, sourceId = heater, sourcePort = "out-0", targetId = pump, targetPort = "in-0" });

            var inserted = graph["nodes"].Where(n => n.Value<string>("type") == "MaterialStream")
                .Select(n => n.Value<string>("id"))
                .Single(id => graph["edges"].Any(e => e.Value<string>("source") == heater && e.Value<string>("target") == id));
            Assert.That(graph["edges"].Any(e => e.Value<string>("source") == inserted && e.Value<string>("target") == pump), Is.True);
            Assert.That(History(graph).Value<string>("undo"), Does.StartWith("Connect"));

            graph = Call("edit.undo", new { flowsheetId = fs });
            Assert.That(graph["nodes"].Select(n => n.Value<string>("id")), Does.Not.Contain(inserted), "one step takes the stream away too");
            Call("flowsheet.close", new { flowsheetId = fs });
        }
    }
}
