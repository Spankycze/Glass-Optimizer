using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SpankyhoOptimizer.Services;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(OperationJournalEntry))]
[JsonSerializable(typeof(List<TweakBatchRecord>))]
[JsonSerializable(typeof(AppUpdateManifest))]
[JsonSerializable(typeof(SectionFailureState))]
[JsonSerializable(typeof(StartupState))]
internal partial class OptimizerJsonContext : JsonSerializerContext
{
}
