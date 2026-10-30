using System;
using System.IO;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>Scripts, the spreadsheet and scenario comparison.</summary>
    [NonParallelizable]
    public class ScriptSpreadsheetScenarioTests
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

        /// <summary>Water heated by a 50 kW duty: feed 300 K, 1 bar, 1 kg/s.</summary>
        private (string fs, string heater, string product) Heater()
        {
            var fs = Call("flowsheet.new", new { name = "m5b" }).Value<string>("id");
            Call("flowsheet.addCompounds", new { flowsheetId = fs, names = new[] { "Water" } });
            Call("flowsheet.addPropertyPackage", new { flowsheetId = fs, name = "Steam Tables (IAPWS-IF97)" });
            Call("flowsheet.setUnitSystem", new { flowsheetId = fs, name = "SI" });
            string Add(string type, string tag, double x) => Call("graph.add", new { flowsheetId = fs, type, x, y = 0, tag }).Value<string>("id");
            var feed = Add("MaterialStream", "feed", 0);
            var heater = Add("Heater", "H-1", 100);
            var product = Add("MaterialStream", "product", 200);
            var duty = Add("EnergyStream", "Q", 100);
            Call("stream.specify", new { flowsheetId = fs, id = feed, temperature = 300.0, pressure = 1e5, massFlow = 1.0, moleFractions = new { Water = 1.0 } });
            Call("graph.connect", new { flowsheetId = fs, sourceId = feed, sourcePort = "out-0", targetId = heater, targetPort = "in-0" });
            Call("graph.connect", new { flowsheetId = fs, sourceId = heater, sourcePort = "out-0", targetId = product, targetPort = "in-0" });
            Call("graph.connect", new { flowsheetId = fs, sourceId = duty, sourcePort = "out-0", targetId = heater, targetPort = "energy" });
            Call("object.setField", new { flowsheetId = fs, id = heater, field = "PROP_HT_3", value = 50.0, unit = "kW" });
            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);
            return (fs, heater, product);
        }

        private double Temperature(string fs, string id) => Call("stream.get", new { flowsheetId = fs, id }).Value<double>("temperature");

        [Test]
        public void AScriptChangesTheFlowsheetAndReportsWhatItPrinted()
        {
            var (fs, _, product) = Heater();
            var script = Call("scripts.save", new
            {
                flowsheetId = fs,
                script = JObject.FromObject(new
                {
                    title = "Double the duty",
                    text = "print('doubling')\nH_1.SetPropertyValue('PROP_HT_3', 100.0)\nFlowsheet.RequestCalculation()\nprint('%.2f' % product.GetPropertyValue('PROP_MS_0'))"
                })
            });
            Assert.That(script.Value<string>("interpreter"), Is.EqualTo("IronPython"));
            var run = Call("scripts.run", new { flowsheetId = fs, id = script.Value<string>("id") });
            Assert.That(run.Value<bool>("ok"), Is.True, run.Value<string>("error"));
            var output = run["output"].Values<string>().ToList();
            Assert.That(output.First(), Is.EqualTo("doubling"));
            Assert.That(output.Last(), Is.EqualTo("323.93"));
            Assert.That(Temperature(fs, product), Is.EqualTo(323.93).Within(0.01));
            Assert.That(Call("graph.get", new { flowsheetId = fs })["history"].Value<string>("undo"), Is.EqualTo("Run script Double the duty"));

            var broken = Call("scripts.save", new { flowsheetId = fs, script = JObject.FromObject(new { title = "Broken", text = "x = 1/0" }) });
            var failed = Call("scripts.run", new { flowsheetId = fs, id = broken.Value<string>("id") });
            Assert.That(failed.Value<bool>("ok"), Is.False);
            Assert.That(failed.Value<string>("error"), Does.Contain("ZeroDivisionError"));

            var linked = Call("scripts.save", new
            {
                flowsheetId = fs,
                script = JObject.FromObject(new { id = broken.Value<string>("id"), title = "Broken", text = "pass", linked = true, linkedTo = "Solver", @event = "SolverFinished" })
            });
            Assert.That(linked.Value<string>("event"), Is.EqualTo("SolverFinished"));
            var list = Call("scripts.list", new { flowsheetId = fs });
            Assert.That(list["scripts"].Select(s => s.Value<string>("title")), Is.EqualTo(new[] { "Broken", "Double the duty" }));
            Assert.That(list["events"]["FlowsheetObject"].Values<string>(), Contains.Item("ObjectCalculationFinished"));
            Call("scripts.delete", new { flowsheetId = fs, id = broken.Value<string>("id") });
            Assert.That(Call("scripts.list", new { flowsheetId = fs })["scripts"].Count(), Is.EqualTo(1));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void SpreadsheetFormulasReadAndWriteTheSimulationAndSaveWithIt()
        {
            var (fs, heater, product) = Heater();
            JToken Cell(JToken sheet, string address) => sheet["cells"].Single(c => c.Value<string>("address") == address);
            var sheet = Call("spreadsheet.set", new
            {
                flowsheetId = fs,
                cells = JArray.FromObject(new[]
                {
                    new { address = "A1", input = "5" },
                    new { address = "A2", input = "=A1*2" },
                    new { address = "B1", input = $"=GETPROPVAL(\"{product}\",\"PROP_MS_0\",\"C\")" },
                    new { address = "C1", input = "note" }
                })
            });
            Assert.That(Cell(sheet, "A2").Value<double>("value"), Is.EqualTo(10));
            Assert.That(Cell(sheet, "B1").Value<double>("value"), Is.EqualTo(Temperature(fs, product) - 273.15).Within(1e-9));
            Assert.That(Cell(sheet, "C1").Value<string>("value"), Is.EqualTo("note"));

            // SETPROPVAL writes the duty when the cell is evaluated; the next solve uses it and
            // GETPROPVAL follows the new result.
            sheet = Call("spreadsheet.set", new
            {
                flowsheetId = fs,
                cells = JArray.FromObject(new[] { new { address = "D1", input = $"=SETPROPVAL(\"{heater}\",\"PROP_HT_3\",\"100\",\"kW\")" } })
            });
            Assert.That(Cell(sheet, "D1").Value<string>("display"), Does.StartWith("EXPORT OK"));
            Call("solve.run", new { flowsheetId = fs });
            Assert.That(Temperature(fs, product), Is.EqualTo(323.93).Within(0.01));
            sheet = Call("spreadsheet.get", new { flowsheetId = fs });
            Assert.That(Cell(sheet, "B1").Value<double>("value"), Is.EqualTo(323.93 - 273.15).Within(0.01));

            var bad = Call("spreadsheet.set", new { flowsheetId = fs, cells = JArray.FromObject(new[] { new { address = "E1", input = "=GETPROPVAL(\"nothing\",\"x\",\"K\")" } }) });
            Assert.That(Cell(bad, "E1").Value<bool>("error"), Is.True);
            Assert.Throws<RpcException>(() => Call("spreadsheet.set", new { flowsheetId = fs, cells = JArray.FromObject(new[] { new { address = "ZZ", input = "1" } }) }));

            var added = Call("spreadsheet.addSheet", new { flowsheetId = fs, name = "Notes" });
            Assert.That(added["sheets"].Values<string>(), Is.EqualTo(new[] { "Sheet1", "Notes" }));
            Call("spreadsheet.set", new { flowsheetId = fs, sheet = "Notes", cells = JArray.FromObject(new[] { new { address = "A1", input = "=Sheet1!A2+1" } }) });

            var path = Path.Combine(Path.GetTempPath(), "m5b-" + Guid.NewGuid().ToString("N") + ".dwxml");
            Call("flowsheet.save", new { flowsheetId = fs, path });
            var reopened = Call("flowsheet.open", new { path }).Value<string>("id");
            var back = Call("spreadsheet.get", new { flowsheetId = reopened });
            Assert.That(back["sheets"].Values<string>(), Is.EqualTo(new[] { "Sheet1", "Notes" }));
            Assert.That(Cell(back, "B1").Value<string>("input"), Does.StartWith("=GETPROPVAL"));
            Assert.That(Cell(back, "A2").Value<double>("value"), Is.EqualTo(10));
            Assert.That(Cell(Call("spreadsheet.get", new { flowsheetId = reopened, sheet = "Notes" }), "A1").Value<double>("value"), Is.EqualTo(11));
            Call("flowsheet.close", new { flowsheetId = reopened });
            File.Delete(path);

            Call("spreadsheet.deleteSheet", new { flowsheetId = fs, sheet = "Notes" });
            Assert.Throws<RpcException>(() => Call("spreadsheet.deleteSheet", new { flowsheetId = fs, sheet = "Sheet1" }), "the last sheet stays");
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void TwoFlowsheetsKeepTheirOwnSpreadsheetFunctions()
        {
            var (a, _, productA) = Heater();
            var (b, heaterB, productB) = Heater();
            Call("object.setField", new { flowsheetId = b, id = heaterB, field = "PROP_HT_3", value = 100.0, unit = "kW" });
            Call("solve.run", new { flowsheetId = b });
            // The same object ids would not exist across flowsheets; each reads its own product.
            var sheetA = Call("spreadsheet.set", new { flowsheetId = a, cells = JArray.FromObject(new[] { new { address = "A1", input = $"=GETPROPVAL(\"{productA}\",\"PROP_MS_0\",\"K\")" } }) });
            var sheetB = Call("spreadsheet.set", new { flowsheetId = b, cells = JArray.FromObject(new[] { new { address = "A1", input = $"=GETPROPVAL(\"{productB}\",\"PROP_MS_0\",\"K\")" } }) });
            Call("solve.run", new { flowsheetId = a });
            Assert.That(Call("spreadsheet.get", new { flowsheetId = a })["cells"][0].Value<double>("value"), Is.EqualTo(311.96).Within(0.01));
            Assert.That(sheetB["cells"][0].Value<double>("value"), Is.EqualTo(323.93).Within(0.01));
            Call("flowsheet.close", new { flowsheetId = a });
            Call("flowsheet.close", new { flowsheetId = b });
        }

        [Test]
        public void AScenarioComparisonPutsTheChangedSpecificationFirst()
        {
            var (fs, heater, _) = Heater();
            Assert.Throws<RpcException>(() => Call("scenarios.compare", new { flowsheetId = fs }));
            Call("scenarios.capture", new { flowsheetId = fs, slot = "A", label = "50 kW" });
            Call("object.setField", new { flowsheetId = fs, id = heater, field = "PROP_HT_3", value = 100.0, unit = "kW" });
            Call("solve.run", new { flowsheetId = fs });
            var state = Call("scenarios.capture", new { flowsheetId = fs, slot = "B", label = "100 kW" });
            Assert.That(state["B"].Value<string>("label"), Is.EqualTo("100 kW"));
            var r = Call("scenarios.compare", new { flowsheetId = fs });
            Assert.That(r.Value<int>("inputsChanged"), Is.EqualTo(1));
            Assert.That(r.Value<int>("outputsChanged"), Is.GreaterThan(5));
            var first = r["rows"][0];
            Assert.That(first.Value<string>("object"), Is.EqualTo("H-1"));
            Assert.That(first.Value<bool>("input"), Is.True);
            Assert.That(first.Value<double>("a"), Is.EqualTo(50.0));
            Assert.That(first.Value<double>("b"), Is.EqualTo(100.0));
            var temperature = r["rows"].First(x => x.Value<string>("object") == "product" && x.Value<string>("property") == "Temperature");
            Assert.That(temperature.Value<double>("delta"), Is.EqualTo(11.96).Within(0.01));
            Assert.That(r["summary"][0].Value<string>(), Does.Contain("Heat Added"));
            Call("scenarios.clear", new { flowsheetId = fs });
            Assert.That(Call("scenarios.get", new { flowsheetId = fs })["A"].Type, Is.EqualTo(JTokenType.Null));
            Call("flowsheet.close", new { flowsheetId = fs });
        }
    }
}
