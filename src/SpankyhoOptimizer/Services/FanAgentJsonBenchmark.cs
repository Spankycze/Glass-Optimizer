using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

internal static class FanAgentJsonBenchmark
{
    internal static string RunCompatibilityAndPerformanceSmoke()
    {
        var profile = new FanProfile
        {
            TargetRpm = 1620,
            CpuTempTargetEnabled = true,
            CpuTempTargetC = 65,
            AiSmartRuntimeEnabled = true
        };
        var health = new FanAgentHealth(
            new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc),
            1234, "15.4.1474", true, true,
            new DateTime(2026, 10, 18, 11, 59, 59, DateTimeKind.Utc),
            null, 51.5, new[] { 1620, 1720, 1820, 1920 }, true,
            "Balanced", false, true, 14.2, 53.5, 4100,
            true, "Stable", 0.1, 0.9, 52.3,
            "Stable", "n=32", "No action", "Quiet");

        var pretty = new JsonSerializerOptions { WriteIndented = true };
        string originalHealth = JsonSerializer.Serialize(health);
        string generatedHealth = JsonSerializer.Serialize(
            health, FanAgentCompactJsonContext.Default.FanAgentHealth);
        string originalProfile = JsonSerializer.Serialize(profile, pretty);
        string generatedProfile = JsonSerializer.Serialize(
            profile, FanAgentIndentedJsonContext.Default.FanProfile);
        if (!string.Equals(originalHealth, generatedHealth, StringComparison.Ordinal)
            || !string.Equals(originalProfile, generatedProfile, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "FanAgent source-generated JSON changed the durable format.");

        FanAgentHealth? decodedHealth = JsonSerializer.Deserialize(
            originalHealth, FanAgentCompactJsonContext.Default.FanAgentHealth);
        FanProfile? decodedProfile = JsonSerializer.Deserialize(
            originalProfile, FanAgentCompactJsonContext.Default.FanProfile);
        if (decodedHealth is null || decodedProfile is null
            || decodedHealth.Rpms.Length != 4
            || decodedHealth.Rpms[3] != 1920
            || decodedHealth.CpuTempC != 51.5
            || decodedProfile.TargetRpm != 1620
            || !decodedProfile.CpuTempTargetEnabled)
            throw new InvalidOperationException(
                "FanAgent source-generated deserialization changed profile or health values.");

        // Validate the existing on-disk user profile without writing to it.
        // Synthetic compatibility remains mandatory if this optional read fails.
        bool realProfileCompared = false;
        string realProfilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpankyhoOptimizer", "fan-profile.json");
        if (System.IO.File.Exists(realProfilePath))
        {
            try
            {
                string saved = System.IO.File.ReadAllText(realProfilePath);
                FanProfile? reflectionSaved = JsonSerializer.Deserialize<FanProfile>(saved);
                FanProfile? generatedSaved = JsonSerializer.Deserialize(
                    saved, FanAgentCompactJsonContext.Default.FanProfile);
                if (reflectionSaved is null || generatedSaved is null ||
                    JsonSerializer.Serialize(reflectionSaved, pretty) !=
                    JsonSerializer.Serialize(generatedSaved,
                        FanAgentIndentedJsonContext.Default.FanProfile))
                    throw new InvalidOperationException(
                        "Persisted fan-profile.json is not roundtrip-compatible.");
                realProfileCompared = true;
            }
            catch (System.IO.IOException) { /* External profile writer may race a read. */ }
            catch (UnauthorizedAccessException) { /* Never elevate for this test. */ }
        }

        const int warmup = 64;
        for (int i = 0; i < warmup; i++)
        {
            _ = JsonSerializer.Serialize(health);
            _ = JsonSerializer.Serialize(health, FanAgentCompactJsonContext.Default.FanAgentHealth);
            _ = JsonSerializer.Serialize(profile, pretty);
            _ = JsonSerializer.Serialize(profile, FanAgentIndentedJsonContext.Default.FanProfile);
        }

        const int iterations = 3000;
        static (double ms, long allocatedBytes) Measure(int count, Func<string> action)
        {
            long startAllocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            int totalLength = 0;
            for (int i = 0; i < count; i++)
                totalLength += action().Length;
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - startAllocated;
            if (totalLength == 0)
                throw new InvalidOperationException("JSON benchmark produced no output.");
            return (Math.Round(elapsedMs, 2), allocated);
        }

        static (double ms, long allocatedBytes) MeasureStream(int count,
            Action<Stream> write)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            long bytes = 0;
            for (int i = 0; i < count; i++)
            {
                using var memory = new MemoryStream(2048);
                write(memory);
                bytes += memory.Length;
            }
            if (bytes == 0) throw new InvalidOperationException("Stream benchmark wrote no data.");
            return (Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 2),
                GC.GetAllocatedBytesForCurrentThread() - before);
        }

        var reflectionHealth = Measure(iterations, () => JsonSerializer.Serialize(health));
        var generatedHealthPerf = Measure(iterations, () =>
            JsonSerializer.Serialize(health, FanAgentCompactJsonContext.Default.FanAgentHealth));
        var healthStreamPerf = MeasureStream(iterations, stream =>
            JsonSerializer.Serialize(stream, health,
                FanAgentCompactJsonContext.Default.FanAgentHealth));
        var reflectionProfile = Measure(iterations, () => JsonSerializer.Serialize(profile, pretty));
        var generatedProfilePerf = Measure(iterations, () =>
            JsonSerializer.Serialize(profile, FanAgentIndentedJsonContext.Default.FanProfile));
        return $"FAN_JSON_COMPAT_BENCH_OK samples={iterations} realProfileCompared={realProfileCompared} " +
               $"healthChars={originalHealth.Length} profileChars={originalProfile.Length} " +
               $"healthReflectionMs={reflectionHealth.ms} healthGeneratedMs={generatedHealthPerf.ms} " +
               $"healthStreamMs={healthStreamPerf.ms} healthStreamAlloc={healthStreamPerf.allocatedBytes} " +
               $"healthReflectionAlloc={reflectionHealth.allocatedBytes} " +
               $"healthGeneratedAlloc={generatedHealthPerf.allocatedBytes} " +
               $"profileReflectionMs={reflectionProfile.ms} profileGeneratedMs={generatedProfilePerf.ms} " +
               $"profileReflectionAlloc={reflectionProfile.allocatedBytes} " +
               $"profileGeneratedAlloc={generatedProfilePerf.allocatedBytes}";
    }
}
