using System.Text.Json.Serialization;

namespace SpankyhoOptimizer.Services;

// RuntimeHost persists these models on a timed cadence. Generated metadata
// eliminates reflection-based contract construction and supports trimmed builds.
// Preserve the legacy JSON property names, omission rules and profile indentation.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(FanProfile))]
[JsonSerializable(typeof(FanAgentHealth))]
internal partial class FanAgentCompactJsonContext : JsonSerializerContext { }

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default,
    WriteIndented = true)]
[JsonSerializable(typeof(FanProfile))]
internal partial class FanAgentIndentedJsonContext : JsonSerializerContext { }
