using System;
using System.Collections.Generic;
using System.Linq;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Methods
{
    /// <summary>
    /// The flowsheet as the canvas sees it: one node per simulation object, with its ports, and one
    /// edge per attached connector. Streams are nodes too, as in DWSIM itself, so a unit operation
    /// is never wired straight to another one.
    /// </summary>
    /// <remarks>
    /// Port ids are <c>in-N</c> and <c>out-N</c> for the object's input and output connector lists,
    /// and <c>energy</c> for its separate energy connector, so they map back onto the engine's
    /// connector indices without a lookup table.
    /// </remarks>
    public sealed class GraphMethods
    {
        private readonly SessionStore _sessions;

        public GraphMethods(SessionStore sessions) { _sessions = sessions; }

        [RpcMethod("graph.get")]
        public JObject Get(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var nodes = new JArray();
                var edges = new JArray();
                var seen = new HashSet<string>();
                foreach (var obj in session.Inner.SimulationObjects.Values)
                {
                    var go = obj.GraphicObject;
                    if (go == null) continue;
                    nodes.Add(Node(obj));
                    foreach (var edge in Edges(go))
                        if (seen.Add(edge.Value<string>("id"))) edges.Add(edge);
                }
                return new JObject { ["nodes"] = nodes, ["edges"] = edges, ["history"] = session.History.ToJson() };
            }
        }

        [RpcMethod("graph.add")]
        public JObject Add(string flowsheetId, string type, double x, double y, string tag = null)
        {
            if (!Enum.TryParse<ObjectType>(type, true, out var objectType))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Unknown object type: " + type);
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                session.Record("Add " + type);
                var obj = session.Inner.AddObject(objectType, (int)Math.Round(x), (int)Math.Round(y), tag ?? "");
                if (obj == null) throw new RpcException(RpcErrorCodes.EngineError, "The engine could not add a " + type + ".");
                // The app has no adjust control panel: a new adjust converges with the flowsheet.
                if (obj is DWSIM.UnitOperations.SpecialOps.Adjust adjust) adjust.SimultaneousAdjust = true;
                return Node(obj);
            }
        }

        [RpcMethod("graph.move")]
        public JObject Move(string flowsheetId, string id, double x, double y)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var obj = session.Object(id);
                session.Record("Move " + obj.GraphicObject.Tag);
                obj.GraphicObject.X = (float)x;
                obj.GraphicObject.Y = (float)y;
                return Node(obj);
            }
        }

        /// <summary>Connects an output port of one object to an input port of another.</summary>
        [RpcMethod("graph.connect")]
        public JObject Connect(string flowsheetId, string sourceId, string sourcePort, string targetId, string targetPort)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var source = session.Object(sourceId).GraphicObject;
                var target = session.Object(targetId).GraphicObject;
                var from = PortIndex(sourcePort, "out");
                var to = PortIndex(targetPort, "in");
                var edit = session.Begin("Connect " + source.Tag + " to " + target.Tag);
                try
                {
                    if (!IsStream(source) && !IsStream(target))
                    {
                        // Two unit operations meet through a stream, as in DWSIM itself: insert one
                        // between them, energy when either port carries energy.
                        var energy = sourcePort == "energy" || (from >= 0 && from < source.OutputConnectors.Count && source.OutputConnectors[from].Type == ConType.ConEn)
                                     || (to >= 0 && to < target.InputConnectors.Count && target.InputConnectors[to].Type == ConType.ConEn);
                        var (sx, sy) = PortPoint(source, sourcePort);
                        var (tx, ty) = PortPoint(target, targetPort);
                        var stream = session.Inner.AddObject(energy ? ObjectType.EnergyStream : ObjectType.MaterialStream,
                            (int)Math.Round((sx + tx) / 2 - 10), (int)Math.Round((sy + ty) / 2 - 10), "");
                        session.Inner.ConnectObjects(source, stream.GraphicObject, from, 0);
                        session.Inner.ConnectObjects(stream.GraphicObject, target, 0, to);
                    }
                    else
                        session.Inner.ConnectObjects(source, target, from, to);
                    // A stream wired to a rigorous column gets a stage, so the column solves as drawn.
                    foreach (var end in new[] { sourceId, targetId })
                        if (session.Object(end) is DWSIM.UnitOperations.UnitOperations.Column column)
                            Editors.ColumnFields.EnsureStreamStages(column);
                }
                catch (Exception ex)
                {
                    throw new RpcException(RpcErrorCodes.EngineError, ex.Message);
                }
                edit.Commit();
                return Get(flowsheetId);
            }
        }

        /// <summary>Moves several objects at once, as one undo step.</summary>
        [RpcMethod("graph.moveMany")]
        public JObject MoveMany(string flowsheetId, JArray moves)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var targets = (moves ?? new JArray()).Select(m => (obj: session.Object(m.Value<string>("id")), x: m.Value<double>("x"), y: m.Value<double>("y"))).ToList();
                if (targets.Count == 0) return Get(flowsheetId);
                session.Record(targets.Count == 1 ? "Move " + targets[0].obj.GraphicObject.Tag : $"Move {targets.Count} objects");
                foreach (var (obj, x, y) in targets)
                {
                    obj.GraphicObject.X = (float)x;
                    obj.GraphicObject.Y = (float)y;
                }
                return Get(flowsheetId);
            }
        }

        /// <summary>Deletes several objects at once, as one undo step.</summary>
        [RpcMethod("graph.deleteMany")]
        public JObject DeleteMany(string flowsheetId, string[] ids)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var graphics = (ids ?? Array.Empty<string>()).Select(id => session.Object(id).GraphicObject).Distinct().ToList();
                if (graphics.Count == 0) return Get(flowsheetId);
                session.Record(graphics.Count == 1 ? "Delete " + graphics[0].Tag : $"Delete {graphics.Count} objects");
                foreach (var go in graphics)
                    if (session.Inner.SimulationObjects.ContainsKey(go.Name))
                        session.Inner.DeleteSelectedObject(null, EventArgs.Empty, go, false, false);
                return Get(flowsheetId);
            }
        }

        /// <summary>
        /// Rotates objects by <paramref name="rotate"/> degrees (a multiple of 90) and flips them
        /// horizontally or vertically; the ports follow.
        /// </summary>
        [RpcMethod("graph.transform")]
        public JObject Transform(string flowsheetId, string[] ids, int rotate = 0, bool flipH = false, bool flipV = false)
        {
            if (rotate % 90 != 0) throw new RpcException(RpcErrorCodes.InvalidParams, "Rotate by a multiple of 90 degrees.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var graphics = (ids ?? Array.Empty<string>()).Select(id => session.Object(id).GraphicObject).ToList();
                if (graphics.Count == 0) return Get(flowsheetId);
                session.Record(rotate != 0 ? "Rotate" : "Flip");
                foreach (var go in graphics)
                {
                    go.Rotation = ((go.Rotation + rotate) % 360 + 360) % 360;
                    if (flipH) go.FlippedH = !go.FlippedH;
                    if (flipV) go.FlippedV = !go.FlippedV;
                }
                return Get(flowsheetId);
            }
        }

        /// <summary>
        /// The objects as the engine's clipboard XML (CopyObjects), to paste here or into another
        /// flowsheet: the objects, their property packages and the compounds.
        /// </summary>
        [RpcMethod("graph.copy")]
        public JObject Copy(string flowsheetId, string[] ids)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var chosen = new HashSet<string>((ids ?? Array.Empty<string>()).Select(id => session.Object(id).Name));
                if (chosen.Count == 0) throw new RpcException(RpcErrorCodes.InvalidParams, "Select something to copy.");
                var flowsheet = Base(session);
                foreach (var obj in session.Inner.SimulationObjects.Values)
                    if (obj.GraphicObject != null) obj.GraphicObject.Selected = chosen.Contains(obj.Name);
                try
                {
                    flowsheet.CopyObjects();
                    return new JObject { ["text"] = DWSIM.SharedClasses.SystemClipboard.GetText(), ["count"] = chosen.Count };
                }
                finally
                {
                    foreach (var obj in session.Inner.SimulationObjects.Values)
                        if (obj.GraphicObject != null) obj.GraphicObject.Selected = false;
                }
            }
        }

        /// <summary>
        /// Pastes what <c>graph.copy</c> returned, offset from the originals. Connections among the
        /// pasted objects are kept. The engine pastes a copy of each property package too; a copy
        /// of a package this flowsheet still has is folded back into it.
        /// </summary>
        [RpcMethod("graph.paste")]
        public JObject Paste(string flowsheetId, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.Contains("DWSIM_Simulation_Data"))
                throw new RpcException(RpcErrorCodes.InvalidParams, "Nothing to paste.");
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var fs = session.Inner;
                var objectsBefore = new HashSet<string>(fs.SimulationObjects.Keys);
                var packagesBefore = new HashSet<string>(fs.PropertyPackages.Keys);
                var edit = session.Begin("Paste");
                DWSIM.SharedClasses.SystemClipboard.SetText(Detached(text));
                // PasteObjects swallows its errors; the last one thrown is what stopped it.
                Exception last = null;
                var thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                void Note(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
                {
                    if (System.Threading.Thread.CurrentThread.ManagedThreadId == thread) last = e.Exception;
                }
                AppDomain.CurrentDomain.FirstChanceException += Note;
                try { Base(session).PasteObjects(false); }
                finally { AppDomain.CurrentDomain.FirstChanceException -= Note; }
                var pasted = fs.SimulationObjects.Values.Where(o => !objectsBefore.Contains(o.Name)).ToList();
                if (pasted.Count == 0)
                    throw new RpcException(RpcErrorCodes.EngineError, "The engine could not paste that" + (last == null ? "." : ": " + last.Message));

                foreach (var key in fs.PropertyPackages.Keys.Where(k => !packagesBefore.Contains(k)).ToList())
                {
                    // PasteObjects names a copy "<random>_<original id>".
                    var original = key.Substring(key.IndexOf('_') + 1);
                    if (!fs.PropertyPackages.TryGetValue(original, out var keep)) continue;
                    var copy = fs.PropertyPackages[key];
                    foreach (var obj in pasted)
                        if (ReferenceEquals(PackageOf(obj), copy)) obj.PropertyPackage = keep;
                    fs.PropertyPackages.Remove(key);
                }
                foreach (var obj in pasted)
                {
                    obj.Calculated = false;
                    if (obj.GraphicObject != null) { obj.GraphicObject.Calculated = false; obj.GraphicObject.Selected = false; }
                }
                edit.Commit();
                var graph = Get(flowsheetId);
                graph["pasted"] = new JArray(pasted.Select(o => o.Name));
                return graph;
            }
        }

        /// <summary>
        /// The clipboard XML with every connection to an object outside it undone: the engine
        /// otherwise tries to reattach a pasted object to the original's neighbours, and the
        /// first port already taken aborts the whole paste.
        /// </summary>
        internal static string Detached(string text)
        {
            System.Xml.Linq.XDocument doc;
            try { doc = System.Xml.Linq.XDocument.Parse(text); }
            catch (Exception ex) { throw new RpcException(RpcErrorCodes.InvalidParams, "Nothing to paste: " + ex.Message); }
            var graphics = doc.Root?.Element("GraphicObjects")?.Elements().ToList() ?? new List<System.Xml.Linq.XElement>();
            var inside = new HashSet<string>(graphics.Select(g => g.Element("Name")?.Value).Where(n => n != null));
            foreach (var g in graphics)
                foreach (var list in new[] { "InputConnectors", "OutputConnectors", "EnergyConnector" })
                    foreach (var c in g.Element(list)?.Elements() ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!string.Equals((string)c.Attribute("IsAttached"), "true", StringComparison.OrdinalIgnoreCase)) continue;
                        var other = (string)c.Attribute("AttachedFromObjID") ?? (string)c.Attribute("AttachedToObjID");
                        var to = (string)c.Attribute("AttachedToObjID");
                        var from = (string)c.Attribute("AttachedFromObjID");
                        if ((from != null && !inside.Contains(from)) || (to != null && !inside.Contains(to)) || (from == null && to == null))
                            c.SetAttributeValue("IsAttached", "false");
                    }
            // A stream that never had a package set names none, and the engine pastes it under
            // "<key>_" alone; give it the first package, the one it falls back to anyway.
            var firstPackage = doc.Root?.Element("PropertyPackages")?.Elements().Select(p => p.Element("ID")?.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            if (firstPackage != null)
                foreach (var so in doc.Root?.Element("SimulationObjects")?.Elements() ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                {
                    var pp = so.Element("PropertyPackage");
                    if (pp != null && string.IsNullOrEmpty(pp.Value)) pp.Value = firstPackage;
                }
            return doc.ToString();
        }

        private static IPropertyPackage PackageOf(ISimulationObject obj)
        {
            try { return obj.PropertyPackage; } catch { return null; }
        }

        private static DWSIM.FlowsheetBase.FlowsheetBase Base(Session session)
            => session.Inner as DWSIM.FlowsheetBase.FlowsheetBase
               ?? throw new RpcException(RpcErrorCodes.EngineError, "This flowsheet does not support copy and paste.");

        private static bool IsStream(IGraphicObject go)
            => go.ObjectType == ObjectType.MaterialStream || go.ObjectType == ObjectType.EnergyStream;

        /// <summary>Where a port sits, in flowsheet coordinates.</summary>
        private static (double x, double y) PortPoint(IGraphicObject go, string port)
        {
            try { go.PositionConnectors(); } catch { }
            IConnectionPoint cp = null;
            if (port == "energy") cp = go.EnergyConnector;
            else if (port?.StartsWith("out-") == true && int.TryParse(port.Substring(4), out var o) && o < go.OutputConnectors.Count) cp = go.OutputConnectors[o];
            else if (port?.StartsWith("in-") == true && int.TryParse(port.Substring(3), out var i) && i < go.InputConnectors.Count) cp = go.InputConnectors[i];
            if (cp?.Position != null) return (cp.Position.X, cp.Position.Y);
            return (go.X + go.Width / 2.0, go.Y + go.Height / 2.0);
        }

        [RpcMethod("graph.disconnect")]
        public JObject Disconnect(string flowsheetId, string sourceId, string targetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                session.Record("Disconnect");
                session.Inner.DisconnectObjects(session.Object(sourceId).GraphicObject, session.Object(targetId).GraphicObject);
                return Get(flowsheetId);
            }
        }

        [RpcMethod("graph.delete")]
        public JObject Delete(string flowsheetId, string id)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                var go = session.Object(id).GraphicObject;
                session.Record("Delete " + go.Tag);
                session.Inner.DeleteSelectedObject(null, EventArgs.Empty, go, false, false);
                return Get(flowsheetId);
            }
        }

        [RpcMethod("graph.autoLayout")]
        public JObject AutoLayout(string flowsheetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                session.Record("Layout");
                session.Inner.AutoLayout();
                return Get(flowsheetId);
            }
        }

        /// <summary>Undoes the last edit and returns the graph as it is then.</summary>
        [RpcMethod("edit.undo")]
        public JObject Undo(string flowsheetId) => Step(flowsheetId, redo: false);

        [RpcMethod("edit.redo")]
        public JObject Redo(string flowsheetId) => Step(flowsheetId, redo: true);

        private JObject Step(string flowsheetId, bool redo)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
                string label;
                try
                {
                    label = redo ? session.History.Redo(session) : session.History.Undo(session);
                }
                catch (Exception ex)
                {
                    throw new RpcException(RpcErrorCodes.EngineError, (redo ? "Redo" : "Undo") + " failed: " + ex.Message);
                }
                if (label == null) throw new RpcException(RpcErrorCodes.EngineError, "Nothing to " + (redo ? "redo." : "undo."));
                var graph = Get(flowsheetId);
                graph["done"] = label;
                return graph;
            }
        }

        internal static JObject Node(ISimulationObject obj)
        {
            var go = obj.GraphicObject;
            try { go.PositionConnectors(); } catch { /* some shapes only position on draw */ }

            // Every port of an energy stream carries energy, whatever its connector type says.
            var energyStream = go.ObjectType == ObjectType.EnergyStream;
            var ports = new JArray();
            for (int i = 0; i < go.InputConnectors.Count; i++)
                ports.Add(Port(go, go.InputConnectors[i], "in-" + i, energyStream || go.InputConnectors[i].Type == ConType.ConEn ? "energy-in" : "in"));
            for (int i = 0; i < go.OutputConnectors.Count; i++)
                ports.Add(Port(go, go.OutputConnectors[i], "out-" + i, energyStream || go.OutputConnectors[i].Type == ConType.ConEn ? "energy-out" : "out"));
            if (go.EnergyConnector != null && go.EnergyConnector.Active)
                ports.Add(Port(go, go.EnergyConnector, "energy", "energy-out"));

            return new JObject
            {
                ["id"] = obj.Name,
                ["tag"] = go.Tag,
                ["type"] = ObjectTypes.Name(go.ObjectType),
                ["x"] = go.X,
                ["y"] = go.Y,
                ["width"] = go.Width,
                ["height"] = go.Height,
                ["rotation"] = go.Rotation,
                ["flippedH"] = go.FlippedH,
                ["flippedV"] = go.FlippedV,
                ["active"] = go.Active,
                ["status"] = go.Status.ToString(),
                ["calculated"] = obj.Calculated,
                ["error"] = string.IsNullOrEmpty(obj.ErrorMessage) ? null : obj.ErrorMessage,
                ["ports"] = ports,
                ["stream"] = StreamState(obj)
            };
        }

        /// <summary>
        /// The stream's molar vapor fraction. A solved stream with no vapor has none recorded, and
        /// that is a fraction of zero, not an unknown; an unsolved one has no answer yet.
        /// </summary>
        internal static double? VaporFraction(DWSIM.Thermodynamics.Streams.MaterialStream ms)
        {
            var vf = ms.Phases.ContainsKey(2) ? ms.Phases[2].Properties.molarfraction : null;
            if (vf.HasValue && !double.IsNaN(vf.Value)) return vf;
            return ms.Calculated ? 0.0 : (double?)null;
        }

        /// <summary>
        /// The few numbers the canvas colours and labels a material stream by, in SI, so a whole
        /// flowsheet's worth comes in one call; null for every other object.
        /// </summary>
        private static JObject StreamState(ISimulationObject obj)
        {
            if (!(obj is DWSIM.Thermodynamics.Streams.MaterialStream ms)) return null;
            var mix = ms.Phases[0].Properties;
            return new JObject
            {
                ["temperature"] = mix.temperature,
                ["pressure"] = mix.pressure,
                ["massFlow"] = mix.massflow,
                ["molarFlow"] = mix.molarflow,
                ["vaporFraction"] = VaporFraction(ms)
            };
        }

        private static JObject Port(IGraphicObject go, IConnectionPoint cp, string id, string kind)
        {
            // Connector positions are absolute; the canvas wants them relative to the node box. Some
            // connectors are saved without a Position object and keep only their X and Y.
            double w = Math.Max(go.Width, 1), h = Math.Max(go.Height, 1);
            double px = cp.Position?.X ?? cp.X, py = cp.Position?.Y ?? cp.Y;
            return new JObject
            {
                ["id"] = id,
                ["kind"] = kind,
                ["name"] = string.IsNullOrEmpty(cp.ConnectorName) ? null : cp.ConnectorName,
                ["x"] = Math.Round((px - go.X) / w, 4),
                ["y"] = Math.Round((py - go.Y) / h, 4),
                ["attached"] = cp.IsAttached
            };
        }

        private static IEnumerable<JObject> Edges(IGraphicObject go)
        {
            for (int i = 0; i < go.OutputConnectors.Count; i++)
            {
                var edge = Edge(go, go.OutputConnectors[i], "out-" + i);
                if (edge != null) yield return edge;
            }
            if (go.EnergyConnector != null && go.EnergyConnector.Active)
            {
                var edge = Edge(go, go.EnergyConnector, "energy");
                if (edge != null) yield return edge;
            }
        }

        private static JObject Edge(IGraphicObject source, IConnectionPoint cp, string sourcePort)
        {
            if (!cp.IsAttached || cp.AttachedConnector == null) return null;
            var con = cp.AttachedConnector;
            var target = con.AttachedTo;
            if (target?.Owner == null || source.Owner == null || ReferenceEquals(target, source)) return null;

            var targetPort = con.AttachedToEnergy ? "energy" : "in-" + con.AttachedToConnectorIndex;
            var energy = source.ObjectType == ObjectType.EnergyStream || target.ObjectType == ObjectType.EnergyStream
                         || cp.Type == ConType.ConEn;
            return new JObject
            {
                ["id"] = source.Owner.Name + ":" + sourcePort + "->" + target.Owner.Name + ":" + targetPort,
                ["source"] = source.Owner.Name,
                ["sourcePort"] = sourcePort,
                ["target"] = target.Owner.Name,
                ["targetPort"] = targetPort,
                ["kind"] = energy ? "energy" : "material"
            };
        }

        /// <summary>Maps a port id onto the engine's connector index; -1 lets the engine choose.</summary>
        private static int PortIndex(string port, string prefix)
        {
            if (string.IsNullOrEmpty(port) || port == "energy") return -1;
            if (port.StartsWith(prefix + "-", StringComparison.Ordinal) &&
                int.TryParse(port.Substring(prefix.Length + 1), out var index))
                return index;
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Port '{port}' is not an {prefix} port.");
        }
    }
}
