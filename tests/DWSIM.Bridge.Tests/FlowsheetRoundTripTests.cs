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
        public void UnknownFlowsheetIsAnEngineError()
        {
            var ex = Assert.Throws<RpcException>(() => Call("graph.get", new { flowsheetId = "missing" }));
            Assert.That(ex.Code, Is.EqualTo(RpcErrorCodes.EngineError));
        }
    }
}
