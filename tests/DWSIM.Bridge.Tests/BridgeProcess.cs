using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>
    /// The engine sidecar as the desktop app runs it: a separate dwsim-bridge process, spoken to in
    /// JSON-RPC over its stdin and stdout. Nothing of the engine is loaded in the test process.
    /// </summary>
    public sealed class BridgeProcess : IDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> _pending =
            new ConcurrentDictionary<long, TaskCompletionSource<JObject>>();
        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>();
        private long _nextId;

        public BridgeProcess()
        {
            var dll = Locate();
            _process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet", "\"" + dll + "\"")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            _process.Start();
            _process.ErrorDataReceived += (_, e) => { };
            _process.BeginErrorReadLine();
            new Thread(ReadLoop) { IsBackground = true, Name = "bridge stdout" }.Start();
            if (!_ready.Task.Wait(TimeSpan.FromMinutes(3)))
                throw new TimeoutException("dwsim-bridge did not report engine/ready");
        }

        /// <summary>The bridge built beside these tests, in the same configuration.</summary>
        private static string Locate()
        {
            var testDir = TestContext.CurrentContext.TestDirectory;
            var config = testDir.Contains(Path.DirectorySeparatorChar + "Debug" + Path.DirectorySeparatorChar) ? "Debug" : "Release";
            var folder = testDir;
            while (folder != null && !Directory.Exists(Path.Combine(folder, "tools", "DWSIM.Bridge")))
                folder = Path.GetDirectoryName(folder);
            Assert.That(folder, Is.Not.Null, "could not find tools/DWSIM.Bridge");
            var dll = Path.Combine(folder, "tools", "DWSIM.Bridge", "bin", config, "net10.0", "dwsim-bridge.dll");
            Assert.That(File.Exists(dll), Is.True, dll + " is not built");
            return dll;
        }

        private void ReadLoop()
        {
            string line;
            while ((line = _process.StandardOutput.ReadLine()) != null)
            {
                var message = JObject.Parse(line);
                if (message["method"]?.ToString() == "engine/ready") _ready.TrySetResult(true);
                var id = message["id"];
                if (id != null && id.Type == JTokenType.Integer && _pending.TryRemove(id.Value<long>(), out var waiter))
                    waiter.TrySetResult(message);
            }
            _ready.TrySetException(new IOException("dwsim-bridge exited"));
            foreach (var waiter in _pending.Values) waiter.TrySetException(new IOException("dwsim-bridge exited"));
        }

        /// <summary>Calls a method and returns its result; an engine error becomes an exception.</summary>
        public JToken Call(string method, object parameters = null)
        {
            var id = Interlocked.Increment(ref _nextId);
            var waiter = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiter;
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters == null ? new JObject() : JObject.FromObject(parameters)
            };
            lock (_process)
            {
                _process.StandardInput.WriteLine(request.ToString(Newtonsoft.Json.Formatting.None));
                _process.StandardInput.Flush();
            }
            if (!waiter.Task.Wait(TimeSpan.FromMinutes(5))) throw new TimeoutException(method + " did not answer");
            var response = waiter.Task.Result;
            if (response["error"] is JObject error)
                throw new InvalidOperationException($"{method}: {error["message"]} ({error["code"]})");
            return response["result"];
        }

        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(10_000)) _process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
            _process.Dispose();
        }
    }
}
