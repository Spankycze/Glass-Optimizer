namespace SpankyhoOptimizer.Services;

public sealed record TaskInfo(string Path, string Name, bool Enabled, bool StartupTrigger = false, string? Executable = null);
