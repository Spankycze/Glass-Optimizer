using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static partial class PowerPlanGuardService
{
    // Fail closed on file/directory reparse points, unreadable or unbounded
    // scripts. Keep this synchronous and small inside the already background
    // COM callback, just before calling IRegisteredTask.Run on that handle.
    private static void EnsureGuardScriptOnDiskMayBeRun()
    {
        string path = GuardScriptPath;
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Power Plan guard script nemá platnou cestu.");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "Power Plan guard script nebo složka je přesměrovaná; spuštění bylo zablokováno.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= 0 || stream.Length > GuardScriptMaxBytes)
            throw new InvalidOperationException(
                "Power Plan guard script má neočekávanou velikost; spuštění bylo zablokováno.");
        byte[] actual = new byte[(int)stream.Length];
        stream.ReadExactly(actual);
        EnsureGuardScriptContentMayBeRun(actual);
    }

    public static async Task<long> RunNowAsync(
        string expectedGuid, CancellationToken token = default)
    {
        if (!Guid.TryParse(expectedGuid, out var parsed))
            throw new ArgumentException("Power plan GUID není platný.", nameof(expectedGuid));
        token.ThrowIfCancellationRequested();
        string normalized = parsed.ToString("D");
        long statusCursor = CaptureGuardStatusCursor();
        await Task.Run(() => TaskSchedulerComService.RunAuthorizedTask(
            TaskName,
            xml =>
            {
                EnsureGuardTaskMayBeRun(xml, normalized);
                EnsureGuardScriptOnDiskMayBeRun();
            },
            token), token).ConfigureAwait(false);
        return statusCursor;
    }

    // A registered task name is not proof that the task still belongs to
    // this optimizer. Only a single actual Exec action invoking OUR script
    // through the trusted system cmd.exe may be deleted as Power Plan guard.
    // Do not accept strings found in XML description/metadata/other actions.
    internal static bool IsOwnedGuardTaskXml(string? xml) =>
        TryReadOwnedGuardTaskGuid(xml, out _);

    // A path merely mentioned in a CMD argument is not proof of ownership:
    // /C must directly execute the quoted absolute guard script as the FIRST
    // command. Accept the schtasks outer-quote form too, but never a second
    // shell command, redirection, expansion, or arbitrary argument text.
    // Return the GUID from this verified action only (never Description/XML).

    // Task XML is parsed only with DTD and external resolvers disabled and a
    // bounded document size; both guard readers share that exact policy.
    private static XmlReaderSettings CreateTaskXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 256 * 1024
    };

    internal static bool TryReadOwnedGuardTaskGuid(string? xml, out string guid)
    {
        guid = string.Empty;
        if (string.IsNullOrWhiteSpace(xml)) return false;
        try
        {
            var settings = CreateTaskXmlReaderSettings();
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = XDocument.Load(reader);
            if (doc.Root is not { } root || root.Name.LocalName != "Task")
                return false;
            var actions = doc.Descendants()
                .Where(node => node.Name.LocalName == "Actions")
                .ToArray();
            if (actions.Length != 1 || actions[0].Parent != root ||
                actions[0].Name.Namespace != root.Name.Namespace)
                return false;
            var actionNodes = actions[0].Elements().ToArray();
            if (actionNodes.Length != 1 ||
                actionNodes[0].Name != root.Name.Namespace + "Exec")
                return false;
            var exec = actionNodes[0];
            var commands = exec.Elements()
                .Where(node => node.Name.LocalName == "Command").ToArray();
            var argumentsNodes = exec.Elements()
                .Where(node => node.Name.LocalName == "Arguments").ToArray();
            if (commands.Length != 1 || argumentsNodes.Length != 1 ||
                commands[0].Name.Namespace != root.Name.Namespace ||
                argumentsNodes[0].Name.Namespace != root.Name.Namespace)
                return false;

            string trustedCmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string command = commands[0].Value.Trim().Trim('"');
            if (!string.Equals(command, trustedCmd, StringComparison.OrdinalIgnoreCase))
                return false;

            string arguments = argumentsNodes[0].Value;
            var switches = GuardCmdSwitches.Match(arguments);
            if (!switches.Success) return false;
            string execution = arguments[switches.Length..];
            // schtasks may retain an outer CMD quote around the quoted script
            // and its parameters. Both forms must still begin with OUR script.
            bool outerQuote = execution.StartsWith("\"\"", StringComparison.Ordinal);
            if (outerQuote) execution = execution[1..];
            string script = "\"" + GuardScriptPath + "\"";
            if (!execution.StartsWith(script, StringComparison.OrdinalIgnoreCase))
                return false;
            string remainder = execution[script.Length..];
            if (remainder.Length < 2 || remainder[0] is not (' ' or '\t'))
                return false;
            remainder = remainder.Trim();
            if (outerQuote)
            {
                if (!remainder.EndsWith('"')) return false;
                remainder = remainder[..^1].TrimEnd();
            }
            if (remainder.Length == 0 || remainder.IndexOfAny(
                    ['\r', '\n', '&', '|', '<', '>', '^', '%', '!', '"']) >= 0)
                return false;
            var parts = remainder.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 11 ||
                !Guid.TryParseExact(parts[0], "D", out var parsed))
                return false;
            // Additional parameters generated by BuildCreateArguments are
            // nonnegative numeric/hex scalars, never free-form shell text.
            if (parts.Skip(1).Any(part => part.Length == 0 ||
                part.Any(ch => !((ch >= '0' && ch <= '9') ||
                                 (ch >= 'a' && ch <= 'f') ||
                                 (ch >= 'A' && ch <= 'F')))))
                return false;
            guid = parsed.ToString("D");
            return true;
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or
                                      InvalidOperationException or System.Security.SecurityException)
        {
            return false;
        }
    }

    // The /Create /F flag is destructive too. Only absent tasks or tasks
    // whose actual XML matches our guard may be replaced. COM read errors
    // propagate instead of being treated as absence.
    internal static void EnsureGuardTasksMayBeReplaced(
        string? logonXml, string? bootXml)
    {
        if ((logonXml is not null && !IsOwnedGuardTaskXml(logonXml)) ||
            (bootXml is not null && !IsOwnedGuardTaskXml(bootXml)))
            throw new InvalidOperationException(
                "Power Plan guard má cizí nebo neočekávanou definici úlohy; přepsání bylo zablokováno.");
    }

    // Validate both before touching either one. Missing tasks are fine,
    // but a replaced, unreadable or structurally foreign task blocks ALL
    // removals and keeps the associated guard script for safe inspection.
    internal static void EnsureGuardTasksMayBeDeleted(
        string? logonXml, string? bootXml)
    {
        if ((logonXml is not null && !IsOwnedGuardTaskXml(logonXml)) ||
            (bootXml is not null && !IsOwnedGuardTaskXml(bootXml)))
            throw new InvalidOperationException(
                "Power Plan guard má cizí nebo neočekávanou definici úlohy; odstranění bylo zablokováno.");
    }

    // Fully synthetic: no COM, schtasks, powercfg, file or registry writes.
    internal static bool RunOwnedGuardTask191Smoke()
    {
        string validXml = new XElement("Task",
            new XElement("Actions",
                new XElement("Exec",
                    new XElement("Command",
                        Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                    new XElement("Arguments",
                        "/D /S /C \"" + GuardScriptPath + "\" 11111111-2222-3333-4444-555555555555"))))
            .ToString(SaveOptions.DisableFormatting);
        string foreignXml = new XElement("Task",
            new XElement("Actions",
                new XElement("Exec",
                    new XElement("Command",
                        Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                    new XElement("Arguments",
                        "/D /S /C \"C:\\OtherVendor\\other-task.cmd\""))))
            .ToString(SaveOptions.DisableFormatting);
        if (!IsOwnedGuardTaskXml(validXml) || IsOwnedGuardTaskXml(foreignXml) ||
            IsOwnedGuardTaskXml(null))
            return false;
        EnsureGuardTasksMayBeDeleted(validXml, validXml);
        EnsureGuardTasksMayBeDeleted(null, validXml);
        EnsureGuardTasksMayBeReplaced(validXml, validXml);
        EnsureGuardTasksMayBeReplaced(null, validXml);
        bool foreignBlocked = false, foreignReplaceBlocked = false;
        try { EnsureGuardTasksMayBeDeleted(validXml, foreignXml); }
        catch (InvalidOperationException) { foreignBlocked = true; }
        try { EnsureGuardTasksMayBeReplaced(validXml, foreignXml); }
        catch (InvalidOperationException) { foreignReplaceBlocked = true; }
        if (!foreignBlocked || !foreignReplaceBlocked) return false;
        int deleted = 0, released = 0;
        object handle = new();
        try
        {
            _ = TaskSchedulerComService.DeleteRegisteredTaskIfPresent(
                () => handle, () => deleted++, _ => released++,
                _ => IsOwnedGuardTaskXml(foreignXml));
            return false;
        }
        catch (InvalidOperationException) { }
        string echoedXml = new XElement("Task",
            new XElement("Actions",
                new XElement("Exec",
                    new XElement("Command", Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                    new XElement("Arguments",
                        "/D /S /C echo \"" + GuardScriptPath +
                        "\" 11111111-2222-3333-4444-555555555555"))))
            .ToString(SaveOptions.DisableFormatting);
        // Compiled executable policy smoke: the old substring admission
        // accepted echo spoofing; /Run previously checked only the task name.
        const string expectedGuid = "11111111-2222-3333-4444-555555555555";
        EnsureGuardTaskMayBeRun(validXml, expectedGuid);
        bool runRejectedForeign = false, runRejectedWrongPlan = false;
        try { EnsureGuardTaskMayBeRun(echoedXml, expectedGuid); }
        catch (InvalidOperationException) { runRejectedForeign = true; }
        try { EnsureGuardTaskMayBeRun(validXml, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"); }
        catch (InvalidOperationException) { runRejectedWrongPlan = true; }
        // Same-handle COM dispatch policy, fully synthetic. No live
        // Schedule.Service connection, task execution, or system mutations.
        object sameTask = new(), runningHandle = new();
        int lookups = 0, reads = 0, launches = 0, releasedTask = 0, releasedRun = 0;
        TaskSchedulerComService.RunRegisteredTaskIfAuthorized(
            () => { lookups++; return sameTask; },
            registered => {
                if (!ReferenceEquals(registered, sameTask))
                    throw new InvalidOperationException("Synthetic COM task identity changed.");
                reads++;
                return validXml;
            },
            xml => EnsureGuardTaskMayBeRun(xml, expectedGuid),
            registered => {
                if (!ReferenceEquals(registered, sameTask))
                    throw new InvalidOperationException("Synthetic COM run identity changed.");
                launches++;
                return runningHandle;
            },
            registered => {
                if (ReferenceEquals(registered, sameTask)) releasedTask++;
                if (ReferenceEquals(registered, runningHandle)) releasedRun++;
            });
        bool sameHandleForeignBlocked = false;
        try
        {
            TaskSchedulerComService.RunRegisteredTaskIfAuthorized(
                () => sameTask,
                _ => echoedXml,
                xml => EnsureGuardTaskMayBeRun(xml, expectedGuid),
                _ => { launches++; return runningHandle; },
                _ => releasedTask++);
        }
        catch (InvalidOperationException) { sameHandleForeignBlocked = true; }

        return deleted == 0 && released == 1 &&
               runRejectedForeign && runRejectedWrongPlan &&
               sameHandleForeignBlocked &&
               lookups == 1 && reads == 1 && launches == 1 &&
               releasedTask == 2 && releasedRun == 1 &&
               !IsOwnedGuardTaskXml(echoedXml) &&
               TaskXmlMatches(validXml, expectedGuid) &&
               !TaskXmlMatches(validXml, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") &&
               RunGuardStatus196Smoke() &&
               RunGuardStatus197Smoke() &&
               RunGuardScript198Smoke() &&
               RunGuardScriptStage199Smoke() &&
               RunGuardDefinition200Smoke() &&
               RunGuardScope201Smoke() &&
               RunGuardGuidConsistency202Smoke();
    }

    public static async Task DisableAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // A task with our old name is not necessarily ours any longer.
        // Preflight BOTH definitions before deleting either task; a replaced
        // or unreadable task must leave the guard script untouched.
        string? logonXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        string? bootXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);
        EnsureGuardTasksMayBeDeleted(logonXml, bootXml);
        // Revalidate XML from the actual COM task handle at delete time to
        // reduce the chance of deleting a concurrently replaced task.
        bool removedLogon = await Task.Run(
            () => TaskSchedulerComService.DeleteIfExists(
                TaskName, IsOwnedGuardTaskXml), token).ConfigureAwait(false);
        bool removedBoot = await Task.Run(
            () => TaskSchedulerComService.DeleteIfExists(
                BootTaskName, IsOwnedGuardTaskXml), token).ConfigureAwait(false);
        if (removedLogon || removedBoot) LogService.Info("power", "Power plan Boot/Logon guard removed.");
        try { if (File.Exists(GuardScriptPath)) File.Delete(GuardScriptPath); }
        catch (Exception ex) { LogService.Error("power", "Power plan guard script cleanup failed", ex); }
        try { if (File.Exists(LegacyGuardScriptPath)) File.Delete(LegacyGuardScriptPath); }
        catch (Exception ex) { LogService.Error("power", "Legacy PowerShell guard cleanup failed", ex); }
    }

    // A partially registered guard can contain only its Boot task. Never
    // report "unconfigured" in that case: duplicate-plan cleanup uses this
    // GUID to decide whether it may delete older power plans. Likewise two
    // conflicting tasks must not silently pick the Logon task's GUID.
    internal static string? ResolveConsistentGuardTaskGuid(
        string? logonXml, string? bootXml)
    {
        string? ReadOwnedGuid(string? xml, string name)
        {
            if (xml is null) return null;
            if (!TryReadOwnedGuardTaskGuid(xml, out string guid))
                throw new InvalidOperationException(
                    "Power Plan guard má cizí nebo nečitelnou definici " +
                    name + " úlohy; identifikace plánu byla zablokována.");
            return guid;
        }

        string? logonGuid = ReadOwnedGuid(logonXml, "Logon");
        string? bootGuid = ReadOwnedGuid(bootXml, "Boot");
        if (logonGuid is not null && bootGuid is not null &&
            !string.Equals(logonGuid, bootGuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Power Plan guard Boot a Logon ukazují na rozdílné GUID; " +
                "automatické čištění napájecích plánů bylo zablokováno.");
        return logonGuid ?? bootGuid;
    }

    // Synthetic smoke: no COM, actual task, power plan or filesystem access.
    internal static bool RunGuardGuidConsistency202Smoke()
    {
        string XmlFor(string guid) => new XElement("Task",
            new XElement("Actions", new XElement("Exec",
                new XElement("Command",
                    Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                new XElement("Arguments",
                    "/D /S /C \"" + GuardScriptPath + "\" " + guid))))
            .ToString(SaveOptions.DisableFormatting);
        const string first = "11111111-2222-3333-4444-555555555555";
        const string second = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        string logon = XmlFor(first);
        string boot = XmlFor(second);
        bool conflictRejected = false, foreignRejected = false;
        try { _ = ResolveConsistentGuardTaskGuid(logon, boot); }
        catch (InvalidOperationException) { conflictRejected = true; }
        try { _ = ResolveConsistentGuardTaskGuid(logon, "<Task>foreign</Task>"); }
        catch (InvalidOperationException) { foreignRejected = true; }
        return ResolveConsistentGuardTaskGuid(null, null) is null &&
               ResolveConsistentGuardTaskGuid(null, logon) == first &&
               ResolveConsistentGuardTaskGuid(logon, null) == first &&
               ResolveConsistentGuardTaskGuid(logon, logon) == first &&
               conflictRejected && foreignRejected;
    }

    public static async Task<string?> TryGetConfiguredGuidAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string? logonXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        string? bootXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);
        return ResolveConsistentGuardTaskGuid(logonXml, bootXml);
    }

    // The latest unfinished write must NOT be skipped in favor of an
    // earlier OK. Never read more than GuardStatusTailLimitBytes, regardless
    // of how many months the CMD task has appended to this status file.
    internal static (string? Line, bool Complete) SelectGuardStatusTail(
        ReadOnlySpan<byte> tail, bool startsMidLine)
    {
        var selected = SelectGuardStatusTailWithOffset(tail, startsMidLine);
        return (selected.Line, selected.Complete);
    }

    // RecordStart is relative to the fixed tail window. A fresh-run receipt
    // accepts only a fully completed line STARTING after the pre-run EOF.
    internal static (string? Line, bool Complete, int RecordStart)
        SelectGuardStatusTailWithOffset(ReadOnlySpan<byte> tail, bool startsMidLine)
    {
        if (tail.IsEmpty) return (null, false, -1);
        int finalNewline = tail.LastIndexOf((byte)'\n');
        if (finalNewline != tail.Length - 1)
        {
            ReadOnlySpan<byte> unfinished = tail[(finalNewline + 1)..];
            if (!IsWhitespaceStatusBytes(unfinished))
                return (DecodeStatusLine(unfinished), false, finalNewline + 1);
        }

        int cursor = finalNewline;
        while (cursor >= 0)
        {
            int previousNewline = tail[..cursor].LastIndexOf((byte)'\n');
            if (previousNewline < 0 && startsMidLine)
                return (null, false, -1); // First record is truncated.
            int recordStart = previousNewline + 1;
            ReadOnlySpan<byte> record = tail[recordStart..cursor];
            if (!IsWhitespaceStatusBytes(record))
                return (DecodeStatusLine(record), true, recordStart);
            cursor = previousNewline;
        }
        return (null, false, -1);
    }

    // Shared offset admission for async status reading and the synchronous
    // release policy smoke; no blocking on asynchronous operations.
    internal static bool IsFreshGuardStatusRecord(
        long windowStart, int recordStart, long requiredOffset) =>
        recordStart >= 0 && windowStart + recordStart >= requiredOffset;
}
