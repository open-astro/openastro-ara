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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28 SQLite catalog handle. Opens the connection on first resolve,
/// applies the §28.6 PRAGMAs, and ensures the §28.1 schema (sessions +
/// frames tables) exists. Repositories receive a callable factory so
/// each operation can open its own short-lived connection — the
/// SqliteConnection is itself a connection-pool front, not a single
/// open socket.
/// </summary>
public interface IAraDatabase {
    /// <summary>
    /// Open a configured connection to the catalog. Caller owns the
    /// returned connection and must dispose it. PRAGMAs are applied
    /// once on first open per server lifetime (see <see cref="InitializeAsync"/>)
    /// — the connection here just inherits them via the SQLite session.
    /// </summary>
    SqliteConnection OpenConnection();

    /// <summary>
    /// Apply PRAGMAs + run schema migrations. Idempotent: safe to call
    /// repeatedly. Called once at server startup.
    /// </summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>Path to the SQLite file on disk, for logging + tooling.</summary>
    string DatabasePath { get; }
}