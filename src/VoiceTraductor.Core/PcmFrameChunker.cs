namespace VoiceTraductor.Core;

public sealed class PcmFrameChunker
{
    public const int SampleRate = 24_000;
    public const int BytesPerSample = 2;
    public const int FrameDurationMilliseconds = 200;
    public const int FrameSizeBytes =
        SampleRate * BytesPerSample * FrameDurationMilliseconds / 1_000;

    private readonly byte[] _buffer = new byte[FrameSizeBytes];
    private int _buffered;

    public event EventHandler<ReadOnlyMemory<byte>>? FrameReady;

    public void Add(ReadOnlySpan<byte> pcm16)
    {
        while (!pcm16.IsEmpty)
        {
            var copyLength = Math.Min(FrameSizeBytes - _buffered, pcm16.Length);
            pcm16[..copyLength].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += copyLength;
            pcm16 = pcm16[copyLength..];

            if (_buffered != FrameSizeBytes)
            {
                continue;
            }

            FrameReady?.Invoke(this, _buffer.ToArray());
            _buffered = 0;
        }
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        _buffered = 0;
    }
}
