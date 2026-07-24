namespace VoiceTraductor.Core;

public enum TranslationDirection
{
    IncomingEnglishToSpanish,
    OutgoingSpanishToEnglish
}

public enum TranslationSessionState
{
    Idle,
    Connecting,
    Ready,
    Reconnecting,
    Stopping,
    Faulted
}

public enum TranscriptKind
{
    Source,
    Translation
}

public enum AudioEndpointDirection
{
    Capture,
    Render
}

public enum MeetingStatus
{
    Active,
    Completed,
    Interrupted,
    Faulted
}

public enum TranslationErrorKind
{
    Authentication,
    RateLimit,
    Network,
    Service,
    Protocol,
    Device,
    Unknown
}

public sealed record AudioEndpoint(
    string Id,
    string Name,
    AudioEndpointDirection Direction,
    bool IsDefault = false);

public sealed record AudioDeviceSelection(
    string MeetingCaptureId,
    string MicrophoneCaptureId,
    string HeadphonesRenderId,
    string MeetingMicrophoneRenderId);

public sealed record AppSettings(
    AudioDeviceSelection? Devices,
    int PushToTalkVirtualKey = 0x77,
    float OriginalVolume = 0f,
    float TranslationVolume = 1f,
    bool MonitorOutgoing = false)
{
    public static AppSettings Default { get; } = new(Devices: null);
}

public sealed record TranslationStreamOptions(
    TranslationDirection Direction,
    string ApiKey,
    string SourceLanguage,
    string TargetLanguage,
    string? NoiseReduction,
    string Endpoint = "wss://api.openai.com/v1/realtime/translations",
    string Model = "gpt-realtime-translate",
    string TranscriptionModel = "gpt-realtime-whisper",
    string? SafetyIdentifier = null);

public sealed record TranscriptDelta(
    TranslationDirection Direction,
    TranscriptKind Kind,
    string Text,
    long? ElapsedMilliseconds,
    DateTimeOffset ReceivedAt);

public sealed record CaptionSegment(
    Guid Id,
    Guid MeetingId,
    TranslationDirection Direction,
    long StartMilliseconds,
    long EndMilliseconds,
    string SourceText,
    string TranslatedText,
    bool IsFinal);

public sealed record CaptionSnapshot(
    TranslationDirection Direction,
    string SourceText,
    string TranslatedText,
    long StartMilliseconds,
    long EndMilliseconds,
    bool IsFinal);

public sealed record MeetingRecord(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    MeetingStatus Status,
    string SourceLanguage = "en",
    string TargetLanguage = "es");

public sealed record LatencySample(
    TranslationDirection Direction,
    DateTimeOffset SpeechDetectedAt,
    DateTimeOffset FirstOutputAt)
{
    public TimeSpan Latency => FirstOutputAt - SpeechDetectedAt;
}

public sealed record AudioDelta(
    TranslationDirection Direction,
    byte[] Pcm16,
    int SampleRate,
    int Channels,
    long? ElapsedMilliseconds);

public sealed record TranslationFault(
    TranslationDirection Direction,
    TranslationErrorKind Kind,
    string Message,
    bool IsRecoverable,
    Exception? Exception = null);

public sealed class TranslationException(
    TranslationErrorKind kind,
    string message,
    bool isRecoverable,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public TranslationErrorKind Kind { get; } = kind;
    public bool IsRecoverable { get; } = isRecoverable;
}
