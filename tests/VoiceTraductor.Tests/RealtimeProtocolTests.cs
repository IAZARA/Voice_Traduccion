using VoiceTraductor.Core;
using VoiceTraductor.Infrastructure.Realtime;

namespace VoiceTraductor.Tests;

public sealed class RealtimeProtocolTests
{
    [Fact]
    public void SessionUpdateConfiguresTranslationAndSourceTranscript()
    {
        var json = RealtimeProtocol.CreateSessionUpdate(
            new TranslationStreamOptions(
                TranslationDirection.OutgoingSpanishToEnglish,
                "not-serialized",
                "es",
                "en",
                "near_field"));

        Assert.Contains("\"language\":\"en\"", json, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"gpt-realtime-whisper\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"near_field\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("not-serialized", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesTranslatedAudioAndTiming()
    {
        var audio = Enumerable.Range(0, 24).Select(value => (byte)value).ToArray();
        var json =
            $$"""
              {
                "type": "session.output_audio.delta",
                "delta": "{{Convert.ToBase64String(audio)}}",
                "sample_rate": 24000,
                "channels": 1,
                "elapsed_ms": 1200
              }
              """;

        var parsed = RealtimeProtocol.ParseServerEvent(
            json,
            TranslationDirection.IncomingEnglishToSpanish);

        Assert.NotNull(parsed.Audio);
        Assert.Equal(audio, parsed.Audio.Pcm16);
        Assert.Equal(1_200, parsed.Audio.ElapsedMilliseconds);
    }

    [Theory]
    [InlineData("invalid_api_key", TranslationErrorKind.Authentication)]
    [InlineData("server returned 401", TranslationErrorKind.Authentication)]
    [InlineData("rate_limit_exceeded", TranslationErrorKind.RateLimit)]
    [InlineData("server returned 429", TranslationErrorKind.RateLimit)]
    [InlineData("invalid_event", TranslationErrorKind.Protocol)]
    public void ClassifiesKnownApiErrors(string code, TranslationErrorKind expected)
    {
        Assert.Equal(expected, RealtimeProtocol.ClassifyError(code, null, null));
    }
}
