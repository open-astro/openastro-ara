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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28 SQLite catalog backed by <c>${profileDir}/openastroara.db</c>.
/// </summary>
public sealed class SqliteAraDatabase : IAraDatabase {
    private readonly string _connectionString;
    private readonly ILogger<SqliteAraDatabase>? _logger;

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
        _logger = logger;
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
                FOREIGN KEY (session_id) REFERENCES sessions(id)
            );
            """, ct);

        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_frames_session_id ON frames(session_id);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_frames_captured_utc ON frames(captured_utc);", ct);

        _logger?.LogInformation("SQLite catalog initialized at {Path}", DatabasePath);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
