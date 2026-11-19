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

/// <summary>The watch's read on a commanded cooler after one refresh-tick observation.</summary>
public enum CoolerResponseVerdict {
    /// <summary>Not commanded on, responding, or an episode already reported.</summary>
    Idle,

    /// <summary>Commanded on and not responding yet, but not for the full window.</summary>
    Settling,

    /// <summary>Not responding for the full window. The caller warns exactly once per episode.</summary>
    NotResponding,

    /// <summary>The cooler responds again after a reported episode.</summary>
    Recovered,
}

/// <summary>Which symptom fired the episode.</summary>
public enum CoolerResponseSymptom {
    /// <summary>No episode.</summary>
    None,

    /// <summary>CoolerOn reads back off while it is commanded on (a camera with no 12 V).</summary>
    ReadsOff,

    /// <summary>CoolerOn reads on, but at 0 % power with the sensor well above its set-point and
    /// not moving.</summary>
    NoPower,
}

/// <summary>
/// #2076 — detects a cooler that Ara switched on but that does not respond: the camera's 12 V is
/// not connected, so the driver accepts CoolerOn = true and keeps reading it back off (Player One),
/// or reads it on at 0 % power with a sensor that never moves. <see cref="CoolingDriftMonitor"/>
/// cannot see this, because it disarms whenever CoolerOn reads off.
///
/// Rule A: commanded on, and CoolerOn reads known-off continuously for <see cref="DefaultWindow"/>.
/// A known-on read restarts the window; an unknown read (the CoolerOn GET threw) neither accuses
/// nor clears. This also catches a cooler that loses power later in the session.
/// Rule B: CoolerOn reads on, power at or below <see cref="IdlePowerPct"/>, the sensor more than
/// <see cref="AboveSetpointC"/> above the set-point, and the sensor has not closed by
/// <see cref="CoolingDriftMonitor.DefaultApproachC"/>, all for <see cref="DefaultWindow"/>.
///
/// Only Ara's own cooler commands arm it (<see cref="Command"/>); a cooler that another client or
/// the driver's session restore switched on is not watched. Wall-clock, like the drift monitor.
/// Not internally synchronized: the owning CameraService mutates it only under its gate.
/// </summary>
public sealed class CoolerResponseWatch {

    /// <summary>How long a cooler may not respond before the warning (#2076: one minute).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    /// <summary>Cooler power at or below this is an idle TEC (#1210's headroom threshold).</summary>
    public const double IdlePowerPct = 1.0;

    /// <summary>An idle TEC this far above its set-point is not regulating, it is starved.</summary>
    public const double AboveSetpointC = 2.0;

    private readonly TimeSpan _window;
    private bool _commanded;
    private DateTimeOffset? _offSinceUtc;
    private DateTimeOffset? _starvedSinceUtc;
    private double _starvedFromC;
    private double? _episodeCcdC;

    public CoolerResponseWatch(TimeSpan? window = null) {
        _window = window ?? DefaultWindow;
    }

    /// <summary>True from the firing observation until the cooler responds, is commanded off, or
    /// the session resets.</summary>
    public bool NotResponding { get; private set; }

    /// <summary>The symptom of the current episode; <see cref="CoolerResponseSymptom.None"/> when none.</summary>
    public CoolerResponseSymptom Symptom { get; private set; }

    /// <summary>A commanded cooler change. Off disarms and clears. On arms only when the cooler is
    /// not already commanded on, so a ramp step, a warm-ladder step or a repeated toggle that
    /// re-sends on does not restart the window or clear a reported episode.</summary>
    public void Command(bool enabled) {
        if (!enabled) {
            Reset();
            return;
        }
        if (_commanded) {
            return;
        }
        Reset();
        _commanded = true;
    }

    /// <summary>Forget everything: a new session, a disconnect or a cooler-off.</summary>
    public void Reset() {
        _commanded = false;
        _offSinceUtc = null;
        _starvedSinceUtc = null;
        _episodeCcdC = null;
        NotResponding = false;
        Symptom = CoolerResponseSymptom.None;
    }

