using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record WindowsModernizerSnapshot(
    bool UiBaselineConfigured,
    bool SearchCleanupConfigured,
    bool FileDialogProfileConfigured,
    bool StartupDelayOff,
    bool BattleNetDelayed,
    bool SpotifyDelayed,
    bool MaintenanceEnabled,
    bool LltdioManual,
    int VendorServicesPresent,
    int VendorServicesTuned,
    bool ArmouryDownloadPresent,
    bool ArmouryDownloadDisabled,
    int? ActiveMtu,
    bool RssOverridesPresent);


public enum AdaptiveTweakState
{
    Needed,
    AlreadyOptimal,
    NotApplicable,
    ManualReview
}

public enum AdaptiveImpact
{
    Low,
    Medium,
    High
}

public sealed record AdaptiveTweakFinding(
    string Id,
    string Area,
    string Title,
    string Detail,
    AdaptiveTweakState State,
    bool SafeAutoApply,
    AdaptiveImpact Impact = AdaptiveImpact.Low);

public sealed record AdaptiveScanReport(
    IReadOnlyList<AdaptiveTweakFinding> Findings,
    int Needed,
    int AlreadyOptimal,
    int NotApplicable,
    int ManualReview)
{
    public int Total => Findings.Count;
}

internal sealed class WindowsModernizerBackup
{
    public Dictionary<string,string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
