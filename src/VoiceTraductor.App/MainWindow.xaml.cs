using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace VoiceTraductor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly GlobalPttHook _pttHook;
    private OverlayWindow? _overlay;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _pttHook = new GlobalPttHook(viewModel.PushToTalkVirtualKey);
        _pttHook.Pressed += OnGlobalPttPressed;
        _pttHook.Released += OnGlobalPttReleased;
        if (!viewModel.IsSetupComplete)
        {
            MainTabs.SelectedIndex = 1;
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        MainTabs.Focus();
        MainTabs.BringIntoView();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_allowClose || !_viewModel.IsRunning)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        var result = MessageBox.Show(
            "Hay una reunión activa. ¿Quieres finalizarla y guardar la transcripción?",
            "Finalizar VoiceTraductor",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteUiActionAsync(() => _viewModel.StopMeetingAsync());
        _allowClose = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _pttHook.Dispose();
        _overlay?.Close();
        base.OnClosed(e);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var succeeded = await ExecuteUiActionAsync(() => _viewModel.StartMeetingAsync());
        if (!succeeded)
        {
            return;
        }

        _overlay ??= new OverlayWindow(_viewModel);
        _overlay.Show();
        _overlay.Activate();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetPushToTalk(false);
        if (await ExecuteUiActionAsync(() => _viewModel.StopMeetingAsync()))
        {
            _overlay?.Hide();
        }
    }

    private async void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowError("Introduce una API key.");
            return;
        }

        if (await ExecuteUiActionAsync(() => _viewModel.ValidateAndSaveApiKeyAsync(key)))
        {
            ApiKeyBox.Clear();
        }
    }

    private async void SaveConfiguration_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiActionAsync(() => _viewModel.SaveConfigurationAsync());

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.RefreshDevices();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void OpenVoiceMeeter_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(
            new ProcessStartInfo("https://vb-audio.com/Voicemeeter/banana.htm")
            {
                UseShellExecute = true
            });
    }

    private void PttButton_Down(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsRunning)
        {
            return;
        }

        PttButton.CaptureMouse();
        _viewModel.SetPushToTalk(true);
    }

    private void PttButton_Up(object sender, MouseButtonEventArgs e)
    {
        _viewModel.SetPushToTalk(false);
        PttButton.ReleaseMouseCapture();
    }

    private void PttButton_LostMouseCapture(object sender, MouseEventArgs e) =>
        _viewModel.SetPushToTalk(false);

    private void OnGlobalPttPressed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => _viewModel.SetPushToTalk(true));

    private void OnGlobalPttReleased(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => _viewModel.SetPushToTalk(false));

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedMeeting is null)
        {
            return;
        }

        if (MessageBox.Show(
                "¿Eliminar definitivamente esta transcripción?",
                "Eliminar reunión",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await ExecuteUiActionAsync(() => _viewModel.DeleteSelectedMeetingAsync());
        }
    }

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "¿Eliminar definitivamente todo el historial?",
                "Borrar historial",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await ExecuteUiActionAsync(() => _viewModel.DeleteAllMeetingsAsync());
        }
    }

    private async void ExportText_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Texto bilingüe (*.txt)|*.txt",
            DefaultExt = ".txt",
            FileName = $"VoiceTraductor-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ExecuteUiActionAsync(
                () => _viewModel.ExportSelectedTextAsync(dialog.FileName));
        }
    }

    private async void ExportVtt_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Subtítulos WebVTT (*.vtt)|*.vtt",
            DefaultExt = ".vtt",
            FileName = $"VoiceTraductor-{DateTime.Now:yyyyMMdd-HHmm}.vtt"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ExecuteUiActionAsync(
                () => _viewModel.ExportSelectedWebVttAsync(dialog.FileName));
        }
    }

    private async Task<bool> ExecuteUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            return false;
        }
    }

    private void ShowError(string message) =>
        MessageBox.Show(
            this,
            message,
            "VoiceTraductor",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}
