using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public static class OpenAiRealtimeTranscriptionProtocol
{
    public static string CreateSessionUpdate(string prompt, IReadOnlyCollection<string> keywords)
    {
        var transcription = new Dictionary<string, object?>
        {
            ["model"] = "gpt-live-transcribe",
            ["prompt"] = prompt,
            ["keywords"] = keywords,
            ["languages"] = new[] { "cs" },
            ["delay"] = "low"
        };
        var input = new Dictionary<string, object?>
        {
            ["format"] = new Dictionary<string, object?> { ["type"] = "audio/pcm", ["rate"] = 24000 },
            ["transcription"] = transcription,
            ["turn_detection"] = null
        };
        var session = new Dictionary<string, object?>
        {
            ["type"] = "transcription",
            ["audio"] = new Dictionary<string, object?> { ["input"] = input }
        };
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = "session.update", ["session"] = session });
    }

    public static string CreateAudioAppend(ReadOnlySpan<byte> pcm16) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = Convert.ToBase64String(pcm16)
        });

    public static string CreateCommit() => "{\"type\":\"input_audio_buffer.commit\"}";

    public static string? TryParseTranscript(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("type", out JsonElement typeElement))
            return null;

        string? type = typeElement.GetString();
        if (string.Equals(type, "conversation.item.input_audio_transcription.delta", StringComparison.Ordinal))
            return root.TryGetProperty("delta", out JsonElement delta) ? delta.GetString() : null;

        if (string.Equals(type, "conversation.item.input_audio_transcription.completed", StringComparison.Ordinal))
            return root.TryGetProperty("transcript", out JsonElement transcript) ? transcript.GetString() : null;

        return null;
    }

    public static string? GetEventType(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("type", out JsonElement type)
            ? type.GetString()
            : null;
    }

    public static string? TryParseErrorMessage(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("error", out JsonElement error))
            return null;
        return error.TryGetProperty("message", out JsonElement message)
            ? message.GetString()
            : null;
    }

    public static bool IsCompletedTranscriptEvent(string json) =>
        string.Equals(
            GetEventType(json),
            "conversation.item.input_audio_transcription.completed",
            StringComparison.Ordinal);
}
