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

/// <summary>The probe's read on the device after this observation.</summary>
public enum ProbeVerdict {
    /// <summary>The device answered; any failure streak is cleared.</summary>
    Healthy,

    /// <summary>The probe failed, but not for long enough to call the device lost —
    /// a single dropped HTTP request or a busy driver must not trip a fault (§42.3).</summary>
    Degraded,

    /// <summary>The failure streak reached the threshold: the device is gone. The caller
    /// trips its connection-lost path (state → Error + fault event) exactly once.</summary>
    Lost,
}

/// <summary>
/// §42.3 — the pure consecutive-failure classifier behind Alpaca disconnect detection, shared by
/// every §32.4 polling device service so the streak semantics are written (and tested) once. Each
/// refresh tick the service makes ONE deliberate probe — reading the Alpaca <c>Connected</c>
/// property, which throws on transport death and returns false on a driver-side disconnect — and
/// feeds the outcome here. A success clears the streak (a flapping network never accumulates);
/// <see cref="DefaultLostThreshold"/> consecutive failures (≈ 6 s at the 2 s refresh interval)
/// declare the device lost. Deliberately NOT wall-clock-based: ticks pause while a service is
/// not Connected, so a tick count can't mis-fire across a reconnect.
///
/// Not internally synchronized — each owning service mutates it (Observe from the refresh
/// tick, Reset from connect/trip) only under its own connection gate, which serializes the
/// refresh-path observations against a concurrent reconnect's Reset.
/// </summary>
public sealed class DeviceConnectionProbe {

    /// <summary>Consecutive probe failures before a device is declared lost.</summary>
    public const int DefaultLostThreshold = 3;

    private readonly int _threshold;
    private int _streak;

    public DeviceConnectionProbe(int threshold = DefaultLostThreshold) {
        _threshold = threshold < 1 ? 1 : threshold;
    }

    /// <summary>The current consecutive-failure count (diagnostics/tests).</summary>
    public int FailureStreak => _streak;

    public ProbeVerdict Observe(bool probeSucceeded) {
        if (probeSucceeded) {
            _streak = 0;
            return ProbeVerdict.Healthy;
        }
        _streak++;
        return _streak >= _threshold ? ProbeVerdict.Lost : ProbeVerdict.Degraded;
    }

    /// <summary>Start a fresh episode — called when a (re)connect succeeds so an old streak
    /// can never bleed into the new session.</summary>
    public void Reset() => _streak = 0;
}
