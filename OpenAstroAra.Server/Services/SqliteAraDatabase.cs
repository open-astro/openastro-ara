#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28 SQLite catalog backed by <c>${profileDir}/openastroara.db</c>.
/// </summary>
public sealed partial class SqliteAraDatabase : IAraDatabase {
    private readonly string _connectionString;
    private readonly ILogger<SqliteAraDatabase> _logger;

    public SqliteAraDatabase(string profileDir, ILogger<SqliteAraDatabase>? logger) {
        Directory.CreateDirectory(profileDir);
        DatabasePath = Path.Combine(profileDir, "openastroara.db");
        // Foreign Keys=True so the connection-string default matches the
        // PRAGMA we set in InitializeAsync; SQLite enforces FK constraints
        // per-connection, not per-database.
        _connectionString = new SqliteConnectionStringBuilder {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
        _logger = logger ?? NullLogger<SqliteAraDatabase>.Instance;
    }

    public string DatabasePath { get; }

    public SqliteConnection OpenConnection() {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task InitializeAsync(CancellationToken ct) {
        await using var conn = OpenConnection();

        // §28.6 PRAGMAs — applied on every connection that participates in
        // catalog work. journal_mode = WAL is *persistent* (saved into the
        // DB file header), so it only needs to be set once; the others are
        // per-connection and are idempotent here.
        await ExecAsync(conn, "PRAGMA journal_mode = WAL;", ct);
        await ExecAsync(conn, "PRAGMA synchronous = NORMAL;", ct);
        await ExecAsync(conn, "PRAGMA temp_store = MEMORY;", ct);
        await ExecAsync(conn, "PRAGMA mmap_size = 268435456;", ct);
        await ExecAsync(conn, "PRAGMA wal_autocheckpoint = 1000;", ct);
        await ExecAsync(conn, "PRAGMA busy_timeout = 5000;", ct);
        await ExecAsync(conn, "PRAGMA foreign_keys = ON;", ct);

        // §28.1 schema. v0.0.1 ships a single fixed schema; no migrations
        // runner. Subsequent versions can add ALTER TABLE statements with
        // a schema_version table when needed.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS sessions (
                id                            TEXT PRIMARY KEY NOT NULL,
                profile_id                    TEXT,
                sequence_json                 TEXT,
                started_at                    TEXT NOT NULL,
                ended_at                      TEXT,
                recovery_needed               INTEGER NOT NULL DEFAULT 0,
                last_completed_instruction_id TEXT,
                current_target_id             TEXT,
                frame_count                   INTEGER NOT NULL DEFAULT 0
            );
            """, ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS frames (
                id                 TEXT PRIMARY KEY NOT NULL,
                session_id         TEXT NOT NULL,
                target_name        TEXT NOT NULL,
                frame_type         TEXT NOT NULL,
                filter_name        TEXT,
                exposure_seconds   REAL NOT NULL,
                gain               INTEGER,
                "offset"           INTEGER,
                temperature_c      REAL,
                captured_utc       TEXT NOT NULL,
                file_path          TEXT NOT NULL,
                file_size_bytes    INTEGER NOT NULL,
                width              INTEGER NOT NULL,
                height             INTEGER NOT NULL,
                bit_depth          INTEGER NOT NULL,
                hfr                REAL,
                star_count         INTEGER,
                eccentricity       REAL,
                guiding_rms_arcsec REAL,
                snr_estimate       REAL,
                quality_score_json TEXT,
                rating             INTEGER NOT NULL DEFAULT 0,
                tags_json          TEXT NOT NULL DEFAULT '[]',
                focuser_position   INTEGER,
                FOREIGN KEY (session_id) REFERENCES sessions(id)
            );
            """, ct);

        // §38 focuser position (for the §50.4 focus-vs-temperature view). Additive
        // column: fresh DBs get it from CREATE TABLE above; a DB created by an
        // earlier build is brought forward with an idempotent ADD COLUMN. (v0.0.1
        // ships no migrations runner — this is the sanctioned additive-column path.)
        await AddColumnIfMissingAsync(conn, "frames", "focuser_position", "INTEGER", ct);
        // §44.6 backup stream: sync bookkeeping + the lazily-cached content hash.
        await AddColumnIfMissingAsync(conn, "frames", "sync_target", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "frames", "synced_at", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "frames", "sha256", "TEXT", ct);

        // §28 widening pass (pre-§40): sub-second calibration exposures need
        // exposure_seconds REAL (they rounded up to 1s as INTEGER), and gain must
        // be nullable (a camera that doesn't report gain stored the -1 sentinel).
        // SQLite can't ALTER COLUMN, so a DB created with the old DDL is rebuilt
        // once via the documented recreate dance; the trigger is the old DDL text
        // itself, so this is idempotent and fresh DBs (already the new shape) skip
        // it. Legacy -1 gain sentinels are normalized to NULL during the copy.
        await MigrateFramesWideningAsync(conn, ct);

        // §28-style pass 2: temperature_c drops NOT NULL — a camera that reports
        // no CCD temperature records NULL, not the fabricated 0.0 sentinel (the
        // same honesty fix #670 made for gain). Existing rows are copied verbatim:
        // a stored 0.0 cannot be told apart from a real 0.0degC reading, so the §39
        // matching queries bucket NULL together with 0 via COALESCE, preserving
        // the documented uncooled-match semantics exactly. This pass also
        // introduces the schema_version table (the #670 review commitment) so
        // future passes key on a version, not a DDL sniff.
        await MigrateTemperatureNullableAsync(conn, ct);
        await ExecAsync(conn, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);", ct);
        await ExecAsync(conn, """
            INSERT INTO schema_version (version)
            SELECT 4 WHERE NOT EXISTS (SELECT 1 FROM schema_version);
            UPDATE schema_version SET version = 4 WHERE version < 4;
            """, ct);

        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_frames_session_id ON frames(session_id);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_frames_captured_utc ON frames(captured_utc);", ct);
        // §50 stats-perf: most stats queries restrict to light frames and
        // order/group by captured_utc (targets, calendar, frame-quality,
        // best-frames, focus-temp). A partial covering index keeps the
        // `frame_type = 'light'` predicate from being a residual filter over the
        // full captured_utc scan. Additive + idempotent like the others — a
        // database from an earlier build gets it on next startup. (Same
        // partial-index shape as idx_diag_open below.)
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_frames_light_captured ON frames(captured_utc) WHERE frame_type = 'light';", ct);

        // §46.5 notifications log. Indexes on posted_utc (most lookups
        // are time-ordered) + read (unread-only filter is the hot path
        // for WILMA's notification bell badge count).
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS notifications (
                id                  TEXT PRIMARY KEY NOT NULL,
                posted_utc          TEXT NOT NULL,
                severity            TEXT NOT NULL,
                category            TEXT NOT NULL,
                title               TEXT NOT NULL,
                message             TEXT NOT NULL,
                read                INTEGER NOT NULL DEFAULT 0,
                dismissed           INTEGER NOT NULL DEFAULT 0,
                dismissed_utc       TEXT,
                payload_json        TEXT,
                related_entity_type TEXT,
                related_entity_id   TEXT
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_notifications_posted_utc ON notifications(posted_utc);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_notifications_read ON notifications(read);", ct);

        // §46.4 + general key/value config. Single row per key. Used so
        // far by notification preferences (single JSON blob value); future
        // sections (alarm-sound file path, etc.) reuse.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS app_config (
                key   TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );
            """, ct);

        // §51 diagnostics log. Same table holds open issues + historical
        // events: cleared_utc IS NULL means open. Issue-specific fields
        // (recommended_action, auto_correctible) are nullable for non-
        // issue events (sequence.started, frame.complete, etc.).
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS diagnostic_events (
                id                      TEXT PRIMARY KEY NOT NULL,
                event_type              TEXT NOT NULL,
                severity                TEXT NOT NULL,
                description             TEXT NOT NULL,
                detected_utc            TEXT NOT NULL,
                cleared_utc             TEXT,
                auto_action_taken       INTEGER NOT NULL DEFAULT 0,
                auto_action_description TEXT,
                recommended_action      TEXT,
                auto_correctible        INTEGER
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_diag_detected_utc ON diagnostic_events(detected_utc);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_diag_open ON diagnostic_events(cleared_utc) WHERE cleared_utc IS NULL;", ct);

        LogCatalogInitialized(DatabasePath);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Per the Microsoft CA2100 guidance: every caller passes a compile-time-constant DDL/PRAGMA string literal; no user input is incorporated into the command text.")]
    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // See the §28 widening comment at the call site. Detection sniffs the stored
    // CREATE TABLE text for the OLD column shape — after the rebuild (or on a
    // fresh DB) the text no longer matches, so re-running is a no-op.
    private static async Task MigrateFramesWideningAsync(SqliteConnection conn, CancellationToken ct) {
        string? ddl;
        await using (var probe = conn.CreateCommand()) {
            probe.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'frames';";
            ddl = (string?)await probe.ExecuteScalarAsync(ct);
        }
        if (ddl is null || !ddl.Contains("exposure_seconds   INTEGER", StringComparison.Ordinal)) {
            return; // fresh DB (new shape) or already migrated
        }
        // The documented SQLite recreate dance, atomic in one transaction. Column
        // list pinned explicitly (both shapes share it; focuser_position exists on
        // any DB that reaches here — AddColumnIfMissingAsync ran just above).
        await ExecAsync(conn, """
            BEGIN;
            CREATE TABLE frames_widened (
                id                 TEXT PRIMARY KEY NOT NULL,
                session_id         TEXT NOT NULL,
                target_name        TEXT NOT NULL,
                frame_type         TEXT NOT NULL,
                filter_name        TEXT,
                exposure_seconds   REAL NOT NULL,
                gain               INTEGER,
                "offset"           INTEGER,
                temperature_c      REAL,
                captured_utc       TEXT NOT NULL,
                file_path          TEXT NOT NULL,
                file_size_bytes    INTEGER NOT NULL,
                width              INTEGER NOT NULL,
                height             INTEGER NOT NULL,
                bit_depth          INTEGER NOT NULL,
                hfr                REAL,
                star_count         INTEGER,
                eccentricity       REAL,
                guiding_rms_arcsec REAL,
                snr_estimate       REAL,
                quality_score_json TEXT,
                rating             INTEGER NOT NULL DEFAULT 0,
                tags_json          TEXT NOT NULL DEFAULT '[]',
                focuser_position   INTEGER,
                FOREIGN KEY (session_id) REFERENCES sessions(id)
            );
            INSERT INTO frames_widened
                SELECT id, session_id, target_name, frame_type, filter_name,
                       exposure_seconds,
                       CASE WHEN gain = -1 THEN NULL ELSE gain END,
                       "offset", temperature_c, captured_utc, file_path,
                       file_size_bytes, width, height, bit_depth, hfr, star_count,
                       eccentricity, guiding_rms_arcsec, snr_estimate,
                       quality_score_json, rating, tags_json, focuser_position
                FROM frames;
            DROP TABLE frames;
            ALTER TABLE frames_widened RENAME TO frames;
            CREATE INDEX IF NOT EXISTS idx_frames_session_id ON frames(session_id);
            CREATE INDEX IF NOT EXISTS idx_frames_captured_utc ON frames(captured_utc);
            CREATE INDEX IF NOT EXISTS idx_frames_light_captured ON frames(captured_utc) WHERE frame_type = 'light';
            COMMIT;
            """, ct);
    }


    // Pass 2 of the frames widening (see call site). Trigger: the stored DDL
    // still carries temperature_c NOT NULL. Data copied verbatim — see the call
    // site comment for why 0.0 sentinels are NOT rewritten to NULL.
    private static async Task MigrateTemperatureNullableAsync(SqliteConnection conn, CancellationToken ct) {
        string? ddl;
        await using (var probe = conn.CreateCommand()) {
            probe.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'frames';";
            ddl = (string?)await probe.ExecuteScalarAsync(ct);
        }
        if (ddl is null || !ddl.Contains("temperature_c      REAL NOT NULL", StringComparison.Ordinal)) {
            return; // fresh DB (new shape) or already migrated
        }
        await ExecAsync(conn, """
            BEGIN;
            CREATE TABLE frames_widened (
                id                 TEXT PRIMARY KEY NOT NULL,
                session_id         TEXT NOT NULL,
                target_name        TEXT NOT NULL,
                frame_type         TEXT NOT NULL,
                filter_name        TEXT,
                exposure_seconds   REAL NOT NULL,
                gain               INTEGER,
                "offset"           INTEGER,
                temperature_c      REAL,
                captured_utc       TEXT NOT NULL,
                file_path          TEXT NOT NULL,
                file_size_bytes    INTEGER NOT NULL,
                width              INTEGER NOT NULL,
                height             INTEGER NOT NULL,
                bit_depth          INTEGER NOT NULL,
                hfr                REAL,
                star_count         INTEGER,
                eccentricity       REAL,
                guiding_rms_arcsec REAL,
                snr_estimate       REAL,
                quality_score_json TEXT,
                rating             INTEGER NOT NULL DEFAULT 0,
                tags_json          TEXT NOT NULL DEFAULT '[]',
                focuser_position   INTEGER,
                FOREIGN KEY (session_id) REFERENCES sessions(id)
            );
            INSERT INTO frames_widened
                SELECT id, session_id, target_name, frame_type, filter_name,
                       exposure_seconds, gain, "offset", temperature_c,
                       captured_utc, file_path, file_size_bytes, width, height,
                       bit_depth, hfr, star_count, eccentricity,
                       guiding_rms_arcsec, snr_estimate, quality_score_json,
                       rating, tags_json, focuser_position
                FROM frames;
            DROP TABLE frames;
            ALTER TABLE frames_widened RENAME TO frames;
            CREATE INDEX IF NOT EXISTS idx_frames_session_id ON frames(session_id);
            CREATE INDEX IF NOT EXISTS idx_frames_captured_utc ON frames(captured_utc);
            CREATE INDEX IF NOT EXISTS idx_frames_light_captured ON frames(captured_utc) WHERE frame_type = 'light';
            COMMIT;
            """, ct);
    }

    /// <summary>Adds a column to an existing table only when it isn't already
    /// present, so re-running init (or bringing a DB created by an earlier build
    /// forward) is idempotent. SQLite's <c>ALTER TABLE ADD COLUMN</c> errors if the
    /// column exists, so we gate on <c>PRAGMA table_info</c> rather than catching.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "table/column/columnType are compile-time-constant identifiers from trusted callers (schema init), never user input; SQLite parameters can't bind DDL identifiers.")]
    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn, string table, string column, string columnType, CancellationToken ct) {
        await using (var probe = conn.CreateCommand()) {
            probe.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await probe.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                // table_info column 1 is the column name.
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) {
                    return;
                }
            }
        }
        await ExecAsync(conn, $"ALTER TABLE {table} ADD COLUMN {column} {columnType};", ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SQLite catalog initialized at {Path}")]
    private partial void LogCatalogInitialized(string path);
}