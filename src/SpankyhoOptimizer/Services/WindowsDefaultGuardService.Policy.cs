using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static partial class WindowsDefaultGuardService
{
    /// <summary>
    /// Isolated filesystem-only regression. Forces a real file-sharing collision,
    /// then concurrent appends. Never accesses the live repair journal or registry.
    /// Cleans up its own temporary folder in all cases.
    /// </summary>
    public static string RunJournalContentionSmoke()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "SpankyGuardJournalSmoke-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "isolated.jsonl");
        Directory.CreateDirectory(directory);
        try
        {
            Task blockedWriter;
            using (var existing = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read))
            {
                using var starting = new ManualResetEventSlim(false);
                blockedWriter = Task.Run(() =>
                {
                    starting.Set();
                    AppendJournalLine(path, "{\"index\":0}", CancellationToken.None);
                });
                if (!starting.Wait(TimeSpan.FromSeconds(2)))
                    throw new IOException("Journal contention test writer did not start.");
                Thread.Sleep(100); // Force a live read-only-shared writer collision.
            }
            blockedWriter.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();

            // Cancelled PREPARE must not create a journal record, even without
            // a sharing collision; the actual repair checks cancellation
            // immediately after preparing and before touching the registry.
            string cancelledPath = Path.Combine(directory, "cancelled-prepared.jsonl");
            using (var alreadyCancelled = new CancellationTokenSource())
            {
                alreadyCancelled.Cancel();
                bool stopped = false;
                try { AppendJournalLine(cancelledPath, "{\"Phase\":\"Prepared\"}", alreadyCancelled.Token); }
                catch (OperationCanceledException) { stopped = true; }
                if (!stopped || File.Exists(cancelledPath))
                    throw new IOException("Cancelled Prepared journal unexpectedly wrote a record.");
            }

            var parallel = Enumerable.Range(1, 8).Select(i => Task.Run(() =>
                AppendJournalLine(path,
                    JsonSerializer.Serialize(new { index = i }), CancellationToken.None))).ToArray();
            Task.WhenAll(parallel).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

            // Cancellation while another process holds the journal must NOT
            // append a late line after the user cancels the operation.
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            using (var cancelled = new CancellationTokenSource())
            using (var started = new ManualResetEventSlim(false))
            {
                Task attempt = Task.Run(() =>
                {
                    started.Set();
                    AppendJournalLine(path, "{\"index\":999}", cancelled.Token);
                });
                if (!started.Wait(TimeSpan.FromSeconds(2)))
                    throw new IOException("Cancelled journal writer did not start.");
                Thread.Sleep(60);
                cancelled.Cancel();
                bool rejected = false;
                try { attempt.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { rejected = true; }
                if (!rejected)
                    throw new IOException("Cancelled journal write unexpectedly completed.");
            }

            string[] lines = File.ReadAllLines(path);
            if (lines.Length != 9)
                throw new IOException("Journal contention test lost or duplicated entries.");
            var ids = new HashSet<int>();
            foreach (string line in lines)
            {
                using var json = JsonDocument.Parse(line);
                if (!ids.Add(json.RootElement.GetProperty("index").GetInt32()))
                    throw new IOException("Journal contention test produced a duplicate entry.");
            }
            if (ids.Count != 9 || !ids.Contains(0) || !ids.Contains(8))
                throw new IOException("Journal contention test did not preserve all writers.");

            // Regression for independent Guard processes saving different
            // approvals concurrently. Only a disposable test file is used.
            string consentPath = Path.Combine(directory, "isolated-consents.json");
            var consentSaves = AvailableGroups.Select(g => Task.Run(() =>
                WriteApprovalAtomically(consentPath, g.Id, approved: true))).ToArray();
            Task.WhenAll(consentSaves).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var approvedGroups = ReadApprovals(consentPath);
            if (approvedGroups.Count != AvailableGroups.Length ||
                AvailableGroups.Any(group => !approvedGroups.Contains(group.Id)))
                throw new IOException("Concurrent consent writes lost an approved group.");
            WriteApprovalAtomically(consentPath, "gpu-tdr", approved: false);
            approvedGroups = ReadApprovals(consentPath);
            if (approvedGroups.Count != AvailableGroups.Length - 1 || approvedGroups.Contains("gpu-tdr") ||
                Directory.GetFiles(directory, "isolated-consents.json.*.new").Length != 0)
                throw new IOException("Atomic consent revocation or temporary cleanup failed.");

            // An in-flight repair holds the same exclusive lease as the user
            // consent writer. Simulate a journaled repair with a disposable
            // consent file: an independent writer must NOT revoke its approval
            // in between final verification and completion of the operation.
            WriteApprovalAtomically(consentPath, "gpu-tdr", approved: true);
            Task revoke;
            using (var repairLease = AcquireApprovalFileLock(consentPath + ".lock"))
            using (var started = new ManualResetEventSlim(false))
            {
                if (!ReadApprovals(consentPath).Contains("gpu-tdr"))
                    throw new IOException("Repair consent precheck unexpectedly missing.");
                revoke = Task.Run(() =>
                {
                    started.Set();
                    WriteApprovalAtomically(consentPath, "gpu-tdr", approved: false);
                });
                if (!started.Wait(TimeSpan.FromSeconds(2)))
                    throw new IOException("Concurrent approval revoker did not start.");
                Thread.Sleep(80);
                if (revoke.IsCompleted || !ReadApprovals(consentPath).Contains("gpu-tdr"))
                    throw new IOException("Approval was revoked during simulated repair lease.");
            }
            revoke.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            if (ReadApprovals(consentPath).Contains("gpu-tdr") ||
                ReadApprovals(consentPath).Count != AvailableGroups.Length - 1)
                throw new IOException("Revocation was not applied after repair lease release.");

            // Navigation/cycle cancellation must abort even when an unrelated
            // process is holding the approval file lease. A cancelled Guard
            // must not wait through all twelve contention retries.
            string cancelLockPath = Path.Combine(directory, "cancelled-approval.lock");
            using (var blocker = AcquireApprovalFileLock(cancelLockPath))
            using (var cancellation = new CancellationTokenSource())
            using (var entered = new ManualResetEventSlim(false))
            {
                var waiter = Task.Run(() =>
                {
                    entered.Set();
                    try
                    {
                        using var unexpected = AcquireApprovalFileLock(
                            cancelLockPath, cancellation.Token);
                        throw new IOException("Cancelled Guard unexpectedly acquired consent lease.");
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                });
                if (!entered.Wait(TimeSpan.FromSeconds(2)))
                    throw new IOException("Guard lock cancellation regression did not start.");
                Thread.Sleep(60);
                cancellation.Cancel();
                waiter.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            // New single-switch Registry Guard must save all seven registry
            // approvals atomically without touching the Network/NIC consent.
            string registryTogglePath = Path.Combine(directory, "registry-master.json");
            WriteApprovalAtomically(registryTogglePath, "green-ethernet", approved: true);
            WriteRegistryApprovalsAtomically(registryTogglePath, enabled: true);
            var allEnabled = ReadApprovalsForWrite(registryTogglePath);
            if (allEnabled.Count != AvailableGroups.Length ||
                AvailableGroups.Any(group => !allEnabled.Contains(group.Id)))
                throw new IOException("Registry master failed to atomically enable seven approvals.");
            WriteRegistryApprovalsAtomically(registryTogglePath, enabled: false);
            var allDisabled = ReadApprovalsForWrite(registryTogglePath);
            if (allDisabled.Count != 1 || !allDisabled.Contains("green-ethernet"))
                throw new IOException("Registry master removed NIC consent or left an approved registry.");
            WriteRegistryApprovalsAtomically(registryTogglePath, enabled: false);
            if (Directory.GetFiles(directory, "registry-master.json.*.new").Length != 0)
                throw new IOException("Registry master left temporary files behind.");
            File.WriteAllText(registryTogglePath, "{invalid json");
            bool refusedBulk = false;
            try { WriteRegistryApprovalsAtomically(registryTogglePath, enabled: true); }
            catch (InvalidDataException) { refusedBulk = true; }
            if (!refusedBulk || File.ReadAllText(registryTogglePath) != "{invalid json")
                throw new IOException("Registry master overwrote corrupt existing approvals.");

            // Corrupted JSON, explicit null and a future-version group must be
            // visible to the read-only monitor as unapproved, but never be
            // overwritten by a seemingly fresh/empty consent write.
            string unsafePath = Path.Combine(directory, "unreadable-consents.json");
            foreach (string unsafeContent in new[]
            {
                "{not valid json", "null", "[\"gpu-tdr\",\"future-version-group\"]"
            })
            {
                File.WriteAllText(unsafePath, unsafeContent);
                bool rejected = false;
                try { WriteApprovalAtomically(unsafePath, "service-shutdown", approved: true); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected || File.ReadAllText(unsafePath) != unsafeContent ||
                    Directory.GetFiles(directory, "unreadable-consents.json.*.new").Length != 0)
                    throw new IOException("Invalid or newer-version consent was overwritten.");
            }
            if (ReadApprovals(unsafePath).Contains("future-version-group"))
                throw new IOException("Read-only consent granted an unknown future group.");
            // Another process can temporarily deny reads of the actual JSON.
            // A failed read must not be mistaken for the first-ever approval.
            const string lockedContent = "[\"gpu-tdr\"]";
            File.WriteAllText(unsafePath, lockedContent);
            using (var unavailable = new FileStream(unsafePath, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None))
            {
                bool refused = false;
                try { WriteApprovalAtomically(unsafePath, "service-shutdown", approved: true); }
                catch (IOException) { refused = true; }
                if (!refused)
                    throw new IOException("Locked existing consent was treated as missing.");
            }
            if (File.ReadAllText(unsafePath) != lockedContent)
                throw new IOException("Temporarily unreadable consent was modified.");
            return "DEFAULT_GUARD_JOURNAL_CONTENTION_OK entries=9 collisions=protected consentWrites=8/8 invalidConsentPreserved=4 liveRegistryWrites=0";
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Info("default-guard", "Temporary journal test cleanup skipped: " +
                    ex.GetType().Name);
            }
        }
    }

    // Pure regression: synthetic objects only, no registry, filesystem or services.
    public static string RunPolicySmoke()
    {
        // A cancelled AI navigation must skip even the first registry/NIC probe.
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            bool stopped = false;
            try { _ = ScanReadOnly(cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            if (!stopped)
                throw new InvalidOperationException("Cancelled AI registry scan did not stop before probing.");
            stopped = false;
            try { _ = NativeNicTuningService.ReadRealtekGreenEthernetStatus(cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            if (!stopped)
                throw new InvalidOperationException("Cancelled Green Ethernet probe did not stop before discovery.");
        }
        var byName = Rules.ToDictionary(r => r.Name, StringComparer.Ordinal);
        if (ServiceShutdownDefaultMs != 5000 ||
            ServiceShutdownDefaultText != ServiceShutdownDefaultMs.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Windows shutdown timeout default consistency failed.");
        if (!ShouldRestore(byName["AutoEndTasks"], "1", RegistryValueKind.String) ||
            !ShouldRestore(byName["AutoEndTasks"], "0", RegistryValueKind.String) ||
            ShouldRestore(byName["AutoEndTasks"], null, null) ||
            ShouldRestore(byName["AutoEndTasks"], 1, RegistryValueKind.DWord) ||
            ShouldRestore(byName["HungAppTimeout"], new byte[] { 1, 2 }, RegistryValueKind.Binary) ||
            !ShouldRestore(byName["HungAppTimeout"], "2000", RegistryValueKind.String) ||
            !ShouldRestore(byName["HungAppTimeout"], "10000", RegistryValueKind.String) ||
            !ShouldRestore(byName["WaitToKillServiceTimeout"], "2000", RegistryValueKind.String) ||
            ShouldRestore(byName["WaitToKillServiceTimeout"], "5000", RegistryValueKind.String) ||
            ShouldRestore(byName["WaitToKillServiceTimeout"], 2000, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["TdrDelay"], 10, RegistryValueKind.DWord) ||
            ShouldRestore(byName["TdrDelay"], null, null) ||
            ShouldRestore(byName["TdrDelay"], "10", RegistryValueKind.String) ||
            ShouldRestore(byName["TdrDelay"], new byte[] { 1, 2 }, RegistryValueKind.Binary) ||
            !ShouldRestore(byName["GameDVR_FSEBehaviorMode"], 2, RegistryValueKind.DWord) ||
            ShouldRestore(byName["GameDVR_FSEBehaviorMode"], "2", RegistryValueKind.String) ||
            ShouldRestore(byName["GameDVR_FSEBehaviorMode"], 0, RegistryValueKind.DWord) ||
            ShouldRestore(byName["GPU Priority"], 8, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["GPU Priority"], 16, RegistryValueKind.DWord) ||
            ShouldRestore(byName["DisablePagingExecutive"], 0, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["DisablePagingExecutive"], 1, RegistryValueKind.DWord) ||
            ShouldRestore(byName["LargeSystemCache"], 0, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["LargeSystemCache"], 1, RegistryValueKind.DWord) ||
            ShouldRestore(byName["SystemResponsiveness"], 20, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["SystemResponsiveness"], 10, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["SvcHostSplitThresholdInKB"], -1, RegistryValueKind.DWord) ||
            ShouldRestore(byName["SvcHostSplitThresholdInKB"], 0x380000, RegistryValueKind.DWord) ||
            ShouldRestore(byName["SvcHostSplitThresholdInKB"], null, null) ||
            ShouldRestore(byName["SvcHostSplitThresholdInKB"], "4294967295", RegistryValueKind.String))
            throw new InvalidOperationException("Windows default guard decision policy failed.");

        // Read-only UI must not mislabel an inaccessible registry key as default.
        if (FormatReadOnlyValue(byName["SvcHostSplitThresholdInKB"], false, null, null) != "(nezjištěno)" ||
            FormatReadOnlyValue(byName["SvcHostSplitThresholdInKB"], true, null, null) != "(Windows default)" ||
            FormatReadOnlyValue(byName["SvcHostSplitThresholdInKB"], true, -1, RegistryValueKind.DWord) != "0xFFFFFFFF" ||
            FormatReadOnlyValue(byName["SvcHostSplitThresholdInKB"], true, 0x380000, RegistryValueKind.DWord) != "0x00380000" ||
            !FormatReadOnlyValue(byName["TdrDelay"], true, "10", RegistryValueKind.String).StartsWith("(nepodporovaný typ:", StringComparison.Ordinal))
            throw new InvalidOperationException("Default Guard read-only state classification failed.");

        // Service-host isolation override must be REMOVED (Windows-managed
        // default), never rewritten as DWORD 0 by the memory-defaults branch.
        if (!RestoresByRemovingValue(byName["SvcHostSplitThresholdInKB"]) ||
            RestoresByRemovingValue(byName["LargeSystemCache"]) ||
            RestoresByRemovingValue(byName["DisablePagingExecutive"]) ||
            !RestoresByRemovingValue(byName["TdrDelay"]) ||
            !RestoresByRemovingValue(byName["AutoEndTasks"]))
            throw new InvalidOperationException("Default Guard delete-vs-set restore policy failed.");

        // Preserve only the old, narrow RAM-size heuristic for Fixer. It
        // must never match 0xFFFFFFFF, unrelated values or unknown RAM size.
        if (!IsLegacyRamSizedSvcHostOverride(16 * 1024 * 1024, 16 * 1024 * 1024) ||
            !IsLegacyRamSizedSvcHostOverride(16 * 1024 * 1024 + 32768, 16 * 1024 * 1024) ||
            IsLegacyRamSizedSvcHostOverride(-1, 16 * 1024 * 1024) ||
            IsLegacyRamSizedSvcHostOverride(1024, 16 * 1024 * 1024) ||
            IsLegacyRamSizedSvcHostOverride(16 * 1024 * 1024, 0))
            throw new InvalidOperationException("Fixer legacy svchost recovery scope failed.");

        // Directly confirmed writes, not the before/after registry drift
        // delta, are the only honest count of changes attributable to us.
        var simulatedResult = new DefaultGuardMachineRepairResult(3, 0, 5, 0, 1);
        if (simulatedResult.AppliedChanges != 1 ||
            simulatedResult.DetectedBefore - simulatedResult.RemainingAfter == simulatedResult.AppliedChanges)
            throw new InvalidOperationException("Default Guard confirmed-write result contract failed.");

        // One or two restores in a rolling hour must not suspend ordinary
        // five-minute monitoring. Only a verified third restore pauses it.
        DateTime at = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        DateTime[] once = [at.AddMinutes(-5)];
        DateTime[] twice = [at.AddMinutes(-35), at.AddMinutes(-5)];
        DateTime[] thrice = [at.AddMinutes(-55), at.AddMinutes(-35), at.AddMinutes(-5)];
        DateTime[] expired = [at.AddHours(-2), at.AddHours(-2), at.AddMinutes(-5)];
        if (ShouldDeferRepeat(false, once, at) ||
            ShouldDeferRepeat(false, twice, at) ||
            !ShouldDeferRepeat(false, thrice, at) ||
            ShouldDeferRepeat(true, thrice, at) ||
            ShouldDeferRepeat(false, expired, at) ||
            ShouldDeferRepeat(false, null, at) ||
            !ShouldReportGreenChange(null, true) ||
            ShouldReportGreenChange(true, true) ||
            !ShouldReportGreenChange(true, false))
            throw new InvalidOperationException("Default Guard multi-restoration anti-flap or NIC log deduplication failed.");

        // Explicit Fixer must bypass auto-throttling WITHOUT bypassing the
        // current HKCU/HKLM consent check. UAC worker alone gets the validated
        // machine-only button-click snapshot; timer alone consumes auto budget.
        if (InvocationPolicy(RepairInvocation.PeriodicGuard) != (false, true) ||
            InvocationPolicy(RepairInvocation.ExplicitFixer) != (false, false) ||
            InvocationPolicy(RepairInvocation.ExplicitUacWorker) != (true, false))
            throw new InvalidOperationException("Default Guard Fixer/manual/periodic invocation policy failed.");

        // A manually confirmed repair (including Fixer) must never consume the
        // periodic Guard's three-auto-repairs/hour allowance.
        const string smokeBudgetKey = "policy-smoke/automatic-budget";
        lock (Sync)
        {
            try
            {
                RecordSuccessfulRepair(smokeBudgetKey, at, automatic: false);
                if (RecentRepairs.ContainsKey(smokeBudgetKey))
                    throw new InvalidOperationException("Manual repair consumed auto-restore budget.");
                RecordSuccessfulRepair(smokeBudgetKey, at, automatic: true);
                if (!RecentRepairs.TryGetValue(smokeBudgetKey, out var budget) || budget.Count != 1 ||
                    ShouldDeferRepeat(false, budget, at))
                    throw new InvalidOperationException("Automatic restore budget accounting failed.");
            }
            finally { RecentRepairs.Remove(smokeBudgetKey); }
        }
        if (!DescribeAutoRestoreAction(true, true, true).Contains("dočasně pozastavena", StringComparison.Ordinal) ||
            DescribeAutoRestoreAction(true, true, false) != "automatická obnova povolena" ||
            !DescribeAutoRestoreAction(true, false, true).Contains("čeká na správce", StringComparison.Ordinal) ||
            DescribeAutoRestoreAction(false, true, true) != "bez povolení opravy")
            throw new InvalidOperationException("AI anti-flap pause explanation is misleading.");

        // Journal preparation is NOT evidence of a committed repair. The
        // outcome receipt has the same OperationId and arrives after readback.
        var prepared = new BackupRecord(at, "gpu-tdr", "LocalMachine",
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrDelay",
            RegistryValueKind.DWord.ToString(), "10", "Remove override");
        var appliedReceipt = prepared with { Phase = "Applied" };
        if (prepared.Phase != "Prepared" || appliedReceipt.Phase != "Applied" ||
            prepared.OperationId != appliedReceipt.OperationId ||
            prepared.OldValue != appliedReceipt.OldValue ||
            !JsonSerializer.Serialize(appliedReceipt).Contains("\"OperationId\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Default Guard write-ahead journal receipt contract failed.");

        // Pure machine readback tests: absence is healthy, unexpected types
        // and inaccessible keys are UNVERIFIED rather than successful repairs.
        if (AssessMachineValue(byName["TdrDelay"], true, null, null) != RegistryAssessment.Healthy ||
            AssessMachineValue(byName["TdrDelay"], true, 10, RegistryValueKind.DWord) != RegistryAssessment.Drift ||
            AssessMachineValue(byName["TdrDelay"], true, "10", RegistryValueKind.String) != RegistryAssessment.Unverified ||
            AssessMachineValue(byName["TdrDelay"], false, null, null) != RegistryAssessment.Unverified ||
            AssessMachineValue(byName["WaitToKillServiceTimeout"], true, "5000", RegistryValueKind.String) != RegistryAssessment.Healthy ||
            AssessMachineValue(byName["WaitToKillServiceTimeout"], true, "2000", RegistryValueKind.String) != RegistryAssessment.Drift ||
            AssessMachineValue(byName["GPU Priority"], true, new byte[] { 1, 2 }, RegistryValueKind.Binary) != RegistryAssessment.Unverified)
            throw new InvalidOperationException("Default Guard unknown-status readback classification failed.");

        // The AI dashboard must count ALL actual Guard rules, not just the
        // original four showcased values. Missing findings are unverified,
        // never counted as healthy; Green Ethernet is a separate read-only NIC.
        if (Rules.Length != 11 || AvailableGroups.Length != 8 ||
            Rules.Any(rule => !AvailableGroups.Any(g => g.Id == rule.GroupId)))
            throw new InvalidOperationException("Registry dashboard group/rule inventory drifted.");
        var aiEvidence = DescribeAiWindowsRegistryHealth(
        [
            new("gpu-tdr", "TdrDelay", "10", true, true),
            new("mmcss-defaults", "SystemResponsiveness", "10", true, true),
            new("service-shutdown", "WaitToKillServiceTimeout", "2000", true, true),
            new("memory-defaults", "SvcHostSplitThresholdInKB", "(nezjištěno)", true, false)
        ], elevated: false);
        if (!aiEvidence.Contains("0/11 v pořádku, 3 odchylek, 8 neověřeno", StringComparison.Ordinal) ||
            !aiEvidence.Contains("Green Ethernet: stav neověřen", StringComparison.Ordinal) ||
            !aiEvidence.Contains("čeká na správce / ruční UAC", StringComparison.Ordinal) ||
            !aiEvidence.Contains("SvcHostSplitThresholdInKB: neověřeno", StringComparison.Ordinal))
            throw new InvalidOperationException("AI Windows registry incomplete-evidence policy failed.");
        var completeEvidence = DescribeAiWindowsRegistryHealth(
            Rules.Select(rule => new Finding(rule.GroupId, rule.Name,
                "(výchozí)", false, false)).ToArray(), elevated: false);
        if (!completeEvidence.Contains("11/11 v pořádku, 0 odchylek, 0 neověřeno", StringComparison.Ordinal) ||
            Rules.Any(rule => !completeEvidence.Contains(rule.Name + ": OK", StringComparison.Ordinal)))
            throw new InvalidOperationException("AI Windows registry complete-evidence policy failed.");
        // Without elevation, approved HKCU repairs are actionable; HKLM
        // repairs must still ask for administrator privileges.
        var mixedPrivilegeEvidence = DescribeAiWindowsRegistryHealth(
        [
            new("app-shutdown", "AutoEndTasks", "1", true, true),
            new("gpu-tdr", "TdrDelay", "10", true, true)
        ], elevated: false);
        if (!mixedPrivilegeEvidence.Contains("AutoEndTasks: odchylka 1 → bez agresivního AutoEndTasks=1 (automatická obnova povolena)", StringComparison.Ordinal) ||
            !mixedPrivilegeEvidence.Contains("TdrDelay: odchylka 10 → Windows Default (bez ručního přepisu) (čeká na správce / ruční UAC)", StringComparison.Ordinal))
            throw new InvalidOperationException("AI registry HKCU/HKLM permission explanation failed.");

        // A validated explicit UAC snapshot must not depend on which user's
        // LocalAppData contains the auto-monitor consent file. Ordinary timer
        // repairs MUST stop immediately if the current approval was revoked.
        var requested = new HashSet<string>(StringComparer.Ordinal) { "gpu-tdr" };
        var revoked = new HashSet<string>(StringComparer.Ordinal);
        if (!ShouldApplyWithConsent(true, "gpu-tdr", requested, revoked) ||
            ShouldApplyWithConsent(false, "gpu-tdr", requested, revoked) ||
            !ShouldApplyWithConsent(false, "gpu-tdr", requested, requested) ||
            ShouldApplyWithConsent(true, "mmcss-defaults", requested, requested))
            throw new InvalidOperationException("Default Guard approval snapshot/revocation rules failed.");

        var machine = ValidateMachineGroupRequest(
            new DefaultGuardMachineRepairRequest(["gpu-tdr", "gpu-priority", "mmcss-defaults"]));
        if (machine.Count != 3 || machine.Contains("app-shutdown") ||
            !WorkerPrivilegePolicy.RequiresElevation("default-guard-repair") ||
            !WorkerHostService.HandledActions.Contains("default-guard-repair") ||
            !WorkerPrivilegePolicy.RequiresElevation("green-ethernet-disable") ||
            !WorkerHostService.HandledActions.Contains("green-ethernet-disable"))
            throw new InvalidOperationException("Privileged Default Guard / NIC worker wiring failed.");
        foreach (string invalid in new[] { "app-shutdown", "green-ethernet", "unknown-action" })
        {
            bool blocked = false;
            try { ValidateMachineGroupRequest(new DefaultGuardMachineRepairRequest([invalid])); }
            catch (ArgumentException) { blocked = true; }
            if (!blocked) throw new InvalidOperationException("Unsafe worker scope was accepted: " + invalid);
        }
        // The Realtek advanced-property provider supplies display/registry
        // values in parallel. Localized labels must map by DWORD code, not
        // by hard-coded English "Disabled". Invalid/ambiguous maps fail closed.
        if (NativeNicTuningService.SelectGreenEthernetDisabledDisplayValue(
                ["Aktivováno", "Vypnuto"], ["1", "0"]) != "Vypnuto" ||
            NativeNicTuningService.SelectGreenEthernetDisabledDisplayValue(
                ["Off", "On"], ["0", "1"]) != "Off")
            throw new InvalidOperationException("Localized Green Ethernet target mapping failed.");
        foreach ((string[] display, string[] registry) invalid in new[]
        {
            (new[] { "Off" }, new[] { "1" }),
            (new[] { "Off", "Off again" }, new[] { "0", "0" }),
            (new[] { "Off" }, new[] { "0", "1" })
        })
        {
            bool rejected = false;
            try { _ = NativeNicTuningService.SelectGreenEthernetDisabledDisplayValue(invalid.display, invalid.registry); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Unsafe Green Ethernet target mapping was accepted.");
        }
        if (NativeNicTuningService.DecodeGreenEthernetRegistryValue("0") != "Disabled" ||
            NativeNicTuningService.DecodeGreenEthernetRegistryValue("1") != "Enabled" ||
            NativeNicTuningService.DecodeGreenEthernetRegistryValue("unknown") != null ||
            NativeNicTuningService.DecodeGreenEthernetRegistryValue(null) != null ||
            Rules.Any(r => r.Key.Contains("Services\\", StringComparison.OrdinalIgnoreCase) ||
                           r.Name is "NetworkThrottlingIndex" or "HwSchMode" or "EnableVirtualizationBasedSecurity") ||
            AvailableGroups.Length != 8 || Rules.Length != 11 ||
            AvailableGroups.Select(g => g.Id).Distinct().Count() != 8 ||
            !NeedsAdministrator("gpu-priority") ||
            !NeedsAdministrator("service-shutdown") ||
            NeedsAdministrator("app-shutdown") ||
            NeedsAdministrator("green-ethernet"))
            throw new InvalidOperationException("Windows default guard allowlist expanded unexpectedly.");
        return "WINDOWS_DEFAULT_GUARD_POLICY_SMOKE_OK groups=8 rules=11 noWindowsWrites=true";
    }

    private static RegistryValueKind? GetValueKindSafe(this RegistryKey key, string name)
    {
        try { return key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) ?
            key.GetValueKind(name) : null; }
        catch (IOException) { return null; }
    }
}
