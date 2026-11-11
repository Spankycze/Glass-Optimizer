using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static class PersistentHardwareCacheService
{
    internal sealed record CacheEnvelope<T>(DateTime SavedUtc, string Fingerprint, T Value);
    private sealed record MachineStableMemoryEntry(DateTime SavedUtc, string Fingerprint, object Value, Type ValueType);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, MachineStableMemoryEntry> MachineStableMemory = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> Fingerprint = new(BuildFingerprintCore, isThreadSafe: true);
    private static readonly Lazy<string> MachineStableFingerprint = new(BuildMachineStableFingerprintCore, isThreadSafe: true);
    // Source-generated System.Text.Json metadata still pays a noticeable first-use
    // initialization cost (~22 ms). Warm only the metadata graph off the UI thread;
    // no file/registry/WMI work is performed here.
    private static int _jsonMetadataWarmQueued;
    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer", "HardwareCache");

    internal static void QueueJsonMetadataWarmup()
    {
        if (System.Threading.Interlocked.Exchange(ref _jsonMetadataWarmQueued, 1) != 0) return;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                _ = HardwareCacheJsonContext.Default.CacheEnvelopeHardwareIdentity;
                _ = HardwareCacheJsonContext.Default.GetTypeInfo(typeof(CacheEnvelope<string>));
            }
            catch (Exception ex)
            {
                LogService.Error("hardware-cache", "JSON metadata warmup failed", ex);
            }
        });
    }

    internal static bool IsValidCacheAge(TimeSpan age, TimeSpan maxAge) =>
        age >= TimeSpan.Zero && age <= maxAge;

    public static bool TryRead<T>(string key, TimeSpan maxAge, out T? value)
    {
        value = default;
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path)) return false;
            var envelope = JsonSerializer.Deserialize<CacheEnvelope<T>>(File.ReadAllText(path), JsonOptions);
            if (envelope == null || !IsValidCacheAge(DateTime.UtcNow - envelope.SavedUtc, maxAge)) return false;
            if (!string.Equals(envelope.Fingerprint, Fingerprint.Value, StringComparison.Ordinal)) return false;
            value = envelope.Value;
            return value != null;
        }
        catch { return false; }
    }
    // A shared fixed ".tmp" name let the GUI and a RuntimeHost overwrite or
    // move each other's in-flight payload for the same cache key. Unique
    // sidecars keep each writer atomic across processes.
    private static string NewTempPath(string path) =>
        path + ".tmp." + Guid.NewGuid().ToString("N");

    // Only reclaim sidecars that no live writer can still own.
    private static void RemoveStaleTempSiblings(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            foreach (string stale in Directory.EnumerateFiles(dir, Path.GetFileName(path) + ".tmp.*"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) > TimeSpan.FromMinutes(10))
                        File.Delete(stale);
                }
                catch { }
            }
        }
        catch { }
    }

    public static void Write<T>(string key, T value)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(CacheDir);
                string path = PathFor(key);
                RemoveStaleTempSiblings(path);
                string temp = NewTempPath(path);
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(new CacheEnvelope<T>(DateTime.UtcNow, Fingerprint.Value, value), JsonOptions));
                    File.Move(temp, path, true);
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
        }
        catch (Exception ex) { LogService.Error("hardware-cache", "Persistent cache write failed: " + key, ex); }
    }

    internal static bool TryReadMachineStable<T>(string key, TimeSpan maxAge, out T? value)
        => TryReadMachineStable(key, maxAge, out value, out _);

    internal static bool TryReadMachineStable<T>(string key, TimeSpan maxAge, out T? value, out TimeSpan age)
    {
        value = default;
        age = TimeSpan.MaxValue;
        try
        {
            lock (Gate)
            {
                if (MachineStableMemory.TryGetValue(key, out var cached) &&
                    cached.ValueType == typeof(T))
                {
                    age = DateTime.UtcNow - cached.SavedUtc;
                    if (IsValidCacheAge(age, maxAge) &&
                        string.Equals(cached.Fingerprint, MachineStableFingerprint.Value, StringComparison.Ordinal))
                    {
                        value = (T)cached.Value;
                        return value != null;
                    }
                    MachineStableMemory.Remove(key);
                }
            }

            // A hot memory-cache hit needs no Stopwatch allocation or file timing.
            // Start measuring only on the cold disk fallback path.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string path = PathFor(key);
            long pathMs = sw.ElapsedMilliseconds;
            if (!File.Exists(path)) return false;
            long existsMs = sw.ElapsedMilliseconds;

            string json = File.ReadAllText(path);
            long readMs = sw.ElapsedMilliseconds;

            if (typeof(T) == typeof(SystemInfoService.HardwareIdentity))
            {
                var typed = (CacheEnvelope<SystemInfoService.HardwareIdentity>?)JsonSerializer.Deserialize(
                    json,
                    typeof(CacheEnvelope<SystemInfoService.HardwareIdentity>),
                    HardwareCacheJsonContext.Default);
                long jsonMs = sw.ElapsedMilliseconds;
                if (typed == null) return false;
                age = DateTime.UtcNow - typed.SavedUtc;
                if (!IsValidCacheAge(age, maxAge)) return false;

                string fingerprint = MachineStableFingerprint.Value;
                long fingerprintMs = sw.ElapsedMilliseconds;
                if (!string.Equals(typed.Fingerprint, fingerprint, StringComparison.Ordinal)) return false;

                value = (T)(object)typed.Value;
                lock (Gate)
                    MachineStableMemory[key] = new MachineStableMemoryEntry(typed.SavedUtc, typed.Fingerprint, typed.Value, typeof(T));
                if (key == "overview-hardware-identity-v1")
                    LogService.Info("performance", $"HARDWARE_CACHE_READ_PHASE pathMs={pathMs} existsMs={existsMs - pathMs} readMs={readMs - existsMs} jsonMs={jsonMs - readMs} fingerprintMs={fingerprintMs - jsonMs} totalMs={sw.ElapsedMilliseconds}");
                return value != null;
            }

            if (typeof(T) == typeof(string))
            {
                using var document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("savedUtc", out var savedUtcElement) ||
                    !root.TryGetProperty("fingerprint", out var fingerprintElement) ||
                    !root.TryGetProperty("value", out var valueElement) ||
                    !savedUtcElement.TryGetDateTime(out DateTime savedUtc))
                    return false;

                string? cachedFingerprint = fingerprintElement.GetString();
                string? cachedValue = valueElement.GetString();
                long jsonMs = sw.ElapsedMilliseconds;
                if (cachedFingerprint == null || cachedValue == null) return false;
                age = DateTime.UtcNow - savedUtc;
                if (!IsValidCacheAge(age, maxAge)) return false;

                string fingerprint = MachineStableFingerprint.Value;
                long fingerprintMs = sw.ElapsedMilliseconds;
                if (!string.Equals(cachedFingerprint, fingerprint, StringComparison.Ordinal)) return false;

                value = (T)(object)cachedValue;
                lock (Gate)
                    MachineStableMemory[key] = new MachineStableMemoryEntry(savedUtc, cachedFingerprint, cachedValue, typeof(T));
                if (key == "bcd-current-v1")
                    LogService.Info("performance",
                        $"BCD_CACHE_READ_PHASE pathMs={pathMs} existsMs={existsMs - pathMs} readMs={readMs - existsMs} jsonMs={jsonMs - readMs} fingerprintMs={fingerprintMs - jsonMs} totalMs={sw.ElapsedMilliseconds}");
                return value != null;
            }

            var envelope = JsonSerializer.Deserialize<CacheEnvelope<T>>(json, JsonOptions);
            if (envelope == null) return false;
            age = DateTime.UtcNow - envelope.SavedUtc;
            if (!IsValidCacheAge(age, maxAge)) return false;

            string genericFingerprint = MachineStableFingerprint.Value;
            if (!string.Equals(envelope.Fingerprint, genericFingerprint, StringComparison.Ordinal)) return false;
            value = envelope.Value;
            if (value != null)
            {
                lock (Gate)
                    MachineStableMemory[key] = new MachineStableMemoryEntry(envelope.SavedUtc, envelope.Fingerprint, value, typeof(T));
            }
            return value != null;
        }
        catch { return false; }
    }

    internal static void WriteMachineStable<T>(string key, T value)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(CacheDir);
                string path = PathFor(key);
                RemoveStaleTempSiblings(path);
                string temp = NewTempPath(path);
                DateTime savedUtc = DateTime.UtcNow;
                string fingerprint = MachineStableFingerprint.Value;
                string json = typeof(T) == typeof(string)
                    ? JsonSerializer.Serialize(
                        new CacheEnvelope<string>(savedUtc, fingerprint, (string)(object)value!),
                        typeof(CacheEnvelope<string>),
                        HardwareCacheJsonContext.Default)
                    : JsonSerializer.Serialize(new CacheEnvelope<T>(savedUtc, fingerprint, value), JsonOptions);
                try
                {
                    File.WriteAllText(temp, json);
                    File.Move(temp, path, true);
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
                MachineStableMemory[key] = new MachineStableMemoryEntry(savedUtc, fingerprint, value!, typeof(T));
            }
        }
        catch (Exception ex) { LogService.Error("hardware-cache", "Machine-stable cache write failed: " + key, ex); }
    }

    public static void Remove(string key)
    {
        try
        {
            lock (Gate)
            {
                MachineStableMemory.Remove(key);
                string path = PathFor(key);
                if (File.Exists(path)) File.Delete(path);
                // A crash between temp write and atomic replace can leave a
                // stale sidecar; never accumulate orphaned cache files.
                string legacyTemp = path + ".tmp";
                if (File.Exists(legacyTemp)) File.Delete(legacyTemp);
                try
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        foreach (string sidecar in Directory.EnumerateFiles(
                            dir, Path.GetFileName(path) + ".tmp.*"))
                                File.Delete(sidecar);
                }
                catch { }
            }
        }
        catch (Exception ex) { LogService.Error("hardware-cache", "Persistent cache remove failed: " + key, ex); }
    }

    internal static void RunMachineStableMemoryCacheSmoke()
    {
        const string key = "__machine-stable-memory-smoke";
        Remove(key);
        try
        {
            WriteMachineStable(key, "v1");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!TryReadMachineStable<string>(key, TimeSpan.FromMinutes(1), out var first) || first != "v1")
                throw new InvalidOperationException("Machine-stable memory cache did not return the written value.");
            long memoryMs = sw.ElapsedMilliseconds;

            if (!BackdateForSmoke(key, TimeSpan.FromMinutes(2)))
                throw new InvalidOperationException("Machine-stable memory cache smoke could not backdate the cache.");
            if (TryReadMachineStable<string>(key, TimeSpan.FromMinutes(1), out _))
                throw new InvalidOperationException("Backdated machine-stable cache incorrectly survived through memory.");

            WriteMachineStable(key, "v2");
            Remove(key);
            if (TryReadMachineStable<string>(key, TimeSpan.FromMinutes(1), out _))
                throw new InvalidOperationException("Removed machine-stable cache incorrectly survived through memory.");

            // A crashed atomic replace can leave a unique sidecar; the next
            // write reclaims only clearly stale ones and never a live writer's.
            string path = PathFor(key);
            string staleSidecar = path + ".tmp." + new string('c', 32);
            string liveSidecar = path + ".tmp." + new string('d', 32);
            File.WriteAllText(staleSidecar, "crashed-writer");
            File.SetLastWriteTimeUtc(staleSidecar, DateTime.UtcNow.AddMinutes(-30));
            File.WriteAllText(liveSidecar, "live-writer");
            WriteMachineStable(key, "v3");
            if (File.Exists(staleSidecar) || !File.Exists(liveSidecar))
                throw new InvalidOperationException(
                    "Hardware cache kept a crashed sidecar or removed a live writer's sidecar.");

            // Remove must clear both the legacy fixed .tmp and every unique sidecar.
            string legacySibling = path + ".tmp";
            File.WriteAllText(legacySibling, "legacy-temp");
            Remove(key);
            if (File.Exists(legacySibling) || File.Exists(liveSidecar) || File.Exists(path))
                throw new InvalidOperationException("Removed machine-stable cache left an orphaned temp sibling.");

            LogService.Info("performance",
                $"MACHINE_STABLE_MEMORY_CACHE_SMOKE_OK memoryMs={memoryMs} " +
                "orphanTempCleaned=true liveSidecarPreserved=true uniqueSidecarWrites=true");
        }
        finally
        {
            Remove(key);
        }
    }

    internal static bool BackdateForSmoke(string key, TimeSpan age)
    {
        try
        {
            lock (Gate)
            {
                string path = PathFor(key);
                if (!File.Exists(path)) return false;
                JsonObject? root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (root == null) return false;
                root["savedUtc"] = DateTime.UtcNow.Subtract(age);
                File.WriteAllText(path, root.ToJsonString(JsonOptions));
                MachineStableMemory.Remove(key);
                return true;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("hardware-cache", "Backdate smoke helper failed: " + key, ex);
            return false;
        }
    }

    private static string BuildFingerprintCore()
    {
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            string machineGuid = machine?.GetValue("MachineGuid")?.ToString() ?? "?";
            string nics = string.Join(";", NetworkInterface.GetAllNetworkInterfaces().Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
            string drives = string.Join(";", DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed).Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return $"{machineGuid}|{Environment.OSVersion.Version.Build}|{nics}|{drives}";
        }
        catch { return Environment.OSVersion.Version.Build.ToString(); }
    }

    private static string BuildMachineStableFingerprintCore()
    {
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            string machineGuid = machine?.GetValue("MachineGuid")?.ToString() ?? "?";
            return $"{machineGuid}|{Environment.OSVersion.Version.Build}";
        }
        catch { return Environment.OSVersion.Version.Build.ToString(); }
    }

    // Proves the machine-stable envelope contract (value, fingerprint binding,
    // age validation, cleanup) on a disposable temp root. It NEVER removes or
    // rewrites the real ProgramData cache that the running app shares.
    internal static void RunIsolatedPersistenceSmoke()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "SpankyHardwareCacheSmoke-" + Guid.NewGuid().ToString("N"));
        const string key = "__isolated-machine-stable-smoke";
        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, key + ".json");
            var savedUtc = DateTime.UtcNow;
            string json = JsonSerializer.Serialize(
                new CacheEnvelope<string>(savedUtc, MachineStableFingerprint.Value, "v1"), JsonOptions);
            File.WriteAllText(path, json);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var envelope = JsonSerializer.Deserialize<CacheEnvelope<string>>(
                File.ReadAllText(path), JsonOptions);
            long readMs = sw.ElapsedMilliseconds;
            if (envelope == null ||
                envelope.Value != "v1" ||
                !string.Equals(envelope.Fingerprint, MachineStableFingerprint.Value, StringComparison.Ordinal) ||
                !IsValidCacheAge(DateTime.UtcNow - envelope.SavedUtc, TimeSpan.FromMinutes(1)) ||
                IsValidCacheAge(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)) ||
                readMs > 150)
                throw new InvalidOperationException(
                    "Isolated machine-stable cache round-trip/fingerprint/age contract failed.");

            File.Delete(path);
            if (File.Exists(path) || Directory.GetFiles(root).Length != 0)
                throw new InvalidOperationException("Isolated machine-stable cache cleanup failed.");
            LogService.Info("performance",
                "PERSISTENT_HARDWARE_CACHE_ISOLATED_SMOKE_OK writeReadback=true " +
                "fingerprintBound=true ageBound=true cleanup=true actualUserCacheTouched=false " +
                "readMs=" + readMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string PathFor(string key)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
        return Path.Combine(CacheDir, key + ".json");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PersistentHardwareCacheService.CacheEnvelope<SystemInfoService.HardwareIdentity>))]
[JsonSerializable(typeof(PersistentHardwareCacheService.CacheEnvelope<string>))]
internal partial class HardwareCacheJsonContext : JsonSerializerContext
{
}
