#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §46.5 SQLite-backed <see cref="INotificationService"/>. Notifications
/// persist across daemon restart via the <c>notifications</c> table on
/// the §28 catalog; preferences live as a JSON blob in <c>app_config</c>
/// under the <c>notification_preferences</c> key.
/// </summary>
public sealed class SqliteNotificationService : INotificationService {
    private const string PrefsKey = "notification_preferences";

    private static readonly Guid[] SampleIds = new[] {
        Guid.Parse("33333333-3333-3333-3333-333333333331"),
        Guid.Parse("33333333-3333-3333-3333-333333333332"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
    };

    private readonly IAraDatabase _db;
    private readonly ILogger<SqliteNotificationService>? _logger;

    public SqliteNotificationService(IAraDatabase db, ILogger<SqliteNotificationService>? logger) {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Seed fixture notifications + default preferences on first init.
    /// Idempotent — skipped if the notifications table is already
    /// populated.
    /// </summary>
    public async Task EnsureSeededAsync(CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM notifications;";
        var count = (long)(await checkCmd.ExecuteScalarAsync(ct) ?? 0L);
        if (count > 0) return;

        var baseTime = new DateTimeOffset(2026, 5, 30, 3, 0, 0, TimeSpan.Zero);
        await InsertAsync(conn, new NotificationDto(
            Id: SampleIds[0],
            PostedUtc: baseTime,
            Severity: NotificationSeverity.Info,
            Category: NotificationCategory.Sequence,
            Title: "Sequence started",
            Message: "M31 imaging sequence started — 2 targets, 12 frames planned.",
            Read: true,
            Dismissed: false,
            DismissedUtc: null,
            Payload: null,
            RelatedEntityType: "session",
            RelatedEntityId: "11111111-1111-1111-1111-111111111111"), ct);
        await InsertAsync(conn, new NotificationDto(
            Id: SampleIds[1],
            PostedUtc: baseTime.AddMinutes(45),
            Severity: NotificationSeverity.Warning,
            Category: NotificationCategory.Storage,
            Title: "Disk space low",
            Message: "Save directory has 8.2 GB free — about 4 more hours of imaging at current rate.",
            Read: false,
            Dismissed: false,
            DismissedUtc: null,
            Payload: null,
            RelatedEntityType: null,
            RelatedEntityId: null), ct);
        await InsertAsync(conn, new NotificationDto(
            Id: SampleIds[2],
            PostedUtc: baseTime.AddMinutes(90),
            Severity: NotificationSeverity.Critical,
            Category: NotificationCategory.Safety,
            Title: "Unsafe weather — paused + parked",
            Message: "Cloud sensor crossed unsafe threshold. Mount parked, dome closed per §35 policy.",
            Read: false,
            Dismissed: false,
            DismissedUtc: null,
            Payload: null,
            RelatedEntityType: null,
            RelatedEntityId: null), ct);

        _logger?.LogInformation("Seeded 3 sample notifications into catalog");
    }

    public async Task<CursorPage<NotificationDto>> ListAsync(int limit, string? cursor, bool? unreadOnly, CancellationToken ct) {
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0) {
            offset = parsed;
        }
        var pageSize = Math.Clamp(limit, 1, 200);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var sql = """
            SELECT id, posted_utc, severity, category, title, message,
                   read, dismissed, dismissed_utc, payload_json,
                   related_entity_type, related_entity_id
            FROM notifications
            WHERE 1=1
            """;
        if (unreadOnly == true) {
            sql += " AND read = 0";
        }
        sql += " ORDER BY posted_utc DESC LIMIT $limit OFFSET $offset;";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$limit", pageSize + 1);
        cmd.Parameters.AddWithValue("$offset", offset);

        var items = new List<NotificationDto>(pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct) && items.Count < pageSize) {
            items.Add(ReadRow(reader));
        }
        var hasMore = await reader.ReadAsync(ct);
        var nextCursor = hasMore ? (offset + pageSize).ToString() : null;
        return new CursorPage<NotificationDto>(items, NextCursor: nextCursor, HasMore: hasMore);
    }

