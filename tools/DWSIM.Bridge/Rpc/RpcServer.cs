using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge.Rpc
{
    /// <summary>Sends JSON-RPC notifications (messages without an id) to the client.</summary>
    public interface INotifier
    {
        void Notify(string method, JToken parameters);
    }

    /// <summary>
    /// Line-delimited JSON-RPC 2.0 over a reader and a writer. Each request runs on the thread pool,
    /// so a long solve does not block a ping or a cancellation; responses and notifications share
    /// one writer under a lock and may therefore arrive out of request order, matched by id.
    /// </summary>
    public sealed class RpcServer : INotifier
    {
        private readonly RpcRegistry _registry;
        private readonly TextWriter _writer;
        private readonly object _writeLock = new object();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending =
            new ConcurrentDictionary<string, CancellationTokenSource>();

        public RpcServer(RpcRegistry registry, TextWriter writer)
        {
            _registry = registry;
            _writer = writer;
        }

        /// <summary>Reads requests until the reader ends, then waits for the ones still running.</summary>
        public void Run(TextReader reader)
        {
            // Starts at one for the reader itself, so it cannot reach zero while lines still come.
            using (var running = new CountdownEvent(1))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var copy = line;
                    running.AddCount();
                    Task.Run(() =>
                    {
                        try
                        {
                            var response = Handle(copy);
                            if (response != null) Write(response);
                        }
                        finally
                        {
                            running.Signal();
                        }
                    });
                }
                running.Signal();
                running.Wait();
            }
        }

        public void Notify(string method, JToken parameters)
        {
            Write(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters ?? new JObject() });
        }

        /// <summary>Handles one request line and returns the response, or null for a notification.</summary>
        public JObject Handle(string line)
        {
            JObject request;
            try
            {
                request = JObject.Parse(line);
            }
            catch (JsonException ex)
            {
                return Error(null, RpcErrorCodes.ParseError, "Parse error: " + ex.Message);
            }

            var id = request["id"];
            var method = request.Value<string>("method");
            if (string.IsNullOrEmpty(method))
                return Error(id, RpcErrorCodes.InvalidRequest, "Invalid request: no method");

            var args = request["params"] as JObject ?? new JObject();

            if (method == "$/cancelRequest")
            {
                if (_pending.TryGetValue(args["id"]?.ToString() ?? "", out var cts)) cts.Cancel();
                return null;
            }

            var key = id?.ToString();
            var source = new CancellationTokenSource();
            if (key != null) _pending[key] = source;
            try
            {
                var result = _registry.Invoke(method, args, source.Token);
                return id == null ? null : new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (OperationCanceledException)
            {
                return id == null ? null : Error(id, RpcErrorCodes.Cancelled, "Request cancelled");
            }
            catch (RpcException ex)
            {
                return id == null ? null : Error(id, ex.Code, ex.Message);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.Collections.Generic.KeyNotFoundException)
            {
                return id == null ? null : Error(id, RpcErrorCodes.EngineError, ex.Message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[dwsim-bridge] " + method + " failed: " + ex);
                return id == null ? null : Error(id, RpcErrorCodes.InternalError, ex.Message);
            }
            finally
            {
                if (key != null) _pending.TryRemove(key, out _);
                source.Dispose();
            }
        }

        private void Write(JObject message)
        {
            var text = message.ToString(Formatting.None);
            lock (_writeLock)
            {
                _writer.WriteLine(text);
                _writer.Flush();
            }
        }

        private static JObject Error(JToken id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message }
            };
        }
    }
}
