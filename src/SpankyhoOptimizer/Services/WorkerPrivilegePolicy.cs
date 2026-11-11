using System;
using System.Collections.Generic;
using System.Security.Principal;

namespace SpankyhoOptimizer.Services;

internal static class WorkerPrivilegePolicy
{
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    internal static readonly HashSet<string> PrivilegedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "service-set",
        "service-set-batch",
        "feature-set",
        "debloat-remove",
        "repair-network",
        "repair-bad-tweaks",
        "default-guard-repair",
        "green-ethernet-disable",
        "sfc",
        "dism-repair",
        "ai-option",
        "tweak-set",
        "tweak-restore",
        "process-run",
        "windows-update-pause",
        "task-set",
        "task-set-batch",
        "usb-power-disable",
        "startup-set",
        "startup-delete",
        "modernizer-adaptive",
        "modernizer-startup-apply",
        "modernizer-startup-restore",
        "modernizer-maintenance-apply",
        "modernizer-maintenance-restore",
        "modernizer-vendor-apply",
        "modernizer-vendor-restore",
        "modernizer-network-optimize",
        "modernizer-network-restore",
        "modernizer-retrim"
    };

    public static bool RequiresElevation(string action) =>
        !string.IsNullOrWhiteSpace(action) && PrivilegedActions.Contains(action);
}
