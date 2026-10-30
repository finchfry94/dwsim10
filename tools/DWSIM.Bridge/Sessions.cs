using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DWSIM.Interfaces;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;
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

        public Fluent Flowsheet { get; private set; }

        /// <summary>While set, the log lines the flowsheet writes are also kept here (a script run returns them).</summary>
        internal List<string> Captured { get; set; }

        /// <summary>Wires a flowsheet to the session (its log listener); run again when undo replaces it.</summary>
        internal Action<Session> Attach { get; set; }

        /// <summary>
        /// Puts a flowsheet loaded from <paramref name="state"/> in place of the current one, as
        /// opening a file would: the objects keep their ids, which the XML carries.
        /// </summary>
        internal void Replace(XDocument state)
        {
            var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dwsim-bridge-" + Guid.NewGuid().ToString("N") + ".dwxml");
            try
            {
                state.Save(temp);
                Flowsheet = Fluent.Load(temp);
            }
            finally
            {
                try { System.IO.File.Delete(temp); } catch { /* best effort */ }
            }
            Inner.FilePath = Path;
            Inner.FlowsheetOptions.FilePath = Path;
            Attach?.Invoke(this);
        }

        public IFlowsheet Inner => Flowsheet.Inner;

        /// <summary>Where the flowsheet was opened from or last saved to; null for a new one.</summary>
        public string Path { get; set; }

        /// <summary>
        /// The engine is not thread-safe, and requests run in parallel, so every method that touches
        /// the flowsheet holds this lock. A solve holds it for its whole run.
        /// </summary>
        public object Gate { get; } = new object();

        /// <summary>The edits that can be undone and redone, and whether any is unsaved.</summary>
        public History History { get; } = new History();

        /// <summary>
        /// Starts an undoable edit: the flowsheet as it is now becomes the undo step once the edit
        /// commits. An edit that throws before <see cref="Edit.Commit"/> leaves no step behind.
        /// Call it holding <see cref="Gate"/>.
        /// </summary>
        public Edit Begin(string label) => new Edit(this, label, Inner.SaveToXML());

        /// <summary>Makes the flowsheet as it is now an undo step; call it holding <see cref="Gate"/>, just before the change.</summary>
        public void Record(string label) => Begin(label).Commit();

        public sealed class Edit
        {
            private readonly Session _session;
            private readonly string _label;
            private readonly XDocument _before;

            internal Edit(Session session, string label, XDocument before)
            {
                _session = session;
                _label = label;
                _before = before;
            }

            public void Commit() => _session.History.Push(_label, _before);
        }

        /// <summary>Finds a simulation object by its id (the engine's Name), falling back to its tag.</summary>
        public ISimulationObject Object(string id)
        {
            if (Inner.SimulationObjects.TryGetValue(id, out var obj)) return obj;
            obj = Inner.SimulationObjects.Values.FirstOrDefault(o => o.GraphicObject?.Tag == id);
            if (obj == null) throw new RpcException(RpcErrorCodes.EngineError, "No object with id or tag '" + id + "'.");
            return obj;
        }
    }

    /// <summary>
    /// Undo and redo as whole-flowsheet snapshots: the XML a save writes, restored the way a
    /// file is opened. (The engine's RestoreSnapshot(All) does not bring deleted objects back
    /// headless.) Snapshots cost a copy of the flowsheet's XML each, so the history keeps the
    /// last <see cref="Depth"/> steps.
    /// </summary>
    public sealed class History
    {
        public const int Depth = 30;

        private readonly LinkedList<(string label, XDocument state)> _undo = new LinkedList<(string, XDocument)>();
        private readonly LinkedList<(string label, XDocument state)> _redo = new LinkedList<(string, XDocument)>();

        /// <summary>Changed since it was opened or last saved.</summary>
        public bool Modified { get; set; }

        public string UndoLabel => _undo.Last?.Value.label;

        public string RedoLabel => _redo.Last?.Value.label;

        internal void Push(string label, XDocument before)
        {
            _undo.AddLast((label, before));
            while (_undo.Count > Depth) _undo.RemoveFirst();
            _redo.Clear();
            Modified = true;
        }

        /// <summary>Restores the state before the last edit; returns its label, or null when there is none.</summary>
        public string Undo(Session session) => Step(session, _undo, _redo);

        public string Redo(Session session) => Step(session, _redo, _undo);

        private string Step(Session session, LinkedList<(string label, XDocument state)> from, LinkedList<(string label, XDocument state)> to)
        {
            if (from.Last == null) return null;
            var (label, state) = from.Last.Value;
            var current = session.Inner.SaveToXML();
            session.Replace(state);
            from.RemoveLast();
            to.AddLast((label, current));
            Modified = true;
            return label;
        }

        public JObject ToJson() => new JObject
        {
            ["canUndo"] = _undo.Count > 0,
            ["canRedo"] = _redo.Count > 0,
            ["undo"] = UndoLabel,
            ["redo"] = RedoLabel,
            ["modified"] = Modified
        };
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
