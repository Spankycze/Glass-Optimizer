using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, RegistryBackupValue>))]
internal partial class RegistryBackupJsonContext : JsonSerializerContext
{
}

public sealed partial class RegistryBackupStore
{
    private sealed class SerializableRegistryValue
    {
        public string Kind { get; set; } = "None";
        public object? Value { get; set; }
        public SerializableRegistryValue() { }
        public SerializableRegistryValue(RegistryValueKind kind, object? value)
        {
            Kind = kind.ToString();
            Value = value;
        }
    }

    private readonly string _file;
    public RegistryBackupStore() : this(null) { }
    public RegistryBackupStore(string? file = null) => _file = file ?? Path.Combine(LogService.Root, "registry-backups.json");

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    private Dictionary<string, RegistryBackupValue>? _cache;

    private async Task<Dictionary<string, RegistryBackupValue>> LoadAsync()
    {
        if (_cache != null) return _cache;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        if (!File.Exists(_file)) return _cache = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            string json = await File.ReadAllTextAsync(_file).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize(json, typeof(Dictionary<string, RegistryBackupValue>), RegistryBackupJsonContext.Default)
                as Dictionary<string, RegistryBackupValue>
                ?? throw new InvalidDataException("Soubor původních hodnot obsahuje prázdnou zálohu.");
            return _cache = new(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogService.Error("rollback", "Original registry backup cannot be read: " + _file, ex);
            throw new InvalidDataException("Původní registry zálohu nelze načíst; soubor nebyl nahrazen.", ex);
        }
    }
    public async Task WarmAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { _ = await LoadAsync(); }
        finally { _gate.Release(); }
    }

    public async Task SaveOriginalBatchAsync(IEnumerable<TweakDefinition> tweaks, CancellationToken token = default)
    {
        var items = tweaks.Where(x => x.RegistryHive.HasValue && x.RegistryPath != null && x.RegistryValueName != null).ToArray();
        if (items.Length == 0) return;
        await _gate.WaitAsync(token);
        try
        {
            var perf = System.Diagnostics.Stopwatch.StartNew();
            Dictionary<string, RegistryBackupValue> dictionary = await LoadAsync();
            long loadMs = perf.ElapsedMilliseconds;
            ValidateBackupBatchIdentities(items, dictionary);
            var missing = items.Where(x => !dictionary.ContainsKey(x.Id)).ToArray();
            if (missing.Length == 0)
            {
                LogService.Info("performance", $"REG_BACKUP_HIT items={items.Length} loadMs={loadMs} totalMs={perf.ElapsedMilliseconds}");
                return;
            }

            var pending = new Dictionary<string, RegistryBackupValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in missing.GroupBy(x => (Hive: x.RegistryHive!.Value, Path: x.RegistryPath!)))
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(group.Key.Hive, RegistryView.Registry64);
                using RegistryKey? key = baseKey.OpenSubKey(group.Key.Path, writable: false);
                var names = (key?.GetValueNames() ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var tweak in group)
                {
                    token.ThrowIfCancellationRequested();
                    if (dictionary.ContainsKey(tweak.Id) || pending.ContainsKey(tweak.Id)) continue;
                    string valueName = tweak.RegistryValueName!;
                    bool existed = names.Contains(valueName);
                    string kind = "None";
                    string? valueBase = null;
                    if (existed && key != null)
                    {
                        object? value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        RegistryValueKind valueKind = key.GetValueKind(valueName);
                        kind = valueKind.ToString();
                        valueBase = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new SerializableRegistryValue(valueKind, Normalize(value))));
                    }
                    pending[tweak.Id] = new RegistryBackupValue(tweak.Id, group.Key.Hive.ToString(), group.Key.Path, valueName, existed, kind, valueBase, key is not null);
                }
            }
            long captureMs = perf.ElapsedMilliseconds - loadMs;
            if (pending.Count > 0)
            {
                var persisted = new Dictionary<string, RegistryBackupValue>(dictionary, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in pending) persisted[entry.Key] = entry.Value;
                long mergeMs = perf.ElapsedMilliseconds - loadMs - captureMs;
                string json = await SerializeBackupsAsync(persisted, token).ConfigureAwait(false);
                long serializeMs = perf.ElapsedMilliseconds - loadMs - captureMs - mergeMs;
                await AtomicFileService.WriteTextAsync(_file, json, token);
                long writeMs = perf.ElapsedMilliseconds - loadMs - captureMs - mergeMs - serializeMs;
                foreach (var entry in pending) dictionary[entry.Key] = entry.Value;
                LogService.Info("performance", $"REG_BACKUP_PHASE items={items.Length} pending={pending.Count} loadMs={loadMs} captureMs={captureMs} mergeMs={mergeMs} serializeMs={serializeMs} writeMs={writeMs} totalMs={perf.ElapsedMilliseconds}");
            }
        }
        finally { _gate.Release(); }
    }
    public Task SaveOriginalAsync(string id, RegistryHive hive, string path, string name) =>
        SaveOriginalAsync(id, hive, path, name, CancellationToken.None);

    public async Task SaveOriginalAsync(string id, RegistryHive hive, string path, string name, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Dictionary<string, RegistryBackupValue> dictionary = await LoadAsync();
            if (dictionary.TryGetValue(id, out RegistryBackupValue? existing))
            {
                // A previously used ID must never silently adopt a different
                // HKCU/HKLM hive, registry key or value name.
                ValidateBackupTargetIdentity(id, hive, path, name, existing);
                return;
            }
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey key = baseKey.OpenSubKey(path, writable: false);
            bool flag = (key?.GetValueNames() ?? Array.Empty<string>()).Contains<string>(name, StringComparer.OrdinalIgnoreCase);
            string kind = "None";
            string valueBase = null;
            if (flag && key != null)
            {
                object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                RegistryValueKind valueKind = key.GetValueKind(name);
                kind = valueKind.ToString();
                valueBase = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new SerializableRegistryValue(valueKind, Normalize(value))));
            }
            var pending = new RegistryBackupValue(id, hive.ToString(), path, name, flag, kind, valueBase, key is not null);
            var persisted = new Dictionary<string, RegistryBackupValue>(dictionary, StringComparer.OrdinalIgnoreCase)
            {
                [id] = pending
            };
            await AtomicFileService.WriteTextAsync(
                _file,
                await SerializeBackupsAsync(persisted, token).ConfigureAwait(false),
                token).ConfigureAwait(false);
            dictionary[id] = pending;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> SerializeBackupsAsync(
        Dictionary<string, RegistryBackupValue> value,
        CancellationToken token = default)
    {
        await using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(
            stream,
            value,
            typeof(Dictionary<string, RegistryBackupValue>),
            RegistryBackupJsonContext.Default,
            token).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public Task<bool> RestoreAsync(string id) => RestoreAsync(id, CancellationToken.None);

    // Recovery must compare the COMPLETE original value and registry key
    // existence, not merely DetectAsync's ON/OFF Boolean projection.
    // This read-only API never creates or mutates a Windows registry key.
    public async Task<bool> IsOriginalStateRestoredAsync(
        string id, RegistryHive expectedHive, string expectedPath,
        string expectedValueName, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!(await LoadAsync().ConfigureAwait(false)).TryGetValue(id, out var saved))
                return false;
            ValidateBackupTargetIdentity(id, expectedHive, expectedPath,
                expectedValueName, saved);
            token.ThrowIfCancellationRequested();
            var (kind, original) = DecodeOriginalSnapshot(saved);
            using RegistryKey root = RegistryKey.OpenBaseKey(expectedHive, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(expectedPath, writable: false);
            bool present = key is not null && key.GetValueNames().Contains(
                expectedValueName, StringComparer.OrdinalIgnoreCase);
            RegistryValueKind actualKind = present
                ? key!.GetValueKind(expectedValueName) : RegistryValueKind.None;
            object? current = present
                ? key!.GetValue(expectedValueName, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames)
                : null;
            return MatchesCompleteOriginalSnapshot(
                saved, key is not null, present, actualKind, current,
                kind, original);
        }
        finally { _gate.Release(); }
    }

    // Isolated tests exercise this EXACT production predicate without
    // touching the user's live registry or backup files.
    internal static bool MatchesCompleteOriginalSnapshot(
        RegistryBackupValue saved, bool currentKeyExists,
        bool currentValueExists, RegistryValueKind currentKind, object? currentValue,
        RegistryValueKind originalKind, object? originalValue)
    {
        // Unknown key history is insufficient to certify an interrupted
        // transaction recovered (legacy backups remain usable manually).
        if (saved.KeyExisted != currentKeyExists ||
            !saved.KeyExisted.HasValue) return false;
        return saved.Existed
            ? IsExactOriginalValue(currentValueExists, currentKind, currentValue,
                originalKind, originalValue)
            : !currentValueExists && originalKind == RegistryValueKind.None;
    }

    private static (RegistryValueKind Kind, object? Original) DecodeOriginalSnapshot(
        RegistryBackupValue value)
    {
        if (!value.Existed)
        {
            if (value.Kind != nameof(RegistryValueKind.None) ||
                !string.IsNullOrEmpty(value.ValueBase64))
                throw new InvalidDataException(
                    "Záloha původně neexistující registry hodnoty je neplatná.");
            return (RegistryValueKind.None, null);
        }
        if (value.KeyExisted == false || string.IsNullOrWhiteSpace(value.ValueBase64))
            throw new InvalidDataException(
                "Záloha původní registry hodnoty je neúplná.");
        var snapshot = JsonSerializer.Deserialize<SerializableRegistryValue>(
            Convert.FromBase64String(value.ValueBase64))
            ?? throw new InvalidDataException("Registry záloha obsahuje neplatná data.");
        if (!Enum.TryParse(snapshot.Kind, out RegistryValueKind kind) ||
            !Enum.IsDefined(kind) ||
            kind is RegistryValueKind.None or RegistryValueKind.Unknown ||
            snapshot.Kind != value.Kind)
            throw new InvalidDataException("Záloha má neplatný registry datový typ.");
        return (kind, Denormalize(kind, snapshot.Value));
    }

    public async Task<bool> RestoreAsync(string id, CancellationToken token = default,
        int? expectedAppliedDword = null, RegistryHive? expectedHive = null,
        string? expectedPath = null, string? expectedValueName = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!(await LoadAsync().ConfigureAwait(false)).TryGetValue(id, out var value))
                return false;
            // Never restore an old backup by ID alone when the current DWORD
            // provider supplies its exact target. Reject mismatched metadata
            // before opening a writable registry key.
            if (expectedHive.HasValue || expectedPath is not null ||
                expectedValueName is not null)
            {
                if (!expectedHive.HasValue || string.IsNullOrWhiteSpace(expectedPath) ||
                    string.IsNullOrWhiteSpace(expectedValueName))
                    throw new InvalidOperationException(
                        "Cílová registry identita pro obnovu není úplná.");
                ValidateBackupTargetIdentity(id, expectedHive.Value,
                    expectedPath, expectedValueName, value);
            }
            token.ThrowIfCancellationRequested();

            // Use the same strict decoder as read-only crash recovery;
            // corrupted original-value metadata must fail before writes.
            var (kind, original) = DecodeOriginalSnapshot(value);

            using RegistryKey root = RegistryKey.OpenBaseKey(
                Enum.Parse<RegistryHive>(value.Hive), RegistryView.Registry64);
            if (!value.Existed)
                return RestoreOriginallyMissingValue(root, value, expectedAppliedDword);

            // Never fabricate an originally existing key that was removed
            // externally; the backup stores a value, not a whole subtree.
            using RegistryKey? key = root.OpenSubKey(value.Path, writable: true);
            if (key is null)
                throw new InvalidOperationException(
                    "Původní registry klíč byl odstraněn; nebudu jej vytvářet znovu.");

            // A previously existing original value can have been edited by
            // another program since our tweak was activated. For DWORD
            // tweaks, only our verified applied value may be replaced.
            bool currentExists = key.GetValueNames().Contains(
                value.Name, StringComparer.OrdinalIgnoreCase);
            RegistryValueKind currentKind = currentExists
                ? key.GetValueKind(value.Name) : RegistryValueKind.None;
            object? currentValue = currentExists
                ? key.GetValue(value.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                : null;
            bool originalAlreadyRestored = IsExactOriginalValue(
                currentExists, currentKind, currentValue, kind, original);
            bool? appliedValueMatches = expectedAppliedDword is null
                ? null
                : currentExists && MatchesExpectedAppliedDword(
                    currentKind, currentValue, expectedAppliedDword) == true;
            switch (PlanExistingValueRestore(originalAlreadyRestored, appliedValueMatches))
            {
                case ExistingValueRestoreDecision.AlreadyOriginal:
                    return true;
                case ExistingValueRestoreDecision.RefuseExternalChange:
                    throw new InvalidOperationException(
                        "Registry hodnota byla změněna mimo Optimizer; " +
                        "původní nastavení nebude přepsáno.");
                case ExistingValueRestoreDecision.RestoreOriginal:
                    break;
                default:
                    throw new InvalidOperationException("Neznámá strategie obnovy registru.");
            }

            // Minimize time between ownership check and restoration. As with
            // all registry writes, this cannot eliminate a concurrent external
            // writer completely without cooperation from that writer.
            if (expectedAppliedDword is not null &&
                CheckCurrentAppliedDword(key, value.Name, expectedAppliedDword) != true)
                throw new InvalidOperationException(
                    "Registry hodnota se změnila během obnovy; cizí zápis zůstal zachován.");

            key.SetValue(value.Name, original!, kind);
            bool exists = key.GetValueNames().Contains(
                value.Name, StringComparer.OrdinalIgnoreCase);
            object? actual = exists
                ? key.GetValue(value.Name, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames)
                : null;
            if (!exists || key.GetValueKind(value.Name) != kind ||
                !RegistryValuesEqual(actual, original))
                throw new IOException(
                    "Windows nepotvrdil obnovení původní registry hodnoty a typu.");
            return true;
        }
        finally { _gate.Release(); }
    }

    private static bool RegistryValuesEqual(object? actual, object? expected) => (actual, expected) switch
    {
        (byte[] a, byte[] b) => a.SequenceEqual(b),
        (string[] a, string[] b) => a.SequenceEqual(b, StringComparer.Ordinal),
        _ => Equals(actual, expected)
    };
    public async Task RestoreAllAsync()
    {
        // Take a stable snapshot of IDs under the original backup lock,
        // then release it BEFORE per-ID RestoreAsync acquires the same lock.
        string[] ids;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ids = (await LoadAsync().ConfigureAwait(false)).Keys.ToArray();
        }
        finally { _gate.Release(); }

        // Never treat partial success as a successful "Restore registry".
        // Independent entries are attempted and failed IDs are aggregated.
        await ExecuteRestoreAllTransactionAsync(
            ids, (id, token) => RestoreAsync(id, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static object? Normalize(object? value)
    {
        if (!(value is byte[] inArray))
        {
            if (value is string[] result)
            {
                return result;
            }
            return value?.ToString();
        }
        return Convert.ToBase64String(inArray);
    }

    private static object Denormalize(RegistryValueKind kind, object? value)
    {
        string text = value?.ToString() ?? string.Empty;
        return kind switch
        {
            RegistryValueKind.DWord => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            RegistryValueKind.Binary => Convert.FromBase64String(text),
            RegistryValueKind.MultiString => (value is JsonElement { ValueKind: JsonValueKind.Array } jsonElement) ? ((object)(from x in jsonElement.EnumerateArray()
                                                                                                                               select x.GetString() ?? string.Empty).ToArray()) : ((object)Array.Empty<string>()),
            _ => text,
        };
    }
}
