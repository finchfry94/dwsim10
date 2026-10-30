using System;
using System.Collections.Concurrent;
using System.Linq;
using DWSIM.Automation.DynamicRunner.Scenarios;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// Scenario comparison, as ScenarioComparisonWindow does it: capture the solved flowsheet as
    /// A, change something and solve, capture B, and list what moved. Snapshots live for the
    /// session, not in the file.
    /// </summary>
    public sealed class ScenarioMethods
    {
        private readonly SessionStore _sessions;
        private readonly ConcurrentDictionary<string, ScenarioSnapshot> _snapshots = new ConcurrentDictionary<string, ScenarioSnapshot>();

        public ScenarioMethods(SessionStore sessions) { _sessions = sessions; }

        private static string Key(string flowsheetId, string slot) => flowsheetId + "|" + slot;

        private static string Slot(string slot)
            => slot == "A" || slot == "B" ? slot : throw new RpcException(RpcErrorCodes.InvalidParams, "The slot is A or B.");

        private static JObject Describe(ScenarioSnapshot s) => s == null ? null : new JObject
        {
            ["label"] = s.Label,
            ["taken"] = s.Taken.ToString("o"),
            ["values"] = s.Values.Count
        };

        [RpcMethod("scenarios.get")]
        public JObject Get(string flowsheetId)
        {
            _sessions.Get(flowsheetId);
            _snapshots.TryGetValue(Key(flowsheetId, "A"), out var a);
            _snapshots.TryGetValue(Key(flowsheetId, "B"), out var b);
            return new JObject { ["A"] = Describe(a), ["B"] = Describe(b) };
        }

        /// <summary>Takes the flowsheet as it is now as scenario A or B.</summary>
        [RpcMethod("scenarios.capture")]
        public JObject Capture(string flowsheetId, string slot, string label = null)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var snap = ScenarioComparison.Snapshot(session.Inner, string.IsNullOrWhiteSpace(label) ? Slot(slot) : label.Trim());
                _snapshots[Key(flowsheetId, Slot(slot))] = snap;
            }
            return Get(flowsheetId);
        }

        /// <summary>
        /// Every property in both snapshots: specifications first, then results, each ordered by
        /// how much it moved. Text values compare as text.
        /// </summary>
        [RpcMethod("scenarios.compare")]
        public JObject Compare(string flowsheetId, double tolerance = 1e-6)
        {
            _sessions.Get(flowsheetId);
            if (!_snapshots.TryGetValue(Key(flowsheetId, "A"), out var a) || !_snapshots.TryGetValue(Key(flowsheetId, "B"), out var b))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Capture both scenarios first.");
            var r = ScenarioComparison.Compare(a, b, tolerance);
            JToken Num(double v) => double.IsNaN(v) || double.IsInfinity(v) ? JValue.CreateNull() : new JValue(v);
            return new JObject
            {
                ["labelA"] = r.LabelA,
                ["labelB"] = r.LabelB,
                ["inputsChanged"] = r.InputsChanged,
                ["outputsChanged"] = r.OutputsChanged,
                ["unchanged"] = r.Unchanged,
                ["summary"] = new JArray(r.Summary),
                ["onlyInA"] = new JArray(r.OnlyInA),
                ["onlyInB"] = new JArray(r.OnlyInB),
                ["rows"] = new JArray(r.Differences
                    .OrderByDescending(d => d.Changed)
                    .ThenByDescending(d => d.IsInput)
                    .ThenByDescending(d => double.IsNaN(d.Percent) ? (d.Changed ? double.MaxValue : 0) : Math.Abs(d.Percent))
                    .Select(d => new JObject
                    {
                        ["object"] = d.ObjectTag,
                        ["objectId"] = d.ObjectName,
                        ["type"] = d.ObjectType,
                        ["property"] = d.Name,
                        ["unit"] = d.Unit,
                        ["input"] = d.IsInput,
                        ["changed"] = d.Changed,
                        ["a"] = double.IsNaN(d.A) ? (JToken)d.TextA : Num(d.A),
                        ["b"] = double.IsNaN(d.B) ? (JToken)d.TextB : Num(d.B),
                        ["delta"] = Num(d.Delta),
                        ["percent"] = Num(d.Percent)
                    }))
            };
        }

        /// <summary>Forgets the snapshots of a flowsheet (when it closes, or on request).</summary>
        [RpcMethod("scenarios.clear")]
        public JObject Clear(string flowsheetId)
        {
            _snapshots.TryRemove(Key(flowsheetId, "A"), out _);
            _snapshots.TryRemove(Key(flowsheetId, "B"), out _);
            return new JObject { ["A"] = null, ["B"] = null };
        }
    }
}
