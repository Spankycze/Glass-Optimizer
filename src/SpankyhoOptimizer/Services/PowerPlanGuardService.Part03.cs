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
    private static bool IsWhitespaceStatusBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (byte ch in bytes)
            if (ch is not (0x20 or 0x09 or 0x0D or 0x0A))
                return false;
        return true;
    }

    private static string DecodeStatusLine(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > GuardStatusLineDisplayLimitBytes)
            bytes = bytes[^GuardStatusLineDisplayLimitBytes..];
        return Encoding.UTF8.GetString(bytes).Trim().TrimStart('\uFEFF');
    }

    internal static async Task<(string? Line, bool Complete)> ReadGuardStatusTailAsync(
        Stream source, CancellationToken token = default, long minimumRecordOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new InvalidOperationException("Power guard status stream musí být čitelný a seekovatelný.");
        ArgumentOutOfRangeException.ThrowIfNegative(minimumRecordOffset);
        token.ThrowIfCancellationRequested();
        long length = source.Length;
        int count = (int)Math.Min(length, GuardStatusTailLimitBytes);
        if (count == 0) return (null, false);
        long start = length - count;
        source.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[count];
        await source.ReadExactlyAsync(buffer.AsMemory(), token).ConfigureAwait(false);
        var result = SelectGuardStatusTailWithOffset(buffer, startsMidLine: start != 0);
        if (minimumRecordOffset > 0 && result.Complete &&
            !IsFreshGuardStatusRecord(start, result.RecordStart, minimumRecordOffset))
            return (result.Line, false);
        return (result.Line, result.Complete);
    }

    internal static bool IsSuccessfulGuardStatusLine(
        string? line, bool complete, string? expectedGuid)
    {
        if (!complete || string.IsNullOrWhiteSpace(line) ||
            !Guid.TryParseExact(expectedGuid, "D", out var expected) ||
            expected == Guid.Empty)
            return false;
        var match = GuardSuccessLineRegex.Match(line);
        return match.Success &&
               Guid.TryParseExact(match.Groups["guid"].Value, "D", out var reported) &&
               expected == reported;
    }

    // Safe compiled release policy smoke. Pure byte buffers only; no COM,
    // status file I/O, guard execution or Windows configuration writes.
    internal static bool RunGuardStatus196Smoke()
    {
        const string expected = "11111111-2222-3333-4444-555555555555";
        string ok = "[25.10.2026 04:00:01] OK guid=" + expected +
            " repaired=1 attempt=1";
        string fail = "[25.10.2026 04:00:02] FAIL guid=" + expected +
            " reason=plan-missing";
        var valid = SelectGuardStatusTail(Encoding.UTF8.GetBytes(ok + "\r\n"), false);
        var latestFailure = SelectGuardStatusTail(
            Encoding.UTF8.GetBytes(ok + "\r\n" + fail + "\r\n"), false);
        var incomplete = SelectGuardStatusTail(
            Encoding.UTF8.GetBytes(ok + "\r\n" + fail), false);
        return valid.Complete &&
               IsSuccessfulGuardStatusLine(valid.Line, valid.Complete, expected) &&
               latestFailure.Complete &&
               !IsSuccessfulGuardStatusLine(
                   latestFailure.Line, latestFailure.Complete, expected) &&
               !incomplete.Complete &&
               !IsSuccessfulGuardStatusLine(
                   incomplete.Line, incomplete.Complete, expected) &&
               !IsSuccessfulGuardStatusLine(
                   "note OK guid=" + expected, true, expected) &&
               !IsSuccessfulGuardStatusLine(ok, true,
                   "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    }

    // Synthetic release smoke for staging-path and reparse-point policy.
    // Does not touch ProgramData, files, Windows tasks or privileges.
    internal static bool RunGuardScriptStage199Smoke()
    {
        EnsureSafeGuardScriptAttributes(FileAttributes.Directory, null);
        EnsureSafeGuardScriptAttributes(FileAttributes.Directory, FileAttributes.Normal);
        bool rejected = false;
        try
        {
            EnsureSafeGuardScriptAttributes(
                FileAttributes.Directory | FileAttributes.ReparsePoint,
                FileAttributes.Normal);
        }
        catch (InvalidOperationException) { rejected = true; }
        bool fileRejected = false;
        try
        {
            EnsureSafeGuardScriptAttributes(
                FileAttributes.Directory, FileAttributes.ReparsePoint);
        }
        catch (InvalidOperationException) { fileRejected = true; }
        const string testDirectory = @"C:\GuardSynthetic";
        string stage = BuildGuardScriptStagePath(testDirectory, "a1b2");
        return rejected && fileRejected &&
               string.Equals(
                   Path.GetDirectoryName(stage), testDirectory,
                   StringComparison.OrdinalIgnoreCase) &&
               stage.EndsWith("power-plan-guard-a1b2.tmp", StringComparison.Ordinal) &&
               !stage.EndsWith("power-plan-guard.cmd.new", StringComparison.Ordinal);
    }

    // Pure release smoke for guard CMD integrity: no disk or task access.
    internal static bool RunGuardScript198Smoke()
    {
        byte[] valid = Encoding.UTF8.GetBytes(BuildGuardScript().Replace("\r\n", "\n"));
        byte[] altered = Encoding.UTF8.GetBytes(BuildGuardScript() + "\nrem different");
        return IsExpectedGuardScriptContent(valid) &&
               !IsExpectedGuardScriptContent(altered) &&
               !IsExpectedGuardScriptContent(Array.Empty<byte>());
    }

    // Compiled, in-memory release smoke: a historical OK is not evidence
    // for a newly requested task Run. No Scheduler/file/registry changes.
    internal static bool RunGuardStatus197Smoke()
    {
        const string guid = "11111111-2222-3333-4444-555555555555";
        string oldOk = "[25.10.2026 04:00:01] OK guid=" + guid +
            " repaired=0 attempt=1\r\n";
        long cursor = Encoding.UTF8.GetByteCount(oldOk);
        var stale = SelectGuardStatusTailWithOffset(
            Encoding.UTF8.GetBytes(oldOk), startsMidLine: false);
        var fresh = SelectGuardStatusTailWithOffset(
            Encoding.UTF8.GetBytes(oldOk + oldOk), startsMidLine: false);
        return stale.Complete &&
               !IsFreshGuardStatusRecord(0, stale.RecordStart, cursor) &&
               fresh.Complete &&
               IsFreshGuardStatusRecord(0, fresh.RecordStart, cursor) &&
               IsSuccessfulGuardStatusLine(fresh.Line, fresh.Complete, guid);
    }

    public static async Task<PowerPlanGuardStatus> ReadStatusAsync(
        string? expectedGuid = null,
        CancellationToken token = default,
        long minimumStatusRecordOffset = 0)
    {
        token.ThrowIfCancellationRequested();

        string? logonXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(TaskName), token).ConfigureAwait(false);
        string? bootXml = await Task.Run(
            () => TaskSchedulerComService.TryGetTaskXml(BootTaskName), token).ConfigureAwait(false);

        string? configuredGuid = TryReadOwnedGuardTaskGuid(logonXml, out string logonGuid)
            ? logonGuid
            : TryReadOwnedGuardTaskGuid(bootXml, out string bootGuid) ? bootGuid : null;

        string normalizedExpected = Guid.TryParse(expectedGuid, out var parsed)
            ? parsed.ToString("D")
            : configuredGuid ?? string.Empty;

        bool logonOk = GuardTaskDefinitionMatches(logonXml, normalizedExpected, boot: false);
        bool bootOk = GuardTaskDefinitionMatches(bootXml, normalizedExpected, boot: true);

        string last = "Guard zatím nemá uložený výsledek.";
        bool completeStatusRecord = false;
        try
        {
            if (File.Exists(GuardStatusPath))
            {
                await using var stream = new FileStream(
                    GuardStatusPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var status = await ReadGuardStatusTailAsync(
                    stream, token, minimumStatusRecordOffset).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(status.Line))
                    last = status.Line;
                completeStatusRecord = status.Complete;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("power", "Power plan guard status read failed", ex);
        }

        bool lastOk = IsSuccessfulGuardStatusLine(
            last, completeStatusRecord, normalizedExpected);
        return new PowerPlanGuardStatus(configuredGuid, logonOk, bootOk, last, lastOk);
    }

    internal static bool TaskXmlMatches(string? xml, string guid) =>
        Guid.TryParse(guid, out var expected) &&
        TryReadOwnedGuardTaskGuid(xml, out string actual) &&
        string.Equals(actual, expected.ToString("D"), StringComparison.OrdinalIgnoreCase);

    // Ownership of an Exec action does not establish that our automatic
    // guard will actually run as SYSTEM at ONSTART / ONLOGON. Inspect the
    // *real* root-level triggers/principal/settings rather than descriptions
    // or other metadata. Keep this distinct from ownership admission:
    // a damaged owned task must remain safe to replace/delete.
    internal static bool GuardTaskDefinitionMatches(
        string? xml, string expectedGuid, bool boot)
    {
        if (!TaskXmlMatches(xml, expectedGuid)) return false;
        try
        {
            var settings = CreateTaskXmlReaderSettings();
            using var reader = XmlReader.Create(new StringReader(xml!), settings);
            var doc = XDocument.Load(reader);
            if (doc.Root is not { } root || root.Name.LocalName != "Task")
                return false;
            XNamespace ns = root.Name.Namespace;

            // Exactly one root-level Triggers containing only our trigger.
            var triggersNodes = doc.Descendants(ns + "Triggers").ToArray();
            if (triggersNodes.Length != 1 || triggersNodes[0].Parent != root)
                return false;
            XElement[] triggers = triggersNodes[0].Elements().ToArray();
            if (triggers.Length != 1 ||
                triggers[0].Name != ns + (boot ? "BootTrigger" : "LogonTrigger"))
                return false;
            XElement trigger = triggers[0];
            if (!GuardXmlEnabled(trigger, ns)) return false;
            // /SC ONLOGON without a user filter must cover EVERY logon.
            // An EndBoundary would silently stop our automatic protection.
            // Repetition would schedule unintended extra SYSTEM runs.
            if (trigger.Elements(ns + "UserId").Any() ||
                trigger.Elements(ns + "EndBoundary").Any() ||
                trigger.Elements(ns + "Repetition").Any())
                return false;
            var delay = trigger.Elements(ns + "Delay").ToArray();
            if (delay.Length != 1 ||
                XmlConvert.ToTimeSpan(delay[0].Value) !=
                TimeSpan.FromSeconds(boot ? 15 : 30))
                return false;

            // Scheduler's built-in LocalSystem SID, with its common
            // account-name serializations. Reject GroupId or another user.
            var principalNodes = doc.Descendants(ns + "Principals").ToArray();
            if (principalNodes.Length != 1 ||
                principalNodes[0].Parent != root)
                return false;
            XElement[] principals = principalNodes[0].Elements().ToArray();
            if (principals.Length != 1 || principals[0].Name != ns + "Principal")
                return false;
            var users = principals[0].Elements(ns + "UserId").ToArray();
            var levels = principals[0].Elements(ns + "RunLevel").ToArray();
            var logonTypes = principals[0].Elements(ns + "LogonType").ToArray();
            if (users.Length != 1 || levels.Length != 1 ||
                logonTypes.Length > 1 ||
                (logonTypes.Length == 1 &&
                 !string.Equals(logonTypes[0].Value.Trim(), "ServiceAccount",
                     StringComparison.OrdinalIgnoreCase)) ||
                principals[0].Elements(ns + "GroupId").Any())
                return false;
            string user = users[0].Value.Trim();
            if (!string.Equals(user, "S-1-5-18", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(user, "SYSTEM", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(user, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(levels[0].Value.Trim(), "HighestAvailable",
                    StringComparison.OrdinalIgnoreCase))
                return false;

            // An explicitly disabled task will not run at its trigger.
            var settingsNodes = doc.Descendants(ns + "Settings").ToArray();
            return settingsNodes.Length <= 1 &&
                   (settingsNodes.Length == 0 ||
                    (settingsNodes[0].Parent == root &&
                     GuardXmlEnabled(settingsNodes[0], ns)));
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or
                                      FormatException or OverflowException or
                                      InvalidOperationException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool GuardXmlEnabled(XElement element, XNamespace ns)
    {
        XElement[] enabled = element.Elements(ns + "Enabled").ToArray();
        return enabled.Length == 0 ||
               (enabled.Length == 1 &&
                bool.TryParse(enabled[0].Value.Trim(), out bool value) && value);
    }

    // Compiled service policy smoke: inspect only constructed XML trees.
    // No Scheduler COM, processes, files or Windows mutations.
    internal static bool RunGuardDefinition200Smoke()
    {
        const string guid = "11111111-2222-3333-4444-555555555555";
        string command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        XElement task = new("Task",
            new XElement("Triggers",
                new XElement("LogonTrigger", new XElement("Delay", "PT30S"))),
            new XElement("Principals",
                new XElement("Principal",
                    new XElement("UserId", "S-1-5-18"),
                    new XElement("RunLevel", "HighestAvailable"))),
            new XElement("Settings", new XElement("Enabled", "true")),
            new XElement("Actions",
                new XElement("Exec",
                    new XElement("Command", command),
                    new XElement("Arguments", "/D /S /C \"" +
                        GuardScriptPath + "\" " + guid))));
        string valid = task.ToString(SaveOptions.DisableFormatting);
        if (!GuardTaskDefinitionMatches(valid, guid, boot: false)) return false;
        task.Element("Triggers")!.Element("LogonTrigger")!
            .Element("Delay")!.Value = "PT15S";
        if (GuardTaskDefinitionMatches(
            task.ToString(SaveOptions.DisableFormatting), guid, boot: false))
            return false;
        task.Element("Triggers")!.Element("LogonTrigger")!
            .Element("Delay")!.Value = "PT30S";
        task.Element("Principals")!.Element("Principal")!
            .Element("UserId")!.Value = "S-1-5-19";
        return !GuardTaskDefinitionMatches(
            task.ToString(SaveOptions.DisableFormatting), guid, boot: false);
    }

    // Release smoke for all-user trigger scope and SYSTEM logon type.
    // Only synthetic task XML is inspected; no Windows tasks are touched.
    internal static bool RunGuardScope201Smoke()
    {
        const string guid = "11111111-2222-3333-4444-555555555555";
        XElement task = new("Task",
            new XElement("Triggers",
                new XElement("LogonTrigger",
                    new XElement("Delay", "PT30S"))),
            new XElement("Principals",
                new XElement("Principal",
                    new XElement("UserId", "S-1-5-18"),
                    new XElement("LogonType", "ServiceAccount"),
                    new XElement("RunLevel", "HighestAvailable"))),
            new XElement("Actions", new XElement("Exec",
                new XElement("Command",
                    Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                new XElement("Arguments",
                    "/D /S /C \"" + GuardScriptPath + "\" " + guid))));
        if (!GuardTaskDefinitionMatches(
            task.ToString(SaveOptions.DisableFormatting), guid, boot: false))
            return false;
        var trigger = task.Element("Triggers")!.Element("LogonTrigger")!;
        trigger.Add(new XElement("UserId", "S-1-5-21-1-2-3-1001"));
        if (GuardTaskDefinitionMatches(
            task.ToString(SaveOptions.DisableFormatting), guid, boot: false))
            return false;
        trigger.Element("UserId")!.Remove();
        task.Element("Principals")!.Element("Principal")!
            .Element("LogonType")!.Value = "Password";
        return !GuardTaskDefinitionMatches(
            task.ToString(SaveOptions.DisableFormatting), guid, boot: false);
    }

    internal static void RunArgumentSmoke()
    {
        const string guid = "11111111-2222-3333-4444-555555555555";
        string args = BuildCreateArguments(guid, boot: false);
        string bootArgs = BuildCreateArguments(guid, boot: true);
        string script = BuildGuardScript();

        var missing = new System.Collections.Generic.List<string>();
        if (!args.Contains(TaskName, StringComparison.Ordinal)) missing.Add("task-name");
        if (!args.Contains("/SC ONLOGON", StringComparison.OrdinalIgnoreCase)) missing.Add("onlogon");
        if (!args.Contains("/DELAY 0000:30", StringComparison.OrdinalIgnoreCase)) missing.Add("logon-delay");
        if (!bootArgs.Contains(BootTaskName, StringComparison.Ordinal)) missing.Add("boot-task-name");
        if (!bootArgs.Contains("/SC ONSTART", StringComparison.OrdinalIgnoreCase)) missing.Add("onstart");
        if (!bootArgs.Contains("/DELAY 0000:15", StringComparison.OrdinalIgnoreCase)) missing.Add("boot-delay");
        if (!args.Contains("/RU SYSTEM", StringComparison.OrdinalIgnoreCase)) missing.Add("run-as-system");
        if (!args.Contains("/RL HIGHEST", StringComparison.OrdinalIgnoreCase)) missing.Add("highest");
        if (!args.Contains("power-plan-guard.cmd", StringComparison.OrdinalIgnoreCase)) missing.Add("cmd-file");
        if (!args.Contains(guid, StringComparison.OrdinalIgnoreCase)) missing.Add("guid");
        if (!args.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase)) missing.Add("cmd-exe");
        if (!script.Contains("SUB_DISK", StringComparison.Ordinal)) missing.Add("disk");
        if (!script.Contains("VIDEOIDLE", StringComparison.Ordinal)) missing.Add("display");
        if (!script.Contains("STANDBYIDLE", StringComparison.Ordinal)) missing.Add("sleep");
        if (!script.Contains("HIBERNATEIDLE", StringComparison.Ordinal)) missing.Add("hibernate");
        if (!script.Contains("/setactive %GUID%", StringComparison.OrdinalIgnoreCase)) missing.Add("setactive");
        if (!script.Contains("/getactivescheme", StringComparison.OrdinalIgnoreCase)) missing.Add("active-readback");
        if (!script.Contains("for /L %%I in (1,1,3)", StringComparison.OrdinalIgnoreCase)) missing.Add("retry");
        if (!script.Contains("power-plan-guard-status.log", StringComparison.OrdinalIgnoreCase)) missing.Add("status-log");
        // CMD exposes positional parameters 10 and 11 only after shifting.
        // Without these captures, the boot/logon task silently hardcodes the
        // performance check interval (15 ms) even for mobile profiles (20 ms).
        if (!script.Contains("set \"PERFCHECK=%~9\"", StringComparison.Ordinal) ||
            !script.Contains("set \"PERFCHECKHEX=%~9\"", StringComparison.Ordinal) ||
            script.Split("shift /8", StringSplitOptions.None).Length != 3)
            missing.Add("perf-check-arguments-10-11");
        if (!script.Contains("%PERFCHECK% %PERFCHECKHEX% perf-check", StringComparison.Ordinal) ||
            script.Contains("15 0000000f perf-check", StringComparison.Ordinal))
            missing.Add("perf-check-not-hardcoded");
        if (!args.Contains(
            $"{PerformanceProfilesService.GetAdaptivePowerProfile().PerfCheckMs} {PerformanceProfilesService.GetAdaptivePowerProfile().PerfCheckMs:x8}",
            StringComparison.OrdinalIgnoreCase))
            missing.Add("scheduler-perf-check-forwarding");

        if (missing.Count != 0)
            throw new InvalidOperationException(
                "Power Plan guard argument/script smoke failed: " + string.Join(",", missing) +
                " | args=" + args.Replace(Environment.NewLine, " ") +
                " | script=" + script.Replace(Environment.NewLine, " | "));

        LogService.Info("POWER_PLAN_GUARD_ARGUMENT_SMOKE_OK criticalNever=4 backend=cmd");
    }
}
