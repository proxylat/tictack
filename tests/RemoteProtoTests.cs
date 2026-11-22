using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace TicTack.Tests
{
    public class RemoteProtoTests
    {
        [Fact]
        public void DecodeHeader_RejectsInvalidMagicAndOversizedFrame()
        {
            var badMagic = new byte[RemoteProto.HeaderBytes];
            Assert.Throws<InvalidDataException>(() => RemoteProto.DecodeHeader(badMagic));

            var oversized = new byte[RemoteProto.HeaderBytes];
            RemoteProto.EncodeHeader(oversized, (uint)RemoteOp.Ok, RemoteProto.MaxFrameBytes + 1);
            Assert.Throws<InvalidDataException>(() => RemoteProto.DecodeHeader(oversized));
        }

        [Fact]
        public async Task SendGather_MultiSegment_RoundTripsOverTcp()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverTask = Task.Run(() =>
            {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                return RemoteProto.ReadFrame(stream);
            });

            using (var tcp = new TcpClient())
            {
                tcp.Connect(IPAddress.Loopback, port);
                var p1 = Encoding.UTF8.GetBytes("hello-");
                var p2 = Encoding.UTF8.GetBytes("gather-world");
                RemoteProto.SendFrame(tcp.GetStream(), tcp.Client, (uint)RemoteOp.Ok, p1, p2, 0, p2.Length);
            }

            var (op, payload) = await serverTask;
            Assert.Equal((uint)RemoteOp.Ok, op);
            Assert.Equal("hello-gather-world", Encoding.UTF8.GetString(payload));
        }

        [Fact]
        public async Task TlsAndPskAuth_SucceedsOnMatch_AndRejectsWrongPsk()
        {
            using var cert = RemoteProto.CreateEphemeralCert();
            var serverPsk = RemoteProto.ResolvePsk("correct-horse-battery", null, null);
            var wrongPsk = RemoteProto.ResolvePsk("wrong-key", null, null);

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            // 1. Valid PSK succeeds over TLS
            var s1 = Task.Run(() =>
            {
                using var c = listener.AcceptTcpClient();
                using var ssl = RemoteProto.WrapServerTls(c.GetStream(), cert);
                RemoteProto.AuthServer(ssl, null, serverPsk);
            });

            using (var c1 = new TcpClient())
            {
                c1.Connect(IPAddress.Loopback, port);
                using var ssl = RemoteProto.WrapClientTls(c1.GetStream(), "127.0.0.1");
                RemoteProto.AuthClient(ssl, null, serverPsk);
            }
            await s1;

            // 2. Wrong PSK fails closed
            var s2 = Task.Run(() =>
            {
                using var c = listener.AcceptTcpClient();
                using var ssl = RemoteProto.WrapServerTls(c.GetStream(), cert);
                Assert.Throws<AuthenticationException>(() => RemoteProto.AuthServer(ssl, null, serverPsk));
            });

            using (var c2 = new TcpClient())
            {
                c2.Connect(IPAddress.Loopback, port);
                using var ssl = RemoteProto.WrapClientTls(c2.GetStream(), "127.0.0.1");
                Assert.ThrowsAny<Exception>(() => RemoteProto.AuthClient(ssl, null, wrongPsk));
            }
            await s2;
        }
    }
}
