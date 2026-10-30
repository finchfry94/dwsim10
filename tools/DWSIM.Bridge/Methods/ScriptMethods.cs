using System;
using System.Collections.Generic;
using System.Linq;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using Newtonsoft.Json.Linq;
using ScriptEnums = DWSIM.Interfaces.Enums.Scripts;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// The flowsheet's Python scripts, as ScriptEditorWindow edits them: text, interpreter, and an
    /// optional link to an event that runs the script by itself. Scripts are saved with the file.
    /// </summary>
    public sealed class ScriptMethods
    {
        private readonly SessionStore _sessions;

        public ScriptMethods(SessionStore sessions) { _sessions = sessions; }

        /// <summary>The events a script can be linked to, by what raises them.</summary>
        private static readonly Dictionary<ScriptEnums.ObjectType, ScriptEnums.EventType[]> Events = new Dictionary<ScriptEnums.ObjectType, ScriptEnums.EventType[]>
        {
            [ScriptEnums.ObjectType.Simulation] = new[]
            {
                ScriptEnums.EventType.SimulationOpened, ScriptEnums.EventType.SimulationSaved, ScriptEnums.EventType.SimulationClosed,
                ScriptEnums.EventType.SimulationTimer1, ScriptEnums.EventType.SimulationTimer5, ScriptEnums.EventType.SimulationTimer15,
                ScriptEnums.EventType.SimulationTimer30, ScriptEnums.EventType.SimulationTimer60
            },
            [ScriptEnums.ObjectType.Solver] = new[] { ScriptEnums.EventType.SolverStarted, ScriptEnums.EventType.SolverFinished, ScriptEnums.EventType.SolverRecycleLoop },
            [ScriptEnums.ObjectType.Integrator] = new[]
            {
                ScriptEnums.EventType.IntegratorStarted, ScriptEnums.EventType.IntegratorFinished, ScriptEnums.EventType.IntegratorError,
                ScriptEnums.EventType.IntegratorStep, ScriptEnums.EventType.IntegratorPreStep
            },
            [ScriptEnums.ObjectType.FlowsheetObject] = new[]
            {
                ScriptEnums.EventType.ObjectCalculationStarted, ScriptEnums.EventType.ObjectCalculationFinished, ScriptEnums.EventType.ObjectCalculationError
            }
        };

        private static JObject ToJson(IScript s) => new JObject
        {
            ["id"] = s.ID,
            ["title"] = s.Title ?? "",
            ["text"] = s.ScriptText ?? "",
            ["interpreter"] = s.PythonInterpreter.ToString(),
            ["linked"] = s.Linked,
            ["linkedTo"] = s.LinkedObjectType.ToString(),
            ["linkedObject"] = string.IsNullOrEmpty(s.LinkedObjectName) ? null : s.LinkedObjectName,
            ["event"] = s.LinkedEventType.ToString()
        };

        /// <summary>The scripts, and the events each kind of source raises.</summary>
        [RpcMethod("scripts.list")]
        public JObject List(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
                return new JObject
                {
                    ["scripts"] = new JArray(session.Inner.Scripts.Values.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).Select(ToJson)),
                    ["events"] = new JObject(Events.Select(e => new JProperty(e.Key.ToString(), new JArray(e.Value.Select(v => v.ToString())))))
                };
        }

        /// <summary>Creates (no id) or replaces a script.</summary>
        [RpcMethod("scripts.save")]
        public JObject Save(string flowsheetId, JObject script)
        {
            if (script == null) throw new RpcException(RpcErrorCodes.InvalidParams, "Give the script.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var id = script.Value<string>("id");
                IScript existing = null;
                if (!string.IsNullOrEmpty(id) && !fs.Scripts.TryGetValue(id, out existing))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "No script with id '" + id + "'.");
                var title = (script.Value<string>("title") ?? "").Trim();
                if (title.Length == 0) title = "Script " + (fs.Scripts.Count + 1);
                var linkedTo = Enum.TryParse<ScriptEnums.ObjectType>(script.Value<string>("linkedTo") ?? "", out var lt) ? lt : ScriptEnums.ObjectType.Simulation;
                var ev = Enum.TryParse<ScriptEnums.EventType>(script.Value<string>("event") ?? "", out var e) ? e : Events[linkedTo][0];
                if (!Events[linkedTo].Contains(ev)) ev = Events[linkedTo][0];
                var linkedObject = script.Value<string>("linkedObject");
                var linked = script.Value<bool?>("linked") ?? false;
                if (linked && linkedTo == ScriptEnums.ObjectType.FlowsheetObject && !fs.SimulationObjects.ContainsKey(linkedObject ?? ""))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Pick the object whose calculation runs the script.");

                session.Record((existing == null ? "Add script " : "Edit script ") + title);
                var s = existing ?? new DWSIM.FlowsheetSolver.Script { ID = Guid.NewGuid().ToString() };
                s.Title = title;
                s.ScriptText = script.Value<string>("text") ?? "";
                s.PythonInterpreter = Enum.TryParse<ScriptEnums.Interpreter>(script.Value<string>("interpreter") ?? "", out var interp) ? interp : ScriptEnums.Interpreter.IronPython;
                s.Linked = linked;
                s.LinkedObjectType = linkedTo;
                s.LinkedObjectName = linkedTo == ScriptEnums.ObjectType.FlowsheetObject ? linkedObject ?? "" : "";
                s.LinkedEventType = ev;
                if (existing == null) fs.Scripts.Add(s.ID, s);
                return ToJson(s);
            }
        }

        [RpcMethod("scripts.delete")]
        public JObject Delete(string flowsheetId, string id)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                if (!session.Inner.Scripts.TryGetValue(id ?? "", out var s))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "No script with id '" + id + "'.");
                session.Record("Delete script " + s.Title);
                session.Inner.Scripts.Remove(id);
            }
            return List(flowsheetId);
        }

        /// <summary>
        /// Runs a script now and returns what it wrote to the log (its output and any error).
        /// A script may change anything, so the run is one undo step.
        /// </summary>
        [RpcMethod("scripts.run")]
        public JObject Run(string flowsheetId, string id)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                if (!session.Inner.Scripts.TryGetValue(id ?? "", out var s))
                    throw new RpcException(RpcErrorCodes.InvalidParams, "No script with id '" + id + "'.");
                session.Record("Run script " + s.Title);
                var output = new List<string>();
                session.Captured = output;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                string error = null;
                // IFlowsheet.RunScript looks the script up by title; the base class runs it by id.
                try { ((DWSIM.FlowsheetBase.FlowsheetBase)session.Inner).RunScript(s.ID); }
                catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
                finally { session.Captured = null; }
                error ??= output.FirstOrDefault(l => l.StartsWith("Error running script", StringComparison.Ordinal));
                return new JObject
                {
                    ["ok"] = error == null,
                    ["error"] = error,
                    ["output"] = new JArray(output.Select(l => l.TrimEnd('\r', '\n')).Where(l => l.Length > 0)),
                    ["durationMs"] = clock.ElapsedMilliseconds
                };
            }
        }
    }
}
