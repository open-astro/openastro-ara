#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using OpenAstroAra.Core.Guiding;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services.Guiding;

public sealed class GuidingAutoTuneRepository {
    private const string Key = "guiding_autotune_latest";
    private const string RestartWarning = "Server restarted during auto-tune; automatic work stopped. Reconnect guider and use rollback.";
    private readonly IAraDatabase _database;

    public GuidingAutoTuneRepository(IAraDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    public GuidingAutoTuneSession? Load() {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_config WHERE key = $key LIMIT 1";
        command.Parameters.AddWithValue("$key", Key);
        var value = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(value)) {
            using var history = connection.CreateCommand();
            history.CommandText = "SELECT session_json FROM guiding_autotune_sessions ORDER BY updated_at_utc DESC LIMIT 1";
            value = history.ExecuteScalar() as string;
        }
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Deserialize(value);
    }

    public GuidingAutoTuneSession? Load(Guid sessionId) {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_json FROM guiding_autotune_sessions WHERE id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        var value = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value) ? null : Deserialize(value);
    }

    /// <summary>
    /// Converts an in-flight session left by a crashed or restarted server into a
    /// recoverable failure. Hardware work never resumes automatically after restart.
    /// </summary>
    public GuidingAutoTuneSession? RecoverInterruptedSession() {
        var session = Load();
        if (session is null || session.State is GuidingAutoTuneState.Idle
            or GuidingAutoTuneState.Proposed or GuidingAutoTuneState.Completed
            or GuidingAutoTuneState.RolledBack or GuidingAutoTuneState.Failed)
            return session;

        var recovered = session with {
            State = GuidingAutoTuneState.Failed,
            Progress = 1,
            CurrentStep = "server restarted; manual rollback available",
            Warnings = session.Warnings.Contains(RestartWarning, StringComparer.Ordinal)
                ? session.Warnings
                : session.Warnings.Concat(new[] { RestartWarning }).ToArray(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        SaveAsync(recovered, CancellationToken.None).GetAwaiter().GetResult();
        return recovered;
    }

    private static GuidingAutoTuneSession? Deserialize(string value) {
        try { return JsonSerializer.Deserialize<GuidingAutoTuneSession>(value); }
        catch (JsonException) { return null; }
    }

    public async Task SaveAsync(GuidingAutoTuneSession session, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(session);
        await using var connection = _database.OpenConnection();
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)
            await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(session);
        await using (var command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_config(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", Key);
            command.Parameters.AddWithValue("$value", json);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var history = connection.CreateCommand()) {
            history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO guiding_autotune_sessions
                    (id, state, started_at_utc, updated_at_utc, session_json, characterization_json)
                VALUES ($id, $state, $started, $updated, $session, $characterization)
                ON CONFLICT(id) DO UPDATE SET
                    state = excluded.state,
                    updated_at_utc = excluded.updated_at_utc,
                    session_json = excluded.session_json,
                    characterization_json = excluded.characterization_json;
                """;
            history.Parameters.AddWithValue("$id", session.Id.ToString("D"));
            history.Parameters.AddWithValue("$state", session.State.ToString());
            history.Parameters.AddWithValue("$started", session.StartedAtUtc.ToString("O"));
            history.Parameters.AddWithValue("$updated", session.UpdatedAtUtc.ToString("O"));
            history.Parameters.AddWithValue("$session", json);
            history.Parameters.AddWithValue("$characterization",
                session.CharacterizationTelemetry is null ? DBNull.Value : JsonSerializer.Serialize(session.CharacterizationTelemetry));
            await history.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveTelemetryWindowAsync(Guid sessionId, string phase,
        GuidingTelemetryWindow window, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(window);
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO guiding_autotune_telemetry_windows
                (session_id, phase, started_at_utc, ended_at_utc, sample_count, telemetry_json)
            VALUES ($session, $phase, $started, $ended, $count, $telemetry)
            ON CONFLICT(session_id, phase) DO UPDATE SET
                started_at_utc = excluded.started_at_utc,
                ended_at_utc = excluded.ended_at_utc,
                sample_count = excluded.sample_count,
                telemetry_json = excluded.telemetry_json;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$phase", phase);
        command.Parameters.AddWithValue("$started", window.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$ended", window.EndedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$count", window.Samples.Count);
        command.Parameters.AddWithValue("$telemetry", JsonSerializer.Serialize(window));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
