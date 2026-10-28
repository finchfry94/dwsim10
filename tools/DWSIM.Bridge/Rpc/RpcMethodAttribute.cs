using System;

namespace DWSIM.Bridge.Rpc
{
    /// <summary>
    /// Marks a public instance method as a JSON-RPC method. Its parameters bind by name from the
    /// request's params object; a <see cref="System.Threading.CancellationToken"/> parameter is
    /// filled with the token that a <c>$/cancelRequest</c> for the same id cancels.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RpcMethodAttribute : Attribute
    {
        public string Name { get; }

        public RpcMethodAttribute(string name) { Name = name; }
    }

    /// <summary>An error the client is meant to show: a bad argument, a missing object.</summary>
    public sealed class RpcException : Exception
    {
        public int Code { get; }

        public RpcException(int code, string message) : base(message) { Code = code; }
    }

    public static class RpcErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        /// <summary>The request was valid but the engine refused it, e.g. an unknown object id.</summary>
        public const int EngineError = -32000;

        /// <summary>The request was cancelled by <c>$/cancelRequest</c>.</summary>
        public const int Cancelled = -32800;
    }
}
