using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.Sessions;

namespace StormATC.Infrastructure.Persistence;

/// <summary>
/// Stores sessions, conversation, decisions, clearances, and handoffs in a local SQLite file.
/// </summary>
/// <remarks>
/// <para>
/// Raw SQL rather than an ORM: the schema is eight small tables that never join in complicated
/// ways, and explicit SQL makes it obvious what is stored — which matters for a privacy claim
/// that has to be verifiable by reading the code.
/// </para>
/// <para>
/// <b>Nothing secret is stored here.</b> No API keys, no authorization headers, no raw audio.
/// Transcripts are stored because they are a core feature, and can be disabled entirely with
/// <see cref="PersistenceOptions.StoreTranscripts"/> and erased with
/// <see cref="ClearAllHistoryAsync"/>.
/// </para>
/// </remarks>
public sealed class SqliteSessionRepository(
    IStoragePaths storagePaths,
    IOptions<PersistenceOptions> persistenceOptions,
    ILogger<SqliteSessionRepository> logger) : ISessionRepository, IDisposable
{
    /// <summary>
    /// Current schema version. Bump when the schema changes and add a matching upgrade step in
    /// <see cref="ApplyMigrationsAsync"/>.
    /// </summary>
    private const int CurrentSchemaVersion = 1;

    private readonly IStoragePaths _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    private readonly PersistenceOptions _options = persistenceOptions?.Value
        ?? throw new ArgumentNullException(nameof(persistenceOptions));

    private readonly ILogger<SqliteSessionRepository> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _storagePaths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
    }.ToString();

    /// <inheritdoc />
    public void Dispose() => _initializationLock.Dispose();

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            _storagePaths.EnsureCreated();

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);

            if (_options.AutomaticRetentionSweep)
            {
                await SweepRetentionAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            _logger.LogInformation("Local database ready at {Path}.", _storagePaths.DatabasePath);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveSessionAsync(AtcSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (
                session_id, started_at, completed_at, mode, callsign, departure_icao,
                destination_icao, flight_phase, current_controller, current_frequency,
                aircraft_type)
            VALUES (
                $id, $startedAt, $completedAt, $mode, $callsign, $departure,
                $destination, $phase, $controller, $frequency, $aircraftType)
            ON CONFLICT(session_id) DO UPDATE SET
                completed_at = excluded.completed_at,
                flight_phase = excluded.flight_phase,
                current_controller = excluded.current_controller,
                current_frequency = excluded.current_frequency;
            """;

        command.Parameters.AddWithValue("$id", session.SessionId.ToString());
        command.Parameters.AddWithValue("$startedAt", ToIso(session.StartedAt));
        command.Parameters.AddWithValue("$completedAt", ToNullableIso(session.CompletedAt));
        command.Parameters.AddWithValue("$mode", session.Mode.ToString());
        command.Parameters.AddWithValue("$callsign", (object?)session.Callsign?.Normalized ?? DBNull.Value);
        command.Parameters.AddWithValue("$departure", (object?)session.FlightPlan?.DepartureIcao ?? DBNull.Value);
        command.Parameters.AddWithValue("$destination", (object?)session.FlightPlan?.DestinationIcao ?? DBNull.Value);
        command.Parameters.AddWithValue("$phase", session.Phase.ToString());
        command.Parameters.AddWithValue("$controller", session.CurrentController.ToString());
        command.Parameters.AddWithValue("$frequency", (object?)session.CurrentFrequency?.ToDisplay() ?? DBNull.Value);
        command.Parameters.AddWithValue("$aircraftType", (object?)session.FlightPlan?.AircraftIcaoType ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveConversationTurnAsync(
        Guid sessionId,
        ConversationTurn turn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);

        // The privacy setting is enforced at the point of writing, not at the point of reading,
        // so turning it off means the data never reaches disk at all.
        if (!_options.StoreTranscripts)
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO conversation_turns (
                turn_id, session_id, timestamp, speaker, controller, text,
                normalized_text, intent, correlation_id, used_deterministic_fallback, warnings)
            VALUES (
                $turnId, $sessionId, $timestamp, $speaker, $controller, $text,
                $normalized, $intent, $correlationId, $fallback, $warnings);
            """;

        command.Parameters.AddWithValue("$turnId", turn.TurnId.ToString());
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$timestamp", ToIso(turn.Timestamp));
        command.Parameters.AddWithValue("$speaker", turn.Speaker.ToString());
        command.Parameters.AddWithValue("$controller", turn.Controller.ToString());
        command.Parameters.AddWithValue("$text", turn.Text);
        command.Parameters.AddWithValue("$normalized", (object?)turn.NormalizedText ?? DBNull.Value);
        command.Parameters.AddWithValue("$intent", (object?)turn.Intent?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$correlationId", (object?)turn.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fallback", turn.UsedDeterministicFallback ? 1 : 0);
        command.Parameters.AddWithValue(
            "$warnings",
            turn.Warnings.Count > 0 ? string.Join(" | ", turn.Warnings) : DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveDecisionAsync(
        Guid sessionId,
        PersistedDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO decisions (
                decision_id, session_id, transmission_id, timestamp, accepted, response_type,
                rejection_reason, readback_outcome, correlation_id,
                used_deterministic_fallback, validation_failure_reason)
            VALUES (
                $decisionId, $sessionId, $transmissionId, $timestamp, $accepted, $responseType,
                $rejectionReason, $readbackOutcome, $correlationId, $fallback, $validationFailure);
            """;

        command.Parameters.AddWithValue("$decisionId", decision.DecisionId.ToString());
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$transmissionId", decision.TransmissionId.ToString());
        command.Parameters.AddWithValue("$timestamp", ToIso(decision.Timestamp));
        command.Parameters.AddWithValue("$accepted", decision.Accepted ? 1 : 0);
        command.Parameters.AddWithValue("$responseType", decision.ResponseType);
        command.Parameters.AddWithValue("$rejectionReason", (object?)decision.RejectionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$readbackOutcome", (object?)decision.ReadbackOutcome ?? DBNull.Value);
        command.Parameters.AddWithValue("$correlationId", decision.CorrelationId);
        command.Parameters.AddWithValue("$fallback", decision.UsedDeterministicFallback ? 1 : 0);
        command.Parameters.AddWithValue(
            "$validationFailure", (object?)decision.ValidationFailureReason ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveClearanceAsync(
        Guid sessionId,
        Clearance clearance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clearance);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO clearances (
                clearance_id, session_id, clearance_type, issued_by, issued_at, acknowledged_at,
                runway, initial_altitude_feet, squawk, next_frequency, hold_short_runway,
                taxi_route, destination, approach_name)
            VALUES (
                $clearanceId, $sessionId, $type, $issuedBy, $issuedAt, $acknowledgedAt,
                $runway, $altitude, $squawk, $frequency, $holdShort,
                $taxiRoute, $destination, $approach);
            """;

        command.Parameters.AddWithValue("$clearanceId", clearance.ClearanceId.ToString());
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$type", clearance.Type.ToString());
        command.Parameters.AddWithValue("$issuedBy", clearance.IssuedBy.ToString());
        command.Parameters.AddWithValue("$issuedAt", ToIso(clearance.IssuedAt));
        command.Parameters.AddWithValue("$acknowledgedAt", ToNullableIso(clearance.AcknowledgedAt));
        command.Parameters.AddWithValue("$runway", (object?)clearance.Runway?.ToDisplay() ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$altitude",
            clearance.InitialAltitude is { } altitude ? altitude.Feet : DBNull.Value);
        command.Parameters.AddWithValue("$squawk", (object?)clearance.Squawk?.Digits ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$frequency", (object?)clearance.NextFrequency?.ToDisplay() ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$holdShort", (object?)clearance.HoldShortOfRunway?.ToDisplay() ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$taxiRoute",
            clearance.TaxiRoute.Count > 0 ? string.Join(",", clearance.TaxiRoute) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$destination", (object?)clearance.ClearedToDestination ?? DBNull.Value);
        command.Parameters.AddWithValue("$approach", (object?)clearance.ApproachName ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveHandoffAsync(
        Guid sessionId,
        ControllerHandoff handoff,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO handoffs (
                session_id, from_controller, to_controller, to_frequency, instructed_at, state, reason)
            VALUES ($sessionId, $from, $to, $frequency, $instructedAt, $state, $reason);
            """;

        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$from", handoff.FromController.ToString());
        command.Parameters.AddWithValue("$to", handoff.ToController.ToString());
        command.Parameters.AddWithValue("$frequency", handoff.ToFrequency.ToDisplay());
        command.Parameters.AddWithValue("$instructedAt", ToIso(handoff.InstructedAt));
        command.Parameters.AddWithValue("$state", handoff.State.ToString());
        command.Parameters.AddWithValue("$reason", (object?)handoff.Reason ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveApplicationEventAsync(
        PersistedApplicationEvent applicationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applicationEvent);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO application_events (timestamp, category, message, session_id, correlation_id, severity)
            VALUES ($timestamp, $category, $message, $sessionId, $correlationId, $severity);
            """;

        command.Parameters.AddWithValue("$timestamp", ToIso(applicationEvent.Timestamp));
        command.Parameters.AddWithValue("$category", applicationEvent.Category);
        command.Parameters.AddWithValue("$message", applicationEvent.Message);
        command.Parameters.AddWithValue(
            "$sessionId", (object?)applicationEvent.SessionId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$correlationId", (object?)applicationEvent.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", applicationEvent.Severity.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.session_id, s.started_at, s.completed_at, s.callsign, s.departure_icao,
                   s.destination_icao, s.mode,
                   (SELECT COUNT(*) FROM conversation_turns t WHERE t.session_id = s.session_id)
            FROM sessions s
            ORDER BY s.started_at DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<SessionSummary>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SessionSummary
            {
                SessionId = Guid.Parse(reader.GetString(0)),
                StartedAt = ParseIso(reader.GetString(1)),
                CompletedAt = reader.IsDBNull(2) ? null : ParseIso(reader.GetString(2)),
                Callsign = reader.IsDBNull(3) ? null : reader.GetString(3),
                Departure = reader.IsDBNull(4) ? null : reader.GetString(4),
                Destination = reader.IsDBNull(5) ? null : reader.GetString(5),
                Mode = reader.GetString(6),
                TurnCount = reader.GetInt32(7),
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationTurn>> LoadConversationAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT turn_id, timestamp, speaker, controller, text, normalized_text, intent,
                   correlation_id, used_deterministic_fallback, warnings
            FROM conversation_turns
            WHERE session_id = $sessionId
            ORDER BY timestamp ASC;
            """;

        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        var results = new List<ConversationTurn>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ConversationTurn
            {
                TurnId = Guid.Parse(reader.GetString(0)),
                Timestamp = ParseIso(reader.GetString(1)),
                Speaker = Enum.Parse<ConversationSpeaker>(reader.GetString(2)),
                Controller = Enum.Parse<ControllerPosition>(reader.GetString(3)),
                Text = reader.GetString(4),
                NormalizedText = reader.IsDBNull(5) ? null : reader.GetString(5),
                Intent = reader.IsDBNull(6) ? null : Enum.Parse<PilotIntentType>(reader.GetString(6)),
                CorrelationId = reader.IsDBNull(7) ? null : reader.GetString(7),
                UsedDeterministicFallback = reader.GetInt32(8) != 0,
                Warnings = reader.IsDBNull(9)
                    ? []
                    : reader.GetString(9).Split(" | ", StringSplitOptions.RemoveEmptyEntries),
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task ClearAllHistoryAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (string table in (string[])
                 ["conversation_turns", "decisions", "clearances", "handoffs",
                  "application_events", "flight_plans", "sessions"])
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;

            // Table names are compile-time constants from the array above, never user input.
            command.CommandText = $"DELETE FROM {table};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Reclaim the space so "clear history" actually shrinks the file on disk, which is what
        // a user asking to erase their history expects.
        await using (SqliteCommand vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("All locally stored StormATC history was deleted.");
    }

    /// <inheritdoc />
    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long version ? (int)version : 0;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // WAL keeps readers from blocking the writer, so the transcript page can query while a
        // transmission is being persisted.
        await using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    /// <summary>
    /// Creates or upgrades the schema.
    /// </summary>
    /// <remarks>
    /// Versioning uses SQLite's built-in <c>user_version</c> pragma. Each future schema change
    /// adds a numbered block here; the create statements stay idempotent so a fresh database and
    /// an upgraded one converge on the same shape.
    /// </remarks>
    private async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        int version;
        await using (SqliteCommand read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            object? result = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            version = result is long value ? (int)value : 0;
        }

        if (version >= CurrentSchemaVersion)
        {
            return;
        }

        _logger.LogInformation(
            "Upgrading the local database schema from version {From} to {To}.",
            version,
            CurrentSchemaVersion);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                session_id          TEXT PRIMARY KEY,
                started_at          TEXT NOT NULL,
                completed_at        TEXT,
                mode                TEXT NOT NULL,
                callsign            TEXT,
                departure_icao      TEXT,
                destination_icao    TEXT,
                flight_phase        TEXT,
                current_controller  TEXT,
                current_frequency   TEXT,
                aircraft_type       TEXT
            );

            CREATE TABLE IF NOT EXISTS conversation_turns (
                turn_id                     TEXT PRIMARY KEY,
                session_id                  TEXT NOT NULL,
                timestamp                   TEXT NOT NULL,
                speaker                     TEXT NOT NULL,
                controller                  TEXT,
                text                        TEXT NOT NULL,
                normalized_text             TEXT,
                intent                      TEXT,
                correlation_id              TEXT,
                used_deterministic_fallback INTEGER NOT NULL DEFAULT 0,
                warnings                    TEXT,
                FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_turns_session_time
                ON conversation_turns(session_id, timestamp);

            CREATE TABLE IF NOT EXISTS decisions (
                decision_id                 TEXT PRIMARY KEY,
                session_id                  TEXT NOT NULL,
                transmission_id             TEXT NOT NULL,
                timestamp                   TEXT NOT NULL,
                accepted                    INTEGER NOT NULL,
                response_type               TEXT NOT NULL,
                rejection_reason            TEXT,
                readback_outcome            TEXT,
                correlation_id              TEXT NOT NULL,
                used_deterministic_fallback INTEGER NOT NULL DEFAULT 0,
                validation_failure_reason   TEXT,
                FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_decisions_session ON decisions(session_id, timestamp);

            CREATE TABLE IF NOT EXISTS clearances (
                clearance_id          TEXT PRIMARY KEY,
                session_id            TEXT NOT NULL,
                clearance_type        TEXT NOT NULL,
                issued_by             TEXT NOT NULL,
                issued_at             TEXT NOT NULL,
                acknowledged_at       TEXT,
                runway                TEXT,
                initial_altitude_feet INTEGER,
                squawk                TEXT,
                next_frequency        TEXT,
                hold_short_runway     TEXT,
                taxi_route            TEXT,
                destination           TEXT,
                approach_name         TEXT,
                FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS handoffs (
                handoff_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id      TEXT NOT NULL,
                from_controller TEXT NOT NULL,
                to_controller   TEXT NOT NULL,
                to_frequency    TEXT NOT NULL,
                instructed_at   TEXT NOT NULL,
                state           TEXT NOT NULL,
                reason          TEXT,
                FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS flight_plans (
                flight_plan_id   TEXT PRIMARY KEY,
                session_id       TEXT,
                source           TEXT NOT NULL,
                callsign         TEXT,
                departure_icao   TEXT,
                destination_icao TEXT,
                route            TEXT,
                cruise_altitude  INTEGER,
                generated_at     TEXT,
                imported_at      TEXT NOT NULL,
                raw_cache_path   TEXT
            );

            CREATE TABLE IF NOT EXISTS application_events (
                event_id       INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp      TEXT NOT NULL,
                category       TEXT NOT NULL,
                message        TEXT NOT NULL,
                session_id     TEXT,
                correlation_id TEXT,
                severity       TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_events_time ON application_events(timestamp);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand setVersion = connection.CreateCommand();

        // PRAGMA user_version does not accept a parameter, so the value is interpolated. It is
        // a compile-time constant, never user input.
        setVersion.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
        await setVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SweepRetentionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _options.RetentionPeriod;

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE started_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", ToIso(cutoff));

        int removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (removed > 0)
        {
            _logger.LogInformation(
                "Retention sweep removed {Count} session(s) older than {Days} days.",
                removed,
                _options.RetentionPeriod.TotalDays);
        }
    }

    // Timestamps are stored as ISO-8601 round-trip strings: sortable as text, unambiguous about
    // offset, and readable when inspecting the database by hand.
    private static string ToIso(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static object ToNullableIso(DateTimeOffset? value) =>
        value is DateTimeOffset instant ? ToIso(instant) : DBNull.Value;

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
