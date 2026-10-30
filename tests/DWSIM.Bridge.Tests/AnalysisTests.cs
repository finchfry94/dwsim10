using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>Results and analysis: the stream table, the report, and phase and binary envelopes.</summary>
    [NonParallelizable]
    public class AnalysisTests
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

        private static string Sample(string file)
        {
            var folder = TestContext.CurrentContext.TestDirectory;
            while (folder != null && !Directory.Exists(Path.Combine(folder, "tests", "flowsheets")))
                folder = Path.GetDirectoryName(folder);
            if (folder == null) Assert.Ignore("tests/flowsheets not found");
            return Path.Combine(folder, "tests", "flowsheets", file);
        }

        private string SolvedCavett()
        {
            var fs = Call("flowsheet.open", new { path = Sample("CavettProblem.dwxml") }).Value<string>("id");
            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);
            return fs;
        }

        [Test]
        public void TheStreamTableAgreesWithEachStream()
        {
            var fs = SolvedCavett();
            var table = Call("streams.table", new { flowsheetId = fs });
            var streams = table["streams"].ToList();
            Assert.That(streams, Has.Count.EqualTo(20));
            Assert.That(streams.Select(s => s.Value<string>("tag")).Take(3), Is.EqualTo(new[] { "1", "2", "3" }), "in natural order");
            var rows = table["rows"].ToDictionary(r => r.Value<string>("key"));
            foreach (var s in streams)
            {
                var id = s.Value<string>("id");
                var state = Call("stream.get", new { flowsheetId = fs, id });
                // Cavett is in SI, so the table's values are the stream's SI ones.
                Assert.That(rows["temperature"]["values"].Value<double>(id), Is.EqualTo(state.Value<double>("temperature")).Within(1e-9));
                Assert.That(rows["pressure"]["values"].Value<double>(id), Is.EqualTo(state.Value<double>("pressure")).Within(1e-6));
                Assert.That(rows["massFlow"]["values"].Value<double>(id), Is.EqualTo(state.Value<double>("massFlow")).Within(1e-9));
                var fractions = rows.Values.Where(r => r.Value<string>("group") == "moleFractions").Sum(r => r["values"].Value<double?>(id) ?? 0);
                Assert.That(fractions, Is.EqualTo(1.0).Within(1e-5), "the mole fractions add up (the engine normalizes to about 1e-6)");
            }
            var mass = Call("streams.table", new { flowsheetId = fs, basis = "mass" });
            Assert.That(mass["rows"].Any(r => r.Value<string>("group") == "massFractions"), Is.True);
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void TheReportNamesEveryObjectAndCanBeNarrowed()
        {
            var fs = SolvedCavett();
            var nodes = Call("graph.get", new { flowsheetId = fs })["nodes"].ToList();
            var html = Call("report.html", new { flowsheetId = fs }).Value<string>("html");
            Assert.That(html, Does.StartWith("<!DOCTYPE html>"));
            foreach (var n in nodes.Where(n => n.Value<string>("type") == "MaterialStream"))
                Assert.That(html, Does.Contain(">" + n.Value<string>("tag") + "<").Or.Contain(n.Value<string>("tag")));
            var one = Call("report.html", new { flowsheetId = fs, objectIds = new[] { nodes[0].Value<string>("id") } }).Value<string>("html");
            Assert.That(one.Length, Is.LessThan(html.Length / 5));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void APhaseEnvelopeHasBubbleAndDewLinesMeetingAtTheCriticalPoint()
        {
            var fs = Call("flowsheet.new", new { name = "envelope" }).Value<string>("id");
            Call("flowsheet.addCompounds", new { flowsheetId = fs, names = new[] { "Methane", "Ethane" } });
            Call("flowsheet.addPropertyPackage", new { flowsheetId = fs, name = "Peng-Robinson (PR)" });
            Call("flowsheet.setUnitSystem", new { flowsheetId = fs, name = "SI" });
            var gas = Call("graph.add", new { flowsheetId = fs, type = "MaterialStream", x = 0.0, y = 0.0, tag = "gas" }).Value<string>("id");
            Call("stream.specify", new
            {
                flowsheetId = fs, id = gas, temperature = 250.0, pressure = 3e6, molarFlow = 1.0,
                moleFractions = new Dictionary<string, double> { ["Methane"] = 0.5, ["Ethane"] = 0.5 }
            });
            Call("solve.run", new { flowsheetId = fs });

            var envelope = Call("analysis.phaseEnvelope", new { flowsheetId = fs, streamId = gas, diagram = "PT" });
            var series = envelope["series"].ToDictionary(s => s.Value<string>("name"));
            Assert.That(series.Keys, Is.SupersetOf(new[] { "Bubble points", "Dew points", "Critical point", "Operating point" }));
            Assert.That(series["Bubble points"]["x"].Count(), Is.GreaterThan(10));
            Assert.That(envelope["x"].Value<string>("unit"), Is.EqualTo("K"));

            // The mixture's critical point lies between the pure methane (190.6 K) and ethane (305.3 K) ones.
            var tc = envelope["criticalPoint"].Value<double>("temperature");
            var pc = envelope["criticalPoint"].Value<double>("pressure");
            Assert.That(tc, Is.InRange(190.6, 305.3));
            Assert.That(pc, Is.GreaterThan(4.5e6), "above both pure critical pressures, as for this pair");

            var ph = Call("analysis.phaseEnvelope", new { flowsheetId = fs, streamId = gas, diagram = "PH", options = new { OperatingPoint = false } });
            Assert.That(ph["series"].Select(s => s.Value<string>("name")), Has.None.EqualTo("Operating point"));
            Assert.Throws<RpcException>(() => Call("analysis.phaseEnvelope", new { flowsheetId = fs, streamId = gas, diagram = "XY" }));
            Assert.Throws<RpcException>(() => Call("analysis.phaseEnvelope", new { flowsheetId = fs, streamId = gas, options = new { NoSuchOption = true } }));
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void TheEthanolWaterTxyShowsTheAzeotrope()
        {
            var fs = Call("flowsheet.new", new { name = "binary" }).Value<string>("id");
            Call("flowsheet.addCompounds", new { flowsheetId = fs, names = new[] { "Ethanol", "Water" } });
            Call("flowsheet.addPropertyPackage", new { flowsheetId = fs, name = "NRTL" });
            Call("flowsheet.setUnitSystem", new { flowsheetId = fs, name = "SI" });

            var txy = Call("analysis.binaryEnvelope", new { flowsheetId = fs, compound1 = "Ethanol", compound2 = "Water", diagram = "Txy", pressure = 101325.0 });
            var bubble = txy["series"].First(s => s.Value<string>("name") == "Bubble points");
            var t = bubble["y"].Select(v => v.Value<double>()).ToList();
            var x = bubble["x"].Select(v => v.Value<double>()).ToList();
            var min = t.IndexOf(t.Min());
            // As in the T03 validation case: a minimum-boiling azeotrope near 351.3 K, rich in ethanol.
            Assert.That(t[min], Is.EqualTo(351.3).Within(1.0));
            Assert.That(x[min], Is.InRange(0.8, 0.95));
            Assert.That(t.First(), Is.EqualTo(373.15).Within(0.5), "pure water boils at 100 °C");

            var pxy = Call("analysis.binaryEnvelope", new { flowsheetId = fs, compound1 = "Ethanol", compound2 = "Water", diagram = "Pxy", temperature = 350.0 });
            Assert.That(pxy["y"].Value<string>("unit"), Is.EqualTo("Pa"));
            Assert.Throws<RpcException>(() => Call("analysis.binaryEnvelope", new { flowsheetId = fs, compound1 = "Ethanol", compound2 = "Methanol" }));
            Call("flowsheet.close", new { flowsheetId = fs });
        }
    }
}
