#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;

namespace OpenAstroAra.Server.Services;

/// <summary>The monitor's read on the cooler after one refresh-tick observation.</summary>
public enum CoolingDriftVerdict {
    /// <summary>In band, disarmed (cooler off / no set-point / unreadable temp), or an
    /// episode already reported.</summary>
    Idle,

    /// <summary>Out of band, but not yet for the sustained window — a TEC catching up after
    /// a set-point change or an ambient swing must not fire (§42.2: "don't abort — user may
    /// want to image at warmer temp"; a blip isn't even worth notifying).</summary>
    Drifting,

    /// <summary>Out of band for the full window: the cooler can't hold set-point. The caller
    /// publishes <see cref="Contracts.EquipmentFaultKind.CoolingDrift"/> exactly once per episode.</summary>
    Drifted,

    /// <summary>Back within the hysteresis band after a fired episode — re-armed.</summary>
    Recovered,
}

/// <summary>
/// §42.2 — detects a camera cooler failing to hold its set-point (the "CCDTemp vs
/// SetCCDTemperature" row): deviation beyond <see cref="DefaultDriftThresholdC"/> sustained
/// for <see cref="DefaultDriftWindow"/> fires once per episode; recovery needs the deviation
/// back under threshold − <see cref="DefaultClearHysteresisC"/> so boundary flap can't churn
/// episodes. Deliberately WALL-CLOCK (unlike <see cref="DeviceConnectionProbe"/>'s tick
/// counting): the camera refresh pass skips ticks during image downloads but the TEC keeps
/// running, so a 4-minute download gap must still count toward the 5-minute window. Only the
/// armed state (cooler on + set-point + readable temp) accumulates — a driver that stops
/// reporting temperature clears the accumulation rather than drifting toward a false fault,
/// though a transient unreadable tick never clears a FIRED episode (that would re-fire on
/// a flapping sensor).
///
/// Not internally synchronized — the owning CameraService mutates it only under its
/// connection gate.
/// </summary>
public sealed class CoolingDriftMonitor {

    /// <summary>Deviation from set-point that counts as out of band (§42.2 row: 5 °C).</summary>
    public const double DefaultDriftThresholdC = 5.0;

    /// <summary>How long the deviation must be sustained (§42.2 row: 5 min).</summary>
    public static readonly TimeSpan DefaultDriftWindow = TimeSpan.FromMinutes(5);

    /// <summary>Recovery hysteresis: an episode clears at threshold − this, not at the
    /// firing threshold itself.</summary>
    public const double DefaultClearHysteresisC = 1.0;

    private readonly double _thresholdC;
    private readonly TimeSpan _window;
    private readonly double _hysteresisC;
    private DateTimeOffset? _outOfBandSinceUtc;
    private bool _episodeFired;

    public CoolingDriftMonitor(double thresholdC = DefaultDriftThresholdC,
            TimeSpan? window = null, double hysteresisC = DefaultClearHysteresisC) {
        _thresholdC = thresholdC;
        _window = window ?? DefaultDriftWindow;
        _hysteresisC = hysteresisC;
    }

    /// <summary>Fresh baseline — called at connect adoption, on connection-lost trip, and on a
    /// commanded cooler change (a new set-point legitimately starts far from the sensor).</summary>
    public void Reset() {
        _outOfBandSinceUtc = null;
        _episodeFired = false;
    }

    /// <summary>One call per refresh tick with the freshly-read cooler state.</summary>
    public CoolingDriftVerdict Observe(bool coolerOn, double? setpointC, double? actualC, DateTimeOffset nowUtc) {
        if (!coolerOn || setpointC is not double setpoint || actualC is not double actual
                || double.IsNaN(setpoint) || double.IsNaN(actual)) {
            // Disarmed: stop accumulating, but keep a fired episode latched — a flapping
            // temperature sensor must not clear-and-refire; Reset() (commanded change or
            // reconnect) or an in-band reading is what re-arms.
            _outOfBandSinceUtc = null;
            return CoolingDriftVerdict.Idle;
        }
        var deviation = Math.Abs(actual - setpoint);
        if (_episodeFired) {
            if (deviation <= _thresholdC - _hysteresisC) {
                _episodeFired = false;
                _outOfBandSinceUtc = null;
                return CoolingDriftVerdict.Recovered;
            }
            return CoolingDriftVerdict.Idle; // still drifted, already reported
        }
        if (deviation > _thresholdC) {
            _outOfBandSinceUtc ??= nowUtc;
            if (nowUtc - _outOfBandSinceUtc.Value >= _window) {
                _episodeFired = true;
                return CoolingDriftVerdict.Drifted;
            }
            return CoolingDriftVerdict.Drifting;
        }
        _outOfBandSinceUtc = null;
        return CoolingDriftVerdict.Idle;
    }
}
