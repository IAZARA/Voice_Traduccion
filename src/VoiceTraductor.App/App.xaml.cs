using System.IO;
using System.Windows;
using VoiceTraductor.Core;
using VoiceTraductor.Infrastructure.Audio;
using VoiceTraductor.Infrastructure.Meeting;
using VoiceTraductor.Infrastructure.Persistence;
using VoiceTraductor.Infrastructure.Realtime;
using VoiceTraductor.Infrastructure.Security;

namespace VoiceTraductor.App;

public partial class App : Application
{
    private IAudioEndpointService? _audioEndpointService;
    private IMeetingSession? _meetingSession;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var applicationDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoiceTraductor");
            var credentialStore = new DpapiCredentialStore(applicationDataDirectory);
            var settingsStore = new JsonSettingsStore(applicationDataDirectory);
            var transcriptStore = new SqliteTranscriptStore(applicationDataDirectory);
            await transcriptStore.InitializeAsync();

            _audioEndpointService = new NAudioEndpointService();
            var streamFactory = new TranslationStreamFactory();
            var captionAssembler = new CaptionAssembler();
            _meetingSession = new MeetingSession(
                streamFactory,
                _audioEndpointService,
                credentialStore,
                transcriptStore,
                captionAssembler);
            var viewModel = new MainViewModel(
                _meetingSession,
                _audioEndpointService,
                credentialStore,
                new OpenAiApiKeyValidator(streamFactory),
                settingsStore,
                transcriptStore);
            await viewModel.InitializeAsync();

            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"VoiceTraductor no pudo iniciar.\n\n{exception.Message}",
                "Error de inicio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_meetingSession is not null)
        {
            await _meetingSession.DisposeAsync();
        }

        _audioEndpointService?.Dispose();
        base.OnExit(e);
    }
}