    public async Task<NotificationDto?> DismissAsync(Guid id, NotificationActionRequestDto request, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE notifications
            SET dismissed = 1, dismissed_utc = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0) return null;
        return await GetAsync(conn, id, ct);
    }

    public async Task<NotificationDto?> MarkReadAsync(Guid id, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE notifications SET read = 1 WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0) return null;
        return await GetAsync(conn, id, ct);
    }

    public async Task<NotificationPreferenceDto> GetPreferencesAsync(CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_config WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", PrefsKey);
        var raw = (string?)(await cmd.ExecuteScalarAsync(ct));
        if (string.IsNullOrEmpty(raw)) return DefaultPreferences();
        try {
            return JsonSerializer.Deserialize(raw, AraJsonSerializerContext.Default.NotificationPreferenceDto)
                ?? DefaultPreferences();
        } catch (JsonException) {
            // Corrupt prefs row — fall back to defaults rather than 500.
            return DefaultPreferences();
        }
    }

    public async Task CreateAsync(NotificationDto notification, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await InsertAsync(conn, notification, ct);
    }

    public async Task<NotificationPreferenceDto> SetPreferencesAsync(NotificationPreferenceDto preferences, CancellationToken ct) {
        var json = JsonSerializer.Serialize(preferences,
            AraJsonSerializerContext.Default.NotificationPreferenceDto);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_config (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", PrefsKey);
        cmd.Parameters.AddWithValue("$value", json);
        await cmd.ExecuteNonQueryAsync(ct);
        return preferences;
    }

    // ─────────────────────────────────────────────────────────────────

    private static async Task<NotificationDto?> GetAsync(SqliteConnection conn, Guid id, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, posted_utc, severity, category, title, message,
                   read, dismissed, dismissed_utc, payload_json,
                   related_entity_type, related_entity_id
            FROM notifications WHERE id = $id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader) : null;
    }

    private static NotificationDto ReadRow(SqliteDataReader reader) {
        System.Text.Json.JsonElement? payload = null;
        if (!reader.IsDBNull(9)) {
            try {
                using var doc = JsonDocument.Parse(reader.GetString(9));
                payload = doc.RootElement.Clone();
            } catch (JsonException) { /* corrupt blob → null */ }
        }
        return new NotificationDto(
            Id: Guid.Parse(reader.GetString(0)),
            PostedUtc: DateTimeOffset.Parse(reader.GetString(1)),
            Severity: Enum.Parse<NotificationSeverity>(reader.GetString(2), ignoreCase: true),
            Category: Enum.Parse<NotificationCategory>(reader.GetString(3), ignoreCase: true),
            Title: reader.GetString(4),
            Message: reader.GetString(5),
            Read: reader.GetInt32(6) != 0,
            Dismissed: reader.GetInt32(7) != 0,
            DismissedUtc: reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            Payload: payload,
            RelatedEntityType: reader.IsDBNull(10) ? null : reader.GetString(10),
            RelatedEntityId: reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    private static async Task InsertAsync(SqliteConnection conn, NotificationDto n, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notifications
                (id, posted_utc, severity, category, title, message,
                 read, dismissed, dismissed_utc, payload_json,
                 related_entity_type, related_entity_id)
            VALUES
                ($id, $posted, $severity, $category, $title, $message,
                 $read, $dismissed, $dismissed_utc, $payload,
                 $related_type, $related_id);
            """;
        cmd.Parameters.AddWithValue("$id", n.Id.ToString());
        cmd.Parameters.AddWithValue("$posted", n.PostedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$severity", n.Severity.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$category", n.Category.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$title", n.Title);
        cmd.Parameters.AddWithValue("$message", n.Message);
        cmd.Parameters.AddWithValue("$read", n.Read ? 1 : 0);
        cmd.Parameters.AddWithValue("$dismissed", n.Dismissed ? 1 : 0);
        cmd.Parameters.AddWithValue("$dismissed_utc", (object?)n.DismissedUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", n.Payload.HasValue ? n.Payload.Value.GetRawText() : DBNull.Value);
        cmd.Parameters.AddWithValue("$related_type", (object?)n.RelatedEntityType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$related_id", (object?)n.RelatedEntityId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NotificationPreferenceDto DefaultPreferences() => new(
        AlarmSoundEnabled: true,
        AlarmSoundFile: null,
        QuietHours: null,
        CategoryPreferences: new[] {
            new NotificationCategoryPrefDto(NotificationCategory.Equipment, true, NotificationSeverity.Info),
            new NotificationCategoryPrefDto(NotificationCategory.Sequence, true, NotificationSeverity.Info),
            new NotificationCategoryPrefDto(NotificationCategory.Storage, true, NotificationSeverity.Info),
            new NotificationCategoryPrefDto(NotificationCategory.Software, true, NotificationSeverity.Info),
            new NotificationCategoryPrefDto(NotificationCategory.Safety, true, NotificationSeverity.Info),
            new NotificationCategoryPrefDto(NotificationCategory.Alarm, true, NotificationSeverity.Warning),
        });
}