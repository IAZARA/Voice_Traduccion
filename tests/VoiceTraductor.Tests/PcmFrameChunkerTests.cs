using VoiceTraductor.Core;

namespace VoiceTraductor.Tests;

public sealed class PcmFrameChunkerTests
{
    [Fact]
    public void EmitsExactTwoHundredMillisecondFrames()
    {
        var chunker = new PcmFrameChunker();
        var frames = new List<byte[]>();
        chunker.FrameReady += (_, frame) => frames.Add(frame.ToArray());

        chunker.Add(new byte[PcmFrameChunker.FrameSizeBytes + 417]);

        var frame = Assert.Single(frames);
        Assert.Equal(9_600, frame.Length);
    }

    [Fact]
    public void CombinesPartialInputWithoutLosingBytes()
    {
        var chunker = new PcmFrameChunker();
        byte[]? emitted = null;
        chunker.FrameReady += (_, frame) => emitted = frame.ToArray();
        var expected = Enumerable.Range(0, PcmFrameChunker.FrameSizeBytes)
            .Select(index => (byte)(index % 251))
            .ToArray();

        chunker.Add(expected.AsSpan(0, 1_234));
        chunker.Add(expected.AsSpan(1_234));

        Assert.Equal(expected, emitted);
    }

    [Fact]
    public void ResetDiscardsBufferedMicrophoneAudio()
    {
        var chunker = new PcmFrameChunker();
        byte[]? emitted = null;
        chunker.FrameReady += (_, frame) => emitted = frame.ToArray();
        chunker.Add(Enumerable.Repeat((byte)0x7F, 4_800).ToArray());

        chunker.Reset();
        chunker.Add(new byte[PcmFrameChunker.FrameSizeBytes]);

        Assert.NotNull(emitted);
        Assert.All(emitted, value => Assert.Equal(0, value));
    }
}
