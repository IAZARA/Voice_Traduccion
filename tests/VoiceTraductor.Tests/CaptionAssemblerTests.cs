using VoiceTraductor.Core;

namespace VoiceTraductor.Tests;

public sealed class CaptionAssemblerTests
{
    [Fact]
    public void DeltasAreAppendOnlyAndSpacesAreNotInvented()
    {
        var meetingId = Guid.NewGuid();
        var assembler = new CaptionAssembler();
        CaptionSegment? finalized = null;
        assembler.StartMeeting(meetingId);
        assembler.SegmentFinalized += (_, segment) => finalized = segment;
        var now = DateTimeOffset.UtcNow;

        assembler.Append(
            new TranscriptDelta(
                TranslationDirection.IncomingEnglishToSpanish,
                TranscriptKind.Source,
                "Good",
                200,
                now));
        assembler.Append(
            new TranscriptDelta(
                TranslationDirection.IncomingEnglishToSpanish,
                TranscriptKind.Source,
                " morning.",
                400,
                now));
        assembler.Append(
            new TranscriptDelta(
                TranslationDirection.IncomingEnglishToSpanish,
                TranscriptKind.Translation,
                "Buenos",
                400,
                now));
        assembler.Append(
            new TranscriptDelta(
                TranslationDirection.IncomingEnglishToSpanish,
                TranscriptKind.Translation,
                " días.",
                600,
                now));

        Assert.NotNull(finalized);
        Assert.Equal(meetingId, finalized.MeetingId);
        Assert.Equal("Good morning.", finalized.SourceText);
        Assert.Equal("Buenos días.", finalized.TranslatedText);
    }

    [Fact]
    public void FlushPersistsPartialCaption()
    {
        var assembler = new CaptionAssembler();
        CaptionSegment? finalized = null;
        assembler.StartMeeting(Guid.NewGuid());
        assembler.SegmentFinalized += (_, segment) => finalized = segment;
        assembler.Append(
            new TranscriptDelta(
                TranslationDirection.OutgoingSpanishToEnglish,
                TranscriptKind.Source,
                "Necesito ayuda",
                1_000,
                DateTimeOffset.UtcNow));

        assembler.Flush(TranslationDirection.OutgoingSpanishToEnglish);

        Assert.NotNull(finalized);
        Assert.Equal("Necesito ayuda", finalized.SourceText);
        Assert.True(finalized.IsFinal);
    }
}
