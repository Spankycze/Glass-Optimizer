using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed partial class AiThirdPartyIdleAutomationService
{
    // v16.181: Do not treat the unnamed Run key (Default) value as an
    // autorun entry. Malformed/nonstring/blank named values may be displayed,
    // but native toggles and AI auto-mutation are not permitted.
    // Everything here is an in-memory synthetic fixture.
    internal static bool RunStartupRegistryAdmission181Smoke()
    {
        static bool Classified(string? name, object? raw, bool expectedPresent,
            bool expectedToggle, string expectedCommand)
        {
            bool present = StartupManagerService.TryClassifyRegistryStartupSlot(
                name, raw, out string command, out bool canToggle);
            return present == expectedPresent && canToggle == expectedToggle &&
                command == expectedCommand;
        }
        if (!Classified("Spotify", @"C:\Apps\Spotify\Spotify.exe",
                true, true, @"C:\Apps\Spotify\Spotify.exe") ||
            !Classified("Spotify", "%APPDATA%\\Spotify.exe",
                true, true, "%APPDATA%\\Spotify.exe") ||
            !Classified("", @"C:\Apps\Spotify\Spotify.exe",
                false, false, @"C:\Apps\Spotify\Spotify.exe") ||
            !Classified("   ", @"C:\Apps\Spotify\Spotify.exe",
                false, false, @"C:\Apps\Spotify\Spotify.exe") ||
            !Classified("Spotify", null, false, false, "") ||
            !Classified("Spotify", "", true, false, "") ||
            !Classified("Spotify", "  ", true, false, "  ") ||
            !Classified("Spotify", 17, true, false, "17") ||
            !Classified("Spotify", new byte[] { 1 }, true, false,
                "System.Byte[]"))
            return false;

        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var good = new StartupEntry($"CurrentUser|Default|{run}|Spotify", "Spotify",
            @"C:\Apps\Spotify\Spotify.exe",
            Microsoft.Win32.RegistryHive.CurrentUser, run, true,
            "Spotify", @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var nativeGood = good with
        {
            View = Microsoft.Win32.RegistryView.Registry64,
            Id = $"CurrentUser|Registry64|{run}|Spotify"
        };
        // If the first recognized row is unsupported, choose the second
        // admissible HKCU registry view rather than ending the opportunity.
        var nativeOwner = new Owner("startup", nativeGood.Id, "spotify",
            new DateTime(2026, 10, 26, 17, 0, 0, DateTimeKind.Utc),
            true, false, nativeGood.Name, nativeGood.Command,
            nativeGood.RunPath, (int)nativeGood.View);
        var nativeDisabled = nativeGood with
        {
            Enabled = false,
            Id = $"CurrentUser|Registry64|{run}\\SpankyhoDisabled|Spotify"
        };
        var userChangedNative = nativeGood with
            { Command = nativeGood.Command + " --manual" };
        if (CanCommitOwnedStartup(nativeGood with
                { Hive = Microsoft.Win32.RegistryHive.LocalMachine,
                    Id = $"LocalMachine|Registry64|{run}|Spotify" }) ||
            CanCommitOwnedStartup(nativeGood with
                { RunPath = run + "Once",
                    Id = $"CurrentUser|Registry64|{run}Once|Spotify" }) ||
            CanRestoreOwnedStartup(nativeOwner with
                { StartupCommand = nativeOwner.StartupCommand + " --external" },
                nativeDisabled) ||
            CanRestoreOwnedStartup(nativeOwner with { Id = "foreign|owner" },
                nativeDisabled) ||
            CanRestoreOwnedStartup(nativeOwner,
                nativeDisabled with { Command = nativeDisabled.Command + " --external" }) ||
            CanRestoreOwnedStartup(nativeOwner,
                nativeDisabled with { Name = "Other",
                    Id = $"CurrentUser|Registry64|{run}\\SpankyhoDisabled|Other" }) ||
            !CanCommitOwnedStartup(nativeGood) ||
            !IsStartupDisableConfirmed(nativeOwner, nativeGood,
                new[] { nativeDisabled }, requireNativeAdmission: true) ||
            IsStartupDisableConfirmed(nativeOwner, nativeGood,
                new[] { nativeDisabled with { Id = nativeGood.Id } },
                requireNativeAdmission: true) ||
            IsStartupDisableConfirmed(nativeOwner, nativeGood,
                new[] { nativeDisabled, nativeGood }, requireNativeAdmission: true) ||
            !IsStartupRestoreConfirmed(nativeOwner,
                new[] { nativeGood }, requireNativeAdmission: true) ||
            IsStartupRestoreConfirmed(nativeOwner,
                new[] { nativeGood with { Id = "foreign|id" } },
                requireNativeAdmission: true) ||
            !IsExternallyResolvedStartupWithoutDisabledSlot(nativeOwner,
                new[] { userChangedNative }, requireNativeAdmission: true) ||
            IsExternallyResolvedStartupWithoutDisabledSlot(nativeOwner,
                new[] { userChangedNative with { Id = "foreign|id" } },
                requireNativeAdmission: true) ||
            IsExternallyResolvedStartupWithoutDisabledSlot(nativeOwner,
                new[] { userChangedNative, nativeDisabled },
                requireNativeAdmission: true) ||
            !CanRestoreOwnedStartup(nativeOwner, nativeDisabled) ||
            !ReferenceEquals(nativeDisabled, FindAdmissibleOwnedRestore(
                nativeOwner, new[] { nativeDisabled }, requireNativeAdmission: true)) ||
            CanRestoreOwnedStartup(nativeOwner, nativeDisabled with { Id = nativeGood.Id }) ||
            CanRestoreOwnedStartup(nativeOwner, nativeDisabled with { Id = null! }) ||
            CanRestoreOwnedStartup(nativeOwner, nativeDisabled with { View = Microsoft.Win32.RegistryView.Default }) ||
            CanRestoreOwnedStartup(nativeOwner, nativeDisabled with { Command = " " }) ||
            CanRestoreOwnedStartup(nativeOwner, nativeDisabled with { Enabled = true }) ||
            FindAdmissibleOwnedRestore(nativeOwner,
                new[] { nativeDisabled with { Id = nativeGood.Id } },
                requireNativeAdmission: true) is not null ||
            !HasCurrentAdmissibleStartup("spotify", nativeOwner,
                new[] { nativeGood }, requireNativeAdmission: true) ||
            HasCurrentAdmissibleStartup("spotify", nativeOwner,
                new[] { nativeGood with { Id = "foreign|id" } },
                requireNativeAdmission: true) ||
            HasCurrentAdmissibleStartup("spotify", nativeOwner,
                new[] { nativeGood with { Id = "" } },
                requireNativeAdmission: true) ||
            HasCurrentAdmissibleStartup("spotify", nativeOwner,
                new[] { nativeGood with { Command = " " } },
                requireNativeAdmission: true) ||
            !ReferenceEquals(nativeGood,
                FindAdmissibleStartup("spotify", new[] { good, nativeGood },
                    requireNativeAdmission: true)) ||
            FindAdmissibleStartup("spotify", new[] { good },
                requireNativeAdmission: true) != null ||
            FindAdmissibleStartup("spotify", new[] { nativeGood with
                { Command = " " }, nativeGood },
                requireNativeAdmission: true) != nativeGood)
            return false;
        return AiThirdPartyIdlePolicy.CanManageStartup("spotify", good) &&
            // Native inventories can contain partially unreadable entries.
            // They must never authorize auto-disable or throw during recovery.
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { SourceKind = null! }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { RunPath = null! }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { Name = null! }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { FilePath = null! }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { CanToggle = false }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { Name = "", CanToggle = false }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { Command = "", CanToggle = false });
    }

    // v16.180: a registry value can disappear between enumeration and read.
    // ReadKey must never manufacture a phantom enabled slot in that race;
    // even a legitimately present empty REG_SZ cannot prove external
    // restoration and must not discard an AI-owned durable journal.
    // Read-only synthetic snapshots only; no live registry or services.
    internal static bool RunStartupRegistryRace180Smoke()
    {
        bool missing = StartupManagerService.TryReadPresentRegistryCommand(
            null, out string missingCommand);
        bool empty = StartupManagerService.TryReadPresentRegistryCommand(
            "", out string emptyCommand);
        bool changed = StartupManagerService.TryReadPresentRegistryCommand(
            "\"C:\\Apps\\Spotify\\Spotify.exe\" --manual",
            out string replacementCommand);
        bool whitespace = StartupManagerService.TryReadPresentRegistryCommand(
            "  ", out string whitespaceCommand);
        if (missing || missingCommand != "" || !empty || emptyCommand != "" ||
            !changed || replacementCommand != "\"C:\\Apps\\Spotify\\Spotify.exe\" --manual" ||
            !whitespace || whitespaceCommand != "  ")
            return false;

        DateTime at = new(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string originalCommand = "\"C:\\Apps\\Spotify\\Spotify.exe\"";
        var owner = new Owner("startup", "fixture", "spotify", at,
            true, false, "Spotify", originalCommand, run, 0);
        var enabled = new StartupEntry("fixture", "Spotify", originalCommand,
            Microsoft.Win32.RegistryHive.CurrentUser, run, true, "Spotify",
            @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var blank = enabled with { Command = emptyCommand };
        var spaces = enabled with { Command = whitespaceCommand };
        var replacement = enabled with { Command = replacementCommand };
        var disabled = enabled with { Enabled = false };
        return !IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   Array.Empty<StartupEntry>()) &&
               !IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { blank }) &&
               !IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { spaces }) &&
               !IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { enabled }) &&
               !IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { replacement, disabled }) &&
               IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { replacement }) &&
               IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { blank, replacement });
    }

    // A user-owned Run replacement may legitimately outlive its AI-owned
    // disabled twin (which the user has independently removed). That journal
    // has nothing left to restore and should not block other AI experiments
    // forever. Preserve ownership on ANY ambiguous or still-disabled slot.
    internal static bool RunStartupExternalResolution168Smoke()
    {
        DateTime at = new(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string command = "\"" + @"C:\Apps\Spotify\Spotify.exe" + "\"";
        var owner = new Owner("startup", "fixture", "spotify", at,
            true, false, "Spotify", command, run, 0);
        var disabled = new StartupEntry("fixture", "Spotify", command,
            Microsoft.Win32.RegistryHive.CurrentUser, run, false, "Spotify",
            @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var enabled = disabled with { Enabled = true };
        var changedRun = enabled with { Command = command + " --manual" };
        var changedDisabled = disabled with { Command = command + " --manual" };
        var differentView = changedDisabled with
            { View = Microsoft.Win32.RegistryView.Registry64 };
        var differentHive = changedDisabled with
            { Hive = Microsoft.Win32.RegistryHive.LocalMachine };
        var differentName = changedRun with { Name = "Other" };
        var differentPath = changedRun with { RunPath = run + @"\Other" };
        var differentSource = changedRun with { SourceKind = "TaskScheduler" };
        bool Resolved(params StartupEntry[] rows) =>
            IsExternallyResolvedStartupWithoutDisabledSlot(owner, rows);

        return Resolved(changedRun) &&
            Resolved(changedRun with { Name = "sPoTiFy" }) &&
            Resolved(differentView, changedRun) &&
            Resolved(changedRun, differentHive) &&
            !Resolved(enabled) &&
            !Resolved(disabled) &&
            !Resolved() &&
            !Resolved(differentName) &&
            !Resolved(differentPath) &&
            !Resolved(differentSource) &&
            !Resolved(changedRun, disabled) &&
            !Resolved(disabled, changedRun) &&
            !Resolved(changedDisabled, changedRun) &&
            !Resolved(changedRun, changedDisabled) &&
            !IsExternallyResolvedStartupWithoutDisabledSlot(
                owner with { OriginalEnabled = false }, new[] { changedRun }) &&
            !IsExternallyResolvedStartupWithoutDisabledSlot(
                owner with { Kind = "task" }, new[] { changedRun }) &&
            // Same evidence was previously classified as not restored, and
            // MUST still be blocked by the restorative write candidate gate.
            !IsStartupRestoreConfirmed(owner, new[] { changedRun }) &&
            FindAdmissibleOwnedRestore(owner, new[] { changedRun }) is null &&
            IsStartupRestoreConfirmed(owner, new[] { enabled }) &&
            FindAdmissibleOwnedRestore(owner, new[] { disabled }) is not null;
    }

    // Read-only synthetic verification of the physical registry slot rule:
    // user-edited values occupy the same Run/SpankyhoDisabled destination,
    // while separate registry views and hives must not be conflated.
    internal static bool RunStartupRestoreSlotCollision166Smoke()
    {
        DateTime at = new(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string command = "\"" + @"C:\Apps\Spotify\Spotify.exe" + "\"";
        var owner = new Owner("startup", "fixture", "spotify", at, true, false,
            "Spotify", command, run, 0);
        var disabled = new StartupEntry("fixture", "Spotify", command,
            Microsoft.Win32.RegistryHive.CurrentUser, run, false, "Spotify",
            @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var enabled = disabled with { Enabled = true };
        var edited = disabled with { Command = command + " --new-user-command" };
        var editedCaps = edited with { Name = "sPoTiFy" };
        var otherView = edited with { View = Microsoft.Win32.RegistryView.Registry64 };
        var otherHive = edited with { Hive = Microsoft.Win32.RegistryHive.LocalMachine };
        var otherPath = edited with { RunPath = run + @"\Other" };

        return IsStartupRestoreConfirmed(owner, new[] { enabled }) &&
            FindAdmissibleOwnedRestore(owner, new[] { disabled }) is not null &&
            !IsStartupRestoreConfirmed(owner, new[] { enabled, edited }) &&
            !IsStartupRestoreConfirmed(owner, new[] { edited, enabled }) &&
            !IsStartupRestoreConfirmed(owner, new[] { enabled, editedCaps }) &&
            FindAdmissibleOwnedRestore(owner,
                new[] { disabled, edited with { Enabled = true } }) is null &&
            FindAdmissibleOwnedRestore(owner,
                new[] { editedCaps with { Enabled = true }, disabled }) is null &&
            !IsStartupRestoreConfirmed(owner,
                new[] { edited with { Enabled = true } }) &&
            IsStartupRestoreConfirmed(owner, new[] { enabled, otherView }) &&
            IsStartupRestoreConfirmed(owner, new[] { enabled, otherHive }) &&
            IsStartupRestoreConfirmed(owner, new[] { enabled, otherPath }) &&
            ReferenceEquals(disabled, FindAdmissibleOwnedRestore(
                owner, new[] { disabled, otherView with { Enabled = true } })) &&
            ReferenceEquals(disabled, FindAdmissibleOwnedRestore(
                owner, new[] { otherHive with { Enabled = true }, disabled }));
    }
}
