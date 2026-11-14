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
    internal static string RunDeltaSmoke()
    {
        StartupEntry A(string id, string command, bool enabled) =>
            new(id, id, command, RegistryHive.CurrentUser, @"Software\Smoke", enabled,
                "Smoke", null);

        StartupEntry[] previous =
        [
            A("A", "a.exe", true),
            A("B", "b.exe", true),
            A("C", "c.exe", false)
        ];
        StartupEntry[] current =
        [
            A("A", "a2.exe", true),
            A("C", "c.exe", false),
            A("D", "d.exe", true)
        ];

        var delta = ComputeDeltaFrom(previous, current);
        if (delta.Added != 1 || delta.Removed != 1 || delta.Changed != 1)
            throw new InvalidOperationException(
                $"Startup delta smoke selhal: +{delta.Added} -{delta.Removed} ~{delta.Changed}");
        var duplicates = ComputeDeltaFrom([previous[0], previous[0], previous[1]],
            [previous[1], previous[1], previous[0] with { Enabled = false }]);
        if (duplicates.Added != 0 || duplicates.Removed != 0 || duplicates.Changed != 1)
            throw new InvalidOperationException("Duplicate legacy Startup IDs corrupted delta comparison.");
        var oldPath = A("path-case", "tool.exe", true) with { FilePath = @"C:\Apps\Tool.exe" };
        if (ComputeDeltaFrom([oldPath], [oldPath with { FilePath = @"c:\apps\TOOL.EXE" }]).HasChanges)
            throw new InvalidOperationException("Startup path case-only change falsely invalidated delta.");

        // Unchanged inventories must not rewrite the atomic JSON baseline at
        // every refresh. Publisher/metadata-only changes still persist, and
        // the 12h refresh prevents a long-lived process from aging its file out.
        DateTime sampleUtc = new(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc);
        if (ShouldPersistDeltaBaseline(previous, previous, sampleUtc, sampleUtc.AddMinutes(2)) ||
            !ShouldPersistDeltaBaseline(null, previous, sampleUtc, sampleUtc.AddMinutes(2)) ||
            !ShouldPersistDeltaBaseline(previous, current, sampleUtc, sampleUtc.AddMinutes(2)) ||
            !ShouldPersistDeltaBaseline(previous,
                [previous[0] with { Publisher = "New Publisher" }, previous[1], previous[2]],
                sampleUtc, sampleUtc.AddMinutes(2)) ||
            !ShouldPersistDeltaBaseline(previous, previous, sampleUtc, sampleUtc.AddHours(12)) ||
            !ShouldPersistDeltaBaseline(previous, previous, sampleUtc, sampleUtc.AddMinutes(-1)))
            throw new InvalidOperationException("Startup delta baseline write coalescing regressed.");

        // Read-only policy fixture: do not invalidate actual persisted cache or
        // touch startup registry; preserve original static state under lock.
        lock (CacheLock)
        {
            var previousDisplay = _displayCache;
            bool previousPending = _displayCacheNeedsRefresh;
            bool previousNeedsRefresh = _cacheNeedsBackgroundRefresh;
            try
            {
                _displayCache = previous;
                _displayCacheNeedsRefresh = false;
                _cacheNeedsBackgroundRefresh = false;
                MarkDisplayCacheForRefreshLocked();
                if (!_displayCacheNeedsRefresh || !_cacheNeedsBackgroundRefresh)
                    throw new InvalidOperationException("Manual refresh did not schedule rehydration.");
                _displayCache = null;
                MarkDisplayCacheForRefreshLocked();
                if (_displayCacheNeedsRefresh || _cacheNeedsBackgroundRefresh)
                    throw new InvalidOperationException("Refresh scheduled without cached display.");
            }
            finally
            {
                _displayCache = previousDisplay;
                _displayCacheNeedsRefresh = previousPending;
                _cacheNeedsBackgroundRefresh = previousNeedsRefresh;
            }
        }

        return "STARTUP_DELTA_SMOKE_OK added=1 removed=1 changed=1 " +
            "duplicates=true pathCase=true manualRefreshFlag=true persistentWrites=0";
    }

    private static void WritePersistentSnapshot(IReadOnlyList<StartupEntry> items) =>
        UserScopedCacheService.Write(PersistentSnapshotKey, items as StartupEntry[] ?? items.ToArray());

    // The AI presence/restore path has no reason to open every EXE to read
    // version metadata: executable identity comes from the registry command.
    internal static bool NeedsPublisherMetadata(bool aiInventory) => !aiInventory;

    // Registry enumeration is not a transaction. GetValueNames may include
    // a name deleted by another process before its GetValue is performed.
    // Only an actual non-null read is evidence that the slot still exists;
    // a legitimate present empty REG_SZ is preserved as a row so inventory
    // can still show it (restore policy separately rejects blank commands).
    internal static bool TryReadPresentRegistryCommand(object? rawValue, out string command)
    {
        command = rawValue?.ToString() ?? string.Empty;
        return rawValue is not null;
    }

    // An unnamed/default Run registry value is not a Windows autorun entry.
    // Other non-string or blank values remain visible for diagnosis but must
    // never offer a toggle: registry moves require real nonempty commands.
    // No registry access here; safe for synthetic inventory and policy tests.
    internal static bool TryClassifyRegistryStartupSlot(
        string? name, object? rawValue, out string command, out bool canToggle)
    {
        canToggle = false;
        if (!TryReadPresentRegistryCommand(rawValue, out command) ||
            string.IsNullOrWhiteSpace(name))
            return false;
        canToggle = rawValue is string && !string.IsNullOrWhiteSpace(command);
        return true;
    }

    private static void ReadKey(List<StartupEntry> result, RegistryHive hive,
        RegistryView view, string path, bool enabled, bool includePublisher = true)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (var name in key.GetValueNames())
        {
            // A removed value MUST NOT become a phantom enabled Run slot.
            // Null default distinguishes disappearance from a present empty
            // string. This matters for the AI-owned startup recovery journal.
            object? rawValue = key.GetValue(
                name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (!TryClassifyRegistryStartupSlot(
                    name, rawValue, out string value, out bool canToggle))
                continue;
            var executable = canToggle ? ExecutableOf(value) : null;
            result.Add(new StartupEntry($"{hive}|{view}|{path}|{name}", name, value, hive, path.Replace(DisabledSuffix, ""), enabled,
                includePublisher ? PublisherOfExecutable(executable) : "Neznámý vydavatel",
                executable, "Registry", null, canToggle, view));
        }
    }

    private static void ReadStartupFolder(List<StartupEntry> result, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        string disabledFolder = Path.Combine(folder, "SpankyhoDisabled");

        void AddFiles(string sourceFolder, bool enabled)
        {
            if (!Directory.Exists(sourceFolder)) return;
            foreach (string file in Directory.EnumerateFiles(sourceFolder))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                result.Add(new StartupEntry(
                    $"folder|{file}", name, file, RegistryHive.CurrentUser, folder, enabled,
                    PublisherOfExecutable(file), file, "StartupFolder", file, true));
            }
        }

        AddFiles(folder, true);
        AddFiles(disabledFolder, false);
    }

    // Read a direct absolute executable target even if it no longer exists:
    // dropping a missing path here made old Spotify/Notion/launcher Run entries
    // invisible to StartupRowViewModel.IsOrphaned and the Startup hero count.
    // Relative/PATH commands and shell wrappers are intentionally unknown,
    // not marked orphaned because their resolution requires a process shell.
    private static string? ExecutableOf(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate;
        if (expanded.StartsWith('"'))
        {
            int closing = expanded.IndexOf('"', 1);
            if (closing <= 1 || (closing + 1 < expanded.Length &&
                !char.IsWhiteSpace(expanded[closing + 1])))
                return null;
            candidate = expanded[1..closing];
        }
        else
        {
            // Windows Run commands sometimes contain unquoted Program Files
            // paths followed by arguments. Find the actual .exe boundary
            // instead of truncating the path at its first space.
            int ending = 0;
            do
            {
                ending = expanded.IndexOf(".exe", ending, StringComparison.OrdinalIgnoreCase);
                if (ending < 0) return null;
                int after = ending + 4;
                if (after == expanded.Length || char.IsWhiteSpace(expanded[after]))
                {
                    candidate = expanded[..after];
                    break;
                }
                ending = after;
            } while (true);
        }

        // Do not resolve relative executable names or UNC paths with blocking
        // network file probes on the UI thread; a direct local absolute EXE is
        // the only file whose missing status we can report with confidence.
        try
        {
            if (!Path.IsPathFullyQualified(candidate) ||
                !string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
        }
        catch (ArgumentException) { return null; }
        return candidate;
    }

    internal static string RunOrphanedRegistryCommandSmoke()
    {
        string missing = Path.Combine(Path.GetTempPath(), "Spanky Missing Startup " + Guid.NewGuid().ToString("N"), "Notion.exe");
        string quoted = $"\"{missing}\" --processStart Notion.exe";
        string bare = $"{missing} --processStart";
        if (File.Exists(missing) ||
            ExecutableOf(quoted) != missing ||
            ExecutableOf(bare) != missing ||
            ExecutableOf($"\"{missing}\"") != missing ||
            ExecutableOf(missing + ".bak") != null ||
            ExecutableOf("notion.exe --processStart") != null ||
            ExecutableOf($"cmd.exe /c \"{missing}\"") != null ||
            ExecutableOf($"\"{missing}\"bad-suffix") != null ||
            ExecutableOf(@"\\test-server\share\Missing.exe") != null ||
            ExecutableOf("") != null)
            throw new InvalidOperationException("Orphaned registry startup target was not parsed safely.");

        return "STARTUP_ORPHAN_REGISTRY_TARGET_OK quoted=true unquotedSpaces=true " +
            "missingPreserved=true relativeExcluded=true wrappersExcluded=true uncExcluded=true writes=0";
    }

    private static string PublisherOfExecutable(string? executable)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executable)) return "Neznámý vydavatel";
            lock (CacheLock)
                if (PublisherCache.TryGetValue(executable, out var cached)) return cached;

            string publisher = FileVersionInfo.GetVersionInfo(executable).CompanyName ?? "Neznámý vydavatel";
            lock (CacheLock) PublisherCache[executable] = publisher;
            return publisher;
        }
        catch { return "Neznámý vydavatel"; }
    }

    // The startup inventory / worker payload is NOT an authority for
    // arbitrary registry paths. Only actual Windows Run / RunOnce slots
    // enumerated by this service are legal. Fail closed before creating a
    // subkey, starting a worker, or moving any native startup value.
    internal static bool IsAdmissibleRegistryStartupMove(StartupEntry item) =>
        string.Equals(item.SourceKind, "Registry", StringComparison.OrdinalIgnoreCase) &&
        item.CanToggle &&
        !string.IsNullOrWhiteSpace(item.Name) &&
        !string.IsNullOrWhiteSpace(item.Command) &&
        item.Hive is RegistryHive.CurrentUser or RegistryHive.LocalMachine &&
        item.View is RegistryView.Registry32 or RegistryView.Registry64 &&
        RunPaths.Any(path => string.Equals(
            item.RunPath, path, StringComparison.OrdinalIgnoreCase));

    internal static bool IsMovableRegistryStartupValueKind(RegistryValueKind kind) =>
        kind is RegistryValueKind.String or RegistryValueKind.ExpandString;

    // The inventory is only a snapshot. A user or another program may change
    // a Run value before we move it. Never migrate an unexpected command:
    // such a move would mutate somebody else's startup preference.
    internal static bool MatchesExpectedRegistryCommand(
        object? currentValue, string expectedCommand) =>
        currentValue is string text &&
        string.Equals(text, expectedCommand, StringComparison.Ordinal);

    // Never remove an unowned destination during rollback. A failed native
    // SetValue may leave an independently-created target of the same command
    // and type; equality is NOT proof that our write succeeded. Only an
    // actually completed own target SetValue may authorize target cleanup,
    // and only when the original source is safely restored and still matches.
    internal static bool MayDeleteStartupRollbackTarget(
        bool targetWrittenByUs, bool sourceRestored, object? targetValue,
        RegistryValueKind targetKind, string originalCommand,
        RegistryValueKind originalKind) =>
        targetWrittenByUs && sourceRestored && targetKind == originalKind &&
        MatchesExpectedRegistryCommand(targetValue, originalCommand);

    // The inventory only offers direct files from the two actual Windows
    // startup folders. Never move an arbitrary path supplied by a stale UI
    // snapshot or a malformed worker payload outside that narrow scope.
    internal static bool IsExpectedStartupFolderSource(
        string? sourceId, string? runPath, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(sourceId) ||
            string.IsNullOrWhiteSpace(runPath) ||
            !Path.IsPathFullyQualified(sourceId) ||
            !Path.IsPathFullyQualified(runPath))
            return false;
        try
        {
            string root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(runPath));
            string userStartup = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup)));
            string commonStartup = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)));
            if (!root.Equals(userStartup, StringComparison.OrdinalIgnoreCase) &&
                !root.Equals(commonStartup, StringComparison.OrdinalIgnoreCase))
                return false;

            string expectedParent = enabled
                ? root : Path.Combine(root, "SpankyhoDisabled");
            string? actualParent = Path.GetDirectoryName(Path.GetFullPath(sourceId));
            return !string.IsNullOrWhiteSpace(Path.GetFileName(sourceId)) &&
                actualParent is not null &&
                actualParent.Equals(expectedParent, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Registry Run value names are case-insensitive. A disabled entry and an
    // enabled entry can legitimately share a name; never replace the target.
    private static void RequireVacantRegistryTarget(IEnumerable<string> targetNames, string entryName)
    {
        // Registry value names are case-insensitive. Overwriting an existing
        // target here could destroy a distinct active startup command, and the
        // old rollback would then remove that unrelated target completely.
        if (targetNames.Contains(entryName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Položku '{entryName}' nelze přesunout: cílový startup záznam se stejným názvem již existuje.");
    }

    // The first list-based collision check is a snapshot. A second
    // just-in-time read detects a value written by another process since
    // that enumeration, BEFORE any SetValue or rollback cleanup can occur.
    // This narrows the race; Registry does not offer an atomic move/CAS.
    internal static void RequireVacantRegistryTargetAtWrite(
        IEnumerable<string> targetNames, object? currentValue, string entryName)
    {
        RequireVacantRegistryTarget(targetNames, entryName);
        if (currentValue is not null)
            throw new InvalidOperationException(
                "Startup destination was populated after enumeration; move rejected.");
    }

    // DeleteValue(name, throwOnMissingValue:false) silently succeeds when
    // another process removed the value since the last readback. Such a
    // no-op must never mark sourceDeletedByUs and authorize resurrection.
    // A strict delete throws on missing; callers only mark own deletion
    // completed AFTER the injected native operation actually returns.
    internal static bool CompleteStrictStartupSourceDelete(
        Action<bool> deleteValue)
    {
        ArgumentNullException.ThrowIfNull(deleteValue);
        deleteValue(true);
        return true;
    }

    // Restoring a missing source when we have NOT successfully completed
    // our own DeleteValue would resurrect a startup item removed by the
    // user/another process. Only our completed delete authorizes rollback.
    internal static bool MayRecreateStartupSourceDuringRollback(
        bool sourceDeletedByUs, object? currentSource) =>
        sourceDeletedByUs && currentSource is null;

    // Do not call CreateSubKey merely to discover that a destination
    // already exists and contains a colliding Run value. First open any
    // existing key, create only if absent, and validate its names. Keep
    // the opened handle owned by the caller on success; on rejection,
    // dispose it without touching its values or deleting the source.
    // The injected operations make ordering verifiable without live registry.
    internal static T OpenVacantRegistryMoveTarget<T>(
        Func<T?> openExisting, Func<T> createIfMissing,
        Func<T, IEnumerable<string>> getValueNames, string entryName)
        where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(openExisting);
        ArgumentNullException.ThrowIfNull(createIfMissing);
        ArgumentNullException.ThrowIfNull(getValueNames);
        if (string.IsNullOrWhiteSpace(entryName))
            throw new ArgumentException(
                "Registry startup value name must be nonempty.", nameof(entryName));

        T target = openExisting() ?? createIfMissing();
        if (target is null)
            throw new InvalidOperationException(
                "Startup registry target could not be opened or created.");
        try
        {
            RequireVacantRegistryTarget(getValueNames(target), entryName);
            return target;
        }
        catch
        {
            // A failed destination admission must not leak the opened key.
            // A broken Dispose must not mask the original collision.
            try { target.Dispose(); }
            catch { }
            throw;
        }
    }
}
