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

    // Preserve the Windows overflow menu. A newly registered icon can be
    // hidden until promoted; removing the chevron makes it inaccessible.
    internal static int DesiredChevronVisibility => 1;

    internal static bool NeedsPromotionUpdate(int currentValue, bool showAll) =>
        currentValue != (showAll ? 1 : 0);

    internal static Task<bool> AreAllTrayIconsShownAsync(CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        // Only per-icon promotion represents the requested tweak state.
        // Do not require disabling the chevron: the overflow is a safety net
        // for icons that register after this command runs.
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
        {
            // On/off never removes access to hidden/new icons.
            if (Convert.ToInt32(tray.GetValue("SystemTrayChevronVisibility") ?? -1)
                != DesiredChevronVisibility)
                tray.SetValue("SystemTrayChevronVisibility",
                    DesiredChevronVisibility, RegistryValueKind.DWord);
        }
        using var root = Registry.CurrentUser.OpenSubKey(NotifyIcons, true);
        if (root != null)
            foreach (string sub in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(sub, true);
                if (key != null &&
                    NeedsPromotionUpdate(Convert.ToInt32(key.GetValue("IsPromoted") ?? -1), showAll))
                    key.SetValue("IsPromoted", showAll ? 1 : 0, RegistryValueKind.DWord);
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
