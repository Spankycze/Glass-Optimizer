namespace SpankyhoOptimizer.Services;

public sealed record RegistryBackupValue(string Key, string Hive, string Path, string Name, bool Existed, string Kind, string? ValueBase64);
