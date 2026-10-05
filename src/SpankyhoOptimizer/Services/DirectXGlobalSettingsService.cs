using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static class DirectXGlobalSettingsService
{
    private const string Path = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string Name = "DirectXUserGlobalSettings";
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _snapshot;
    private static DateTime _snapshotAt;
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(10);

    internal static async Task<bool> GetAsync(string key, string expected, CancellationToken token = default)
    {
        var map = await GetSnapshotAsync(token).ConfigureAwait(false);
        return map.TryGetValue(key, out var value) && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<Dictionary<string, string>> GetSnapshotAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (_snapshot != null && DateTime.UtcNow - _snapshotAt < SnapshotTtl)
                return Task.FromResult(_snapshot);

            using var reg = Registry.CurrentUser.OpenSubKey(Path);
            var snapshot = Parse(Convert.ToString(reg?.GetValue(Name)) ?? string.Empty);
            _snapshot = snapshot;
            _snapshotAt = DateTime.UtcNow;
            return Task.FromResult(snapshot);
        }
    }

    internal static Task SetAsync(string key, string value, bool enabled, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var reg = Registry.CurrentUser.CreateSubKey(Path, true);
        var map = Parse(Convert.ToString(reg?.GetValue(Name)) ?? string.Empty);
        if (enabled) map[key] = value; else map.Remove(key);
        string text = string.Join("", map.Select(x => $"{x.Key}={x.Value};"));
        if (string.IsNullOrEmpty(text)) reg.DeleteValue(Name, false); else reg.SetValue(Name, text, RegistryValueKind.String);
        lock (Gate)
        {
            _snapshot = map;
            _snapshotAt = DateTime.UtcNow;
        }
    }, token);
    private static Dictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int p = part.IndexOf('=');
            if (p <= 0) continue;
            map[part[..p]] = part[(p + 1)..];
        }
        return map;
    }
}
