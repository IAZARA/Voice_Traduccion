using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;
using VoiceTraductor.Core;

namespace VoiceTraductor.Infrastructure.Audio;

public sealed class NAudioEndpointService : IAudioEndpointService
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public IReadOnlyList<AudioEndpoint> GetCaptureEndpoints() =>
        Enumerate(DataFlow.Capture, AudioEndpointDirection.Capture);

    public IReadOnlyList<AudioEndpoint> GetRenderEndpoints() =>
        Enumerate(DataFlow.Render, AudioEndpointDirection.Render);

    public IAudioCapture CreatePcm24KhzCapture(string endpointId)
    {
        var device = _enumerator.GetDevice(endpointId);
        return new WasapiPcmCapture(device);
    }

    public IAudioPlayback CreatePcm24KhzPlayback(
        string endpointId,
        TimeSpan maximumBuffer)
    {
        var device = _enumerator.GetDevice(endpointId);
        return new WasapiPcmPlayback(device, maximumBuffer);
    }

    public bool HasVoiceMeeterRoutes()
    {
        var captures = GetCaptureEndpoints();
        var renders = GetRenderEndpoints();
        return captures.Any(
                   endpoint => endpoint.Name.Contains(
                       "VoiceMeeter Output",
                       StringComparison.OrdinalIgnoreCase)) &&
               captures.Any(
                   endpoint => endpoint.Name.Contains(
                       "VoiceMeeter Aux Output",
                       StringComparison.OrdinalIgnoreCase)) &&
               renders.Any(
                   endpoint => endpoint.Name.Contains(
                       "VoiceMeeter Input",
                       StringComparison.OrdinalIgnoreCase)) &&
               renders.Any(
                   endpoint => endpoint.Name.Contains(
                       "VoiceMeeter Aux Input",
                       StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _enumerator.Dispose();

    private IReadOnlyList<AudioEndpoint> Enumerate(
        DataFlow flow,
        AudioEndpointDirection direction)
    {
        var defaultId = TryGetDefaultDeviceId(flow);
        return _enumerator
            .EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(
                device => new AudioEndpoint(
                    device.ID,
                    device.FriendlyName,
                    direction,
                    string.Equals(device.ID, defaultId, StringComparison.Ordinal)))
            .OrderByDescending(endpoint => endpoint.IsDefault)
            .ThenBy(endpoint => endpoint.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private string? TryGetDefaultDeviceId(DataFlow flow)
    {
        try
        {
            return _enumerator.GetDefaultAudioEndpoint(flow, Role.Communications).ID;
        }
        catch (COMException)
        {
            return null;
        }
    }
}

internal sealed class WasapiPcmCapture : IAudioCapture
{
    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _sourceBuffer;
    private readonly IWaveProvider _pcmProvider;
    private readonly byte[] _readBuffer = new byte[PcmFrameChunker.FrameSizeBytes * 2];
    private bool _disposed;

    public WasapiPcmCapture(MMDevice device)
    {
        _capture = new WasapiCapture(device, true, 100);
        _sourceBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };

        ISampleProvider samples = _sourceBuffer.ToSampleProvider();
        samples = samples.WaveFormat.Channels switch
        {
            1 => samples,
            2 => new StereoToMonoSampleProvider(samples),
            _ => new DownmixToMonoSampleProvider(samples)
        };
        samples = new WdlResamplingSampleProvider(samples, PcmFrameChunker.SampleRate);
        _pcmProvider = new SampleToWaveProvider16(samples);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public event EventHandler<ReadOnlyMemory<byte>>? FrameReady;
    public event EventHandler<float>? LevelChanged;
    public event EventHandler<Exception>? Faulted;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _capture.StartRecording();
    }

    public void Stop()
    {
        if (!_disposed && _capture.CaptureState != CaptureState.Stopped)
        {
            _capture.StopRecording();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        try
        {
            _sourceBuffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
            while (_sourceBuffer.BufferedBytes > 0)
            {
                var read = _pcmProvider.Read(_readBuffer, 0, _readBuffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var frame = _readBuffer.AsMemory(0, read).ToArray();
                FrameReady?.Invoke(this, frame);
                LevelChanged?.Invoke(this, CalculateLevel(frame));
            }
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(this, exception);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
        {
            Faulted?.Invoke(this, args.Exception);
        }
    }

    private static float CalculateLevel(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length < 2)
        {
            return 0;
        }

        double sum = 0;
        var samples = pcm16.Length / 2;
        for (var offset = 0; offset + 1 < pcm16.Length; offset += 2)
        {
            var sample = (short)(pcm16[offset] | (pcm16[offset + 1] << 8));
            var normalized = sample / 32768d;
            sum += normalized * normalized;
        }

        return (float)Math.Clamp(Math.Sqrt(sum / samples), 0, 1);
    }
}

internal sealed class WasapiPcmPlayback : IAudioPlayback
{
    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeWaveProvider16 _volumeProvider;
    private readonly WasapiOut _output;
    private bool _disposed;

    public WasapiPcmPlayback(MMDevice device, TimeSpan maximumBuffer)
    {
        _buffer = new BufferedWaveProvider(
            new WaveFormat(PcmFrameChunker.SampleRate, 16, 1))
        {
            BufferDuration = maximumBuffer,
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _volumeProvider = new VolumeWaveProvider16(_buffer);
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        _output.Init(_volumeProvider);
        _output.Play();
    }

    public float Volume
    {
        get => _volumeProvider.Volume;
        set => _volumeProvider.Volume = Math.Clamp(value, 0f, 1f);
    }

    public void Enqueue(ReadOnlySpan<byte> pcm16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var copy = pcm16.ToArray();
        _buffer.AddSamples(copy, 0, copy.Length);
    }

    public void Clear() => _buffer.ClearBuffer();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _output.Stop();
        _output.Dispose();
    }
}

internal sealed class DownmixToMonoSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly float[] _sourceBuffer = new float[16_384];

    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        var channels = source.WaveFormat.Channels;
        var requested = Math.Min(_sourceBuffer.Length, count * channels);
        requested -= requested % channels;
        var read = source.Read(_sourceBuffer, 0, requested);
        var outputSamples = read / channels;
        for (var sampleIndex = 0; sampleIndex < outputSamples; sampleIndex++)
        {
            double sum = 0;
            var baseIndex = sampleIndex * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += _sourceBuffer[baseIndex + channel];
            }

            buffer[offset + sampleIndex] = (float)(sum / channels);
        }

        return outputSamples;
    }
}
