using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static class WinhanceUiService
{
    private const string TrayNotify = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify";
    private const string NotifyIcons = @"Control Panel\NotifyIconSettings";
    private const string Intl = @"Control Panel\International";

    internal static Task<bool> AreAllTrayIconsShownAsync(CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var tray = Registry.CurrentUser.OpenSubKey(TrayNotify);
        if (Convert.ToInt32(tray?.GetValue("SystemTrayChevronVisibility") ?? 1) != 0) return false;
        using var root = Registry.CurrentUser.OpenSubKey(NotifyIcons);
        if (root == null) return false;
        foreach (string sub in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(sub);
            if (Convert.ToInt32(key?.GetValue("IsPromoted") ?? 0) != 1) return false;
        }
        return true;
    }, token);
    internal static Task SetAllTrayIconsAsync(bool showAll, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using (var tray = Registry.CurrentUser.CreateSubKey(TrayNotify, true))
            tray.SetValue("SystemTrayChevronVisibility", showAll ? 0 : 1, RegistryValueKind.DWord);
        using var root = Registry.CurrentUser.OpenSubKey(NotifyIcons, true);
        if (root != null)
            foreach (string sub in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(sub, true);
                key?.SetValue("IsPromoted", showAll ? 1 : 0, RegistryValueKind.DWord);
            }
    }, token);

    internal static Dictionary<string, string> ReadRegional()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Intl);
        string Get(string name) => Convert.ToString(key?.GetValue(name)) ?? string.Empty;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sShortDate"] = Get("sShortDate"),
            ["iFirstDayOfWeek"] = Get("iFirstDayOfWeek"),
            ["sDecimal"] = Get("sDecimal"),
            ["sList"] = Get("sList"),
            ["iMeasure"] = Get("iMeasure"),
            ["sMonDecimalSep"] = Get("sMonDecimalSep")
        };
    }
    internal static void WriteRegional(IReadOnlyDictionary<string, string> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Intl, true);
        foreach (var pair in values) key.SetValue(pair.Key, pair.Value, RegistryValueKind.String);
    }
}
