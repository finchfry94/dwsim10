using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DWSIM.Interfaces;
using DWSIM.Bridge.Rpc;
using Fluent = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.Bridge
{
    /// <summary>One open flowsheet: the engine document plus the lock that serializes access to it.</summary>
    public sealed class Session
    {
        internal Session(string id, Fluent flowsheet, string path)
        {
            Id = id;
            Flowsheet = flowsheet;
            Path = path;
        }

        public string Id { get; }

        public Fluent Flowsheet { get; }

        public IFlowsheet Inner => Flowsheet.Inner;

        /// <summary>Where the flowsheet was opened from or last saved to; null for a new one.</summary>
        public string Path { get; set; }

        /// <summary>
        /// The engine is not thread-safe, and requests run in parallel, so every method that touches
        /// the flowsheet holds this lock. A solve holds it for its whole run.
        /// </summary>
        public object Gate { get; } = new object();

        /// <summary>Finds a simulation object by its id (the engine's Name), falling back to its tag.</summary>
        public ISimulationObject Object(string id)
        {
            if (Inner.SimulationObjects.TryGetValue(id, out var obj)) return obj;
            obj = Inner.SimulationObjects.Values.FirstOrDefault(o => o.GraphicObject?.Tag == id);
            if (obj == null) throw new RpcException(RpcErrorCodes.EngineError, "No object with id or tag '" + id + "'.");
            return obj;
        }
    }

    public sealed class SessionStore
    {
        private readonly ConcurrentDictionary<string, Session> _sessions = new ConcurrentDictionary<string, Session>();

        public Session Add(Fluent flowsheet, string path)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 12);
            var session = new Session(id, flowsheet, path);
            _sessions[id] = session;
            return session;
        }

        public Session Get(string flowsheetId)
        {
            if (flowsheetId != null && _sessions.TryGetValue(flowsheetId, out var session)) return session;
            throw new RpcException(RpcErrorCodes.EngineError, "No open flowsheet with id '" + flowsheetId + "'.");
        }

        public bool Remove(string flowsheetId) => _sessions.TryRemove(flowsheetId ?? "", out _);

        public IReadOnlyList<Session> All => _sessions.Values.ToList();
    }
}
