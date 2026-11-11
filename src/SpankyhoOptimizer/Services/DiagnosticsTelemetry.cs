using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SpankyhoOptimizer.Services;

public static class DiagnosticsTelemetry
{
    public const string SourceName = "SpankyhoOptimizer";
    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName, "1.0.0");
    private static readonly Histogram<double> DurationMs = Meter.CreateHistogram<double>("spanky.operation.duration", "ms");
    private static readonly Counter<long> OperationCount = Meter.CreateCounter<long>("spanky.operation.count");
    private static readonly Counter<long> CacheHitCount = Meter.CreateCounter<long>("spanky.cache.hit");
    private static readonly Counter<long> CacheMissCount = Meter.CreateCounter<long>("spanky.cache.miss");

    public static bool Enabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable("SPANKY_OTEL"), "1", StringComparison.Ordinal);

    // The hot call sites use two fixed tags. The params-array overload used to
    // allocate on every run even with tracing disabled.
    public static Activity? Start(string operation,
        (string Key, object? Value) first, (string Key, object? Value) second)
    {
        if (!Enabled) return null;
        Activity? activity = ActivitySource.StartActivity(operation, ActivityKind.Internal);
        if (activity is null) return null;
        activity.SetTag(first.Key, first.Value);
        activity.SetTag(second.Key, second.Value);
        return activity;
    }

    public static Activity? Start(string operation, params (string Key, object? Value)[] tags)
    {
        if (!Enabled) return null;
        Activity? activity = ActivitySource.StartActivity(operation, ActivityKind.Internal);
        if (activity == null) return null;
        foreach (var tag in tags)
            activity.SetTag(tag.Key, tag.Value);
        return activity;
    }

    public static void RecordDuration(string operation, double milliseconds,
        (string Key, object? Value) first)
    {
        if (!Enabled) return;
        var tags = new TagList { { "operation", operation }, { first.Key, first.Value } };
        DurationMs.Record(milliseconds, tags);
        OperationCount.Add(1, tags);
    }

    public static void RecordDuration(string operation, double milliseconds,
        (string Key, object? Value) first, (string Key, object? Value) second,
        (string Key, int Value) third)
    {
        if (!Enabled) return;
        var tags = new TagList
        {
            { "operation", operation }, { first.Key, first.Value },
            { second.Key, second.Value }, { third.Key, third.Value }
        };
        DurationMs.Record(milliseconds, tags);
        OperationCount.Add(1, tags);
    }

    public static void RecordDuration(string operation, double milliseconds, params (string Key, object? Value)[] tags)
    {
        if (!Enabled) return;
        var tagList = new TagList { { "operation", operation } };
        foreach (var tag in tags) tagList.Add(tag.Key, tag.Value);
        DurationMs.Record(milliseconds, tagList);
        OperationCount.Add(1, tagList);
    }

    public static void RecordCache(string cache, bool hit)
    {
        if (!Enabled) return;
        var tags = new TagList { { "cache", cache } };
        if (hit) CacheHitCount.Add(1, tags);
        else CacheMissCount.Add(1, tags);
    }
}
