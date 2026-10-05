using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class GTweakPrivacyBlockService
{
    private const string BeginMarker = "# BEGIN SPANKYHO GTWEAK MICROSOFT BLOCK";
    private const string EndMarker = "# END SPANKYHO GTWEAK MICROSOFT BLOCK";
    private const string FirewallRule = "Spankyho GTweak Microsoft Telemetry Block";
    private static string HostsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    private static string BlocklistPath => Path.Combine(AppContext.BaseDirectory, "Resources", "GTweakBlocklist.txt");
    private static string IpListPath => Path.Combine(AppContext.BaseDirectory, "Resources", "GTweakTelemetryIps.txt");

    public static bool IsApplied()
    {
        try
        {
            if (!File.Exists(HostsPath)) return false;
            string text = File.ReadAllText(HostsPath);
            return text.Contains(BeginMarker, StringComparison.Ordinal) && text.Contains(EndMarker, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public static async Task SetAppliedAsync(bool enabled, CancellationToken token)
    {
        await UpdateHostsAsync(enabled, token).ConfigureAwait(false);
        await UpdateFirewallAsync(enabled, token).ConfigureAwait(false);
        await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
    }
    private static async Task UpdateHostsAsync(bool enabled, CancellationToken token)
    {
        if (enabled && !File.Exists(BlocklistPath))
            throw new FileNotFoundException("GTweak blocklist resource nebyl nalezen.", BlocklistPath);
        string original = File.Exists(HostsPath) ? await File.ReadAllTextAsync(HostsPath, token).ConfigureAwait(false) : string.Empty;
        string cleaned = RemoveMarkedBlock(original).TrimEnd();
        string result = cleaned;
        if (enabled)
        {
            string block = (await File.ReadAllTextAsync(BlocklistPath, token).ConfigureAwait(false)).Trim();
            result += (result.Length == 0 ? string.Empty : Environment.NewLine + Environment.NewLine) +
                      BeginMarker + Environment.NewLine + block + Environment.NewLine + EndMarker + Environment.NewLine;
        }
        else if (result.Length > 0) result += Environment.NewLine;

        var info = new FileInfo(HostsPath);
        if (info.Exists && info.IsReadOnly) info.IsReadOnly = false;
        string temp = HostsPath + ".spanky.tmp";
        await File.WriteAllTextAsync(temp, result, new UTF8Encoding(false), token).ConfigureAwait(false);
        File.Move(temp, HostsPath, true);
    }

    private static string RemoveMarkedBlock(string text)
    {
        int start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0) return text;
        int end = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0) return text[..start];
        end += EndMarker.Length;
        return text.Remove(start, end - start).TrimEnd('\r', '\n');
    }
    private static async Task UpdateFirewallAsync(bool enabled, CancellationToken token)
    {
        var remove = await ProcessRunner.RunAsync("netsh.exe",
            $"advfirewall firewall delete rule name=\"{FirewallRule}\"", TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        if (!enabled) return;
        if (!File.Exists(IpListPath)) throw new FileNotFoundException("GTweak telemetry IP resource nebyl nalezen.", IpListPath);
        string ips = (await File.ReadAllTextAsync(IpListPath, token).ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(ips)) throw new InvalidDataException("GTweak telemetry IP seznam je prázdný.");
        var add = await ProcessRunner.RunAsync("netsh.exe",
            $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=out action=block remoteip={ips} enable=yes profile=any",
            TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        if (!add.Success) throw new InvalidOperationException("Firewall block rule nebylo možné vytvořit: " + add.StdErr + add.StdOut);
    }
}
