using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public enum NetCheckSeverity { Healthy, Degraded, Severe }

    public readonly record struct NetCheckResult(NetCheckSeverity Severity, string Reason);

    // Zero-leak system network stability gate for scheduled cloud jobs.
    // Default off; zero external dependencies. Local check inspects OS NIC
    // link/route/packet-error counters + local default gateway (0 bytes leave
    // the LAN). Optional net_check_host accepts bare hostnames, URLs, or host:port.
    internal static class NetCheck
    {
        public static async Task<NetCheckResult> EvaluateAsync(
            string? hostPort,
            int samples,
            int maxRttMs,
            int waitSeconds,
            bool pingGateway,
            ILogger log,
            CancellationToken ct)
        {
            int count = Math.Max(1, samples);
            int maxRtt = Math.Max(50, maxRttMs);
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, waitSeconds));

            while (true)
            {
                var res = await EvaluateOnceAsync(hostPort, count, maxRtt, pingGateway, ct).ConfigureAwait(false);
                if (res.Severity != NetCheckSeverity.Severe || DateTime.UtcNow >= deadline)
                    return res;

                log.Debug($"NetCheck waiting ({res.Reason})...");
                var remaining = deadline - DateTime.UtcNow;
                var delay = remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        public static bool DecideOnResult(
            NetCheckResult result,
            string jobName,
            ILogger log,
            Func<string, bool>? interactivePrompt = null)
        {
            if (result.Severity == NetCheckSeverity.Healthy)
                return true;

            if (result.Severity == NetCheckSeverity.Degraded)
            {
                log.Warn($"NetCheck warning for job '{jobName}': {result.Reason} (continuing).");
                return true;
            }

            log.Warn($"NetCheck severe instability for job '{jobName}': {result.Reason}");
            if (interactivePrompt != null)
                return interactivePrompt(result.Reason);

            if (Environment.UserInteractive && !Console.IsInputRedirected)
                return PromptTerminal(jobName, result.Reason);

            log.Warn($"Job '{jobName}' skipped due to severe network instability (non-interactive).");
            return false;
        }

        private static bool PromptTerminal(string jobName, string reason)
        {
            Console.WriteLine();
            Console.WriteLine($"[TicTack NetCheck] Job '{jobName}' detected severe network instability:");
            Console.WriteLine($"  {reason}");
            Console.Write("Continue anyway? [c]ontinue / [s]kip (auto-skip in 30s): ");
            var readTask = Task.Run(() => Console.ReadLine());
            if (!readTask.Wait(TimeSpan.FromSeconds(30)))
            {
                Console.WriteLine("\nTimed out; skipping job.");
                return false;
            }
            var ans = (readTask.Result ?? string.Empty).Trim().ToLowerInvariant();
            return ans == "c" || ans == "continue" || ans == "y" || ans == "yes";
        }

        private static async Task<NetCheckResult> EvaluateOnceAsync(
            string? hostPort, int samples, int maxRttMs, bool pingGateway, CancellationToken ct)
        {
            var local = await CheckSystemAsync(samples, maxRttMs, pingGateway, ct).ConfigureAwait(false);
            if (local.Severity == NetCheckSeverity.Severe)
                return local;

            if (string.IsNullOrWhiteSpace(hostPort))
                return local;

            var (host, port) = ParseHostPort(hostPort);
            var target = await ProbeTcpAsync(host, port, samples, maxRttMs, ct).ConfigureAwait(false);
            if (target.Severity == NetCheckSeverity.Severe)
                return target;
            return target.Severity == NetCheckSeverity.Degraded ? target : local;
        }

        internal static async Task<NetCheckResult> CheckSystemAsync(
            int samples, int maxRttMs, bool pingGateway, CancellationToken ct)
        {
            var nic = FindPrimaryInterface(out var gateway);
            if (nic == null)
                return new NetCheckResult(NetCheckSeverity.Severe, "no active network interface with a default gateway");

            long prevErrors = GetErrorCounter(nic);
            int pingOk = 0;
            int pingAttempted = 0;
            long worstRtt = 0;

            for (int i = 0; i < samples; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cur = FindInterfaceById(nic.Id);
                if (cur == null || cur.OperationalStatus != OperationalStatus.Up)
                    return new NetCheckResult(NetCheckSeverity.Severe, $"interface '{nic.Name}' dropped during stability check");

                long errNow = GetErrorCounter(cur);
                if (errNow - prevErrors > 5)
                    return new NetCheckResult(NetCheckSeverity.Severe, $"interface '{nic.Name}' packet errors spiked (+{errNow - prevErrors})");
                prevErrors = errNow;

                if (pingGateway && gateway != null)
                {
                    pingAttempted++;
                    try
                    {
                        using var ping = new Ping();
                        var reply = await ping.SendPingAsync(gateway, Math.Max(maxRttMs, 1000)).ConfigureAwait(false);
                        if (reply.Status == IPStatus.Success)
                        {
                            pingOk++;
                            if (reply.RoundtripTime > worstRtt) worstRtt = reply.RoundtripTime;
                        }
                    }
                    catch
                    {
                        // ICMP may be unprivileged/blocked on some systems; handled below.
                    }
                }

                if (i + 1 < samples)
                    await Task.Delay(150, ct).ConfigureAwait(false);
            }

            if (pingAttempted > 0)
            {
                if (pingOk == 0)
                    return new NetCheckResult(NetCheckSeverity.Degraded, $"local gateway {gateway} did not reply to ICMP (ICMP may be blocked; NIC '{nic.Name}' is up)");
                if (pingOk < pingAttempted)
                    return new NetCheckResult(NetCheckSeverity.Severe, $"local gateway {gateway} packet loss ({pingAttempted - pingOk}/{pingAttempted} dropped)");
                if (worstRtt > maxRttMs)
                    return new NetCheckResult(NetCheckSeverity.Degraded, $"local gateway {gateway} high latency ({worstRtt}ms > {maxRttMs}ms)");
            }

            return new NetCheckResult(NetCheckSeverity.Healthy, string.Empty);
        }

        private static async Task<NetCheckResult> ProbeTcpAsync(
            string host, int port, int samples, int maxRttMs, CancellationToken ct)
        {
            int timeoutMs = Math.Max(maxRttMs, 1000);
            int succeeded = 0;
            long worstRtt = 0;
            string lastErr = string.Empty;

            for (int i = 0; i < samples; i++)
            {
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                var sw = Stopwatch.StartNew();
                try
                {
                    await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                    sw.Stop();
                    succeeded++;
                    if (sw.ElapsedMilliseconds > worstRtt) worstRtt = sw.ElapsedMilliseconds;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastErr = $"timeout > {timeoutMs}ms";
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }

                if (i + 1 < samples)
                    await Task.Delay(150, ct).ConfigureAwait(false);
            }

            if (succeeded == 0)
                return new NetCheckResult(NetCheckSeverity.Severe, $"{host}:{port} unreachable ({lastErr})");
            if (succeeded < samples)
                return new NetCheckResult(NetCheckSeverity.Severe, $"{host}:{port} unstable ({samples - succeeded}/{samples} failed: {lastErr})");
            if (worstRtt > maxRttMs)
                return new NetCheckResult(NetCheckSeverity.Degraded, $"{host}:{port} high RTT ({worstRtt}ms > {maxRttMs}ms)");
            return new NetCheckResult(NetCheckSeverity.Healthy, string.Empty);
        }

        private static NetworkInterface? FindPrimaryInterface(out IPAddress? gateway)
        {
            gateway = null;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    var gw = props.GatewayAddresses
                        .Select(g => g.Address)
                        .FirstOrDefault(a => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any));
                    if (gw != null)
                    {
                        gateway = gw;
                        return ni;
                    }
                }
            }
            catch { }
            return null;
        }

        private static NetworkInterface? FindInterfaceById(string id)
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == id);
            }
            catch { return null; }
        }

        private static long GetErrorCounter(NetworkInterface ni)
        {
            try
            {
                var stats = ni.GetIPStatistics();
                return stats.OutgoingPacketsWithErrors + stats.IncomingPacketsDiscarded;
            }
            catch { return 0; }
        }

        internal static (string Host, int Port) ParseHostPort(string input)
        {
            var s = input.Trim();
            int defaultPort = 443;
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(8);
                defaultPort = 443;
            }
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(7);
                defaultPort = 80;
            }

            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);

            int colon = s.LastIndexOf(':');
            if (colon > 0 && colon < s.Length - 1
                && !s.EndsWith(']')
                && int.TryParse(s.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                && port >= 1 && port <= 65535)
            {
                return (s.Substring(0, colon).Trim('[', ']'), port);
            }
            return (s.Trim('[', ']'), defaultPort);
        }
    }
}
