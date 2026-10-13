#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.2 matrix row 18 — "Dew formation suspected": humidity near saturation AND the air at its dew
/// point AND the light frames' HFR drifting up (halos forming) → an advisory, nothing automatic (the
/// user wipes the optics or switches the heaters on). The verdict opens the §51 <c>dew_formation</c>
/// diagnostic that <see cref="DiagnosticsAutofocusGate"/> already defers autofocus on, and posts one
/// Warning notification per episode; the diagnostic clears itself when the pattern breaks. Pure rule in
/// <see cref="DewFormationRule"/>; this class only polls and keeps the episode.
/// </summary>
public sealed partial class DewFormationAdvisor : IHostedService, IDisposable {
    public const string EventType = "dew_formation";

    /// <summary>The HFR trend is judged over the most recent lights of the CURRENT filter taken since
    /// the last autofocus (an autofocus resets the baseline; another filter has another HFR), capped
    /// to this many frames so real dew in the last subs moves the late third.</summary>
    public const int WindowFrames = 30;

    private readonly IObservingConditionsService? _weather;
    private readonly ImageHistoryService? _history;
    private readonly IDiagnosticsService? _diagnostics;
    private readonly INotificationService? _notifications;
    private readonly ILogger<DewFormationAdvisor> _logger;
    private readonly SemaphoreSlim _tick = new(1, 1);
    private Timer? _timer;
    private bool _episodeOpen;
    private bool _disposed;

