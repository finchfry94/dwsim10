using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DWSIM.Bridge.Rpc;
using DWSIM.Bridge.Tools;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// The engineering tools (sizing, hydrates, distillation curves and the like): each describes
    /// its inputs as schema fields and answers a run with sections, charts and tables.
    /// </summary>
    public sealed class ToolMethods
    {
        private readonly SessionStore _sessions;

        internal static readonly IReadOnlyList<ITool> All = new ITool[]
        {
            new HydrateTool(),
            new PsvSizingTool(),
            new SeparatorSizingTool(),
            new DepressurizationTool(),
            new CriticalPointTool(),
            new DistillationCurvesTool(),
            new McCabeThieleTool()
        };

        public ToolMethods(SessionStore sessions) { _sessions = sessions; }

        /// <summary>Every tool with its inputs, their defaults taken from this flowsheet.</summary>
        [RpcMethod("tools.list")]
        public JArray List(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                return new JArray(All.Select(t => new JObject
                {
                    ["id"] = t.Id,
                    ["title"] = t.Title,
                    ["group"] = t.Group,
                    ["description"] = t.Description,
                    ["inputs"] = new JArray(t.Inputs(session.Inner))
                }));
            }
        }

        /// <summary>
        /// Runs a tool. <paramref name="inputs"/> maps field ids to values; <paramref name="units"/>
        /// gives the unit a number was typed in, when not the field's own.
        /// </summary>
        [RpcMethod("tools.run")]
        public JObject Run(string flowsheetId, string tool, JObject inputs = null, JObject units = null, CancellationToken cancellation = default)
        {
            var t = All.FirstOrDefault(x => x.Id == tool) ?? throw new RpcException(RpcErrorCodes.InvalidParams, "No tool '" + tool + "'.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var result = new ToolResult(session.Inner);
                using (cancellation.Register(() => DWSIM.GlobalSettings.Settings.TaskCancellationTokenSource?.Cancel()))
                {
                    try
                    {
                        t.Run(session.Inner, new ToolInputs(session.Inner, inputs, units), result, cancellation);
                    }
                    catch (RpcException) { throw; }
                    catch (Exception ex)
                    {
                        throw new RpcException(RpcErrorCodes.EngineError, t.Title + " failed: " + (ex.InnerException ?? ex).Message);
                    }
                }
                if (cancellation.IsCancellationRequested) throw new RpcException(RpcErrorCodes.Cancelled, "Cancelled.");
                var json = result.ToJson();
                json["tool"] = t.Id;
                json["title"] = t.Title;
                return json;
            }
        }
    }
}
