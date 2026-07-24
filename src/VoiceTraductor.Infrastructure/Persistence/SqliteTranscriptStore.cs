using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using VoiceTraductor.Core;
using VoiceTraductor.Infrastructure.Security;

namespace VoiceTraductor.Infrastructure.Persistence;

public sealed class SqliteTranscriptStore : ITranscriptStore
{
    private readonly string _connectionString;
    private readonly DpapiProtector _protector;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);

    public SqliteTranscriptStore(
        string applicationDataDirectory,
        DpapiProtector? protector = null)
    {
        Directory.CreateDirectory(applicationDataDirectory);
        var databasePath = Path.Combine(applicationDataDirectory, "meetings.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _protector = protector ?? new DpapiProtector();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteLockedAsync(
                async connection =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        PRAGMA journal_mode = WAL;
                        PRAGMA foreign_keys = ON;

                        CREATE TABLE IF NOT EXISTS meetings (
                            id TEXT PRIMARY KEY,
                            started_at TEXT NOT NULL,
                            ended_at TEXT NULL,
                            status INTEGER NOT NULL,
                            source_language TEXT NOT NULL,
                            target_language TEXT NOT NULL
                        );

                        CREATE TABLE IF NOT EXISTS segments (
                            id TEXT PRIMARY KEY,
                            meeting_id TEXT NOT NULL,
                            direction INTEGER NOT NULL,
                            start_ms INTEGER NOT NULL,
                            end_ms INTEGER NOT NULL,
                            source_text BLOB NOT NULL,
                            translated_text BLOB NOT NULL,
                            is_final INTEGER NOT NULL,
                            FOREIGN KEY(meeting_id) REFERENCES meetings(id) ON DELETE CASCADE
                        );

                        CREATE INDEX IF NOT EXISTS ix_segments_meeting_time
                            ON segments(meeting_id, start_ms);

                        UPDATE meetings
                        SET status = $interrupted,
                            ended_at = COALESCE(ended_at, $now)
                        WHERE status = $active;
                        """;
                    command.Parameters.AddWithValue(
                        "$interrupted",
                        (int)MeetingStatus.Interrupted);
                    command.Parameters.AddWithValue("$active", (int)MeetingStatus.Active);
                    command.Parameters.AddWithValue(
                        "$now",
                        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task CreateMeetingAsync(
        MeetingRecord meeting,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO meetings
                        (id, started_at, ended_at, status, source_language, target_language)
                    VALUES
                        ($id, $started, $ended, $status, $source, $target);
                    """;
                command.Parameters.AddWithValue("$id", meeting.Id.ToString("D"));
                command.Parameters.AddWithValue(
                    "$started",
                    meeting.StartedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue(
                    "$ended",
                    meeting.EndedAt?.ToString("O", CultureInfo.InvariantCulture) ??
                    (object)DBNull.Value);
                command.Parameters.AddWithValue("$status", (int)meeting.Status);
                command.Parameters.AddWithValue("$source", meeting.SourceLanguage);
                command.Parameters.AddWithValue("$target", meeting.TargetLanguage);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public Task FinishMeetingAsync(
        Guid meetingId,
        DateTimeOffset endedAt,
        MeetingStatus status,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE meetings
                    SET ended_at = $ended, status = $status
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", meetingId.ToString("D"));
                command.Parameters.AddWithValue(
                    "$ended",
                    endedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$status", (int)status);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public Task AddSegmentAsync(
        CaptionSegment segment,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO segments
                        (id, meeting_id, direction, start_ms, end_ms,
                         source_text, translated_text, is_final)
                    VALUES
                        ($id, $meeting, $direction, $start, $end,
                         $source, $translated, $final);
                    """;
                command.Parameters.AddWithValue("$id", segment.Id.ToString("D"));
                command.Parameters.AddWithValue("$meeting", segment.MeetingId.ToString("D"));
                command.Parameters.AddWithValue("$direction", (int)segment.Direction);
                command.Parameters.AddWithValue("$start", segment.StartMilliseconds);
                command.Parameters.AddWithValue("$end", segment.EndMilliseconds);
                command.Parameters.Add("$source", SqliteType.Blob).Value =
                    _protector.Protect(segment.SourceText);
                command.Parameters.Add("$translated", SqliteType.Blob).Value =
                    _protector.Protect(segment.TranslatedText);
                command.Parameters.AddWithValue("$final", segment.IsFinal ? 1 : 0);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public async Task<IReadOnlyList<MeetingRecord>> GetMeetingsAsync(
        CancellationToken cancellationToken = default)
    {
        var meetings = new List<MeetingRecord>();
        await ExecuteLockedAsync(
                async connection =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT id, started_at, ended_at, status,
                               source_language, target_language
                        FROM meetings
                        ORDER BY started_at DESC;
                        """;
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        meetings.Add(
                            new MeetingRecord(
                                Guid.Parse(reader.GetString(0)),
                                DateTimeOffset.Parse(
                                    reader.GetString(1),
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind),
                                reader.IsDBNull(2)
                                    ? null
                                    : DateTimeOffset.Parse(
                                        reader.GetString(2),
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.RoundtripKind),
                                (MeetingStatus)reader.GetInt32(3),
                                reader.GetString(4),
                                reader.GetString(5)));
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
        return meetings;
    }

    public async Task<IReadOnlyList<CaptionSegment>> GetSegmentsAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        var segments = new List<CaptionSegment>();
        await ExecuteLockedAsync(
                async connection =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT id, direction, start_ms, end_ms,
                               source_text, translated_text, is_final
                        FROM segments
                        WHERE meeting_id = $meeting
                        ORDER BY start_ms, rowid;
                        """;
                    command.Parameters.AddWithValue("$meeting", meetingId.ToString("D"));
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        segments.Add(
                            new CaptionSegment(
                                Guid.Parse(reader.GetString(0)),
                                meetingId,
                                (TranslationDirection)reader.GetInt32(1),
                                reader.GetInt64(2),
                                reader.GetInt64(3),
                                _protector.Unprotect((byte[])reader[4]),
                                _protector.Unprotect((byte[])reader[5]),
                                reader.GetInt32(6) == 1));
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
        return segments;
    }

    public Task DeleteMeetingAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM meetings WHERE id = $id;";
                command.Parameters.AddWithValue("$id", meetingId.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public Task DeleteAllAsync(CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM meetings;";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public async Task ExportTextAsync(
        Guid meetingId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var meeting = (await GetMeetingsAsync(cancellationToken).ConfigureAwait(false))
            .Single(record => record.Id == meetingId);
        var segments = await GetSegmentsAsync(meetingId, cancellationToken)
            .ConfigureAwait(false);
        var builder = new StringBuilder()
            .AppendLine("VoiceTraductor — transcripción bilingüe")
            .AppendLine($"Inicio: {meeting.StartedAt.ToLocalTime():G}")
            .AppendLine($"Estado: {meeting.Status}")
            .AppendLine();

        foreach (var segment in segments)
        {
            builder.AppendLine(
                $"[{FormatTextTime(segment.StartMilliseconds)}] {DirectionLabel(segment.Direction)}");
            builder.AppendLine($"Original: {segment.SourceText}");
            builder.AppendLine($"Traducción: {segment.TranslatedText}");
            builder.AppendLine();
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ExportWebVttAsync(
        Guid meetingId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var segments = await GetSegmentsAsync(meetingId, cancellationToken)
            .ConfigureAwait(false);
        var builder = new StringBuilder("WEBVTT")
            .AppendLine()
            .AppendLine();

        foreach (var segment in segments)
        {
            builder.AppendLine(
                $"{FormatVttTime(segment.StartMilliseconds)} --> " +
                $"{FormatVttTime(Math.Max(segment.EndMilliseconds, segment.StartMilliseconds + 1))}");
            builder.AppendLine($"[{DirectionLabel(segment.Direction)}] {segment.TranslatedText}");
            builder.AppendLine($"Original: {segment.SourceText}");
            builder.AppendLine();
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ExecuteLockedAsync(
        Func<SqliteConnection, Task> action,
        CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await action(connection).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private static string DirectionLabel(TranslationDirection direction) =>
        direction == TranslationDirection.IncomingEnglishToSpanish
            ? "Inglés → Español"
            : "Español → Inglés";

    private static string FormatTextTime(long milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"hh\:mm\:ss");

    private static string FormatVttTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}