    /// <summary>How often the pattern is re-read. Weather stations update every few seconds and
    /// the HFR trend moves per frame; a minute is plenty for an advisory.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public DewFormationAdvisor(
            IObservingConditionsService? weather,
            ImageHistoryService? history,
            IDiagnosticsService? diagnostics,
            INotificationService? notifications,
            ILogger<DewFormationAdvisor>? logger = null) {
        _weather = weather;
        _history = history;
        _diagnostics = diagnostics;
        _notifications = notifications;
        _logger = logger ?? NullLogger<DewFormationAdvisor>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        // Startup reconciliation (the DiskSpaceMonitor pattern): the episode lives in memory but the
        // diagnostic lives in SQLite, so an event left open by a previous daemon would defer autofocus
        // for good. Nothing has been emitted yet, so this only touches a stale one; if dew is still
        // forming, the first tick re-opens it.
        if (_diagnostics is not null) {
            try {
                await _diagnostics.ClearOpenEventsByTypeAsync(EventType, UtcNow(), cancellationToken).ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                LogTickFailed(ex); // best effort: a diagnostics hiccup must not stop the daemon's hosted services
            }
        }
        if (_weather is not null && _history is not null) {
            _timer = new Timer(_ => _ = CheckOnceAsync(CancellationToken.None), null, PollInterval, PollInterval);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        // Let a tick in flight finish before Dispose frees the gate it will release.
        await _tick.WaitAsync(cancellationToken).ConfigureAwait(false);
        _tick.Release();
    }

    /// <summary>One poll: read the weather and the HFR trend, move the episode. Internal for tests.</summary>
    internal async Task CheckOnceAsync(CancellationToken ct) {
        if (_weather is null || _history is null || _disposed || !await _tick.WaitAsync(0, ct).ConfigureAwait(false)) {
            return;
        }
        try {
            ObservingConditionsDto? weather;
            try {
                weather = await _weather.GetAsync(ct).ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                LogWeatherReadFailed(ex);
                return; // no verdict without weather; the episode keeps its last state
            }
            if (weather is null || weather.State != EquipmentConnectionState.Connected) {
                return; // a station that is not connected reads nothing: same as a failed read
            }
            // Deliberate asymmetry: a CONNECTED station that leaves a reading null is answering
            // ("I cannot see dew conditions"), so an open episode closes; a station that has gone
            // away is not answering, so the episode keeps its last state (above).
            var hfr = RecentLightHfr(_history.ImagePoints, _history.AutofocusPoints);
            var verdict = DewFormationRule.Evaluate(weather.HumidityPct, weather.TemperatureC, weather.DewPointC, hfr);
            if (verdict.Suspected && !_episodeOpen) {
                // The flag follows a successfully WRITTEN diagnostic: a failed write retries next tick
                // instead of counting as an episode nobody can see; the notification after it is best
                // effort, so a failed post never re-opens a second diagnostic.
                await OpenEpisodeAsync(verdict.Detail, ct).ConfigureAwait(false);
            } else if (!verdict.Suspected && _episodeOpen) {
                await CloseEpisodeAsync(ct).ConfigureAwait(false);
                _episodeOpen = false;
            }
        } catch (ObjectDisposedException) {
            // A timer callback queued just before Dispose: nothing to do.
            return;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogTickFailed(ex);
        } finally {
            if (!_disposed) {
                _tick.Release();
            }
        }
    }

    /// <summary>The HFR figures the rule judges: lights after the latest autofocus (its ordinal
    /// watermark), of the filter the most recent light used, newest <see cref="WindowFrames"/> only.
    /// Internal static for tests.</summary>
    internal static List<double> RecentLightHfr(IReadOnlyList<OpenAstroAra.Sequencer.Interfaces.ImageHistoryEntry> images,
            IReadOnlyList<OpenAstroAra.Sequencer.Interfaces.AutofocusHistoryEntry> autofocus) {
        var sinceId = autofocus.Count == 0 ? long.MinValue : autofocus[^1].Id;
        var lights = images.Where(p => string.Equals(p.Type, "LIGHT", StringComparison.OrdinalIgnoreCase) && p.Id > sinceId).ToList();
        if (lights.Count == 0) {
            return [];
        }
        var filter = lights[^1].Filter;
        return lights.Where(p => string.Equals(p.Filter, filter, StringComparison.OrdinalIgnoreCase))
            .TakeLast(WindowFrames).Select(p => p.Hfr).ToList();
    }

    private async Task OpenEpisodeAsync(string detail, CancellationToken ct) {
        var now = UtcNow();
        var description = $"Dew formation suspected: {detail}. Wipe the optics or switch on the dew heaters; autofocus is deferred while this is open.";
        LogSuspected(detail);
        if (_diagnostics is not null) {
            await _diagnostics.CreateEventAsync(
                new DiagnosticEventDto(Guid.NewGuid(), EventType, DiagnosticHealth.Yellow, description, now,
                    ClearedUtc: null, AutoActionTaken: false, AutoActionDescription: null),
                recommendedAction: "Wipe the optics or switch on the dew heaters.",
                autoCorrectible: false, ct).ConfigureAwait(false);
        }
        _episodeOpen = true; // the diagnostic is on record (or there is no diagnostics store to write)
        if (_notifications is not null) {
            try {
                await _notifications.CreateAsync(new NotificationDto(
                    Id: Guid.NewGuid(), PostedUtc: now, Severity: NotificationSeverity.Warning, Category: NotificationCategory.Equipment,
                    Title: "Dew formation suspected", Message: description,
                    Read: false, Dismissed: false, DismissedUtc: null, Payload: null, RelatedEntityType: null, RelatedEntityId: null), ct)
                    .ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                LogNotifyFailed(ex); // best effort: the diagnostic already carries the advisory
            }
        }
    }

    private async Task CloseEpisodeAsync(CancellationToken ct) {
        LogCleared();
        if (_diagnostics is not null) {
            await _diagnostics.ClearOpenEventsByTypeAsync(EventType, UtcNow(), ct).ConfigureAwait(false);
        }
    }

    public void Dispose() {
        _disposed = true;
        _timer?.Dispose();
        _tick.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dew formation suspected: {Detail}")]
    private partial void LogSuspected(string detail);
    [LoggerMessage(Level = LogLevel.Information, Message = "Dew formation pattern cleared; the dew_formation diagnostic is closed.")]
    private partial void LogCleared();
    [LoggerMessage(Level = LogLevel.Debug, Message = "Dew advisor: weather read failed; keeping the last verdict.")]
    private partial void LogWeatherReadFailed(Exception ex);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Dew advisor tick failed.")]
    private partial void LogTickFailed(Exception ex);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Dew advisory notification could not be posted; the dew_formation diagnostic is open.")]
    private partial void LogNotifyFailed(Exception ex);
}

/// <summary>The §42.2 row-18 pattern as a pure rule.</summary>
public static class DewFormationRule {
    /// <summary>Relative humidity at or above this is "near 100 %".</summary>
    public const double HumidityThresholdPct = 95.0;
    /// <summary>Ambient within this many degrees of the dew point is "at the dew point".</summary>
    public const double DewDeltaThresholdC = 1.0;
    /// <summary>Lights needed before the HFR trend means anything.</summary>
    public const int MinFrames = 6;
    /// <summary>The latest third's median HFR must exceed the earliest third's by this fraction.</summary>
    public const double HfrRiseFraction = 0.15;

    public static (bool Suspected, string Detail) Evaluate(double? humidityPct, double? temperatureC, double? dewPointC, IReadOnlyList<double> recentLightHfr) {
        if (humidityPct is not double rh || temperatureC is not double t || dewPointC is not double dp) {
            return (false, "weather incomplete");
        }
        if (rh < HumidityThresholdPct) {
            return (false, $"humidity {rh:0} %");
        }
        var delta = t - dp;
        if (delta > DewDeltaThresholdC) {
            return (false, $"air {delta:0.0} °C above the dew point");
        }
        var hfr = recentLightHfr.Where(h => h > 0 && double.IsFinite(h)).ToList();
        if (hfr.Count < MinFrames) {
            return (false, $"only {hfr.Count} light frames to judge the HFR trend");
        }
        var third = hfr.Count / 3;
        var early = Median(hfr.Take(third));
        var late = Median(hfr.Skip(hfr.Count - third));
        if (early <= 0 || late < early * (1 + HfrRiseFraction)) {
            return (false, $"HFR steady ({early:0.00} → {late:0.00})");
        }
        return (true, string.Create(CultureInfo.InvariantCulture,
            $"humidity {rh:0} %, air {delta:0.0} °C above the dew point, HFR up {(late / early - 1) * 100:0} % ({early:0.00} → {late:0.00} px)"));
    }

    private static double Median(IEnumerable<double> values) {
        var s = values.OrderBy(v => v).ToArray();
        return s.Length == 0 ? 0 : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2.0;
    }
}
