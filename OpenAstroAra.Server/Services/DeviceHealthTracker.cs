#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Services;

/// <summary>What one runtime read did to the device's health.</summary>
public enum HealthVerdict {
    /// <summary>The read worked and the device was healthy before.</summary>
    Healthy,

    /// <summary>The read failed, but not for long enough to call the device degraded.</summary>
    Faltering,

    /// <summary>This failure reached the threshold: the device is degraded. Reported once per episode.</summary>
    BecameDegraded,

    /// <summary>Still failing inside a degraded episode.</summary>
    StillDegraded,

    /// <summary>The first good read after a degraded episode: the device is back.</summary>
    Recovered,
}

/// <summary>
/// §42.3's second channel. The connection probe (<see cref="DeviceConnectionProbe"/>) only asks
/// <c>Connected</c>, and a bridge keeps answering true while every real read fails: on 2026-10-16
/// the filter wheel answered <c>EFW_ERROR_REMOVED</c> to every Position read for minutes and the
/// camera's state read failed the same way, while both chips stayed green — Ara only noticed on
/// the next move or exposure. Each polling service feeds its ONE essential runtime read here
/// (wheel position, camera state, focuser and rotator position); a consecutive-failure streak of
/// <see cref="DefaultThreshold"/> ticks (≈ 6 s at the 2 s refresh) is a <c>degraded</c> fault
/// (amber chip, one reconnect attempt), and the first good read afterwards is the recovery.
/// Pure and unsynchronized like the probe: the owning service calls it under its gate.
/// </summary>
public sealed class DeviceHealthTracker {

    public const int DefaultThreshold = 3;

    private readonly int _threshold;
    private int _streak;
    private bool _degraded;

    public DeviceHealthTracker(int threshold = DefaultThreshold) {
        _threshold = threshold < 1 ? 1 : threshold;
    }

    /// <summary>Consecutive failed reads so far (diagnostics/tests).</summary>
    public int FailureStreak => _streak;

    /// <summary>True inside a degraded episode.</summary>
    public bool IsDegraded => _degraded;

    /// <summary>The last failed read's reason (the driver's words), null after a good read.</summary>
    public string? LastReason { get; private set; }

    public HealthVerdict Observe(bool readSucceeded, string? reason = null) {
        if (readSucceeded) {
            _streak = 0;
            LastReason = null;
            if (_degraded) {
                _degraded = false;
                return HealthVerdict.Recovered;
            }
            return HealthVerdict.Healthy;
        }
        _streak++;
        LastReason = string.IsNullOrWhiteSpace(reason) ? LastReason : reason;
        if (_degraded) {
            return HealthVerdict.StillDegraded;
        }
        if (_streak >= _threshold) {
            _degraded = true;
            return HealthVerdict.BecameDegraded;
        }
        return HealthVerdict.Faltering;
    }

    /// <summary>A fresh session (connect / disconnect) starts clean: no streak, no episode.</summary>
    public void Reset() {
        _streak = 0;
        _degraded = false;
        LastReason = null;
    }
}
