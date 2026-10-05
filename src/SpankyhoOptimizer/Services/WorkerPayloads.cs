using System;
using System.Text;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

internal sealed record ServiceChangeRequest(string Name, uint StartType);
internal sealed record ServiceBatchRequest(ServiceChangeRequest[] Items);
internal sealed record DebloatPackageRequest(string PackageFullName, bool AllUsers = false);
internal sealed record FeatureRequest(string Name, bool Disable = false);
internal sealed record AiWorkerRequest(string Option, bool Revert, bool Backup);
internal sealed record AiWorkerResult(int ExitCode, string StdOut, string StdErr);
internal sealed record WorkerAck(bool Ok = true);

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
