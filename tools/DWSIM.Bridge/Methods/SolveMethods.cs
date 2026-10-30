using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DWSIM.Automation.FluentAPI.Diagnostics;
using DWSIM.Bridge.Rpc;
using DWSIM.GlobalSettings;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Methods
{
    public sealed class SolveMethods
    {
        private readonly SessionStore _sessions;
        private readonly INotifier _notifier;

        public SolveMethods(SessionStore sessions, INotifier notifier)
        {
            _sessions = sessions;
            _notifier = notifier;
        }

        /// <summary>
        /// Solves the flowsheet. Announces <c>solve/started</c> and <c>solve/finished</c> so every
        /// view can show the run, not only the one that asked for it. Cancelling the request stops
        /// the solver at its next check.
        /// </summary>
        [RpcMethod("solve.run")]
        public JObject Run(string flowsheetId, CancellationToken cancellation)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                _notifier.Notify("solve/started", new JObject { ["flowsheetId"] = flowsheetId });
                var clock = Stopwatch.StartNew();
                // The engine's solver watches the process-wide token, not one handed to it.
                using (cancellation.Register(() => Settings.TaskCancellationTokenSource?.Cancel()))
                {
                    var errors = session.Flowsheet.TrySolve();
                    // The spreadsheet's GETPROPVAL cells follow the new results.
                    try { SpreadsheetMethods.Recalculate(session); } catch { /* a bad formula shows in its cell */ }
                    var result = new JObject
                    {
                        ["flowsheetId"] = flowsheetId,
                        ["ok"] = errors.Count == 0,
                        ["cancelled"] = cancellation.IsCancellationRequested,
                        ["durationMs"] = clock.ElapsedMilliseconds,
                        ["errors"] = new JArray(errors.Select(e => (e.InnerException ?? e).Message))
                    };
                    _notifier.Notify("solve/finished", result);
                    return result;
                }
            }
        }

        /// <summary>The faults that stop a flowsheet solving, found without solving it.</summary>
        [RpcMethod("solve.check")]
        public JArray Check(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                return new JArray(FlowsheetDiagnostics.Check(session.Inner).Select(f => new JObject
                {
                    ["code"] = f.Code,
                    ["severity"] = f.Severity.ToString().ToLowerInvariant(),
                    ["object"] = f.ObjectTag,
                    ["message"] = f.Message,
                    ["fix"] = f.Fix
                }));
            }
        }
    }
}
