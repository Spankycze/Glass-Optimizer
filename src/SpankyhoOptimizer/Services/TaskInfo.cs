namespace SpankyhoOptimizer.Services;

public sealed record TaskInfo(string Path, string Name, bool Enabled, bool StartupTrigger = false, string? Executable = null)
{
    public TaskInfo(string path, string name, bool enabled, bool startupTrigger)
        : this(path, name, enabled, startupTrigger, null) { }

    public void Deconstruct(out string path, out string name, out bool enabled, out bool startupTrigger)
    {
        path = Path;
        name = Name;
        enabled = Enabled;
        startupTrigger = StartupTrigger;
    }
}
