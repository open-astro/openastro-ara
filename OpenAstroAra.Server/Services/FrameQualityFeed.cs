#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>The side the daemon's §59.5 post-capture analysis writes to (one reading per analysed LIGHT).</summary>
public interface IFrameQualitySink {
    void Record(Guid frameId, double eccentricity, double fwhmPx, double exposureSeconds);
}

/// <summary>
/// #1264 — the in-memory latest-frame-quality feed the sequencer's <c>UnguidedExposureGuard</c> reads
/// (<see cref="IFrameQualityFeed"/>) and the camera's analysis worker writes (<see cref="IFrameQualitySink"/>).
/// Acting back on the catalog goes through the frame repository (tags) and the notification center.
/// </summary>
public sealed partial class FrameQualityFeed : IFrameQualityFeed, IFrameQualitySink {
    private readonly IFrameRepository? _frames;
    private readonly INotificationService? _notifications;
    private readonly ILogger<FrameQualityFeed> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private long _sequence;
    private FrameQuality? _latest;

    public FrameQualityFeed(IFrameRepository? frames = null, INotificationService? notifications = null,
            ILogger<FrameQualityFeed>? logger = null, Func<DateTimeOffset>? clock = null) {
        _frames = frames;
        _notifications = notifications;
        _logger = logger ?? NullLogger<FrameQualityFeed>.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public FrameQuality? LatestLight => Volatile.Read(ref _latest);

    public void Record(Guid frameId, double eccentricity, double fwhmPx, double exposureSeconds) {
        var seq = Interlocked.Increment(ref _sequence);
        Volatile.Write(ref _latest, new FrameQuality(frameId, seq, eccentricity, fwhmPx, exposureSeconds, _clock()));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort catalog tag: a repository fault must never fail the guard or the run. Log-and-recover boundary.")]
    public async Task FlagFrameAsync(Guid frameId, string tag, CancellationToken ct) {
        if (_frames is null || string.IsNullOrWhiteSpace(tag)) {
            return;
        }
        try {
            await _frames.BulkTagAsync(new BulkTagRequestDto([frameId], [tag], []), idempotencyKey: null, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            LogFlagFailed(ex, frameId, tag);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort notification: a store fault must never fail the guard or the run. Log-and-recover boundary.")]
    public async Task NotifyAsync(string title, string message, CancellationToken ct) {
        if (_notifications is null) {
            return;
        }
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(), PostedUtc: _clock(), Severity: NotificationSeverity.Warning,
                Category: NotificationCategory.Sequence, Title: title, Message: message,
                Read: false, Dismissed: false, DismissedUtc: null, Payload: null,
                RelatedEntityType: null, RelatedEntityId: null), ct).ConfigureAwait(false);
        } catch (Exception ex) {
            LogNotifyFailed(ex, title);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unguided guard: tagging frame {FrameId} '{Tag}' failed")]
    private partial void LogFlagFailed(Exception ex, Guid frameId, string tag);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unguided guard: notification '{Title}' failed")]
    private partial void LogNotifyFailed(Exception ex, string title);
}
