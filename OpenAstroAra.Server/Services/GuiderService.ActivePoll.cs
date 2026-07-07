#region "copyright"

/*
    Copyright (c) 2026 - present Open Astro contributors

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §63.3 active hang-detection. The passive <c>PHD2ConnectionLost</c> event only
/// catches a *dead* socket; a wedged daemon whose event socket stays open but whose
/// RPC dispatch is unresponsive would previously go unnoticed forever. While
/// Connected, a one-shot rescheduling timer pings <c>get_app_state</c> on a fresh
/// per-call RPC connection — every 2 s while guiding (a wedge there costs frames
/// immediately) and every 10 s otherwise. Three consecutive failed pings converge on
/// the same link-down path as a socket death (Error → §63.3 recovery → §42.2 fault
/// flow). One ping in flight at a time; a success resets the streak.
/// </summary>
public sealed partial class GuiderService {

    // All guarded by _gate except the Interlocked single-flight flag.
    private Timer? _pingTimer;
    private int _pingFailureStreak;
    private long _pingSession; // bumped on every loop start/stop so a stale tick can't act
    private int _pingInFlight; // Interlocked single-flight

    // ─── test seams (the UnattendedShutdownService knob pattern) ───
    /// <summary>Ping cadence while the guider reports it is actively guiding.</summary>
    internal TimeSpan PingIntervalGuiding { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Ping cadence for any other connected state.</summary>
    internal TimeSpan PingIntervalIdle { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Per-ping RPC timeout (§63.3's 5 s poll bound).</summary>
    internal TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Consecutive failures that declare the daemon down (§63.3's "3 consecutive RPC failures").</summary>
    internal int PingFailureThreshold { get; set; } = 3;
    /// <summary>Probe seam — tests swap the concrete <c>PHD2Guider.PingAsync</c> for a scriptable one.</summary>
    internal Func<OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PHD2Guider, int, Task<bool>> PingProbe { get; set; } =
        static (guider, timeoutMs) => guider.PingAsync(timeoutMs);

    // Caller holds _gate.
    private void StartPingLoopLocked() {
        if (_disposed) {
            return;
        }
        StopPingLoopLocked();
        var session = ++_pingSession;
        _pingFailureStreak = 0;
        _pingTimer = new Timer(_ => OnPingTick(session), null, PingIntervalIdle, Timeout.InfiniteTimeSpan);
    }

    // Caller holds _gate.
    private void StopPingLoopLocked() {
        ++_pingSession;
        _pingTimer?.Dispose();
        _pingTimer = null;
        _pingFailureStreak = 0;
    }

    private void OnPingTick(long session) {
        // Timer callback boundary — nothing may throw, overlapping ticks skip.
        _ = PingOnceAsync(session);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-tick boundary: any fault is logged and the next tick retries; an escape would be an unobserved task exception.")]
    internal async Task PingOnceAsync(long session) {
        if (Interlocked.CompareExchange(ref _pingInFlight, 1, 0) != 0) {
            return;
        }
        try {
            OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PHD2Guider? guider;
            lock (_gate) {
                if (_disposed || session != _pingSession || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                guider = _guider;
            }
            if (guider is null) {
                return;
            }

            var alive = await PingProbe(guider, (int)PingTimeout.TotalMilliseconds).ConfigureAwait(false);

            var declareDown = false;
            lock (_gate) {
                if (_disposed || session != _pingSession || _state != EquipmentConnectionState.Connected
                    || !ReferenceEquals(guider, _guider)) {
                    return;
                }
                if (alive) {
                    _pingFailureStreak = 0;
                } else {
                    _pingFailureStreak++;
                    LogPingFailed(_pingFailureStreak, PingFailureThreshold);
                    if (_pingFailureStreak >= PingFailureThreshold) {
                        declareDown = true;
                        LogPingDeclaredDown(_pingFailureStreak);
                        // HandleLinkDownLocked → SetStateLocked(Error) stops this loop.
                        HandleLinkDownLocked();
                    }
                }
                if (!declareDown) {
                    RescheduleLocked(session);
                }
            }
        } catch (Exception ex) {
            LogPingTickFailed(ex);
            lock (_gate) {
                if (!_disposed && session == _pingSession) {
                    RescheduleLocked(session);
                }
            }
        } finally {
            Interlocked.Exchange(ref _pingInFlight, 0);
        }
    }

    // Caller holds _gate. One-shot reschedule so the cadence tracks the CURRENT
    // guiding state (2 s guiding / 10 s idle) without a second timer.
    private void RescheduleLocked(long session) {
        if (_pingTimer is null || session != _pingSession) {
            return;
        }
        // PHD2Guider.State is the raw PHD2 AppState string ("Guiding" while looping on a star).
        var guiding = string.Equals(_guider?.State, "Guiding", StringComparison.OrdinalIgnoreCase);
        _pingTimer.Change(guiding ? PingIntervalGuiding : PingIntervalIdle, Timeout.InfiniteTimeSpan);
    }

    [LoggerMessage(EventId = 6331, Level = LogLevel.Warning, Message = "§63.3 guider liveness ping failed ({Streak}/{Threshold})")]
    private partial void LogPingFailed(int streak, int threshold);

    [LoggerMessage(EventId = 6332, Level = LogLevel.Error, Message = "§63.3 guider declared down after {Streak} consecutive failed pings (socket alive, RPC unresponsive) — treating as connection lost")]
    private partial void LogPingDeclaredDown(int streak);

    [LoggerMessage(EventId = 6333, Level = LogLevel.Warning, Message = "§63.3 liveness ping tick failed (loop continues)")]
    private partial void LogPingTickFailed(Exception exception);
}
