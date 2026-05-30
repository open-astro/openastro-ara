#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.4 — placeholder <see cref="INotificationService"/> so WILMA's
/// Notifications view (§46 inbox + §46.4 preferences) can develop against
/// real wire shapes. Three sample notifications covering the common
/// severity + category combinations seen in a real session:
///
///  - Info / Sequence — "Sequence started"
///  - Warning / Storage — disk-space low advisory
///  - Critical / Safety — unsafe-weather pause (read-only — read = false
///    so the UI can render the bold/unread state)
///
/// Mutating endpoints (dismiss + mark-read) update an in-memory cache
/// but don't persist; the §46.5 SQLite-backed notifications log lands
/// in Phase 13.x alongside the §28 frame catalog DB.
///
/// Preferences are loaded from a single in-memory <see cref="NotificationPreferenceDto"/>
/// (defaults: alarm sound on, no quiet hours, every category enabled) —
/// users can flip toggles and they'll persist for the daemon lifetime
/// but reset on restart. Phase 12h.7-style file persistence for these
/// preferences is a follow-up.
/// </summary>
public sealed class PlaceholderNotificationService : INotificationService {
    private static readonly Guid[] SampleIds = new[] {
        Guid.Parse("33333333-3333-3333-3333-333333333331"),
        Guid.Parse("33333333-3333-3333-3333-333333333332"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
    };

    private readonly object _lock = new();
    private readonly Dictionary<Guid, NotificationDto> _notifications;
    private NotificationPreferenceDto _preferences;

    public PlaceholderNotificationService() {
        var baseTime = new DateTimeOffset(2026, 5, 30, 3, 0, 0, TimeSpan.Zero);
        _notifications = new Dictionary<Guid, NotificationDto> {
            [SampleIds[0]] = new(
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
                RelatedEntityId: "11111111-1111-1111-1111-111111111111"),
            [SampleIds[1]] = new(
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
                RelatedEntityId: null),
            [SampleIds[2]] = new(
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
                RelatedEntityId: null),
        };

        // Defaults match the §46.4 spec: alarms on, every category
        // enabled at Info+. Quiet hours off.
        _preferences = new NotificationPreferenceDto(
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

    public Task<CursorPage<NotificationDto>> ListAsync(int limit, string? cursor, bool? unreadOnly, CancellationToken ct) {
        lock (_lock) {
            IEnumerable<NotificationDto> q = _notifications.Values.OrderByDescending(n => n.PostedUtc);
            if (unreadOnly == true) q = q.Where(n => !n.Read);
            var items = q.Take(Math.Max(1, limit)).ToList();
            return Task.FromResult(new CursorPage<NotificationDto>(items, NextCursor: null, HasMore: false));
        }
    }

    public Task<NotificationDto?> DismissAsync(Guid id, NotificationActionRequestDto request, CancellationToken ct) {
        lock (_lock) {
            if (!_notifications.TryGetValue(id, out var existing)) return Task.FromResult<NotificationDto?>(null);
            var updated = existing with { Dismissed = true, DismissedUtc = DateTimeOffset.UtcNow };
            _notifications[id] = updated;
            return Task.FromResult<NotificationDto?>(updated);
        }
    }

    public Task<NotificationDto?> MarkReadAsync(Guid id, CancellationToken ct) {
        lock (_lock) {
            if (!_notifications.TryGetValue(id, out var existing)) return Task.FromResult<NotificationDto?>(null);
            var updated = existing with { Read = true };
            _notifications[id] = updated;
            return Task.FromResult<NotificationDto?>(updated);
        }
    }

    public Task<NotificationPreferenceDto> GetPreferencesAsync(CancellationToken ct) {
        lock (_lock) { return Task.FromResult(_preferences); }
    }

    public Task<NotificationPreferenceDto> SetPreferencesAsync(NotificationPreferenceDto preferences, CancellationToken ct) {
        lock (_lock) {
            _preferences = preferences;
            return Task.FromResult(_preferences);
        }
    }
}
