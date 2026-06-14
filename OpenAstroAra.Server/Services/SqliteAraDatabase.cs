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
                exposure_seconds   INTEGER NOT NULL,
                gain               INTEGER NOT NULL,
                "offset"           INTEGER,
                temperature_c      REAL NOT NULL,
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

        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_frames_session_id ON frames(session_id);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_frames_captured_utc ON frames(captured_utc);", ct);

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