using System.Collections.ObjectModel;
using System.Windows;
using VoiceTraductor.Core;

namespace VoiceTraductor.App;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IMeetingSession _meetingSession;
    private readonly IAudioEndpointService _audioEndpoints;
    private readonly ICredentialStore _credentialStore;
    private readonly IApiKeyValidator _apiKeyValidator;
    private readonly ISettingsStore _settingsStore;
    private readonly ITranscriptStore _transcriptStore;
    private AppSettings _settings = AppSettings.Default;
    private AudioEndpoint? _meetingCapture;
    private AudioEndpoint? _microphoneCapture;
    private AudioEndpoint? _headphonesRender;
    private AudioEndpoint? _meetingMicrophoneRender;
    private MeetingRecord? _selectedMeeting;
    private float _originalVolume;
    private float _translationVolume = 1f;
    private string _incomingOriginal = "El texto original en inglés aparecerá aquí.";
    private string _incomingTranslation = "La traducción al español aparecerá aquí.";
    private string _outgoingOriginal = "Mantén F8 para hablar en español.";
    private string _outgoingTranslation = "La traducción al inglés aparecerá aquí.";
    private string _overlayOriginal = "Esperando audio…";
    private string _overlayTranslation = "VoiceTraductor está listo";
    private string _statusMessage = "Completa la configuración inicial.";
    private bool _hasCredential;
    private bool _hasVoiceMeeter;
    private bool _isBusy;

    public MainViewModel(
        IMeetingSession meetingSession,
        IAudioEndpointService audioEndpoints,
        ICredentialStore credentialStore,
        IApiKeyValidator apiKeyValidator,
        ISettingsStore settingsStore,
        ITranscriptStore transcriptStore)
    {
        _meetingSession = meetingSession;
        _audioEndpoints = audioEndpoints;
        _credentialStore = credentialStore;
        _apiKeyValidator = apiKeyValidator;
        _settingsStore = settingsStore;
        _transcriptStore = transcriptStore;
        _meetingSession.CaptionChanged += OnCaptionChanged;
        _meetingSession.Faulted += OnFaulted;
        _meetingSession.StateChanged += OnSessionStateChanged;
    }

    public ObservableCollection<AudioEndpoint> CaptureEndpoints { get; } = [];
    public ObservableCollection<AudioEndpoint> RenderEndpoints { get; } = [];
    public ObservableCollection<MeetingRecord> Meetings { get; } = [];
    public ObservableCollection<CaptionSegment> SelectedMeetingSegments { get; } = [];

    public AudioEndpoint? MeetingCapture
    {
        get => _meetingCapture;
        set
        {
            if (SetProperty(ref _meetingCapture, value))
            {
                OnPropertyChanged(nameof(CanStartMeeting));
            }
        }
    }

    public AudioEndpoint? MicrophoneCapture
    {
        get => _microphoneCapture;
        set
        {
            if (SetProperty(ref _microphoneCapture, value))
            {
                OnPropertyChanged(nameof(CanStartMeeting));
            }
        }
    }

    public AudioEndpoint? HeadphonesRender
    {
        get => _headphonesRender;
        set
        {
            if (SetProperty(ref _headphonesRender, value))
            {
                OnPropertyChanged(nameof(CanStartMeeting));
            }
        }
    }

    public AudioEndpoint? MeetingMicrophoneRender
    {
        get => _meetingMicrophoneRender;
        set
        {
            if (SetProperty(ref _meetingMicrophoneRender, value))
            {
                OnPropertyChanged(nameof(CanStartMeeting));
            }
        }
    }

    public MeetingRecord? SelectedMeeting
    {
        get => _selectedMeeting;
        set
        {
            if (SetProperty(ref _selectedMeeting, value))
            {
                OnPropertyChanged(nameof(HasSelectedMeeting));
                _ = LoadSelectedMeetingAsync();
            }
        }
    }

    public float OriginalVolume
    {
        get => _originalVolume;
        set
        {
            if (SetProperty(ref _originalVolume, Math.Clamp(value, 0f, 1f)))
            {
                _meetingSession.SetOriginalVolume(_originalVolume);
            }
        }
    }

    public float TranslationVolume
    {
        get => _translationVolume;
        set
        {
            if (SetProperty(ref _translationVolume, Math.Clamp(value, 0f, 1f)))
            {
                _meetingSession.SetTranslationVolume(_translationVolume);
            }
        }
    }

    public string IncomingOriginal
    {
        get => _incomingOriginal;
        private set => SetProperty(ref _incomingOriginal, value);
    }

    public string IncomingTranslation
    {
        get => _incomingTranslation;
        private set => SetProperty(ref _incomingTranslation, value);
    }

    public string OutgoingOriginal
    {
        get => _outgoingOriginal;
        private set => SetProperty(ref _outgoingOriginal, value);
    }

    public string OutgoingTranslation
    {
        get => _outgoingTranslation;
        private set => SetProperty(ref _outgoingTranslation, value);
    }

    public string OverlayOriginal
    {
        get => _overlayOriginal;
        private set => SetProperty(ref _overlayOriginal, value);
    }

    public string OverlayTranslation
    {
        get => _overlayTranslation;
        private set => SetProperty(ref _overlayTranslation, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasCredential
    {
        get => _hasCredential;
        private set
        {
            if (SetProperty(ref _hasCredential, value))
            {
                OnPropertyChanged(nameof(CanStartMeeting));
                OnPropertyChanged(nameof(IsSetupComplete));
            }
        }
    }

    public bool HasVoiceMeeter
    {
        get => _hasVoiceMeeter;
        private set
        {
            if (SetProperty(ref _hasVoiceMeeter, value))
            {
                OnPropertyChanged(nameof(IsSetupComplete));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanStartMeeting));
            }
        }
    }

    public bool IsRunning => _meetingSession.IsRunning;
    public bool HasSelectedMeeting => SelectedMeeting is not null;
    public bool PushToTalkActive => _meetingSession.PushToTalkActive;
    public string IncomingState => StateLabel(_meetingSession.IncomingState);
    public string OutgoingState => StateLabel(_meetingSession.OutgoingState);
    public double IncomingLevelPercent => _meetingSession.IncomingLevel * 100;
    public double MicrophoneLevelPercent => _meetingSession.MicrophoneLevel * 100;
    public int PushToTalkVirtualKey => _settings.PushToTalkVirtualKey;
    public bool IsSetupComplete => HasCredential && HasVoiceMeeter && DevicesSelected;
    public bool DevicesSelected =>
        MeetingCapture is not null &&
        MicrophoneCapture is not null &&
        HeadphonesRender is not null &&
        MeetingMicrophoneRender is not null;
    public bool CanStartMeeting =>
        !IsRunning && !IsBusy && HasCredential && DevicesSelected;

    public async Task InitializeAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        OriginalVolume = _settings.OriginalVolume;
        TranslationVolume = _settings.TranslationVolume;
        HasCredential = _credentialStore.Exists;
        RefreshDevices();
        ApplySavedDevices();
        await RefreshHistoryAsync();
        StatusMessage = IsSetupComplete
            ? "Configuración lista. Inicia una reunión cuando quieras."
            : "Completa los pasos de configuración antes de iniciar.";
    }

    public void RefreshDevices()
    {
        var captures = _audioEndpoints.GetCaptureEndpoints();
        var renders = _audioEndpoints.GetRenderEndpoints();
        CaptureEndpoints.Clear();
        RenderEndpoints.Clear();
        foreach (var endpoint in captures)
        {
            CaptureEndpoints.Add(endpoint);
        }

        foreach (var endpoint in renders)
        {
            RenderEndpoints.Add(endpoint);
        }

        HasVoiceMeeter = _audioEndpoints.HasVoiceMeeterRoutes();
    }

    public async Task ValidateAndSaveApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusMessage = "Validando la credencial con OpenAI…";
        try
        {
            await _apiKeyValidator.ValidateAsync(apiKey, cancellationToken);
            await _credentialStore.SaveAsync(apiKey, cancellationToken);
            HasCredential = true;
            StatusMessage = "API key validada y cifrada para este usuario de Windows.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (!DevicesSelected)
        {
            throw new InvalidOperationException("Selecciona los cuatro dispositivos de audio.");
        }

        _settings = new AppSettings(
            new AudioDeviceSelection(
                MeetingCapture!.Id,
                MicrophoneCapture!.Id,
                HeadphonesRender!.Id,
                MeetingMicrophoneRender!.Id),
            _settings.PushToTalkVirtualKey,
            OriginalVolume,
            TranslationVolume,
            false);
        await _settingsStore.SaveAsync(_settings, cancellationToken);
        StatusMessage = "Dispositivos guardados. Configura B1/B2 en VoiceMeeter.";
        OnPropertyChanged(nameof(IsSetupComplete));
        OnPropertyChanged(nameof(CanStartMeeting));
    }

    public async Task StartMeetingAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStartMeeting)
        {
            throw new InvalidOperationException("La configuración todavía no está completa.");
        }

        await SaveConfigurationAsync(cancellationToken);
        IsBusy = true;
        StatusMessage = "Conectando las dos direcciones de traducción…";
        try
        {
            await _meetingSession.StartAsync(
                _settings.Devices!,
                OriginalVolume,
                TranslationVolume,
                cancellationToken);
            StatusMessage = "Reunión activa. Mantén F8 para hablar en español.";
        }
        finally
        {
            IsBusy = false;
            NotifySessionProperties();
        }
    }

    public async Task StopMeetingAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusMessage = "Finalizando y guardando la transcripción…";
        try
        {
            await _meetingSession.StopAsync(cancellationToken);
            await RefreshHistoryAsync(cancellationToken);
            StatusMessage = "Reunión finalizada y guardada localmente.";
        }
        finally
        {
            IsBusy = false;
            NotifySessionProperties();
        }
    }

    public void SetPushToTalk(bool active)
    {
        _meetingSession.SetPushToTalk(active);
        NotifySessionProperties();
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        var meetings = await _transcriptStore.GetMeetingsAsync(cancellationToken);
        await RunOnUiAsync(
            () =>
            {
                Meetings.Clear();
                foreach (var meeting in meetings)
                {
                    Meetings.Add(meeting);
                }
            });
    }

    public async Task DeleteSelectedMeetingAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedMeeting is null)
        {
            return;
        }

        await _transcriptStore.DeleteMeetingAsync(SelectedMeeting.Id, cancellationToken);
        SelectedMeeting = null;
        SelectedMeetingSegments.Clear();
        await RefreshHistoryAsync(cancellationToken);
    }

    public async Task DeleteAllMeetingsAsync(CancellationToken cancellationToken = default)
    {
        await _transcriptStore.DeleteAllAsync(cancellationToken);
        SelectedMeeting = null;
        SelectedMeetingSegments.Clear();
        await RefreshHistoryAsync(cancellationToken);
    }

    public Task ExportSelectedTextAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        SelectedMeeting is null
            ? Task.CompletedTask
            : _transcriptStore.ExportTextAsync(SelectedMeeting.Id, path, cancellationToken);

    public Task ExportSelectedWebVttAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        SelectedMeeting is null
            ? Task.CompletedTask
            : _transcriptStore.ExportWebVttAsync(
                SelectedMeeting.Id,
                path,
                cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _meetingSession.CaptionChanged -= OnCaptionChanged;
        _meetingSession.Faulted -= OnFaulted;
        _meetingSession.StateChanged -= OnSessionStateChanged;
        await _meetingSession.DisposeAsync();
    }

    private void ApplySavedDevices()
    {
        var devices = _settings.Devices;
        if (devices is null)
        {
            MeetingCapture = FindEndpoint(
                CaptureEndpoints,
                "VoiceMeeter Output",
                exclude: "Aux");
            MicrophoneCapture = CaptureEndpoints.FirstOrDefault(
                endpoint => endpoint.IsDefault &&
                            !endpoint.Name.Contains(
                                "VoiceMeeter",
                                StringComparison.OrdinalIgnoreCase));
            HeadphonesRender = RenderEndpoints.FirstOrDefault(
                endpoint => endpoint.IsDefault &&
                            !endpoint.Name.Contains(
                                "VoiceMeeter",
                                StringComparison.OrdinalIgnoreCase));
            MeetingMicrophoneRender = FindEndpoint(
                RenderEndpoints,
                "VoiceMeeter Aux Input");
            return;
        }

        MeetingCapture = CaptureEndpoints.FirstOrDefault(e => e.Id == devices.MeetingCaptureId);
        MicrophoneCapture =
            CaptureEndpoints.FirstOrDefault(e => e.Id == devices.MicrophoneCaptureId);
        HeadphonesRender =
            RenderEndpoints.FirstOrDefault(e => e.Id == devices.HeadphonesRenderId);
        MeetingMicrophoneRender =
            RenderEndpoints.FirstOrDefault(e => e.Id == devices.MeetingMicrophoneRenderId);
    }

    private static AudioEndpoint? FindEndpoint(
        IEnumerable<AudioEndpoint> endpoints,
        string contains,
        string? exclude = null) =>
        endpoints.FirstOrDefault(
            endpoint =>
                endpoint.Name.Contains(contains, StringComparison.OrdinalIgnoreCase) &&
                (exclude is null ||
                 !endpoint.Name.Contains(exclude, StringComparison.OrdinalIgnoreCase)));

    private async Task LoadSelectedMeetingAsync()
    {
        var selected = SelectedMeeting;
        if (selected is null)
        {
            SelectedMeetingSegments.Clear();
            return;
        }

        var segments = await _transcriptStore.GetSegmentsAsync(selected.Id);
        await RunOnUiAsync(
            () =>
            {
                SelectedMeetingSegments.Clear();
                foreach (var segment in segments)
                {
                    SelectedMeetingSegments.Add(segment);
                }
            });
    }

    private void OnCaptionChanged(object? sender, CaptionSnapshot caption)
    {
        _ = RunOnUiAsync(
            () =>
            {
                var source = string.IsNullOrWhiteSpace(caption.SourceText)
                    ? "…"
                    : caption.SourceText;
                var translation = string.IsNullOrWhiteSpace(caption.TranslatedText)
                    ? "…"
                    : caption.TranslatedText;
                if (caption.Direction == TranslationDirection.IncomingEnglishToSpanish)
                {
                    IncomingOriginal = source;
                    IncomingTranslation = translation;
                    OverlayOriginal = $"EN · {source}";
                    OverlayTranslation = translation;
                }
                else
                {
                    OutgoingOriginal = source;
                    OutgoingTranslation = translation;
                    OverlayOriginal = $"ES · {source}";
                    OverlayTranslation = translation;
                }
            });
    }

    private void OnFaulted(object? sender, TranslationFault fault)
    {
        _ = RunOnUiAsync(
            () =>
            {
                StatusMessage = fault.Kind switch
                {
                    TranslationErrorKind.Authentication =>
                        "La API key fue rechazada. Revísala en Configuración.",
                    TranslationErrorKind.RateLimit =>
                        "La API alcanzó un límite o no tiene saldo disponible.",
                    TranslationErrorKind.Device =>
                        "Un dispositivo de audio se desconectó.",
                    _ => fault.Message
                };
            });
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) =>
        _ = RunOnUiAsync(NotifySessionProperties);

    private void NotifySessionProperties()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(PushToTalkActive));
        OnPropertyChanged(nameof(IncomingState));
        OnPropertyChanged(nameof(OutgoingState));
        OnPropertyChanged(nameof(IncomingLevelPercent));
        OnPropertyChanged(nameof(MicrophoneLevelPercent));
        OnPropertyChanged(nameof(CanStartMeeting));
    }

    private static string StateLabel(TranslationSessionState state) =>
        state switch
        {
            TranslationSessionState.Idle => "Inactiva",
            TranslationSessionState.Connecting => "Conectando",
            TranslationSessionState.Ready => "Lista",
            TranslationSessionState.Reconnecting => "Reconectando",
            TranslationSessionState.Stopping => "Finalizando",
            TranslationSessionState.Faulted => "Con error",
            _ => state.ToString()
        };

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
