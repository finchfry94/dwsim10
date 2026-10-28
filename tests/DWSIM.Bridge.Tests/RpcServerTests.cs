using System.IO;
using System.Threading;
using DWSIM.Bridge.Rpc;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DWSIM.Bridge.Tests
{
    /// <summary>The protocol layer alone, with a stub method class and no engine.</summary>
    public class RpcServerTests
    {
        private sealed class Stub
        {
            [RpcMethod("math.add")]
            public double Add(double a, double b = 1) => a + b;

            [RpcMethod("wait")]
            public JObject Wait(CancellationToken token)
            {
                token.WaitHandle.WaitOne(5000);
                token.ThrowIfCancellationRequested();
                return new JObject();
            }

            [RpcMethod("fail")]
            public void Fail() => throw new System.ArgumentException("bad input");
        }

        private static RpcServer Server(out StringWriter output)
        {
            var registry = new RpcRegistry();
            registry.Register(new Stub());
            output = new StringWriter();
            return new RpcServer(registry, output);
        }

        [Test]
        public void BindsParametersByNameAndAppliesDefaults()
        {
            var server = Server(out _);
            var response = server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"math.add\",\"params\":{\"a\":2}}");
            Assert.That(response["result"].Value<double>(), Is.EqualTo(3));
            Assert.That(response["id"].Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void ReportsProtocolErrorsWithStandardCodes()
        {
            var server = Server(out _);
            Assert.That(server.Handle("{not json")["error"]["code"].Value<int>(), Is.EqualTo(RpcErrorCodes.ParseError));
            Assert.That(server.Handle("{\"id\":2,\"method\":\"nope\"}")["error"]["code"].Value<int>(), Is.EqualTo(RpcErrorCodes.MethodNotFound));
            Assert.That(server.Handle("{\"id\":3,\"method\":\"math.add\"}")["error"]["code"].Value<int>(), Is.EqualTo(RpcErrorCodes.InvalidParams));
            Assert.That(server.Handle("{\"id\":4,\"method\":\"fail\"}")["error"]["message"].Value<string>(), Is.EqualTo("bad input"));
        }

        [Test]
        public void NotificationsGetNoResponse()
        {
            var server = Server(out _);
            Assert.That(server.Handle("{\"method\":\"math.add\",\"params\":{\"a\":1}}"), Is.Null);
        }

        [Test]
        public void CancelRequestStopsARunningMethod()
        {
            var server = Server(out _);
            JObject response = null;
            var worker = new Thread(() => response = server.Handle("{\"id\":\"w\",\"method\":\"wait\"}"));
            worker.Start();
            Thread.Sleep(200);
            server.Handle("{\"method\":\"$/cancelRequest\",\"params\":{\"id\":\"w\"}}");
            Assert.That(worker.Join(3000), Is.True, "the method did not return after cancellation");
            Assert.That(response["error"]["code"].Value<int>(), Is.EqualTo(RpcErrorCodes.Cancelled));
        }

        [Test]
        public void RunAnswersEveryLineThenReturnsWhenInputEnds()
        {
            var server = Server(out var output);
            server.Run(new StringReader(
                "{\"id\":1,\"method\":\"math.add\",\"params\":{\"a\":1,\"b\":2}}\n\n" +
                "{\"id\":2,\"method\":\"math.add\",\"params\":{\"a\":5}}\n"));
            var lines = output.ToString().Trim().Split('\n');
            Assert.That(lines, Has.Length.EqualTo(2));
        }
    }
}
