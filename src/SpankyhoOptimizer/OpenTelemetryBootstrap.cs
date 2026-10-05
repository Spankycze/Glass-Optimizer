#if SPANKY_OTEL
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

internal static class OpenTelemetryBootstrap
{
    private static TracerProvider? _tracerProvider;
    private static MeterProvider? _meterProvider;

    internal static void Initialize()
    {
        if (!DiagnosticsTelemetry.Enabled) return;

        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(DiagnosticsTelemetry.SourceName)
            .AddConsoleExporter()
            .Build();

        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(DiagnosticsTelemetry.SourceName)
            .AddConsoleExporter()
            .Build();
    }
}
#else
namespace SpankyhoOptimizer;

internal static class OpenTelemetryBootstrap
{
    internal static void Initialize() { }
}
#endif
