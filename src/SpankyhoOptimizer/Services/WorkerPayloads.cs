using System;
using System.Text;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

internal sealed record ServiceChangeRequest(string Name, uint StartType);
internal sealed record ServiceBatchRequest(ServiceChangeRequest[] Items, bool SkipMissing = false);
internal sealed record DebloatPackageRequest(string PackageFullName, bool AllUsers = false);
internal sealed record FeatureRequest(string Name, bool Disable = false);
internal sealed record AiWorkerRequest(
    string Option,
    bool Revert,
    bool Backup,
    string[]? InstallClassicApps = null,
    bool RunWinUpdateRepair = false,
    bool AllOptions = false,
    bool ExcludeOptions = false);
internal sealed record AiWorkerResult(int ExitCode, string StdOut, string StdErr);
internal sealed record TweakWorkerRequest(string Id, bool Enabled);
internal sealed record TweakWorkerResult(bool Actual, string Status, string? Error = null);
internal sealed record PrivilegedProcessRequest(string File, string Arguments, int TimeoutSeconds);
internal sealed record BooleanMutationRequest(bool Enabled);
internal sealed record TaskStateChangeRequest(string Path, bool Enabled);
internal sealed record TaskStateBatchRequest(TaskStateChangeRequest[] Items);
internal sealed record StartupStateChangeRequest(StartupEntry Item, bool Enabled);
internal sealed record StartupDeleteRequest(StartupEntry Item);
internal sealed record ModernizerAdaptiveRequest(AdaptiveScanReport Report);
internal sealed record WorkerAck(bool Ok = true);
internal sealed record DefaultGuardMachineRepairRequest(string[] Groups);
internal sealed record DefaultGuardMachineRepairResult(int DetectedBefore, int RemainingAfter, int TrackedValues, int UnverifiedAfter, int AppliedChanges);

internal static class WorkerPayloads
{
    public static string Encode<T>(T value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    public static T Decode<T>(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Worker payload is missing.");
        string json = Encoding.UTF8.GetString(Convert.FromBase64String(value));
        return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("Worker payload is invalid.");
    }
}
