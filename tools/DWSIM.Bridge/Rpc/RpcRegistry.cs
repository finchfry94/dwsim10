using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Rpc
{
    /// <summary>
    /// The method table: every <see cref="RpcMethodAttribute"/> method of the registered objects,
    /// keyed by its RPC name. Same reflection pattern as the MCP server's ToolRegistry, without the
    /// tool schema an assistant needs.
    /// </summary>
    public sealed class RpcRegistry
    {
        private sealed class Entry
        {
            public object Instance;
            public MethodInfo Method;
            public ParameterInfo[] Parameters;
        }

        private readonly Dictionary<string, Entry> _methods = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public void Register(object instance)
        {
            var type = instance.GetType();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<RpcMethodAttribute>();
                if (attr == null) continue;
                if (_methods.TryGetValue(attr.Name, out var clash))
                    throw new InvalidOperationException(
                        $"Two methods are registered as '{attr.Name}': {clash.Instance.GetType().Name}.{clash.Method.Name} and {type.Name}.{method.Name}.");
                _methods[attr.Name] = new Entry { Instance = instance, Method = method, Parameters = method.GetParameters() };
            }
        }

        public IReadOnlyList<string> Names => _methods.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();

        public bool Contains(string name) => _methods.ContainsKey(name);

        /// <summary>Binds <paramref name="args"/> to the method's parameters and calls it.</summary>
        public JToken Invoke(string name, JObject args, CancellationToken token)
        {
            if (!_methods.TryGetValue(name, out var entry))
                throw new RpcException(RpcErrorCodes.MethodNotFound, "Method not found: " + name);

            var values = new object[entry.Parameters.Length];
            for (int i = 0; i < entry.Parameters.Length; i++)
            {
                var p = entry.Parameters[i];
                if (p.ParameterType == typeof(CancellationToken))
                {
                    values[i] = token;
                }
                else if (args != null && args.TryGetValue(p.Name, out var val) && val.Type != JTokenType.Null)
                {
                    try
                    {
                        values[i] = val.ToObject(p.ParameterType);
                    }
                    catch (Exception ex)
                    {
                        throw new RpcException(RpcErrorCodes.InvalidParams, $"Parameter '{p.Name}': {ex.Message}");
                    }
                }
                else if (p.HasDefaultValue)
                {
                    values[i] = p.DefaultValue;
                }
                else
                {
                    throw new RpcException(RpcErrorCodes.InvalidParams, "Missing required parameter: " + p.Name);
                }
            }

            object result;
            try
            {
                result = entry.Method.Invoke(entry.Instance, values);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }

            if (result == null) return JValue.CreateNull();
            return result as JToken ?? JToken.FromObject(result);
        }
    }
}
