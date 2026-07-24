using System.Text;

namespace VoiceTraductor.Core;

public sealed class CaptionAssembler(
    TimeSpan? silenceThreshold = null,
    TimeSpan? maximumSegmentDuration = null) : ICaptionAssembler
{
    private readonly TimeSpan _silenceThreshold = silenceThreshold ?? TimeSpan.FromMilliseconds(800);
    private readonly TimeSpan _maximumSegmentDuration =
        maximumSegmentDuration ?? TimeSpan.FromSeconds(8);
    private readonly Dictionary<TranslationDirection, Buffer> _buffers = new();

    public event EventHandler<CaptionSnapshot>? CaptionChanged;
    public event EventHandler<CaptionSegment>? SegmentFinalized;

    public Guid MeetingId { get; private set; }

    public void StartMeeting(Guid meetingId)
    {
        MeetingId = meetingId;
        _buffers.Clear();
    }

    public void Append(TranscriptDelta delta)
    {
        if (string.IsNullOrEmpty(delta.Text))
        {
            return;
        }

        var buffer = GetBuffer(delta.Direction);
        var elapsed = Math.Max(0, delta.ElapsedMilliseconds ?? buffer.LastElapsedMilliseconds);
        if (!buffer.HasContent)
        {
            buffer.StartMilliseconds = elapsed;
        }

        if (delta.Kind == TranscriptKind.Source)
        {
            buffer.Source.Append(delta.Text);
        }
        else
        {
            buffer.Translation.Append(delta.Text);
        }

        buffer.LastElapsedMilliseconds = Math.Max(buffer.LastElapsedMilliseconds, elapsed);
        buffer.LastDeltaAt = delta.ReceivedAt;

        Publish(buffer, false);

        var reachedMaximum =
            buffer.LastElapsedMilliseconds - buffer.StartMilliseconds >=
            _maximumSegmentDuration.TotalMilliseconds;
        var hasSentenceBoundary =
            delta.Kind == TranscriptKind.Translation &&
            EndsSentence(buffer.Translation);

        if (reachedMaximum || (hasSentenceBoundary && buffer.Source.Length > 0))
        {
            Finalize(delta.Direction);
        }
    }

    public void NotifySilence(TranslationDirection direction, long elapsedMilliseconds)
    {
        var buffer = GetBuffer(direction);
        if (!buffer.HasContent)
        {
            return;
        }

        var elapsedSinceDelta = DateTimeOffset.UtcNow - buffer.LastDeltaAt;
        var streamSilence = elapsedMilliseconds - buffer.LastElapsedMilliseconds;
        if (elapsedSinceDelta >= _silenceThreshold ||
            streamSilence >= _silenceThreshold.TotalMilliseconds)
        {
            buffer.LastElapsedMilliseconds =
                Math.Max(buffer.LastElapsedMilliseconds, elapsedMilliseconds);
            Finalize(direction);
        }
    }

    public void Flush(TranslationDirection direction) => Finalize(direction);

    public void FlushAll()
    {
        foreach (var direction in Enum.GetValues<TranslationDirection>())
        {
            Finalize(direction);
        }
    }

    private Buffer GetBuffer(TranslationDirection direction)
    {
        if (_buffers.TryGetValue(direction, out var buffer))
        {
            return buffer;
        }

        buffer = new Buffer(direction);
        _buffers[direction] = buffer;
        return buffer;
    }

    private void Publish(Buffer buffer, bool isFinal)
    {
        CaptionChanged?.Invoke(
            this,
            new CaptionSnapshot(
                buffer.Direction,
                buffer.Source.ToString(),
                buffer.Translation.ToString(),
                buffer.StartMilliseconds,
                buffer.LastElapsedMilliseconds,
                isFinal));
    }

    private void Finalize(TranslationDirection direction)
    {
        var buffer = GetBuffer(direction);
        if (!buffer.HasContent)
        {
            return;
        }

        var snapshot = new CaptionSnapshot(
            direction,
            buffer.Source.ToString().Trim(),
            buffer.Translation.ToString().Trim(),
            buffer.StartMilliseconds,
            Math.Max(buffer.StartMilliseconds + 1, buffer.LastElapsedMilliseconds),
            true);
        CaptionChanged?.Invoke(this, snapshot);

        SegmentFinalized?.Invoke(
            this,
            new CaptionSegment(
                Guid.NewGuid(),
                MeetingId,
                direction,
                snapshot.StartMilliseconds,
                snapshot.EndMilliseconds,
                snapshot.SourceText,
                snapshot.TranslatedText,
                true));
        buffer.Reset();
    }

    private static bool EndsSentence(StringBuilder text)
    {
        for (var index = text.Length - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                continue;
            }

            return text[index] is '.' or '!' or '?' or '。' or '！' or '？';
        }

        return false;
    }

    private sealed class Buffer(TranslationDirection direction)
    {
        public TranslationDirection Direction { get; } = direction;
        public StringBuilder Source { get; } = new();
        public StringBuilder Translation { get; } = new();
        public long StartMilliseconds { get; set; }
        public long LastElapsedMilliseconds { get; set; }
        public DateTimeOffset LastDeltaAt { get; set; } = DateTimeOffset.UtcNow;
        public bool HasContent => Source.Length > 0 || Translation.Length > 0;

        public void Reset()
        {
            Source.Clear();
            Translation.Clear();
            StartMilliseconds = 0;
            LastElapsedMilliseconds = 0;
            LastDeltaAt = DateTimeOffset.UtcNow;
        }
    }
}
