using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record StorageOptimizationSnapshot(
    bool? TrimEnabled,
    int? LastAccessMode,
    string LastAccessText,
    int? EightDotThreeMode,
    string EightDotThreeText,
    bool? ScheduledOptimizeEnabled,
    string WriteCacheText);

public static class StorageOptimizationService
{
    private const string ScheduledDefrag = @"\Microsoft\Windows\Defrag\ScheduledDefrag";

    public static async Task<StorageOptimizationSnapshot> ReadAsync(CancellationToken token = default)
    {
        var trimTask = ProcessRunner.RunAsync(
            "fsutil.exe", "behavior query DisableDeleteNotify", TimeSpan.FromSeconds(12), token);
        var lastTask = ProcessRunner.RunAsync(
            "fsutil.exe", "behavior query disablelastaccess", TimeSpan.FromSeconds(12), token);
        var eightTask = ProcessRunner.RunAsync(
            "fsutil.exe", "8dot3name query C:", TimeSpan.FromSeconds(12), token);
        var taskState = ReadScheduledOptimizeAsync(token);

        await Task.WhenAll(trimTask, lastTask, eightTask, taskState).ConfigureAwait(false);

        ProcessResult trim = await trimTask.ConfigureAwait(false);
        ProcessResult last = await lastTask.ConfigureAwait(false);
        ProcessResult eight = await eightTask.ConfigureAwait(false);

        bool? trimEnabled = trim.Success ? ParseTrimEnabled(trim.StdOut + trim.StdErr) : null;
        int? lastMode = last.Success ? ParseFirstInt(last.StdOut + last.StdErr, @"DisableLastAccess\s*=\s*(\d+)") : null;
        int? eightMode = eight.Success ? ParseEightDotThree(eight.StdOut + eight.StdErr) : null;

        return new StorageOptimizationSnapshot(
            trimEnabled,
            lastMode,
            FormatLastAccess(lastMode),
            eightMode,
            FormatEightDotThree(eightMode),
            await taskState.ConfigureAwait(false),
            "Windows / driver managed • buffer flushing ponechat zapnutý");
    }

    public static async Task<StorageOptimizationSnapshot> ApplyRecommendedAsync(CancellationToken token = default)
    {
        var before = await ReadAsync(token).ConfigureAwait(false);
        bool trimChanged = false;
        bool lastChanged = false;
        bool taskChanged = false;

        try
        {
            if (before.TrimEnabled == false)
            {
                await RequireFsutilAsync("behavior set DisableDeleteNotify 0", token).ConfigureAwait(false);
                trimChanged = true;
            }

            if (before.LastAccessMode.HasValue && !IsLastAccessDisabled(before.LastAccessMode.Value))
            {
                await RequireFsutilAsync("behavior set disablelastaccess 1", token).ConfigureAwait(false);
                lastChanged = true;
            }

            if (before.ScheduledOptimizeEnabled == false)
            {
                await new TaskSchedulerService().SetEnabledAsync(ScheduledDefrag, true, token).ConfigureAwait(false);
                taskChanged = true;
            }

            var after = await ReadAsync(token).ConfigureAwait(false);
            if (before.TrimEnabled == false && after.TrimEnabled != true)
                throw new InvalidOperationException("TRIM read-back nepotvrdil Enabled.");
            if (before.LastAccessMode.HasValue && !IsLastAccessDisabled(before.LastAccessMode.Value) &&
                (!after.LastAccessMode.HasValue || !IsLastAccessDisabled(after.LastAccessMode.Value)))
                throw new InvalidOperationException("Last Access read-back nepotvrdil Disabled.");
            if (before.ScheduledOptimizeEnabled == false && after.ScheduledOptimizeEnabled != true)
                throw new InvalidOperationException("Scheduled Optimize read-back nepotvrdil Enabled.");

            LogService.Info("storage",
                $"STORAGE_RECOMMENDED_OK trim={after.TrimEnabled} lastAccess={after.LastAccessMode} scheduled={after.ScheduledOptimizeEnabled}");
            return after;
        }
        catch
        {
            try
            {
                if (taskChanged && before.ScheduledOptimizeEnabled.HasValue)
                    await new TaskSchedulerService().SetEnabledAsync(
                        ScheduledDefrag, before.ScheduledOptimizeEnabled.Value, CancellationToken.None).ConfigureAwait(false);
                if (lastChanged && before.LastAccessMode.HasValue)
                    await RequireFsutilAsync(
                        $"behavior set disablelastaccess {before.LastAccessMode.Value}", CancellationToken.None).ConfigureAwait(false);
                if (trimChanged && before.TrimEnabled.HasValue)
                    await RequireFsutilAsync(
                        $"behavior set DisableDeleteNotify {(before.TrimEnabled.Value ? 0 : 1)}", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                LogService.Error("storage", "Storage Recommended rollback failed", rollbackEx);
            }
            throw;
        }
    }

    private static async Task<bool?> ReadScheduledOptimizeAsync(CancellationToken token)
    {
        try
        {
            return await Task.Run(
                () => TaskSchedulerComService.GetEnabled(ScheduledDefrag, token),
                token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Error("storage", "Scheduled Optimize read-back failed", ex);
            return null;
        }
    }

    private static async Task RequireFsutilAsync(string args, CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync(
            "fsutil.exe", args, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException((result.StdErr + Environment.NewLine + result.StdOut).Trim());
    }

    private static bool? ParseTrimEnabled(string text)
    {
        var matches = Regex.Matches(text, @"DisableDeleteNotify\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        if (matches.Count == 0) return null;
        foreach (Match match in matches)
            if (int.TryParse(match.Groups[1].Value, out int value) && value == 0) return true;
        return false;
    }

    private static int? ParseFirstInt(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : null;
    }

    private static int? ParseEightDotThree(string text)
    {
        int? value = ParseFirstInt(text, @"volume state is\s*:\s*(\d+)");
        if (value.HasValue) return value;
        return ParseFirstInt(text, @"8dot3name creation state is\s*:\s*(\d+)");
    }

    private static bool IsLastAccessDisabled(int mode) => mode is 1 or 3;

    private static string FormatLastAccess(int? mode) => mode switch
    {
        0 => "Enabled (user managed)",
        1 => "Disabled (user managed)",
        2 => "Enabled (system managed)",
        3 => "Disabled (system managed)",
        _ => "Nezjištěno"
    };

    private static string FormatEightDotThree(int? mode) => mode switch
    {
        0 => "Enabled on all volumes",
        1 => "Disabled on all volumes",
        2 => "Per-volume",
        3 => "Disabled except system volume",
        _ => "Nezjištěno / per-volume state"
    };
}
