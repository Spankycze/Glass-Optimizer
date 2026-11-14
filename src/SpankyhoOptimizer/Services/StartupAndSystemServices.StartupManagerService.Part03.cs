using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static partial class StartupManagerService
{
    internal static string RunRegistryMoveCollisionSmoke()
    {
        string[] existing = { "Spotify", "Notion" };
        RequireVacantRegistryTarget(existing, "DifferentApp");
        int rejected = 0;
        foreach (string colliding in new[] { "Spotify", "SPOTIFY", "notion" })
        {
            try { RequireVacantRegistryTarget(existing, colliding); }
            catch (InvalidOperationException) { rejected++; }
        }
        if (rejected != 3)
            throw new InvalidOperationException(
                "Startup registry move would overwrite an existing active/disabled entry.");

        // v16.183: the collision check must use OpenSubKey first, without
        // invoking CreateSubKey for a pre-existing destination. Pure synthetic
        // IDisposable handles stand in for registry keys; no OS key is opened.
        int createdDuringCollision = 0;
        using var occupiedHandle = new MemoryStream();
        bool collisionRejected = false;
        try
        {
            _ = OpenVacantRegistryMoveTarget(
                () => occupiedHandle,
                () => { createdDuringCollision++; return new MemoryStream(); },
                _ => new[] { "SPOTIFY", "Notion" }, "Spotify");
        }
        catch (InvalidOperationException) { collisionRejected = true; }
        if (!collisionRejected || createdDuringCollision != 0 ||
            occupiedHandle.CanRead)
            throw new InvalidOperationException(
                "Existing startup collision created target or leaked registry handle.");

        int createsForMissingKey = 0;
        using var createdHandle = new MemoryStream();
        var admitted = OpenVacantRegistryMoveTarget(
            () => (MemoryStream?)null,
            () => { createsForMissingKey++; return createdHandle; },
            _ => Array.Empty<string>(), "Spotify");
        if (createsForMissingKey != 1 || !ReferenceEquals(admitted, createdHandle) ||
            !admitted.CanRead)
            throw new InvalidOperationException(
                "Missing startup registry destination was not created exactly once.");

        // v16.184: the destination may be populated after GetValueNames()
        // but before SetValue, and an unrelated user can remove the source
        // before our own DeleteValue. Neither scenario authorizes rollback
        // to overwrite somebody else's choice. No registry operations.
        RequireVacantRegistryTargetAtWrite(Array.Empty<string>(), null, "Spotify");
        bool lateTargetOccupied = false;
        try
        {
            RequireVacantRegistryTargetAtWrite(
                Array.Empty<string>(), "external-override.exe", "Spotify");
        }
        catch (InvalidOperationException) { lateTargetOccupied = true; }
        bool newlyEnumeratedCollision = false;
        try
        {
            RequireVacantRegistryTargetAtWrite(
                new[] { "SPOTIFY" }, null, "Spotify");
        }
        catch (InvalidOperationException) { newlyEnumeratedCollision = true; }
        if (!lateTargetOccupied || !newlyEnumeratedCollision ||
            MayRecreateStartupSourceDuringRollback(false, null) ||
            MayRecreateStartupSourceDuringRollback(false, "old.exe") ||
            MayRecreateStartupSourceDuringRollback(true, "user-new.exe") ||
            !MayRecreateStartupSourceDuringRollback(true, null))
            throw new InvalidOperationException(
                "Registry startup rollback would resurrect an external deletion or overwrite the target.");

        // v16.185: native DeleteValue must throw if another process
        // removed the source between readback and our own deletion.
        // Only an ACTUALLY completed strict delete grants rollback rights.
        var syntheticSource = new Dictionary<string, string>
        {
            ["Spotify"] = "original.exe"
        };
        bool sourceWasDeletedByUs = false, strictMissingRejected = false;
        syntheticSource.Remove("Spotify"); // external user removed it
        try
        {
            sourceWasDeletedByUs = CompleteStrictStartupSourceDelete(
                throwOnMissing =>
                {
                    if (!syntheticSource.Remove("Spotify") && throwOnMissing)
                        throw new ArgumentException("Synthetic source is already gone.");
                });
        }
        catch (ArgumentException) { strictMissingRejected = true; }
        if (!strictMissingRejected || sourceWasDeletedByUs ||
            MayRecreateStartupSourceDuringRollback(
                sourceWasDeletedByUs, syntheticSource.GetValueOrDefault("Spotify")))
            throw new InvalidOperationException(
                "Strict registry source delete treated an external deletion as our own.");

        syntheticSource["Spotify"] = "original.exe";
        sourceWasDeletedByUs = CompleteStrictStartupSourceDelete(
            throwOnMissing =>
            {
                if (!syntheticSource.Remove("Spotify") && throwOnMissing)
                    throw new ArgumentException("Synthetic source is absent.");
            });
        if (!sourceWasDeletedByUs || syntheticSource.ContainsKey("Spotify") ||
            !MayRecreateStartupSourceDuringRollback(
                sourceWasDeletedByUs, syntheticSource.GetValueOrDefault("Spotify")))
            throw new InvalidOperationException(
                "Strict registry source delete did not grant legitimate rollback rights.");

        // No registry reads/writes: exercise stale inventory, unchanged
        // command, changed command, wrong data type and null values.
        if (!MatchesExpectedRegistryCommand("app.exe --silent", "app.exe --silent") ||
            MatchesExpectedRegistryCommand("app.exe --update", "app.exe --silent") ||
            MatchesExpectedRegistryCommand("APP.exe --silent", "app.exe --silent") ||
            MatchesExpectedRegistryCommand(null, "app.exe --silent") ||
            MatchesExpectedRegistryCommand(new[] { "app.exe" }, "app.exe"))
            throw new InvalidOperationException(
                "Startup Run command changed after inventory: stale move admission regressed.");

        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Spotify"] = "disabled-command"
        };
        var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPOTIFY"] = "original-active-command"
        };
        bool blockedBeforeWrite = false;
        try
        {
            RequireVacantRegistryTarget(target.Keys, "Spotify");
            target["Spotify"] = source["Spotify"];
            source.Remove("Spotify");
        }
        catch (InvalidOperationException) { blockedBeforeWrite = true; }
        if (!blockedBeforeWrite || source["Spotify"] != "disabled-command" ||
            target["Spotify"] != "original-active-command")
            throw new InvalidOperationException(
                "Startup collision validation did not preserve both existing commands.");

        // The rollback must not delete a target when the native SetValue
        // never completed, when source restore failed, or when data/kind differ.
        if (MayDeleteStartupRollbackTarget(false, true, "app.exe", RegistryValueKind.String,
                "app.exe", RegistryValueKind.String) ||
            MayDeleteStartupRollbackTarget(true, false, "app.exe", RegistryValueKind.String,
                "app.exe", RegistryValueKind.String) ||
            !MayDeleteStartupRollbackTarget(true, true, "app.exe", RegistryValueKind.String,
                "app.exe", RegistryValueKind.String) ||
            MayDeleteStartupRollbackTarget(true, true, "other.exe", RegistryValueKind.String,
                "app.exe", RegistryValueKind.String) ||
            MayDeleteStartupRollbackTarget(true, true, "app.exe", RegistryValueKind.ExpandString,
                "app.exe", RegistryValueKind.String) ||
            MayDeleteStartupRollbackTarget(true, true, null, RegistryValueKind.None,
                "app.exe", RegistryValueKind.String))
            throw new InvalidOperationException(
                "Startup rollback might discard the sole surviving registry command.");

        // v16.182: a stale inventory/worker payload must not authorize
        // arbitrary native registry mutations outside real Run/RunOnce
        // slots, nor a default/blank value. All test entries are synthetic.
        var validRun = new StartupEntry("synthetic", "Spotify",
            "\"C:\\Apps\\Spotify.exe\" --silent",
            RegistryHive.CurrentUser, RunPaths[0], true,
            "Spotify", @"C:\Apps\Spotify.exe",
            View: RegistryView.Registry64);
        if (!IsAdmissibleRegistryStartupMove(validRun) ||
            !IsAdmissibleRegistryStartupMove(validRun with
                { RunPath = RunPaths[1], Hive = RegistryHive.LocalMachine,
                  View = RegistryView.Registry32 }) ||
            IsAdmissibleRegistryStartupMove(validRun with { Name = "" }) ||
            IsAdmissibleRegistryStartupMove(validRun with { Command = " " }) ||
            IsAdmissibleRegistryStartupMove(validRun with
                { RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run\Other" }) ||
            IsAdmissibleRegistryStartupMove(validRun with
                { RunPath = RunPaths[0] + DisabledSuffix }) ||
            IsAdmissibleRegistryStartupMove(validRun with
                { Hive = RegistryHive.ClassesRoot }) ||
            IsAdmissibleRegistryStartupMove(validRun with
                { View = RegistryView.Default }) ||
            IsAdmissibleRegistryStartupMove(validRun with
                { SourceKind = "ScheduledTask" }) ||
            !IsMovableRegistryStartupValueKind(RegistryValueKind.String) ||
            !IsMovableRegistryStartupValueKind(RegistryValueKind.ExpandString) ||
            IsMovableRegistryStartupValueKind(RegistryValueKind.DWord) ||
            IsMovableRegistryStartupValueKind(RegistryValueKind.MultiString))
            throw new InvalidOperationException(
                "Startup registry move allowed a malformed location, kind or worker payload.");

        // Pure path fixtures: no Windows startup files are created or moved.
        string startupFolder = Environment.GetFolderPath(
            Environment.SpecialFolder.Startup);
        string launcher = Path.Combine(startupFolder, "fixture.lnk");
        string disabled = Path.Combine(startupFolder,
            "SpankyhoDisabled", "fixture.lnk");
        if (!IsExpectedStartupFolderSource(launcher, startupFolder, true) ||
            !IsExpectedStartupFolderSource(disabled, startupFolder, false) ||
            IsExpectedStartupFolderSource(launcher, startupFolder, false) ||
            IsExpectedStartupFolderSource(disabled, startupFolder, true) ||
            IsExpectedStartupFolderSource(
                Path.Combine(startupFolder, "Subfolder", "fixture.lnk"),
                startupFolder, true) ||
            IsExpectedStartupFolderSource(launcher,
                Path.GetTempPath(), true) ||
            IsExpectedStartupFolderSource(@"fixture.lnk",
                startupFolder, true))
            throw new InvalidOperationException(
                "Startup folder source admission is not restricted to Windows startup folders.");

        return "STARTUP_REGISTRY_MOVE_COLLISION_SMOKE_OK cases=5 collisionsRejected=4 " +
            "existingSourcePreserved=true existingTargetPreserved=true registryWrites=0";
    }

    public static async Task SetEnabledAsync(StartupEntry item, bool enabled, CancellationToken token = default)
    {
        // The AI can be switched off while the registry operation is still
        // queued. Reject cancellation before any native startup mutation.
        token.ThrowIfCancellationRequested();
        if (item.Enabled == enabled) return;
        if (!item.CanToggle) throw new InvalidOperationException("Tato startup položka je chráněná.");
        if (item.SourceKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase))
            TaskSchedulerService.EnsureTaskMutationAllowed(
                item.SourceId ?? string.Empty, enabled);
        if (!string.Equals(item.SourceKind, "StartupFolder", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.SourceKind, "ScheduledTask", StringComparison.OrdinalIgnoreCase) &&
            !IsAdmissibleRegistryStartupMove(item))
            throw new InvalidOperationException(
                "Startup registry cesta, název, příkaz nebo registr view nejsou v povoleném rozsahu Run/RunOnce.");

        bool requiresElevation =
            item.Hive != RegistryHive.CurrentUser ||
            (item.SourceKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase) &&
             item.RunPath.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StringComparison.OrdinalIgnoreCase));

        if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator() && requiresElevation)
        {
            try
            {
                await WorkerClientService.RunAsync<WorkerAck>(
                    "startup-set",
                    WorkerPayloads.Encode(new StartupStateChangeRequest(item, enabled)),
                    TimeSpan.FromSeconds(45),
                    token).ConfigureAwait(false);
            }
            finally
            {
                // A worker timeout/failed acknowledgement does not prove
                // that its native write never started. Drop stale inventory.
                InvalidateCache();
            }
            LogService.Info($"Startup privileged state changed and verified via worker: {item.Name}, enabled={enabled}");
            return;
        }

        if (item.SourceKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string path = item.SourceId ?? throw new InvalidOperationException("Task path chybí.");
            try
            {
                await new TaskSchedulerService().SetEnabledAsync(path, enabled, token).ConfigureAwait(false);
            }
            finally
            {
                // Readback/cancellation failures may follow a successful COM
                // mutation. Do not retain a stale Startup page snapshot.
                InvalidateCache();
            }
            LogService.Info($"Startup task changed and verified: {path}, enabled={enabled}");
            return;
        }

        if (item.SourceKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase))
        {
            Task fileMove = Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                string source = item.SourceId ?? throw new InvalidOperationException("Startup file path chybí.");
                if (!IsExpectedStartupFolderSource(source, item.RunPath, item.Enabled))
                    throw new InvalidOperationException(
                        "Startup soubor neleží v očekávané složce Po spuštění, přesun zablokován.");
                string disabledDir = Path.Combine(item.RunPath, "SpankyhoDisabled");
                string target = enabled
                    ? Path.Combine(item.RunPath, Path.GetFileName(source))
                    : Path.Combine(disabledDir, Path.GetFileName(source));
                if (!File.Exists(source)) throw new InvalidOperationException("Startup soubor už neexistuje.");
                if (File.Exists(target)) throw new IOException("Cílový startup soubor už existuje.");
                token.ThrowIfCancellationRequested();
                // Re-enabling never needs to create the disabled folder.
                // Only a pending disable creates its destination directory.
                if (!enabled)
                {
                    Directory.CreateDirectory(disabledDir);
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(target)) throw new IOException("Cílový startup soubor už existuje.");
                }
                File.Move(source, target);
                if (!File.Exists(target) || File.Exists(source))
                {
                    try { if (File.Exists(target) && !File.Exists(source)) File.Move(target, source); } catch { }
                    throw new InvalidOperationException("Read-back nepotvrdil přesun startup souboru.");
                }
            }, token);
            try { await fileMove.ConfigureAwait(false); }
            finally { InvalidateCache(); }
            LogService.Info($"Startup folder item changed and verified: {item.Name}, enabled={enabled}");
            return;
        }

        Task registryMove = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var root = RegistryKey.OpenBaseKey(item.Hive, item.View);
            var sourcePath = item.RunPath + (enabled ? DisabledSuffix : "");
            var targetPath = item.RunPath + (enabled ? "" : DisabledSuffix);
            using var source = root.OpenSubKey(sourcePath, writable: true) ?? throw new InvalidOperationException("Položka po spuštění už neexistuje.");
            var value = source.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? throw new InvalidOperationException("Hodnotu nelze přečíst.");
            // Revalidate actual registry contents immediately before the
            // write, not only the earlier StartupEntry/AI inventory object.
            // Fail closed rather than moving a replaced/updated Run command.
            if (!MatchesExpectedRegistryCommand(value, item.Command))
                throw new InvalidOperationException(
                    "Startup příkaz se od inventáře změnil, přesun je zablokován.");
            var kind = source.GetValueKind(item.Name);
            // Revalidate the native REG_SZ / REG_EXPAND_SZ type as well as
            // the command before creating any destination. A stale, forged
            // or unexpected value kind must never be migrated.
            if (!IsMovableRegistryStartupValueKind(kind))
                throw new InvalidOperationException(
                    "Typ položky startup registru není REG_SZ ani REG_EXPAND_SZ, přesun zablokován.");
            token.ThrowIfCancellationRequested();
            using var target = OpenVacantRegistryMoveTarget(
                () => root.OpenSubKey(targetPath, writable: true),
                () => root.CreateSubKey(targetPath, writable: true)
                    ?? throw new InvalidOperationException(
                        "Startup registry target could not be created."),
                key => key.GetValueNames(), item.Name);
            // Existing targets are checked without a CreateSubKey attempt.
            // If the target does not exist, create it and check again after
            // creation, before writing. After writing, finish or rollback;
            // never intentionally strand half a startup move.
            token.ThrowIfCancellationRequested();
            // The source may also have been removed or replaced by the
            // user since the initial read. Do not resurrect that command
            // by copying it to the target from our stale snapshot.
            if (!MatchesExpectedRegistryCommand(
                    source.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                    item.Command) || source.GetValueKind(item.Name) != kind)
                throw new InvalidOperationException(
                    "Startup source changed before the move write; operation cancelled.");
            // Fail before entering the rollback catch when another writer
            // fills the target between the first inventory check and now.
            // Never allow the rollback to delete someone else's value.
            RequireVacantRegistryTargetAtWrite(
                target.GetValueNames(),
                target.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                item.Name);
            bool sourceDeletedByUs = false;
            bool targetWrittenByUs = false;
            try
            {
                target.SetValue(item.Name, value, kind);
                // A native SetValue that throws is ambiguous: never delete a
                // potentially external matching target in our rollback.
                targetWrittenByUs = true;
                // A successful SetValue call is not enough to delete the
                // only original. Verify the exact copied command and type,
                // and check the source again before deleting it.
                if (!MatchesExpectedRegistryCommand(
                        target.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                        item.Command) || target.GetValueKind(item.Name) != kind ||
                    !MatchesExpectedRegistryCommand(
                        source.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                        item.Command) || source.GetValueKind(item.Name) != kind)
                    throw new InvalidOperationException(
                        "Startup položka se změnila během přesunu, zdroj ponechán.");
                // A concurrent external delete must throw, not be
                // mistaken for our completed deletion. Otherwise rollback
                // could resurrect the user-removed Run entry.
                sourceDeletedByUs = CompleteStrictStartupSourceDelete(
                    throwOnMissing => source.DeleteValue(item.Name, throwOnMissing));
                var targetValue = target.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var sourceValue = source.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (!MatchesExpectedRegistryCommand(targetValue, item.Command) ||
                    sourceValue != null || target.GetValueKind(item.Name) != kind)
                    throw new InvalidOperationException("Read-back nepotvrdil příkaz, typ a přesun startup položky.");
            }
            catch
            {
                // Restore source only if it is still vacant. Never overwrite
                // a user-modified value that raced with this operation.
                bool originalRestored = false;
                try
                {
                    object? currentSource = source.GetValue(
                        item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (MayRecreateStartupSourceDuringRollback(
                            sourceDeletedByUs, currentSource))
                    {
                        source.SetValue(item.Name, value, kind);
                        currentSource = source.GetValue(
                            item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    }
                    originalRestored = MatchesExpectedRegistryCommand(
                        currentSource, item.Command) && source.GetValueKind(item.Name) == kind;
                }
                catch { }
                // Critical: if restoring the source FAILED, the matching
                // target may be the only surviving copy. Never delete it.
                // Also preserve a target changed by any external writer.
                try
                {
                    object? currentTarget = target.GetValue(
                        item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    RegistryValueKind currentKind = currentTarget is null
                        ? RegistryValueKind.None : target.GetValueKind(item.Name);
                    if (MayDeleteStartupRollbackTarget(
                            targetWrittenByUs, originalRestored, currentTarget,
                            currentKind, item.Command, kind))
                        target.DeleteValue(item.Name, false);
                }
                catch { }
                throw;
            }
        }, token);
        try { await registryMove.ConfigureAwait(false); }
        finally { InvalidateCache(); }
        LogService.Info($"Startup registry state changed and verified: {item.Name}, enabled={enabled}, view={item.View}");
    }

    // Permanent removal of an app-owned startup entry. Scheduled tasks stay
    // toggle-only; registry Run values and Startup-folder shortcuts are the
    // narrow, reversible-by-reinstall scope offered by the UI.
    public static async Task DeleteAsync(StartupEntry item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!item.CanToggle)
            throw new InvalidOperationException("Tato startup položka je chráněná.");

        if (item.SourceKind.Equals("Registry", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAdmissibleRegistryStartupMove(item))
                throw new InvalidOperationException(
                    "Startup registry cesta, název, příkaz nebo registr view nejsou v povoleném rozsahu Run/RunOnce.");
            bool requiresElevation = item.Hive != RegistryHive.CurrentUser;
            if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator() && requiresElevation)
            {
                try
                {
                    await WorkerClientService.RunAsync<WorkerAck>(
                        "startup-delete",
                        WorkerPayloads.Encode(new StartupDeleteRequest(item)),
                        TimeSpan.FromSeconds(45),
                        token).ConfigureAwait(false);
                }
                finally { InvalidateCache(); }
                LogService.Info($"Startup registry item deleted via worker: {item.Name}");
                return;
            }

            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var root = RegistryKey.OpenBaseKey(item.Hive, item.View);
                string path = item.RunPath + (item.Enabled ? "" : DisabledSuffix);
                using var key = root.OpenSubKey(path, writable: true)
                    ?? throw new InvalidOperationException("Položka po spuštění už neexistuje.");
                object? value = key.GetValue(item.Name, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value is null ||
                    !MatchesExpectedRegistryCommand(value, item.Command) ||
                    !IsMovableRegistryStartupValueKind(key.GetValueKind(item.Name)))
                    throw new InvalidOperationException(
                        "Startup příkaz se od inventáře změnil, smazání je zablokováno.");
                token.ThrowIfCancellationRequested();
                key.DeleteValue(item.Name, throwOnMissingValue: false);
                if (key.GetValueNames().Any(x =>
                        string.Equals(x, item.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        "Read-back stále vidí startup hodnotu.");
            }, token).ConfigureAwait(false);
            InvalidateCache();
            LogService.Info($"Startup registry item deleted and verified: {item.Name}");
            return;
        }

        if (item.SourceKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase))
        {
            bool requiresElevation = item.RunPath.Equals(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                StringComparison.OrdinalIgnoreCase);
            if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator() && requiresElevation)
            {
                try
                {
                    await WorkerClientService.RunAsync<WorkerAck>(
                        "startup-delete",
                        WorkerPayloads.Encode(new StartupDeleteRequest(item)),
                        TimeSpan.FromSeconds(45),
                        token).ConfigureAwait(false);
                }
                finally { InvalidateCache(); }
                LogService.Info($"Startup folder item deleted via worker: {item.Name}");
                return;
            }

            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                string source = item.SourceId
                    ?? throw new InvalidOperationException("Startup file path chybí.");
                if (!IsExpectedStartupFolderSource(source, item.RunPath, item.Enabled))
                    throw new InvalidOperationException(
                        "Startup soubor neleží v očekávané složce Po spuštění, smazání zablokováno.");
                if (!File.Exists(source))
                    throw new InvalidOperationException("Startup soubor už neexistuje.");
                token.ThrowIfCancellationRequested();
                File.Delete(source);
                if (File.Exists(source))
                    throw new InvalidOperationException(
                        "Read-back stále vidí startup soubor.");
            }, token).ConfigureAwait(false);
            InvalidateCache();
            LogService.Info($"Startup folder item deleted and verified: {item.Name}");
            return;
        }

        if (item.SourceKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase))
        {
            string path = item.SourceId
                ?? throw new InvalidOperationException("Task path chybí.");
            if (path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\SpankyhoOptimizer-", StringComparison.OrdinalIgnoreCase) ||
                !TaskSafetyService.CanDisable(path))
                throw new InvalidOperationException("Tato plánovaná úloha je chráněná.");

            if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator())
            {
                try
                {
                    await WorkerClientService.RunAsync<WorkerAck>(
                        "startup-delete",
                        WorkerPayloads.Encode(new StartupDeleteRequest(item)),
                        TimeSpan.FromSeconds(45),
                        token).ConfigureAwait(false);
                }
                finally { InvalidateCache(invalidateTaskScheduler: true); }
                LogService.Info($"Startup scheduled task deleted via worker: {path}");
                return;
            }

            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                _ = TaskSchedulerComService.DeleteIfExists(path);
                if (TaskSchedulerComService.TryGetTaskXml(path) != null)
                    throw new InvalidOperationException(
                        "Read-back stále vidí plánovanou úlohu.");
            }, token).ConfigureAwait(false);
            InvalidateCache(invalidateTaskScheduler: true);
            LogService.Info($"Startup scheduled task deleted and verified: {path}");
            return;
        }

        throw new InvalidOperationException(
            "Mazání je povoleno jen pro registry, složku Po spuštění a bezpečné plánované úlohy.");
    }
}