    /// <summary>One call per refresh tick with the freshly-read cooler state.</summary>
    public CoolerResponseVerdict Observe(bool coolerOn, bool coolerStateKnown, double? powerPct,
            double? ccdC, double? setpointC, DateTimeOffset nowUtc) {
        if (!_commanded || !coolerStateKnown) {
            // Unknown: keep both windows as they are; the next known read decides.
            return !_commanded || NotResponding || (_offSinceUtc is null && _starvedSinceUtc is null)
                ? CoolerResponseVerdict.Idle
                : CoolerResponseVerdict.Settling;
        }
        if (!coolerOn) {
            _starvedSinceUtc = null;
            if (NotResponding) {
                return CoolerResponseVerdict.Idle; // still off, already reported
            }
            _offSinceUtc ??= nowUtc;
            return nowUtc - _offSinceUtc.Value >= _window
                ? Fire(CoolerResponseSymptom.ReadsOff, ccdC)
                : CoolerResponseVerdict.Settling;
        }
        _offSinceUtc = null;
        var starved = Starved(powerPct, ccdC, setpointC);
        if (NotResponding) {
            var progressed = ccdC is double now && _episodeCcdC is double then
                && then - now >= CoolingDriftMonitor.DefaultApproachC;
            if (!starved || progressed) {
                NotResponding = false;
                Symptom = CoolerResponseSymptom.None;
                _starvedSinceUtc = null;
                _episodeCcdC = null;
                return CoolerResponseVerdict.Recovered;
            }
            return CoolerResponseVerdict.Idle; // still starved, already reported
        }
        if (!starved) {
            _starvedSinceUtc = null;
            return CoolerResponseVerdict.Idle;
        }
        var ccd = ccdC!.Value;
        if (_starvedSinceUtc is null || _starvedFromC - ccd >= CoolingDriftMonitor.DefaultApproachC) {
            // First starved read, or the sensor is still closing on the set-point: the window
            // (re)starts here, as in the drift monitor (#1210).
            _starvedSinceUtc = nowUtc;
            _starvedFromC = ccd;
        }
        return nowUtc - _starvedSinceUtc.Value >= _window
            ? Fire(CoolerResponseSymptom.NoPower, ccdC)
            : CoolerResponseVerdict.Settling;
    }

    /// <summary>A sequencer Cool Camera counted the minute of CoolerOn reading off itself (in its
    /// own poll time) and is about to fail. Opens the episode now when the refresh pass has not yet,
    /// so the warning always comes before the step failure (#2076 user decision: warn, then fail)
    /// and a cooler-off right after the failure (a plan's Warm Camera) cannot swallow it. True when
    /// this call opened the episode, i.e. the caller must publish the warning.</summary>
    public bool ReportReadsOff(double? ccdC) {
        if (!_commanded || NotResponding) {
            return false;
        }
        _offSinceUtc = null;
        _starvedSinceUtc = null;
        Fire(CoolerResponseSymptom.ReadsOff, ccdC);
        return true;
    }

    private CoolerResponseVerdict Fire(CoolerResponseSymptom symptom, double? ccdC) {
        NotResponding = true;
        Symptom = symptom;
        _episodeCcdC = ccdC is double c && !double.IsNaN(c) ? c : null;
        return CoolerResponseVerdict.NotResponding;
    }

    /// <summary>An idle TEC well above a known set-point. Unknown power, sensor or set-point is
    /// never evidence.</summary>
    private static bool Starved(double? powerPct, double? ccdC, double? setpointC) =>
        powerPct is double power && !double.IsNaN(power) && power <= IdlePowerPct
        && ccdC is double ccd && !double.IsNaN(ccd)
        && setpointC is double setpoint && !double.IsNaN(setpoint)
        && ccd - setpoint > AboveSetpointC;
}
