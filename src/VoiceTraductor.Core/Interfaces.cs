namespace VoiceTraductor.Core;

public interface ITranslationStream : IAsyncDisposable
{
    TranslationDirection Direction { get; }
    TranslationSessionState State { get; }

    event EventHandler<TranslationSessionState>? StateChanged;
    event EventHandler<AudioDelta>? AudioReceived;
    event EventHandler<TranscriptDelta>? TranscriptReceived;
    event EventHandler<TranslationFault>? Faulted;

    Task ConnectAsync(TranslationStreamOptions options, CancellationToken cancellationToken = default);
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}

public interface ITranslationStreamFactory
{
    ITranslationStream Create(TranslationDirection direction);
}

public interface IApiKeyValidator
{
    Task ValidateAsync(string apiKey, CancellationToken cancellationToken = default);
}

public interface IAudioCapture : IDisposable
{
    event EventHandler<ReadOnlyMemory<byte>>? FrameReady;
    event EventHandler<float>? LevelChanged;
    event EventHandler<Exception>? Faulted;

    void Start();
    void Stop();
}

public interface IAudioPlayback : IDisposable
{
    float Volume { get; set; }
    void Enqueue(ReadOnlySpan<byte> pcm16);
    void Clear();
}

public interface IAudioEndpointService : IDisposable
{
    IReadOnlyList<AudioEndpoint> GetCaptureEndpoints();
    IReadOnlyList<AudioEndpoint> GetRenderEndpoints();
    IAudioCapture CreatePcm24KhzCapture(string endpointId);
    IAudioPlayback CreatePcm24KhzPlayback(string endpointId, TimeSpan maximumBuffer);
    bool HasVoiceMeeterRoutes();
}

public interface ICaptionAssembler
{
    event EventHandler<CaptionSnapshot>? CaptionChanged;
    event EventHandler<CaptionSegment>? SegmentFinalized;

    Guid MeetingId { get; }
    void StartMeeting(Guid meetingId);
    void Append(TranscriptDelta delta);
    void NotifySilence(TranslationDirection direction, long elapsedMilliseconds);
    void Flush(TranslationDirection direction);
    void FlushAll();
}

public interface ITranscriptStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task CreateMeetingAsync(MeetingRecord meeting, CancellationToken cancellationToken = default);
    Task FinishMeetingAsync(
        Guid meetingId,
        DateTimeOffset endedAt,
        MeetingStatus status,
        CancellationToken cancellationToken = default);
    Task AddSegmentAsync(CaptionSegment segment, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MeetingRecord>> GetMeetingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CaptionSegment>> GetSegmentsAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default);
    Task DeleteMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
    Task ExportTextAsync(Guid meetingId, string path, CancellationToken cancellationToken = default);
    Task ExportWebVttAsync(Guid meetingId, string path, CancellationToken cancellationToken = default);
}

public interface ICredentialStore
{
    bool Exists { get; }
    Task SaveAsync(string apiKey, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IMeetingSession : IAsyncDisposable
{
    bool IsRunning { get; }
    bool PushToTalkActive { get; }
    float IncomingLevel { get; }
    float MicrophoneLevel { get; }
    MeetingRecord? CurrentMeeting { get; }
    TranslationSessionState IncomingState { get; }
    TranslationSessionState OutgoingState { get; }

    event EventHandler<CaptionSnapshot>? CaptionChanged;
    event EventHandler<TranslationFault>? Faulted;
    event EventHandler? StateChanged;

    Task StartAsync(
        AudioDeviceSelection devices,
        float originalVolume,
        float translationVolume,
        CancellationToken cancellationToken = default);
    void SetPushToTalk(bool active);
    void SetOriginalVolume(float volume);
    void SetTranslationVolume(float volume);
    Task StopAsync(CancellationToken cancellationToken = default);
}
