using System.IO;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>Dynamic mode, the readiness fixes, integrators, schedules, events, states and runs.</summary>
    [NonParallelizable]
    public class DynamicsTests
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

        private JToken Call(string method, object args, CancellationToken token = default)
            => _registry.Invoke(method, JObject.FromObject(args), token);

        private static string Sample(string file)
        {
            var folder = TestContext.CurrentContext.TestDirectory;
            while (folder != null && !Directory.Exists(Path.Combine(folder, "tests", "flowsheets")))
                folder = Path.GetDirectoryName(folder);
            if (folder == null) Assert.Ignore("tests/flowsheets not found");
            return Path.Combine(folder, "tests", "flowsheets", file);
        }

        private string Open(string file)
        {
            var fs = Call("flowsheet.open", new { path = Sample(file) }).Value<string>("id");
            Assert.That(Call("solve.run", new { flowsheetId = fs }).Value<bool>("ok"), Is.True);
            return fs;
        }

        [Test]
        public void TheVesselSampleIntegratesAndRecordsItsVariables()
        {
            var fs = Open("VesselDepressurization.dwxml");
            var ex = Assert.Throws<RpcException>(() => Call("dynamics.run", new { flowsheetId = fs }));
            Assert.That(ex.Message, Does.Contain("dynamic mode"));

            var state = Call("dynamics.setMode", new { flowsheetId = fs, enabled = true });
            Assert.That(state.Value<bool>("enabled"), Is.True);
            var integrator = state["integrators"].Single();
            Assert.That(integrator.Value<double>("step"), Is.EqualTo(1.0));
            Assert.That(integrator.Value<double>("duration"), Is.EqualTo(100.0));
            Assert.That(integrator["variables"].Count(), Is.EqualTo(4));

            var readiness = Call("dynamics.readiness", new { flowsheetId = fs });
            Assert.That(readiness.Any(r => r.Value<bool>("canFix")), Is.True);
            var fixes = Call("dynamics.applyFixes", new { flowsheetId = fs });
            Assert.That(fixes["applied"], Is.Not.Empty);
            Assert.That(fixes["failed"], Is.Empty);
            Assert.That(fixes["remaining"].Any(r => r.Value<bool>("canFix")), Is.False);

            var run = Call("dynamics.run", new { flowsheetId = fs });
            Assert.That(run["errors"], Is.Empty);
            Assert.That(run.Value<int>("samples"), Is.EqualTo(101), "t = 0 and every second to 100 s");
            Assert.That(run["charts"].Count(), Is.EqualTo(4), "one per unit");
            var times = run["charts"][0]["series"][0]["x"].Values<double>().ToList();
            Assert.That(times.First(), Is.EqualTo(0.0));
            Assert.That(times.Last(), Is.EqualTo(100.0).Within(1e-6));
            var pressure = run["charts"].First(c => c["y"].Value<string>("unit") == "bar")["series"][0]["y"].Values<double>().ToList();
            Assert.That(pressure.Last(), Is.Not.EqualTo(pressure.First()).Within(1e-3), "the vessel's pressure moves");
            Assert.That(run["table"]["rows"].Count(), Is.EqualTo(101));
            Assert.That(Call("graph.get", new { flowsheetId = fs })["history"].Value<string>("undo"), Does.StartWith("Run "));

            var again = Call("dynamics.results", new { flowsheetId = fs, integrator = integrator.Value<string>("id") });
            Assert.That(again.Value<int>("samples"), Is.EqualTo(101));

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var stopped = Call("dynamics.run", new { flowsheetId = fs }, cancelled.Token);
                Assert.That(stopped.Value<bool>("cancelled"), Is.True);
                Assert.That(stopped.Value<int>("steps"), Is.LessThan(100));
            }
            Call("flowsheet.close", new { flowsheetId = fs });
        }

        [Test]
        public void AStepInTheFeedRaisesTheSeparatorLevel()
        {
            var fs = Open("ThreePhaseSeparator.dwxml");
            Call("dynamics.setMode", new { flowsheetId = fs, enabled = true });
            var fixes = Call("dynamics.applyFixes", new { flowsheetId = fs });
            Assert.That(fixes["applied"].Values<string>(), Has.Some.StartsWith("NO_SCHEDULE"));
            var nodes = Call("graph.get", new { flowsheetId = fs })["nodes"].ToDictionary(n => n.Value<string>("tag"), n => n.Value<string>("id"));
            var state = fixes["state"];
            var integrator = state["integrators"].Single();

            state = Call("dynamics.saveIntegrator", new
            {
                flowsheetId = fs,
                integrator = JObject.FromObject(new
                {
                    id = integrator.Value<string>("id"),
                    name = "Two minutes",
                    step = 2.0,
                    duration = 120.0,
                    variables = new[] { new { @object = nodes["separator"], property = "Liquid Level", name = "Level" } }
                })
            });
            Assert.That(state["integrators"][0]["variables"].Single().Value<string>("unit"), Is.Not.Null.And.Not.Empty);

            var feedMass = Call("stream.get", new { flowsheetId = fs, id = nodes["feed"] }).Value<double>("massFlow");
            state = Call("dynamics.saveEventSet", new
            {
                flowsheetId = fs,
                eventSet = JObject.FromObject(new
                {
                    name = "Feed step",
                    events = new[]
                    {
                        new { time = 20.0, kind = "ChangeProperty", @object = nodes["feed"], property = "PROP_MS_2", value = (1.2 * feedMass).ToString(System.Globalization.CultureInfo.InvariantCulture), unit = "kg/s" }
                    }
                })
            });
            var eventSet = state["eventSets"].Single();
            Assert.That(eventSet["events"].Single().Value<double>("time"), Is.EqualTo(20.0));
            Assert.That(eventSet["events"].Single().Value<string>("label"), Does.StartWith("feed · "));

            state = Call("dynamics.storeState", new { flowsheetId = fs, name = "Steady" });
            Assert.That(state["states"].Values<string>(), Contains.Item("Steady"));
            var schedule = state["schedules"].Single();
            state = Call("dynamics.saveSchedule", new
            {
                flowsheetId = fs,
                schedule = JObject.FromObject(new
                {
                    id = schedule.Value<string>("id"),
                    name = "Feed step",
                    integrator = integrator.Value<string>("id"),
                    useCurrentState = false,
                    initialState = "Steady",
                    eventSet = eventSet.Value<string>("id")
                })
            });
            Assert.That(state["schedules"][0].Value<string>("eventSet"), Is.EqualTo(eventSet.Value<string>("id")));

            var run = Call("dynamics.run", new { flowsheetId = fs });
            Assert.That(run["errors"], Is.Empty, string.Join("; ", run["errors"].Values<string>()));
            Assert.That(run.Value<int>("samples"), Is.EqualTo(61));
            var level = run["charts"].Single()["series"].Single()["y"].Values<double>().ToList();
            Assert.That(level[60], Is.GreaterThan(level[10] + 0.1), "20 % more feed fills the separator");

            Assert.Throws<RpcException>(() => Call("dynamics.saveEventSet", new
            {
                flowsheetId = fs,
                eventSet = JObject.FromObject(new { name = "Bad", events = new[] { new { time = 5.0, kind = "ChangeProperty" } } })
            }));
            state = Call("dynamics.delete", new { flowsheetId = fs, kind = "eventSet", id = eventSet.Value<string>("id") });
            Assert.That(state["schedules"][0].Value<string>("eventSet"), Is.Null, "the schedule lets go of it");
            state = Call("dynamics.deleteState", new { flowsheetId = fs, name = "Steady" });
            Assert.That(state["schedules"][0].Value<bool>("useCurrentState"), Is.True);

            // Saved with the file.
            var path = Path.Combine(Path.GetTempPath(), "m5b-dyn-" + System.Guid.NewGuid().ToString("N") + ".dwxml");
            Call("flowsheet.save", new { flowsheetId = fs, path });
            var reopened = Call("flowsheet.open", new { path }).Value<string>("id");
            var back = Call("dynamics.get", new { flowsheetId = reopened });
            // Dynamic mode itself is not saved: files open in steady state, as in the desktop app.
            Assert.That(back["integrators"][0].Value<string>("name"), Is.EqualTo("Two minutes"));
            Assert.That(back["integrators"][0]["variables"].Count(), Is.EqualTo(1));
            Call("flowsheet.close", new { flowsheetId = reopened });
            File.Delete(path);
            Call("flowsheet.close", new { flowsheetId = fs });
        }
    }
}
