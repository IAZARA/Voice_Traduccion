using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using VoiceTraductor.Core;

namespace VoiceTraductor.Infrastructure.Realtime;

public sealed class RealtimeTranslationStream(TranslationDirection direction)
    : ITranslationStream
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];

    private readonly Channel<byte[]> _outgoingAudio =
        Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(10)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _socketGate = new();
    private CancellationTokenSource? _lifetimeCts;
    private ClientWebSocket? _socket;
    private Task? _lifecycleTask;
    private TaskCompletionSource _initialReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TranslationStreamOptions? _options;
    private volatile bool _stopping;

    public TranslationDirection Direction { get; } = direction;
    public TranslationSessionState State { get; private set; } = TranslationSessionState.Idle;

    public event EventHandler<TranslationSessionState>? StateChanged;
    public event EventHandler<AudioDelta>? AudioReceived;
    public event EventHandler<TranscriptDelta>? TranscriptReceived;
    public event EventHandler<TranslationFault>? Faulted;

    public async Task ConnectAsync(
        TranslationStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_lifecycleTask is not null)
        {
            throw new InvalidOperationException("La sesión ya fue iniciada.");
        }

        _options = options;
        _stopping = false;
        _initialReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetState(TranslationSessionState.Connecting);
        _lifecycleTask = RunLifecycleAsync(_lifetimeCts.Token);
        try
        {
            await _initialReady.Task
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            _lifetimeCts.Cancel();
            throw new TranslationException(
                TranslationErrorKind.Network,
                "La API no respondió dentro de 15 segundos.",
                true,
                exception);
        }
    }

    public ValueTask SendAudioAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken = default)
    {
        if (pcm16.Length != PcmFrameChunker.FrameSizeBytes)
        {
            throw new ArgumentException(
                $"Cada bloque debe contener {PcmFrameChunker.FrameSizeBytes} bytes.",
                nameof(pcm16));
        }

        return _outgoingAudio.Writer.WriteAsync(pcm16.ToArray(), cancellationToken);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_lifecycleTask is null)
        {
            SetState(TranslationSessionState.Idle);
            return;
        }

        _stopping = true;
        SetState(TranslationSessionState.Stopping);
        var socket = GetSocket();
        if (socket?.State == WebSocketState.Open)
        {
            try
            {
                await SendTextAsync(
                        socket,
                        RealtimeProtocol.CreateSessionClose(),
                        cancellationToken)
                    .ConfigureAwait(false);
                await _closed.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A bounded close prevents shutdown from hanging on a broken network.
            }
            catch (WebSocketException)
            {
                // The transport is already unavailable; cancellation below completes cleanup.
            }
        }

        _lifetimeCts?.Cancel();
        try
        {
            await _lifecycleTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            DisposeSocket();
            _lifecycleTask = null;
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            SetState(TranslationSessionState.Idle);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }

    private async Task RunLifecycleAsync(CancellationToken cancellationToken)
    {
        var reconnectAttempt = 0;
        var connectedOnce = false;
        while (!cancellationToken.IsCancellationRequested && !_stopping)
        {
            try
            {
                if (connectedOnce)
                {
                    SetState(TranslationSessionState.Reconnecting);
                }

                var socket = await ConnectSocketAsync(cancellationToken).ConfigureAwait(false);
                connectedOnce = true;
                reconnectAttempt = 0;
                SetState(TranslationSessionState.Ready);
                _initialReady.TrySetResult();

                using var connectionCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var receiveTask = ReceiveLoopAsync(socket, connectionCts.Token);
                var sendTask = SendLoopAsync(socket, connectionCts.Token);
                var completed = await Task.WhenAny(receiveTask, sendTask).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                connectionCts.Cancel();
                await IgnoreCancellationAsync(receiveTask).ConfigureAwait(false);
                await IgnoreCancellationAsync(sendTask).ConfigureAwait(false);

                if (!_stopping && !cancellationToken.IsCancellationRequested)
                {
                    throw new WebSocketException("La conexión de traducción se cerró.");
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || _stopping)
            {
                break;
            }
            catch (TranslationException exception) when (!exception.IsRecoverable)
            {
                var fault = new TranslationFault(
                    Direction,
                    exception.Kind,
                    exception.Message,
                    false,
                    exception);
                Faulted?.Invoke(this, fault);
                SetState(TranslationSessionState.Faulted);
                _initialReady.TrySetException(exception);
                break;
            }
            catch (Exception exception) when (!_stopping)
            {
                var kind = RealtimeProtocol.ClassifyError(null, null, exception.Message);
                if (kind == TranslationErrorKind.Service &&
                    exception is WebSocketException or HttpRequestException)
                {
                    kind = TranslationErrorKind.Network;
                }

                if (kind is TranslationErrorKind.Authentication or
                    TranslationErrorKind.RateLimit or
                    TranslationErrorKind.Protocol)
                {
                    var fatal = new TranslationException(
                        kind,
                        exception.Message,
                        false,
                        exception);
                    Faulted?.Invoke(
                        this,
                        new TranslationFault(
                            Direction,
                            kind,
                            fatal.Message,
                            false,
                            fatal));
                    SetState(TranslationSessionState.Faulted);
                    _initialReady.TrySetException(fatal);
                    break;
                }

                Faulted?.Invoke(
                    this,
                    new TranslationFault(
                        Direction,
                        kind,
                        "Se perdió la conexión; se intentará restablecer.",
                        true,
                        exception));
                DisposeSocket();
                SetState(TranslationSessionState.Reconnecting);
                var delay = ReconnectDelays[Math.Min(reconnectAttempt, ReconnectDelays.Length - 1)];
                reconnectAttempt++;
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _initialReady.TrySetCanceled(cancellationToken);
    }

    private async Task<ClientWebSocket> ConnectSocketAsync(CancellationToken cancellationToken)
    {
        var options = _options ?? throw new InvalidOperationException("Falta configuración.");
        DisposeSocket();
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {options.ApiKey}");
        if (!string.IsNullOrWhiteSpace(options.SafetyIdentifier))
        {
            socket.Options.SetRequestHeader(
                "OpenAI-Safety-Identifier",
                options.SafetyIdentifier);
        }

        var separator = options.Endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var uri = new Uri(
            $"{options.Endpoint}{separator}model={Uri.EscapeDataString(options.Model)}");
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        SetSocket(socket);

        var created = await ReceiveEventAsync(socket, cancellationToken).ConfigureAwait(false);
        if (created.Fault is not null)
        {
            throw ToException(created.Fault);
        }

        if (created.Type != "session.created")
        {
            throw new TranslationException(
                TranslationErrorKind.Protocol,
                $"Se esperaba session.created y se recibió {created.Type}.",
                false);
        }

        await SendTextAsync(
                socket,
                RealtimeProtocol.CreateSessionUpdate(options),
                cancellationToken)
            .ConfigureAwait(false);

        while (true)
        {
            var updated = await ReceiveEventAsync(socket, cancellationToken).ConfigureAwait(false);
            Dispatch(updated);
            if (updated.Fault is not null)
            {
                throw ToException(updated.Fault);
            }

            if (updated.Type == "session.updated")
            {
                return socket;
            }
        }
    }

    private async Task SendLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in _outgoingAudio.Reader.ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            await SendTextAsync(
                    socket,
                    RealtimeProtocol.CreateAudioAppend(frame),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               socket.State == WebSocketState.Open)
        {
            var serverEvent = await ReceiveEventAsync(socket, cancellationToken)
                .ConfigureAwait(false);
            Dispatch(serverEvent);
            if (serverEvent.Fault is { IsRecoverable: false } fault)
            {
                throw ToException(fault);
            }

            if (serverEvent.Type == "session.closed")
            {
                _closed.TrySetResult();
                return;
            }
        }
    }

    private void Dispatch(RealtimeServerEvent serverEvent)
    {
        if (serverEvent.Audio is not null)
        {
            AudioReceived?.Invoke(this, serverEvent.Audio);
        }

        if (serverEvent.Transcript is not null)
        {
            TranscriptReceived?.Invoke(this, serverEvent.Transcript);
        }

        if (serverEvent.Fault is not null)
        {
            Faulted?.Invoke(this, serverEvent.Fault);
        }
    }

    private async Task<RealtimeServerEvent> ReceiveEventAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (result.CloseStatus is WebSocketCloseStatus.PolicyViolation)
                {
                    throw new TranslationException(
                        TranslationErrorKind.Authentication,
                        result.CloseStatusDescription ?? "La API rechazó la credencial.",
                        false);
                }

                throw new WebSocketException(
                    result.CloseStatusDescription ?? "El servidor cerró la conexión.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return RealtimeProtocol.ParseServerEvent(
            Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length)),
            Direction);
    }

    private async Task SendTextAsync(
        ClientWebSocket socket,
        string payload,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void SetState(TranslationSessionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void SetSocket(ClientWebSocket socket)
    {
        lock (_socketGate)
        {
            _socket = socket;
        }
    }

    private ClientWebSocket? GetSocket()
    {
        lock (_socketGate)
        {
            return _socket;
        }
    }

    private void DisposeSocket()
    {
        lock (_socketGate)
        {
            _socket?.Dispose();
            _socket = null;
        }
    }

    private static TranslationException ToException(TranslationFault fault) =>
        new(fault.Kind, fault.Message, fault.IsRecoverable, fault.Exception);

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
