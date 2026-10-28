using System;
using System.Linq;
using System.Reflection;
using DWSIM.Bridge.Rpc;
using DWSIM.Interfaces.Enums.GraphicObjects;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Methods
{
    public sealed class EngineMethods
    {
        private readonly RpcRegistry _registry;

        public EngineMethods(RpcRegistry registry) { _registry = registry; }

        [RpcMethod("engine.ping")]
        public JObject Ping() => new JObject { ["pong"] = true };

        /// <summary>What the engine offers: its version, its methods, the object types it can place.</summary>
        [RpcMethod("engine.info")]
        public JObject Info()
        {
            var engine = typeof(DWSIM.Interfaces.IFlowsheet).Assembly.GetName().Version;
            return new JObject
            {
                ["bridgeVersion"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                ["engineVersion"] = engine?.ToString(),
                ["methods"] = new JArray(_registry.Names),
                ["objectTypes"] = new JArray(Enum.GetNames(typeof(ObjectType)).OrderBy(n => n))
            };
        }
    }
}
