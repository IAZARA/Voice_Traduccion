using System.Text.Json;
using VoiceTraductor.Core;

namespace VoiceTraductor.Infrastructure.Realtime;

public static class RealtimeProtocol
{
    public static string CreateSessionUpdate(TranslationStreamOptions options)
    {
        object? noiseReduction = options.NoiseReduction is null
            ? null
            : new { type = options.NoiseReduction };

        return JsonSerializer.Serialize(
            new
            {
                type = "session.update",
                session = new
                {
                    audio = new
                    {
                        input = new
                        {
                            transcription = new { model = options.TranscriptionModel },
                            noise_reduction = noiseReduction
                        },
                        output = new { language = options.TargetLanguage }
                    }
                }
            });
    }

    public static string CreateAudioAppend(ReadOnlySpan<byte> pcm16) =>
        JsonSerializer.Serialize(
            new
            {
                type = "session.input_audio_buffer.append",
                audio = Convert.ToBase64String(pcm16)
            });

    public static string CreateSessionClose() => """{"type":"session.close"}""";

    public static RealtimeServerEvent ParseServerEvent(
        string json,
        TranslationDirection direction)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? string.Empty
            : string.Empty;
        var elapsed = root.TryGetProperty("elapsed_ms", out var elapsedElement) &&
                      elapsedElement.TryGetInt64(out var parsedElapsed)
            ? parsedElapsed
            : (long?)null;

        return type switch
        {
            "session.created" => new RealtimeServerEvent(type),
            "session.updated" => new RealtimeServerEvent(type),
            "session.closed" => new RealtimeServerEvent(type),
            "session.input_transcript.delta" => new RealtimeServerEvent(
                type,
                Transcript: new TranscriptDelta(
                    direction,
                    TranscriptKind.Source,
                    root.GetProperty("delta").GetString() ?? string.Empty,
                    elapsed,
                    DateTimeOffset.UtcNow)),
            "session.output_transcript.delta" => new RealtimeServerEvent(
                type,
                Transcript: new TranscriptDelta(
                    direction,
                    TranscriptKind.Translation,
                    root.GetProperty("delta").GetString() ?? string.Empty,
                    elapsed,
                    DateTimeOffset.UtcNow)),
            "session.output_audio.delta" => new RealtimeServerEvent(
                type,
                Audio: new AudioDelta(
                    direction,
                    Convert.FromBase64String(root.GetProperty("delta").GetString() ?? string.Empty),
                    TryGetInt(root, "sample_rate", 24_000),
                    TryGetInt(root, "channels", 1),
                    elapsed)),
            "error" => ParseError(root, direction),
            _ => new RealtimeServerEvent(type)
        };
    }

    private static RealtimeServerEvent ParseError(
        JsonElement root,
        TranslationDirection direction)
    {
        var error = root.GetProperty("error");
        var code = error.TryGetProperty("code", out var codeElement)
            ? codeElement.GetString()
            : null;
        var errorType = error.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;
        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString() ?? "La API devolvió un error."
            : "La API devolvió un error.";
        var kind = ClassifyError(code, errorType, message);
        var recoverable = kind is TranslationErrorKind.Network or TranslationErrorKind.Service;

        return new RealtimeServerEvent(
            "error",
            Fault: new TranslationFault(direction, kind, message, recoverable));
    }

    public static TranslationErrorKind ClassifyError(
        string? code,
        string? type,
        string? message)
    {
        var value = $"{code} {type} {message}".ToLowerInvariant();
        if (value.Contains("api key", StringComparison.Ordinal) ||
            value.Contains("api_key", StringComparison.Ordinal) ||
            value.Contains("auth", StringComparison.Ordinal) ||
            value.Contains("unauthorized", StringComparison.Ordinal) ||
            value.Contains("401", StringComparison.Ordinal) ||
            value.Contains("403", StringComparison.Ordinal))
        {
            return TranslationErrorKind.Authentication;
        }

        if (value.Contains("rate", StringComparison.Ordinal) ||
            value.Contains("quota", StringComparison.Ordinal) ||
            value.Contains("limit", StringComparison.Ordinal) ||
            value.Contains("429", StringComparison.Ordinal))
        {
            return TranslationErrorKind.RateLimit;
        }

        if (value.Contains("invalid", StringComparison.Ordinal) ||
            value.Contains("validation", StringComparison.Ordinal))
        {
            return TranslationErrorKind.Protocol;
        }

        return TranslationErrorKind.Service;
    }

    private static int TryGetInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value)
            ? value
            : fallback;
}

public sealed record RealtimeServerEvent(
    string Type,
    AudioDelta? Audio = null,
    TranscriptDelta? Transcript = null,
    TranslationFault? Fault = null);
