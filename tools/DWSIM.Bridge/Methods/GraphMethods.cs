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
                return new JObject { ["nodes"] = nodes, ["edges"] = edges };
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
                try
                {
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
                return Get(flowsheetId);
            }
        }

        [RpcMethod("graph.disconnect")]
        public JObject Disconnect(string flowsheetId, string sourceId, string targetId)
        {
            var session = _sessions.Get(flowsheetId);
            lock (session.Gate)
            {
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
                session.Inner.AutoLayout();
                return Get(flowsheetId);
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
