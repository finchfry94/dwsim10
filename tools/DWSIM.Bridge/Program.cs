using System;
using System.Globalization;
using System.IO;
using System.Threading;
using DWSIM.Bridge.Methods;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;

namespace DWSIM.Bridge
{
    static class Program
    {
        static int Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            // stdout carries the protocol and nothing else: the engine writes to Console.Out in
            // places, so it goes to stderr, which the host app shows as the engine log.
            var protocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = false };
            Console.SetOut(Console.Error);

            var pythonPath = Environment.GetEnvironmentVariable("DWSIM_PYTHON_PATH");
            if (!string.IsNullOrEmpty(pythonPath)) DWSIM.GlobalSettings.Settings.PythonPath = pythonPath;

            var registry = new RpcRegistry();
            var server = new RpcServer(registry, protocol);
            BridgeHost.Register(registry, new SessionStore(), server);

            Console.Error.WriteLine("[dwsim-bridge] Loading compounds and property packages...");
            var started = DateTime.UtcNow;
            BridgeHost.WarmUp();
            Console.Error.WriteLine($"[dwsim-bridge] Ready in {(DateTime.UtcNow - started).TotalSeconds:0.0} s, {registry.Names.Count} methods.");
            server.Notify("engine/ready", new JObject { ["methods"] = new JArray(registry.Names) });

            server.Run(new StreamReader(Console.OpenStandardInput()));
            return 0;
        }
    }
}
