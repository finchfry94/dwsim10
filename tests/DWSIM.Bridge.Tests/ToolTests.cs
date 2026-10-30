using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using DWSIM.Thermodynamics.Streams;
using DWSIM.Thermodynamics.Utilities.Hydrates;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>The engineering tools, the pipe profile and the Excel export.</summary>
    [NonParallelizable]
    public class ToolTests
    {
        private sealed class Silent : INotifier
        {
            public void Notify(string method, JToken parameters) { }
        }

        private RpcRegistry _registry;
        private SessionStore _sessions;

        [OneTimeSetUp]
        public void StartEngine()
        {
            _registry = new RpcRegistry();
            _sessions = new SessionStore();
            BridgeHost.Register(_registry, _sessions, new Silent());
            BridgeHost.WarmUp();
        }

        private JToken Call(string method, object args)
            => _registry.Invoke(method, JObject.FromObject(args), CancellationToken.None);

        private static string Sample(string file)
        {
            var folder = TestContext.CurrentContext.TestDirectory;
            while (folder != null && !Directory.Exists(Path.Combine(folder, "tests", "flowsheets")))
                folder = Path.GetDirectoryName(folder);
            if (folder == null) Assert.Ignore("tests/flowsheets not found");
            return Path.Combine(folder, "tests", "flowsheets", file);
        }

        /// <summary>A new SI flowsheet with Peng-Robinson and the given compounds.</summary>
        private string NewFlowsheet(params string[] compounds)
        {
            var fs = Call("flowsheet.new", new { name = "tools" }).Value<string>("id");
            Call("flowsheet.addCompounds", new { flowsheetId = fs, names = compounds });
            Call("flowsheet.addPropertyPackage", new { flowsheetId = fs, name = "Peng-Robinson (PR)" });
            Call("flowsheet.setUnitSystem", new { flowsheetId = fs, name = "SI" });
            return fs;
        }

        private string Stream(string fs, string tag, double t, double p, object fractions, double x = 0)
        {
            var id = Call("graph.add", new { flowsheetId = fs, type = "MaterialStream", x, y = 0, tag }).Value<string>("id");
            Call("stream.specify", new { flowsheetId = fs, id, temperature = t, pressure = p, molarFlow = 1.0, moleFractions = fractions });
            return id;
        }

        private JToken Run(string fs, string tool, object inputs)
            => Call("tools.run", new { flowsheetId = fs, tool, inputs = JObject.FromObject(inputs) });

        private static Dictionary<string, JToken> Fields(JToken result, string section)
            => result["sections"].First(s => s.Value<string>("title") == section)["fields"]
                .GroupBy(f => f.Value<string>("label")).ToDictionary(g => g.Key, g => g.First()["value"]);

        [Test]
        public void EveryToolIsListedWithItsInputs()
        {
            var fs = NewFlowsheet("Methane", "Water");
            var tools = Call("tools.list", new { flowsheetId = fs }).ToList();
            Assert.That(tools.Select(t => t.Value<string>("id")), Is.EquivalentTo(new[]
                { "hydrates", "psv", "separator", "depressurization", "criticalPoint", "distillationCurves", "mccabeThiele" }));
            foreach (var t in tools)
            {
                Assert.That(t.Value<string>("title"), Is.Not.Empty);
                Assert.That(t.Value<string>("group"), Is.Not.Empty);
                Assert.That(t["inputs"].All(i => i["id"] != null && i["kind"] != null), Is.True, t.Value<string>("id"));
            }
            var ex = Assert.Throws<RpcException>(() => Run(fs, "nope", new { }));
            Assert.That(ex.Code, Is.EqualTo(RpcErrorCodes.InvalidParams));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void HydratesMatchTheEngineAndFormInTheExpectedRange()
        {
            var fs = NewFlowsheet("Methane", "Water");
            var gas = Stream(fs, "gas", 277.15, 10e6, new { Methane = 0.95, Water = 0.05 });
            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);

            var r = Run(fs, "hydrates", new { stream = gas, model = "0" });
            var atT = Fields(r, "At the stream temperature");
            var atP = Fields(r, "At the stream pressure");
            var pf = atT["Formation pressure"].Value<double>();
            Assert.That(pf, Is.InRange(3e6, 6e6), "methane hydrate at 4 °C forms at about 4 MPa");
            Assert.That(atP["Formation temperature"].Value<double>(), Is.InRange(280.0, 290.0));
            Assert.That(Fields(r, "Verdict")["Hydrates form"].Value<string>(), Is.EqualTo("Yes"), "10 MPa is above the formation pressure");

            // The same numbers as calling the engine's calculator directly.
            var ms = (MaterialStream)_sessions.Get(fs).Inner.SimulationObjects[gas];
            var direct = HydrateCalculator.Calculate(ms, HydrateModel.VanDerWaalsPlatteeuw, false);
            Assert.That(pf, Is.EqualTo(direct.FormationPressure).Within(1e-6 * direct.FormationPressure));
            Assert.That(atP["Formation temperature"].Value<double>(), Is.EqualTo(direct.FormationTemperature).Within(1e-6));

            var dry = NewFlowsheet("Methane", "Ethane");
            var s = Stream(dry, "dry", 300, 5e6, new { Methane = 0.9, Ethane = 0.1 });
            var ex = Assert.Throws<RpcException>(() => Run(dry, "hydrates", new { stream = s }));
            Assert.That(ex.Message, Does.Contain("no water"));
            Call("flowsheet.close", new { flowsheetId = fs });
            Call("flowsheet.close", new { flowsheetId = dry });
        }

        [Test]
        public void ThePsvToolSizesAnOrificeAndRefusesAnUnsolvedInlet()
        {
            var fs = NewFlowsheet("Methane", "Ethane");
            var gas = Stream(fs, "gas", 300, 5e6, new { Methane = 0.9, Ethane = 0.1 });
            var v = Call("graph.add", new { flowsheetId = fs, type = "Valve", x = 100, y = 0, tag = "PSV" }).Value<string>("id");
            var o = Call("graph.add", new { flowsheetId = fs, type = "MaterialStream", x = 200, y = 0, tag = "out" }).Value<string>("id");
            Call("graph.connect", new { flowsheetId = fs, sourceId = gas, sourcePort = "out-0", targetId = v, targetPort = "in-0" });
            Call("graph.connect", new { flowsheetId = fs, sourceId = v, sourcePort = "out-0", targetId = o, targetPort = "in-0" });

            var ex = Assert.Throws<RpcException>(() => Run(fs, "psv", new { valve = v, fluid = "1" }));
            Assert.That(ex.Message, Does.Contain("not calculated"));

            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);
            var r = Run(fs, "psv", new { valve = v, fluid = "1" });
            var area = r["sections"].SelectMany(s => s["fields"]).First(f => f.Value<string>("label") == "Required area");
            Assert.That(area.Value<double>("value"), Is.GreaterThan(0));
            Assert.That(area.Value<string>("unit"), Is.EqualTo("in2"));
            Assert.That(r["sections"].SelectMany(s => s["fields"]).First(f => f.Value<string>("label") == "API designation").Value<string>("value"),
                Does.Match("^[D-T]$|larger"));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void TheSeparatorToolSizesTheSampleVessel()
        {
            var fs = Call("flowsheet.open", new { path = Sample("GasLiquidSeparatorSizing.dwxmz") }).Value<string>("id");
            Call("solve.run", new { flowsheetId = fs });
            var tool = Call("tools.list", new { flowsheetId = fs }).First(t => t.Value<string>("id") == "separator");
            var vessel = tool["inputs"][0]["options"][0].Value<string>("value");
            var vertical = Run(fs, "separator", new { vessel });
            var horizontal = Run(fs, "separator", new { vessel, orientation = "horizontal" });
            double Diameter(JToken r) => r["sections"].SelectMany(s => s["fields"]).First(f => f.Value<string>("label") == "Minimum diameter").Value<double>("value");
            Assert.That(Diameter(vertical), Is.InRange(300.0, 3000.0), "mm");
            Assert.That(Diameter(horizontal), Is.InRange(300.0, 3000.0), "mm");
            Assert.That(Diameter(horizontal), Is.Not.EqualTo(Diameter(vertical)).Within(1e-6));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void TheCriticalPointLiesBetweenThePureComponents()
        {
            var fs = NewFlowsheet("Methane", "Ethane", "Propane");
            var gas = Stream(fs, "gas", 300, 5e6, new { Methane = 0.8, Ethane = 0.15, Propane = 0.05 });
            Call("solve.run", new { flowsheetId = fs });
            var cp = Fields(Run(fs, "criticalPoint", new { stream = gas }), "Critical point");
            Assert.That(cp["Temperature"].Value<double>(), Is.InRange(190.6, 305.3), "between methane's and ethane's Tc");
            Assert.That(cp["Pressure"].Value<double>(), Is.InRange(5e6, 12e6));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void McCabeThieleStepsOffAReasonableBenzeneTolueneColumn()
        {
            var fs = NewFlowsheet("Benzene", "Toluene");
            var r = Run(fs, "mccabeThiele", new { light = "Benzene", heavy = "Toluene", xf = 0.5, xd = 0.95, xb = 0.05, reflux = 2.0 });
            var f = r["sections"].SelectMany(s => s["fields"]).ToDictionary(x => x.Value<string>("label"), x => x["value"]);
            var rmin = f["Minimum reflux Rmin"].Value<double>();
            var nmin = f["Minimum stages (total reflux)"].Value<double>();
            var n = f["Theoretical stages"].Value<double>();
            Assert.That(rmin, Is.InRange(0.8, 1.6), "relative volatility about 2.4");
            Assert.That(nmin, Is.InRange(5.0, 9.0));
            Assert.That(n, Is.GreaterThan(nmin));
            Assert.That(f["Feed stage (from the top)"].Value<double>(), Is.InRange(1.0, n));
            Assert.That(r["charts"].Count(), Is.GreaterThanOrEqualTo(1));
            Assert.That(r["charts"][0]["series"].Any(s => s.Value<string>("name").Contains("quilibrium")), Is.True);
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void DepressurizationLowersThePressureAndCoolsTheGas()
        {
            var fs = NewFlowsheet("Methane", "Ethane", "Propane");
            var gas = Stream(fs, "gas", 300, 5e6, new { Methane = 0.8, Ethane = 0.15, Propane = 0.05 });
            Call("solve.run", new { flowsheetId = fs });
            var r = Run(fs, "depressurization", new { stream = gas, duration = 120.0, step = 2.0, pressure = 5e6, temperature = 300.0 });
            var p = r["charts"].First(c => c.Value<string>("title") == "Pressure")["series"][0]["y"].Values<double>().ToList();
            Assert.That(p.First(), Is.EqualTo(5e6).Within(1e3));
            Assert.That(p.Last(), Is.LessThan(p.First() * 0.8));
            var t = r["charts"].First(c => c.Value<string>("title") == "Temperatures")["series"][0]["y"].Values<double>();
            Assert.That(t.Min(), Is.LessThan(300.0));

            var ex = Assert.Throws<RpcException>(() => Run(fs, "depressurization", new { stream = gas, duration = 1e5, step = 1.0 }));
            Assert.That(ex.Message, Does.Contain("20,000"));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void DistillationCurvesRiseWithThePercentDistilled()
        {
            var fs = Call("flowsheet.open", new { path = Sample("PetroleumDistillation.dwxml") }).Value<string>("id");
            Call("solve.run", new { flowsheetId = fs });
            var stream = Call("graph.get", new { flowsheetId = fs })["nodes"].First(n => n.Value<string>("type") == "MaterialStream").Value<string>("id");
            var r = Run(fs, "distillationCurves", new { stream, percents = "0, 10, 30, 50, 70, 90, 100" });
            Assert.That(r["charts"].Count(), Is.GreaterThanOrEqualTo(1));
            foreach (var series in r["charts"][0]["series"])
            {
                var y = series["y"].Values<double>().ToList();
                Assert.That(y.Count, Is.GreaterThan(2), series.Value<string>("name"));
                Assert.That(y.Last(), Is.GreaterThan(y.First()), series.Value<string>("name"));
            }
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void ASolvedPipeShowsItsProfileAndTheFlowsheetExportsToExcel()
        {
            var fs = NewFlowsheet("Water", "Methane");
            string Add(string type, string tag, double x) => Call("graph.add", new { flowsheetId = fs, type, x, y = 0, tag }).Value<string>("id");
            var a = Add("MaterialStream", "in", 0);
            var pipe = Add("Pipe", "PIPE", 100);
            var b = Add("MaterialStream", "out", 200);
            Call("stream.specify", new { flowsheetId = fs, id = a, temperature = 300.0, pressure = 2e6, massFlow = 5.0, moleFractions = new { Water = 0.7, Methane = 0.3 } });
            Call("graph.connect", new { flowsheetId = fs, sourceId = a, sourcePort = "out-0", targetId = pipe, targetPort = "in-0" });
            Call("graph.connect", new { flowsheetId = fs, sourceId = pipe, sourcePort = "out-0", targetId = b, targetPort = "in-0" });

            JToken Field(string id) => Call("object.schema", new { flowsheetId = fs, id = pipe })["sections"].SelectMany(s => s["fields"]).FirstOrDefault(f => f.Value<string>("id") == id);
            Assert.That(Field("table:pipeResults"), Is.Null, "no profile before the pipe is solved");
            var row = (JObject)Field("table:pipeProfile")["newRow"].DeepClone();
            row["length"] = 100.0;
            row["outerDiameter"] = 8.625;
            row["innerDiameter"] = 7.981;
            Call("object.setField", new { flowsheetId = fs, id = pipe, field = "table:pipeProfile", value = new JArray(row) });
            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);

            var results = Field("table:pipeResults");
            Assert.That(results.Value<bool>("readOnly"), Is.True);
            Assert.That(results["chart"].Value<string>("x"), Is.EqualTo("length"));
            var rows = results["rows"].ToList();
            Assert.That(rows.Count, Is.EqualTo(6), "five increments and the outlet");
            Assert.That(rows.First().Value<double>("length"), Is.EqualTo(0));
            Assert.That(rows.Last().Value<double>("length"), Is.EqualTo(100).Within(1e-9));
            Assert.That(rows.Last().Value<double>("pressure"), Is.LessThan(rows.First().Value<double>("pressure")));
            var outlet = Call("stream.get", new { flowsheetId = fs, id = b });
            Assert.That(rows.Last().Value<double>("pressure"), Is.EqualTo(outlet.Value<double>("pressure")).Within(1.0));

            var xlsx = Call("export.xlsx", new { flowsheetId = fs });
            Assert.That(xlsx.Value<string>("name"), Does.EndWith(".xlsx"));
            var bytes = Convert.FromBase64String(xlsx.Value<string>("base64"));
            Assert.That(bytes.Take(2), Is.EqualTo(new byte[] { (byte)'P', (byte)'K' }), "a zip package");
            using (var package = new OfficeOpenXml.ExcelPackage(new MemoryStream(bytes)))
            {
                var sheets = package.Workbook.Worksheets.Select(w => w.Name).ToList();
                Assert.That(sheets, Is.EqualTo(new[] { "Streams", "Objects" }));
                var streams = package.Workbook.Worksheets["Streams"];
                Assert.That(streams.Cells[1, 3].Text, Is.EqualTo("in"));
                Assert.That(streams.Cells[2, 1].Text, Is.EqualTo("Temperature"));
                Assert.That(Convert.ToDouble(streams.Cells[2, 3].Value), Is.EqualTo(300.0).Within(1e-9));
            }
            Call("flowsheet.close", new { flowsheetId = fs });
        }
    }
}
