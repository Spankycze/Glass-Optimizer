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

public sealed class RegistryBackupStore
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

    private readonly string _file = Path.Combine(LogService.Root, "registry-backups.json");

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    private Dictionary<string, RegistryBackupValue>? _cache;

    private async Task<Dictionary<string, RegistryBackupValue>> LoadAsync()
    {
        if (_cache != null)
        {
            return _cache;
        }
        Directory.CreateDirectory(LogService.Root);
        try
        {
            if (File.Exists(_file))
            {
                string json = await File.ReadAllTextAsync(_file);
                RegistryBackupStore registryBackupStore = this;
                Dictionary<string, RegistryBackupValue>? obj = JsonSerializer.Deserialize(json, typeof(Dictionary<string, RegistryBackupValue>), RegistryBackupJsonContext.Default) as Dictionary<string, RegistryBackupValue> ?? new Dictionary<string, RegistryBackupValue>();
                Dictionary<string, RegistryBackupValue> result = obj;
                registryBackupStore._cache = obj;
                return result;
            }
        }
        catch
        {
        }
        return _cache = new Dictionary<string, RegistryBackupValue>();
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
                    pending[tweak.Id] = new RegistryBackupValue(tweak.Id, group.Key.Hive.ToString(), group.Key.Path, valueName, existed, kind, valueBase);
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
    public async Task SaveOriginalAsync(string id, RegistryHive hive, string path, string name)
    {
        await _gate.WaitAsync();
        try
        {
            Dictionary<string, RegistryBackupValue> dictionary = await LoadAsync();
            if (dictionary.ContainsKey(id))
            {
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
            var pending = new RegistryBackupValue(id, hive.ToString(), path, name, flag, kind, valueBase);
            var persisted = new Dictionary<string, RegistryBackupValue>(dictionary, StringComparer.OrdinalIgnoreCase)
            {
                [id] = pending
            };
            await AtomicFileService.WriteTextAsync(
                _file,
                await SerializeBackupsAsync(persisted).ConfigureAwait(false));
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

    public async Task<bool> RestoreAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            if (!(await LoadAsync()).TryGetValue(id, out var value))
            {
                return false;
            }
            using RegistryKey registryKey = RegistryKey.OpenBaseKey(Enum.Parse<RegistryHive>(value.Hive), RegistryView.Registry64);
            using RegistryKey registryKey2 = registryKey.CreateSubKey(value.Path, writable: true);
            if (!value.Existed)
            {
                registryKey2.DeleteValue(value.Name, throwOnMissingValue: false);
            }
            else if (!string.IsNullOrEmpty(value.ValueBase64))
            {
                SerializableRegistryValue serializableRegistryValue = JsonSerializer.Deserialize<SerializableRegistryValue>(Convert.FromBase64String(value.ValueBase64));
                if ((object)serializableRegistryValue != null)
                {
                    RegistryValueKind registryValueKind = Enum.Parse<RegistryValueKind>(serializableRegistryValue.Kind);
                    registryKey2.SetValue(value.Name, Denormalize(registryValueKind, serializableRegistryValue.Value), registryValueKind);
                }
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreAllAsync()
    {
        string[] array = (await LoadAsync()).Keys.ToArray();
        string[] array2 = array;
        foreach (string id in array2)
        {
            await RestoreAsync(id);
        }
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
        int result;
        long result2;
        return kind switch
        {
            RegistryValueKind.DWord => int.TryParse(text, out result) ? result : 0,
            RegistryValueKind.QWord => long.TryParse(text, out result2) ? result2 : 0,
            RegistryValueKind.Binary => Convert.FromBase64String(text),
            RegistryValueKind.MultiString => (value is JsonElement { ValueKind: JsonValueKind.Array } jsonElement) ? ((object)(from x in jsonElement.EnumerateArray()
                                                                                                                               select x.GetString() ?? string.Empty).ToArray()) : ((object)Array.Empty<string>()),
            _ => text,
        };
    }
}
