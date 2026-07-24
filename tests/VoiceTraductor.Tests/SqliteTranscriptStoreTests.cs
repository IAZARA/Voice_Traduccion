using VoiceTraductor.Core;
using VoiceTraductor.Infrastructure.Persistence;

namespace VoiceTraductor.Tests;

public sealed class SqliteTranscriptStoreTests
{
    [Fact]
    public async Task RoundTripsEncryptedTranscriptAndExportsBothFormats()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "VoiceTraductor.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteTranscriptStore(directory);
            await store.InitializeAsync();
            var meeting = new MeetingRecord(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                null,
                MeetingStatus.Active);
            await store.CreateMeetingAsync(meeting);
            await store.AddSegmentAsync(
                new CaptionSegment(
                    Guid.NewGuid(),
                    meeting.Id,
                    TranslationDirection.IncomingEnglishToSpanish,
                    200,
                    1_400,
                    "The total is fifty dollars.",
                    "El total es cincuenta dólares.",
                    true));
            await store.FinishMeetingAsync(
                meeting.Id,
                DateTimeOffset.UtcNow,
                MeetingStatus.Completed);

            var segment = Assert.Single(await store.GetSegmentsAsync(meeting.Id));
            Assert.Equal("El total es cincuenta dólares.", segment.TranslatedText);

            var textPath = Path.Combine(directory, "meeting.txt");
            var vttPath = Path.Combine(directory, "meeting.vtt");
            await store.ExportTextAsync(meeting.Id, textPath);
            await store.ExportWebVttAsync(meeting.Id, vttPath);

            Assert.Contains("cincuenta dólares", await File.ReadAllTextAsync(textPath));
            Assert.StartsWith("WEBVTT", await File.ReadAllTextAsync(vttPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
