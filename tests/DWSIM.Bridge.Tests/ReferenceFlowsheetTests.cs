using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.Thermodynamics.Streams;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>
    /// Does the bridge give the same answers as the engine hosted the way the desktop app hosts it?
    /// </summary>
    /// <remarks>
    /// Every reference flowsheet is solved twice: through the bridge's methods, and through a plain
    /// <c>FlowsheetBase</c> loaded and solved as the Avalonia app does it (LoadZippedXML /
    /// LoadFromXML, then RequestCalculationAndWait). The stream results must agree.
    ///
    /// The results stored in each file, from whichever DWSIM version last saved it, are reported
    /// beside them for information. They are not asserted: they record the engine's past, not
    /// what this host does with today's engine.
    /// </remarks>
    [NonParallelizable]
    public class ReferenceFlowsheetTests
    {
        /// <summary>Agreement required between the two hosts, relative (T, P, flow) or absolute (fractions).</summary>
        private const double Tolerance = 1e-6;

        private sealed class Silent : INotifier
        {
            public void Notify(string method, JToken parameters) { }
        }

        private sealed class StreamResult
        {
            public double? T, P, W, VF;
            public Dictionary<string, double?> X = new Dictionary<string, double?>();
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

        private static string Folder()
        {
            var folder = TestContext.CurrentContext.TestDirectory;
            while (folder != null && !Directory.Exists(Path.Combine(folder, "tests", "flowsheets")))
                folder = Path.GetDirectoryName(folder);
            return folder == null ? null : Path.Combine(folder, "tests", "flowsheets");
        }

        public static IEnumerable<string> Files()
        {
            var folder = Folder();
            if (folder == null) return Enumerable.Empty<string>();
            return Directory.GetFiles(folder)
                .Where(f => f.EndsWith(".dwxml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dwxmz", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(f => f, StringComparer.Ordinal);
        }

        [TestCaseSource(nameof(Files))]
        public void BridgeMatchesTheAppHost(string file)
        {
            var path = Path.Combine(Folder(), file);

            // Through the bridge: what the new app shows.
            var id = Call("flowsheet.open", new { path }).Value<string>("id");
            Dictionary<string, StreamResult> saved, viaBridge;
            JObject solve;
            try
            {
                var graph = Call("graph.get", new { flowsheetId = id });
                var streams = graph["nodes"].Where(n => n.Value<string>("type") == "MaterialStream").ToList();
                saved = FromBridge(id, streams);
                solve = (JObject)Call("solve.run", new { flowsheetId = id });
                viaBridge = FromBridge(id, streams);
            }
            finally
            {
                Call("flowsheet.close", new { flowsheetId = id });
            }

            // The app's way: a FlowsheetBase loaded from the file and asked to calculate.
            var app = new DWSIM.DynamicRunner.Flowsheet(null, null);
            app.Init();
            if (file.EndsWith(".dwxmz", StringComparison.OrdinalIgnoreCase)) app.LoadZippedXML(path);
            else app.LoadFromXML(System.Xml.Linq.XDocument.Load(path));
            var appErrors = app.RequestCalculationAndWait();
            var viaApp = FromEngine(app);

            TestContext.WriteLine($"bridge solve: ok={solve.Value<bool>("ok")} errors={string.Join(" | ", solve["errors"])}");
            TestContext.WriteLine($"app solve: errors={appErrors.Count} {string.Join(" | ", appErrors.Select(e => (e.InnerException ?? e).Message))}");
            Report("saved in file vs bridge solve", saved, viaBridge);

            Assert.That(solve.Value<bool>("ok"), Is.EqualTo(appErrors.Count == 0), "the two hosts disagree on whether the flowsheet solves");
            Assert.That(viaBridge.Keys, Is.EquivalentTo(viaApp.Keys), "different streams");

            var problems = Compare(viaBridge, viaApp, Tolerance);
            Assert.That(problems, Is.Empty, "bridge and app host differ:\n" + string.Join("\n", problems.Take(20)));
        }

        private Dictionary<string, StreamResult> FromBridge(string flowsheetId, IEnumerable<JToken> streams)
        {
            var result = new Dictionary<string, StreamResult>();
            foreach (var node in streams)
            {
                var s = Call("stream.get", new { flowsheetId, id = node.Value<string>("id") });
                if (!s.Value<bool>("calculated")) continue;
                var mixture = s["phases"].First(p => p.Value<int>("index") == 0);
                result[s.Value<string>("tag")] = new StreamResult
                {
                    T = s.Value<double?>("temperature"),
                    P = s.Value<double?>("pressure"),
                    W = s.Value<double?>("massFlow"),
                    VF = s.Value<double?>("vaporFraction"),
                    X = mixture["compounds"].ToDictionary(c => c.Value<string>("name"), c => c.Value<double?>("moleFraction"))
                };
            }
            return result;
        }

        private static Dictionary<string, StreamResult> FromEngine(IFlowsheet fs)
        {
            var result = new Dictionary<string, StreamResult>();
            foreach (var ms in fs.SimulationObjects.Values.OfType<MaterialStream>())
            {
                if (!ms.Calculated) continue;
                var mix = ms.Phases[0].Properties;
                result[ms.GraphicObject.Tag] = new StreamResult
                {
                    T = Finite(mix.temperature),
                    P = Finite(mix.pressure),
                    W = Finite(mix.massflow),
                    VF = ms.Phases.ContainsKey(2) ? Finite(ms.Phases[2].Properties.molarfraction) : null,
                    X = ms.Phases[0].Compounds.Values.ToDictionary(c => c.Name, c => Finite(c.MoleFraction))
                };
            }
            return result;
        }

        private static double? Finite(double? v) => v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) ? v : null;

        /// <summary>Lists every quantity that differs by more than the tolerance.</summary>
        private static List<string> Compare(Dictionary<string, StreamResult> a, Dictionary<string, StreamResult> b, double tolerance)
        {
            var problems = new List<string>();
            foreach (var tag in a.Keys.Intersect(b.Keys))
            {
                void Check(string what, double? x, double? y, bool relative)
                {
                    if (!x.HasValue || !y.HasValue) return;
                    var d = Math.Abs(x.Value - y.Value);
                    if (relative) d /= Math.Max(Math.Abs(y.Value), 1e-12);
                    if (d > tolerance) problems.Add($"{tag} {what}: {x:G10} vs {y:G10} ({(relative ? "rel " : "abs ")}{d:E2})");
                }
                Check("T", a[tag].T, b[tag].T, true);
                Check("P", a[tag].P, b[tag].P, true);
                Check("mass flow", a[tag].W, b[tag].W, true);
                Check("vapor fraction", a[tag].VF, b[tag].VF, false);
                foreach (var c in a[tag].X.Keys.Intersect(b[tag].X.Keys))
                    Check("x " + c, a[tag].X[c], b[tag].X[c], false);
            }
            return problems;
        }

        private static void Report(string title, Dictionary<string, StreamResult> before, Dictionary<string, StreamResult> after)
        {
            var worst = new Dictionary<string, (double d, string where)>();
            void Note(string what, string tag, double? x, double? y, bool relative)
            {
                if (!x.HasValue || !y.HasValue) return;
                var d = Math.Abs(x.Value - y.Value);
                if (relative) d /= Math.Max(Math.Abs(x.Value), 1e-12);
                if (!worst.TryGetValue(what, out var w) || d > w.d) worst[what] = (d, tag);
            }
            foreach (var tag in before.Keys.Intersect(after.Keys))
            {
                Note("T", tag, before[tag].T, after[tag].T, true);
                Note("P", tag, before[tag].P, after[tag].P, true);
                Note("mass flow", tag, before[tag].W, after[tag].W, true);
                Note("vapor fraction", tag, before[tag].VF, after[tag].VF, false);
                foreach (var c in before[tag].X.Keys.Intersect(after[tag].X.Keys))
                    Note("mole fraction", tag + ":" + c, before[tag].X[c], after[tag].X[c], false);
            }
            TestContext.WriteLine($"{title}: {before.Count} saved streams; worst " +
                string.Join(", ", worst.Select(w => $"{w.Key} {w.Value.d:E1} ({w.Value.where})")));
        }
    }
}
