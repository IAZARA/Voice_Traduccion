using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using VoiceTraductor.Core;

namespace VoiceTraductor.Infrastructure.Meeting;

public sealed class MeetingSession(
    ITranslationStreamFactory streamFactory,
    IAudioEndpointService audioEndpoints,
    ICredentialStore credentialStore,
    ITranscriptStore transcriptStore,
    ICaptionAssembler captionAssembler) : IMeetingSession
{
    private readonly object _audioGate = new();
    private readonly PcmFrameChunker _incomingChunker = new();
    private readonly PcmFrameChunker _outgoingChunker = new();
    private readonly Stopwatch _meetingClock = new();
    private ITranslationStream? _incomingStream;
    private ITranslationStream? _outgoingStream;
    private IAudioCapture? _meetingCapture;
    private IAudioCapture? _microphoneCapture;
    private IAudioPlayback? _translatedIncomingPlayback;
    private IAudioPlayback? _originalIncomingPlayback;
    private IAudioPlayback? _translatedOutgoingPlayback;
    private Timer? _silenceTimer;
    private Channel<CaptionSegment>? _segmentChannel;
    private Task? _segmentWriterTask;
    private long _lastIncomingSpeechMilliseconds;
    private long _lastOutgoingSpeechMilliseconds;
    private bool _disposed;

    public bool IsRunning { get; private set; }
    public bool PushToTalkActive { get; private set; }
    public float IncomingLevel { get; private set; }
    public float MicrophoneLevel { get; private set; }
    public MeetingRecord? CurrentMeeting { get; private set; }
    public TranslationSessionState IncomingState =>
        _incomingStream?.State ?? TranslationSessionState.Idle;
    public TranslationSessionState OutgoingState =>
        _outgoingStream?.State ?? TranslationSessionState.Idle;

    public event EventHandler<CaptionSnapshot>? CaptionChanged;
    public event EventHandler<TranslationFault>? Faulted;
    public event EventHandler? StateChanged;

    public async Task StartAsync(
        AudioDeviceSelection devices,
        float originalVolume,
        float translationVolume,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            throw new InvalidOperationException("Ya hay una reunión activa.");
        }

        var apiKey = await credentialStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranslationException(
                TranslationErrorKind.Authentication,
                "Configura y valida una API key antes de iniciar.",
                false);
        }

        IsRunning = true;
        PushToTalkActive = false;
        _meetingClock.Restart();
        CurrentMeeting = new MeetingRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            MeetingStatus.Active);
        captionAssembler.StartMeeting(CurrentMeeting.Id);
        captionAssembler.CaptionChanged += OnCaptionChanged;
        captionAssembler.SegmentFinalized += OnSegmentFinalized;
        await transcriptStore.CreateMeetingAsync(CurrentMeeting, cancellationToken)
            .ConfigureAwait(false);
        StartSegmentWriter();

        try
        {
            CreateAudioRoutes(devices, originalVolume, translationVolume);
            CreateTranslationStreams(apiKey);

            await Task.WhenAll(
                    _incomingStream!.ConnectAsync(
                        CreateOptions(
                            TranslationDirection.IncomingEnglishToSpanish,
                            apiKey,
                            "en",
                            "es",
                            null),
                        cancellationToken),
                    _outgoingStream!.ConnectAsync(
                        CreateOptions(
                            TranslationDirection.OutgoingSpanishToEnglish,
                            apiKey,
                            "es",
                            "en",
                            "near_field"),
                        cancellationToken))
                .ConfigureAwait(false);

            _meetingCapture!.Start();
            _microphoneCapture!.Start();
            _silenceTimer = new Timer(
                CheckSilence,
                null,
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(200));
            RaiseStateChanged();
        }
        catch
        {
            await StopInternalAsync(MeetingStatus.Faulted, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
    }

    public void SetPushToTalk(bool active)
    {
        if (!IsRunning || PushToTalkActive == active)
        {
            return;
        }

        lock (_audioGate)
        {
            _outgoingChunker.Reset();
            PushToTalkActive = active;
        }

        if (!active)
        {
            _lastOutgoingSpeechMilliseconds = _meetingClock.ElapsedMilliseconds;
        }

        RaiseStateChanged();
    }

    public void SetOriginalVolume(float volume)
    {
        if (_originalIncomingPlayback is not null)
        {
            _originalIncomingPlayback.Volume = Math.Clamp(volume, 0f, 1f);
        }
    }

    public void SetTranslationVolume(float volume)
    {
        if (_translatedIncomingPlayback is not null)
        {
            _translatedIncomingPlayback.Volume = Math.Clamp(volume, 0f, 1f);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        StopInternalAsync(MeetingStatus.Completed, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (IsRunning)
        {
            await StopInternalAsync(MeetingStatus.Interrupted, CancellationToken.None)
                .ConfigureAwait(false);
        }

        _disposed = true;
    }

    private void CreateAudioRoutes(
        AudioDeviceSelection devices,
        float originalVolume,
        float translationVolume)
    {
        _meetingCapture = audioEndpoints.CreatePcm24KhzCapture(devices.MeetingCaptureId);
        _microphoneCapture =
            audioEndpoints.CreatePcm24KhzCapture(devices.MicrophoneCaptureId);
        _translatedIncomingPlayback = audioEndpoints.CreatePcm24KhzPlayback(
            devices.HeadphonesRenderId,
            TimeSpan.FromSeconds(3));
        _originalIncomingPlayback = audioEndpoints.CreatePcm24KhzPlayback(
            devices.HeadphonesRenderId,
            TimeSpan.FromSeconds(2));
        _translatedOutgoingPlayback = audioEndpoints.CreatePcm24KhzPlayback(
            devices.MeetingMicrophoneRenderId,
            TimeSpan.FromSeconds(3));

        _translatedIncomingPlayback.Volume = Math.Clamp(translationVolume, 0f, 1f);
        _originalIncomingPlayback.Volume = Math.Clamp(originalVolume, 0f, 1f);
        _translatedOutgoingPlayback.Volume = 1f;

        _meetingCapture.FrameReady += OnMeetingAudio;
        _meetingCapture.LevelChanged += OnIncomingLevel;
        _meetingCapture.Faulted += OnAudioFault;
        _microphoneCapture.FrameReady += OnMicrophoneAudio;
        _microphoneCapture.LevelChanged += OnMicrophoneLevel;
        _microphoneCapture.Faulted += OnAudioFault;
        _incomingChunker.FrameReady += OnIncomingFrame;
        _outgoingChunker.FrameReady += OnOutgoingFrame;
    }

    private void CreateTranslationStreams(string apiKey)
    {
        _incomingStream =
            streamFactory.Create(TranslationDirection.IncomingEnglishToSpanish);
        _outgoingStream =
            streamFactory.Create(TranslationDirection.OutgoingSpanishToEnglish);
        SubscribeStream(_incomingStream);
        SubscribeStream(_outgoingStream);
    }

    private void SubscribeStream(ITranslationStream stream)
    {
        stream.StateChanged += OnStreamStateChanged;
        stream.AudioReceived += OnTranslatedAudio;
        stream.TranscriptReceived += OnTranscript;
        stream.Faulted += OnTranslationFault;
    }

    private void UnsubscribeStream(ITranslationStream stream)
    {
        stream.StateChanged -= OnStreamStateChanged;
        stream.AudioReceived -= OnTranslatedAudio;
        stream.TranscriptReceived -= OnTranscript;
        stream.Faulted -= OnTranslationFault;
    }

    private void OnMeetingAudio(object? sender, ReadOnlyMemory<byte> audio)
    {
        _originalIncomingPlayback?.Enqueue(audio.Span);
        lock (_audioGate)
        {
            _incomingChunker.Add(audio.Span);
        }
    }

    private void OnMicrophoneAudio(object? sender, ReadOnlyMemory<byte> audio)
    {
        lock (_audioGate)
        {
            if (PushToTalkActive)
            {
                _outgoingChunker.Add(audio.Span);
            }
            else
            {
                var silence = new byte[audio.Length];
                _outgoingChunker.Add(silence);
            }
        }
    }

    private void OnIncomingFrame(object? sender, ReadOnlyMemory<byte> frame)
    {
        if (_incomingStream is not null)
        {
            _ = SendFrameAsync(_incomingStream, frame);
        }
    }

    private void OnOutgoingFrame(object? sender, ReadOnlyMemory<byte> frame)
    {
        if (_outgoingStream is not null)
        {
            _ = SendFrameAsync(_outgoingStream, frame);
        }
    }

    private async Task SendFrameAsync(
        ITranslationStream stream,
        ReadOnlyMemory<byte> frame)
    {
        try
        {
            await stream.SendAudioAsync(frame).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRunning)
        {
            Faulted?.Invoke(
                this,
                new TranslationFault(
                    stream.Direction,
                    TranslationErrorKind.Network,
                    "No se pudo enviar un bloque de audio.",
                    true,
                    exception));
        }
    }

    private void OnTranslatedAudio(object? sender, AudioDelta audio)
    {
        if (audio.Direction == TranslationDirection.IncomingEnglishToSpanish)
        {
            _translatedIncomingPlayback?.Enqueue(audio.Pcm16);
        }
        else
        {
            _translatedOutgoingPlayback?.Enqueue(audio.Pcm16);
        }
    }

    private void OnTranscript(object? sender, TranscriptDelta delta) =>
        captionAssembler.Append(delta);

    private void OnCaptionChanged(object? sender, CaptionSnapshot caption) =>
        CaptionChanged?.Invoke(this, caption);

    private void OnSegmentFinalized(object? sender, CaptionSegment segment)
    {
        _segmentChannel?.Writer.TryWrite(segment);
    }

    private void OnIncomingLevel(object? sender, float level)
    {
        IncomingLevel = level;
        if (level >= 0.012f)
        {
            _lastIncomingSpeechMilliseconds = _meetingClock.ElapsedMilliseconds;
        }

        RaiseStateChanged();
    }

    private void OnMicrophoneLevel(object? sender, float level)
    {
        MicrophoneLevel = level;
        if (PushToTalkActive && level >= 0.012f)
        {
            _lastOutgoingSpeechMilliseconds = _meetingClock.ElapsedMilliseconds;
        }

        RaiseStateChanged();
    }

    private void OnAudioFault(object? sender, Exception exception)
    {
        Faulted?.Invoke(
            this,
            new TranslationFault(
                TranslationDirection.IncomingEnglishToSpanish,
                TranslationErrorKind.Device,
                "Un dispositivo de audio dejó de estar disponible.",
                false,
                exception));
    }

    private void OnTranslationFault(object? sender, TranslationFault fault) =>
        Faulted?.Invoke(this, fault);

    private void OnStreamStateChanged(object? sender, TranslationSessionState state) =>
        RaiseStateChanged();

    private void CheckSilence(object? state)
    {
        if (!IsRunning)
        {
            return;
        }

        var elapsed = _meetingClock.ElapsedMilliseconds;
        if (elapsed - Interlocked.Read(ref _lastIncomingSpeechMilliseconds) >= 800)
        {
            captionAssembler.NotifySilence(
                TranslationDirection.IncomingEnglishToSpanish,
                elapsed);
        }

        if (!PushToTalkActive &&
            elapsed - Interlocked.Read(ref _lastOutgoingSpeechMilliseconds) >= 800)
        {
            captionAssembler.NotifySilence(
                TranslationDirection.OutgoingSpanishToEnglish,
                elapsed);
        }
    }

    private void StartSegmentWriter()
    {
        _segmentChannel = Channel.CreateUnbounded<CaptionSegment>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        _segmentWriterTask = Task.Run(
            async () =>
            {
                await foreach (var segment in _segmentChannel.Reader.ReadAllAsync()
                                   .ConfigureAwait(false))
                {
                    await transcriptStore.AddSegmentAsync(segment).ConfigureAwait(false);
                }
            });
    }

    private async Task StopInternalAsync(
        MeetingStatus status,
        CancellationToken cancellationToken)
    {
        if (!IsRunning && CurrentMeeting is null)
        {
            return;
        }

        IsRunning = false;
        PushToTalkActive = false;
        _silenceTimer?.Dispose();
        _silenceTimer = null;
        _meetingCapture?.Stop();
        _microphoneCapture?.Stop();
        captionAssembler.FlushAll();

        var closeTasks = new List<Task>();
        if (_incomingStream is not null)
        {
            closeTasks.Add(_incomingStream.CloseAsync(cancellationToken));
        }

        if (_outgoingStream is not null)
        {
            closeTasks.Add(_outgoingStream.CloseAsync(cancellationToken));
        }

        try
        {
            await Task.WhenAll(closeTasks).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            status = MeetingStatus.Interrupted;
            Faulted?.Invoke(
                this,
                new TranslationFault(
                    TranslationDirection.IncomingEnglishToSpanish,
                    TranslationErrorKind.Network,
                    "La sesión terminó sin recibir la confirmación final del servicio.",
                    true,
                    exception));
        }

        _segmentChannel?.Writer.TryComplete();
        if (_segmentWriterTask is not null)
        {
            await _segmentWriterTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (CurrentMeeting is not null)
        {
            var endedAt = DateTimeOffset.UtcNow;
            await transcriptStore.FinishMeetingAsync(
                    CurrentMeeting.Id,
                    endedAt,
                    status,
                    cancellationToken)
                .ConfigureAwait(false);
            CurrentMeeting = CurrentMeeting with
            {
                EndedAt = endedAt,
                Status = status
            };
        }

        await DisposeSessionResourcesAsync().ConfigureAwait(false);
        _meetingClock.Stop();
        IncomingLevel = 0;
        MicrophoneLevel = 0;
        captionAssembler.CaptionChanged -= OnCaptionChanged;
        captionAssembler.SegmentFinalized -= OnSegmentFinalized;
        RaiseStateChanged();
    }

    private async Task DisposeSessionResourcesAsync()
    {
        if (_incomingStream is not null)
        {
            UnsubscribeStream(_incomingStream);
            await _incomingStream.DisposeAsync().ConfigureAwait(false);
            _incomingStream = null;
        }

        if (_outgoingStream is not null)
        {
            UnsubscribeStream(_outgoingStream);
            await _outgoingStream.DisposeAsync().ConfigureAwait(false);
            _outgoingStream = null;
        }

        _meetingCapture?.Dispose();
        _meetingCapture = null;
        _microphoneCapture?.Dispose();
        _microphoneCapture = null;
        _translatedIncomingPlayback?.Dispose();
        _translatedIncomingPlayback = null;
        _originalIncomingPlayback?.Dispose();
        _originalIncomingPlayback = null;
        _translatedOutgoingPlayback?.Dispose();
        _translatedOutgoingPlayback = null;
        _incomingChunker.Reset();
        _outgoingChunker.Reset();
        _segmentChannel = null;
        _segmentWriterTask = null;
    }

    private static TranslationStreamOptions CreateOptions(
        TranslationDirection direction,
        string apiKey,
        string source,
        string target,
        string? noiseReduction) =>
        new(
            direction,
            apiKey,
            source,
            target,
            noiseReduction,
            SafetyIdentifier: CreateSafetyIdentifier());

    private static string CreateSafetyIdentifier()
    {
        var input = Encoding.UTF8.GetBytes(
            $"{Environment.UserDomainName}\\{Environment.UserName}");
        return $"vt_{Convert.ToHexString(SHA256.HashData(input))[..32].ToLowerInvariant()}";
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
