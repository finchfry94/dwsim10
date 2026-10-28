using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>
    /// The methods the desktop app calls, run against the engine on a reference flowsheet:
    /// open, read the graph, solve, read a stream, edit, save and reopen.
    /// </summary>
    [NonParallelizable]
    public class FlowsheetRoundTripTests
    {
        private sealed class Recorder : INotifier
        {
            public readonly List<string> Methods = new List<string>();

            public void Notify(string method, JToken parameters)
            {
                lock (Methods) Methods.Add(method);
            }
        }

        private RpcRegistry _registry;
        private Recorder _notifications;

        [OneTimeSetUp]
        public void StartEngine()
        {
            _registry = new RpcRegistry();
            _notifications = new Recorder();
            BridgeHost.Register(_registry, new SessionStore(), _notifications);
            BridgeHost.WarmUp();
        }

        private JToken Call(string method, object args = null)
        {
            return _registry.Invoke(method, args == null ? new JObject() : JObject.FromObject(args), CancellationToken.None);
        }

        private static string Flowsheets()
        {
            var folder = TestContext.CurrentContext.TestDirectory;
            while (folder != null && !Directory.Exists(Path.Combine(folder, "tests", "flowsheets")))
                folder = Path.GetDirectoryName(folder);
            Assert.That(folder, Is.Not.Null, "could not find tests/flowsheets");
            return Path.Combine(folder, "tests", "flowsheets");
        }

        [Test]
        public void CavettOpensAsAConnectedGraphAndSolves()
        {
            var fs = Call("flowsheet.open", new { path = Path.Combine(Flowsheets(), "CavettProblem.dwxml") });
            var id = fs.Value<string>("id");
            Assert.That(fs["compounds"].Count(), Is.EqualTo(16));

            var graph = Call("graph.get", new { flowsheetId = id });
            var nodes = (JArray)graph["nodes"];
            var edges = (JArray)graph["edges"];
            Assert.That(nodes.Count, Is.GreaterThan(30));
            var ids = new HashSet<string>(nodes.Select(n => n.Value<string>("id")));
            foreach (var e in edges)
            {
                Assert.That(ids, Does.Contain(e.Value<string>("source")));
                Assert.That(ids, Does.Contain(e.Value<string>("target")));
                var source = nodes.First(n => n.Value<string>("id") == e.Value<string>("source"));
                Assert.That(source["ports"].Select(p => p.Value<string>("id")), Does.Contain(e.Value<string>("sourcePort")));
            }

            var solved = Call("solve.run", new { flowsheetId = id });
            Assert.That(solved.Value<bool>("ok"), Is.True, string.Join("; ", solved["errors"]));
            Assert.That(_notifications.Methods, Does.Contain("solve/finished"));

            var feed = nodes.First(n => n.Value<string>("type") == "MaterialStream");
            var stream = Call("stream.get", new { flowsheetId = id, id = feed.Value<string>("id") });
            Assert.That(stream.Value<double>("temperature"), Is.GreaterThan(200).And.LessThan(800));
            var mixture = stream["phases"].First(p => p.Value<int>("index") == 0);
            Assert.That(mixture["compounds"].Sum(c => c.Value<double>("moleFraction")), Is.EqualTo(1).Within(1e-6));

            Call("flowsheet.close", new { flowsheetId = id });
        }

        [Test]
        public void NewFlowsheetEditsSaveAndReopen()
        {
            var id = Call("flowsheet.new", new { name = "bridge test" }).Value<string>("id");
            var heater = Call("graph.add", new { flowsheetId = id, type = "Heater", x = 200, y = 100, tag = "H-1" });
            var inlet = Call("graph.add", new { flowsheetId = id, type = "MaterialStream", x = 50, y = 100, tag = "S-1" });
            Call("graph.connect", new
            {
                flowsheetId = id,
                sourceId = inlet.Value<string>("id"), sourcePort = "out-0",
                targetId = heater.Value<string>("id"), targetPort = "in-0"
            });
            Call("graph.move", new { flowsheetId = id, id = heater.Value<string>("id"), x = 300, y = 120 });
            Call("object.rename", new { flowsheetId = id, id = "S-1", tag = "Feed" });

            var graph = Call("graph.get", new { flowsheetId = id });
            Assert.That(graph["edges"].Count(), Is.EqualTo(1));
            Assert.That(graph["nodes"].First(n => n.Value<string>("tag") == "H-1").Value<double>("x"), Is.EqualTo(300));

            var path = Path.Combine(Path.GetTempPath(), "bridge-test-" + System.Guid.NewGuid().ToString("N") + ".dwxmz");
            try
            {
                Call("flowsheet.save", new { flowsheetId = id, path });
                var reopened = Call("flowsheet.open", new { path }).Value<string>("id");
                var again = Call("graph.get", new { flowsheetId = reopened });
                Assert.That(again["nodes"].Select(n => n.Value<string>("tag")), Is.SupersetOf(new[] { "H-1", "Feed" }));
                Assert.That(again["edges"].Count(), Is.EqualTo(1));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void ANewSimulationIsSetUpSolvedAndTrimmed()
        {
            var id = Call("flowsheet.new", new { name = "setup" }).Value<string>("id");

            var hits = (JArray)Call("compounds.search", new { flowsheetId = id, query = "ethanol", limit = 5 });
            Assert.That(hits.First().Value<string>("name"), Is.EqualTo("Ethanol"), "an exact name match comes first");
            Assert.That((JArray)Call("propertyPackages.list", new { flowsheetId = id }), Has.Some.Matches<JToken>(t => t.Value<string>() == "NRTL"));

            Call("flowsheet.addCompounds", new { flowsheetId = id, names = new[] { "Water", "Ethanol" } });
            var summary = Call("flowsheet.addPropertyPackage", new { flowsheetId = id, name = "NRTL" });
            Assert.That(summary["compounds"].Select(c => c.Value<string>()), Is.EqualTo(new[] { "Water", "Ethanol" }));
            Assert.That(summary["propertyPackages"].Count(), Is.EqualTo(1));

            var systems = (JArray)Call("unitSystems.list", new { flowsheetId = id });
            Assert.That(systems.Select(u => u.Value<string>("name")), Does.Contain("SI"));
            Assert.That(Call("flowsheet.setUnitSystem", new { flowsheetId = id, name = "SI" })["unitSystem"].Value<string>("temperature"), Is.EqualTo("K"));

            var feed = Call("graph.add", new { flowsheetId = id, type = "MaterialStream", x = 0, y = 0, tag = "feed" }).Value<string>("id");
            Call("stream.specify", new
            {
                flowsheetId = id, id = feed, pressure = 101325.0, vaporFraction = 0.0, molarFlow = 1.0,
                compoundMolarFlows = new Dictionary<string, double> { ["Water"] = 0.6, ["Ethanol"] = 0.4 }
            });
            Assert.That(Call("solve.run", new { flowsheetId = id }).Value<bool>("ok"), Is.True);
            var stream = Call("stream.get", new { flowsheetId = id, id = feed });
            Assert.That(stream.Value<string>("spec"), Is.EqualTo("Pressure_and_VaporFraction"));
            Assert.That(stream.Value<double>("temperature"), Is.InRange(351.0, 373.2), "bubble point between the pure boiling points");

            // Fractions with a total flow: the composition first, then the total scales it.
            Call("stream.specify", new
            {
                flowsheetId = id, id = feed, temperature = 300.0, pressure = 101325.0, massFlow = 2.0,
                massFractions = new Dictionary<string, double> { ["Water"] = 1.0, ["Ethanol"] = 1.0 }
            });
            Assert.That(Call("solve.run", new { flowsheetId = id }).Value<bool>("ok"), Is.True);
            stream = Call("stream.get", new { flowsheetId = id, id = feed });
            Assert.That(stream.Value<string>("spec"), Is.EqualTo("Temperature_and_Pressure"));
            Assert.That(stream.Value<double>("massFlow"), Is.EqualTo(2.0).Within(1e-9));
            var mixture = stream["phases"].First(p => p.Value<int>("index") == 0)["compounds"];
            Assert.That(mixture.Select(c => c.Value<double>("massFraction")), Is.All.EqualTo(0.5).Within(1e-9), "normalized to 50/50");

            var nrtl = summary["propertyPackages"][0].Value<string>("id");
            Assert.Throws<RpcException>(() => Call("flowsheet.removePropertyPackage", new { flowsheetId = id, id = nrtl }), "the only package, with a stream");
            summary = Call("flowsheet.addPropertyPackage", new { flowsheetId = id, name = "Peng-Robinson (PR)" });
            summary = Call("flowsheet.removePropertyPackage", new { flowsheetId = id, id = nrtl });
            Assert.That(summary["propertyPackages"].Select(p => p.Value<string>("model")), Is.EqualTo(new[] { "Peng-Robinson (PR)" }));
            Assert.That(Call("solve.run", new { flowsheetId = id }).Value<bool>("ok"), Is.True, "the feed falls back to the package left");

            summary = Call("flowsheet.removeCompound", new { flowsheetId = id, name = "Ethanol" });
            Assert.That(summary["compounds"].Select(c => c.Value<string>()), Is.EqualTo(new[] { "Water" }));
            stream = Call("stream.get", new { flowsheetId = id, id = feed });
            var overall = stream["phases"].First(p => p.Value<int>("index") == 0)["compounds"];
            Assert.That(overall.Select(c => c.Value<string>("name")), Is.EqualTo(new[] { "Water" }));
            Assert.That(stream.Value<bool>("calculated"), Is.False);

            Call("flowsheet.close", new { flowsheetId = id });
        }

        [Test]
        public void UnknownFlowsheetIsAnEngineError()
        {
            var ex = Assert.Throws<RpcException>(() => Call("graph.get", new { flowsheetId = "missing" }));
            Assert.That(ex.Code, Is.EqualTo(RpcErrorCodes.EngineError));
        }
    }
}
