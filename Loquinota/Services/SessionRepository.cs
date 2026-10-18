using System.Globalization;
using Loquinota.Helpers;
using Loquinota.Models;
using Microsoft.Data.Sqlite;

namespace Loquinota.Services;

/// <summary>
/// SQLite persistence for sessions (Microsoft.Data.Sqlite).
///
/// Contract:
///  - Database at <see cref="DatabasePath"/> (default AppStorage.DatabasePath).
///  - Schema: Sessions(Id TEXT PK, Title, CreatedAtUtc TEXT, DurationTicks INTEGER,
///    SourceType INTEGER, AudioFilePath, TranscriptText, NotesText, SourceFileName,
///    ModelName, Language).
///  - <see cref="InitializeAsync"/> is idempotent and creates the schema.
///  - GetAllAsync returns newest first.
///  - Audio files live under <see cref="SessionsDirectory"/> as {sessionId}.wav.
///  - Must be safe to call from multiple pages; use a connection per operation
///    (Microsoft.Data.Sqlite is not thread-safe across a shared connection).
/// </summary>
public sealed class SessionRepository
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS Sessions (
            Id             TEXT    NOT NULL PRIMARY KEY,
            Title          TEXT    NOT NULL DEFAULT '',
            CreatedAtUtc   TEXT    NOT NULL,
            DurationTicks  INTEGER NOT NULL DEFAULT 0,
            SourceType     INTEGER NOT NULL DEFAULT 0,
            AudioFilePath  TEXT    NOT NULL DEFAULT '',
            TranscriptText TEXT    NOT NULL DEFAULT '',
            NotesText      TEXT    NOT NULL DEFAULT '',
            SourceFileName TEXT    NULL,
            ModelName      TEXT    NULL,
            Language       TEXT    NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Sessions_CreatedAtUtc ON Sessions (CreatedAtUtc DESC);
        """;

    private const string SelectColumns =
        "Id, Title, CreatedAtUtc, DurationTicks, SourceType, AudioFilePath, " +
        "TranscriptText, NotesText, SourceFileName, ModelName, Language";

    private const string UpsertSql = """
        INSERT INTO Sessions
            (Id, Title, CreatedAtUtc, DurationTicks, SourceType, AudioFilePath,
             TranscriptText, NotesText, SourceFileName, ModelName, Language)
        VALUES
            ($id, $title, $createdAtUtc, $durationTicks, $sourceType, $audioFilePath,
             $transcriptText, $notesText, $sourceFileName, $modelName, $language)
        ON CONFLICT(Id) DO UPDATE SET
            Title          = excluded.Title,
            CreatedAtUtc   = excluded.CreatedAtUtc,
            DurationTicks  = excluded.DurationTicks,
            SourceType     = excluded.SourceType,
            AudioFilePath  = excluded.AudioFilePath,
            TranscriptText = excluded.TranscriptText,
            NotesText      = excluded.NotesText,
            SourceFileName = excluded.SourceFileName,
            ModelName      = excluded.ModelName,
            Language       = excluded.Language;
        """;

    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private volatile bool _initialized;

    public SessionRepository(string? databasePath = null)
    {
        var path = string.IsNullOrWhiteSpace(databasePath) ? AppStorage.DatabasePath : databasePath!;
        DatabasePath = Path.GetFullPath(path);

        // The audio folder always sits next to the database, which for the default
        // production path is exactly AppStorage.SessionsDirectory.
        var databaseDirectory = Path.GetDirectoryName(DatabasePath) ?? AppStorage.RootDirectory;
        SessionsDirectory = Path.Combine(databaseDirectory, "Sessions");
    }

    public string DatabasePath { get; private set; } = "";

    public string SessionsDirectory { get; private set; } = "";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"SessionRepository.InitializeAsync failed for '{DatabasePath}'.", ex);
            throw;
        }
    }

    public async Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

            await using var connection = CreateConnection();
            await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {SelectColumns} FROM Sessions ORDER BY CreatedAtUtc DESC, rowid DESC;";

            var sessions = new List<Session>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sessions.Add(ReadSession(reader));
            }

            return sessions;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("SessionRepository.GetAllAsync failed.", ex);
            throw;
        }
    }

    public async Task<Session?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

            await using var connection = CreateConnection();
            await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM Sessions WHERE Id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadSession(reader)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"SessionRepository.GetAsync failed for id '{id}'.", ex);
            throw;
        }
    }

    /// <summary>Inserts or updates a session by Id.</summary>
    public async Task UpsertAsync(Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

            await using var connection = CreateConnection();
            await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = UpsertSql;
            command.Parameters.AddWithValue("$id", session.Id ?? "");
            command.Parameters.AddWithValue("$title", session.Title ?? "");
            command.Parameters.AddWithValue("$createdAtUtc", FormatCreatedAt(session.CreatedAt));
            command.Parameters.AddWithValue("$durationTicks", session.Duration.Ticks);
            command.Parameters.AddWithValue("$sourceType", (int)session.SourceType);
            command.Parameters.AddWithValue("$audioFilePath", session.AudioFilePath ?? "");
            command.Parameters.AddWithValue("$transcriptText", session.TranscriptText ?? "");
            command.Parameters.AddWithValue("$notesText", session.NotesText ?? "");
            command.Parameters.AddWithValue("$sourceFileName", (object?)session.SourceFileName ?? DBNull.Value);
            command.Parameters.AddWithValue("$modelName", (object?)session.ModelName ?? DBNull.Value);
            command.Parameters.AddWithValue("$language", (object?)session.Language ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"SessionRepository.UpsertAsync failed for id '{session.Id}'.", ex);
            throw;
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        string? recordedAudioPath = null;
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

            await using var connection = CreateConnection();
            await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT AudioFilePath FROM Sessions WHERE Id = $id LIMIT 1;";
                select.Parameters.AddWithValue("$id", id);
                var value = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is string { Length: > 0 } path)
                {
                    recordedAudioPath = path;
                }
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM Sessions WHERE Id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"SessionRepository.DeleteAsync failed for id '{id}'.", ex);
            throw;
        }

        // Best effort: losing the row must not fail because a file is locked.
        TryDeleteAudioFile(GetAudioFilePath(id));
        if (!string.IsNullOrWhiteSpace(recordedAudioPath)
            && !string.Equals(recordedAudioPath, GetAudioFilePath(id), StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteAudioFile(recordedAudioPath);
        }
    }

    /// <summary>Returns SessionsDirectory\{sessionId}.wav (file may not exist yet).</summary>
    public string GetAudioFilePath(string sessionId)
        => Path.Combine(SessionsDirectory, $"{sessionId}.wav");

    /// <summary>Copies an existing WAV into the sessions directory and returns its path.</summary>
    public async Task<string> ImportAudioAsync(
        string sessionId,
        string sourceWavPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session id is required to import audio.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(sourceWavPath))
        {
            throw new ArgumentException("A source WAV path is required to import audio.", nameof(sourceWavPath));
        }

        var sourcePath = Path.GetFullPath(sourceWavPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The WAV file to import does not exist.", sourcePath);
        }

        var targetPath = GetAudioFilePath(sessionId);
        var temporaryPath = $"{targetPath}.tmp-{Guid.NewGuid():N}";

        try
        {
            Directory.CreateDirectory(SessionsDirectory);

            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            // Replace atomically so a crash can never leave a half-written WAV in place.
            File.Move(temporaryPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFile(temporaryPath);
            Log.Error($"SessionRepository.ImportAudioAsync failed for id '{sessionId}'.", ex);
            throw;
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? AppStorage.RootDirectory);
            Directory.CreateDirectory(SessionsDirectory);

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // busy timeout keeps concurrent writers from failing while WAL lets readers proceed.
            await using (var configure = connection.CreateCommand())
            {
                configure.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=8000;";
                await configure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = SchemaSql;
                await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            Log.Info($"Session repository ready at '{DatabasePath}'.");
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        };

        return new SqliteConnection(builder.ToString());
    }

    private static async Task OpenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var configure = connection.CreateCommand();
        configure.CommandText = "PRAGMA busy_timeout=8000; PRAGMA synchronous=NORMAL;";
        await configure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Session ReadSession(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Title = reader.GetString(1),
        CreatedAt = ParseCreatedAt(reader.GetString(2)),
        Duration = TimeSpan.FromTicks(reader.GetInt64(3)),
        SourceType = (SessionSourceType)reader.GetInt32(4),
        AudioFilePath = reader.GetString(5),
        TranscriptText = reader.GetString(6),
        NotesText = reader.GetString(7),
        SourceFileName = reader.IsDBNull(8) ? null : reader.GetString(8),
        ModelName = reader.IsDBNull(9) ? null : reader.GetString(9),
        Language = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private static string FormatCreatedAt(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseCreatedAt(string value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static void TryDeleteAudioFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete audio file '{path}'.", ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary file cleanup is best effort.
        }
    }
}
