namespace SpankyhoOptimizer.Models;

public sealed record DeviceInfo(
    string InstanceId,
    string Class,
    string FriendlyName,
    bool Present,
    string Manufacturer = "Unknown",
    int ProblemCode = 0);
