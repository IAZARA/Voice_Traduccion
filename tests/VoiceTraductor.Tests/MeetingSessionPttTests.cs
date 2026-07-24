using VoiceTraductor.Core;
using VoiceTraductor.Infrastructure.Meeting;

namespace VoiceTraductor.Tests;

public sealed class MeetingSessionPttTests
{
    [Fact]
    public async Task MicrophoneIsSilentOutsidePttAndReleaseDropsPartialAudio()
    {
        var incomingStream =
            new FakeTranslationStream(TranslationDirection.IncomingEnglishToSpanish);
        var outgoingStream =
            new FakeTranslationStream(TranslationDirection.OutgoingSpanishToEnglish);
        var audio = new FakeAudioEndpointService();
        var session = new MeetingSession(
            new FakeStreamFactory(incomingStream, outgoingStream),
            audio,
            new FakeCredentialStore(),
            new FakeTranscriptStore(),
            new CaptionAssembler());
        var devices = new AudioDeviceSelection(
            "meeting-capture",
            "microphone",
            "headphones",
            "meeting-microphone");

        await session.StartAsync(devices, 0, 1);
        var microphone = audio.Captures["microphone"];

        microphone.Emit(Enumerable.Repeat((byte)0x55, PcmFrameChunker.FrameSizeBytes).ToArray());
        await WaitForFramesAsync(outgoingStream, 1);
        Assert.All(outgoingStream.Frames[0], value => Assert.Equal(0, value));

        session.SetPushToTalk(true);
        microphone.Emit(Enumerable.Repeat((byte)0x33, PcmFrameChunker.FrameSizeBytes).ToArray());
        await WaitForFramesAsync(outgoingStream, 2);
        Assert.All(outgoingStream.Frames[1], value => Assert.Equal(0x33, value));

        microphone.Emit(Enumerable.Repeat((byte)0x77, 4_800).ToArray());
        session.SetPushToTalk(false);
        microphone.Emit(new byte[PcmFrameChunker.FrameSizeBytes]);
        await WaitForFramesAsync(outgoingStream, 3);
        Assert.All(outgoingStream.Frames[2], value => Assert.Equal(0, value));

        await session.StopAsync();
        await session.DisposeAsync();
    }

    private static async Task WaitForFramesAsync(
        FakeTranslationStream stream,
        int expectedCount)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(2);
        while (stream.Frames.Count < expectedCount && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.True(
            stream.Frames.Count >= expectedCount,
            $"Se esperaban {expectedCount} bloques y llegaron {stream.Frames.Count}.");
    }

    private sealed class FakeStreamFactory(
        FakeTranslationStream incoming,
        FakeTranslationStream outgoing) : ITranslationStreamFactory
    {
        public ITranslationStream Create(TranslationDirection direction) =>
            direction == TranslationDirection.IncomingEnglishToSpanish
                ? incoming
                : outgoing;
    }

    private sealed class FakeTranslationStream(TranslationDirection direction)
        : ITranslationStream
    {
        private readonly object _gate = new();

        public TranslationDirection Direction { get; } = direction;
        public TranslationSessionState State { get; private set; }
        public List<byte[]> Frames { get; } = [];

        public event EventHandler<TranslationSessionState>? StateChanged;
        public event EventHandler<AudioDelta>? AudioReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<TranscriptDelta>? TranscriptReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<TranslationFault>? Faulted
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(
            TranslationStreamOptions options,
            CancellationToken cancellationToken = default)
        {
            State = TranslationSessionState.Ready;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public ValueTask SendAudioAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Frames.Add(pcm16.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            State = TranslationSessionState.Idle;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudioEndpointService : IAudioEndpointService
    {
        public Dictionary<string, FakeAudioCapture> Captures { get; } = [];

        public IReadOnlyList<AudioEndpoint> GetCaptureEndpoints() => [];
        public IReadOnlyList<AudioEndpoint> GetRenderEndpoints() => [];

        public IAudioCapture CreatePcm24KhzCapture(string endpointId)
        {
            var capture = new FakeAudioCapture();
            Captures[endpointId] = capture;
            return capture;
        }

        public IAudioPlayback CreatePcm24KhzPlayback(
            string endpointId,
            TimeSpan maximumBuffer) =>
            new FakeAudioPlayback();

        public bool HasVoiceMeeterRoutes() => true;
        public void Dispose() { }
    }

    private sealed class FakeAudioCapture : IAudioCapture
    {
        public event EventHandler<ReadOnlyMemory<byte>>? FrameReady;
        public event EventHandler<float>? LevelChanged
        {
            add { }
            remove { }
        }
        public event EventHandler<Exception>? Faulted
        {
            add { }
            remove { }
        }

        public void Emit(byte[] pcm16) => FrameReady?.Invoke(this, pcm16);
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class FakeAudioPlayback : IAudioPlayback
    {
        public float Volume { get; set; }
        public void Enqueue(ReadOnlySpan<byte> pcm16) { }
        public void Clear() { }
        public void Dispose() { }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public bool Exists => true;
        public Task SaveAsync(string apiKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<string?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("test-key");
        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeTranscriptStore : ITranscriptStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task CreateMeetingAsync(
            MeetingRecord meeting,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task FinishMeetingAsync(
            Guid meetingId,
            DateTimeOffset endedAt,
            MeetingStatus status,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task AddSegmentAsync(
            CaptionSegment segment,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<MeetingRecord>> GetMeetingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MeetingRecord>>([]);
        public Task<IReadOnlyList<CaptionSegment>> GetSegmentsAsync(
            Guid meetingId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptionSegment>>([]);
        public Task DeleteMeetingAsync(
            Guid meetingId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ExportTextAsync(
            Guid meetingId,
            string path,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ExportWebVttAsync(
            Guid meetingId,
            string path,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
