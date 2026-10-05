using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Fixed endpoint documented by Cloudflare's own speedtest library; never changes DNS configuration.
public sealed class AiNetworkWindowsBackend : IAiNetworkRuntimeBackend
{
    public const string ProbeUrl = "https://speed.cloudflare.com/__down?bytes=262144";
    private const int PayloadBytes = 262144;
    private const int Samples = 5;
    private static async Task<string> PowerShellAsync(string command, CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync("powershell.exe",
            "-NoLogo -NoProfile -NonInteractive -Command \"" + command + "\"",
            TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException("TCP provider command failed: " + result.StdErr.Trim());
        return result.StdOut.Trim();
    }

    public async Task<string> ReadSettingAsync(CancellationToken token)
    {
        string value = await PowerShellAsync("$ErrorActionPreference='Stop'; (Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal.ToString()", token).ConfigureAwait(false);
        return value.ToLowerInvariant();
    }

    public async Task ApplySettingAsync(string value, CancellationToken token)
    {
        if (value is not ("normal" or "restricted"))
            throw new InvalidOperationException("Only captured Normal/Restricted Internet autotuning is supported.");
        // Supported scoped Windows provider; no netsh, adapter reset, route, resolver or driver edits.
        await PowerShellAsync("$ErrorActionPreference='Stop'; Set-NetTCPSetting -SettingName Internet -AutoTuningLevelLocal " + value + " -ErrorAction Stop", token).ConfigureAwait(false);
        TcpGlobalStateService.Invalidate();
    }

    public async Task<AiNetworkProbeResult> MeasureAsync(CancellationToken token)
    {
        DateTime started = DateTime.UtcNow;
        var transfers = new List<double>();
        var connects = new List<double>();
        string context = "";
        double ambient = 0, cpu = double.NaN, loss = double.NaN;
        NetworkInterface? nic = null;
        long beforeTraffic = 0, bodyBytes = 0;
        var phaseClock = Stopwatch.StartNew();
        CpuTimes beforeCpu = ReadCpu();
        try
        {
            IPAddress remote = (await Dns.GetHostAddressesAsync("speed.cloudflare.com", token).ConfigureAwait(false))
                .First(x => x.AddressFamily == AddressFamily.InterNetwork);
            string power = ReadPower();
            for (int i = 0; i < Samples; i++)
            {
                token.ThrowIfCancellationRequested();
                double connectMs = 0, providerMs = 0;
                using var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false, UseProxy = false,
                    AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(3),
                    ConnectCallback = async (connection, cancellation) =>
                    {
                        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            var connectClock = Stopwatch.StartNew();
                            await socket.ConnectAsync(new IPEndPoint(remote, 443), cancellation).ConfigureAwait(false);
                            connectMs = connectClock.Elapsed.TotalMilliseconds;
                            var local = (IPEndPoint)socket.LocalEndPoint!;
                            NetworkInterface currentNic = NetworkInterface.GetAllNetworkInterfaces().First(n =>
                                n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local.Address)));
                            string currentContext = Context(currentNic, local.Address, remote, power);
                            if (context.Length != 0 && context != currentContext) throw new InvalidOperationException("Network adapter/route changed during probe");
                            if (nic == null)
                            {
                                nic = currentNic;
                                context = currentContext;
                                beforeTraffic = Traffic(nic);
                                var quietClock = Stopwatch.StartNew();
                                await Task.Delay(300, cancellation).ConfigureAwait(false);
                                ambient = (Traffic(nic) - beforeTraffic) / quietClock.Elapsed.TotalSeconds;
                                if (ambient > 49152) throw new InvalidOperationException("Active network traffic; defer bounded probe");
                                beforeTraffic = Traffic(nic);
                            }
                            // Verify the measured connection actually uses the profile being tested.
                            if (i == 0)
                            {
                                var providerClock = Stopwatch.StartNew();
                                string applied = await PowerShellAsync("(Get-NetTCPConnection -LocalPort " + local.Port +
                                    " -RemotePort 443 -State Established -ErrorAction Stop | Select-Object -First 1).AppliedSetting.ToString()", cancellation).ConfigureAwait(false);
                                providerMs = providerClock.Elapsed.TotalMilliseconds + 300;
                                if (!applied.Equals("Internet", StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException("Probe uses " + applied + " TCP profile; Internet trial deferred");
                            }
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { socket.Dispose(); throw; }
                    }
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
                using var request = new HttpRequestMessage(HttpMethod.Get, ProbeUrl)
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.ConnectionClose = true;
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                requestBudget.CancelAfter(TimeSpan.FromSeconds(7));
                var transferClock = Stopwatch.StartNew();
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestBudget.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength != PayloadBytes ||
                    response.Headers.Age.GetValueOrDefault() > TimeSpan.Zero || response.Content.Headers.ContentEncoding.Count != 0)
                    throw new InvalidOperationException("Fixed fresh payload unavailable; HTTP " + (int)response.StatusCode);
                using Stream stream = await response.Content.ReadAsStreamAsync(requestBudget.Token).ConfigureAwait(false);
                byte[] buffer = new byte[16384];
                int received = 0;
                while (received <= PayloadBytes)
                {
                    int count = await stream.ReadAsync(buffer, requestBudget.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                }
                if (received != PayloadBytes) throw new InvalidOperationException("HTTP payload length mismatch");
                bodyBytes += received;
                transfers.Add(Math.Max(.01, transferClock.Elapsed.TotalMilliseconds - providerMs));
                connects.Add(Math.Max(.01, connectMs));
                if (ReadPower() != power) throw new InvalidOperationException("Power mode changed during probe");
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            // ICMP loss is context only: HTTP transfer time is the TCP benefit metric.
            int pingReceived = 0;
            for (int i = 0; i < 5; i++)
            {
                using var ping = new Ping();
                try
                {
                    PingReply reply = await ping.SendPingAsync(remote, 400).WaitAsync(token).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success) pingReceived++;
                }
                catch (PingException) { }
            }
            loss = 100d * (5 - pingReceived) / 5;
            cpu = CpuPercent(beforeCpu, ReadCpu());
            if (nic != null)
            {
                // A conservative subtraction allows protocol overhead to count as ambient traffic.
                ambient = Math.Max(ambient, Math.Max(0, Traffic(nic) - beforeTraffic - bodyBytes) / phaseClock.Elapsed.TotalSeconds);
                string afterContext = Context(nic, nic.GetIPProperties().UnicastAddresses.First(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Address, remote, ReadPower());
                if (context != afterContext) throw new InvalidOperationException("Connectivity context changed");
            }
            return new(started, context, true, PayloadBytes, transfers.ToArray(), connects.ToArray(), loss, cpu, ambient, "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(started, context, false, PayloadBytes, transfers.ToArray(), connects.ToArray(),
                double.IsFinite(loss) ? loss : 100, double.IsFinite(cpu) ? cpu : 100, ambient, ex.GetBaseException().Message);
        }
    }

    private static long Traffic(NetworkInterface n)
    {
        IPInterfaceStatistics stats = n.GetIPStatistics();
        return stats.BytesReceived + stats.BytesSent;
    }
    private static string Context(NetworkInterface n, IPAddress local, IPAddress remote, string power)
    {
        var props = n.GetIPProperties();
        return string.Join("|", n.Id, local, remote, n.OperationalStatus, n.Speed, props.GetIPv4Properties()?.Mtu,
            string.Join(",", props.GatewayAddresses.Select(x => x.Address).OrderBy(x => x.ToString())),
            string.Join(",", props.DnsAddresses.OrderBy(x => x.ToString())), power);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CpuTimes { public ulong Idle, Kernel, User; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    private static CpuTimes ReadCpu()
    {
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) throw new InvalidOperationException("CPU context unavailable");
        return new() { Idle = idle, Kernel = kernel, User = user };
    }
    private static double CpuPercent(CpuTimes a, CpuTimes b)
    {
        ulong total = b.Kernel - a.Kernel + b.User - a.User;
        return total == 0 ? double.NaN : 100d * (total - (b.Idle - a.Idle)) / total;
    }
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr active);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    private static string ReadPower()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr active) != 0) throw new InvalidOperationException("Power context unavailable");
        try { return Marshal.PtrToStructure<Guid>(active).ToString("D"); }
        finally { LocalFree(active); }
    }
}
