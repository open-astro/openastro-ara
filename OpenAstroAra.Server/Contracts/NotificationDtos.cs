#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// PORT_PLAYBOOK.md §10.9 + §46 (notifications) + §35.5 (alarms)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>Notification severity.</summary>
public enum NotificationSeverity {
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>Notification UI category for client filtering (§46.3).</summary>
public enum NotificationCategory {
    Equipment,
    Sequence,
    Storage,
    Software,
    Safety,
    Alarm
}

/// <summary>One notification entry.</summary>
public sealed record NotificationDto(
    Guid Id,
    DateTimeOffset PostedUtc,
    NotificationSeverity Severity,
    NotificationCategory Category,
    string Title,
    string Message,
    bool Read,
    bool Dismissed,
    DateTimeOffset? DismissedUtc,
    System.Text.Json.JsonElement? Payload,
    string? RelatedEntityType,
    string? RelatedEntityId);

/// <summary>POST /api/v1/notifications/{id}/dismiss + mark-read share this empty body.</summary>
public sealed record NotificationActionRequestDto(
    string? Reason);

/// <summary>GET /api/v1/notifications/preferences — current settings (§46.4).</summary>
public sealed record NotificationPreferenceDto(
    bool AlarmSoundEnabled,
    string? AlarmSoundFile,
    QuietHoursDto? QuietHours,
    IReadOnlyList<NotificationCategoryPrefDto> CategoryPreferences);

public sealed record NotificationCategoryPrefDto(
    NotificationCategory Category,
    bool Enabled,
    NotificationSeverity? MinimumSeverity);

/// <summary>Quiet-hours window (§46.4).</summary>
public sealed record QuietHoursDto(
    bool Enabled,
    string StartLocalTime,
    string EndLocalTime,
    bool MuteAlarmsTotal);
