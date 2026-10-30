using System;
using System.IO;
using System.Linq;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using Newtonsoft.Json.Linq;
using Fluent = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.Bridge.Methods
{
    public sealed class FlowsheetMethods
    {
        private readonly SessionStore _sessions;
        private readonly INotifier _notifier;

        public FlowsheetMethods(SessionStore sessions, INotifier notifier)
        {
            _sessions = sessions;
            _notifier = notifier;
        }

        [RpcMethod("flowsheet.new")]
        public JObject New(string name = null)
        {
            return Open(Fluent.Create(string.IsNullOrWhiteSpace(name) ? "New Simulation" : name), null);
        }

        [RpcMethod("flowsheet.open")]
        public JObject OpenFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new RpcException(RpcErrorCodes.EngineError, "File not found: " + path);
            return Open(Fluent.Load(path), Path.GetFullPath(path));
        }

        /// <summary>Saves to <paramref name="path"/>, or back to the file it came from; .dwxmz is zipped.</summary>
        [RpcMethod("flowsheet.save")]
        public JObject Save(string flowsheetId, string path = null)
        {
            var session = _sessions.Get(flowsheetId);
            var target = string.IsNullOrWhiteSpace(path) ? session.Path : path;
            if (string.IsNullOrWhiteSpace(target))
                throw new RpcException(RpcErrorCodes.InvalidParams, "A new flowsheet needs a path to save to.");
            var compressed = !string.Equals(Path.GetExtension(target), ".dwxml", StringComparison.OrdinalIgnoreCase);
            lock (session.Gate)
            {
                session.Flowsheet.Save(target, compressed);
                session.Path = Path.GetFullPath(target);
                session.History.Modified = false;
            }
            return new JObject { ["path"] = session.Path };
        }

        [RpcMethod("flowsheet.close")]
        public JObject Close(string flowsheetId)
        {
            return new JObject { ["closed"] = _sessions.Remove(flowsheetId) };
        }

        [RpcMethod("flowsheet.list")]
        public JArray List()
        {
            return new JArray(_sessions.All.Select(s => Summary(s)));
        }

        [RpcMethod("flowsheet.summary")]
        public JObject Summary(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate) return Summary(session);
        }

        private JObject Open(Fluent flowsheet, string path)
        {
            var session = _sessions.Add(flowsheet, path);
            var id = session.Id;
            session.Attach = s => s.Inner.SetMessageListener((text, type) =>
                _notifier.Notify("engine/log", new JObject
                {
                    ["flowsheetId"] = id,
                    ["level"] = Level(type),
                    ["text"] = text
                }));
            session.Attach(session);
            lock (session.Gate) return Summary(session);
        }

        internal static JObject Summary(Session session)
        {
            var inner = session.Inner;
            var su = inner.FlowsheetOptions.SelectedUnitSystem;
            return new JObject
            {
                ["id"] = session.Id,
                ["name"] = inner.FlowsheetOptions.SimulationName,
                ["path"] = session.Path,
                ["compounds"] = new JArray(inner.SelectedCompounds.Keys.ToArray()),
                ["propertyPackages"] = new JArray(inner.PropertyPackages.Values.Select(pp => new JObject
                {
                    ["id"] = pp.UniqueID,
                    ["name"] = pp.Tag,
                    ["model"] = (pp as DWSIM.Thermodynamics.PropertyPackages.PropertyPackage)?.ComponentName ?? pp.GetType().Name
                })),
                ["unitSystem"] = su == null ? null : UnitSystem(su),
                ["objectCount"] = inner.SimulationObjects.Count
            };
        }

        /// <summary>The units the flowsheet displays, for the quantities the UI shows most.</summary>
        internal static JObject UnitSystem(IUnitsOfMeasure su)
        {
            return new JObject
            {
                ["name"] = su.Name,
                ["temperature"] = su.temperature,
                ["pressure"] = su.pressure,
                ["massflow"] = su.massflow,
                ["molarflow"] = su.molarflow,
                ["volumetricFlow"] = su.volumetricFlow,
                ["enthalpy"] = su.enthalpy,
                ["heatflow"] = su.heatflow,
                ["density"] = su.density
            };
        }

        private static string Level(IFlowsheet.MessageType type)
        {
            switch (type)
            {
                case IFlowsheet.MessageType.GeneralError: return "error";
                case IFlowsheet.MessageType.Warning: return "warning";
                case IFlowsheet.MessageType.Tip: return "tip";
                default: return "info";
            }
        }
    }
}
