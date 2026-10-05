using System.Text.Encodings.Web;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public readonly record struct NeuralTtsWorkerResponse(
    int Id,
    bool Ok,
    long GenerationMs,
    string? Error);

public static class NeuralTtsWorkerProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string CreateRequest(int id, string text, string outputPath) =>
        JsonSerializer.Serialize(new
        {
            id,
            text,
            output = outputPath
        }, JsonOptions);

    public static NeuralTtsWorkerResponse ParseResponse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        return new NeuralTtsWorkerResponse(
            root.TryGetProperty("id", out JsonElement id) ? id.GetInt32() : 0,
            root.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean(),
            root.TryGetProperty("generation_ms", out JsonElement ms) ? ms.GetInt64() : 0,
            root.TryGetProperty("error", out JsonElement error) ? error.GetString() : null);
    }
}
